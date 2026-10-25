using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using PolyLab.PBOGPacker.Core;
using PolyLab.PBOGPacker.Core.P3d;

namespace PolyLab.PBOGPacker.Tests;

// Models\make_models.py builds plbox (skeleton, animations, a section, a proxy, point flags, bone weights, a second UV
// set, masses) and plplain (the same without model.cfg) from scratch and binarizes them; both the MLOD sources and the
// binarized models are committed. A conversion must give back what the source says, less what binarize drops.
public sealed class OdolTests
{
    private static string Models([CallerFilePath] string here = "") => Path.Combine(Path.GetDirectoryName(here)!, "Models");

    private static string Source(string name, string sub) => Path.Combine(Models(), sub, "polylab", "test", name + ".p3d");

    [Fact]
    public void The_own_skeletal_model_reads_with_its_skeleton_animations_and_mass()
    {
        var m = Odol.Read(Path.Combine(Models(), "plbox.p3d"));
        Assert.Equal(56, m.Version);
        Assert.Equal(new[] { "0", "1", "Memory", "Geometry" }, m.Lods.Select(l => Odol.LodName(l.Resolution)));
        Assert.Equal("plbox_skel", m.Skeleton);
        Assert.Equal(new[] { ("door", ""), ("lid", "door") }, m.Bones);
        Assert.Equal(36.5f, m.Mass);
        var rot = m.Animations.Single(a => a.Name == "door_rot");
        Assert.Equal((0, "user", 1), (rot.Type, rot.Source, rot.Address));
        Assert.Equal(new[] { 0f, 1.5707964f }, rot.Values);
        var slide = m.Animations.Single(a => a.Name == "lid_slide");
        Assert.Equal((4, 2, 0.5f), (slide.Type, slide.Address, slide.Values[1]));
        Assert.Equal(new Vector3(0, 0, 2), slide.Axis[0]!.Value.Dir);
        Assert.Equal(0.5f, m.Animations.Single(a => a.Name == "door_hide").Values.Single());
        Assert.Equal(@"\polylab\test\plproxy", m.Lods[0].Proxies.Single().Name);
        Assert.Contains(m.Lods[0].Selections, s => s.Name == "camo" && s.Sectional);
    }

    [Fact]
    public void Every_cut_of_the_model_fails_as_damaged_data()
    {
        var data = File.ReadAllBytes(Path.Combine(Models(), "plbox.p3d"));
        for (var n = 0; n < data.Length; n += 37)
            Assert.Throws<InvalidDataException>(() => Odol.Read(data[..n]));
        var older = (byte[])data.Clone();
        BitConverter.GetBytes(53).CopyTo(older, 4);
        Assert.Throws<InvalidDataException>(() => Odol.Read(older));
    }

    [Fact]
    public void The_converted_model_gives_back_the_source_geometry_flags_and_selections()
    {
        var ours = ReadMlod(Mlod.Write(Odol.Read(Path.Combine(Models(), "plbox.p3d")), [], []));
        var src = ReadMlod(File.ReadAllBytes(Source("plbox", "src")));
        Assert.Equal(src.Select(l => l.Res), ours.Select(l => l.Res));
        for (var i = 0; i < src.Count; i++)
        {
            Assert.Equal(src[i].Points.Select(p => (Key(p.Pos), p.Flags)).Order(), ours[i].Points.Select(p => (Key(p.Pos), p.Flags)).Order());
            if (i < 2) Assert.Equal(Faces(src[i]), Faces(ours[i]));
        }
        foreach (var (lod, sel) in new[] { (0, "camo"), (0, @"proxy:\polylab\test\plproxy.001"), (1, "camo"), (1, "door"), (2, "axis_door"),
                     (2, "axis_slide"), (3, "component01") })
            Assert.Equal(Selected(src[lod], sel), Selected(ours[lod], sel));
        // Binarize skins every point to one bone at full weight here: the four points in both door and lid went to lid,
        // so the bones come back split between door and lid, and the lid's partial weights are gone.
        var lid = Selected(ours[0], "lid");
        Assert.Equal(4, lid.Count);
        Assert.All(lid, p => Assert.Equal(1, p.W));
        Assert.Equal(Selected(src[0], "door").Select(p => p.Pos).Order(), Selected(ours[0], "door").Concat(lid).Select(p => p.Pos).Order());
        Assert.Equal(new[] { 0, 1 }, ours[0].Tags.Where(t => t.Name == "#UVSet#").Select(t => BitConverter.ToInt32(t.Data, 0)));
    }

