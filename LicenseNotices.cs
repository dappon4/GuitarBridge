using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace GuitarBridge;

internal static class LicenseNotices
{
    internal static string ReadAll()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var text = new StringBuilder("Guitar Bridge — open-source notices\n\n");
        foreach (string name in assembly.GetManifestResourceNames().Where(n => n == "GuitarBridge.LICENSE" || n.StartsWith("GuitarBridge.licenses.", StringComparison.Ordinal)).OrderBy(n => n, StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            text.AppendLine(name.Replace("GuitarBridge.", "", StringComparison.Ordinal));
            text.AppendLine(new string('=', 60));
            text.AppendLine(reader.ReadToEnd());
            text.AppendLine();
        }
        return text.ToString();
    }

    internal static void Show()
    {
        Directory.CreateDirectory(Settings.Folder);
        string path = Path.Combine(Settings.Folder, "Open-source-notices.txt");
        File.WriteAllText(path, ReadAll());
        var info = new ProcessStartInfo("notepad.exe") { UseShellExecute = true };
        info.ArgumentList.Add(path);
        Process.Start(info);
    }
}
