using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GuitarBridge;

public sealed class AudioTelemetry
{
    private float gain = 0.5f;
    public float Gain { get => Volatile.Read(ref gain); set => Volatile.Write(ref gain, OutputLevel.Normalize(value)); }
    public float Peak { get; internal set; }
    public float LimiterReductionDb { get; internal set; }
    public double BufferedMs { get; internal set; }
    public double QueueTargetMs { get; internal set; }
    public int Underruns { get; internal set; }
    public int Overruns { get; internal set; }
}

public sealed class AudioHostMessage
{
    public string Kind { get; set; } = "";
    public Settings? Settings { get; set; }
    public float Gain { get; set; }
    public float Peak { get; set; }
    public float LimiterReductionDb { get; set; }
    public double BufferedMs { get; set; }
    public double QueueTargetMs { get; set; }
    public int Underruns { get; set; }
    public int Overruns { get; set; }
    public int AsioFrames { get; set; }
    public string OutputFormat { get; set; } = "";
    public string OutputDiagnostics { get; set; } = "";
    public string? Error { get; set; }
    public string Stage { get; set; } = "";
    public bool AsioResetRequested { get; set; }
}

internal enum AudioStartupTest { None, ResetOnce, ResetAlways, OutputFailure }

// Native driver calls cannot be safely cancelled on a managed thread. A session
// owns a child process so a stuck stop/dispose can be ended and retried safely.
public sealed class AudioHostClient
{
    private sealed class AsioResetException : Exception
    {
        public AsioResetException() : base("The ASIO driver keeps requesting a reset after reopening. Check its control panel settings and try again.") { }
    }
    private sealed class Session
    {
        public readonly NamedPipeServerStream Pipe;
        public readonly Process Process;
        public readonly SemaphoreSlim Writes = new(1);
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public StreamWriter? Writer;
        public volatile bool Stopping;
        public int Released;
        public Session(NamedPipeServerStream pipe, Process process) { Pipe = pipe; Process = process; }
    }
    private readonly SemaphoreSlim lifecycle = new(1);
    private readonly bool testStopHang;
    private readonly bool testStartHang;
    private readonly TimeSpan startupTimeout;
    private Session? session;
    private volatile bool running;
    private string? fault;
    private int closeVersion;
    internal AudioStartupTest StartupTest { get; init; }
    internal int StartupAttempts { get; private set; }
    internal int StartupResetStops { get; private set; }
    internal int GainMessagesSent { get; private set; }
    public string StartupStage { get; private set; } = "Connecting audio process";
    internal int? DiagnosticProcessId => session?.Process.Id;
    public AudioTelemetry? Buffer { get; private set; }
    public bool Running => running;
    public string? Fault => Volatile.Read(ref fault);
    public int AsioFrames { get; private set; }
    public string OutputDiagnostics { get; private set; } = "";
    public string OutputFormat { get; private set; } = "";
    public AudioHostClient(bool testStopHang = false, TimeSpan? startupTimeout = null, bool testStartHang = false)
    {
        this.testStopHang = testStopHang;
        this.testStartHang = testStartHang;
        this.startupTimeout = startupTimeout ?? TimeSpan.FromSeconds(8);
    }

