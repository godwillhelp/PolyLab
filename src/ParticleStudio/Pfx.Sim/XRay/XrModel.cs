using System.Globalization;

namespace Pfx.Sim.XRay;

// Doubles, like the numpy arrays of xray.py, so the golden tests compare the same arithmetic.
public readonly record struct D3(double X, double Y, double Z)
{
    public static readonly D3 Zero = new(0, 0, 0);
    public static D3 operator +(D3 a, D3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static D3 operator -(D3 a, D3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static D3 operator -(D3 a) => new(-a.X, -a.Y, -a.Z);
    public static D3 operator *(D3 a, double k) => new(a.X * k, a.Y * k, a.Z * k);
    public static D3 operator *(D3 a, D3 b) => new(a.X * b.X, a.Y * b.Y, a.Z * b.Z);
    public static D3 operator /(D3 a, double k) => new(a.X / k, a.Y / k, a.Z / k);
    public double Dot(D3 b) => X * b.X + Y * b.Y + Z * b.Z;
    public D3 Cross(D3 b) => new(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);
    public double Length2 => X * X + Y * Y + Z * Z;
    public double Length => Math.Sqrt(Length2);
    public D3 Unit() { double n = Length; return n > 0 ? this / n : this; }
    public static D3 Min(D3 a, D3 b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));
    public static D3 Max(D3 a, D3 b) => new(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));
    public double this[int i] => i == 0 ? X : i == 1 ? Y : Z;

    // "x,y,z" as the .pe files and xray.py vec3() read it: missing parts are 0.
    public static D3 Parse(string s)
    {
        var p = s.Split(',').Select(x => double.Parse(x.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture)).Concat(new double[3]).ToArray();
        return new D3(p[0], p[1], p[2]);
    }
}

// The editor parameter lists of every action type (engine ParticleEffectActions.cpp constructors, as xray.py SPEC):
// kind f float, v vector, d domain, b bool, i int. The keys of a .pe section (flt_0000, vec_0000, ...) number the
// parameters of one kind in byte order of their names (memory xray-particle-pe-format).
public static class XrSpec
{
    public static readonly IReadOnlyDictionary<int, (string Type, (char Kind, string Name)[] Params)> Actions = new Dictionary<int, (string, (char, string)[])>
    {
        [0] = ("avoid", new[] { ('d', "Position"), ('f', "Magnitude"), ('f', "Epsilon"), ('f', "Look Ahead"), ('b', "Allow Rotate") }),
        [1] = ("bounce", new[] { ('d', "Position"), ('f', "Friction"), ('f', "Resilience"), ('f', "Cutoff"), ('b', "Allow Rotate") }),
        [3] = ("copyvertexb", new[] { ('b', "Copy Position") }),
        [4] = ("damping", new[] { ('v', "Damping"), ('f', "V Low"), ('f', "V High") }),
        [5] = ("explosion", new[] { ('v', "Center"), ('f', "Velocity"), ('f', "Magnitude"), ('f', "Standart Dev"), ('f', "Epsilon"), ('f', "Age"), ('b', "Allow Rotate") }),
        [6] = ("follow", new[] { ('f', "Magnitude"), ('f', "Epsilon"), ('f', "Max Radius") }),
        [7] = ("gravitate", new[] { ('f', "Magnitude"), ('f', "Epsilon"), ('f', "Max Radius") }),
        [8] = ("gravity", new[] { ('v', "Direction"), ('b', "Allow Rotate") }),
        [9] = ("jet", new[] { ('d', "Accelerate"), ('v', "Center"), ('f', "Magnitude"), ('f', "Epsilon"), ('f', "Max Radius"), ('b', "Allow Rotate") }),
        [10] = ("killold", new[] { ('f', "Age Limit"), ('b', "Kill Less Than") }),
        [11] = ("matchvelocity", new[] { ('f', "Magnitude"), ('f', "Epsilon"), ('f', "Max Radius") }),
        [12] = ("move", Array.Empty<(char, string)>()),
        [13] = ("orbitline", new[] { ('v', "Position"), ('v', "Axis"), ('f', "Magnitude"), ('f', "Epsilon"), ('f', "Max Radius"), ('b', "Allow Rotate") }),
        [14] = ("orbitpoint", new[] { ('v', "Center"), ('f', "Magnitude"), ('f', "Epsilon"), ('f', "Max Radius"), ('b', "Allow Rotate") }),
        [15] = ("randomaccel", new[] { ('d', "Accelerate"), ('b', "Allow Rotate") }),
        [16] = ("randomdisplace", new[] { ('d', "Displace"), ('b', "Allow Rotate") }),
        [17] = ("randomvelocity", new[] { ('d', "Velocity"), ('b', "Allow Rotate") }),
        [18] = ("restore", new[] { ('f', "Time") }),
        [19] = ("sink", new[] { ('b', "Kill Inside"), ('d', "Domain"), ('b', "Allow Rotate") }),
        [20] = ("sinkvelocity", new[] { ('b', "Kill Inside"), ('d', "Domain"), ('b', "Allow Rotate") }),
        [21] = ("source", new[] { ('f', "Rate"), ('d', "Domain"), ('d', "Velocity"), ('d', "Rotation"), ('d', "Size"), ('b', "Single Size"), ('d', "Color"),
            ('f', "Color\\Alpha"), ('f', "Starting Age"), ('f', "Age Sigma"), ('f', "Parent Motion"), ('b', "Allow Rotate") }),
        [22] = ("speedlimit", new[] { ('f', "Min Speed"), ('f', "Max Speed") }),
        [23] = ("targetcolor", new[] { ('v', "Color"), ('f', "Alpha"), ('f', "Scale"), ('f', "TimeFrom"), ('f', "TimeTo") }),
        [24] = ("targetsize", new[] { ('v', "Size"), ('v', "Scale") }),
        [25] = ("targetrotate", new[] { ('v', "Rotation"), ('f', "Scale") }),
        [26] = ("targetrotate", new[] { ('v', "Rotation"), ('f', "Scale") }),
        [27] = ("targetvelocity", new[] { ('v', "Velocity"), ('f', "Scale"), ('b', "Allow Rotate") }),
        [28] = ("targetvelocity", new[] { ('v', "Velocity"), ('f', "Scale"), ('b', "Allow Rotate") }),
        [29] = ("vortex", new[] { ('v', "Center"), ('v', "Axis"), ('f', "Magnitude"), ('f', "Epsilon"), ('f', "Max Radius"), ('b', "Allow Rotate") }),
        [30] = ("turbulence", new[] { ('f', "Frequency"), ('i', "Octaves"), ('f', "Magnitude"), ('f', "Delta"), ('v', "Movement") }),
        [31] = ("scatter", new[] { ('v', "Center"), ('f', "Magnitude"), ('f', "Epsilon"), ('f', "Max Radius"), ('b', "Allow Rotate") }),
    };

