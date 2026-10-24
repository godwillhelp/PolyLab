using Pfx.Sim.XRay;

namespace Pfx.XRay;

public enum XrEntryKind { Effect, Group, Texture, Sound, Lanim, Zone, Item }

// One place an entry comes from: a root of a source, as a loose file or as an entry of that root's particles.xr.
public sealed record XrProvider(XrSource Source, XrSourceRoot Root, string File, bool Library);

public sealed class XrEntry
{
    public required XrEntryKind Kind { get; init; }
    // Game path ("particles/anomaly2/x.pe", "textures/x/y.dds", "sounds/a/b.ogg"), a light animation name, or a section name.
    public required string Key { get; init; }
    public required string Name { get; init; }
    // Where it is, in priority order: the first one wins (see XrIndex).
    public List<XrProvider> Providers { get; } = new();
    public string? Texture { get; set; }
    public string? Shader { get; set; }
    public HashSet<string> Types { get; } = new();
    public XrProvider Winner => Providers[0];
}

// Everything one root holds, as xray.py's Sources sees it: loose files by game path, its particle library, its light
// animations and its config files.
public sealed class XrRootData
{
    public required XrSource Source { get; init; }
    public required XrSourceRoot Root { get; init; }
    public Dictionary<string, (string File, XrKind Kind)> Loose { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? LibraryFile { get; set; }
    public Dictionary<string, Dictionary<string, Dictionary<string, string>>> Library { get; set; } = new();
    public List<XrLightAnim> Lanims { get; } = new();
    public Dictionary<string, LtxFile> Configs { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Problems { get; } = new();
}

// Words that name an anomaly type in effect, group, texture and section names (X-Ray mods use Russian slang in Latin
// letters and English words). Used for "search by type"; the UI words the ids.
public static class XrTypes
{
    public static readonly IReadOnlyDictionary<string, string[]> Words = new Dictionary<string, string[]>
    {
        ["electra"] = new[] { "electra", "electric", "elektra", "tesla", "shock", "lightning", "thunder", "electro" },
        ["fire"] = new[] { "zharka", "thermal", "fire", "burn", "flame", "campfire", "heat", "fireball" },
        ["gravity"] = new[] { "gravi", "springboard", "tramplin", "voronka", "vortex", "funnel", "mincer", "myasorubka", "karusel", "whirligig", "nogravity" },
        ["acid"] = new[] { "acid", "kisel", "chemical", "buzz", "toxic" },
        ["teleport"] = new[] { "teleport", "spatial" },
        ["radiation"] = new[] { "radioactive", "radiation" },
        ["psi"] = new[] { "psi", "psychic", "monolith" },
        ["generator"] = new[] { "generator", "generatory" },
        ["tornado"] = new[] { "tornado" },
    };

    public static IEnumerable<string> Of(string name)
    {
        string n = name.ToLowerInvariant();
        foreach (var (type, words) in Words)
            if (words.Any(w => n.Contains(w, StringComparison.Ordinal)))
                yield return type;
    }
}

public sealed class XrIndex
{
    private readonly Dictionary<(XrEntryKind, string), XrEntry> _entries = new();

    private XrIndex(XrSourceSet sources, List<XrRootData> roots)
    {
        Sources = sources;
        Roots = roots;
    }

    public XrSourceSet Sources { get; }
    public IReadOnlyList<XrRootData> Roots { get; }
    public IEnumerable<XrEntry> Entries => _entries.Values;
    public XrEntry? Entry(XrEntryKind kind, string key) => _entries.GetValueOrDefault((kind, key.ToLowerInvariant()));

    // Reads every root of every source (selected options and cores; switched-off options are left out) from the scan
    // listing: the kinds the scan found, so nothing is guessed from extensions. Loose effects, groups, libraries,
    // light animations and configs are parsed; textures and sounds are listed by path.
    public static XrIndex Build(XrSourceSet sources, Func<string, IEnumerable<(string Game, XrKind Kind, string File)>> listing,
        IProgress<int>? progress, CancellationToken cancel)
    {
        var data = new List<XrRootData>();
        foreach (var s in sources.Sources)
            foreach (var r in s.Roots.Where(r => r.Core || r.Selected))
            {
                cancel.ThrowIfCancellationRequested();
                var d = new XrRootData { Source = s, Root = r };
                foreach (var (game, kind, file) in listing(r.Path))
                {
                    d.Loose[game] = (file, kind);
                    try
                    {
                        if (kind == XrKind.Library && game == "particles.xr")
                        {
                            d.LibraryFile = file;
                            d.Library = XrLibrary.Read(File.ReadAllBytes(file));
                        }
                        else if (kind == XrKind.Lanims)
                        {
                            d.Lanims.AddRange(XrLanims.Read(File.ReadAllBytes(file)));
                        }
                        else if (kind == XrKind.Config && game.StartsWith("configs/", StringComparison.Ordinal))
                        {
                            d.Configs[game] = XrLtx.Read(File.ReadAllBytes(file), file);
                        }
                    }
                    catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or KeyNotFoundException or ArgumentException)
                    {
                        d.Problems.Add($"{file}: {e.Message}");
                    }
                }
                data.Add(d);
                progress?.Report(data.Count);
            }
        var index = new XrIndex(sources, data);
        index.Fill(cancel);
        return index;
    }

    // Provider order: sources by priority; inside a source its selected options before its core (an option replaces
    // the core's files, as xray.py's order(tag, variant) puts the variant first); inside a root the loose file before
    // the root's library entry.
    private IEnumerable<XrRootData> InOrder() =>
        Sources.Sources.SelectMany(s => Roots.Where(d => d.Source == s).OrderBy(d => d.Root.Core ? 1 : 0).ThenBy(d => s.Roots.IndexOf(d.Root)));

    private XrEntry Get(XrEntryKind kind, string key, string name)
    {
        var k = (kind, key.ToLowerInvariant());
        if (!_entries.TryGetValue(k, out var e))
            _entries[k] = e = new XrEntry { Kind = kind, Key = k.Item2, Name = name };
        return e;
    }

    private void Fill(CancellationToken cancel)
    {
        foreach (var d in InOrder())
        {
            cancel.ThrowIfCancellationRequested();
            foreach (var (game, (file, kind)) in d.Loose)
            {
                if (kind is XrKind.Effect or XrKind.Group && game.StartsWith("particles/", StringComparison.Ordinal) && game.EndsWith(kind == XrKind.Effect ? ".pe" : ".pg", StringComparison.Ordinal))
                    AddParticle(d, game, file, false, null);
                else if (kind == XrKind.Texture && game.StartsWith("textures/", StringComparison.Ordinal))
                    Add(XrEntryKind.Texture, d, game, file, StripExt(game["textures/".Length..]));
                else if (kind == XrKind.Sound && game.StartsWith("sounds/", StringComparison.Ordinal))
                    Add(XrEntryKind.Sound, d, game, file, StripExt(game["sounds/".Length..]));
            }
            foreach (var (game, s) in d.Library)
                AddParticle(d, game, d.LibraryFile + "/" + game["particles/".Length..], true, s);
            foreach (var a in d.Lanims)
                Add(XrEntryKind.Lanim, d, a.Name, d.Loose.First(l => l.Value.Kind == XrKind.Lanims).Value.File, a.Name);
            foreach (var (_, cfg) in d.Configs)
                foreach (var sec in cfg.Sections)
                {
                    if (XrAnomalyPlanner.SetsParticles(sec))
                        Add(XrEntryKind.Zone, d, sec.Name, cfg.Path, sec.Name);
                    if (sec.Keys.ContainsKey("inv_name"))
                        Add(XrEntryKind.Item, d, sec.Name, cfg.Path, sec.Name);
                }
        }
    }

    private XrEntry Add(XrEntryKind kind, XrRootData d, string key, string file, string name)
    {
        var e = Get(kind, key, name);
        if (!e.Providers.Any(p => p.Root == d.Root && p.Library == false && p.File == file))
            e.Providers.Add(new XrProvider(d.Source, d.Root, file, false));
        foreach (var t in XrTypes.Of(name))
            e.Types.Add(t);
        return e;
    }

    private void AddParticle(XrRootData d, string game, string file, bool library, Dictionary<string, Dictionary<string, string>>? sections)
    {
        bool group = game.EndsWith(".pg", StringComparison.Ordinal);
        string name = StripExt(game["particles/".Length..]);
        var e = Get(group ? XrEntryKind.Group : XrEntryKind.Effect, game, name);
        e.Providers.Add(new XrProvider(d.Source, d.Root, file, library));
        foreach (var t in XrTypes.Of(name))
            e.Types.Add(t);
        if (e.Providers.Count > 1 || group)
            return;
        var secs = sections ?? Sections(file);
        var sprite = secs?.GetValueOrDefault("sprite");
        e.Shader = sprite?.GetValueOrDefault("shader")?.Replace('\\', '/').ToLowerInvariant();
        e.Texture = sprite?.GetValueOrDefault("texture")?.Split(',')[0].Trim().Replace('\\', '/').ToLowerInvariant();
        if (e.Texture != null)
            e.Texture = StripExt(e.Texture);
    }

    private static Dictionary<string, Dictionary<string, string>>? Sections(string file)
    {
        try
        {
            return XrText.ReadIni(File.ReadAllBytes(file));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string StripExt(string p)
    {
        int slash = p.LastIndexOf('/'), dot = p.LastIndexOf('.');
        return dot > slash ? p[..dot] : p;
    }

    // The root order xray.py uses for one particle of a source and variant: that variant, the source's core, then the
    // other sources' cores in priority order (Sources.order).
    public IEnumerable<XrRootData> Order(XrSource source, string? variant)
    {
        var order = source.Order(variant).Select(r => (source, r)).ToList();
        foreach (var s in Sources.Sources.Where(s => s != source))
            if (s.CoreRoot is { } c)
                order.Add((s, c));
        foreach (var (s, r) in order)
            if (Roots.FirstOrDefault(d => d.Root == r) is { } d)
                yield return d;
    }

    // A particle (game path) as xray.py Sources.particle finds it: per root the loose file, then that root's library.
    public (string File, Dictionary<string, Dictionary<string, string>> Sections, XrRootData Root, bool Library)? Particle(XrSource source, string? variant, string game)
    {
        game = game.Replace('\\', '/').ToLowerInvariant();
        foreach (var d in Order(source, variant))
        {
            if (d.Loose.TryGetValue(game, out var f) && Sections(f.File) is { } secs)
                return (f.File, secs, d, false);
            if (d.Library.TryGetValue(game, out var lib))
                return (d.LibraryFile + "/" + game["particles/".Length..], lib, d, true);
        }
        return null;
    }

    // A loose file (texture, sound) as xray.py Sources.find does: the first root in the order that has it.
    public (string File, XrRootData Root)? Find(XrSource source, string? variant, string game)
    {
        game = game.Replace('\\', '/').ToLowerInvariant();
        foreach (var d in Order(source, variant))
            if (d.Loose.TryGetValue(game, out var f))
                return (f.File, d);
        return null;
    }

    public XrEffect? Effect(XrSource source, string? variant, string name)
    {
        var p = Particle(source, variant, "particles/" + name + ".pe");
        return p is { } x ? XrEffect.Parse(x.Sections, x.File) : null;
    }

    public XrGroup? Group(XrSource source, string? variant, string name)
    {
        var p = Particle(source, variant, "particles/" + name + ".pg");
        return p is { } x ? XrGroup.Parse(x.Sections) : null;
    }

    // Configs as the game of this source and variant would see them: per game path the file of the first root in the
    // order wins; sections of all winning files merged (higher priority added last, so it wins).
    public LtxConfig Config(XrSource source, string? variant)
    {
        var order = Order(source, variant).ToList();
        var winners = new Dictionary<string, LtxFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in order)
            foreach (var (game, file) in d.Configs)
                winners.TryAdd(game, file);
        var cfg = new LtxConfig();
        for (int i = order.Count - 1; i >= 0; i--)
            foreach (var (game, file) in order[i].Configs.OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase))
                if (winners[game] == file)
                    cfg.Add(file);
        return cfg;
    }

    // Distinct effects and groups of each source's roots in this index, by path as xray.py sees them (any loose file
    // named .pe / .pg under particles\, and library entries) — what xray.py can find for that tag.
    public Dictionary<string, (int Effects, int Groups)> CountsPerTag()
    {
        var out_ = new Dictionary<string, (int, int)>();
        foreach (var s in Sources.Sources)
        {
            var pe = new HashSet<string>();
            var pg = new HashSet<string>();
            foreach (var d in Roots.Where(d => d.Source == s))
            {
                foreach (var game in d.Loose.Keys.Concat(d.Library.Keys))
                {
                    if (!game.StartsWith("particles/", StringComparison.Ordinal))
                        continue;
                    if (game.EndsWith(".pe", StringComparison.Ordinal))
                        pe.Add(game);
                    else if (game.EndsWith(".pg", StringComparison.Ordinal))
                        pg.Add(game);
                }
            }
            out_[s.Tag] = (pe.Count, pg.Count);
        }
        return out_;
    }

    // Search by name (any part), by texture ("texture" field contains the text) and by anomaly type.
    public IEnumerable<XrEntry> Search(string text, IReadOnlySet<XrEntryKind>? kinds, string? type)
    {
        string q = text.Trim().Replace('\\', '/').ToLowerInvariant();
        return Entries.Where(e => (kinds == null || kinds.Contains(e.Kind)) && (type == null || e.Types.Contains(type))
                                  && (q.Length == 0 || e.Key.Contains(q, StringComparison.Ordinal) || (e.Texture?.Contains(q, StringComparison.Ordinal) ?? false)))
            .OrderBy(e => e.Kind).ThenBy(e => e.Key, StringComparer.Ordinal);
    }
}
