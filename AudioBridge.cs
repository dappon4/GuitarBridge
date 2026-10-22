using System;
using System.Threading;
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GuitarBridge;

// A bounded stereo FIFO. The reader makes a small continuous rate adjustment
// to follow the independent USB and speaker clocks without accumulating delay.
public sealed class BridgeBuffer : IWaveProvider, ISampleProvider
{
    private readonly object gate = new();
    private readonly float[] ring;
    private readonly int capacity;
    private readonly int ringMask;
    private int target;
    private int maximumTarget;
    private int minimumTarget;
    private long tuningFrames;
    private bool tuneQueue;
    private int minimumHeadroom = int.MaxValue;
    private long written, consumed;
    private double fraction;
    private bool primed;
    private float gain, peak;
    private readonly OutputLimiter limiter;
    private float limiterReductionDb;
    private int underruns, overruns;
    private int bufferedFrames;
    public WaveFormat WaveFormat { get; }
    public float Gain { get => Volatile.Read(ref gain); set => Volatile.Write(ref gain, OutputLevel.Normalize(value)); }
    public float Peak => Volatile.Read(ref peak);
    public float LimiterReductionDb => Volatile.Read(ref limiterReductionDb);
    public int Underruns => Volatile.Read(ref underruns);
    public int Overruns => Volatile.Read(ref overruns);
    public double BufferedMs => Volatile.Read(ref bufferedFrames) * 1000.0 / WaveFormat.SampleRate;
    public double TargetMs => Volatile.Read(ref target) * 1000.0 / WaveFormat.SampleRate;
    internal static int LowLatencyTarget(int captureFrames, int renderFrames) => renderFrames + captureFrames + 2;
    public void SetTargetFrames(int frames)
    {
        target = Math.Clamp(frames, 8, capacity / 2);
        maximumTarget = Math.Min(capacity / 2, target * 2);
        tuneQueue = false;
    }
    public void ConfigureLatency(int captureFrames, int renderFrames)
    {
        SetTargetFrames(LowLatencyTarget(captureFrames, renderFrames));
        minimumTarget = Math.Min(target, renderFrames + captureFrames / 2 + WaveFormat.SampleRate / 1000 + 2);
        tuningFrames = -2L * WaveFormat.SampleRate;
        minimumHeadroom = int.MaxValue;
        tuneQueue = true;
    }

