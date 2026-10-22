using System;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GuitarBridge;

public static class HardwareProbe
{
    // List available devices without choosing or capturing an input.
    public static string Run()
    {
        var report = new StringBuilder();
        try
        {
            foreach (string driver in AsioOut.GetDriverNames()) report.AppendLine("ASIO driver: " + driver);
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device) report.AppendLine("Output: " + device.FriendlyName);
            }
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (device) report.AppendLine("Windows input: " + device.FriendlyName);
            }
        }
        catch (Exception ex) { report.AppendLine("Device check: " + ex.Message); }
        return report.ToString();
    }
}
