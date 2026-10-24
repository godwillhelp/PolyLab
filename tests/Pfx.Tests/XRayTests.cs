using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Pfx.Sim.XRay;
using Pfx.XRay;

namespace Pfx.Tests;

// P9: X-Ray import. Synthetic files (built here, no donor content) for the readers, the scanner, the index and the
// simulator; the golden and acceptance tests run on the user's own X-Ray folders when the environment names them:
//   PFX_XRAY_JSON     = an "xray.py --json" model of the full pipeline (parse = xray.py parse_effect)
//   PFX_XRAY_GOLDEN   = an "xray.py --json" model with replays (tools/ParticleStudio/xray/golden.py; simulator = xray.py simulate)
//   PFX_XRAY_INPUTS   = folders to scan, ';' (e.g. a Downloads folder with X-Ray mods and an unpacked gamedata)
//   PFX_XRAY_EXPECTED = tools/ParticleStudio/xray/expected.py output for those folders (roots and counts per tag of xray.py)
public class XRayTests
{
    private static readonly Encoding Cp1251;

    static XRayTests()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Cp1251 = Encoding.GetEncoding(1251);
    }

    private static string Temp()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pfx_xr_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Put(string root, string rel, byte[] bytes)
    {
        string p = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, bytes);
    }

    private static void Put(string root, string rel, string text) => Put(root, rel, Cp1251.GetBytes(text));

    // A .pe dump with one source (point domain, given rate) and a move, keys numbered as the editor writes them.
    private static string Pe(double rate = 50, int max = 100, string texture = "fx\\spark", int flags = 1, string extra = "", int extraCount = 0)
    {
        return $"""
            [_effect]
            action_count = {2 + extraCount}
            flags = {flags}
            max_particles = {max}
            version = 1

            [action_0000]
            action_name = source
            action_type = 21
            bool_0000 = off
            bool_0001 = on
            flags = 1
            flt_0000 = 0
            flt_0001 = 1
            flt_0002 = 0
            flt_0003 = {rate.ToString(System.Globalization.CultureInfo.InvariantCulture)}
            flt_0004 = 0
            version = 1

            [action_0001]
            action_name = move
            action_type = 12
            flags = 1
            version = 1
            {extra}
            [domain_action_0000_0000]
            type = 0
            v0 = 1, 1, 1
            v1 = 0, 0, 0
            v2 = 0, 0, 0

            [domain_action_0000_0001]
            type = 0
            v0 = 0, 0, 0
            v1 = 0, 0, 0
            v2 = 0, 0, 0

            [domain_action_0000_0002]
            type = 0
            v0 = 0, 0, 0
            v1 = 0, 0, 0
            v2 = 0, 0, 0

            [domain_action_0000_0003]
            type = 0
            v0 = 0.5, 0.5, 0.5
            v1 = 0, 0, 0
            v2 = 0, 0, 0

            [domain_action_0000_0004]
            type = 0
            v0 = 0, 1, 0
            v1 = 0, 0, 0
            v2 = 0, 0, 0

            [sprite]
            shader = particles\add
            texture = {texture}
            """;
    }

    private static string Pg(params (string Effect, double T0, double T1, int Flags, string Birth, string Death)[] items)
    {
        var sb = new StringBuilder($"[_group]\neffects_count = {items.Length}\nflags = 0\ntimelimit = 0\nversion = 3\n");
        for (int i = 0; i < items.Length; i++)
        {
            var it = items[i];
            sb.Append($"\n[effect_{i:D4}]\neffect_name = {it.Effect}\nflags = {it.Flags}\non_birth_child = {it.Birth}\non_death_child = {it.Death}\non_play_child = \ntime0 = {it.T0.ToString(System.Globalization.CultureInfo.InvariantCulture)}\ntime1 = {it.T1.ToString(System.Globalization.CultureInfo.InvariantCulture)}\n");
        }
        return sb.ToString();
    }

    // Two Ogg pages: the identification packet and the comment packet whose first user comment carries the X-Ray
    // parameters (u32 version, f32 min, f32 max, f32 volume).
    private static byte[] Ogg(float min, float max, float volume)
    {
        byte[] Page(byte[] packet, int seq)
        {
            var lacing = new List<byte>();
            int left = packet.Length;
            while (left >= 255)
            {
                lacing.Add(255);
                left -= 255;
            }
            lacing.Add((byte)left);
            var page = new List<byte>(Encoding.ASCII.GetBytes("OggS")) { 0, (byte)(seq == 0 ? 2 : 0) };
            page.AddRange(new byte[8]);
            page.AddRange(BitConverter.GetBytes(1u));
            page.AddRange(BitConverter.GetBytes((uint)seq));
            page.AddRange(new byte[4]);
            page.Add((byte)lacing.Count);
            page.AddRange(lacing);
            page.AddRange(packet);
            return page.ToArray();
        }
        var ident = new List<byte> { 1 };
        ident.AddRange(Encoding.ASCII.GetBytes("vorbis"));
        ident.AddRange(new byte[23]);
        var comment = new List<byte> { 3 };
        comment.AddRange(Encoding.ASCII.GetBytes("vorbis"));
        var vendor = Encoding.ASCII.GetBytes("pfx");
        comment.AddRange(BitConverter.GetBytes((uint)vendor.Length));
        comment.AddRange(vendor);
        comment.AddRange(BitConverter.GetBytes(1u));
        var xr = new List<byte>();
        xr.AddRange(BitConverter.GetBytes(3u));
        xr.AddRange(BitConverter.GetBytes(min));
        xr.AddRange(BitConverter.GetBytes(max));
        xr.AddRange(BitConverter.GetBytes(volume));
        comment.AddRange(BitConverter.GetBytes((uint)xr.Count));
        comment.AddRange(xr);
        return Page(ident.ToArray(), 0).Concat(Page(comment.ToArray(), 1)).ToArray();
    }

    private static byte[] Dds(int w, int h)
    {
        var b = new byte[128 + w * h / 2];
        Encoding.ASCII.GetBytes("DDS ").CopyTo(b, 0);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), 124);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(12), h);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(16), w);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(80), 4);
        Encoding.ASCII.GetBytes("DXT1").CopyTo(b, 84);
        return b;
    }

    private static byte[] Chunk(uint id, byte[] body) => BitConverter.GetBytes(id).Concat(BitConverter.GetBytes((uint)body.Length)).Concat(body).ToArray();

    private static byte[] Lanims(string name, float fps, int frames, params (int Frame, uint Argb)[] keys)
    {
        var common = Cp1251.GetBytes(name).Append((byte)0).Concat(BitConverter.GetBytes(fps)).Concat(BitConverter.GetBytes((uint)frames)).ToArray();
        var k = BitConverter.GetBytes((uint)keys.Length).Concat(keys.SelectMany(x => BitConverter.GetBytes((uint)x.Frame).Concat(BitConverter.GetBytes(x.Argb)))).ToArray();
        var item = Chunk(1, common).Concat(Chunk(2, k)).ToArray();
        return Chunk(0, BitConverter.GetBytes((ushort)1)).Concat(Chunk(1, Chunk(0, item))).ToArray();
    }

    [Fact]
    public void Recognizes_files_by_content_not_by_name()
    {
        Assert.Equal(XrKind.Effect, XrRecognize.Recognize(Cp1251.GetBytes(Pe()), 1000).Kind);
        Assert.Equal(XrKind.Group, XrRecognize.Recognize(Cp1251.GetBytes(Pg(("a", 0, 0, 4, "", ""))), 1000).Kind);
        var zone = XrRecognize.Recognize(Cp1251.GetBytes("[zone_x]:zone_base\nidle_particles = a\\b ; comment\nblowout_sound = s\n"), 60);
        Assert.Equal(XrKind.Config, zone.Kind);
        Assert.Equal(1, zone.Facts!.Zones);
        var ogg = XrRecognize.Recognize(Ogg(2, 30, 1.5f), 200);
        Assert.Equal(XrKind.Sound, ogg.Kind);
        Assert.Equal(new XrSoundParams(3, 2, 30, 1.5), ogg.Facts!.Sound);
        var dds = XrRecognize.Recognize(Dds(64, 32), 1152);
        Assert.Equal((XrKind.Texture, 64, 32, "DXT1"), (dds.Kind, dds.Facts!.Width, dds.Facts.Height, dds.Facts.Format));
        var lib = Chunk(1, BitConverter.GetBytes((ushort)1)).Concat(Chunk(3, Array.Empty<byte>())).ToArray();
        Assert.Equal(XrKind.Library, XrRecognize.Recognize(lib, lib.Length).Kind);
        var lan = Lanims("x", 10, 10, (0, 0xFF102030));
        Assert.Equal(XrKind.Lanims, XrRecognize.Recognize(lan, lan.Length).Kind);
        var xdb = Chunk(0x29A, Encoding.ASCII.GetBytes("[header]\r\nauto_load = true\r\n"));
        Assert.Equal(XrKind.Archive, XrRecognize.Recognize(xdb, xdb.Length).Kind);
        var sqlite = Encoding.ASCII.GetBytes("SQLite format 3\0").Concat(new byte[100]).ToArray();
        Assert.NotEqual(XrKind.Archive, XrRecognize.Recognize(sqlite, sqlite.Length).Kind);
        var zip = new byte[] { (byte)'P', (byte)'K', 3, 4, 0, 0, 0, 0 };
        Assert.Equal((XrKind.OtherArchive, "otherarchive"), (XrRecognize.Recognize(zip, 8).Kind, XrRecognize.Recognize(zip, 8).Why));
        var sevenZip = new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0, 4 };
        Assert.Equal(XrKind.OtherArchive, XrRecognize.Recognize(sevenZip, 8).Kind);
        Assert.Equal(XrKind.Script, XrRecognize.Recognize(Encoding.ASCII.GetBytes("function a()\n  return 1\nend\n"), 30).Kind);
        Assert.Equal(XrKind.Interface, XrRecognize.Recognize(Encoding.ASCII.GetBytes("<?xml version=\"1.0\"?><w/>"), 25).Kind);
    }

    [Fact]
    public void Scanner_finds_roots_at_any_depth_groups_core_and_options_and_lists_what_it_skipped()
    {
        string dir = Temp();
        try
        {
            Put(dir, @"Mods\MyMod\01 Core\gamedata\particles\fx\a.pe", Pe());
            Put(dir, @"Mods\MyMod\01 Core\gamedata\particles\fx\b.pe", Pe());
            Put(dir, @"Mods\MyMod\01 Core\gamedata\textures\fx\spark.dds", Dds(8, 8));
            Put(dir, @"Mods\MyMod\02 Options\Fast\gamedata\particles\fx\a.pe", Pe(rate: 99));
            Put(dir, @"Loose\particles\other\c.txt", Pe());
            Put(dir, @"Loose\sounds\hum.ogg", Ogg(1, 10, 1));
            Put(dir, @"stray.ogg", Ogg(1, 10, 1));
            Put(dir, @"pack.zip", new byte[] { (byte)'P', (byte)'K', 3, 4, 1, 2 });
            Put(dir, @"Mods\MyMod\01 Core\gamedata\scripts\x.script", "function a()\nend\n");
            var (r, _) = XrScanner.Scan(dir, null, null, CancellationToken.None);
            Assert.False(r.Nothing);
            var mods = r.Mods.ToDictionary(m => m.Name);
            Assert.Equal(new[] { "Loose", "Mods" }, mods.Keys.OrderBy(k => k));
            var mod = mods["Mods"];
            Assert.Equal(@"MyMod\01 Core", mod.Core.Name);
            Assert.True(mod.Core.Core);
            var option = mod.Roots[1];
            Assert.Equal(@"MyMod\02 Options\Fast", option.Name);
            Assert.Equal(1, option.OverridesCore);
            Assert.Equal(2, mod.Core.Effects);
            Assert.Equal(Path.Combine(dir, "Loose"), mods["Loose"].Core.Path);
            Assert.Equal(1, mods["Loose"].Core.Effects);
            Assert.Equal(1, r.Skipped["outside"].Count);
            Assert.Equal(1, r.Skipped["otherarchive"].Count);
            Assert.Equal(1, r.Skipped["script"].Count);
            Assert.Equal(1, r.SoundsWithParams);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void A_folder_without_xray_content_reports_nothing_usable_with_the_skipped_list()
    {
        string dir = Temp();
        try
        {
            Put(dir, "notes.txt", "hello\nworld\n");
            Put(dir, "photo.jpg", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46, 0x49, 0x46, 0 });
            Put(dir, @"sub\data.db", Encoding.ASCII.GetBytes("SQLite format 3\0").Concat(new byte[64]).ToArray());
            var (r, _) = XrScanner.Scan(dir, null, null, CancellationToken.None);
            Assert.True(r.Nothing);
            Assert.Empty(r.Mods);
            Assert.Equal(3, r.Skipped.Values.Sum(s => s.Count));
            Assert.Contains(r.Skipped, s => s.Value.Examples.Contains(@"sub\data.db"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Rescan_reads_only_files_whose_size_or_time_changed_and_tells_real_changes_by_hash()
    {
        string dir = Temp();
        try
        {
            Put(dir, @"m\gamedata\particles\a.pe", Pe());
            Put(dir, @"m\gamedata\particles\b.pe", Pe());
            Put(dir, @"m\gamedata\particles\c.pe", Pe());
            var (first, cache) = XrScanner.Scan(dir, null, null, CancellationToken.None);
            Assert.Equal(3, first.Read);
            var (again, cache2) = XrScanner.Scan(dir, cache, null, CancellationToken.None);
            Assert.Equal((0, 3), (again.Read, again.Reused));
            Put(dir, @"m\gamedata\particles\a.pe", Pe(rate: 7));
            string b = Path.Combine(dir, @"m\gamedata\particles\b.pe");
            File.SetLastWriteTimeUtc(b, File.GetLastWriteTimeUtc(b).AddMinutes(1));
            var (third, _) = XrScanner.Scan(dir, cache2, null, CancellationToken.None);
            Assert.Equal((2, 1, 1), (third.Read, third.Reused, third.SameContent));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Ltx_inheritance_later_keys_patches_and_deletions()
    {
        var cfg = new LtxConfig();
        cfg.Add(XrLtx.Read(Cp1251.GetBytes("[base]\na = 1\nb = 2\nc = 3 // comment\n[mid]:base\nb = 20\nb = 21\n"), "base.ltx"));
        cfg.Add(XrLtx.Read(Cp1251.GetBytes("[other]\nd = 4\n[zone]:mid, other\nidle_particles = x\\y ;comment\n[lost]:nowhere\n"), "zone.ltx"));
        cfg.Add(XrLtx.Read(Cp1251.GetBytes("![zone]\na = 100\n!d\n@[zone]\nidle_particles = never\n"), "mod_patch.ltx"));
        var z = cfg.Resolve("zone")!;
        Assert.Equal("100", z["a"]);
        Assert.Equal("21", z["b"]);
        Assert.Equal("3", z["c"]);
        Assert.False(z.ContainsKey("d"));
        Assert.Equal("x\\y", z["idle_particles"]);
        Assert.Equal("x/y", XrAnomalyPlanner.Ref(z["idle_particles"]));
        Assert.Equal(new[] { "nowhere" }, cfg.MissingParents("lost"));
    }

    [Fact]
    public void Light_animation_reader_and_the_engine_colour_rule()
    {
        var anims = XrLanims.Read(Lanims("pulse", 10, 20, (0, 0xFF000000), (10, 0xFFFF8000)));
        var a = Assert.Single(anims);
        Assert.Equal(("pulse", 10.0, 20, 2.0), (a.Name, a.Fps, a.Frames, a.Seconds));
        var mid = a.ColourAt(0.5);
        Assert.Equal(0.5, mid.R, 3);
        Assert.Equal(0.25098, mid.G, 3);
        var held = a.ColourAt(1.5);
        Assert.Equal((1.0, 128 / 255.0), (held.R, held.G));
        Assert.Equal(a.ColourAt(0.5), a.ColourAt(2.5));
    }

    private static XrSourceSet TwoSources(string dir)
    {
        var set = new XrSourceSet();
        set.Sources.Add(new XrSource { Tag = "aa", Name = "A", Roots = { new XrSourceRoot { Path = Path.Combine(dir, "a_core"), Id = "core", Core = true, Selected = true },
            new XrSourceRoot { Path = Path.Combine(dir, "a_opt"), Id = "opt", Selected = true } } });
        set.Sources.Add(new XrSource { Tag = "bb", Name = "B", Roots = { new XrSourceRoot { Path = Path.Combine(dir, "b_core"), Id = "core", Core = true, Selected = true } } });
        return set;
    }

    private static XrIndex IndexOf(string dir, XrSourceSet set)
    {
        var (_, cache) = XrScanner.Scan(dir, null, null, CancellationToken.None);
        return XrIndex.Build(set, cache.List, null, CancellationToken.None);
    }

    [Fact]
    public void Index_finds_particles_in_xray_py_order_and_names_the_winner()
    {
        string dir = Temp();
        try
        {
            Put(dir, @"a_core\particles\fx\a.pe", Pe(rate: 1));
            Put(dir, @"a_opt\particles\fx\a.pe", Pe(rate: 2));
            Put(dir, @"a_core\particles\fx\only_core.pe", Pe(texture: "fx\\core_tex"));
            Put(dir, @"b_core\particles\fx\a.pe", Pe(rate: 3));
            Put(dir, @"b_core\particles\fx\from_b.pe", Pe());
            Put(dir, @"b_core\textures\fx\spark.dds", Dds(8, 8));
            var set = TwoSources(dir);
            var index = IndexOf(dir, set);
            var a = set.Source("aa")!;
            double Rate(string? variant, string name) => index.Effect(a, variant, name)!.Actions[0].F("Rate");
            Assert.Equal(2, Rate("opt", "fx/a"));
            Assert.Equal(1, Rate(null, "fx/a"));
            Assert.Equal(3, index.Effect(set.Source("bb")!, null, "fx/a")!.Actions[0].F("Rate"));
            Assert.NotNull(index.Effect(a, null, "fx/from_b"));
            Assert.NotNull(index.Find(a, null, "textures/fx/spark.dds"));
            var entry = index.Entry(XrEntryKind.Effect, "particles/fx/a.pe")!;
            Assert.Equal(new[] { "opt", "core", "core" }, entry.Providers.Select(p => p.Root.Id));
            Assert.Equal("aa", entry.Winner.Source.Tag);
            Assert.Equal(new[] { "particles/fx/only_core.pe" }, index.Search("core_tex", null, null).Select(e => e.Key));
            Assert.Equal((2, 0), index.CountsPerTag()["aa"]);
            Assert.Equal((2, 0), index.CountsPerTag()["bb"]);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Everything_for_a_zone_collects_groups_children_textures_sounds_lights_and_timings()
    {
        string dir = Temp();
        try
        {
            Put(dir, @"a_core\configs\zones\zone_x.ltx", "[zone_x]:zone_base\nidle_particles = fx\\grp\nblowout_particles = fx\\gone\nidle_sound = fx\\hum\n" +
                "blowout_light = on\nlight_color = 1,0.5,0\nidle_light_anim = pulse\nblowout_time = 3000\npostprocess = pp_x\nhit_type = shock\n");
            Put(dir, @"b_core\configs\zones\zone_base.ltx", "[zone_base]\neffective_radius = 1\nawaking_time = 50\n");
            Put(dir, @"a_core\particles\fx\grp.pg", Pg(("fx/a", 0, 0, 4 | 32, "fx/kid", ""), ("fx/off", 0, 0, 0, "", "")));
            Put(dir, @"a_core\particles\fx\a.pe", Pe(texture: "fx\\spark"));
            Put(dir, @"a_core\particles\fx\kid.pe", Pe(texture: "fx\\missing_one"));
            Put(dir, @"a_core\textures\fx\spark.dds", Dds(8, 8));
            Put(dir, @"a_core\textures\fx\missing_one.2048.dds", Dds(8, 8));
            Put(dir, @"a_core\sounds\fx\hum.ogg", Ogg(2, 40, 1.25f));
            Put(dir, @"b_core\lanims.xr", Lanims("pulse", 5, 10, (0, 0xFF0000FF)));
            var set = TwoSources(dir);
            var index = IndexOf(dir, set);
            Assert.NotNull(index.Entry(XrEntryKind.Zone, "zone_x"));
            var plan = XrAnomalyPlanner.Plan(index, set.Source("aa")!, null, "zone_x", new Dictionary<string, string>());
            var idle = plan.Particles.Single(p => p.Role == "idle_particles");
            Assert.True(idle.Group);
            Assert.Equal(new[] { "fx/a", "fx/kid" }, idle.Effects.Select(e => e.Name));
            Assert.Equal(("fx/a", "on_birth_child"), (idle.Effects[1].ChildOf, idle.Effects[1].ChildKind));
            Assert.NotNull(idle.Effects[0].Texture!.File);
            Assert.Null(idle.Effects[1].Texture!.File);
            Assert.EndsWith("missing_one.2048.dds", idle.Effects[1].Texture!.Proposal);
            Assert.Equal(new[] { "fx/gone" }, plan.Particles.Single(p => p.Role == "blowout_particles").Missing);
            Assert.Equal(new XrSoundParams(3, 2, 40, 1.25), plan.Sounds.Single().Params);
            Assert.Equal("pulse", plan.Lights.Single(l => l.Role == "idle_light").Anim!.Name);
            Assert.Equal("1,0.5,0", plan.Lights.Single(l => l.Role == "blowout_light").Keys["light_color"]);
            Assert.Equal(("3000", "50"), (plan.Timings["blowout_time"], plan.Timings["awaking_time"]));
            Assert.Equal(("pp_x", "shock", "1"), (plan.Postprocess, plan.Hit["hit_type"], plan.Hit["effective_radius"]));
            Assert.Empty(plan.MissingParents);
            Assert.Equal((1, 2), (plan.TexturesFound, plan.TexturesTotal));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static XrEffect Effect(string text) => XrEffect.Parse(XrText.ReadIni(Cp1251.GetBytes(text)), "test.pe");

    [Fact]
    public void Source_emits_its_rate_per_second_with_the_fraction_dithered_and_stops_at_max()
    {
        var sim = new XrEffectSim(Effect(Pe(rate: 45, max: 100000)), new XrRandom(7));
        sim.Play();
        for (int i = 0; i < 300; i++)
            sim.Step();
        Assert.InRange(sim.Count, 45 * 9.9 * 0.97, 45 * 9.9 * 1.03);
        var capped = new XrEffectSim(Effect(Pe(rate: 1000, max: 40)), new XrRandom(7));
        capped.Play();
        for (int i = 0; i < 30; i++)
            capped.Step();
        Assert.Equal(40, capped.Count);
        Assert.All(capped.Particles.ToArray(), p => Assert.Equal(p.Age, p.Pos.Y, 9));
    }

    [Fact]
    public void Effect_time_limit_silences_the_source_and_the_effect_ends_with_its_last_particle()
    {
        string killold = "\n[action_0002]\naction_name = killold\naction_type = 10\nbool_0000 = off\nflags = 1\nflt_0000 = 1.5\nversion = 1\n";
        var e = Effect(Pe(rate: 30, flags: 1 | XrEffect.TimeLimit, extra: killold, extraCount: 1) + "\n[timelimit]\nvalue = 1\n");
        var sim = new XrEffectSim(e, new XrRandom(1));
        sim.Play();
        int steps = 0;
        while (sim.Playing && steps < 1000)
        {
            sim.Step();
            steps++;
            if (steps * 0.033 > 1.05)
                Assert.True(sim.Silent);
        }
        Assert.False(sim.Playing);
        Assert.InRange(steps * 0.033, 1.0 + 1.5 - 0.1, 1.0 + 1.5 + 0.1);
    }

    [Fact]
    public void Group_starts_items_at_t0_stops_them_at_t1_and_starts_birth_children()
    {
        var effects = new Dictionary<string, XrEffect>
        {
            ["late"] = Effect(Pe(rate: 30)),
            ["kid"] = Effect(Pe(rate: 1000, max: 1, flags: 1 | XrEffect.TimeLimit) + "\n[timelimit]\nvalue = 0.2\n"),
        };
        var group = XrGroup.Parse(XrText.ReadIni(Cp1251.GetBytes(Pg(("late", 0.5, 1.0, 4 | 32, "kid", "")))));
        var sim = new XrGroupSim(group, n => effects.GetValueOrDefault(n), new XrRandom(3), new D3(10, 0, 0));
        sim.Play();
        int Live() => sim.Effects().First().Count;
        for (int i = 0; i < 14; i++)
            sim.Frame(33);
        Assert.Equal(0, Live());
        for (int i = 0; i < 6; i++)
            sim.Frame(33);
        Assert.True(Live() > 0);
        Assert.True(sim.Effects().Count() > 1);
        var child = sim.Effects().Skip(1).First();
        Assert.Equal(10, child.Particles[0].Pos.X, 6);
        for (int i = 0; i < 20; i++)
            sim.Frame(33);
        Assert.Equal(0, Live());
        Assert.Empty(sim.Problems);
    }

    // ---- golden tests against xray.py (environment) ----

    private static readonly Dictionary<string, Dictionary<string, Dictionary<string, Dictionary<string, string>>>> Libraries = new(StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, Dictionary<string, string>> SectionsOf(string path)
    {
        int at = path.IndexOf("particles.xr/", StringComparison.OrdinalIgnoreCase);
        if (at < 0)
            return XrText.ReadIni(File.ReadAllBytes(path));
        string lib = path[..(at + "particles.xr".Length)];
        if (!Libraries.TryGetValue(lib, out var entries))
            Libraries[lib] = entries = XrLibrary.Read(File.ReadAllBytes(lib));
        return entries["particles/" + path[(at + "particles.xr/".Length)..]];
    }

    private static D3 V(JsonElement e) => new(e[0].GetDouble(), e[1].GetDouble(), e[2].GetDouble());

    private static void Same(JsonElement expected, XrEffect e)
    {
        Assert.Equal(expected.GetProperty("max").GetInt32(), e.Max);
        Assert.Equal(expected.GetProperty("flags").GetInt32(), e.Flags);
        Assert.Equal(expected.GetProperty("step").GetDouble(), e.Step);
        Assert.Equal(expected.GetProperty("shader").GetString(), e.Shader);
        Assert.Equal(expected.GetProperty("texture").GetString(), e.Texture);
        var frame = expected.GetProperty("frame");
        Assert.Equal(frame.ValueKind == JsonValueKind.Null, e.Frame == null);
        if (e.Frame != null)
            Assert.Equal((frame.GetProperty("tile")[0].GetInt32(), frame.GetProperty("tile")[1].GetInt32(), frame.GetProperty("count").GetInt32(), frame.GetProperty("speed").GetDouble()),
                (e.Frame.TileU, e.Frame.TileV, e.Frame.Count, e.Frame.Speed));
        double? N(string k) => expected.GetProperty(k).ValueKind == JsonValueKind.Null ? null : expected.GetProperty(k).GetDouble();
        Assert.Equal(N("timelimit"), e.TimeLimitValue);
        Assert.Equal(N("collision"), e.CollisionResilience);
        var actions = expected.GetProperty("actions").EnumerateArray().ToList();
        Assert.Equal(actions.Count, e.Actions.Count);
        for (int i = 0; i < actions.Count; i++)
        {
            var x = actions[i];
            var a = e.Actions[i];
            Assert.Equal((x.GetProperty("type").GetString(), x.GetProperty("name").GetString()), (a.Type, a.Name));
            foreach (var p in x.EnumerateObject().Where(p => p.Name is not ("type" or "name" or "enabled")))
            {
                if (a.Floats.TryGetValue(p.Name, out var f))
                    Assert.Equal(p.Value.GetDouble(), f);
                else if (a.Vectors.TryGetValue(p.Name, out var v))
                    Assert.Equal(V(p.Value), v);
                else if (a.Bools.TryGetValue(p.Name, out var b))
                    Assert.Equal(p.Value.GetBoolean(), b);
                else if (a.Ints.TryGetValue(p.Name, out var n))
                    Assert.Equal(p.Value.GetInt32(), n);
                else if (a.Domains.TryGetValue(p.Name, out var d))
                {
                    Assert.Equal(p.Value.GetProperty("domain").GetInt32(), d.Type);
                    Assert.Equal(p.Value.GetProperty("values").EnumerateArray().Select(y => y.GetDouble()), d.Values);
                }
                else
                    Assert.Fail($"{e.Path}: parameter {p.Name} of {a.Type} not parsed");
            }
        }
    }

    [Fact]
    public void Parse_equals_xray_py_parse_effect_for_every_effect_of_the_pipeline()
    {
        var path = Environment.GetEnvironmentVariable("PFX_XRAY_JSON");
        if (string.IsNullOrWhiteSpace(path))
            return;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        int n = 0;
        foreach (var p in doc.RootElement.GetProperty("particles").EnumerateArray())
            foreach (var em in p.GetProperty("emitters").EnumerateArray())
            {
                var eff = em.GetProperty("effect");
                string file = eff.GetProperty("path").GetString()!;
                Same(eff, XrEffect.Parse(SectionsOf(file), file));
                n++;
            }
        Assert.True(n > 0);
    }

    [Fact]
    public void Simulator_replays_the_xray_py_particles_step_for_step()
    {
        var path = Environment.GetEnvironmentVariable("PFX_XRAY_GOLDEN");
        if (string.IsNullOrWhiteSpace(path))
            return;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        int compared = 0;
        foreach (var p in doc.RootElement.GetProperty("particles").EnumerateArray())
            foreach (var em in p.GetProperty("emitters").EnumerateArray())
            {
                if (em.GetProperty("replay") is not { ValueKind: JsonValueKind.Object } replay || !replay.TryGetProperty("rec", out var rec))
                    continue;
                string file = em.GetProperty("effect").GetProperty("path").GetString()!;
                var eff = XrEffect.Parse(SectionsOf(file), file);
                double start = eff.Actions.First(a => a.Type == "source").F("Starting Age");
                var birth = replay.GetProperty("birth");
                int count = birth.GetProperty("pos").GetArrayLength();
                var parts = Enumerable.Range(0, count).Select(i => new XrParticle
                {
                    Pos = V(birth.GetProperty("pos")[i]), PosB = V(birth.GetProperty("pos")[i]), Vel = V(birth.GetProperty("vel")[i]), Rot = birth.GetProperty("rot")[i][0].GetDouble(),
                    Size = V(birth.GetProperty("size")[i]), R = birth.GetProperty("rgba")[i][0].GetDouble(), G = birth.GetProperty("rgba")[i][1].GetDouble(),
                    B = birth.GetProperty("rgba")[i][2].GetDouble(), A = birth.GetProperty("rgba")[i][3].GetDouble(), Age = start,
                }).ToList();
                var sim = new XrEffectSim(eff, new XrRandom(1)) { IgnoreTimeLimit = true };
                sim.Load(parts);
                double dt = sim.StepMs / 1000.0;
                var steps = rec.EnumerateArray().ToList();
                for (int k = 0; k < steps.Count; k++)
                {
                    var before = sim.Particles.ToArray().ToDictionary(x => x.Id, x => x.Rot);
                    sim.Step();
                    var now = sim.Particles.ToArray().ToDictionary(x => x.Id);
                    var row = steps[k].EnumerateArray().ToList();
                    for (int id = 0; id < count; id++)
                    {
                        bool alive = row[id][0].ValueKind != JsonValueKind.Null;
                        Assert.True(alive == now.ContainsKey(id), $"{file} step {k} particle {id}: alive {alive} in xray.py");
                        if (!alive)
                            continue;
                        var m = now[id];
                        double[] mine = { m.Age - start, Math.Abs(m.Size.X), Math.Abs(m.Size.Y), m.R, m.G, m.B, m.A, (m.Rot - before[id]) / dt, m.Vel.Length, m.Pos.Y };
                        for (int c = 0; c < 10; c++)
                        {
                            double want = row[id][c].GetDouble();
                            Assert.True(Math.Abs(mine[c] - want) <= 1e-9 * Math.Max(1, Math.Abs(want)), $"{file} step {k} particle {id} channel {c}: {mine[c]} vs xray.py {want}");
                        }
                        compared++;
                    }
                }
            }
        Assert.True(compared > 0);
    }

    // ---- acceptance on the user's folders (environment) ----

    [Fact]
    public void Scanner_finds_every_root_of_xray_py_with_its_tag_and_the_index_counts_equal_per_tag()
    {
        var inputs = Environment.GetEnvironmentVariable("PFX_XRAY_INPUTS");
        var expectedFile = Environment.GetEnvironmentVariable("PFX_XRAY_EXPECTED");
        if (string.IsNullOrWhiteSpace(inputs) || string.IsNullOrWhiteSpace(expectedFile))
            return;
        var expected = XrExpected.Load(expectedFile);
        var scans = inputs.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(f => XrScanner.Scan(f, null, null, CancellationToken.None)).ToList();
        var checks = expected.CheckRoots(scans.Select(s => s.Result));
        Assert.All(checks, c => Assert.True(c.Ok, $"{c.Expected.Tag}:{c.Expected.Variant} {c.Expected.Path}: found {c.Found?.Name ?? "nothing"}, tag '{c.Tag}'"));
        var set = expected.Sources(scans.Select(s => s.Result));
        var index = XrIndex.Build(set, root => scans.Select(s => s.Cache).Where(c => c.Holds(root)).OrderByDescending(c => c.Folder.Length).Take(1).SelectMany(c => c.List(root)),
            null, CancellationToken.None);
        var counts = index.CountsPerTag();
        foreach (var (tag, want) in expected.Counts)
            Assert.Equal(want, counts[tag]);
    }
}
