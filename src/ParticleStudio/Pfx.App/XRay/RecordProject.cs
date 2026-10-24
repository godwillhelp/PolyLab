using System.Globalization;
using System.Text.Json;
using Pfx.Core.Model;
using Pfx.XRay;

namespace Pfx.App.XRay;

// A zone import as one composition (spec 7 P10: "a zone import opens as a composition with its states, sounds and
// lights filled from the record"). The X-Ray zone cycle (engine CustomZone: idle -> awaking -> blowout -> accumulate
// -> idle) becomes the states:
// - idle (until an event): the idle particles, the idle sound (looped) and the idle light when the zone has it on;
// - charge: the awake particles and sound for awaking_time;
// - active: the blowout particles, light and sound, each after its own blowout_*_time, then the accumulate particles
//   and sound after blowout_time; it lasts blowout_time + accamulate_time. Blowout effects are one-shots the engine
//   does not stop at the accumulate switch, and a state change soft-stops parts here, so the two zone states share
//   one state;
// - hit (over idle, until an event): the hit_small particles and the hit sound.
// The other roles (big hits, entrances, idle_small / idle_big on objects inside) become parts no state starts.
// Parts carry xray.py's turn rates and hard stops; hazes come from xray.py's script data with their delays; the
// blowout light follows CG's conversion (full for one 30 fps frame, then a linear fade over light_time, as the
// engine's blowout light fades); the idle light takes its lanim's frames and their mean colour (engine rule).
public static class RecordProject
{
    // One frame at 30 fps: the blowout light is full only at its start (CG_AnomalyShowcaseList: Life(0.033, light_time)).
    public const float FlashLife = 0.033f;

    private static readonly (string Role, string State, string? TimeKey)[] Roles =
    {
        ("idle_particles", "idle", null),
        ("awake_particles", "charge", null),
        ("blowout_particles", "active", "blowout_particles_time"),
        ("accum_particles", "active", "blowout_time"),
        ("hit_small_particles", "hit", null),
    };

