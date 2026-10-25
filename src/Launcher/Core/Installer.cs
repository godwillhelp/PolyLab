using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace PolyLab.Launcher.Core;

// tool.json at the root of a tool package; Graft is the GRAFT release tag the tool is built for.
public sealed record ToolManifest(string Id, string Version, string Exe, string? Graft = null);

public enum InstallError { None, Running, NoPackage, NoDigest, Digest, BadPackage, NotFound, Download, Io }

public sealed record InstallResult(InstallError Error, string Detail = "")
{
    public bool Ok => Error == InstallError.None;
}

// Installs under the install root: tools\<id>\ from the Poly Lab releases, graft\ from the GRAFT releases.
// A package is downloaded beside its target, checked against the release digest, unpacked into a staging
// folder, checked again, and only then swapped in: a failure at any step leaves the installed version as it was.
public sealed class Installer(HttpClient http, string root)
{
    public const string ManifestFile = "tool.json";
    public const string GraftVersionFile = ".version";

    public string ToolsDir => Path.Combine(root, "tools");
    public string GraftDir => Path.Combine(root, "graft");
    public string ToolDir(string id) => Path.Combine(ToolsDir, id);

    public ToolManifest? Installed(string id) => ReadManifest(ToolDir(id));

    public string ExePath(ToolInfo tool) =>
        Path.Combine(ToolDir(tool.Id), Path.GetFileName(Installed(tool.Id)?.Exe ?? tool.Exe));

