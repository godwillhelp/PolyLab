using System.Text;

namespace Pfx.XRay;

// A source = one mod (or the base game) with a tag; its roots = the core and the option folders, each on or off. The
// list order is the priority (first wins). Saved by the app in the user's settings folder (no machine path in code,
// O2′); public setters because System.Text.Json reads it.
public sealed class XrSourceRoot
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    // The variant id xray.py gets for this root: "core" for the core, else a name made from the option's folders.
    public string Id { get; set; } = "";
    public bool Core { get; set; }
    public bool Selected { get; set; }
}

public sealed class XrSource
{
    public string Tag { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Known { get; set; }
    public List<XrSourceRoot> Roots { get; set; } = new();

    public XrSourceRoot? CoreRoot => Roots.FirstOrDefault(r => r.Core);

    // Roots in the order xray.py looks into them for one variant: that variant, then the core (Sources.order).
    public IEnumerable<XrSourceRoot> Order(string? variant)
    {
        if (variant != null && Roots.FirstOrDefault(r => r.Id == variant && !r.Core) is { } v)
            yield return v;
        if (CoreRoot is { } c)
            yield return c;
    }
}

public sealed class XrSourceSet
{
    public List<XrSource> Sources { get; set; } = new();

    public XrSource? Source(string tag) => Sources.FirstOrDefault(s => s.Tag == tag);

    // A scanned mod becomes a source (or refreshes the one with the same core folder): the core on, options off.
    public XrSource Add(XrMod mod, string tag)
    {
        var existing = Sources.FirstOrDefault(s => s.CoreRoot is { } c && string.Equals(c.Path, mod.Core.Path, StringComparison.OrdinalIgnoreCase));
        var source = existing ?? new XrSource();
        var kept = source.Roots.ToDictionary(r => r.Path, r => r.Selected, StringComparer.OrdinalIgnoreCase);
        source.Tag = tag;
        source.Name = mod.Name;
        source.Known = mod.Guess.Known;
        source.Roots = new List<XrSourceRoot>();
        var ids = new HashSet<string> { "core" };
        foreach (var r in mod.Roots)
        {
            string id = r.Core ? "core" : Unique(Slug(r.Name), ids);
            source.Roots.Add(new XrSourceRoot { Path = r.Path, Name = r.Name, Id = id, Core = r.Core, Selected = kept.TryGetValue(r.Path, out var on) ? on : r.Core });
        }
        if (existing == null)
            Sources.Add(source);
        return source;
    }

    public static string Slug(string name)
    {
        var sb = new StringBuilder();
        foreach (char c in name.ToLowerInvariant())
            sb.Append(char.IsAsciiLetterOrDigit(c) ? c : '_');
        var s = string.Join('_', sb.ToString().Split('_', StringSplitOptions.RemoveEmptyEntries));
        return s.Length == 0 ? "option" : s;
    }

    private static string Unique(string id, HashSet<string> used)
    {
        string cand = id;
        for (int i = 2; !used.Add(cand); i++)
            cand = $"{id}_{i}";
        return cand;
    }

    // The tag a new source of unknown origin is offered: the guess, else the initials of its name, kept unique.
    public string Propose(XrMod mod)
    {
        string tag = mod.Guess.Tag;
        if (tag.Length == 0)
        {
            var words = mod.Name.Split(new[] { ' ', '_', '-', '.' }, StringSplitOptions.RemoveEmptyEntries).Where(w => char.IsLetter(w[0])).ToList();
            tag = string.Concat(words.Take(2).Select(w => char.ToLowerInvariant(w[0])));
            if (tag.Length < 2)
                tag = "xr";
        }
        var used = Sources.Where(s => s.CoreRoot?.Path is not { } p || !string.Equals(p, mod.Core.Path, StringComparison.OrdinalIgnoreCase)).Select(s => s.Tag).ToHashSet();
        string cand = tag;
        for (int i = 2; used.Contains(cand); i++)
            cand = $"{tag}{i}";
        return cand;
    }

    // Tags are folder names inside the import folder and keys of the xray.py request: lower-case letters and digits.
    public static bool ValidTag(string tag) => tag.Length is >= 1 and <= 12 && tag.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c));

    // The "sources" table of an xray.py request: every source with all its roots, in priority order. xray.py looks into
    // the asked variant, its source's core, then the other sources' cores in this order (Sources.order), so options
    // that are switched off cost nothing there.
    public Dictionary<string, Dictionary<string, string>> Request()
    {
        var table = new Dictionary<string, Dictionary<string, string>>();
        foreach (var s in Sources)
        {
            var roots = new Dictionary<string, string>();
            foreach (var r in s.Roots)
                roots[r.Id] = r.Path.Replace('\\', '/');
            if (roots.ContainsKey("core"))
                table[s.Tag] = roots;
        }
        return table;
    }
}
