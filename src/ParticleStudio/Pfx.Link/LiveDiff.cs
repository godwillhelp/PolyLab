using Pfx.Core.Model;
using Pfx.Core.Schema;
using Pfx.Core.Text;

namespace Pfx.Link;

// A key of one emitter set on the live source: the emitter by name (the game numbers emitters by its own list), the
// parameter and its "set" arguments.
public sealed record ParamChange(string Emitter, string Key, GameParam Param, string Arguments);

// What it takes to bring a part in the game from the text it was spawned from (live) to the text the user has now:
// nothing, a list of parameter sets, or a reload of the file. Only keys of GameParams on emitters that exist in both,
// with the same names in the same order, go as sets; anything else (effect keys, graphs, materials, shapes, added or
// renamed emitters, a key written twice) reloads. Whitespace and number spelling do not count as changes when the
// tokens are equal; "1" and "1.0" do count (the file text changed, but they set the same value).
public sealed class LiveDiff
{
    public bool Reload { get; private init; }
    public IReadOnlyList<ParamChange> Changes { get; private init; } = Array.Empty<ParamChange>();
    public bool Same => !Reload && Changes.Count == 0;

    private static readonly LiveDiff ReloadAll = new() { Reload = true };

    public static LiveDiff Compare(PtcDocument live, PtcDocument now)
    {
        if (!SameItems(Outside(live.Block), Outside(now.Block)))
            return ReloadAll;
        var liveEmitors = live.EmitorsBlock;
        var nowEmitors = now.EmitorsBlock;
        if ((liveEmitors == null) != (nowEmitors == null))
            return ReloadAll;
        if (liveEmitors != null && !SameItems(NotEmitters(liveEmitors), NotEmitters(nowEmitors!)))
            return ReloadAll;
        var a = live.Emitters;
        var b = now.Emitters;
        if (a.Count != b.Count)
            return ReloadAll;
        var changes = new List<ParamChange>();
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].Name != b[i].Name || a[i].Name.Length == 0)
                return ReloadAll;
            if (!CompareEmitter(a[i], b[i], now.Schema, changes))
                return ReloadAll;
        }
        return new LiveDiff { Changes = changes };
    }

    // One key of one emitter as a set, whatever the live text holds (the field menu's "push to the game now").
    public static ParamChange? Single(EmitterView emitter, string key)
    {
        var p = GameParams.OfKey(key);
        if (p is not { Writable: true } || ValuesOf(emitter, key) is not { } values || GameParams.Arguments(p, values) is not { } args)
            return null;
        return new ParamChange(emitter.Name, p.Key!, p, args);
    }

    private static bool CompareEmitter(EmitterView live, EmitterView now, PfxSchema schema, List<ParamChange> changes)
    {
        var la = Signatures(live.Block);
        var na = Signatures(now.Block);
        if (la == null || na == null)
            return Sig(live.Block) == Sig(now.Block);
        foreach (var key in la.Keys.Union(na.Keys, StringComparer.OrdinalIgnoreCase))
        {
            la.TryGetValue(key, out var ls);
            na.TryGetValue(key, out var ns);
            if (ls == ns)
                continue;
            if (ls is { IsBlock: true } || ns is { IsBlock: true })
                return false;
            var p = GameParams.OfKey(key);
            if (p is not { Writable: true })
                return false;
            var values = ValuesOf(now, key) ?? Default(schema, key);
            if (values == null || GameParams.Arguments(p, values) is not { } args)
                return false;
            changes.Add(new ParamChange(now.Name, p.Key!, p, args));
        }
        return true;
    }

    private static IReadOnlyList<string>? ValuesOf(EmitterView e, string key) =>
        e.Block.FindLine(key) is { } line ? line.Values.SelectMany(t => t.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToList() : null;

    private static IReadOnlyList<string>? Default(PfxSchema schema, string key) =>
        schema.Find(SchemaScope.Emitor, key)?.Default?.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private sealed record Signature(string Text, bool IsBlock);

    // Key -> signature of the line or block; null when a key appears twice (then only the whole text can tell).
    private static Dictionary<string, Signature>? Signatures(EnfBlock block)
    {
        var map = new Dictionary<string, Signature>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in block.Items)
        {
            string key = item switch { EnfLine l => l.Key, EnfBlock b => b.Key, _ => "" };
            if (!map.TryAdd(key, new Signature(Sig(item), item is EnfBlock)))
                return null;
        }
        return map;
    }

    private static IEnumerable<EnfItem> Outside(EnfBlock effect) =>
        effect.Items.Where(i => !(i is EnfBlock b && b.Key.Equals("Emitors", StringComparison.OrdinalIgnoreCase)));

    private static IEnumerable<EnfItem> NotEmitters(EnfBlock emitors) =>
        emitors.Items.Where(i => !(i is EnfBlock b && b.Key.Equals("EmitorDef", StringComparison.OrdinalIgnoreCase)));

    private static bool SameItems(IEnumerable<EnfItem> a, IEnumerable<EnfItem> b) => a.Select(Sig).SequenceEqual(b.Select(Sig));

    private static string Sig(EnfItem item) => item switch
    {
        EnfLine l => string.Join(" ", l.Tokens.Select(t => t.Value)),
        EnfBlock b => string.Join(" ", b.Header.Select(t => t.Value)) + " {" + string.Join(";", b.Items.Select(Sig)) + "}",
        _ => "",
    };
}
