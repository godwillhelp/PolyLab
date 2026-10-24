using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pfx.App.Core;
using Pfx.XRay;

namespace Pfx.App.XRay;

// Owner of the X-Ray import state (spec 3.9). On disk, in the user's folders (6.3, O2′): the sources (roots with tag
// and priority) and the substitute table in the settings folder; scan caches in the cache folder. In memory: the last
// scan, the index of the sources, the job running now. Jobs run on workers, one at a time, cancellable, with progress;
// results come back on the UI thread. xray.py gets the sources and the substitutes in every request.
public sealed class XrayWorkspace
{
    private CancellationTokenSource? _cancel;
    private int _indexGeneration;

    public XrayWorkspace()
    {
        Sources = Load<XrSourceSet>(SourcesPath) ?? new XrSourceSet();
        Substitutes = Load<Dictionary<string, string>>(SubstPath) ?? new Dictionary<string, string>();
    }

    public static string SourcesPath => Path.Combine(AppSettings.Folder, "xray_sources.json");
    public static string SubstPath => Path.Combine(AppSettings.Folder, "xray_subst.json");
    public static string CacheDir => Path.Combine(AppSettings.CacheFolder, "xray");

    public XrSourceSet Sources { get; }
    public Dictionary<string, string> Substitutes { get; }
    public XrScanResult? LastScan { get; private set; }
    public XrIndex? Index { get; private set; }
    public bool IndexBuilding { get; private set; }
    // The job running now (a localisation key of its name) and its progress 0..1; null = idle.
    public string? Job { get; private set; }
    public double Progress { get; private set; }
    public string ProgressText { get; private set; } = "";
    public bool Busy => Job != null;
    public XrBridgeResult? LastImport { get; private set; }
    public bool AutoSave { get; set; } = true;

    public event Action? Changed;
    public event Action<string, string>? Toast;

