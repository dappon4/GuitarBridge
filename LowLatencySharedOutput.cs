using System;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace GuitarBridge;

// IAudioClient3 is not exposed by NAudio 2.2.1. Keep the native extension local
// while retaining NAudio's format conversion and render-client wrappers.
internal sealed class LowLatencySharedOutput : IWavePlayer
{
    private readonly AudioClient client;
    private readonly IAudioClient3 native;
    private readonly AutoResetEvent ready = new(false);
    private readonly ManualResetEvent stop = new(false);
    private readonly WaitHandle[] waits;
    private readonly SynchronizationContext? context = SynchronizationContext.Current;
    private IWaveProvider? source;
    private Thread? thread;
    private int state;
    private int period, ahead;
    private byte[] bytes = Array.Empty<byte>();
    public WaveFormat OutputWaveFormat { get; private set; } = null!;
    public double PeriodMs => period * 1000.0 / OutputWaveFormat.SampleRate;
    public double AheadMs => ahead * 1000.0 / OutputWaveFormat.SampleRate;
    public double DefaultPeriodMs { get; private set; }
    public double MinimumPeriodMs { get; private set; }
    public bool Raw { get; private set; }
    public string Description => $"Shared · {PeriodMs:0.##} ms period · {AheadMs:0.##} ms output queue";
    public PlaybackState PlaybackState => (PlaybackState)Volatile.Read(ref state);
    public float Volume { get => 1; set { if (value != 1) throw new NotSupportedException("Set gain on the bridge buffer."); } }
    public event EventHandler<StoppedEventArgs>? PlaybackStopped;

    public LowLatencySharedOutput(string deviceId)
    {
        waits = new WaitHandle[] { stop, ready };
        native = Activate(deviceId);
        client = new AudioClient((NAudio.CoreAudioApi.Interfaces.IAudioClient)native);
    }

    internal static int SelectPeriod(int rate, int requestedMs, uint fundamental, uint minimum, uint maximum)
    {
        if (fundamental == 0 || minimum == 0 || maximum < minimum)
            throw new NotSupportedException("The output driver reported invalid engine periods.");
        long wanted = Math.Max(minimum, (long)Math.Ceiling(rate * requestedMs / 1000.0));
        long rounded = ((wanted + fundamental - 1) / fundamental) * fundamental;
        long result = Math.Min(rounded, maximum / fundamental * fundamental);
        if (result < minimum || result > int.MaxValue) throw new NotSupportedException("No valid shared engine period.");
        return (int)result;
    }

    internal static IWaveProvider ConvertSource(IWaveProvider input, WaveFormat format)
    {
        // Other endpoint layouts use the existing WASAPI compatibility path.
        if (format.Channels is not (1 or 2)) throw new NotSupportedException("Low-latency output requires mono or stereo speakers.");
        var encoding = format is WaveFormatExtensible ext ? ext.ToStandardWaveFormat().Encoding : format.Encoding;
        bool floating = encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32;
        bool pcm16 = encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16;
        if (!floating && !pcm16) throw new NotSupportedException("This speaker format requires the compatibility output path.");
        if (floating && format.Channels == 2 && input.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat &&
            input.WaveFormat.BitsPerSample == 32 && input.WaveFormat.Channels == 2 && input.WaveFormat.SampleRate == format.SampleRate)
            return input;
        ISampleProvider samples = input as ISampleProvider ?? input.ToSampleProvider();
        if (samples.WaveFormat.SampleRate != format.SampleRate)
            samples = new WdlResamplingSampleProvider(samples, format.SampleRate);
        if (format.Channels == 1) samples = new StereoToMonoSampleProvider(samples);
        return floating ? new SampleToWaveProvider(samples) : new SampleToWaveProvider16(samples);
    }