    public static readonly IReadOnlyDictionary<char, string> KeyPrefix = new Dictionary<char, string> { ['f'] = "flt", ['v'] = "vec", ['b'] = "bool", ['i'] = "int" };

    // xray.py NO_ANALOGUE / NOT_SIMULATED: what the DayZ conversion drops and what its replay leaves out.
    public static readonly IReadOnlySet<string> NoAnalogue = new HashSet<string> { "vortex", "orbitpoint", "orbitline", "turbulence", "scatter", "explosion",
        "gravitate", "follow", "matchvelocity", "jet", "avoid", "bounce", "restore", "copyvertexb", "randomvelocity", "randomdisplace", "speedlimit" };
    public static readonly IReadOnlySet<string> NotSimulated = new HashSet<string> { "turbulence", "gravitate", "follow", "matchvelocity", "jet", "avoid",
        "bounce", "restore", "copyvertexb" };

    public static IReadOnlyList<string> Sorted(IEnumerable<string> names) => names.OrderBy(n => n, StringComparer.Ordinal).ToList();
}

public sealed class XrAction
{
    public required string Type { get; init; }
    public required string Name { get; init; }
    public bool Enabled { get; init; }
    public Dictionary<string, double> Floats { get; } = new();
    public Dictionary<string, D3> Vectors { get; } = new();
    public Dictionary<string, bool> Bools { get; } = new();
    public Dictionary<string, int> Ints { get; } = new();
    public Dictionary<string, XrDomain> Domains { get; } = new();

    public double F(string name) => Floats[name];
    public D3 V(string name) => Vectors[name];
    public bool B(string name) => Bools[name];
    public XrDomain D(string name) => Domains[name];
}

public sealed record XrFrame(int TileU, int TileV, int Count, double Speed, double TexU, double TexV, int DimX);

