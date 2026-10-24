using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;

namespace Pfx.XRay;

// What a scan remembers per file (relative path of the scanned folder): a later scan that finds the same size and
// write time takes the kind and facts from here without opening the file; a changed file is read again and its hash
// says whether the content really changed.
public sealed class XrCachedFile
{
    public long Size { get; set; }
    public long Mtime { get; set; }
    public string Hash { get; set; } = "";
    // The hash covers the whole file (text kinds and libraries) or only the head (textures, sounds and the rest, the
    // bulk of the bytes: their full hash is taken when an import uses them).
    public bool WholeHash { get; set; }
    public XrKind Kind { get; set; }
    public XrFacts? Facts { get; set; }
    public string? Why { get; set; }
}

public sealed class XrScanCache
{
    public string Folder { get; set; } = "";
    public Dictionary<string, XrCachedFile> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool Holds(string root)
    {
        string f = System.IO.Path.GetFullPath(Folder).TrimEnd('\\') + "\\";
        string r = System.IO.Path.GetFullPath(root).TrimEnd('\\') + "\\";
        return r.StartsWith(f, StringComparison.OrdinalIgnoreCase);
    }

    // Every file under a root this scan saw, as (game path, kind, full path) — the index's listing.
    public IEnumerable<(string Game, XrKind Kind, string File)> List(string root)
    {
        string full = System.IO.Path.GetFullPath(root).TrimEnd('\\');
        string prefix = System.IO.Path.GetRelativePath(Folder, full);
        prefix = prefix == "." ? "" : prefix.TrimEnd('\\') + "\\";
        foreach (var (rel, f) in Files)
            if (rel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                yield return (rel[prefix.Length..].Replace('\\', '/').ToLowerInvariant(), f.Kind, System.IO.Path.Combine(Folder, rel));
    }
}

public sealed record XrScanProgress(int Done, int Total, int Read);

// A gamedata root found by the scan (the folder whose particles\, textures\, sounds\, configs\ ... hold the content).
public sealed class XrFoundRoot
{
    public required string Path { get; init; }
    // The root's folder relative to its mod folder, without the trailing "gamedata" (the option's name in the mod).
    public required string Name { get; init; }
    public required string Mod { get; init; }
    public Dictionary<XrKind, int> Counts { get; } = new();
    public int Effects { get; set; }
    public int Files { get; set; }
    public bool Core { get; set; }
    // Files this root shares (same game path) with the core, and with each other option of its mod.
    public int OverridesCore { get; set; }
    public Dictionary<string, int> Overlaps { get; } = new();
    public HashSet<string> Paths { get; } = new(StringComparer.OrdinalIgnoreCase);
}

// Which known mod a set of roots looks like, and the file that says so (Known: arrival, dao, cv, anomaly, or null).
public sealed record XrTagGuess(string Tag, string? Known, string? Marker);

public sealed class XrMod
{
    public required string Name { get; init; }
    public required string Folder { get; init; }
    public required XrTagGuess Guess { get; init; }
    public List<XrFoundRoot> Roots { get; } = new();
    public XrFoundRoot Core => Roots[0];
}

public sealed class XrScanResult
{
    public required string Folder { get; init; }
    public List<XrMod> Mods { get; } = new();
    public Dictionary<XrKind, int> Kinds { get; } = new();
    // X-Ray sounds with their parameters in the Vorbis comment, of all X-Ray sounds found.
    public int SoundsWithParams { get; set; }
    // Skip reason code -> file count and up to SkipExamples paths.
    public Dictionary<string, (int Count, List<string> Examples)> Skipped { get; } = new();
    public List<string> Archives { get; } = new();
    public int Files { get; set; }
    public int Read { get; set; }
    public int Reused { get; set; }
    public int SameContent { get; set; }
    public TimeSpan Took { get; set; }
    public bool Nothing => Mods.Count == 0 && Archives.Count == 0;
    public IEnumerable<XrFoundRoot> Roots => Mods.SelectMany(m => m.Roots);
}

public static class XrScanner
{
    // The folders the engine maps under gamedata (fsgame.ltx of Anomaly 1.5.3: $game_ai$ ... $game_scripts$).
    public static readonly IReadOnlySet<string> TopFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "ai", "spawns", "levels", "meshes", "anims", "shaders", "particles", "sounds", "textures", "configs", "addons", "scripts" };
    public const int SkipExamples = 5;

