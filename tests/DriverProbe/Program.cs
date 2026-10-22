using GuitarBridge;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Text.Json;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length < 2) return 2;
        string reportPath = args[1];
        try
        {
            if (args[0] == "--list")
            {
                var devices = new List<object>();
                using var enumerator = new MMDeviceEnumerator();
                foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    using (device)
                    {
                        try
                        {
                            using var output = new LowLatencySharedOutput(device.ID) { RequestedMs = 2 };
                            output.Init(new SilenceProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)));
                            devices.Add(new { Id = device.ID, Name = device.FriendlyName, Rate = output.OutputWaveFormat.SampleRate,
                                MinimumMs = output.MinimumPeriodMs, DefaultMs = output.DefaultPeriodMs, SelectedMs = output.PeriodMs,
                                OutputQueueMs = output.AheadMs, Raw = output.Raw,
                                Muted = device.AudioEndpointVolume.Mute, MasterVolume = device.AudioEndpointVolume.MasterVolumeLevelScalar });
                        }
                        catch (Exception ex) { devices.Add(new { Id = device.ID, Name = device.FriendlyName, Error = ex.Message }); }
                    }
                }
                File.WriteAllText(reportPath, JsonSerializer.Serialize(devices, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }
            if ((args[0] == "--tone" || args[0] == "--tone-B") && args.Length == 3)
            {
                double frequency = args[0] == "--tone-B" ? 660 : 440;
                var tone = new SignalGenerator(48000, 2) { Gain = 0.04, Frequency = frequency, Type = SignalGeneratorType.Sin };
                using var output = new LowLatencySharedOutput(args[2]) { RequestedMs = 2 };
                Exception? failure = null;
                output.PlaybackStopped += (_, e) => failure = e.Exception;
                output.Init(new SampleToWaveProvider(tone));
                output.Play();
                Thread.Sleep(3000);
                bool playing = output.PlaybackState == PlaybackState.Playing;
                output.Stop();
                if (!playing || failure != null) throw new InvalidOperationException("Speaker tone failed.", failure);
                File.WriteAllText(reportPath, $"PASS: quiet {frequency} Hz tone submitted to {args[2]} for three seconds; {output.PeriodMs:0.##} ms period. Physical audibility requires user confirmation.");
                return 0;
            }
            return 2;
        }
        catch (Exception ex) { File.WriteAllText(reportPath, ex.ToString()); return 1; }
    }
}
