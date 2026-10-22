using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GuitarBridge;

internal static class StartupProbe
{
    internal static void TestNativeOutput(string path)
    {
        var settings = Settings.Load();
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDevice(settings.OutputId);
        using var client = device.AudioClient;
        var format = client.MixFormat;
        File.WriteAllText(path, $"Initializing exact Windows mix format: {format}; no automatic-conversion flags.\n");
        client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.EventCallback, 100000, 0, format, Guid.Empty);
        File.AppendAllText(path, "PASS: native shared output initialized.\n");
    }

    internal static async Task RunAsync(string path, bool shared = false, int? rate = null, bool defaultOutput = false)
    {
        var settings = Settings.Load();
        settings.Volume = 0;
        settings.AutoStart = false;
        if (shared) settings.Exclusive = false;
        if (rate.HasValue) settings.SampleRate = rate.Value;
        if (defaultOutput)
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            settings.OutputId = device.ID;
        }
        var client = new AudioHostClient(startupTimeout: TimeSpan.FromSeconds(40));
        var report = new StringBuilder();
        report.AppendLine($"Input: {settings.Driver} / {settings.InputName}; {settings.SampleRate} Hz; {(settings.Exclusive ? "exclusive" : "shared")} output. Test volume: zero.");
        using (var enumerator = new MMDeviceEnumerator())
        using (var device = enumerator.GetDevice(settings.OutputId)) report.AppendLine("Output: " + device.FriendlyName);
        string last = "";
        try
        {
            var start = client.StartAsync(settings);
            while (!start.IsCompleted)
            {
                string status = $"PID={client.DiagnosticProcessId}; stage={client.StartupStage}";
                if (status != last) { report.AppendLine(status); File.WriteAllText(path, report.ToString()); last = status; }
                await Task.Delay(100);
            }
            await start;
            double totalQueue = 0, maxQueue = 0;
            int previousGaps = 0, previousResyncs = 0;
            for (int i = 0; i < 300; i++)
            {
                await Task.Delay(100);
                if (client.Fault != null) throw new InvalidOperationException(client.Fault);
                double queue = client.Buffer?.BufferedMs ?? 0;
                totalQueue += queue; maxQueue = Math.Max(maxQueue, queue);
                int gaps = client.Buffer?.Underruns ?? 0, resyncs = client.Buffer?.Overruns ?? 0;
                if (gaps != previousGaps || resyncs != previousResyncs)
                    report.AppendLine($"At {(i + 1) / 10.0:0.0}s after startup: gaps={gaps}, resyncs={resyncs}.");
                previousGaps = gaps; previousResyncs = resyncs;
            }
            report.AppendLine($"Started after {client.StartupAttempts} attempt(s), {client.AsioFrames} ASIO samples. Output: " + client.OutputFormat + ". Fault: " + (client.Fault ?? "none"));
            report.AppendLine(client.OutputDiagnostics);
            report.AppendLine($"30-second stability: queue mean {totalQueue / 300:0.##} ms, max {maxQueue:0.##} ms; gaps {client.Buffer?.Underruns}, resyncs {client.Buffer?.Overruns}.");
            report.AppendLine($"Final queue target: {client.Buffer?.QueueTargetMs:0.##} ms.");
            if (!settings.Exclusive)
            {
                using var enumerator = new MMDeviceEnumerator();
                using var device = enumerator.GetDevice(settings.OutputId);
                using var otherApp = new WasapiOut(device, AudioClientShareMode.Shared, true, settings.BufferMs);
                Exception? playbackError = null;
                otherApp.PlaybackStopped += (_, e) => System.Threading.Interlocked.Exchange(ref playbackError, e.Exception);
                otherApp.Init(new SilenceProvider(WaveFormat.CreateIeeeFloatWaveFormat(settings.SampleRate, 2)));
                // Diagnostic only: NAudio 2.2.1's old renderer fills this entire
                // allocation on each event. Inspect its client to compare with
                // the new one-period queue, without claiming acoustic latency.
                var oldClient = typeof(WasapiOut).GetField("audioClient", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(otherApp) as AudioClient;
                if (oldClient != null)
                    report.AppendLine($"Previous output path fills {oldClient.BufferSize} frames at {otherApp.OutputWaveFormat.SampleRate} Hz = {oldClient.BufferSize * 1000.0 / otherApp.OutputWaveFormat.SampleRate:0.##} ms; reported stream latency {oldClient.StreamLatency / 10000.0:0.##} ms (not end-to-end).");
                otherApp.Play();
                await Task.Delay(3000);
                if (otherApp.PlaybackState != PlaybackState.Playing || playbackError != null || client.Fault != null)
                    throw new InvalidOperationException("Concurrent shared playback failed. " + (playbackError?.Message ?? client.Fault));
                otherApp.Stop();
                report.AppendLine("PASS: a second Windows shared audio stream played concurrently with ASIO routing on the same speakers (silent test).");
            }
        }
        catch (Exception ex) { report.AppendLine("Result: " + ex.Message); }
        finally
        {
            // Release the native devices before terminating the diagnostic host.
            // Forced process exit during playback can leave a driver holding the output.
            try
            {
                if (client.Running)
                {
                    await client.StopAsync();
                    report.AppendLine("Session stopped cleanly.");
                }
            }
            catch (Exception ex) { report.AppendLine("Cleanup: " + ex.Message); }
            finally { await client.CloseAsync(); }
            File.WriteAllText(path, report.ToString());
        }
    }
}