    [Fact]
    public void Without_a_model_cfg_class_selections_keep_their_points_and_weights()
    {
        var m = Odol.Read(Path.Combine(Models(), "plplain.p3d"));
        Assert.False(ModelCfg.Needed(m));
        var ours = ReadMlod(Mlod.Write(m, [], []));
        var src = ReadMlod(File.ReadAllBytes(Source("plplain", "src_plain")));
        Assert.Equal(Selected(src[0], "lid"), Selected(ours[0], "lid"));
    }

    [Fact]
    public void Point_masses_give_back_the_stored_mass_centre_of_mass_and_inertia()
    {
        var m = Odol.Read(Path.Combine(Models(), "plbox.p3d"));
        var geo = ReadMlod(Mlod.Write(m, [], []))[3];
        var mass = geo.Tags.Single(t => t.Name == "#Mass#").Data;
        var pts = geo.Points.Select(p => p.Pos - m.BoundingCenter).ToList();
        var ms = Enumerable.Range(0, pts.Count).Select(k => (double)BitConverter.ToSingle(mass, 4 * k)).ToList();
        Assert.All(ms, v => Assert.True(v >= 0));
        Check(pts, ms, m.Mass, m.CenterOfMass, m.InvInertia, 1e-5);
    }

    // Spread, flat (no inertia stored) and sparse targets (mass on 6 of 300 points: the spread solution stalls at the
    // edge, NNLS meets them) are met; targets no non-negative masses reach still keep the total mass and the centre.
    [Theory]
    [InlineData("spread")]
    [InlineData("flat")]
    [InlineData("sparse")]
    public void The_mass_solver_meets_reachable_targets(string kind)
    {
        var rng = new Random(7);
        var n = kind == "sparse" ? 300 : 40;
        var pts = Enumerable.Range(0, n).Select(_ => new Vector3(rng.NextSingle() * 4 - 2, kind == "flat" ? 0 : rng.NextSingle() * 3, rng.NextSingle() * 2 - 1)).ToList();
        var ms = pts.Select((_, k) => kind != "sparse" || k % 50 == 0 ? rng.NextDouble() * 5 + 0.1 : 0).ToList();
        var total = ms.Sum();
        var com = pts.Zip(ms, (p, w) => p * (float)w).Aggregate(Vector3.Zero, (a, b) => a + b) / (float)total;
        var inv = kind == "flat" ? new float[9] : Invert(Inertia(pts, ms, com));
        var r = Mass.Solve(pts, (float)total, com, inv);
        Assert.True(r.MassError < 1e-6 && r.ComError < 1e-5 && r.InertiaError < 1e-6, $"{kind}: {r.MassError} {r.ComError} {r.InertiaError}");
        Assert.All(r.Masses, v => Assert.True(v >= 0));
        Check(pts, r.Masses.Select(v => (double)v).ToList(), (float)total, com, kind == "flat" ? null : inv, 1e-4);
    }

    [Fact]
    public void Unreachable_inertia_keeps_the_total_mass_and_the_centre_of_mass()
    {
        var pts = new List<Vector3>();
        for (var i = 0; i < 8; i++) pts.Add(new Vector3(i & 1, i >> 1 & 1, i >> 2 & 1));
        var com = new Vector3(0.4f, 0.5f, 0.6f);
        var r = Mass.Solve(pts, 10, com, [-1, 0, 0, 0, -1, 0, 0, 0, 1]);
        Assert.True(r.MassError < 1e-6 && r.ComError < 1e-5, $"{r.MassError} {r.ComError}");
        Assert.True(r.InertiaError > 0.1);
        Assert.All(r.Masses, v => Assert.True(v >= 0));
    }

