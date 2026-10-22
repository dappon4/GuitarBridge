using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GuitarBridge;

internal sealed record UpdatePlan(int ParentId, long ParentStarted, string Target, string Download, string Sha256, bool Installer, string Version);

internal static class UpdateWorker
{
    internal static async Task StartAsync(PreparedUpdate update, CancellationToken token)
    {
        string target = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the running app.");
        string directory = Path.GetDirectoryName(update.File)!;
        string helper = Path.Combine(directory, "GuitarBridge.Update.exe");
        // The helper must live outside the app folder so the original EXE can be replaced.
        await using (var input = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
        await using (var output = new FileStream(helper, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            await input.CopyToAsync(output, token);
        using var parent = Process.GetCurrentProcess();
        var plan = new UpdatePlan(parent.Id, parent.StartTime.ToUniversalTime().Ticks, target, update.File, update.Sha256, update.Installer, update.Version.ToString(3));
        string planPath = Path.Combine(directory, "update.json");
        await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(plan), token);
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--apply-update");
        start.ArgumentList.Add(planPath);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start the update helper.");
    }

    internal static async Task ApplyAsync(string planPath, Action<string, bool>? restart = null)
    {
        var plan = JsonSerializer.Deserialize<UpdatePlan>(await File.ReadAllTextAsync(planPath)) ?? throw new InvalidDataException("Invalid update instructions.");
        string directory = Path.GetDirectoryName(Path.GetFullPath(planPath))!;
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(plan.Download)), directory, StringComparison.OrdinalIgnoreCase) ||
            !Path.IsPathFullyQualified(plan.Target) || !plan.Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            !Version.TryParse(plan.Version, out var version) || version.Build < 0 ||
            Path.GetFullPath(plan.Target).Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Invalid update locations.");
        try
        {
            using var parent = Process.GetProcessById(plan.ParentId);
            if (parent.StartTime.ToUniversalTime().Ticks != plan.ParentStarted) throw new InvalidOperationException("The original app process changed.");
            await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        }
        catch (ArgumentException) { /* The original app already exited. */ }
        try
        {
            await UpdateService.VerifyAsync(plan.Download, plan.Sha256, CancellationToken.None);
            if (plan.Installer)
            {
                if (!UpdateService.IsMsiInstallation(plan.Target)) throw new InvalidOperationException("The installed app location changed.");
                var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe")) { UseShellExecute = true, Verb = "runas" };
                start.Arguments = $"/i \"{plan.Download}\" /passive /norestart";
                using var installer = Process.Start(start) ?? throw new InvalidOperationException("Windows Installer could not start.");
                await installer.WaitForExitAsync();
                if (installer.ExitCode != 0 && installer.ExitCode != 3010) throw new InvalidOperationException("Windows Installer returned " + installer.ExitCode + ".");
            }
            else await ReplacePortableAsync(plan.Download, plan.Target);
        }
        catch
        {
            (restart ?? Restart)(plan.Target, true);
            throw;
        }
        (restart ?? Restart)(plan.Target, false);
        // Keep only the small helper/plan until the next launch; never delete the running helper.
        try { File.Delete(plan.Download); } catch { }
    }

    internal static async Task ReplacePortableAsync(string download, string target)
    {
        string staging = target + ".update-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var input = new FileStream(download, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
            await using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                await input.CopyToAsync(output);
            // Same-directory rename keeps a failed copy from corrupting the working app.
            for (int retry = 0; ; retry++)
            {
                try { File.Move(staging, target, overwrite: true); break; }
                catch (IOException) when (retry < 20) { await Task.Delay(250); }
            }
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    private static void Restart(string path, bool noAutoStart)
    {
        var start = new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path)! };
        if (noAutoStart) start.ArgumentList.Add("--no-auto-start");
        Process.Start(start)?.Dispose();
    }
}
