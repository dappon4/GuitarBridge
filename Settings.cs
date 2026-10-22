using System;
using System.IO;
using System.Text.Json;

namespace GuitarBridge;

public sealed class Settings
{
    public string Driver { get; set; } = "";
    public string OutputId { get; set; } = "";
    public int FirstChannel { get; set; }
    public string InputName { get; set; } = "";
    public bool Stereo { get; set; } = true;
    public int SampleRate { get; set; } = 48000;
    public int BufferMs { get; set; } = 10;
    public bool Exclusive { get; set; }
    public double Volume { get; set; } = 0.5;
    public bool AutoStart { get; set; }
    public bool CheckForUpdates { get; set; } = true;
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GuitarBridge");
    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(Folder, "settings.json"))) ?? new(); }
        catch { return new(); }
    }
    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, "settings.json"), JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
