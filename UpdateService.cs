using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace GuitarBridge;

internal sealed record UpdateAsset(string Name, Uri Url, long Size, string? Digest);
internal sealed record AppRelease(Version Version, string Tag, UpdateAsset Executable, UpdateAsset Installer, UpdateAsset Checksums);
internal sealed record PreparedUpdate(string File, string Sha256, bool Installer, Version Version);

internal sealed class UpdateService : IDisposable
{
    internal const string Repository = "dappon4/GuitarBridge";
    internal const long MaximumDownload = 512L * 1024 * 1024;
    private readonly HttpClient http;
    internal static Version CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
    internal static string DisplayVersion => CurrentVersion.ToString(3);

    internal UpdateService(HttpClient? client = null)
    {
        http = client ?? new HttpClient();
        http.Timeout = TimeSpan.FromMinutes(10);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("GuitarBridge/" + DisplayVersion);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    internal async Task<AppRelease?> CheckAsync(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return ParseRelease(await ReadSmallAsync(response, 1024 * 1024, timeout.Token).ConfigureAwait(false), CurrentVersion);
    }

    internal static AppRelease? ParseRelease(string json, Version current)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Regex.IsMatch(tag, @"^v\d+\.\d+\.\d+$") || !Version.TryParse(tag[1..], out var version)) return null;
        // Assembly versions have a fourth component; release tags have three.
        if (version <= new Version(current.Major, current.Minor, Math.Max(0, current.Build))) return null;
        UpdateAsset Asset(string name)
        {
            var matches = root.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("name").GetString() == name).ToArray();
            if (matches.Length != 1) throw new InvalidDataException("The release is missing a unique download: " + name);
            var item = matches[0];
            string url = item.GetProperty("browser_download_url").GetString() ?? "";
            string expected = $"https://github.com/{Repository}/releases/download/{tag}/{name}";
            if (url != expected) throw new InvalidDataException("The update download is outside the project release.");
            long size = item.GetProperty("size").GetInt64();
            if (size <= 0 || size > MaximumDownload) throw new InvalidDataException("Invalid update download size.");
            return new UpdateAsset(name, new Uri(url), size,
                item.TryGetProperty("digest", out var digest) ? digest.GetString() : null);
        }
        string basename = $"GuitarBridge-{version.ToString(3)}-win-x64";
        return new AppRelease(version, tag, Asset(basename + ".exe"), Asset(basename + ".msi"), Asset("SHA256SUMS.txt"));
    }

    internal async Task<PreparedUpdate> DownloadAsync(AppRelease release, bool installer, string cacheRoot, IProgress<int>? progress, CancellationToken token)
    {
        var asset = installer ? release.Installer : release.Executable;
        using var sumsResponse = await http.GetAsync(release.Checksums.Url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        sumsResponse.EnsureSuccessStatusCode();
        string sums = await ReadSmallAsync(sumsResponse, 16384, token).ConfigureAwait(false);
        string hash = ChecksumFor(sums, asset.Name);
        if (asset.Digest != null && !asset.Digest.Equals("sha256:" + hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("GitHub's asset digest and the release checksum disagree.");
        string directory = Path.Combine(cacheRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, asset.Name);
        try
        {
            using var response = await http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length != asset.Size) throw new InvalidDataException("Update size changed.");
            await using (var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                byte[] block = new byte[81920];
                long total = 0;
                int lastPercent = -1;
                while (true)
                {
                    int count = await input.ReadAsync(block, token).ConfigureAwait(false);
                    if (count == 0) break;
                    total += count;
                    if (total > asset.Size || total > MaximumDownload) throw new InvalidDataException("Update exceeded its declared size.");
                    await output.WriteAsync(block.AsMemory(0, count), token).ConfigureAwait(false);
                    int percent = (int)(total * 100 / asset.Size);
                    if (percent != lastPercent) { progress?.Report(percent); lastPercent = percent; }
                }
                if (total != asset.Size) throw new InvalidDataException("The update download was incomplete.");
            }
            await VerifyAsync(path, hash, token).ConfigureAwait(false);
            return new PreparedUpdate(path, hash, installer, release.Version);
        }
        catch { try { File.Delete(path); Directory.Delete(directory); } catch { } throw; }
    }

    internal static string ChecksumFor(string text, string name)
    {
        var matches = text.TrimStart('\uFEFF').Split('\n').Select(line => Regex.Match(line.TrimEnd('\r'), @"^([a-fA-F0-9]{64})  (.+)$"))
            .Where(match => match.Success && match.Groups[2].Value == name).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("The release has no unique SHA-256 checksum for " + name);
        return matches[0].Groups[1].Value.ToLowerInvariant();
    }

    internal static async Task VerifyAsync(string file, string expected, CancellationToken token)
    {
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        string actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Update checksum verification failed. Nothing was installed.");
    }

    private static async Task<string> ReadSmallAsync(HttpResponseMessage response, int maximum, CancellationToken token)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var memory = new MemoryStream();
        byte[] buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (memory.Length + count > maximum) throw new InvalidDataException("Update metadata is too large.");
            memory.Write(buffer, 0, count);
        }
        return System.Text.Encoding.UTF8.GetString(memory.ToArray()).TrimStart('\uFEFF');
    }

    internal static bool IsMsiInstallation(string executable)
    {
        string installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GuitarBridge", "GuitarBridge.exe");
        using var key = Registry.LocalMachine.OpenSubKey(@"Software\GuitarBridge");
        return Path.GetFullPath(executable).Equals(installed, StringComparison.OrdinalIgnoreCase) && key?.GetValue("Installed") is int value && value == 1;
    }

    public void Dispose() => http.Dispose();
}
