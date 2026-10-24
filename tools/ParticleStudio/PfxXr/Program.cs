using System.Diagnostics;
using System.Text.Json;
using Pfx.XRay;

namespace PfxXr;

// Headless X-Ray import runs for P9: folder scans (with a cache file for the incremental re-scan), the acceptance check
// against xray.py's own list (tools/ParticleStudio/xray/expected.py) and anomaly plans from zone sections.
public static class Program
{
    private const string Usage = """
        pfxxr scan <folder> [--cache <file>]                         kinds, mods, roots, skipped, time
        pfxxr accept <expected.json> <folder>... [--cache-dir <dir>]  roots and counts per tag vs xray.py
        pfxxr plan <expected.json> <section> <tag> [<variant>] <folder>... [--cache-dir <dir>]   everything for a zone
        """;

    public static int Main(string[] args)
    {
        try
        {
            switch (args.FirstOrDefault())
            {
                case "scan" when args.Length >= 2:
                    ScanOne(args[1], Opt(args, "--cache"), true);
                    return 0;
                case "accept" when args.Length >= 3:
                    return Accept(args[1], Folders(args, 2), Opt(args, "--cache-dir"));
                case "plan" when args.Length >= 5:
                    return Plan(args, Opt(args, "--cache-dir"));
                default:
                    Console.Error.WriteLine(Usage);
                    return 2;
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }

    private static string? Opt(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static List<string> Folders(string[] args, int from)
    {
        var list = new List<string>();
        for (int i = from; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                i++;
                continue;
            }
            list.Add(args[i]);
        }
        return list;
    }

    private static string CacheFile(string? dir, string folder) =>
        Path.Combine(dir ?? Path.GetTempPath(), "pfxxr_" + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(folder).ToLowerInvariant())))[..12] + ".json");

    private static (XrScanResult, XrScanCache) ScanOne(string folder, string? cacheFile, bool print)
    {
        XrScanCache? old = null;
        if (cacheFile != null && File.Exists(cacheFile))
            old = JsonSerializer.Deserialize<XrScanCache>(File.ReadAllBytes(cacheFile));
        var (result, cache) = XrScanner.Scan(folder, old, null, CancellationToken.None);
        if (cacheFile != null)
            File.WriteAllBytes(cacheFile, JsonSerializer.SerializeToUtf8Bytes(cache));
        if (print)
            Print(result);
        return (result, cache);
    }

    private static void Print(XrScanResult r)
    {
        Console.WriteLine($"{r.Folder}: {r.Files} files, read {r.Read}, from cache {r.Reused}, same content {r.SameContent}, {r.Took.TotalSeconds:F1} s");
        Console.WriteLine("kinds: " + string.Join(", ", r.Kinds.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value}")) + $"; sounds with X-Ray parameters {r.SoundsWithParams}");
        foreach (var m in r.Mods)
        {
            Console.WriteLine($"mod {m.Name}: tag '{m.Guess.Tag}' ({m.Guess.Known ?? "unknown"}{(m.Guess.Marker != null ? ": " + m.Guess.Marker : "")}), {m.Roots.Count} roots");
            foreach (var root in m.Roots)
                Console.WriteLine($"  {(root.Core ? "core  " : "option")} {root.Name}: effects {root.Effects}, files {root.Files}, over core {root.OverridesCore}");
        }
        foreach (var (why, (n, ex)) in r.Skipped.OrderByDescending(s => s.Value.Count))
            Console.WriteLine($"skipped {why}: {n} (e.g. {string.Join("; ", ex.Take(2))})");
        foreach (var a in r.Archives)
            Console.WriteLine($"archive to unpack: {a}");
        if (r.Nothing)
            Console.WriteLine("nothing usable");
    }

    private static (List<XrScanResult>, List<XrScanCache>) ScanAll(IEnumerable<string> folders, string? cacheDir)
    {
        var results = new List<XrScanResult>();
        var caches = new List<XrScanCache>();
        foreach (var f in folders)
        {
            var (r, c) = ScanOne(f, CacheFile(cacheDir, f), false);
            Console.WriteLine($"scan {r.Folder}: {r.Files} files, read {r.Read}, from cache {r.Reused}, {r.Took.TotalSeconds:F1} s, {r.Mods.Count} mods, {r.Roots.Count()} roots");
            results.Add(r);
            caches.Add(c);
        }
        return (results, caches);
    }

    private static XrIndex Index(XrSourceSet set, List<XrScanCache> caches)
    {
        var clock = Stopwatch.StartNew();
        var index = XrIndex.Build(set, root => caches.Where(c => c.Holds(root)).OrderByDescending(c => c.Folder.Length).Take(1).SelectMany(c => c.List(root)), null, CancellationToken.None);
        Console.WriteLine($"index: {index.Entries.Count()} entries, {clock.Elapsed.TotalSeconds:F1} s; " +
                          string.Join(", ", index.Entries.GroupBy(e => e.Kind).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}")));
        foreach (var d in index.Roots.Where(d => d.Problems.Count > 0))
            Console.WriteLine($"  problems in {d.Root.Path}: {string.Join("; ", d.Problems.Take(3))}");
        return index;
    }

    private static int Accept(string expectedFile, List<string> folders, string? cacheDir)
    {
        var expected = XrExpected.Load(expectedFile);
        var (scans, caches) = ScanAll(folders, cacheDir);
        int bad = 0;
        foreach (var c in expected.CheckRoots(scans))
        {
            Console.WriteLine($"{(c.Ok ? "ok  " : "FAIL")} {c.Expected.Tag}:{c.Expected.Variant} {c.Expected.Path} -> " +
                              (c.Found == null ? "not found" : $"found as {(c.Found.Core ? "core" : "option")} '{c.Found.Name}' of {c.Mod!.Name}, tag '{c.Tag}'"));
            bad += c.Ok ? 0 : 1;
        }
        var set = expected.Sources(scans);
        var index = Index(set, caches);
        var counts = index.CountsPerTag();
        foreach (var (tag, (pe, pg)) in expected.Counts)
        {
            var have = counts.GetValueOrDefault(tag);
            bool ok = have == (pe, pg);
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")} counts {tag}: xray.py {pe} effects {pg} groups, index {have.Effects} / {have.Groups}");
            bad += ok ? 0 : 1;
        }
        var entries = index.Entries.Where(e => e.Kind is XrEntryKind.Effect or XrEntryKind.Group).GroupBy(e => e.Winner.Source.Tag)
            .Select(g => $"{g.Key} {g.Count()}");
        Console.WriteLine("recognised effects + groups by winning tag: " + string.Join(", ", entries));
        Console.WriteLine(bad == 0 ? "ACCEPTED" : $"{bad} failures");
        return bad == 0 ? 0 : 1;
    }

    private static int Plan(string[] args, string? cacheDir)
    {
        var expected = XrExpected.Load(args[1]);
        string section = args[2], tag = args[3];
        string? variant = args.Length > 4 && !Directory.Exists(args[4]) ? args[4] : null;
        var (scans, caches) = ScanAll(Folders(args, variant != null ? 5 : 4), cacheDir);
        var set = expected.Sources(scans);
        var index = Index(set, caches);
        var plan = XrAnomalyPlanner.Plan(index, set.Source(tag)!, variant, section, new Dictionary<string, string>());
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            plan.Section, plan.Type, plan.Postprocess, plan.Timings, plan.Hit, plan.MissingParents,
            Particles = plan.Particles.Select(p => new { p.Role, p.Ref, p.File, p.Group, p.Missing, Effects = p.Effects.Select(e => new { e.Name, e.Shader, e.ChildOf, e.ChildKind, Texture = e.Texture?.Name, Found = e.Texture?.File, e.Texture?.Proposal }) }),
            Sounds = plan.Sounds.Select(s => new { s.Role, s.Ref, s.File, s.Params }),
            Lights = plan.Lights.Select(l => new { l.Role, l.Keys, Anim = l.Anim?.Name, Mean = l.Anim?.Mean() }),
        }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