    public BridgeBuffer(int rate, int targetFrames)
    {
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, 2);
        limiter = new OutputLimiter(rate);
        target = Math.Max(8, targetFrames);
        capacity = Math.Max(rate, target * 8);
        maximumTarget = Math.Min(capacity / 2, target * 2);
        int slots = 1;
        while (slots < capacity) slots <<= 1;
        ringMask = slots - 1;
        ring = new float[slots * 2];
        Gain = 0.5f;
    }

    public void Push(float[] samples, int frames, int channels)
    {
        // Single producer / single consumer: publish only after copying a whole
        // capture block. The consumer publishes its position after reading, so
        // these slots cannot be overwritten while it interpolates them.
        // Unlike TryEnter, this does not drop a capture block when rendering is busy.
        long writeAt = Volatile.Read(ref written);
        if (frames > capacity || writeAt - Volatile.Read(ref consumed) + frames >= capacity)
        { Interlocked.Increment(ref overruns); return; }
        float p = 0;
        for (int i = 0; i < frames; i++)
        {
            int at = ((int)(writeAt + i) & ringMask) * 2;
            float left = samples[i * channels];
            float right = channels == 1 ? left : samples[i * channels + 1];
            ring[at] = float.IsFinite(left) ? left : 0;
            ring[at + 1] = float.IsFinite(right) ? right : 0;
            p = Math.Max(p, Math.Max(Math.Abs(ring[at]), Math.Abs(ring[at + 1])));
        }
        Volatile.Write(ref written, writeAt + frames);
        Volatile.Write(ref bufferedFrames, (int)Math.Max(0, writeAt + frames - Volatile.Read(ref consumed)));
        Volatile.Write(ref peak, p);
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        int trailing = count & 7;
        if (trailing != 0) Array.Clear(buffer, offset + count - trailing, trailing);
        ReadFrames(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, count - trailing)));
        return count;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        if ((count & 1) != 0) buffer[offset + count - 1] = 0;
        ReadFrames(buffer.AsSpan(offset, count & ~1));
        return count;
    }

    private void ReadFrames(Span<float> output)
    {
        lock (gate)
        {
            int frames = output.Length / 2;
            long published = Volatile.Read(ref written);
            long readAt = consumed;
            long available = published - readAt;
            int startThreshold = Math.Max(target, frames + 2);
            if (!primed)
            {
                if (available < startThreshold)
                {
                    output.Clear(); limiter.Reset(); Volatile.Write(ref limiterReductionDb, 0); return;
                }
                primed = true;
            }
            // Recover from scheduler stalls by discarding stale audio, never replaying a long backlog.
            if (available > Math.Max(target * 4, frames * 4))
            {
                readAt = published - startThreshold;
                fraction = 0;
                available = startThreshold;
                Interlocked.Increment(ref overruns);
            }
            double ratio = 1 + Math.Clamp((available - startThreshold) / (double)WaveFormat.SampleRate * 0.2, -0.002, 0.002);
            float volume = Gain;
            for (int f = 0; f < frames; f++)
            {
                if (published - readAt < 2)
                {
                    primed = false;
                    fraction = 0;
                    Interlocked.Increment(ref underruns);
                    // Add a small margin after starvation, bounded to twice the
                    // initial target. Stable routes retain the short queue.
                    target = Math.Min(maximumTarget, target + Math.Min(frames, Math.Max(8, target / 4)));
                    tuneQueue = false;
                    output.Slice(f * 2).Clear();
                    break;
                }
                int a = ((int)readAt & ringMask) * 2;
                int b = ((int)(readAt + 1) & ringMask) * 2;
                float left = (ring[a] + (ring[b] - ring[a]) * (float)fraction) * volume;
                float right = (ring[a + 1] + (ring[b + 1] - ring[a + 1]) * (float)fraction) * volume;
                limiter.Process(ref left, ref right);
                output[f * 2] = left;
                output[f * 2 + 1] = right;
                fraction += ratio;
                int advance = (int)fraction;
                readAt += advance;
                fraction -= advance;
            }
            Volatile.Write(ref consumed, readAt);
            Volatile.Write(ref limiterReductionDb, limiter.ReductionDb);
            Volatile.Write(ref bufferedFrames, (int)Math.Max(0, Volatile.Read(ref written) - readAt));
            if (tuneQueue)
            {
                minimumHeadroom = Math.Min(minimumHeadroom, (int)(published - readAt));
                tuningFrames += frames;
                if (tuningFrames >= WaveFormat.SampleRate)
                {
                    // Settle for three seconds, then trim only 16 frames per
                    // second when the entire window retained safe headroom.
                    int guard = WaveFormat.SampleRate / 1000 + 2;
                    if (minimumHeadroom > guard + 16) target = Math.Max(minimumTarget, target - 16);
                    else if (minimumHeadroom < guard)
                    {
                        target = Math.Min(maximumTarget, target + 32);
                        tuneQueue = false;
                    }
                    tuningFrames = 0;
                    minimumHeadroom = int.MaxValue;
                }
            }
        }
    }
}

public sealed class AudioBridge : IDisposable
{
    private AsioOut? input;
    private IWavePlayer? output;
    private MMDevice? device;
    private float[] samples = Array.Empty<float>();
    private volatile bool active;
    private string? callbackError;
    private int resetRequested;
    private long lastCapture;
    private string stage = "Idle";
    public string Stage => Volatile.Read(ref stage);
    public BridgeBuffer? Buffer { get; private set; }
    public bool Running => active;
    public bool ResetRequested => Volatile.Read(ref resetRequested) != 0;
    public int AsioFrames { get; private set; }
    public string? Fault => Volatile.Read(ref callbackError) ?? (active && Stopwatch.GetElapsedTime(Interlocked.Read(ref lastCapture)).TotalSeconds > 3 ? "No ASIO audio callbacks received for three seconds. Check the USB connection and enabled ASIO inputs, then restart routing." : null);
    public string OutputFormat { get; private set; } = "";
    public string OutputDiagnostics { get; private set; } = "";

