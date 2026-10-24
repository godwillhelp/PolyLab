using System.Text.Json;

namespace Pfx.XRay;

// The acceptance check of P9 against a list xray.py itself wrote (tools/xray/expected.py: its XRAY_SOURCES as absolute
// roots with tags, and the effects its Sources class can find per tag): which expected roots the scans found, with
// which guessed tag, and the effect counts of the index per tag.
public sealed class XrExpected
{
    public sealed record Root(string Tag, string Variant, string Path);

    public required IReadOnlyList<string> Order { get; init; }
    public required IReadOnlyList<Root> Roots { get; init; }
    public required IReadOnlyDictionary<string, (int Effects, int Groups)> Counts { get; init; }
    // xray.py's own output folder (manifest XRAY_STAGE): the files an import must equal byte for byte.
    public string? Stage { get; init; }

    public static XrExpected Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var r = doc.RootElement;
        return new XrExpected
        {
            Order = r.GetProperty("order").EnumerateArray().Select(e => e.GetString()!).ToList(),
            Roots = r.GetProperty("roots").EnumerateArray().Select(e => new Root(e.GetProperty("tag").GetString()!, e.GetProperty("variant").GetString()!,
                System.IO.Path.GetFullPath(e.GetProperty("path").GetString()!))).ToList(),
            Counts = r.GetProperty("counts").EnumerateObject().ToDictionary(p => p.Name,
                p => (p.Value.GetProperty("effects").GetInt32(), p.Value.GetProperty("groups").GetInt32())),
            Stage = r.TryGetProperty("stage", out var st) ? st.GetString() : null,
        };
    }

    public sealed record RootCheck(Root Expected, XrFoundRoot? Found, XrMod? Mod, string? Tag, bool Ok);

    public IReadOnlyList<RootCheck> CheckRoots(IEnumerable<XrScanResult> scans)
    {
        var mods = scans.SelectMany(s => s.Mods).ToList();
        return Roots.Select(e =>
        {
            var mod = mods.FirstOrDefault(m => m.Roots.Any(r => string.Equals(r.Path, e.Path, StringComparison.OrdinalIgnoreCase)));
            var found = mod?.Roots.First(r => string.Equals(r.Path, e.Path, StringComparison.OrdinalIgnoreCase));
            bool core = e.Variant == "core";
            return new RootCheck(e, found, mod, mod?.Guess.Tag, found != null && mod!.Guess.Tag == e.Tag && found.Core == core);
        }).ToList();
    }

    // Sources as the user would add them for this check: each mod that holds an expected root, tagged by the guess, in
    // xray.py's order, with exactly the expected options switched on.
    public XrSourceSet Sources(IEnumerable<XrScanResult> scans)
    {
        var set = new XrSourceSet();
        var wanted = Roots.Select(r => r.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in scans.SelectMany(s => s.Mods).Where(m => m.Roots.Any(r => wanted.Contains(r.Path))).OrderBy(m => Order.ToList().IndexOf(m.Guess.Tag)))
        {
            var s = set.Add(mod, mod.Guess.Tag);
            foreach (var r in s.Roots)
                r.Selected = r.Core || wanted.Contains(r.Path);
        }
        return set;
    }
}
