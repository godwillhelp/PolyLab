using System.Text;
using System.Text.Json.Nodes;
using Pfx.Core.Export;
using Pfx.Core.Model;

namespace Pfx.Tests;

// P10: the export profiles (spec 3.13) on a small project in a temporary folder.
public sealed class ExportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfx_export_" + Guid.NewGuid().ToString("N"));

    public ExportTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "proj", "particles"));
        Directory.CreateDirectory(Path.Combine(_dir, "proj", "cg_xray", "ar", "materials"));
        Directory.CreateDirectory(Path.Combine(_dir, "proj", "sounds"));
        File.WriteAllText(Path.Combine(_dir, "proj", "particles", "flames.ptc"),
            "EffectDef {\r\n Emitors {\r\n  EmitorDef a {\r\n   Material \"{0123456789ABCDEF}cg_xray/ar/materials/Flame.emat\"\r\n  }\r\n" +
            "  EmitorDef b {\r\n   Material \"graphics/particles/materials/fire/fire_01.emat\"\r\n  }\r\n }\r\n}\r\n", Encoding.Latin1);
        File.WriteAllText(Path.Combine(_dir, "proj", "particles", "column.ptc"),
            "EffectDef {\r\n Emitors {\r\n  EmitorDef c {\r\n   Material \"cg_xray/ar/materials/flame.emat\"\r\n  }\r\n }\r\n}\r\n", Encoding.Latin1);
        File.WriteAllText(Path.Combine(_dir, "proj", "cg_xray", "ar", "materials", "flame.emat"),
            "ParticleEffectMaterial {\r\n ColorMap \"cg_xray/ar/materials/flame.edds\"\r\n}\r\n", Encoding.Latin1);
        File.WriteAllBytes(Path.Combine(_dir, "proj", "cg_xray", "ar", "materials", "flame.edds"), new byte[] { 1, 2, 3 });
        File.WriteAllBytes(Path.Combine(_dir, "proj", "sounds", "fire.ogg"), Encoding.ASCII.GetBytes("OggS-test"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, true);
    }

    private string Proj => Path.Combine(_dir, "proj");

    private sealed class Source : IExportSource
    {
        private readonly string _root;

        public Source(string root) => _root = root;

        public string? Resolve(string gamePath)
        {
            string rel = System.Text.RegularExpressions.Regex.Replace(gamePath, "^\\{[0-9A-Fa-f]{16}\\}", "");
            string full = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(full) ? full : null;
        }

        public byte[] Read(string diskPath) => File.ReadAllBytes(diskPath);

        // Mod folders of these tests are not under a data root: the game path falls back to the folder's name.
        public string? GamePath(string diskFolder) => null;
    }

    private ProjectFile Project(string prefix, string mod) => new()
    {
        Name = "zharka",
        Parts = { new PartEntry { Id = "flames", Path = "particles/flames.ptc", Spin = 30 }, new PartEntry { Id = "column", Path = "particles/column.ptc", Stop = 2.5f } },
        Lights = { new LightEntry { Id = "glow", FlickerAmp = 0.5f, FlickerSpeed = 4, Curve = { new[] { 0f, 1f }, new[] { 1f, 0.5f } }, CurveLoop = true } },
        Hazes = { new HazeEntry { Id = "heat", Power = 0.02f } },
        Sounds = { new SoundEntry { Id = "burn", File = "sounds/fire.ogg", Loop = true, Range = 40 }, new SoundEntry { Id = "roar", GameSet = "HeavyFire_SoundSet" } },
        States =
        {
            new StateEntry { Name = "idle", Items = { new() { Ref = "flames" }, new() { Ref = "glow" }, new() { Ref = "heat" }, new() { Ref = "burn" } } },
            new StateEntry { Name = "active", Duration = 3, OverIdle = true, Items = { new() { Ref = "column" }, new() { Ref = "roar" } } },
        },
        Anomaly = { Archetype = "burn", Tick = 0.5f, Shells = { ["hit"] = new Shell { R = 2, YMax = 3 } } },
        Export = { Dayz = { ModFolder = Path.Combine(_dir, mod), Prefix = prefix } },
    };

    private static string Text(ExportPlan plan, string end) => Encoding.UTF8.GetString(plan.Files.Single(f => f.Path.Replace('\\', '/').EndsWith(end)).Bytes);

    [Fact]
    public void The_mod_profile_copies_and_renames_everything_but_vanilla()
    {
        var plan = DayzModExport.Plan(Project("SFA", "SampleA"), Proj, new Source(Proj));
        Assert.True(plan.CanWrite, string.Join("; ", plan.Errors.Select(e => e.Key + " " + string.Join(",", e.Args))));
        string flames = Text(plan, "fx/particles/sfa_zharka_flames.ptc");
        Assert.Contains("\"SampleA/fx/materials/flame.emat\"", flames);
        Assert.Contains("\"graphics/particles/materials/fire/fire_01.emat\"", flames);
        Assert.DoesNotContain("cg_xray", flames);
        Assert.DoesNotContain("{0123456789ABCDEF}", flames);
        // Two parts use the material by two spellings of one file: copied once.
        Assert.Single(plan.Files, f => f.What == "material");
        Assert.Contains("\"SampleA/fx/textures/flame.edds\"", Text(plan, "fx/materials/flame.emat"));
        Assert.Contains(plan.Files, f => f.Path.EndsWith(Path.Combine("fx", "sounds", "zharka_burn.ogg")));
        string config = Text(plan, "fx/config.cpp");
        Assert.Contains("class SFA_Fx", config);
        Assert.Contains("requiredAddons[] = {\"DZ_Data\", \"DZ_Scripts\", \"DZ_Sounds_Effects\"};", config);
        Assert.Contains("samples[] = {{\"SampleA\\fx\\sounds\\zharka_burn\", 1}};", config);
        Assert.Contains("files[] = {\"SampleA/fx/scripts/4_World\"};", config);
        string list = Text(plan, "SFA_FxList.c");
        Assert.Contains("static const int SFA_ZHARKA_FLAMES = RegisterParticle(\"SampleA/fx/particles/\", \"sfa_zharka_flames\");", list);
        string world = Text(plan, "SFA_Fx.c");
        Assert.Contains("class SFA_FxLight extends PointLightBase", world);
        Assert.Contains("super.OnFrameLightSource(other, timeSlice);", world);
        Assert.Contains("s.AddSound(\"HeavyFire_SoundSet\", 0, false);", world);
        Assert.Contains("part.m_Stop = 2.5;", world);
        Assert.Contains("CurveAt", world);
        // Unused features are not generated; generated code has no comments.
        Assert.DoesNotContain("PathAt", world);
        Assert.DoesNotContain("FxTint", world + list);
        Assert.DoesNotContain("//", world + list + config);
        Assert.Contains("\r\n", world);
        Assert.NotNull(plan.Example);
        // Every runtime method the generated code calls is declared in it (a missed rename once left Enter()).
        var declared = System.Text.RegularExpressions.Regex.Matches(world, "^\\s*(?:protected |static |override )*[\\w<>]+ (\\w+)\\(", System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).ToHashSet();
        // Engine members the light class calls on itself (ScriptedLightBase) and the vector constructor.
        var engine = new HashSet<string> { "Vector", "FadeOut", "Destroy", "SetBrightnessTo", "SetDiffuseColorV", "SetAmbientColorV" };
        var called = System.Text.RegularExpressions.Regex.Matches(world, "(?<![\\w.])([A-Z]\\w*)\\(")
            .Select(m => m.Groups[1].Value).ToHashSet();
        Assert.DoesNotContain(called, c => !declared.Contains(c) && !engine.Contains(c) && !world.Contains($"class {c}"));
    }

    [Fact]
    public void Two_prefixes_share_no_name()
    {
        var a = DayzModExport.Plan(Project("SFA", "SampleA"), Proj, new Source(Proj));
        var b = DayzModExport.Plan(Project("SFB", "SampleB"), Proj, new Source(Proj));
        var classes = (ExportPlan plan) => System.Text.RegularExpressions.Regex.Matches(
                string.Concat(plan.Files.Where(f => f.Path.EndsWith(".c") || f.Path.EndsWith(".cpp")).Select(f => Encoding.UTF8.GetString(f.Bytes))),
                "(?:class|static const int) (\\w+)").Select(m => m.Groups[1].Value)
            .Where(n => n is not ("ParticleList" or "CfgPatches" or "CfgMods" or "CfgSoundShaders" or "CfgSoundSets" or "defs" or "gameScriptModule" or "worldScriptModule"))
            .ToHashSet();
        var ca = classes(a);
        var cb = classes(b);
        Assert.NotEmpty(ca);
        Assert.Empty(ca.Intersect(cb));
        // Particle file names too: the game's particle list keys them by name alone (seen in game, P10).
        var names = (ExportPlan plan) => plan.Files.Where(f => f.What == "particle").Select(f => Path.GetFileName(f.Path)).ToHashSet();
        Assert.Empty(names(a).Intersect(names(b)));
    }

    [Fact]
    public void Re_export_keeps_user_files_and_removes_only_its_own()
    {
        var p = Project("SFA", "SampleA");
        var plan = DayzModExport.Plan(p, Proj, new Source(Proj));
        plan.Write();
        string fx = Path.Combine(_dir, "SampleA", "fx");
        Assert.All(plan.Files, f => Assert.True(File.Exists(f.Path)));
        string mine = Path.Combine(fx, "scripts", "4_World", "MyStarter.c");
        File.WriteAllText(mine, "class MyStarter {}");
        var again = DayzModExport.Plan(p, Proj, new Source(Proj));
        Assert.All(again.Files, f => Assert.Equal(FileState.Same, f.State));
        Assert.Empty(again.Remove);
        // A part removed from the project: its file goes, the user's file stays.
        p.States[1].Items.RemoveAll(i => i.Ref == "column");
        p.Parts.RemoveAll(x => x.Id == "column");
        var third = DayzModExport.Plan(p, Proj, new Source(Proj));
        Assert.Single(third.Remove);
        third.Write();
        Assert.False(File.Exists(Path.Combine(fx, "particles", "sfa_zharka_column.ptc")));
        Assert.True(File.Exists(mine));
        Assert.DoesNotContain("MyStarter", File.ReadAllText(Path.Combine(fx, DayzModExport.MarkerName)));
        // A file in the way that the tool does not own is never overwritten.
        File.Delete(Path.Combine(fx, DayzModExport.MarkerName));
        var blocked = DayzModExport.Plan(p, Proj, new Source(Proj));
        Assert.False(blocked.CanWrite);
        Assert.Contains(blocked.Errors, e => e.Key == "Exp.NotOwned");
    }

    [Fact]
    public void Bad_settings_and_references_are_errors()
    {
        var p = Project("1bad", "SampleA");
        p.States[0].Items.Add(new StateItem { Ref = "nothing" });
        var plan = DayzModExport.Plan(p, Proj, new Source(Proj));
        Assert.Contains(plan.Errors, e => e.Key == "Exp.BadPrefix");
        Assert.Contains(plan.Errors, e => e.Key == "Exp.BadRef");
        var q = Project("SFA", "SampleA");
        File.Delete(Path.Combine(Proj, "cg_xray", "ar", "materials", "flame.edds"));
        var missing = DayzModExport.Plan(q, Proj, new Source(Proj));
        Assert.Contains(missing.Errors, e => e.Key == "Exp.Missing");
    }

    [Fact]
    public void The_cg_profile_writes_a_donor_assemble_takes()
    {
        var p = Project("SFA", "SampleA");
        p.Parts.Add(new PartEntry { Id = "debris", Path = "particles/column.ptc", Near = 150, Spin = -20 });
        p.States[0].Items.Add(new StateItem { Ref = "debris" });
        p.Export.Profile = "cg";
        p.Export.Cg = new CgProfile { DonorFolder = Path.Combine(_dir, "donor"), SlotFile = Path.Combine(_dir, "CG_AnomalyShowcasePfx.c"), Type = "zharka" };
        var plan = CgExport.Plan(p, Proj, new Source(Proj));
        Assert.True(plan.CanWrite, string.Join("; ", plan.Errors.Select(e => e.Key)));
        Assert.Contains("\"cg_pfx/zharka/materials/flame.emat\"", Text(plan, "particles/zharka_cg_flames.ptc"));
        var json = JsonNode.Parse(Text(plan, CgExport.JsonName))!;
        Assert.Equal("cgp", json["tag"]!.GetValue<string>());
        var project = json["projects"]!["zharka"]!;
        var wave = project["wave"]!.AsArray().Select(w => w![3]!.GetValue<string>()).ToList();
        Assert.Equal(new[] { "cg_flames", "cg_column" }, wave);
        var flames = project["fx"]!["zharka_cg_flames"]!;
        Assert.Equal(30, flames["spin"]!.GetValue<double>());
        var near = flames["parts"]![0]!;
        Assert.Equal("cg_debris", near["state"]!.GetValue<string>());
        Assert.Equal(150, near["near"]!.GetValue<double>());
        Assert.Equal(2.5, project["fx"]!["zharka_cg_column"]!["stop"]!.GetValue<double>());
        var sound = project["sounds"]![0]!.AsArray();
        Assert.Equal("sounds/zharka_cg_burn.ogg", sound[1]!.GetValue<string>());
        Assert.Equal(1, sound[4]!.GetValue<int>());
        Assert.Equal(0.5, project["config"]!["timing"]!["tick"]!.GetValue<double>());
        string slot = Text(plan, "CG_AnomalyShowcasePfx.c");
        Assert.StartsWith("class CG_AnomShowPfx", slot);
        Assert.Contains("st.Ptc(ParticleList.CG_ANOM_ZHARKA_CG_FLAMES);", slot);
        Assert.DoesNotContain("CG_ANOM_ZHARKA_CG_DEBRIS", slot);
        Assert.Contains("st.Snd(\"CG_Anom_Zharka_Cg_Burn_SoundSet\");", slot);
        Assert.Contains("st.Snd(\"HeavyFire_SoundSet\");", slot);
        Assert.Contains("li.Haze(2, 0.02);", slot);
        Assert.Contains("st.OverIdle();", slot);
        Assert.Contains(plan.Notes, n => n.Key == "Exp.CgNoCurve");
        plan.Write();

        // A second project in the same donor folder: both stay in the json and the slot file.
        var q = Project("SFA", "SampleA");
        q.Name = "zharka_blue";
        q.Export = p.Export;
        var second = CgExport.Plan(q, Proj, new Source(Proj));
        Assert.True(second.CanWrite, string.Join("; ", second.Errors.Select(e => e.Key)));
        var both = JsonNode.Parse(Text(second, CgExport.JsonName))!["projects"]!.AsObject();
        Assert.Equal(new[] { "zharka", "zharka_blue" }, both.Select(kv => kv.Key));
        Assert.Contains("particles/zharka_cg_zharka_blue_flames.ptc", both["zharka_blue"]!["wave"]![0]![1]!.GetValue<string>());
        string slots = Text(second, "CG_AnomalyShowcasePfx.c");
        Assert.Contains("\"zharka_cg\"", slots);
        Assert.Contains("\"zharka_cg_zharka_blue\"", slots);
    }

    // P12b: the flash (CG_AnomFlash's rule) and the dust around the player reach both profiles.
    [Fact]
    public void Flash_and_around_dust_reach_both_profiles()
    {
        ProjectFile With(ProjectFile p)
        {
            p.Parts.Add(new PartEntry { Id = "dust", Path = "particles/column.ptc", Around = 100, Swirl = -261.8f, SwirlMin = 10 });
            p.Flashes.Add(new FlashEntry { Id = "blind", At = new[] { 0, 1.5f, 0 }, Full = 3, Max = 30 });
            p.States[0].Items.Add(new StateItem { Ref = "dust" });
            p.States[1].Items.Add(new StateItem { Ref = "blind" });
            return p;
        }
        var mod = DayzModExport.Plan(With(Project("SFA", "SampleA")), Proj, new Source(Proj));
        Assert.True(mod.CanWrite, string.Join("; ", mod.Errors.Select(e => e.Key)));
        string world = Text(mod, "fx/scripts/4_World/SFA_Fx.c");
        Assert.Contains("s.AddFlash(Vector(0, 1.5, 0), 3, 30, 0);", world);
        Assert.Contains("protected void FxBlind(vector pos, float rangeFull, float rangeMax)", world);
        Assert.Contains("player.OnPlayerReceiveFlashbangHitStart(MiscGameplayFunctions.IsPlayerOrientedTowardPos(player, pos, 60));", world);
        Assert.Contains("part.m_Around = 100;", world);
        Assert.Contains("part.m_Swirl = -261.8;", world);
        Assert.Contains("ps.SetOrientation(Vector(heading - 270, 0, 0));", world);
        Assert.DoesNotContain("//", world);

        var p = With(Project("SFA", "SampleA"));
        p.Export.Profile = "cg";
        p.Export.Cg = new CgProfile { DonorFolder = Path.Combine(_dir, "donor2"), SlotFile = Path.Combine(_dir, "Slots2.c"), Type = "zharka" };
        var cg = CgExport.Plan(p, Proj, new Source(Proj));
        Assert.True(cg.CanWrite, string.Join("; ", cg.Errors.Select(e => e.Key)));
        var fx = JsonNode.Parse(Text(cg, CgExport.JsonName))!["projects"]!["zharka"]!["fx"]!["zharka_cg_dust"]!;
        Assert.Equal(new[] { -261.8, 10 }, fx["swirl"]!.AsArray().Select(v => Math.Round(v!.GetValue<double>(), 1)));
        string slot = Text(cg, "Slots2.c");
        Assert.Contains("st.Around(ParticleList.CG_ANOM_ZHARKA_CG_DUST, 100);", slot);
        Assert.Contains("st.Flash(3, 30, \"0 1.5 0\");", slot);
        Assert.DoesNotContain("st.Ptc(ParticleList.CG_ANOM_ZHARKA_CG_DUST);", slot);
        Assert.Contains(cg.Notes, n => n.Key == "Exp.CgAroundTint");
    }
}