    [Fact]
    public void The_model_cfg_finds_the_axes_and_writes_skeleton_sections_and_animations()
    {
        var m = Odol.Read(Path.Combine(Models(), "plbox.p3d"));
        Assert.True(ModelCfg.Needed(m));
        var added = new List<Mlod.Axis>();
        var notes = new List<string>();
        var axes = ModelCfg.Axes(m, added, notes);
        Assert.Empty(added);
        Assert.Equal(new[] { "axis_door", "axis_slide", null }, axes.Select(a => a?.Name));
        var cfg = ModelCfg.Write(m, "plbox", axes, notes);
        foreach (var line in new[] { "\"door\",\"\",", "\"lid\",\"door\"", "sections[]=", "\"camo\"", "class door_rot", "type=\"rotation\";",
                     "sourceAddress=\"loop\";", "angle1=1.5707964;", "type=\"translation\";", "sourceAddress=\"mirror\";", "offset1=0.5;",
                     "type=\"hide\";", "hideValue=0.5;", "memory=1;" })
            Assert.Contains(line, cfg);
        Assert.Empty(notes);
    }

    [Fact]
    public void An_embedded_material_reads_back_as_its_rvmat()
    {
        var m = Odol.Read(Path.Combine(Models(), "plbox.p3d"));
        var notes = new List<string>();
        foreach (var mat in m.Lods[0].Materials)
        {
            var file = Path.Combine(Models(), "src", mat.Path);
            Assert.Equal(Squash(File.ReadAllText(file)), Squash(Rvmat.Write(mat, notes)));
        }
        Assert.Empty(notes);
    }

    [Fact]
    public void The_reference_check_takes_an_odols_references_from_the_parsed_model()
    {
        var stage = Path.Combine(Path.GetTempPath(), "pbog_odol_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(stage);
            var file = Path.Combine(stage, "plbox.p3d");
            File.Copy(Path.Combine(Models(), "plbox.p3d"), file);
            var refs = new Dictionary<string, IReadOnlyList<(string, bool)>> { [file] = ModelExport.References(Odol.Read(file)).ToList() };
            var r = RefCheck.Scan(stage, @"polylab\test", null, null, refs);
            Assert.Equal(new[] { @"\polylab\test\plproxy.p3d", @"polylab\test\data\box_co.paa" }, r.Missing.Select(f => f.Ref).Order());
            Assert.Equal(new[] { ("plbox.p3d", 2) }, r.OdolOther);
        }
        finally { Directory.Delete(stage, true); }
    }

    private static string Squash(string s) => new(s.Where(c => !char.IsWhiteSpace(c)).ToArray());

    private static (int, int, int) Key(Vector3 p) => ((int)MathF.Round(p.X * 1e4f), (int)MathF.Round(p.Y * 1e4f), (int)MathF.Round(p.Z * 1e4f));

    private static List<string> Faces(MlodLod l) => l.Faces.Select(f =>
    {
        var ring = f.Points.Select(p => Key(l.Points[p].Pos)).ToList();
        var k = Enumerable.Range(0, ring.Count).MinBy(i => string.Join(";", ring.Skip(i).Concat(ring.Take(i))))!;
        return string.Join(";", ring.Skip(k).Concat(ring.Take(k))) + "|" + f.Texture + "|" + f.Material + "|" + f.Flags;
    }).Order().ToList();

    private static List<((int, int, int) Pos, byte W)> Selected(MlodLod l, string name)
    {
        var d = l.Tags.Single(t => t.Name == name).Data;
        return Enumerable.Range(0, l.Points.Count).Where(i => d[i] != 0).Select(i => (Key(l.Points[i].Pos), d[i])).Order().ToList();
    }

