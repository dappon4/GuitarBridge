using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;

namespace GuitarBridge;

internal static class DefaultOutputTests
{
    internal sealed class FakePolicy : IDefaultOutputPolicy
    {
        internal readonly Dictionary<Role, string> Current = new()
        {
            [Role.Console] = "original-console", [Role.Multimedia] = "original-media", [Role.Communications] = "original-calls"
        };
        internal readonly HashSet<string> Missing = new();
        internal Role? FailOnce;
        internal int Changes;
        public string? Get(Role role) => Current.GetValueOrDefault(role);
        public bool IsActive(string id) => !Missing.Contains(id);
        public void Set(string id, Role role)
        {
            if (FailOnce == role) { FailOnce = null; throw new InvalidOperationException("Simulated Windows output failure."); }
            Current[role] = id;
            Changes++;
        }
    }

    internal static async Task<string> RunAsync()
    {
        var results = new List<string>();
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); results.Add("PASS: " + message); }
        string directory = Path.Combine(Path.GetTempPath(), "GuitarBridge-output-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string journal = Path.Combine(directory, "restore.json");
        var policy = new FakePolicy();
        var output = new DefaultOutputRouting(policy, journal);
        try
        {
            await output.PrepareAsync("route-output");
            Check(policy.Changes == 0 && !File.Exists(journal), "Preparing a route saves defaults without switching Windows before audio starts.");
            await output.RestoreAsync();
            Check(policy.Changes == 0, "Failed audio startup does not change Windows defaults.");
            await output.PrepareAsync("route-output");
            await output.ActivateAsync();
            Check(policy.Get(Role.Console) == "route-output" && policy.Get(Role.Multimedia) == "route-output", "Active routing selects the requested Windows playback output.");
            Check(policy.Get(Role.Communications) == "original-calls", "The communications device is preserved.");
            Check(File.Exists(journal), "Original defaults are journaled before switching.");
            await output.RestoreAsync();
            Check(policy.Get(Role.Console) == "original-console" && policy.Get(Role.Multimedia) == "original-media", "Stop restores each playback role's original default.");
            Check(!File.Exists(journal), "Successful restoration clears the recovery file.");
            await output.PrepareAsync("route-output");
            await output.ActivateAsync();
            policy.Current[Role.Console] = "manual-choice";
            await output.RestoreAsync();
            Check(policy.Get(Role.Console) == "manual-choice" && policy.Get(Role.Multimedia) == "original-media", "A manual output change while routing is active is respected.");
            policy.Current[Role.Console] = "original-console";
            await output.PrepareAsync("route-output");
            policy.FailOnce = Role.Multimedia;
            try { await output.ActivateAsync(); throw new Exception("Simulated switch failure was ignored."); } catch (InvalidOperationException) { }
            await output.RestoreAsync();
            Check(policy.Get(Role.Console) == "original-console" && policy.Get(Role.Multimedia) == "original-media", "A partial Windows switch can be rolled back.");
            await output.PrepareAsync("route-output");
            await output.ActivateAsync();
            var recovered = new DefaultOutputRouting(policy, journal);
            await recovered.RestoreAsync();
            Check(policy.Get(Role.Console) == "original-console" && policy.Get(Role.Multimedia) == "original-media", "The next launch restores defaults after an unclean app exit.");
            output = new DefaultOutputRouting(policy, journal);
            await output.PrepareAsync("route-output");
            await output.ActivateAsync();
            policy.Missing.Add("original-media");
            try { await output.RestoreAsync(); throw new Exception("Missing device was ignored."); } catch (AggregateException) { }
            Check(policy.Get(Role.Console) == "original-console" && File.Exists(journal), "Disconnected originals retain recovery data while other roles restore normally.");
            policy.Missing.Clear();
            await output.RestoreAsync();
            Check(policy.Get(Role.Multimedia) == "original-media" && !File.Exists(journal), "Restoration succeeds when the original device returns.");
            await output.PrepareAsync("route-output");
            await output.ActivateAsync();
            policy.Current.Remove(Role.Console);
            await output.RestoreAsync();
            Check(policy.Get(Role.Console) == "original-console", "A missing current default is restored when the original remains available.");
            policy.Current[Role.Console] = policy.Current[Role.Multimedia] = "route-output";
            int changes = policy.Changes;
            await output.PrepareAsync("route-output");
            await output.ActivateAsync();
            await output.RestoreAsync();
            Check(policy.Changes == changes, "Selecting the existing default makes no unnecessary Windows changes.");
        }
        finally { Directory.Delete(directory, recursive: true); }
        return string.Join(Environment.NewLine, results);
    }

    internal static async Task<string> ProbeAsync(string journal)
    {
        var policy = new WindowsDefaultOutputPolicy();
        var before = Enum.GetValues<Role>().ToDictionary(role => role, role => policy.Get(role));
        using var enumerator = new MMDeviceEnumerator();
        string? target = null;
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device) if (!before.Values.Contains(device.ID)) target ??= device.ID;
        }
        if (target == null) return "SKIP: no alternate active playback device is available; no Windows settings changed.";
        var output = new DefaultOutputRouting(policy, journal);
        try
        {
            await output.PrepareAsync(target);
            foreach (var role in before.Keys) if (policy.Get(role) != before[role]) throw new Exception("Preparing a route changed Windows output.");
            await output.ActivateAsync();
            foreach (var role in DefaultOutputRouting.PlaybackRoles) if (before[role] != null && policy.Get(role) != target) throw new Exception("Windows output switch failed.");
        }
        finally { await output.RestoreAsync(); }
        foreach (var role in before.Keys) if (policy.Get(role) != before[role]) throw new Exception("The original Windows output was not restored.");
        return "PASS: Windows playback defaults switched to another active endpoint and returned to their originals. Communications output was unchanged. No audio device was opened for playback or capture.";
    }
}
