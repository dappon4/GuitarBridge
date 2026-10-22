using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace GuitarBridge;

internal static class UpdateTests
{
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }

    internal static async Task<string> RunAsync()
    {
        var results = new List<string>();
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); results.Add("PASS: " + message); }
        void Reject(Action action, string message) { try { action(); } catch (InvalidDataException) { results.Add("PASS: " + message); return; } throw new Exception(message); }
        string tag = "v99.0.0";
        string exe = "GuitarBridge-99.0.0-win-x64.exe", msi = "GuitarBridge-99.0.0-win-x64.msi";
        byte[] payload = Encoding.UTF8.GetBytes("synthetic update payload, never executed");
        string hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        object Asset(string name) => new { name, browser_download_url = $"https://github.com/{UpdateService.Repository}/releases/download/{tag}/{name}", size = payload.Length, digest = "sha256:" + hash };
        string json = JsonSerializer.Serialize(new { draft = false, prerelease = false, tag_name = tag, assets = new[] { Asset(exe), Asset(msi), Asset("SHA256SUMS.txt") } });
        var release = UpdateService.ParseRelease(json, new Version(0, 1, 0, 0))!;
        Check(release.Version == new Version(99, 0, 0), "New stable release is detected using numeric version ordering.");
        Check(UpdateService.ParseRelease(json, new Version(99, 0, 0, 0)) == null, "Equal four-part assembly version is not an update.");
        Check(UpdateService.ParseRelease(json, new Version(100, 0, 0)) == null, "Older release never causes a downgrade.");
        Check(UpdateService.ParseRelease(json.Replace("\"prerelease\":false", "\"prerelease\":true"), new Version(0, 0, 0)) == null, "Prereleases are excluded.");
        Check(UpdateService.ParseRelease(json.Replace("\"draft\":false", "\"draft\":true"), new Version(0, 0, 0)) == null, "Draft releases are excluded.");
        Reject(() => UpdateService.ParseRelease(json.Replace("github.com/", "example.org/"), new Version(0, 0, 0)), "Downloads outside this project's GitHub release are rejected.");
        Reject(() => UpdateService.ParseRelease(json.Replace(exe, "missing.exe"), new Version(0, 0, 0)), "Incomplete releases are rejected.");
        Reject(() => UpdateService.ChecksumFor(hash + "  " + exe + "\n" + hash + "  " + exe, exe), "Ambiguous checksums are rejected.");
        Reject(() => UpdateService.ChecksumFor(hash + "  other.exe", exe), "A checksum for another file is rejected.");

        string root = Path.Combine(Path.GetTempPath(), "GuitarBridge-update-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            bool corrupt = false;
            using var service = new UpdateService(new HttpClient(new Handler((request, token) =>
            {
                token.ThrowIfCancellationRequested();
                HttpContent content = request.RequestUri!.AbsolutePath.EndsWith("SHA256SUMS.txt", StringComparison.Ordinal)
                    ? new StringContent($"\uFEFF{hash}  {exe}\n{hash}  {msi}\n")
                    : new ByteArrayContent(corrupt ? new byte[payload.Length] : payload);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            })));
            var portable = await service.DownloadAsync(release, false, root, null, CancellationToken.None);
            Check(File.Exists(portable.File) && portable.Sha256 == hash && !portable.Installer, "Portable update downloads and verifies SHA-256 including BOM checksums.");
            var installer = await service.DownloadAsync(release, true, root, null, CancellationToken.None);
            Check(installer.Installer && installer.File.EndsWith(".msi", StringComparison.Ordinal), "MSI installations select the MSI payload.");
            string target = Path.Combine(root, "existing.exe");
            await File.WriteAllTextAsync(target, "original app");
            await UpdateWorker.ReplacePortableAsync(portable.File, target);
            Check(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(target))).ToLowerInvariant() == hash, "Portable replacement commits the complete verified payload.");
            await File.WriteAllTextAsync(target, "preserve original");
            try { await UpdateWorker.ReplacePortableAsync(Path.Combine(root, "missing.exe"), target); throw new Exception("Missing download unexpectedly copied."); }
            catch (FileNotFoundException) { }
            Check(await File.ReadAllTextAsync(target) == "preserve original", "Failed copying preserves the existing app.");
            bool restarted = false;
            string planPath = Path.Combine(Path.GetDirectoryName(portable.File)!, "update.json");
            var plan = new UpdatePlan(int.MaxValue, 0, target, portable.File, hash, false, "99.0.0");
            await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(plan));
            await UpdateWorker.ApplyAsync(planPath, (path, noAutoStart) => restarted = path == target && !noAutoStart);
            Check(restarted && Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(target))).ToLowerInvariant() == hash,
                "Full helper path re-verifies, replaces, and requests restart after the original process exits.");
            await File.WriteAllTextAsync(portable.File, "tampered after download");
            await File.WriteAllTextAsync(target, "preserve original");
            bool recovered = false;
            try { await UpdateWorker.ApplyAsync(planPath, (path, noAutoStart) => recovered = path == target && noAutoStart); throw new Exception("Tampering accepted."); }
            catch (InvalidDataException) { }
            Check(recovered && await File.ReadAllTextAsync(target) == "preserve original", "Helper detects post-download tampering and reopens the original app without starting audio.");
            corrupt = true;
            int directories = Directory.GetDirectories(root).Length;
            try { await service.DownloadAsync(release, false, root, null, CancellationToken.None); throw new Exception("Corruption accepted."); }
            catch (InvalidDataException) { results.Add("PASS: Corrupt download cannot be installed."); }
            Check(Directory.GetDirectories(root).Length == directories, "Failed downloads are cleaned up.");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try { await service.DownloadAsync(release, false, root, null, cancelled.Token); throw new Exception("Cancellation ignored."); }
            catch (OperationCanceledException) { results.Add("PASS: Update requests honor cancellation."); }
            using var absent = new UpdateService(new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)))));
            Check(await absent.CheckAsync(CancellationToken.None) == null, "A repository without releases is handled normally.");
            using var offline = new UpdateService(new HttpClient(new Handler((_, _) => throw new HttpRequestException("Simulated offline connection"))));
            try { await offline.CheckAsync(CancellationToken.None); throw new Exception("Offline check unexpectedly passed."); }
            catch (HttpRequestException) { results.Add("PASS: Offline failures propagate to the non-blocking update UI."); }
            using var slowCancellation = new CancellationTokenSource();
            using var slow = new UpdateService(new HttpClient(new Handler(async (_, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return new HttpResponseMessage(HttpStatusCode.OK);
            })));
            int ticks = 0;
            var heartbeat = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            heartbeat.Tick += (_, _) => ticks++;
            heartbeat.Start();
            try
            {
                var pending = slow.CheckAsync(slowCancellation.Token);
                await Task.Delay(100);
                Check(ticks > 0 && !pending.IsCompleted, "UI dispatcher stays responsive during a stalled update request.");
                slowCancellation.Cancel();
                try { await pending; throw new Exception("Stalled request ignored cancellation."); } catch (OperationCanceledException) { }
            }
            finally { heartbeat.Stop(); }
        }
        finally { Directory.Delete(root, recursive: true); }
        return string.Join(Environment.NewLine, results);
    }
}