    public static (XrScanResult Result, XrScanCache Cache) Scan(string folder, XrScanCache? old, IProgress<XrScanProgress>? progress, CancellationToken cancel)
    {
        var clock = Stopwatch.StartNew();
        folder = System.IO.Path.GetFullPath(folder).TrimEnd('\\', '/');
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System };
        var files = Directory.EnumerateFiles(folder, "*", options).ToList();
        var prior = old != null && string.Equals(old.Folder, folder, StringComparison.OrdinalIgnoreCase) ? old.Files : new Dictionary<string, XrCachedFile>();
        var cache = new XrScanCache { Folder = folder };
        var found = new ConcurrentDictionary<string, XrCachedFile>(StringComparer.OrdinalIgnoreCase);
        int done = 0, read = 0, same = 0;
        var parallel = new ParallelOptions { CancellationToken = cancel, MaxDegreeOfParallelism = Environment.ProcessorCount };
        Parallel.ForEach(files, parallel, path =>
        {
            string rel = System.IO.Path.GetRelativePath(folder, path);
            var info = new FileInfo(path);
            long mtime = info.LastWriteTimeUtc.Ticks;
            if (prior.TryGetValue(rel, out var hit) && hit.Size == info.Length && hit.Mtime == mtime)
            {
                found[rel] = hit;
            }
            else
            {
                var entry = Read(path, info.Length, mtime);
                if (hit != null && hit.Hash == entry.Hash && hit.Size == entry.Size)
                    Interlocked.Increment(ref same);
                found[rel] = entry;
                Interlocked.Increment(ref read);
            }
            int n = Interlocked.Increment(ref done);
            if ((n & 255) == 0)
                progress?.Report(new XrScanProgress(n, files.Count, read));
        });
        foreach (var (k, v) in found)
            cache.Files[k] = v;
        var result = Build(folder, cache);
        result.Files = files.Count;
        result.Read = read;
        result.Reused = files.Count - read;
        result.SameContent = same;
        result.Took = clock.Elapsed;
        progress?.Report(new XrScanProgress(files.Count, files.Count, read));
        return (result, cache);
    }

    private static XrCachedFile Read(string path, long size, long mtime)
    {
        try
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
            var head = new byte[(int)Math.Min(size, XrRecognize.HeadBytes)];
            f.ReadExactly(head);
            var (kind, facts, why) = XrRecognize.Recognize(head, size);
            bool whole = kind is XrKind.Effect or XrKind.Group or XrKind.Config or XrKind.Library or XrKind.Lanims;
            string hash;
            if (whole && size > head.Length)
            {
                f.Position = 0;
                hash = Convert.ToHexString(SHA256.HashData(f));
            }
            else
            {
                hash = Convert.ToHexString(SHA256.HashData(head));
            }
            return new XrCachedFile { Size = size, Mtime = mtime, Hash = hash, WholeHash = whole || size <= head.Length, Kind = kind, Facts = facts, Why = why };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new XrCachedFile { Size = size, Mtime = mtime, Kind = XrKind.Other, Why = "unreadable" };
        }
    }

    // Roots, mods and the skipped list from the per-file kinds (no file is opened here).
    public static XrScanResult Build(string folder, XrScanCache cache)
    {
        var result = new XrScanResult { Folder = folder };
        var roots = new Dictionary<string, XrFoundRoot>(StringComparer.OrdinalIgnoreCase);
        void Skip(string why, string rel)
        {
            var (n, ex) = result.Skipped.TryGetValue(why, out var s) ? s : (0, new List<string>());
            if (ex.Count < SkipExamples)
                ex.Add(rel);
            result.Skipped[why] = (n + 1, ex);
        }
        foreach (var (rel, f) in cache.Files.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (f.Kind == XrKind.Archive)
            {
                result.Archives.Add(System.IO.Path.Combine(folder, rel));
                continue;
            }
            if (!XrKinds.IsContent(f.Kind))
            {
                Skip(f.Why ?? "text", rel);
                continue;
            }
            var segs = rel.Split('\\', '/');
            int at = RootDepth(segs, f.Kind);
            if (at < 0)
            {
                Skip("outside", rel);
                continue;
            }
            string rootRel = string.Join('\\', segs.Take(at));
            string rootPath = at == 0 ? folder : System.IO.Path.Combine(folder, rootRel);
            if (!roots.TryGetValue(rootPath, out var root))
                roots[rootPath] = root = NewRoot(folder, rootPath, segs, at);
            string game = string.Join('/', segs.Skip(at)).ToLowerInvariant();
            root.Paths.Add(game);
            root.Files++;
            root.Counts[f.Kind] = root.Counts.GetValueOrDefault(f.Kind) + 1;
            result.Kinds[f.Kind] = result.Kinds.GetValueOrDefault(f.Kind) + 1;
            if (f.Kind == XrKind.Sound && f.Facts?.Sound != null)
                result.SoundsWithParams++;
            if (f.Kind is XrKind.Effect or XrKind.Group && game.StartsWith("particles/", StringComparison.Ordinal))
                root.Effects++;
        }
        foreach (var group in roots.Values.GroupBy(r => r.Mod, StringComparer.OrdinalIgnoreCase))
        {
            // The core: the root with a particle library, else the most loose effects, else the most files.
            var list = group.OrderByDescending(r => r.Counts.ContainsKey(XrKind.Library)).ThenByDescending(r => r.Effects).ThenByDescending(r => r.Files)
                .ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase).ToList();
            list[0].Core = true;
            foreach (var r in list.Skip(1))
            {
                r.OverridesCore = r.Paths.Count(list[0].Paths.Contains);
                foreach (var other in list.Skip(1).Where(o => o != r))
                    if (r.Paths.Count(other.Paths.Contains) is int n and > 0)
                        r.Overlaps[other.Name] = n;
            }
            var core = list[0];
            var others = list.Skip(1).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase);
            string modFolder = group.Key.Length == 0 ? folder : System.IO.Path.Combine(folder, group.Key);
            var mod = new XrMod { Name = group.Key.Length == 0 ? ModName(folder) : group.Key, Folder = modFolder, Guess = Guess(list) };
            mod.Roots.Add(core);
            mod.Roots.AddRange(others);
            result.Mods.Add(mod);
        }
        return result;
    }

    // Where the root of a content file sits in its path: before the deepest "gamedata" whose next folder is one the
    // engine maps, else before the first such folder; a library or light animation file names its folder directly.
    private static int RootDepth(string[] segs, XrKind kind)
    {
        int n = segs.Length;
        if (kind is XrKind.Library or XrKind.Lanims)
            return n - 1;
        for (int i = n - 2; i >= 1; i--)
            if (segs[i - 1].Equals("gamedata", StringComparison.OrdinalIgnoreCase) && TopFolders.Contains(segs[i]))
                return i;
        for (int i = 0; i < n - 1; i++)
            if (TopFolders.Contains(segs[i]))
                return i;
        return -1;
    }

    private static XrFoundRoot NewRoot(string folder, string rootPath, string[] segs, int at)
    {
        string mod = at == 0 ? "" : segs[0];
        var inside = segs.Take(at).Skip(at == 0 ? 0 : 1).ToList();
        if (inside.Count > 0 && inside[^1].Equals("gamedata", StringComparison.OrdinalIgnoreCase))
            inside.RemoveAt(inside.Count - 1);
        string name = inside.Count > 0 ? string.Join('\\', inside) : ModName(rootPath);
        return new XrFoundRoot { Path = rootPath, Name = name, Mod = mod };
    }

    private static string ModName(string path)
    {
        var dir = new DirectoryInfo(path);
        return dir.Name.Equals("gamedata", StringComparison.OrdinalIgnoreCase) && dir.Parent != null ? dir.Parent.Name : dir.Name;
    }

    // Known mods by a folder or set of files only they ship (public mods of STALKER Anomaly / GAMMA, not paths of a PC).
    private static readonly (string Known, string Tag, Func<XrFoundRoot, string?> Marker)[] Signatures =
    {
        ("arrival", "ar", r => r.Paths.FirstOrDefault(p => p.StartsWith("particles/semitone/anomalies/", StringComparison.Ordinal))),
        ("dao", "da", r => r.Paths.FirstOrDefault(p => p.StartsWith("particles/eugenium_anomalies/", StringComparison.Ordinal) || p.StartsWith("scripts/drx_da_", StringComparison.Ordinal))),
        ("anomaly", "an", r => r.Paths.Contains("particles.xr") && r.Paths.Contains("configs/system.ltx") ? "configs/system.ltx" : null),
        ("cv", "cv", r => r.Paths.Contains("particles.xr") ? r.Paths.FirstOrDefault(p => p.StartsWith("particles/crommcruac/", StringComparison.Ordinal) || p.StartsWith("particles/_samples_particles_/", StringComparison.Ordinal)) : null),
    };

    public static XrTagGuess Guess(IReadOnlyList<XrFoundRoot> roots)
    {
        foreach (var (known, tag, marker) in Signatures)
            foreach (var r in roots)
                if (marker(r) is { } m)
                    return new XrTagGuess(tag, known, m);
        return new XrTagGuess("", null, null);
    }
}
