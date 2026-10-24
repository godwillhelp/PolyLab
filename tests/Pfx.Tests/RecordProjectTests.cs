using System.Globalization;
using System.Text.Json;
using Pfx.App.XRay;
using Pfx.Core.Model;
using Pfx.XRay;

namespace Pfx.Tests;

// P10 acceptance: a zone import (zone_mine_electric) opens as a composition whose states, sounds and lights equal the
// record.
public class RecordProjectTests
{
    private static ImportRecord Electra(string role, string state, string ptcStem, string script) => new()
    {
        Tag = "ar", Variant = "core", PType = "electra", State = state, Zone = "zone_mine_electric", Role = role,
        Ptc = { $"particles/{ptcStem}.ptc" },
        Script = JsonDocument.Parse(script).RootElement.Clone(),
        Sounds =
        {
            new("idle_sound", "semitone/anomalies/electra/electra_idle", "x:/sounds/electra_idle.ogg", null, null, null),
            new("blowout_sound", "semitone/anomalies/electra/electra_blowout", "x:/sounds/electra_blowout.ogg", 7, 100, 1.5),
            new("hit_sound", "semitone/anomalies/electra/electra_hit", "x:/sounds/electra_hit.ogg", 2, 30, 1),
        },
        Lights =
        {
            new("blowout_light", new() { ["blowout_light"] = "on", ["light_color"] = "0.8,0.9,1", ["light_range"] = "30.0", ["light_time"] = "1", ["light_height"] = "1.5" }, null, null, null),
            new("idle_light", new() { ["idle_light"] = "off", ["idle_light_range"] = "4.0", ["idle_light_anim"] = "koster_01_electra", ["idle_light_height"] = "0.75" },
                "koster_01_electra", new[] { 0.6432679738562095, 0.8015359477124182, 0.994705882352941 }, 2),
        },
        Timings = { ["awaking_time"] = "100", ["accamulate_time"] = "1500", ["blowout_time"] = "100", ["blowout_particles_time"] = "20", ["blowout_light_time"] = "40" },
    };

    private static List<ImportRecord> Zone() => new()
    {
        Electra("idle_particles", "ar_idle", "electra_ar_idle", "{\"haze\":[],\"parts\":[],\"ptype\":\"electra\",\"spin\":0.0,\"state\":\"ar_idle\",\"tag\":\"ar\"}"),
        Electra("blowout_particles", "ar_blowout", "electra_ar_blowout",
            "{\"haze\":[{\"at\":[0,0.5,0],\"delay\":0.0,\"fade\":1.089,\"life\":1.0,\"power\":0.01498,\"radius\":2.277}],\"parts\":[],\"spin\":12.5,\"stop\":3.25,\"ptype\":\"electra\",\"state\":\"ar_blowout\",\"tag\":\"ar\"}"),
        Electra("hit_small_particles", "ar_hit_small", "electra_ar_hit_small", "{\"haze\":[],\"parts\":[],\"spin\":0}"),
        Electra("entrance_big_particles", "ar_entrance_big", "electra_ar_entrance_big", "{\"haze\":[],\"parts\":[],\"spin\":0}"),
        // A P9 record of a role that names the same particle as hit_small: its role was written as hit_small.
        Electra("hit_small_particles", "ar_entrance_small", "electra_ar_entrance_small", "{\"haze\":[],\"parts\":[],\"spin\":0}"),
    };

    private static ProjectFile Build(List<ImportRecord> records) =>
        RecordProject.From(records, rel => rel.Replace('\\', '/'), s => "sounds/" + Path.GetFileName(s.File!.Replace('\\', '/')), null);

