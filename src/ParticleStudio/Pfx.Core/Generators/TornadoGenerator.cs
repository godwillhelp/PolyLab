using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Pfx.Core.Model;

namespace Pfx.Core.Generators;

// One storm-ring of the tornado: radius of the ring, blob size, emitters on the ring, births per second each.
public sealed record TornadoRing(double Radius, double Blob, int Count, double Rate);

// The numbers own_tornado.py (CG_AnomalyTools) builds the CG tornado from; the defaults are its module constants.
// Bolts and strike are copied from a donor effect (CG's X-Ray conversion of the generator anomaly); without one the
// generator leaves those two parts out.
public sealed record TornadoSettings
{
    public string Name { get; init; } = "tornado_cg";
    public string Dust { get; init; } = "graphics/particles/materials/smoke/smoke_dust_04.emat";
    public string Stones { get; init; } = "graphics/particles/materials/smoke/smoke_debris_01.emat";
    public string? BoltDonor { get; init; }
    public string StrikeGlow { get; init; } = "generator_sparks_main_wider_03";
    public string[] Bolts { get; init; } = { "generator_sparks_main", "generator_sparks_main_08", "generator_sparks_main_07", "generator_sparks_main_small" };
    public double ZoneRadius { get; init; } = 100;
    public double[] Dark { get; init; } = { 0.289, 0.145, 0.058 };
    public double[] Brown { get; init; } = { 0.481, 0.241, 0.096 };
    public int ColumnPuffs { get; init; } = 540;
    public double FunnelHeight { get; init; } = 65;
    public double FunnelBase { get; init; } = 10;
    public double FunnelTop { get; init; } = 22;
    public double FunnelSpin { get; init; } = -150;
    public int Strands { get; init; } = 5;
    public double StrandSpeed { get; init; } = 6;
    public double JetHeading { get; init; } = 270;
    public double AroundLife { get; init; } = 5;
    public TornadoRing[] Rings { get; init; } = { new(30, 12, 10, 3), new(55, 18, 14, 4.5), new(85, 20, 20, 4) };
    public double CapSpin { get; init; } = -25;
    public double DebrisNear { get; init; } = 150;
    public double[] StrikeGap { get; init; } = { 3, 8 };
    public double StrikeBurst { get; init; } = 0.2;
    public double StrikeBrightness { get; init; } = 1;

    public double Circulation => TornadoGenerator.Radians(FunnelSpin) * (FunnelBase * FunnelBase);
    public double WallSlope => TornadoGenerator.Degrees(Math.Atan((FunnelTop - FunnelBase) / FunnelHeight));
    public double StrandRise => FunnelHeight / (StrandSpeed * Math.Cos(TornadoGenerator.Radians(WallSlope)));
}

// A file the generator made and the script data CG keeps beside it (manifest.py OWN_FX: spin, near radius).
public sealed record GeneratedPart(string Suffix, byte[] Bytes, float Spin, float Near);

public sealed record TornadoResult(IReadOnlyList<GeneratedPart> Parts, IReadOnlyList<string> Notes, double[] Swirl);

// Port of own_tornado.py: the same settings give the same .ptc bytes (golden test against the script's output). Every
// number goes through the script's own formatting ("%.3f" without trailing zeros) and its order of operations.
public static class TornadoGenerator
{
    private static readonly double[] All = { 0, 180 };
    private static readonly double[] Flat = { 85, 95 };
    private static readonly double[] Dense = { 0, 0, 0.12, 0.85, 0.75, 0.75, 1, 0 };
    private static readonly double[] Soft = { 0, 0, 0.15, 0.6, 1, 0 };
    private static readonly double[] Haze = { 0, 0, 0.15, 0.45, 0.85, 0.45, 1, 0 };
    private static readonly double[] Speck = { 0, 0, 0.1, 1, 0.9, 1, 1, 0 };
    private static readonly double[] Drift = { 0, 0, 0.2, 0.9, 0.8, 0.9, 1, 0 };
    private static readonly double[] Column = { 0, 0, 0.03, 0.85, 0.9, 0.75, 1, 0 };
    private static readonly string[] BoltScaled = { "ShapeSize", "Offset", "SizeMultiplier", "SizeRND" };
    private static readonly string[] BoltHeld = { "Velocity", "VelocityRND", "GravityMultiply" };
    private static readonly Regex DonorBlock = new(@"^  EmitorDef (\w+) \{\n(.*?)^  \}$", RegexOptions.Multiline | RegexOptions.Singleline);
    private static readonly Encoding Latin1 = Encoding.Latin1;