    private static void Check(IReadOnlyList<Vector3> pts, IReadOnlyList<double> ms, float mass, Vector3 com, float[]? inv, double tol)
    {
        var total = ms.Sum();
        Assert.True(Math.Abs(total - mass) <= tol * mass, $"mass {total} vs {mass}");
        var c = pts.Zip(ms, (p, w) => p * (float)w).Aggregate(Vector3.Zero, (a, b) => a + b) / (float)total;
        Assert.True((c - com).Length() <= tol * Math.Max(1, com.Length()), $"centre {c} vs {com}");
        if (inv == null) return;
        var got = Invert(Inertia(pts, ms, com));
        for (var i = 0; i < 9; i++) Assert.True(Math.Abs(got[i] - inv[i]) <= tol * Math.Max(1, inv.Max(Math.Abs)), $"inertia {i}: {got[i]} vs {inv[i]}");
    }

    private static double[,] Inertia(IReadOnlyList<Vector3> pts, IReadOnlyList<double> ms, Vector3 com)
    {
        var t = new double[3, 3];
        for (var k = 0; k < pts.Count; k++)
        {
            double[] r = [pts[k].X - com.X, pts[k].Y - com.Y, pts[k].Z - com.Z];
            var rr = r[0] * r[0] + r[1] * r[1] + r[2] * r[2];
            for (var i = 0; i < 3; i++)
                for (var j = 0; j < 3; j++) t[i, j] += ms[k] * ((i == j ? rr : 0) - r[i] * r[j]);
        }
        return t;
    }

    private static float[] Invert(double[,] m)
    {
        double a = m[0, 0], b = m[0, 1], c = m[0, 2], d = m[1, 0], e = m[1, 1], f = m[1, 2], g = m[2, 0], h = m[2, 1], k = m[2, 2];
        var det = a * (e * k - f * h) - b * (d * k - f * g) + c * (d * h - e * g);
        return new[] { e * k - f * h, c * h - b * k, b * f - c * e, f * g - d * k, a * k - c * g, c * d - a * f, d * h - e * g, b * g - a * h, a * e - b * d }
            .Select(v => (float)(v / det)).ToArray();
    }

    private sealed record MlodFace(int[] Points, uint Flags, string Texture, string Material);
    private sealed record MlodLod(float Res, List<(Vector3 Pos, uint Flags)> Points, List<MlodFace> Faces, List<(string Name, byte[] Data)> Tags);

    private static List<MlodLod> ReadMlod(byte[] d)
    {
        var r = new BinaryReader(new MemoryStream(d), Encoding.Latin1);
        Assert.Equal("MLOD", Encoding.ASCII.GetString(r.ReadBytes(4)));
        r.ReadInt32();
        var lods = new List<MlodLod>();
        for (var n = r.ReadInt32(); n > 0; n--)
        {
            Assert.Equal("P3DM", Encoding.ASCII.GetString(r.ReadBytes(4)));
            r.ReadBytes(8);
            int np = r.ReadInt32(), nn = r.ReadInt32(), nf = r.ReadInt32();
            r.ReadInt32();
            var pts = Enumerable.Range(0, np).Select(_ => (new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()), r.ReadUInt32())).ToList();
            r.ReadBytes(12 * nn);
            var faces = new List<MlodFace>();
            for (var f = 0; f < nf; f++)
            {
                var count = r.ReadInt32();
                var p = new int[4];
                for (var k = 0; k < 4; k++)
                {
                    p[k] = r.ReadInt32();
                    r.ReadBytes(12);
                }
                faces.Add(new MlodFace(p[..count], r.ReadUInt32(), Z(r), Z(r)));
            }
            Assert.Equal("TAGG", Encoding.ASCII.GetString(r.ReadBytes(4)));
            var tags = new List<(string, byte[])>();
            while (true)
            {
                r.ReadByte();
                var name = Z(r);
                var data = r.ReadBytes(r.ReadInt32());
                if (name == "#EndOfFile#") break;
                tags.Add((name, data));
            }
            lods.Add(new MlodLod(r.ReadSingle(), pts, faces, tags));
        }
        return lods;
    }

    private static string Z(BinaryReader r)
    {
        var b = new List<byte>();
        for (var c = r.ReadByte(); c != 0; c = r.ReadByte()) b.Add(c);
        return Encoding.Latin1.GetString(b.ToArray());
    }
}
