namespace Pfx.XRay;

// One section of an X-Ray .ltx config as written: its parents ("[name]:a, b"), its own keys in file order (a key set
// twice keeps the later value, memory xray-particle-pe-format) and the DLTX marks of Anomaly mods: "![name]" patches an
// existing section, "@[name]" creates it only when missing, "!key" deletes a key.
public sealed class LtxSection
{
    public required string Name { get; init; }
    public required string File { get; init; }
    public int Line { get; init; }
    public char Mark { get; init; }
    public List<string> Parents { get; } = new();
    public Dictionary<string, string> Keys { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Deleted { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record LtxFile(string Path, IReadOnlyList<LtxSection> Sections, IReadOnlyList<string> Includes);

public static class XrLtx
{
    // A config file by content: at least one "[section]" line and every other non-comment line a key, a "key = value"
    // or an "#include" (the CInifile grammar).
    public static bool Looks(string text, out int sections)
    {
        sections = 0;
        int other = 0, keys = 0;
        foreach (var raw in XrText.Lines(text).Take(400))
        {
            string line = Strip(raw);
            if (line.Length == 0)
                continue;
            if (Header(line, out _, out _, out _))
                sections++;
            else if (line.StartsWith("#include", StringComparison.OrdinalIgnoreCase))
                continue;
            else if (line.Contains('=') || IsBareKey(line))
                keys++;
            else
                other++;
        }
        return sections > 0 && other <= keys / 20;
    }

    private static bool IsBareKey(string line) => line.All(c => char.IsLetterOrDigit(c) || c is '_' or '.' or '$' or '!' or '\\' or '/' or '-' or ',' or ' ');

    public static LtxFile Read(byte[] bytes, string path) => Read(XrText.Cp1251.GetString(bytes), path);

    public static LtxFile Read(string text, string path)
    {
        var sections = new List<LtxSection>();
        var includes = new List<string>();
        LtxSection? cur = null;
        int n = 0;
        foreach (var raw in XrText.Lines(text))
        {
            n++;
            string line = Strip(raw);
            if (line.Length == 0)
                continue;
            if (line.StartsWith("#include", StringComparison.OrdinalIgnoreCase))
            {
                includes.Add(line[8..].Trim().Trim('"'));
                continue;
            }
            if (Header(line, out var name, out var mark, out var parents))
            {
                cur = new LtxSection { Name = name, File = path, Line = n, Mark = mark };
                cur.Parents.AddRange(parents);
                sections.Add(cur);
                continue;
            }
            if (cur == null)
                continue;
            int eq = line.IndexOf('=');
            string key = (eq >= 0 ? line[..eq] : line).Trim();
            string value = eq >= 0 ? line[(eq + 1)..].Trim() : "";
            if (key.StartsWith('!'))
            {
                cur.Deleted.Add(key[1..].Trim());
                cur.Keys.Remove(key[1..].Trim());
            }
            else if (key.Length > 0)
            {
                cur.Keys[key] = Unquote(value);
                cur.Deleted.Remove(key);
            }
        }
        return new LtxFile(path, sections, includes);
    }

    private static string Unquote(string v) => v.Length >= 2 && v[0] == '"' && v[^1] == '"' ? v[1..^1] : v;

    // Comments: ';' anywhere and "//" outside quotes (CInifile).
    private static string Strip(string raw)
    {
        bool quoted = false;
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c == '"')
                quoted = !quoted;
            else if (c == ';' || (!quoted && c == '/' && i + 1 < raw.Length && raw[i + 1] == '/'))
                return raw[..i].Trim();
        }
        return raw.Trim();
    }

    private static bool Header(string line, out string name, out char mark, out List<string> parents)
    {
        name = "";
        mark = '\0';
        parents = new List<string>();
        int open = 0;
        if (line.Length > 1 && line[0] is '!' or '@' && line[1] == '[')
        {
            mark = line[0];
            open = 1;
        }
        if (line[open] != '[')
            return false;
        int close = line.IndexOf(']', open);
        if (close < 0)
            return false;
        name = line[(open + 1)..close].Trim().ToLowerInvariant();
        string rest = line[(close + 1)..].Trim();
        if (rest.StartsWith(':'))
            parents.AddRange(rest[1..].Split(',').Select(p => p.Trim().ToLowerInvariant()).Where(p => p.Length > 0));
        return name.Length > 0;
    }
}

// Sections of many config files merged by name, with inheritance resolved on demand: parents in order, each later
// parent over the earlier ones, the section's own keys over all of them; "!" patches apply after the base section, in
// the order the files were added. When two files define the same plain section, the later file wins (the engine's
// include order is not modelled; Twice lists such names).
public sealed class LtxConfig
{
    private readonly Dictionary<string, LtxSection> _base = new();
    private readonly Dictionary<string, List<LtxSection>> _patches = new();
    private readonly Dictionary<string, Dictionary<string, string>> _resolved = new();

    public HashSet<string> Twice { get; } = new();

    public void Add(LtxFile file)
    {
        _resolved.Clear();
        foreach (var s in file.Sections)
        {
            if (s.Mark == '!')
            {
                if (!_patches.TryGetValue(s.Name, out var list))
                    _patches[s.Name] = list = new List<LtxSection>();
                list.Add(s);
            }
            else if (s.Mark == '@' && _base.ContainsKey(s.Name))
            {
                continue;
            }
            else
            {
                if (_base.ContainsKey(s.Name))
                    Twice.Add(s.Name);
                _base[s.Name] = s;
            }
        }
    }

    public IEnumerable<string> Names => _base.Keys;
    public LtxSection? Section(string name) => _base.GetValueOrDefault(name.ToLowerInvariant());

    // The keys a section ends up with, and every section that contributed (for "where does this value come from").
    public IReadOnlyDictionary<string, string>? Resolve(string name) => Resolve(name.ToLowerInvariant(), new HashSet<string>());

    private Dictionary<string, string>? Resolve(string name, HashSet<string> path)
    {
        if (_resolved.TryGetValue(name, out var done))
            return done;
        if (!_base.TryGetValue(name, out var s) || !path.Add(name))
            return null;
        var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parent in s.Parents)
            if (Resolve(parent, path) is { } pk)
                foreach (var (k, v) in pk)
                    keys[k] = v;
        foreach (var (k, v) in s.Keys)
            keys[k] = v;
        foreach (var patch in _patches.GetValueOrDefault(name) ?? new List<LtxSection>())
        {
            foreach (var k in patch.Deleted)
                keys.Remove(k);
            foreach (var (k, v) in patch.Keys)
                keys[k] = v;
        }
        path.Remove(name);
        _resolved[name] = keys;
        return keys;
    }

    // Parents that are named but defined nowhere in the added files (base game files the folder does not hold).
    public IEnumerable<string> MissingParents(string name)
    {
        var seen = new HashSet<string>();
        var stack = new Stack<string>(new[] { name.ToLowerInvariant() });
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (!seen.Add(n))
                continue;
            if (!_base.TryGetValue(n, out var s))
            {
                if (n != name.ToLowerInvariant())
                    yield return n;
                continue;
            }
            foreach (var p in s.Parents)
                stack.Push(p);
        }
    }
}