    public int RequestedMs { get; init; } = 5;
    public void Init(IWaveProvider waveProvider)
    {
        if (source != null) throw new InvalidOperationException("Output is already initialized.");
        // Raw mode bypasses optional per-stream effects; speaker protection and
        // always-on endpoint processing remain. Other apps keep their own modes.
        var properties = new ClientProperties { Size = 16, Category = 11, Options = 1 };
        int propertiesResult = native.SetClientProperties(ref properties);
        Raw = propertiesResult >= 0;
        if (!Raw)
        {
            properties.Options = 0;
            Marshal.ThrowExceptionForHR(native.SetClientProperties(ref properties));
        }
        OutputWaveFormat = client.MixFormat;
        var converted = ConvertSource(waveProvider, OutputWaveFormat);
        IntPtr format = WaveFormat.MarshalToPtr(OutputWaveFormat);
        try
        {
            Marshal.ThrowExceptionForHR(native.GetSharedModeEnginePeriod(format, out uint normal, out uint fundamental, out uint minimum, out uint maximum));
            DefaultPeriodMs = normal * 1000.0 / OutputWaveFormat.SampleRate;
            MinimumPeriodMs = minimum * 1000.0 / OutputWaveFormat.SampleRate;
            period = SelectPeriod(OutputWaveFormat.SampleRate, RequestedMs, fundamental, minimum, maximum);
            Marshal.ThrowExceptionForHR(native.InitializeSharedAudioStream(0x40000, (uint)period, format, IntPtr.Zero));
        }
        finally { Marshal.FreeHGlobal(format); }
        // Supply the next engine pass. Filling the entire endpoint allocation
        // adds extra periods of delay without changing the hardware period.
        ahead = Math.Min(client.BufferSize, period);
        client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle());
        _ = client.AudioRenderClient;
        bytes = new byte[period * OutputWaveFormat.BlockAlign];
        source = converted;
        // Allocate/JIT the resampler before ASIO starts. Empty capture supplies
        // silence here; the first real speaker callback does no cold initialization.
        source.Read(bytes, 0, bytes.Length);
    }

    public void Play()
    {
        if (source == null) throw new InvalidOperationException("Output is not initialized.");
        if (thread?.IsAlive == true) return;
        stop.Reset();
        Volatile.Write(ref state, (int)PlaybackState.Playing);
        thread = new Thread(Render) { IsBackground = true, Name = "Low latency speaker output", Priority = ThreadPriority.Highest };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    private void Render()
    {
        Exception? failure = null;
        IntPtr scheduling = AvSetMmThreadCharacteristics("Pro Audio", out _);
        try
        {
            // Prime with silence. Pulling two periods from live capture here can
            // starve a queue that only needs to supply one period per callback.
            client.AudioRenderClient.GetBuffer(ahead);
            client.AudioRenderClient.ReleaseBuffer(ahead, AudioClientBufferFlags.Silent);
            client.Start();
            while (true)
            {
                int signaled = WaitHandle.WaitAny(waits, 2000);
                if (signaled == 0) break;
                if (signaled == WaitHandle.WaitTimeout) throw new TimeoutException("The speaker driver stopped requesting audio.");
                if (stop.WaitOne(0)) break;
                Fill();
            }
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            try { client.Stop(); client.Reset(); } catch (Exception ex) { failure ??= ex; }
            if (scheduling != IntPtr.Zero) AvRevertMmThreadCharacteristics(scheduling);
            Volatile.Write(ref state, (int)PlaybackState.Stopped);
            var args = new StoppedEventArgs(failure);
            if (context != null) context.Post(_ => PlaybackStopped?.Invoke(this, args), null);
            else PlaybackStopped?.Invoke(this, args);
        }
    }

    private void Fill()
    {
        int needed = Math.Max(0, ahead - client.CurrentPadding);
        // Small reads prevent startup and catch-up from demanding a large FIFO.
        while (needed > 0 && !stop.WaitOne(0))
        {
            int frames = Math.Min(period, needed);
            int count = frames * OutputWaveFormat.BlockAlign;
            int read = source!.Read(bytes, 0, count);
            if (read < count) Array.Clear(bytes, read, count - read);
            var destination = client.AudioRenderClient.GetBuffer(frames);
            try { Marshal.Copy(bytes, 0, destination, count); }
            finally { client.AudioRenderClient.ReleaseBuffer(frames, AudioClientBufferFlags.None); }
            needed -= frames;
        }
    }

    public void Pause() => Stop();
    public void Stop() { stop.Set(); if (thread != Thread.CurrentThread) thread?.Join(); }
    public void Dispose() { Stop(); client.Dispose(); ready.Dispose(); stop.Dispose(); }

    private static IAudioClient3 Activate(string id)
    {
        var enumerator = (IEndpointEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"), true)!)!;
        IEndpoint? endpoint = null;
        try
        {
            Marshal.ThrowExceptionForHR(enumerator.GetDevice(id, out endpoint));
            var iid = typeof(IAudioClient3).GUID;
            Marshal.ThrowExceptionForHR(endpoint.Activate(ref iid, 23, IntPtr.Zero, out object result));
            return (IAudioClient3)result;
        }
        finally
        {
            if (endpoint != null) Marshal.ReleaseComObject(endpoint);
            Marshal.ReleaseComObject(enumerator);
        }
    }

    [DllImport("avrt.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr AvSetMmThreadCharacteristics(string taskName, out uint taskIndex);
    [DllImport("avrt.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEndpointEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint state, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IEndpoint endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IEndpoint endpoint);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEndpoint
    {
        [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object result);
    }
    // Complete inherited vtable order: IAudioClient, IAudioClient2, IAudioClient3.
    [ComImport, Guid("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient3
    {
        [PreserveSig] int Initialize(int mode, uint flags, long duration, long periodicity, IntPtr format, IntPtr session);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int mode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long normal, out long minimum);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, out IntPtr service);
        [PreserveSig] int IsOffloadCapable(int category, [MarshalAs(UnmanagedType.Bool)] out bool capable);
        [PreserveSig] int SetClientProperties(ref ClientProperties properties);
        [PreserveSig] int GetBufferSizeLimits(IntPtr format, [MarshalAs(UnmanagedType.Bool)] bool eventDriven, out long minimum, out long maximum);
        [PreserveSig] int GetSharedModeEnginePeriod(IntPtr format, out uint normal, out uint fundamental, out uint minimum, out uint maximum);
        [PreserveSig] int GetCurrentSharedModeEnginePeriod(out IntPtr format, out uint period);
        [PreserveSig] int InitializeSharedAudioStream(uint flags, uint period, IntPtr format, IntPtr session);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ClientProperties
    {
        public uint Size;
        public int Offload;
        public int Category;
        public uint Options;
    }
}