    public void Start(Settings settings)
    {
        Stop();
        callbackError = null;
        OutputDiagnostics = "";
        Volatile.Write(ref resetRequested, 0);
        try
        {
            // Validate the destination before acquiring any input devices.
            Buffer = new BridgeBuffer(settings.SampleRate, settings.SampleRate * settings.BufferMs / 1000) { Gain = (float)settings.Volume };
            using var enumerator = new MMDeviceEnumerator();
            Volatile.Write(ref stage, "Opening Windows output device");
            device = enumerator.GetDevice(settings.OutputId);
            Volatile.Write(ref stage, "Reading Windows output format");
            Volatile.Write(ref stage, "Initializing WASAPI output: " + device.FriendlyName);
            double outputPeriodMs = settings.BufferMs;
            string? compatibilityReason = null;
            if (!settings.Exclusive)
            {
                LowLatencySharedOutput? fast = null;
                try
                {
                    fast = new LowLatencySharedOutput(settings.OutputId) { RequestedMs = settings.BufferMs };
                    fast.Init(Buffer);
                    output = fast;
                    outputPeriodMs = fast.PeriodMs;
                    OutputFormat = fast.Description;
                    OutputDiagnostics = $"{fast.OutputWaveFormat.SampleRate} Hz; raw={fast.Raw}; minimum={fast.MinimumPeriodMs:0.##} ms; default={fast.DefaultPeriodMs:0.##} ms";
                }
                catch (Exception ex)
                {
                    fast?.Dispose();
                    if (ex is not (System.Runtime.InteropServices.COMException or NotSupportedException or InvalidCastException)) throw;
                    compatibilityReason = $"Low-latency output unavailable ({ex.Message}).";
                }
            }
            if (output == null)
            {
                var compatible = new WasapiOut(device, settings.Exclusive ? AudioClientShareMode.Exclusive : AudioClientShareMode.Shared, true, settings.BufferMs);
                output = compatible;
                compatible.Init(Buffer);
                OutputFormat = settings.Exclusive ? "WASAPI exclusive" : "Shared compatibility output";
                OutputDiagnostics = compatibilityReason ?? "";
            }
            output.PlaybackStopped += OnOutputStopped;
            Volatile.Write(ref stage, "Loading ASIO driver");
            input = new AsioOut(settings.Driver);
            int channels = settings.Stereo ? 2 : 1;
            if (settings.FirstChannel < 0 || settings.FirstChannel + channels > input.DriverInputChannelCount)
                throw new InvalidOperationException("The selected input channels are unavailable. Enable the required inputs in your ASIO driver's control panel, then scan channels again.");
            if (!string.IsNullOrEmpty(settings.InputName) && input.AsioInputChannelName(settings.FirstChannel) != settings.InputName)
                throw new InvalidOperationException("The ASIO channel mapping changed. Scan channels and select the input again.");
            input.InputChannelOffset = settings.FirstChannel;
            Volatile.Write(ref stage, "Initializing ASIO input buffers");
            input.InitRecordAndPlayback(null, channels, settings.SampleRate);
            AsioFrames = input.FramesPerBuffer;
            samples = new float[AsioFrames * channels];
            int renderFrames = (int)Math.Ceiling(settings.SampleRate * outputPeriodMs / 1000.0);
            if (output is LowLatencySharedOutput) Buffer.ConfigureLatency(AsioFrames, renderFrames);
            else Buffer.SetTargetFrames(Math.Max(AsioFrames * 2, renderFrames));
            input.AudioAvailable += OnAudio;
            input.DriverResetRequest += OnReset;
            Interlocked.Exchange(ref lastCapture, Stopwatch.GetTimestamp());
            active = true;
            Volatile.Write(ref stage, "Starting ASIO capture");
            input.Play();
            Volatile.Write(ref stage, "Starting output playback");
            output.Play();
            Volatile.Write(ref stage, "Running");
        }
        catch (Exception ex)
        {
            Volatile.Write(ref callbackError, "Could not start audio: " + ex.Message);
            Stop();
            throw;
        }
    }

    private void OnAudio(object? sender, AsioAudioAvailableEventArgs e)
    {
        if (!active) return;
        try
        {
            e.GetAsInterleavedSamples(samples);
            Interlocked.Exchange(ref lastCapture, Stopwatch.GetTimestamp());
            Buffer?.Push(samples, e.SamplesPerBuffer, e.InputBuffers.Length);
        }
        catch (Exception ex) { Volatile.Write(ref callbackError, "ASIO capture stopped: " + ex.Message); active = false; }
    }
    private void OnReset(object? sender, EventArgs e)
    {
        // Record the request only; the owner reopens the session off the driver callback.
        if (ReferenceEquals(sender, input)) Interlocked.Exchange(ref resetRequested, 1);
    }
    private void OnOutputStopped(object? sender, StoppedEventArgs e)
    {
        if (active && ReferenceEquals(sender, output)) Volatile.Write(ref callbackError, "Audio output stopped. " + (e.Exception?.Message ?? "Check the output device and restart routing."));
    }
    public void Stop()
    {
        active = false;
        var oldInput = input; input = null;
        var oldOutput = output; output = null;
        try
        {
            if (oldInput != null)
            {
                oldInput.AudioAvailable -= OnAudio;
                oldInput.DriverResetRequest -= OnReset;
                try { oldInput.Stop(); } finally { oldInput.Dispose(); }
            }
        }
        finally
        {
            try
            {
                if (oldOutput != null)
                {
                    oldOutput.PlaybackStopped -= OnOutputStopped;
                    try { oldOutput.Stop(); } finally { oldOutput.Dispose(); }
                }
            }
            finally { device?.Dispose(); device = null; Buffer = null; }
        }
    }
    public void Dispose() => Stop();
}