// One .pe effect as xray.py parse_effect reads it: disabled actions and malformed ones are left out with a note.
public sealed class XrEffect
{
    public const int Sprite = 1 << 0, Framed = 1 << 10, Animated = 1 << 11, RandomFrame = 1 << 12, TimeLimit = 1 << 14, AlignToPath = 1 << 15,
        Collision = 1 << 16, VelocityScale = 1 << 18, WorldAlign = 1 << 20, FaceAlign = 1 << 21;

    public required string Path { get; init; }
    public int Max { get; init; }
    public int Flags { get; init; }
    public double Step { get; init; }
    public string Shader { get; init; } = "";
    public string Texture { get; init; } = "";
    public XrFrame? Frame { get; init; }
    public double? TimeLimitValue { get; init; }
    public double? CollisionResilience { get; init; }
    public D3? VelScale { get; init; }
    public D3? Align { get; init; }
    public List<XrAction> Actions { get; } = new();
    public List<string> Notes { get; } = new();

    public bool Has(int flag) => (Flags & flag) != 0;

    public static XrEffect Parse(IReadOnlyDictionary<string, Dictionary<string, string>> s, string path)
    {
        string Get(string sect, string key, string fallback) => s.TryGetValue(sect, out var d) && d.TryGetValue(key, out var v) ? v : fallback;
        int flags = (int)Num(Get("_effect", "flags", "0"));
        var tex = Get("sprite", "texture", "").Split(',').Select(t => t.Trim().Replace('\\', '/').ToLowerInvariant()).Where(t => t.Length > 0).ToList();
        XrFrame? frame = null;
        if ((flags & Framed) != 0 && s.TryGetValue("frame", out var f))
        {
            var ts = (f.TryGetValue("tex_size", out var t) ? t : "1,1").Split(',').Select(Num).ToList();
            int tile(int i) => i < ts.Count && ts[i] > 0 ? Math.Max(1, (int)Math.Round(1.0 / ts[i], MidpointRounding.ToEven)) : 1;
            frame = new XrFrame(tile(0), tile(1), (int)Num(f.GetValueOrDefault("frame_count", "1")), Num(f.GetValueOrDefault("speed", "0")),
                ts.Count > 0 ? ts[0] : 1, ts.Count > 1 ? ts[1] : 1, (int)Num(f.GetValueOrDefault("dim_x", "1")));
        }
        var eff = new XrEffect
        {
            Path = path,
            Max = (int)Num(Get("_effect", "max_particles", "0")),
            Flags = flags,
            Step = Num(Get("_effect", "update_step", "33")) / 1000.0,
            Shader = Get("sprite", "shader", "").Replace('\\', '/').ToLowerInvariant(),
            Texture = tex.Count > 0 ? StripExtension(tex[0]) : "",
            Frame = frame,
            TimeLimitValue = (flags & TimeLimit) != 0 && s.TryGetValue("timelimit", out var tl) ? Num(tl.GetValueOrDefault("value", "0")) : null,
            CollisionResilience = (flags & Collision) != 0 && s.TryGetValue("collision", out var c) ? Num(c.GetValueOrDefault("collide_resilence", "0")) : null,
            VelScale = (flags & VelocityScale) != 0 && s.TryGetValue("velocity_scale", out var vs) ? D3.Parse(vs["value"]) : null,
            Align = (flags & AlignToPath) != 0 && s.TryGetValue("align_to_path", out var al) ? D3.Parse(al["default_rotation"]) : null,
        };
        int count = (int)Num(Get("_effect", "action_count", "0"));
        for (int i = 0; i < count; i++)
        {
            string sect = $"action_{i:D4}";
            if (!s.ContainsKey(sect))
                continue;
            var act = ParseAction(s, sect, eff.Notes);
            if (act == null)
                continue;
            if (!act.Enabled)
            {
                eff.Notes.Add($"    action {act.Name} disabled, skipped");
                continue;
            }
            eff.Actions.Add(act);
        }
        return eff;
    }