    public static ProjectFile From(IReadOnlyList<ImportRecord> records, Func<string, string> partPath, Func<ImportRecord.SoundData, string?> soundPath,
        Func<string, XrLightAnim?>? lanim = null)
    {
        var any = records.FirstOrDefault(r => r.Zone != null) ?? records.First();
        var timings = any.Timings;
        double Ms(string key) => timings.TryGetValue(key, out var v) && double.TryParse(v.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d / 1000 : 0;
        var file = new ProjectFile { Name = ProjectNames.Id(any.Zone ?? any.Key) };
        var idle = new StateEntry { Name = "idle" };
        var charge = new StateEntry { Name = "charge", Duration = (float)Ms("awaking_time") };
        var active = new StateEntry { Name = "active", Duration = (float)(Ms("blowout_time") + Ms("accamulate_time")) };
        var hit = new StateEntry { Name = "hit", OverIdle = true };
        var states = new Dictionary<string, StateEntry> { ["idle"] = idle, ["charge"] = charge, ["active"] = active, ["hit"] = hit };

        foreach (var r in records.OrderBy(r => Array.FindIndex(Roles, x => x.Role == RoleOf(r)) is var i && i < 0 ? int.MaxValue : i).ThenBy(r => r.Key))
        {
            var role = Roles.FirstOrDefault(x => x.Role == RoleOf(r));
            float delay = role.TimeKey != null ? (float)Ms(role.TimeKey) : 0;
            string prefix = $"{r.PType}_{r.Tag}_";
            foreach (var ptc in r.Ptc)
            {
                string stem = Path.GetFileNameWithoutExtension(ptc);
                string id = ProjectNames.Unique(ProjectNames.Id(stem.StartsWith(prefix, StringComparison.Ordinal) ? stem[prefix.Length..] : stem), file.Items().Select(i => i.Id));
                var part = new PartEntry { Id = id, Path = partPath(Path.Combine(r.Tag, ptc)) };
                ScriptOf(r, stem, part);
                file.Parts.Add(part);
                if (role.Role != null)
                    states[role.State!].Items.Add(new StateItem { Ref = id, Delay = delay });
            }
            if (r.Script is { ValueKind: JsonValueKind.Object } script && script.TryGetProperty("haze", out var hazes) && hazes.ValueKind == JsonValueKind.Array)
            {
                int n = 0;
                foreach (var h in hazes.EnumerateArray())
                {
                    string id = ProjectNames.Unique(ProjectNames.Id((role.State ?? r.State) + "_haze" + ++n), file.Items().Select(i => i.Id));
                    var at = h.GetProperty("at").EnumerateArray().Select(x => (float)x.GetDouble()).ToArray();
                    file.Hazes.Add(new HazeEntry
                    {
                        Id = id, At = at, Radius = Num(h, "radius"), Power = Num(h, "power"),
                        Life = h.TryGetProperty("life", out var life) && life.ValueKind == JsonValueKind.Number ? (float)life.GetDouble() : 0, Fade = Num(h, "fade"),
                    });
                    if (role.Role != null)
                        states[role.State!].Items.Add(new StateItem { Ref = id, Delay = delay + Num(h, "delay") });
                }
            }
        }

        foreach (var s in any.Sounds)
        {
            if (soundPath(s) is not { } path)
                continue;
            string id = ProjectNames.Unique(ProjectNames.Id(s.Role), file.Items().Select(i => i.Id));
            var entry = new SoundEntry { Id = id, File = path, Loop = s.Role == "idle_sound" };
            if (s.Volume is double v)
                entry.Volume = (float)v;
            if (s.Range is double range)
                entry.Range = (float)range;
            file.Sounds.Add(entry);
            switch (s.Role)
            {
                case "idle_sound": idle.Items.Add(new StateItem { Ref = id }); break;
                case "awake_sound": charge.Items.Add(new StateItem { Ref = id }); break;
                case "blowout_sound": active.Items.Add(new StateItem { Ref = id, Delay = (float)Ms("blowout_sound_time") }); break;
                case "accum_sound": active.Items.Add(new StateItem { Ref = id, Delay = (float)Ms("blowout_time") }); break;
                case "hit_sound": hit.Items.Add(new StateItem { Ref = id }); break;
            }
        }

        foreach (var l in any.Lights)
        {
            string id = ProjectNames.Unique(ProjectNames.Id(l.Role), file.Items().Select(i => i.Id));
            if (l.Role == "blowout_light")
            {
                var light = new LightEntry
                {
                    Id = id, Color = Floats(l.Keys.GetValueOrDefault("light_color"), new[] { 1f, 1f, 1f }), Brightness = 1,
                    Radius = Float(l.Keys.GetValueOrDefault("light_range"), 10), Offset = new[] { 0f, Float(l.Keys.GetValueOrDefault("light_height"), 1), 0f },
                    Life = FlashLife, FadeOut = Float(l.Keys.GetValueOrDefault("light_time"), 1),
                };
                file.Lights.Add(light);
                if (On(l.Keys.GetValueOrDefault("blowout_light")))
                    active.Items.Add(new StateItem { Ref = id, Delay = (float)Ms("blowout_light_time") });
            }
            else
            {
                var light = new LightEntry
                {
                    Id = id, Brightness = 1, Radius = Float(l.Keys.GetValueOrDefault("idle_light_range"), 4),
                    Offset = new[] { 0f, Float(l.Keys.GetValueOrDefault("idle_light_height"), 1), 0f },
                    Shadow = On(l.Keys.GetValueOrDefault("idle_light_shadow")), FadeOut = 1,
                    Color = l.AnimMean is { Length: 3 } m ? new[] { (float)m[0], (float)m[1], (float)m[2] } : new[] { 1f, 1f, 1f },
                };
                if (l.Anim != null && lanim?.Invoke(l.Anim) is { Fps: > 0, Frames: > 0 } a)
                {
                    light.Anim = new LightAnim { Name = a.Name, Fps = (float)a.Fps };
                    for (int f = 0; f < a.Frames; f++)
                    {
                        var c = a.ColourAt(f / a.Fps);
                        light.Anim.Frames.Add(new[] { (float)c.R, (float)c.G, (float)c.B });
                    }
                }
                file.Lights.Add(light);
                if (On(l.Keys.GetValueOrDefault("idle_light")))
                    idle.Items.Add(new StateItem { Ref = id });
            }
        }

        file.States.Add(idle);
        if (charge.Items.Count > 0)
            file.States.Add(charge);
        if (active.Items.Count > 0)
            file.States.Add(active);
        if (hit.Items.Count > 0)
            file.States.Add(hit);
        return file;
    }

    // The zone role of a record from its state (<tag>_<role without "_particles">, as the import names it): records
    // written before P10 took the first role naming the same particle (electra's entrance_small is its hit_small).
    private static string? RoleOf(ImportRecord r)
    {
        string role = r.State.StartsWith(r.Tag + "_", StringComparison.Ordinal) ? r.State[(r.Tag.Length + 1)..] + "_particles" : "";
        return XrAnomalyPlanner.ParticleRoles.Contains(role) ? role : r.Role;
    }

    // xray.py's script data: the main file's turn and hard stop, a split part's own (fx "parts", by its state).
    private static void ScriptOf(ImportRecord r, string stem, PartEntry part)
    {
        if (r.Script is not { ValueKind: JsonValueKind.Object } s)
            return;
        if (stem == r.Key)
        {
            part.Spin = Num(s, "spin");
            if (s.TryGetProperty("stop", out var stop) && stop.ValueKind == JsonValueKind.Number)
                part.Stop = (float)stop.GetDouble();
            return;
        }
        if (!s.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array)
            return;
        foreach (var p in parts.EnumerateArray())
        {
            if (!p.TryGetProperty("state", out var st) || $"{r.PType}_{st.GetString()}" != stem)
                continue;
            part.Spin = Num(p, "spin");
            part.Near = Num(p, "near");
            if (p.TryGetProperty("stop", out var stop) && stop.ValueKind == JsonValueKind.Number)
                part.Stop = (float)stop.GetDouble();
        }
    }

    private static float Num(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? (float)v.GetDouble() : 0;

    private static bool On(string? v) => v != null && v.Trim().ToLowerInvariant() is "on" or "true" or "1" or "yes";

    private static float Float(string? v, float fallback) =>
        v != null && float.TryParse(v.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : fallback;

    private static float[] Floats(string? v, float[] fallback)
    {
        var parts = (v ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var nums = parts.Select(p => float.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : float.NaN).ToArray();
        return nums.Length >= 3 && nums.Take(3).All(float.IsFinite) ? nums.Take(3).ToArray() : fallback;
    }

    // Every record of one zone import: the records next to a part's record with the same zone and tag.
    public static List<ImportRecord> ZoneOf(ImportRecord record, string recordFolder)
    {
        var list = new List<ImportRecord>();
        foreach (var f in Directory.EnumerateFiles(recordFolder, "*.xray.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<ImportRecord>(File.ReadAllBytes(f)) is { } r && r.Zone == record.Zone && r.Tag == record.Tag && r.Variant == record.Variant)
                    list.Add(r);
            }
            catch (Exception e) when (e is IOException or JsonException)
            {
            }
        }
        return list;
    }
}
