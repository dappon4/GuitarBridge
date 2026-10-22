using System;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime;

namespace GuitarBridge;

public static class AudioHostServer
{
    public static async Task RunAsync(string pipeName, bool testStopHang, bool testStartHang = false, string? startupTest = null)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(8000);
        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, leaveOpen: true);
        using var writes = new SemaphoreSlim(1);
        using var done = new CancellationTokenSource();
        var audio = new AudioWorker();
        string? testStage = null;
        async Task Send(AudioHostMessage message)
        {
            await writes.WaitAsync();
            try { await writer.WriteLineAsync(JsonSerializer.Serialize(message)); }
            finally { writes.Release(); }
        }
        async Task Telemetry()
        {
            try
            {
                while (!done.IsCancellationRequested)
                {
                    var buffer = audio.Bridge.Buffer;
                    await Send(new AudioHostMessage { Kind = "status", Error = audio.Bridge.Fault, Stage = testStage ?? audio.Bridge.Stage,
                        AsioResetRequested = audio.Bridge.ResetRequested,
                        Peak = buffer?.Peak ?? 0, LimiterReductionDb = buffer?.LimiterReductionDb ?? 0,
                        BufferedMs = buffer?.BufferedMs ?? 0, QueueTargetMs = buffer?.TargetMs ?? 0,
                        Underruns = buffer?.Underruns ?? 0, Overruns = buffer?.Overruns ?? 0 });
                    await Task.Delay(100, done.Token);
                }
            }
            catch (Exception) when (done.IsCancellationRequested || !pipe.IsConnected) { }
        }
        var telemetry = Telemetry();
        var previousLatencyMode = GCSettings.LatencyMode;
        try
        {
            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
            while (await reader.ReadLineAsync() is string line)
            {
                var message = JsonSerializer.Deserialize<AudioHostMessage>(line);
                if (message?.Kind == "start")
                {
                    try
                    {
                        if (testStartHang)
                        {
                            testStage = "Initializing WASAPI output: Simulated output";
                            await Task.Delay(Timeout.Infinite);
                        }
                        if (startupTest == "error") throw new InvalidOperationException("Simulated output failure.");
                        if (!testStopHang && startupTest == null)
                            await audio.StartAsync(message.Settings ?? throw new IOException("Missing audio settings."));
                        // ASIO drivers may post a reset after Play returns when a panel setting
                        // changed. Keep the startup handshake open while those messages drain.
                        await Task.Delay(350);
                        bool reset = startupTest == "reset" || audio.Bridge.ResetRequested;
                        if (startupTest == "reset")
                        {
                            // Exercise duplicate asynchronous reset notifications too.
                            await Send(new AudioHostMessage { Kind = "status", AsioResetRequested = true });
                            await Send(new AudioHostMessage { Kind = "status", AsioResetRequested = true });
                        }
                        await Send(new AudioHostMessage { Kind = reset ? "reset" : "started",
                            AsioFrames = startupTest == null ? audio.Bridge.AsioFrames : 512,
                            OutputFormat = audio.Bridge.OutputFormat, OutputDiagnostics = audio.Bridge.OutputDiagnostics });
                    }
                    catch (Exception ex) { await Send(new AudioHostMessage { Kind = "error", Error = ex.Message }); return; }
                }
                else if (message?.Kind == "gain")
                {
                    if (audio.Bridge.Buffer is { } buffer) buffer.Gain = message.Gain;
                }
                else if (message?.Kind == "stop")
                {
                    if (audio.Bridge.Buffer is { } buffer) buffer.Gain = 0;
                    if (testStopHang) await Task.Delay(Timeout.Infinite);
                    await audio.StopAsync();
                    await Send(new AudioHostMessage { Kind = "stopped" });
                    return;
                }
            }
        }
        finally
        {
            GCSettings.LatencyMode = previousLatencyMode;
            done.Cancel();
            try { await telemetry; } catch { }
        }
    }
}