    private static XrAction? ParseAction(IReadOnlyDictionary<string, Dictionary<string, string>> secs, string sect, List<string> log)
    {
        var a = secs[sect];
        int tid = (int)Num(a.GetValueOrDefault("action_type", "-1"));
        if (!XrSpec.Actions.TryGetValue(tid, out var spec))
        {
            log.Add($"    action {a.GetValueOrDefault("action_name", "?")}: unknown type {tid}, skipped");
            return null;
        }
        var act = new XrAction { Type = spec.Type, Name = a.GetValueOrDefault("action_name", spec.Type), Enabled = ((int)Num(a.GetValueOrDefault("flags", "1")) & 1) != 0 };
        foreach (char k in "fvbi")
        {
            var names = XrSpec.Sorted(spec.Params.Where(p => p.Kind == k).Select(p => p.Name));
            string prefix = XrSpec.KeyPrefix[k] + "_";
            int have = a.Keys.Count(key => key.StartsWith(prefix, StringComparison.Ordinal));
            if (have != names.Count)
            {
                log.Add($"    action {act.Name}: {have} {XrSpec.KeyPrefix[k]} keys, expected {names.Count}, skipped");
                return null;
            }
            for (int i = 0; i < names.Count; i++)
            {
                string raw = a[$"{prefix}{i:D4}"];
                switch (k)
                {
                    case 'f': act.Floats[names[i]] = Num(raw); break;
                    case 'v': act.Vectors[names[i]] = D3.Parse(raw); break;
                    case 'b': act.Bools[names[i]] = On(raw); break;
                    default: act.Ints[names[i]] = int.Parse(raw.Trim(), CultureInfo.InvariantCulture); break;
                }
            }
        }
        var domains = XrSpec.Sorted(spec.Params.Where(p => p.Kind == 'd').Select(p => p.Name));
        for (int i = 0; i < domains.Count; i++)
        {
            if (!secs.TryGetValue($"domain_{sect}_{i:D4}", out var ds))
            {
                log.Add($"    action {act.Name}: domain {domains[i]} missing, skipped");
                return null;
            }
            var v = new[] { D3.Parse(ds["v0"]), D3.Parse(ds["v1"]), D3.Parse(ds["v2"]) }.SelectMany(x => new[] { x.X, x.Y, x.Z }).ToArray();
            act.Domains[domains[i]] = new XrDomain((int)Num(ds["type"]), v);
        }
        return act;
    }

    public static double Num(string s) => double.Parse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);

    private static bool On(string s) => s.Trim().ToLowerInvariant() is "on" or "true" or "yes" or "1";

    // Python's os.path.splitext: the extension starts at the last dot of the name, unless only dots precede it.
    private static string StripExtension(string p)
    {
        int slash = p.LastIndexOf('/');
        int dot = p.LastIndexOf('.');
        if (dot <= slash)
            return p;
        for (int i = slash + 1; i < dot; i++)
            if (p[i] != '.')
                return p[..dot];
        return p;
    }
}

public sealed record XrGroupItem(string Name, double T0, double T1, int Flags, IReadOnlyList<(string Kind, string Name)> Children)
{
    public const int Deferred = 1 << 0, OnPlayChild = 1 << 1, Enabled = 1 << 2, OnPlayRewind = 1 << 4, OnBirthChild = 1 << 5, OnDeadChild = 1 << 6;
    public bool Has(int flag) => (Flags & flag) != 0;
}

// One .pg group as xray.py parse_group reads it.
public sealed record XrGroup(double TimeLimit, IReadOnlyList<XrGroupItem> Effects)
{
    public static XrGroup Parse(IReadOnlyDictionary<string, Dictionary<string, string>> s)
    {
        var g = s.GetValueOrDefault("_group") ?? new Dictionary<string, string>();
        double Or0(Dictionary<string, string> d, string key) => d.TryGetValue(key, out var v) && v.Trim().Length > 0 ? XrEffect.Num(v) : 0;
        var items = new List<XrGroupItem>();
        int n = (int)Or0(g, "effects_count");
        for (int i = 0; i < n; i++)
        {
            if (!s.TryGetValue($"effect_{i:D4}", out var e) || e.Count == 0)
                continue;
            var children = new List<(string, string)>();
            foreach (var k in new[] { "on_play_child", "on_birth_child", "on_death_child" })
                if (e.TryGetValue(k, out var c) && c.Trim().Length > 0)
                    children.Add((k, c.Replace('\\', '/').ToLowerInvariant()));
            items.Add(new XrGroupItem(e.GetValueOrDefault("effect_name", "").Replace('\\', '/').ToLowerInvariant(), Or0(e, "time0"), Or0(e, "time1"),
                (int)Or0(e, "flags"), children));
        }
        return new XrGroup(Or0(g, "timelimit"), items);
    }
}