    private static T? Load<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllBytes(path)) : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void SaveFile(string path, object value)
    {
        if (!AutoSave)
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions { WriteIndented = true }));
    }

    public void SaveSources() => SaveFile(SourcesPath, Sources);
    public void SaveSubstitutes() => SaveFile(SubstPath, Substitutes);

    private static string CacheFile(string folder) =>
        Path.Combine(CacheDir, "scan_" + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(folder).TrimEnd('\\').ToLowerInvariant())))[..16] + ".json");

    private static XrScanCache? LoadCache(string folder) => Load<XrScanCache>(CacheFile(folder));

    private void SaveCache(XrScanCache cache)
    {
        Directory.CreateDirectory(CacheDir);
        AtomicFile.Write(CacheFile(cache.Folder), JsonSerializer.SerializeToUtf8Bytes(cache));
    }

    public void Cancel() => _cancel?.Cancel();

    // One job at a time: a second request while one runs is refused with a toast (the panels disable their buttons).
    private bool Run<T>(string job, Func<IProgress<(double, string)>, CancellationToken, T> work, Action<T> done)
    {
        if (Busy)
        {
            Toast?.Invoke(Loc.T("Xr.Toast.Busy"), "warn");
            return false;
        }
        Job = job;
        Progress = 0;
        ProgressText = "";
        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;
        var progress = new Progress<(double Part, string Text)>(p =>
        {
            Progress = p.Part;
            ProgressText = p.Text;
            Changed?.Invoke();
        });
        var ui = TaskScheduler.FromCurrentSynchronizationContext();
        Changed?.Invoke();
        Task.Run(() => work(progress, token), token).ContinueWith(t =>
        {
            Job = null;
            _cancel?.Dispose();
            _cancel = null;
            if (t.IsCanceled || t.Exception?.InnerException is OperationCanceledException)
                Toast?.Invoke(Loc.T("Xr.Toast.Cancelled"), "info");
            else if (t.Exception is { } ex)
                Toast?.Invoke(Loc.F("Xr.Toast.Failed", Loc.T(job), (ex.InnerException ?? ex).Message), "err");
            else
                done(t.Result);
            Changed?.Invoke();
        }, ui);
        return true;
    }

    public bool Scan(string folder, Action<XrScanResult>? after = null)
    {
        if (!Directory.Exists(folder))
        {
            Toast?.Invoke(Loc.F("Xr.Toast.NoFolder", folder), "err");
            return false;
        }
        return Run("Xr.Job.Scan", (progress, cancel) =>
        {
            var clock = Stopwatch.StartNew();
            var p = new Progress<XrScanProgress>(s => progress.Report((s.Total == 0 ? 0 : s.Done / (double)s.Total, Loc.F("Xr.Progress.Files", s.Done, s.Total))));
            var (result, cache) = XrScanner.Scan(folder, LoadCache(folder), p, cancel);
            SaveCache(cache);
            return result;
        }, result =>
        {
            LastScan = result;
            after?.Invoke(result);
        });
    }

    // A scanned mod becomes a source (or refreshes the one with the same core folder), then the index is rebuilt.
    public XrSource AddSource(XrMod mod, string tag)
    {
        var s = Sources.Add(mod, tag);
        SaveSources();
        RebuildIndex();
        return s;
    }

    public void SourcesEdited()
    {
        SaveSources();
        RebuildIndex();
    }

    // Built on a worker; a newer request makes an older result be dropped (generation). Roots no saved scan holds
    // (sources loaded from the settings of another session) are scanned first, from their own cache.
    public void RebuildIndex(Action<XrIndex>? after = null)
    {
        int generation = ++_indexGeneration;
        IndexBuilding = true;
        Changed?.Invoke();
        var sources = JsonSerializer.Deserialize<XrSourceSet>(JsonSerializer.SerializeToUtf8Bytes(Sources))!;
        var ui = TaskScheduler.FromCurrentSynchronizationContext();
        Task.Run(() =>
        {
            var caches = new List<XrScanCache>();
            foreach (var root in sources.Sources.SelectMany(s => s.Roots).Where(r => r.Core || r.Selected))
            {
                if (caches.Any(c => c.Holds(root.Path)))
                    continue;
                var cache = FindCache(root.Path);
                if (cache == null)
                {
                    (_, cache) = XrScanner.Scan(root.Path, LoadCache(root.Path), null, CancellationToken.None);
                    SaveCache(cache);
                }
                caches.Add(cache);
            }
            return XrIndex.Build(sources, root => caches.Where(c => c.Holds(root)).OrderByDescending(c => c.Folder.Length).Take(1).SelectMany(c => c.List(root)),
                null, CancellationToken.None);
        }).ContinueWith(t =>
        {
            if (generation != _indexGeneration)
                return;
            IndexBuilding = false;
            if (t.Exception is { } ex)
                Toast?.Invoke(Loc.F("Xr.Toast.Failed", Loc.T("Xr.Job.Index"), (ex.InnerException ?? ex).Message), "err");
            else
            {
                Index = t.Result;
                after?.Invoke(t.Result);
            }
            Changed?.Invoke();
        }, ui);
    }

    // The saved scan of the root's folder or of a folder above it (the scan of a whole Downloads holds its mods).
    private static XrScanCache? FindCache(string root)
    {
        for (var dir = new DirectoryInfo(root); dir != null; dir = dir.Parent)
            if (LoadCache(dir.FullName) is { } c && c.Holds(root))
                return c;
        return null;
    }

    // The index's sources are a copy (the worker's); this finds the copy of a saved source.
    public XrSource? IndexSource(string tag) => Index?.Sources.Source(tag);

    public bool Import(IReadOnlyList<XrImportPart> parts, string importFolder, string python, string script, XrAnomalyPlan? plan, Action<IReadOnlyList<string>> opened)
    {
        string stage = Path.Combine(importFolder, ImportRecord.Prefix);
        var (exe, args) = Pfx.Textures.CpuUpscaler.SplitCommand(python);
        var bridge = new XrBridge(exe, args, script);
        var sources = Sources;
        var subst = new Dictionary<string, string>(Substitutes);
        return Run("Xr.Job.Import", (progress, cancel) =>
        {
            var result = bridge.Convert(sources, parts, subst, stage, null,
                new Progress<(int Done, int Total)>(p => progress.Report((p.Done / (double)Math.Max(1, p.Total), Loc.F("Xr.Progress.Parts", p.Done, p.Total)))), cancel);
            var files = new List<string>();
            foreach (var p in result.Particles)
            {
                var record = ImportRecord.From(p, result, plan, parts.First(x => x.Ref == p.Ref && x.State == p.State));
                record.Save(stage);
                files.AddRange(p.Ptc.Select(rel => Path.Combine(stage, p.Tag, rel.Replace('/', '\\'))));
            }
            return (result, files);
        }, r =>
        {
            LastImport = r.result;
            opened(r.files);
        });
    }

    // X-Ray .db archives through the game's converter.exe, into a folder next to the archive (never silently: the
    // import panel asks first and names the target).
    public bool Unpack(string archive, string converter, string target, Action<string> done)
    {
        return Run("Xr.Job.Unpack", (progress, cancel) =>
        {
            Directory.CreateDirectory(target);
            var start = new ProcessStartInfo(converter) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(converter)! };
            foreach (var a in new[] { "-unpack", "-xdb", archive, "-dir", target })
                start.ArgumentList.Add(a);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("converter.exe did not start");
            using (cancel.Register(() =>
                   {
                       try
                       {
                           process.Kill(true);
                       }
                       catch (InvalidOperationException)
                       {
                       }
                   }))
                process.WaitForExit();
            cancel.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"converter.exe exit code {process.ExitCode}");
            return target;
        }, done);
    }
}