    public async Task StartAsync(Settings settings)
    {
        await lifecycle.WaitAsync();
        try
        {
            await StopCoreAsync();
            Volatile.Write(ref fault, null);
            StartupStage = "Connecting audio process";
            Buffer = new AudioTelemetry { Gain = (float)settings.Volume };
            StartupAttempts = 0;
            StartupResetStops = 0;
            int version = Volatile.Read(ref closeVersion);
            for (int attempt = 0; ; attempt++)
            {
                if (version != Volatile.Read(ref closeVersion)) throw new OperationCanceledException();
                StartupAttempts++;
                string pipeName = "GuitarBridge-" + Guid.NewGuid().ToString("N");
                var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
                info.ArgumentList.Add(StartupTest != AudioStartupTest.None ? "--audio-host-reset-test" :
                    testStartHang ? "--audio-host-start-test" : testStopHang ? "--audio-host-test" : "--audio-host");
                info.ArgumentList.Add(pipeName);
                if (StartupTest != AudioStartupTest.None)
                    info.ArgumentList.Add(StartupTest == AudioStartupTest.OutputFailure ? "error" :
                        StartupTest == AudioStartupTest.ResetAlways || attempt == 0 ? "reset" : "ready");
                Process process;
                try { process = Process.Start(info) ?? throw new IOException("Could not start the audio process."); }
                catch { pipe.Dispose(); throw; }
                var current = session = new Session(pipe, process);
                try
                {
                    await pipe.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(8));
                    current.Writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                    _ = ReadAsync(current);
                    await SendAsync(current, new AudioHostMessage { Kind = "start", Settings = settings });
                    await current.Started.Task.WaitAsync(startupTimeout);
                    running = true;
                    _ = SendGainAsync(current);
                    return;
                }
                catch (Exception ex)
                {
                    current.Stopping = true;
                    if (ex is AsioResetException)
                    {
                        // Releasing an active native audio process can leave Windows holding
                        // the output. Ask its driver thread to stop/dispose before replacement.
                        StartupStage = "Applying ASIO settings…";
                        try { await StopCoreAsync(); StartupResetStops++; }
                        catch (Exception stopError)
                        {
                            throw new InvalidOperationException("Could not reopen the ASIO driver after its settings changed. " + stopError.Message, stopError);
                        }
                    }
                    else
                    {
                        await ReleaseAsync(current);
                        session = null;
                    }
                    running = false;
                    if (ex is AsioResetException && attempt == 0 && version == Volatile.Read(ref closeVersion))
                    {
                        // A fresh process reloads the driver's preferred buffer size, just as
                        // a second manual start would, without exposing a recoverable warning.
                        Volatile.Write(ref fault, null);
                        Buffer = new AudioTelemetry { Gain = (float)settings.Volume };
                        StartupStage = "Applying ASIO settings…";
                        continue;
                    }
                    Buffer = null;
                    throw new InvalidOperationException(ex is TimeoutException
                        ? $"Audio startup timed out at: {StartupStage}. Its audio process was closed. " +
                          (StartupStage.Contains("output", StringComparison.OrdinalIgnoreCase)
                            ? "Test the selected output in Windows Sound settings, or try another output device."
                            : "Check the input device in your ASIO control panel and try again.")
                        : ex.Message, ex);
                }
            }
        }
        finally { lifecycle.Release(); }
    }

    private async Task ReadAsync(Session current)
    {
        try
        {
            using var reader = new StreamReader(current.Pipe, leaveOpen: true);
            while (await reader.ReadLineAsync() is string line)
            {
                var message = JsonSerializer.Deserialize<AudioHostMessage>(line);
                if (message == null || session != current) continue;
                if (!string.IsNullOrEmpty(message.Stage)) StartupStage = message.Stage;
                if (message.Error is string error)
                {
                    Volatile.Write(ref fault, error);
                    current.Started.TrySetException(new InvalidOperationException(error));
                    continue;
                }
                if (message.Kind == "reset" || message.AsioResetRequested)
                {
                    if (current.Started.Task.IsCompletedSuccessfully && !current.Stopping)
                        Volatile.Write(ref fault, "ASIO settings changed while routing. Stop and start routing to apply them.");
                    else current.Started.TrySetException(new AsioResetException());
                    continue;
                }
                if (message.Kind == "started")
                {
                    AsioFrames = message.AsioFrames;
                    OutputFormat = message.OutputFormat;
                    OutputDiagnostics = message.OutputDiagnostics;
                    current.Started.TrySetResult();
                }
                if (message.Kind == "stopped") current.Stopped.TrySetResult();
                var buffer = Buffer;
                if (buffer != null)
                {
                    buffer.Peak = message.Peak;
                    buffer.LimiterReductionDb = message.LimiterReductionDb;
                    buffer.BufferedMs = message.BufferedMs;
                    buffer.QueueTargetMs = message.QueueTargetMs;
                    buffer.Underruns = message.Underruns;
                    buffer.Overruns = message.Overruns;
                }
            }
            if (!current.Stopping) throw new IOException("The audio process exited. Start routing again to reconnect.");
        }
        catch (Exception ex)
        {
            if (!current.Stopping && session == current)
            {
                Volatile.Write(ref fault, ex.Message);
                current.Started.TrySetException(ex);
            }
        }
    }
    private static async Task SendAsync(Session current, AudioHostMessage message)
    {
        await current.Writes.WaitAsync();
        try { await current.Writer!.WriteLineAsync(JsonSerializer.Serialize(message)); }
        finally { current.Writes.Release(); }
    }
    private async Task SendGainAsync(Session current)
    {
        try
        {
            float sentGain = float.NaN;
            while (session == current && !current.Stopping)
            {
                float gain = Buffer?.Gain ?? 0;
                if (gain != sentGain)
                {
                    await SendAsync(current, new AudioHostMessage { Kind = "gain", Gain = gain });
                    GainMessagesSent++;
                    sentGain = gain;
                }
                await Task.Delay(50);
            }
        }
        catch { /* ReadAsync reports pipe/process failures. */ }
    }
    public async Task StopAsync()
    {
        await lifecycle.WaitAsync();
        try { await StopCoreAsync(); }
        finally { lifecycle.Release(); }
    }
    private async Task StopCoreAsync()
    {
        running = false;
        var current = session;
        if (current == null) return;
        current.Stopping = true;
        bool timedOut = false;
        try
        {
            await SendAsync(current, new AudioHostMessage { Kind = "stop" }).WaitAsync(TimeSpan.FromSeconds(1));
            await current.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch { timedOut = true; }
        finally
        {
            session = null;
            Buffer = null;
            await ReleaseAsync(current);
        }
        if (timedOut) throw new TimeoutException("The audio driver stalled while stopping. Its audio process was closed and the controls are available again. Check the ASIO device selection before retrying.");
    }
    private static async Task ReleaseAsync(Session current)
    {
        if (Interlocked.Exchange(ref current.Released, 1) != 0) return;
        try
        {
            if (!current.Process.HasExited)
            {
                try { current.Process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { /* Already exited between the check and Kill. */ }
                await current.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            }
        }
        finally { current.Pipe.Dispose(); current.Process.Dispose(); }
    }
    public async Task CloseAsync()
    {
        Interlocked.Increment(ref closeVersion);
        // Quit must also terminate a session that is still inside StartAsync.
        var current = session;
        session = null;
        running = false;
        Buffer = null;
        if (current == null) return;
        current.Stopping = true;
        current.Started.TrySetCanceled();
        current.Stopped.TrySetResult();
        await ReleaseAsync(current);
    }
}