    public string? InstalledGraft()
    {
        try
        {
            var f = Path.Combine(GraftDir, GraftVersionFile);
            return File.Exists(f) && File.Exists(Path.Combine(GraftDir, "graft.exe")) ? File.ReadAllText(f).Trim() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public async Task<InstallResult> InstallToolAsync(ToolInfo tool, Offer offer, IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var name = $"{Path.GetFileNameWithoutExtension(tool.Exe)}-{offer.Version}-win-x64.zip";
        var asset = offer.Release.Assets.FirstOrDefault(a => a.Name == name);
        if (asset == null) return new(InstallError.NoPackage, name);
        return await InstallAsync(asset, ToolDir(tool.Id), ExePath(tool), stage => CheckTool(stage, tool, offer), progress, ct);
    }

    public async Task<InstallResult> InstallGraftAsync(string api, string tag, CancellationToken ct = default)
    {
        GhRelease? release;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{api}/releases/tags/{Uri.EscapeDataString(tag)}");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var resp = await http.SendAsync(req, ct);
            if (resp.StatusCode == HttpStatusCode.NotFound) return new(InstallError.NotFound, "GRAFT " + tag);
            if (!resp.IsSuccessStatusCode) return new(InstallError.Download, "HTTP " + (int)resp.StatusCode);
            release = JsonSerializer.Deserialize<GhRelease>(await resp.Content.ReadAsStringAsync(ct), Feed.Json);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException || e is TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new(InstallError.Download, e.Message);
        }
        var asset = release?.Assets?.FirstOrDefault(a => a.Name.EndsWith("-windows-x64.zip", StringComparison.OrdinalIgnoreCase));
        if (asset == null) return new(InstallError.NoPackage, "GRAFT " + tag);
        return await InstallAsync(asset, GraftDir, Path.Combine(GraftDir, "graft.exe"), stage => FinishGraft(stage, tag), null, ct);
    }

    public InstallResult Uninstall(ToolInfo tool)
    {
        if (IsRunning(ExePath(tool))) return new(InstallError.Running);
        var old = Sibling(ToolDir(tool.Id), ".old");
        try
        {
            Clean(old);
            if (Directory.Exists(ToolDir(tool.Id))) Directory.Move(ToolDir(tool.Id), old);
            Clean(old);
            return new(InstallError.None);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new(InstallError.Io, e.Message); }
    }

    // A folder can be renamed while an exe inside it runs, so the swap itself is no lock: a running copy is
    // found by its image path and the install is refused before anything moves.
    public static bool IsRunning(string exe)
    {
        var full = Path.GetFullPath(exe);
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
            using (p)
            {
                try
                {
                    if (string.Equals(p.MainModule?.FileName, full, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException) { }
            }
        return false;
    }

    private async Task<InstallResult> InstallAsync(GhAsset asset, string target, string exe, Func<string, string?> finish,
        IProgress<double>? progress, CancellationToken ct)
    {
        const string prefix = "sha256:";
        if (asset.Digest is not { Length: 71 } digest || !digest.StartsWith(prefix, StringComparison.Ordinal))
            return new(InstallError.NoDigest, asset.Name);
        if (IsRunning(exe)) return new(InstallError.Running);
        string zip = Sibling(target, ".zip"), stage = Sibling(target, ".new"), old = Sibling(target, ".old");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Clean(zip, stage, old);
            var got = await DownloadAsync(asset, zip, progress, ct);
            if (!string.Equals(got, digest[prefix.Length..], StringComparison.OrdinalIgnoreCase))
                return new(InstallError.Digest, asset.Name);
            ZipFile.ExtractToDirectory(zip, stage);
            if (finish(stage) is { } bad) return new(InstallError.BadPackage, bad);
            if (IsRunning(exe)) return new(InstallError.Running);
            if (Directory.Exists(target)) Directory.Move(target, old);
            try
            {
                Directory.Move(stage, target);
            }
            catch (Exception) when (!Directory.Exists(target) && Directory.Exists(old))
            {
                Directory.Move(old, target);
                throw;
            }
            return new(InstallError.None);
        }
        catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new(InstallError.Download, e.Message);
        }
        catch (InvalidDataException e) { return new(InstallError.BadPackage, e.Message); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new(InstallError.Io, e.Message); }
        finally
        {
            Clean(zip, stage, old);
        }
    }

    // Streams the asset to disk and returns its SHA-256 as hex.
    private async Task<string> DownloadAsync(GhAsset asset, string path, IProgress<double>? progress, CancellationToken ct)
    {
        using var resp = await http.GetAsync(asset.BrowserDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? asset.Size;
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(path);
        var buf = new byte[1 << 16];
        long done = 0;
        int n;
        while ((n = await src.ReadAsync(buf, ct)) > 0)
        {
            sha.AppendData(buf, 0, n);
            await dst.WriteAsync(buf.AsMemory(0, n), ct);
            done += n;
            if (total > 0) progress?.Report(Math.Min(1, (double)done / total));
        }
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    private static string? CheckTool(string stage, ToolInfo tool, Offer offer)
    {
        var m = ReadManifest(stage);
        if (m == null) return ManifestFile + " missing";
        if (m.Id != tool.Id) return $"{ManifestFile}: id {m.Id}";
        if (m.Version != offer.Version.ToString()) return $"{ManifestFile}: version {m.Version}";
        if (Path.GetFileName(m.Exe) != m.Exe || !File.Exists(Path.Combine(stage, m.Exe))) return m.Exe + " missing";
        return null;
    }

    private static string? FinishGraft(string stage, string tag)
    {
        foreach (var need in new[] { "graft.exe", "hid.dll" })
            if (!File.Exists(Path.Combine(stage, need))) return need + " missing";
        File.WriteAllText(Path.Combine(stage, GraftVersionFile), tag);
        File.WriteAllText(Path.Combine(stage, "GRAFT_SOURCE.txt"),
            $"GRAFT {tag} (KR_GRAFTED) is licensed under GPL-3.0-or-later with the GRAFT plugin exception.\n" +
            "Poly Lab downloaded it unmodified from the upstream release. Complete corresponding source:\n" +
            "https://github.com/KRdayzmodding/KR_GRAFTED\n" +
            "The licence texts ship in this folder (LICENSE, LICENSE-EXCEPTION).\n");
        return null;
    }

    private static ToolManifest? ReadManifest(string dir)
    {
        try
        {
            var f = Path.Combine(dir, ManifestFile);
            return File.Exists(f) ? JsonSerializer.Deserialize<ToolManifest>(File.ReadAllText(f), Feed.Json) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    // tools\.pbog.new beside tools\pbog: same volume, so the swap is a rename.
    private static string Sibling(string target, string suffix) =>
        Path.Combine(Path.GetDirectoryName(target)!, "." + Path.GetFileName(target) + suffix);

    private static void Clean(params string[] paths)
    {
        foreach (var p in paths)
            try
            {
                if (File.Exists(p)) File.Delete(p);
                else if (Directory.Exists(p)) Directory.Delete(p, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