    // CPython's math.radians / math.degrees multiply by these constants.
    public static double Radians(double degrees) => degrees * (Math.PI / 180.0);
    public static double Degrees(double radians) => radians * (180.0 / Math.PI);

    // Python's "%.3f" % v with trailing zeros and the point stripped; "-0" and "" become "0".
    public static string Num(double v)
    {
        string s = v.ToString("F3", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');
        return s is "-0" or "" ? "0" : s;
    }

    private static string Nums(IEnumerable<double> values) => string.Join(" ", values.Select(Num));

    // Python's float % (the sign of the divisor).
    private static double Mod(double a, double b)
    {
        double m = a % b;
        if (m != 0 && (b < 0) != (m < 0))
            m += b;
        return m == 0 ? 0 : m;
    }

    private static List<(double X, double Z)> Ring(double radius, int count, double phase = 0) =>
        Enumerable.Range(0, count).Select(i => (radius * Math.Cos(Radians(phase + 360.0 * i / count)),
            radius * Math.Sin(Radians(phase + 360.0 * i / count)))).ToList();

    private sealed record E(string Name, int Local, double[] Extent, double[] Polar, double Rate, double Speed, double Life,
        double Size, double[] ColorLow, double[] ColorHigh, double[] Alpha)
    {
        public double[] Offset { get; init; } = { 0, 0, 0 };
        public string? Material { get; init; }
        public double SizeStart { get; init; } = 0.5;
        public double Drag { get; init; }
        public double Rot { get; init; } = 30;
        public bool Box { get; init; }
        public double? ScaleY { get; init; }
        public double Azimuth { get; init; } = 360;
        public double Yaw { get; init; }
    }

    private static List<string> Emitter(E e, TornadoSettings s)
    {
        var lines = new List<string>
        {
            $"  EmitorDef {e.Name} {{",
            $"   ShapeType {(e.Box ? "Box" : "Ellipse")}",
            $"   ShapeSize {Nums(e.Extent)}",
            $"   ConeAngle {Num(e.Azimuth)} {Num(2 * e.Polar[0])} {Num(2 * e.Polar[1])}",
            $"   MaxNum {(long)Math.Ceiling(e.Rate * e.Life * 1.2)}",
            $"   BirthRate {Num(e.Rate)}",
            "   BirthRateRND 0",
            $"   Offset {Nums(e.Offset)}",
            $"   Angles {Num(e.Yaw)} 0 0",
            $"   LocalTransform {e.Local}",
            $"   Material \"{e.Material ?? s.Dust}\"",
        };
        if (e.ScaleY is { } scaleY)
            lines.Add($"   ScaleY {Num(scaleY)}");
        lines.AddRange(new[]
        {
            $"   SizeMultiplier {Num(e.Size)}",
            $"   SizeRND {Num(e.Size * 0.25)}",
            "   RandomRotDir 1",
            $"   RotMultiplier {Num(e.Rot)}",
            "   RandomAngle 1",
            "   VelAngle 0",
            "   BillboardingType Full",
            $"   Velocity {Num(e.Speed)}",
            $"   VelocityRND {Num(e.Speed * 0.25)}",
            $"   AirResistance {Num(e.Drag)}",
            "   GravityMultiply 0",
            "   Wind 0",
            "   RandomFrame 1",
            $"   LifeTime {Num(e.Life)}",
            $"   LifeTimeRND {Num(e.Life * 0.1)}",
            "   Color {",
            $"    0 {Nums(e.ColorLow)} 1 {Nums(e.ColorHigh)}",
            "   }",
            "   Alpha {",
            $"    {Nums(e.Alpha)}",
            "   }",
            "   Size {",
            $"    0 {Num(e.SizeStart)} 1 1",
            "   }",
            "   EffectTime 10",
            "   Repeat 1",
            "  }",
        });
        return lines;
    }

    // own_tornado.write: LF lines joined, a final LF, every LF written as CRLF.
    private static byte[] File(IEnumerable<List<string>> emitters)
    {
        var lines = new List<string> { "EffectDef {", " Emitors {" };
        foreach (var e in emitters)
            lines.AddRange(e);
        lines.Add(" }");
        lines.Add("}");
        return Latin1.GetBytes((string.Join("\n", lines) + "\n").Replace("\n", "\r\n"));
    }

    private static List<List<string>> Funnel(TornadoSettings s) => new()
    {
        Emitter(new E("column", 1, new[] { 0.8 * s.FunnelBase, 0, 0.8 * s.FunnelBase }, new[] { s.WallSlope, s.WallSlope },
            s.ColumnPuffs / s.StrandRise, s.StrandSpeed, s.StrandRise, 1.2 * s.FunnelTop, s.Dark, s.Brown, Column)
        { SizeStart = s.FunnelBase / s.FunnelTop }, s),
    };

    private static List<List<string>> InnerDebris(TornadoSettings s) => new()
    {
        Emitter(new E("inner_debris", 1, new[] { 1.6 * s.FunnelTop, s.FunnelHeight, 1.6 * s.FunnelTop }, All, 40, 0.5, 6, 0.6,
            s.Dark, s.Dark, Speck) { Offset = new[] { 0, s.FunnelHeight / 2, 0 }, Material = s.Stones, SizeStart = 1, Rot = 300 }, s),
    };

    private static List<List<string>> StrandEmitters(TornadoSettings s)
    {
        var parts = new List<List<string>>();
        var ring = Ring(s.FunnelBase, s.Strands);
        for (int i = 0; i < ring.Count; i++)
        {
            var (x, z) = ring[i];
            double heading = Degrees(Math.Atan2(x, z));
            parts.Add(Emitter(new E($"strand_{i}", 1, new[] { 0.8, 0, 0.8 }, new[] { s.WallSlope, s.WallSlope }, 12, s.StrandSpeed,
                s.StrandRise, 8, s.Dark, s.Dark, Dense)
            { Offset = new[] { x, 1, z }, SizeStart = 0.6, Azimuth = 0, Yaw = Mod(heading - s.JetHeading, 360) }, s));
        }
        return parts;
    }

    private static List<List<string>> Cap(TornadoSettings s)
    {
        double h = s.FunnelHeight;
        return new()
        {
            Emitter(new E("cap_body", 1, new double[] { 64, 16, 64 }, new double[] { 40, 80 }, 16, 1.5, 12, 30, s.Dark, s.Brown, Dense)
            { Offset = new[] { 0, h + 12, 0 }, Drag = 0.2, ScaleY = 0.6 }, s),
            Emitter(new E("cap_under", 1, new double[] { 76, 4, 76 }, new double[] { 85, 95 }, 10, 1.5, 10, 26, s.Dark, s.Dark, Dense)
            { Offset = new[] { 0, h + 3, 0 }, Drag = 0.3, ScaleY = 0.4 }, s),
            Emitter(new E("cap_spread", 1, new double[] { 50, 8, 50 }, new double[] { 75, 92 }, 30, 7, 10, 22, s.Dark, s.Brown, Dense)
            { Offset = new[] { 0, h + 8, 0 }, Drag = 0.4, ScaleY = 0.5 }, s),
            Emitter(new E("cap_rim", 1, new double[] { 90, 12, 90 }, new double[] { 70, 100 }, 40, 3, 8, 11, s.Dark, s.Brown, Soft)
            { Offset = new[] { 0, h + 14, 0 }, Drag = 0.3, ScaleY = 0.6 }, s),
            Emitter(new E("cap_crown", 1, new double[] { 90, 12, 90 }, new double[] { 30, 85 }, 18, 2, 12, 26, s.Dark, s.Brown, Dense)
            { Offset = new[] { 0, h + 22, 0 }, Drag = 0.2, ScaleY = 0.5 }, s),
        };
    }

    private static List<List<string>> OuterDebris(TornadoSettings s) => new()
    {
        Emitter(new E("debris", 1, new double[] { 120, 70, 120 }, All, 80, 0.5, 8, 0.8, s.Dark, s.Dark, Speck)
        { Offset = new double[] { 0, 40, 0 }, Material = s.Stones, SizeStart = 1, Rot = 300 }, s),
    };

    private static List<List<string>> Billow(TornadoSettings s) => new()
    {
        Emitter(new E("ground_billow", 1, new[] { 2 * s.FunnelBase, 0, 2 * s.FunnelBase }, new double[] { 0, 50 }, 14, 2, 4, 16, s.Dark, s.Brown, Dense)
        { Drag = 0.8, SizeStart = 0.6 }, s),
    };

    // The emitters of one group of the tornado as emitter texts (the funnel and strand recipes add them to a part).
    public static IReadOnlyList<string> Group(TornadoSettings s, string group) => (group switch
    {
        "column" => Funnel(s),
        "strands" => StrandEmitters(s),
        "billow" => Billow(s),
        "cap" => Cap(s),
        "inner_debris" => InnerDebris(s),
        _ => throw new ArgumentException(group),
    }).Select(lines => "\n" + string.Join("\n", lines)).ToList();

    private static List<List<string>> Satellites(TornadoSettings s)
    {
        var parts = new List<List<string>>();
        var ring = Ring(60, 3);
        for (int i = 0; i < ring.Count; i++)
        {
            var (x, z) = ring[i];
            parts.Add(Emitter(new E($"sat_{i}_low", 1, new double[] { 2, 5, 2 }, new double[] { 0, 10 }, 10, 1, 2.5, 5, s.Dark, s.Brown, Dense)
            { Offset = new[] { x, 5, z } }, s));
            parts.Add(Emitter(new E($"sat_{i}_high", 1, new double[] { 3, 5, 3 }, new double[] { 0, 15 }, 10, 1, 2.5, 6, s.Dark, s.Brown, Dense)
            { Offset = new[] { x, 13, z } }, s));
            parts.Add(Emitter(new E($"sat_{i}_skirt", 1, new double[] { 5, 0, 5 }, new double[] { 0, 60 }, 8, 1.5, 2, 6, s.Dark, s.Brown, Soft)
            { Offset = new[] { x, 0, z }, Drag = 1 }, s));
        }
        return parts;
    }

    private static List<List<string>> StormRing(TornadoSettings s, int index, TornadoRing r) =>
        Ring(r.Radius, r.Count).Select((p, i) => Emitter(new E($"ring_{index}_{i}", 1, new[] { 2 * r.Blob, 24, 2 * r.Blob }, All, r.Rate, 0.3, 10,
            1.6 * r.Blob, s.Dark, s.Brown, Haze) { Offset = new[] { p.X, 12, p.Z }, ScaleY = 0.6 }, s)).ToList();

    private static Dictionary<string, List<string>> DonorBlocks(byte[] bytes)
    {
        string text = Latin1.GetString(bytes).Replace("\r\n", "\n");
        var blocks = new Dictionary<string, List<string>>();
        foreach (Match m in DonorBlock.Matches(text))
            blocks[m.Groups[1].Value] = m.Groups[2].Value.TrimEnd('\n').Split('\n').ToList();
        return blocks;
    }

    private static string[] Words(string line) => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static double[] DonorValue(IEnumerable<string> lines, string key) =>
        Words(lines.First(l => Words(l) is { Length: > 0 } w && w[0] == key)).Skip(1)
            .Select(v => double.Parse(v, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();

    private static List<List<string>> GeneratorEmitters(TornadoSettings s, Dictionary<string, List<string>> blocks, IEnumerable<string> names,
        Func<string, string, string, string> edit)
    {
        double k = s.FunnelHeight / s.Bolts.Max(n => DonorValue(blocks[n], "Offset")[1] + DonorValue(blocks[n], "ShapeSize")[1] / 2);
        var parts = new List<List<string>>();
        foreach (string name in names)
        {
            var output = new List<string> { $"  EmitorDef {name} {{" };
            foreach (string raw in blocks[name])
            {
                string line = raw;
                string key = line.Trim().Length > 0 ? Words(line)[0] : "";
                if (BoltScaled.Contains(key))
                    line = $"   {key} {Nums(DonorValue(new[] { line }, key).Select(v => v * k))}";
                else if (BoltHeld.Contains(key))
                    line = $"   {key} 0";
                output.Add(edit(name, key, line));
            }
            output.Add("  }");
            parts.Add(output);
        }
        return parts;
    }

    public static TornadoResult Generate(TornadoSettings s)
    {
        var parts = new List<GeneratedPart>();
        var notes = new List<string>();
        float Spin(double v) => (float)v;
        float RingSpin(double r) => (float)double.Parse((Degrees(s.Circulation / (r * r))).ToString("F2", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

        Dictionary<string, List<string>>? donor = null;
        if (s.BoltDonor != null && System.IO.File.Exists(s.BoltDonor))
            donor = DonorBlocks(System.IO.File.ReadAllBytes(s.BoltDonor));
        if (donor != null && s.Bolts.Append(s.StrikeGlow).All(donor.ContainsKey))
        {
            parts.Add(new GeneratedPart("", File(GeneratorEmitters(s, donor, s.Bolts, (_, _, line) => line)), 0, 0));
            parts.Add(new GeneratedPart("_strike", File(GeneratorEmitters(s, donor, s.Bolts.Append(s.StrikeGlow), (name, key, line) => key switch
            {
                "Offset" => "   Offset 0 0 0",
                "BirthRate" => $"   BirthRate {Num(DonorValue(new[] { line }, key)[0] / s.StrikeBurst)}",
                "LifeTime" when s.Bolts.Contains(name) => $"   LifeTime {Num(s.StrikeBurst)}",
                _ => line,
            })), 0, 0));
        }
        else
        {
            notes.Add("Gen.NoBoltDonor");
        }
        parts.Add(new GeneratedPart("_t1", File(Funnel(s).Concat(StrandEmitters(s)).Concat(Billow(s))), Spin(s.FunnelSpin), 0));
        parts.Add(new GeneratedPart("_t2", File(Satellites(s)), -12, 0));
        parts.Add(new GeneratedPart("_t3", File(Cap(s)), Spin(s.CapSpin), 0));
        for (int i = 0; i < s.Rings.Length; i++)
            parts.Add(new GeneratedPart($"_t{4 + i}", File(StormRing(s, i, s.Rings[i])), RingSpin(s.Rings[i].Radius), 0));
        parts.Add(new GeneratedPart("_t8", File(InnerDebris(s)), Spin(s.FunnelSpin), (float)s.DebrisNear));
        parts.Add(new GeneratedPart("_t9", File(OuterDebris(s)), Spin(s.CapSpin), (float)s.DebrisNear));
        double upstream = Math.Abs(s.Circulation) / (s.ZoneRadius / 2) * s.AroundLife / 2;
        parts.Add(new GeneratedPart("_around", File(new[]
        {
            Emitter(new E("around", 0, new double[] { 50, 6, 50 }, Flat, 300, 1, s.AroundLife, 14, s.Dark, s.Brown, Drift)
            { Offset = new[] { upstream, 2, 0 }, Box = true, Azimuth = 20 }, s),
        }), 0, 0));
        double swirl = double.Parse(s.Circulation.ToString("F1", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        return new TornadoResult(parts, notes, new[] { swirl, s.FunnelBase });
    }

    public const string PartsFolder = "particles";

    // The project manifest.py builds for the tornado (OWN_FX): one state with every turning part, the bolts and the
    // strikes (light at the funnel height, as STRIKE "light"). The around-the-player dust has no project field yet
    // (the camera-attached swirl is CG runtime code): its file is written, no state starts it.
    public static ProjectFile Project(TornadoSettings s, TornadoResult r)
    {
        var file = new ProjectFile { Name = ProjectNames.Id(s.Name) };
        var idle = new StateEntry { Name = "idle" };
        foreach (var part in r.Parts)
        {
            var entry = new PartEntry
            {
                Id = ProjectNames.Id(s.Name + part.Suffix), Path = PartsFolder + "/" + s.Name + part.Suffix + ".ptc", Spin = part.Spin, Near = part.Near,
            };
            file.Parts.Add(entry);
            if (part.Suffix is not ("_strike" or "_around"))
                idle.Items.Add(new StateItem { Ref = entry.Id });
        }
        if (file.Parts.FirstOrDefault(p => p.Path.EndsWith("_strike.ptc", StringComparison.Ordinal)) is { } strikePart)
        {
            var strike = new StrikeEntry
            {
                Id = ProjectNames.Id(s.Name + "_strikes"), Part = strikePart.Id, Height = (float)s.FunnelHeight, RadiusLow = (float)s.FunnelBase,
                RadiusHigh = (float)s.FunnelTop, GapMin = (float)s.StrikeGap[0], GapMax = (float)s.StrikeGap[1], Duration = (float)s.StrikeBurst,
                LightRadius = (float)s.FunnelHeight, LightBrightness = (float)s.StrikeBrightness,
            };
            file.Strikes.Add(strike);
            idle.Items.Add(new StateItem { Ref = strike.Id });
        }
        file.States.Add(idle);
        return file;
    }
}