    [Fact]
    public void An_electra_zone_becomes_its_states()
    {
        var p = Build(Zone());
        Assert.Equal("zone_mine_electric", p.Name);
        Assert.Equal(new[] { "idle", "active", "hit" }, p.States.Select(s => s.Name));
        Assert.Equal(0, p.States[0].Duration);
        Assert.Equal(1.6f, p.States[1].Duration, 4);
        Assert.True(p.States[2].OverIdle);
        Assert.Equal(new[] { "idle", "blowout", "hit_small", "entrance_big", "entrance_small" }, p.Parts.Select(x => x.Id));
        Assert.Equal(new[] { "hit_small" }, p.States[2].Items.Where(i => p.Find(i.Ref) is PartEntry).Select(i => i.Ref));
        Assert.Equal("ar/particles/electra_ar_blowout.ptc", p.Parts[1].Path);
        Assert.Equal(12.5f, p.Parts[1].Spin);
        Assert.Equal(3.25f, p.Parts[1].Stop);
        // Delays from the zone timings.
        Assert.Equal(0.02f, p.States[1].Items.Single(i => i.Ref == "blowout").Delay, 5);
        Assert.Equal(0.04f, p.States[1].Items.Single(i => i.Ref == "blowout_light").Delay, 5);
        // The haze of the blowout part starts with the part.
        var haze = Assert.Single(p.Hazes);
        Assert.Equal(2.277f, haze.Radius, 4);
        Assert.Equal(0.01498f, haze.Power, 5);
        Assert.Equal(1.0f, haze.Life);
        Assert.Equal(1.089f, haze.Fade, 4);
        Assert.Equal(0.02f, p.States[1].Items.Single(i => i.Ref == haze.Id).Delay, 5);
        // Sounds with the record's range and volume; the idle sound loops.
        var idleSound = p.Sounds.Single(s => s.Id == "idle_sound");
        Assert.True(idleSound.Loop);
        Assert.Equal("sounds/electra_idle.ogg", idleSound.File);
        var blowSound = p.Sounds.Single(s => s.Id == "blowout_sound");
        Assert.Equal(100f, blowSound.Range);
        Assert.Equal(1.5f, blowSound.Volume);
        Assert.False(blowSound.Loop);
        Assert.Contains(p.States[0].Items, i => i.Ref == "idle_sound");
        Assert.Contains(p.States[2].Items, i => i.Ref == "hit_sound");
        // Lights: the blowout flash as CG converts it, the idle light (off in this zone) kept but not started.
        var flash = p.Lights.Single(l => l.Id == "blowout_light");
        Assert.Equal(new[] { 0.8f, 0.9f, 1f }, flash.Color);
        Assert.Equal(30f, flash.Radius);
        Assert.Equal(1.5f, flash.Offset[1]);
        Assert.Equal(1f, flash.FadeOut);
        Assert.Equal(RecordProject.FlashLife, flash.Life);
        var idleLight = p.Lights.Single(l => l.Id == "idle_light");
        Assert.Equal(4f, idleLight.Radius);
        Assert.Equal(0.75f, idleLight.Offset[1]);
        Assert.Equal(0.6432679738562095f, idleLight.Color[0], 6);
        Assert.DoesNotContain(p.States[0].Items, i => i.Ref == "idle_light");
        // Entrance parts are kept, no state starts them.
        Assert.DoesNotContain(p.States.SelectMany(s => s.Items), i => i.Ref == "entrance_big");
    }

    [Fact]
    public void Lanim_frames_follow_the_engine_rule()
    {
        var anim = new XrLightAnim("pulse", 2, 4, new List<(int, uint)> { (0, 0xFF000000), (2, 0xFFFF8000) });
        var records = Zone();
        records[0].Lights[1] = records[0].Lights[1] with { Keys = new() { ["idle_light"] = "on", ["idle_light_range"] = "4.0" } };
        var p = RecordProject.From(records, rel => rel, s => "s.ogg", name => name == "koster_01_electra" ? anim : null);
        var light = p.Lights.Single(l => l.Id == "idle_light");
        Assert.NotNull(light.Anim);
        Assert.Equal(2f, light.Anim!.Fps);
        Assert.Equal(4, light.Anim.Frames.Count);
        Assert.Equal(0.5f, light.Anim.Frames[1][0], 4);
        Assert.Equal(1f, light.Anim.Frames[3][0], 4);
        Assert.Contains(p.States[0].Items, i => i.Ref == "idle_light");
    }

    // The P9 import of zone_mine_electric (PFX_XRAY_RECORDS = its cg_xray\ar\particles folder): every value of the
    // composition equals the record it came from.
    [Fact]
    public void A_real_zone_import_equals_its_records()
    {
        string? dir = Environment.GetEnvironmentVariable("PFX_XRAY_RECORDS");
        if (dir == null || !File.Exists(Path.Combine(dir, "electra_ar_idle.xray.json")))
            return;
        var idle = JsonSerializer.Deserialize<ImportRecord>(File.ReadAllBytes(Path.Combine(dir, "electra_ar_idle.xray.json")))!;
        var records = RecordProject.ZoneOf(idle, dir);
        Assert.True(records.Count >= 5);
        var p = RecordProject.From(records, rel => rel, s => s.File, null);
        double Ms(string k) => double.Parse(idle.Timings[k], CultureInfo.InvariantCulture) / 1000;
        Assert.Equal((float)(Ms("blowout_time") + Ms("accamulate_time")), p.States.Single(s => s.Name == "active").Duration, 5);
        foreach (var s in idle.Sounds.Where(s => s.File != null))
        {
            var e = p.Sounds.Single(x => x.Id == ProjectNames.Id(s.Role));
            Assert.Equal(s.File, e.File);
            if (s.Range is double r)
                Assert.Equal((float)r, e.Range);
            if (s.Volume is double v)
                Assert.Equal((float)v, e.Volume);
        }
        var blow = idle.Lights.Single(l => l.Role == "blowout_light");
        var flash = p.Lights.Single(l => l.Id == "blowout_light");
        Assert.Equal(float.Parse(blow.Keys["light_range"], CultureInfo.InvariantCulture), flash.Radius);
        Assert.Equal(float.Parse(blow.Keys["light_time"], CultureInfo.InvariantCulture), flash.FadeOut);
        Assert.Equal(blow.Keys["light_color"].Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture)), flash.Color);
        foreach (var r in records)
            foreach (var ptc in r.Ptc)
                Assert.Contains(p.Parts, x => x.Path.Replace('\\', '/').EndsWith(ptc));
        var blowRecord = records.Single(r => r.Role == "blowout_particles");
        int hazes = blowRecord.Script!.Value.GetProperty("haze").GetArrayLength();
        Assert.Equal(hazes, p.Hazes.Count(h => h.Id.StartsWith("active_haze")));
    }
}
