using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using System.Reflection;
using System.Diagnostics;
using System.Windows.Threading;

namespace GuitarBridge;

public static class SelfTests
{
    public static string BenchmarkBuffer()
    {
        var buffer = new BridgeBuffer(48000, 768) { Gain = 0.75f };
        var capture = new float[256 * 2];
        for (int i = 0; i < capture.Length; i++) capture[i] = (float)Math.Sin(i * 0.02) * 0.5f;
        var render = new byte[512 * 8];
        buffer.Push(capture, 256, 2); buffer.Push(capture, 256, 2);
        void Cycle() { buffer.Push(capture, 256, 2); buffer.Push(capture, 256, 2); buffer.Read(render, 0, render.Length); }
        for (int i = 0; i < 50000; i++) Cycle();
        var times = new double[5];
        long allocated = 0;
        for (int run = 0; run < times.Length; run++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            for (int i = 0; i < 40000; i++) Cycle();
            times[run] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            allocated += GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Array.Sort(times);
        return $"Median of 5 runs, 40000 x 512 stereo frames: {times[2]:0.###} ms\nManaged allocation across measured loops: {allocated} bytes\nGaps: {buffer.Underruns}; resyncs: {buffer.Overruns}; checksum: {BitConverter.ToSingle(render, 512):0.000000}\n";
    }
    public static async Task<string> RunHostAsync()
    {
        var client = new AudioHostClient(testStopHang: true);
        int ticks = 0;
        var heartbeat = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
        heartbeat.Tick += (_, _) => ticks++;
        heartbeat.Start();
        try
        {
            await client.StartAsync(new Settings());
            if (!client.Running) throw new Exception("Test audio host did not start.");
            await Task.Delay(150);
            int gainMessages = client.GainMessagesSent;
            await Task.Delay(150);
            if (gainMessages == 0 || client.GainMessagesSent != gainMessages) throw new Exception("Unchanged gain generated repeated control messages.");
            client.Buffer!.Gain = 0.25f;
            await Task.Delay(150);
            if (client.GainMessagesSent != gainMessages + 1) throw new Exception("A gain change was lost or repeatedly transmitted.");
            int beforeStop = ticks;
            var watch = Stopwatch.StartNew();
            bool timedOut = false;
            try { await client.StopAsync(); } catch (TimeoutException) { timedOut = true; }
            if (!timedOut || watch.Elapsed.TotalSeconds > 6 || client.Running || client.Buffer != null)
                throw new Exception("A stalled stop did not recover within the timeout.");
            if (ticks - beforeStop < 5) throw new Exception("The UI heartbeat stalled during driver recovery.");
            await client.StartAsync(new Settings());
            if (!client.Running) throw new Exception("Could not restart after a stalled stop.");
            try { await client.StopAsync(); } catch (TimeoutException) { }
            var testWindow = new MainWindow(false, new AudioHostClient(testStopHang: true));
            testWindow.Drivers.ItemsSource = new[] { "Simulated ASIO driver" };
            testWindow.Drivers.SelectedIndex = 0;
            testWindow.Outputs.ItemsSource = new[] { new OutputChoice("test-output", "Test speakers") };
            testWindow.Outputs.SelectedIndex = 0;
            testWindow.Channels.ItemsSource = new[] { new ChannelChoice(0, "1 · Microphone Array input 1") };
            testWindow.Channels.SelectedIndex = 0;
            testWindow.Volume.Value = 150;
            await testWindow.StartRoutingAsync();
            if (!testWindow.IsRouting || testWindow.RouteButton.IsChecked != true)
                throw new Exception("Window did not allow the selected ASIO input.");
            if (testWindow.Volume.Maximum != 200 || testWindow.VolumeLabel.Text != "150%" || Math.Abs(testWindow.IsRouting ? testWindow.Volume.Value - 150 : 1) > 0.001)
                throw new Exception("The boost control did not preserve the selected 150% output level.");
            const string originalError = "Original audio test error";
            testWindow.ShowError(originalError);
            await testWindow.StopRoutingAsync(originalError);
            if (!testWindow.RouteButton.IsEnabled || !testWindow.RefreshButton.IsEnabled || !testWindow.RouteSettings.IsEnabled || testWindow.RouteButton.IsChecked == true)
                throw new Exception("Window controls remained disabled after stop timeout.");
            if (!testWindow.WarningPopupText.Text.Contains(originalError)) throw new Exception("Original error was lost during cleanup.");
            await testWindow.StartRoutingAsync();
            if (!testWindow.IsRouting) throw new Exception("Window could not restart routing after recovery.");
            await testWindow.ExitAsync();
            var startupWindow = new MainWindow(false, new AudioHostClient(startupTimeout: TimeSpan.FromSeconds(1), testStartHang: true));
            startupWindow.Drivers.ItemsSource = new[] { "Simulated ASIO driver" };
            startupWindow.Drivers.SelectedIndex = 0;
            startupWindow.Outputs.ItemsSource = new[] { new OutputChoice("test-output", "Test output") };
            startupWindow.Outputs.SelectedIndex = 0;
            startupWindow.Channels.ItemsSource = new[] { new ChannelChoice(0, "1 · Test input") };
            startupWindow.Channels.SelectedIndex = 0;
            await startupWindow.StartRoutingAsync();
            if (!startupWindow.WarningPopupText.Text.Contains("Simulated output") || !startupWindow.RouteSettings.IsEnabled || !startupWindow.RouteButton.IsEnabled)
                throw new Exception("Startup timeout did not report its stage and restore controls.");
            await startupWindow.ExitAsync();
            var resetClient = new AudioHostClient { StartupTest = AudioStartupTest.ResetOnce };
            var resetWindow = new MainWindow(false, resetClient);
            resetWindow.Drivers.ItemsSource = new[] { "Simulated ASIO driver" };
            resetWindow.Drivers.SelectedIndex = 0;
            resetWindow.Outputs.ItemsSource = new[] { new OutputChoice("test-output", "Test output") };
            resetWindow.Outputs.SelectedIndex = 0;
            resetWindow.Channels.ItemsSource = new[] { new ChannelChoice(0, "1 · Test input") };
            resetWindow.Channels.SelectedIndex = 0;
            int beforeReset = ticks;
            try
            {
                // One UI start must encompass the initial reset and a fresh child process.
                await resetWindow.StartRoutingAsync();
                if (!resetWindow.IsRouting || resetWindow.RouteButton.IsChecked != true ||
                    resetClient.StartupAttempts != 2 || resetClient.StartupResetStops != 1 || resetClient.AsioFrames != 512 || resetClient.Fault != null ||
                    resetWindow.WarningButton.Visibility == System.Windows.Visibility.Visible)
                    throw new Exception("One-click startup did not apply the ASIO reset and new buffer size.");
                if (ticks - beforeReset < 5) throw new Exception("ASIO reset recovery blocked the UI dispatcher.");
                await resetWindow.StopRoutingAsync();
                if (resetClient.DiagnosticProcessId != null || !resetWindow.RouteSettings.IsEnabled)
                    throw new Exception("Recovered ASIO session did not stop cleanly.");
            }
            finally { await resetWindow.ExitAsync(); }
            var repeatedReset = new AudioHostClient { StartupTest = AudioStartupTest.ResetAlways };
            try
            {
                string? error = null;
                try { await repeatedReset.StartAsync(new Settings()); } catch (InvalidOperationException ex) { error = ex.Message; }
                if (error == null || !error.Contains("keeps requesting a reset") || repeatedReset.StartupAttempts != 2 || repeatedReset.StartupResetStops != 2 ||
                    repeatedReset.Running || repeatedReset.Buffer != null || repeatedReset.DiagnosticProcessId != null)
                    throw new Exception("Repeated ASIO resets were not bounded and cleaned up.");
            }
            finally { await repeatedReset.CloseAsync(); }
            var failedOutput = new AudioHostClient { StartupTest = AudioStartupTest.OutputFailure };
            try
            {
                string? error = null;
                try { await failedOutput.StartAsync(new Settings()); } catch (InvalidOperationException ex) { error = ex.Message; }
                if (error == null || !error.Contains("Simulated output failure") || failedOutput.StartupAttempts != 1 || failedOutput.DiagnosticProcessId != null)
                    throw new Exception("A genuine output error was retried or lost during ASIO recovery.");
            }
            finally { await failedOutput.CloseAsync(); }
            return "PASS: isolated audio process starts without opening audio devices in test mode.\nPASS: permanently stalled Stop is terminated within the timeout.\nPASS: UI dispatcher remains responsive throughout Stop recovery.\nPASS: a fresh session starts successfully after a stalled Stop.\nPASS: any selected ASIO channel is allowed, including microphone inputs.\nPASS: routing switch, Refresh, and device controls are restored after a native stop timeout.\nPASS: original audio error remains available in the warning details.\nPASS: window restarts routing after recovery and Quit closes its child process.\nPASS: startup timeout names the failing output stage and restores the window controls.\nPASS: one UI start automatically reopens after ASIO reset notifications and reports the new buffer size without a warning.\nPASS: ASIO reset recovery keeps the UI responsive and the recovered session stops cleanly.\nPASS: persistent ASIO reset requests stop after one automatic retry and release the child process.\nPASS: genuine output errors are preserved and are not automatically retried.";
        }
        finally { heartbeat.Stop(); try { await client.CloseAsync(); } catch { } }
    }

    public static string Run()
    {
        var results = new List<string>();
        void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); results.Add("PASS: " + name); }
        var gainControl = new AudioTelemetry { Gain = 1.5f };
        Check(gainControl.Gain == 1.5f && OutputLevel.Normalize(9) == 2 && OutputLevel.Normalize(float.NaN) == 0,
            "Gain controls allow boost to 200% and reject invalid gain values");
        var boosted = new BridgeBuffer(48000, 128) { Gain = 2 };
        var quietInput = new float[512];
        for (int i = 0; i < 256; i++) { quietInput[i * 2] = 0.2f; quietInput[i * 2 + 1] = -0.1f; }
        boosted.Push(quietInput, 256, 2);
        var boostedOutput = new float[128]; boosted.Read(boostedOutput, 0, boostedOutput.Length);
        Check(Math.Abs(boostedOutput[0] - 0.4f) < 0.00001 && Math.Abs(boostedOutput[1] + 0.2f) < 0.00001 && boosted.LimiterReductionDb == 0,
            "200% doubles quiet audio without altering stereo balance or engaging the limiter");
        boosted = new BridgeBuffer(48000, 128) { Gain = 2 };
        for (int i = 0; i < 256; i++) { quietInput[i * 2] = 0.75f; quietInput[i * 2 + 1] = -0.375f; }
        boosted.Push(quietInput, 256, 2); boosted.Read(boostedOutput, 0, boostedOutput.Length);
        bool safeStereo = true;
        for (int i = 0; i < 64; i++)
            safeStereo &= float.IsFinite(boostedOutput[i * 2]) && Math.Abs(boostedOutput[i * 2]) <= 1 &&
                Math.Abs(boostedOutput[i * 2 + 1]) <= 1 && Math.Abs(boostedOutput[i * 2 + 1] + boostedOutput[i * 2] / 2) < 0.000001;
        Check(safeStereo && boosted.LimiterReductionDb > 3, "Boost limiting contains peaks immediately and links both stereo channels");
        var releaseLimiter = new OutputLimiter(48000);
        float limitLeft = 2, limitRight = -1;
        releaseLimiter.Process(ref limitLeft, ref limitRight);
        Check(limitLeft == 1 && limitRight == -0.5f, "The limiter catches the first over-range sample without lookahead");
        float previousLevel = 0;
        bool smooth = true;
        for (int i = 0; i < 9600; i++)
        {
            limitLeft = 0.5f; limitRight = -0.25f;
            releaseLimiter.Process(ref limitLeft, ref limitRight);
            smooth &= limitLeft >= previousLevel && limitLeft <= 0.5f;
            previousLevel = limitLeft;
        }
        Check(smooth && limitLeft > 0.49f, "Limiter gain recovers smoothly after a peak without adding an audio buffer");
        Check(LowLatencySharedOutput.SelectPeriod(48000, 5, 48, 144, 480) == 240 &&
              LowLatencySharedOutput.SelectPeriod(44100, 5, 4, 48, 448) == 224 &&
              LowLatencySharedOutput.SelectPeriod(48000, 1, 48, 144, 480) == 144 &&
              LowLatencySharedOutput.SelectPeriod(48000, 50, 48, 144, 480) == 480,
              "Shared engine periods respect the request, minimum, maximum, and fundamental step");
        bool invalidPeriod = false;
        try { LowLatencySharedOutput.SelectPeriod(48000, 5, 0, 0, 480); } catch (NotSupportedException) { invalidPeriod = true; }
        Check(invalidPeriod, "Invalid driver periods trigger the compatibility path");
        var conversionInput = new BridgeBuffer(44100, 256) { Gain = 1 };
        var conversionSamples = new float[4096];
        for (int i = 0; i < 2048; i++) { conversionSamples[2 * i] = 0.25f; conversionSamples[2 * i + 1] = -0.5f; }
        conversionInput.Push(conversionSamples, 2048, 2);
        var converted = LowLatencySharedOutput.ConvertSource(conversionInput, NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
        var convertedBytes = new byte[240 * 8];
        converted.Read(convertedBytes, 0, convertedBytes.Length);
        Check(Math.Abs(BitConverter.ToSingle(convertedBytes, 200 * 8) - 0.25f) < 0.001 &&
              Math.Abs(BitConverter.ToSingle(convertedBytes, 200 * 8 + 4) + 0.5f) < 0.001,
              "44.1 kHz capture converts to 48 kHz speaker format without losing stereo or gain");
        var monoInput = new BridgeBuffer(48000, 128) { Gain = 1 };
        monoInput.Push(conversionSamples, 2048, 2);
        var pcm = LowLatencySharedOutput.ConvertSource(monoInput, new NAudio.Wave.WaveFormat(48000, 16, 1));
        var pcmBytes = new byte[128 * 2]; pcm.Read(pcmBytes, 0, pcmBytes.Length);
        Check(Math.Abs(BitConverter.ToInt16(pcmBytes, 0) + 4096) < 2, "Mono PCM16 speakers receive the averaged stereo channels");
        bool unsupportedLayout = false;
        try { LowLatencySharedOutput.ConvertSource(monoInput, NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(48000, 6)); }
        catch (NotSupportedException) { unsupportedLayout = true; }
        Check(unsupportedLayout, "Unsupported speaker layouts select compatibility instead of corrupting audio");
        var b = new BridgeBuffer(48000, 128) { Gain = 1 };
        var bytes = new byte[64 * 8 + 16];
        Array.Fill(bytes, (byte)123);
        b.Read(bytes, 8, 64 * 8);
        Check(bytes[0] == 123 && bytes[^1] == 123 && BitConverter.ToSingle(bytes, 8) == 0, "Empty buffer produces silence and respects byte offsets");
        var mono = new float[256]; Array.Fill(mono, 0.25f);
        b.Push(mono, 256, 1);
        b.Read(bytes, 8, 64 * 8);
        Check(Math.Abs(BitConverter.ToSingle(bytes, 8) - 0.25f) < 0.0001 && BitConverter.ToSingle(bytes, 8) == BitConverter.ToSingle(bytes, 12), "Mono is duplicated to both speakers");
        b.Gain = 0;
        b.Read(bytes, 8, 64 * 8);
        Check(BitConverter.ToSingle(bytes, 8) == 0, "Live gain mutes playback");
        b = new BridgeBuffer(48000, 128) { Gain = 0.5f };
        var stereo = new float[512];
        for (int i = 0; i < 256; i++) { stereo[i * 2] = 0.5f; stereo[i * 2 + 1] = -0.25f; }
        b.Push(stereo, 256, 2); b.Read(bytes, 8, 64 * 8);
        Check(Math.Abs(BitConverter.ToSingle(bytes, 8) - 0.25f) < 0.0001 && Math.Abs(BitConverter.ToSingle(bytes, 12) + 0.125f) < 0.0001, "Stereo separation and gain are preserved");
        for (int i = 0; i < 20; i++) b.Read(bytes, 8, 64 * 8);
        Check(b.Underruns > 0 && BitConverter.ToSingle(bytes, 8) == 0, "Starvation produces silence and records an underrun");
        b.Push(stereo, 256, 2); b.Read(bytes, 8, 64 * 8);
        Check(BitConverter.ToSingle(bytes, 8) != 0, "Stream recovers after starvation");
        Check(b.TargetMs > 128 * 1000.0 / 48000 && b.TargetMs <= 256 * 1000.0 / 48000,
            "Starvation adds a bounded safety margin to the shorter queue");
        for (int i = 0; i < 500; i++) b.Push(stereo, 256, 2);
        b.Read(bytes, 8, 64 * 8);
        Check(b.Overruns > 0 && b.BufferedMs < 20, "Overflow and scheduler stalls cannot accumulate latency");
        b = new BridgeBuffer(48000, 256) { Gain = 1 };
        var block = new byte[128 * 8];
        var input = new float[128]; Array.Fill(input, 0.2f);
        b.Push(input, 128, 1); b.Push(input, 128, 1); b.Push(input, 128, 1);
        for (int i = 0; i < 100000; i++)
        {
            b.Push(input, 128, 1);
            if (i % 1000 == 0) b.Push(new[] { 0.2f }, 1, 1);
            b.Read(block, 0, block.Length);
        }
        Check(b.Underruns == 0 && b.Overruns == 0 && b.BufferedMs < 20, "Long simulated session with independent clock drift stays bounded");
        // Independent 256-frame capture and 144-frame render callbacks, with
        // positive and negative clock drift and different callback phases.
        foreach (double drift in new[] { 0.9999, 1.0001 })
        foreach (int phase in new[] { 0, 63, 143 })
        {
            var low = new BridgeBuffer(48000, BridgeBuffer.LowLatencyTarget(256, 144));
            low.ConfigureLatency(256, 144);
            var capture = new float[256]; Array.Fill(capture, 0.2f);
            var render = new byte[144 * 8];
            double nextCapture = 0, nextRender = phase;
            for (int i = 0; i < 100000; i++)
            {
                if (nextCapture <= nextRender) { low.Push(capture, 256, 1); nextCapture += 256 * drift; }
                else { low.Read(render, 0, render.Length); nextRender += 144; }
            }
            Check(low.Underruns == 0 && low.Overruns == 0 && low.BufferedMs < 15,
                $"Small queue remains stable with independent callback sizes, drift {drift}, phase {phase} (gaps {low.Underruns}, resyncs {low.Overruns}, queue {low.BufferedMs:0.##} ms)");
            Check(low.TargetMs < BridgeBuffer.LowLatencyTarget(256, 144) * 1000.0 / 48000,
                "Stable routing gradually releases excess capture headroom");
        }
        var exactFormat = new BridgeBuffer(48000, 768);
        Check(ReferenceEquals(exactFormat, LowLatencySharedOutput.ConvertSource(exactFormat, exactFormat.WaveFormat)),
            "Matching float stereo rates bypass sample-conversion adapters");
        var bytePath = new BridgeBuffer(44100, 512) { Gain = 0.73f };
        var floatPath = new BridgeBuffer(44100, 512) { Gain = 0.73f };
        var byteResult = new byte[256 * 8]; var floatResult = new float[256 * 2 + 2];
        floatResult[0] = 123; floatResult[^1] = 456;
        for (int cycle = 0; cycle < 1000; cycle++)
        {
            bytePath.Push(stereo, 256, 2); floatPath.Push(stereo, 256, 2);
            bytePath.Read(byteResult, 0, byteResult.Length); floatPath.Read(floatResult, 1, 512);
            for (int i = 0; i < 512; i++)
                if (BitConverter.ToSingle(byteResult, i * 4) != floatResult[i + 1]) throw new Exception("Float and byte audio paths disagree.");
        }
        Check(floatResult[0] == 123 && floatResult[^1] == 456, "Direct float reads preserve audio samples, gain, offsets, and ring-wrap behavior");
        var safety = new BridgeBuffer(48000, 402);
        safety.ConfigureLatency(256, 144);
        safety.Push(stereo, 256, 2); safety.Push(stereo, 256, 2);
        var safetyRead = new byte[144 * 8];
        for (int i = 0; i < 20; i++) safety.Read(safetyRead, 0, safetyRead.Length);
        double backedOff = safety.TargetMs;
        for (int i = 0; i < 10000; i++) { safety.Push(stereo, 144, 2); safety.Read(safetyRead, 0, safetyRead.Length); }
        Check(safety.Underruns > 0 && backedOff > 402 * 1000.0 / 48000 && safety.TargetMs >= backedOff,
            "A starvation increases headroom and disables further automatic trimming for that session");
        var wrap = new BridgeBuffer(48000, 512) { Gain = 1 };
        var wave = new float[512]; var waveBytes = new byte[256 * 8];
        long sampleIndex = 0; float lastSample = 0; bool sawSignal = false;
        for (int cycle = 0; cycle < 2000; cycle++)
        {
            for (int i = 0; i < 256; i++, sampleIndex++)
            { wave[i * 2] = (float)Math.Sin(sampleIndex * 0.002) * 0.5f; wave[i * 2 + 1] = -wave[i * 2]; }
            wrap.Push(wave, 256, 2);
            wrap.Read(waveBytes, 0, waveBytes.Length);
            for (int i = 0; i < 256; i++)
            {
                float left = BitConverter.ToSingle(waveBytes, i * 8), right = BitConverter.ToSingle(waveBytes, i * 8 + 4);
                if (Math.Abs(left + right) > 0.000001f || (sawSignal && Math.Abs(left - lastSample) > 0.01f))
                    throw new Exception("Audio discontinuity at a ring wrap or channel boundary.");
                if (left != 0) sawSignal = true;
                lastSample = left;
            }
        }
        Check(sawSignal && wrap.Underruns == 0 && wrap.Overruns == 0, "Continuous stereo audio survives repeated ring wraps without discontinuities");
        b = new BridgeBuffer(48000, 256);
        var producer = Task.Run(() => { for (int i = 0; i < 10000; i++) b.Push(input, 128, 1); });
        var consumer = Task.Run(() => { for (int i = 0; i < 10000; i++) b.Read(block, 0, block.Length); });
        Check(Task.WaitAll(new[] { producer, consumer }, 10000) && b.BufferedMs <= 1000, "Concurrent capture and rendering complete without deadlock or unbounded queue");
        var testBuffer = new BridgeBuffer(48000, 256);
        var gate = typeof(BridgeBuffer).GetField("gate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(testBuffer)!;
        using var locked = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var stalledAudio = Task.Run(() => { lock (gate) { locked.Set(); release.Wait(); } });
        Check(locked.Wait(2000), "Simulated audio stall started");
        try
        {
            var uiWork = Task.Run(() =>
            {
                for (int i = 0; i < 10000; i++) { testBuffer.Gain = i % 101 / 100f; _ = testBuffer.BufferedMs; _ = testBuffer.Peak; }
            });
            Check(uiWork.Wait(1000), "Volume changes and UI meter reads never wait for a stalled audio lock");
            var captureDuringRender = Task.Run(() => testBuffer.Push(input, 128, 1));
            Check(captureDuringRender.Wait(1000) && testBuffer.Overruns == 0 && testBuffer.BufferedMs > 0,
                "Capture publishes a complete block without waiting or dropping it while the renderer is stalled");
        }
        finally { release.Set(); stalledAudio.Wait(2000); }
        var volumeWork = Task.Run(() => { for (int i = 0; i < 100000; i++) testBuffer.Gain = i % 101 / 100f; });
        var renderWork = Task.Run(() => { for (int i = 0; i < 10000; i++) { testBuffer.Push(input, 128, 1); testBuffer.Read(block, 0, block.Length); } });
        Check(Task.WaitAll(new[] { volumeWork, renderWork }, 10000), "Rapid volume adjustment and audio processing run concurrently");
        var worker = new AudioWorker();
        int owner = 0;
        worker.RunAsync(() => owner = Environment.CurrentManagedThreadId).GetAwaiter().GetResult();
        Check(owner != Environment.CurrentManagedThreadId, "Driver lifecycle runs off the UI thread");
        var slowDriver = worker.RunAsync(() => Thread.Sleep(300));
        Check(!slowDriver.IsCompleted, "A slow driver operation returns control immediately");
        slowDriver.GetAwaiter().GetResult();
        worker.RunAsync(() =>
        {
            if (owner != Environment.CurrentManagedThreadId || Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                throw new Exception("Driver thread affinity changed");
        }).GetAwaiter().GetResult();
        worker.CloseAsync().GetAwaiter().GetResult();
        results.Add("PASS: Driver operations retain STA thread affinity and shut down cleanly");
        return string.Join(Environment.NewLine, results);
    }
}
