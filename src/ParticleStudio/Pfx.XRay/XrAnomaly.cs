using Pfx.Sim.XRay;

namespace Pfx.XRay;

public sealed record XrPlanTexture(string Name, string? File, string? Proposal, bool FromTable);

public sealed record XrPlanEffect(string Name, string? File, bool Library, string? Shader, XrPlanTexture? Texture, string? ChildOf, string? ChildKind);

// One particle a zone names (idle_particles = ...): the group or single effect it resolves to, with every effect and
// child effect it plays.
public sealed record XrPlanParticle(string Role, string Ref, string? File, bool Group, IReadOnlyList<XrPlanEffect> Effects, IReadOnlyList<string> Missing);

public sealed record XrPlanSound(string Role, string Ref, string? File, XrSoundParams? Params);

public sealed record XrPlanLight(string Role, IReadOnlyDictionary<string, string> Keys, XrLightAnim? Anim);

// Everything a zone section of a source (with one option of that source) makes up: the import composition. Particles
// become parts; sounds, lights, timings, the screen effect and the hit settings are data for P10 (export), kept with
// the imported parts, never dropped.
public sealed record XrAnomalyPlan(string Section, XrSource Source, string? Variant, string? Type, IReadOnlyList<XrPlanParticle> Particles,
    IReadOnlyList<XrPlanSound> Sounds, IReadOnlyList<XrPlanLight> Lights, IReadOnlyDictionary<string, string> Timings, string? Postprocess,
    IReadOnlyDictionary<string, string> Hit, IReadOnlyList<string> MissingParents)
{
    public int TexturesFound => Particles.SelectMany(p => p.Effects).Count(e => e.Texture?.File != null);
    public int TexturesTotal => Particles.SelectMany(p => p.Effects).Count(e => e.Texture != null);
}

public static class XrAnomalyPlanner
{
    // Zone keys (engine CustomZone.cpp Load and the configs of Anomaly 1.5.3 / Arrival / DAO).
    public static readonly string[] ParticleRoles = { "idle_particles", "blowout_particles", "accum_particles", "awake_particles",
        "hit_small_particles", "hit_big_particles", "idle_small_particles", "idle_big_particles", "entrance_small_particles", "entrance_big_particles" };
    public static readonly string[] SoundRoles = { "idle_sound", "blowout_sound", "accum_sound", "awake_sound", "hit_sound", "entrance_sound" };
    public static readonly string[] BlowoutLightKeys = { "blowout_light", "light_color", "light_range", "light_time", "light_height" };
    public static readonly string[] IdleLightKeys = { "idle_light", "idle_light_range", "idle_light_anim", "idle_light_height", "idle_light_shadow", "idle_light_volumetric" };
    public static readonly string[] TimingKeys = { "awaking_time", "accamulate_time", "blowout_time", "blowout_particles_time", "blowout_light_time",
        "blowout_sound_time", "blowout_explosion_time", "blowout_wind", "blowout_wind_time_start", "blowout_wind_time_peak", "blowout_wind_time_end",
        "blowout_wind_power", "disable_time", "disable_time_small", "disable_idle_time", "idle_particles_dont_stop" };
    public static readonly string[] HitKeys = { "hit_type", "hit_impulse_scale", "max_start_power", "attenuation", "effective_radius" };

    // A section that names particles itself (not only through a parent) is an anomaly of its own in the list.
    public static bool SetsParticles(LtxSection s) => ParticleRoles.Any(r => s.Keys.TryGetValue(r, out var v) && v.Trim().Length > 0);

    public static XrAnomalyPlan Plan(XrIndex index, XrSource source, string? variant, string section, IReadOnlyDictionary<string, string> substitutes)
    {
        var cfg = index.Config(source, variant);
        var keys = cfg.Resolve(section) ?? new Dictionary<string, string>();
        var particles = new List<XrPlanParticle>();
        foreach (var role in ParticleRoles)
            if (keys.TryGetValue(role, out var raw) && Ref(raw) is { } r)
                particles.Add(Particle(index, source, variant, role, r, substitutes));
        var sounds = new List<XrPlanSound>();
        foreach (var role in SoundRoles)
            if (keys.TryGetValue(role, out var raw) && Ref(raw) is { } r)
            {
                var hit = index.Find(source, variant, "sounds/" + r + ".ogg");
                XrSoundParams? p = null;
                if (hit is { } h)
                {
                    try
                    {
                        p = XrSoundParams.Read(ReadHead(h.File));
                    }
                    catch (IOException)
                    {
                    }
                }
                sounds.Add(new XrPlanSound(role, r, hit?.File, p));
            }
        var lights = new List<XrPlanLight>();
        var blow = BlowoutLightKeys.Where(keys.ContainsKey).ToDictionary(k => k, k => keys[k]);
        if (blow.Count > 0)
            lights.Add(new XrPlanLight("blowout_light", blow, null));
        var idle = IdleLightKeys.Where(keys.ContainsKey).ToDictionary(k => k, k => keys[k]);
        if (idle.Count > 0)
        {
            XrLightAnim? anim = null;
            if (idle.TryGetValue("idle_light_anim", out var name))
                anim = index.Order(source, variant).SelectMany(d => d.Lanims).FirstOrDefault(a => a.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
            lights.Add(new XrPlanLight("idle_light", idle, anim));
        }
        var timings = TimingKeys.Where(keys.ContainsKey).ToDictionary(k => k, k => keys[k]);
        var hitKeys = HitKeys.Where(keys.ContainsKey).ToDictionary(k => k, k => keys[k]);
        string? type = XrTypes.Of(section + " " + string.Join(' ', particles.Select(p => p.Ref))).FirstOrDefault();
        return new XrAnomalyPlan(section, source, variant, type, particles, sounds, lights, timings, keys.GetValueOrDefault("postprocess"), hitKeys,
            cfg.MissingParents(section).ToList());
    }

    // "semitone\anomalies\electra\electra_idle ;comment" -> "semitone/anomalies/electra/electra_idle"; empty -> null.
    public static string? Ref(string raw)
    {
        string v = raw.Split(';')[0].Trim().Trim('"').Replace('\\', '/').ToLowerInvariant();
        return v.Length == 0 || v is "none" or "off" or "false" ? null : v;
    }

    private static byte[] ReadHead(string file)
    {
        using var f = File.OpenRead(file);
        var head = new byte[(int)Math.Min(f.Length, XrRecognize.HeadBytes)];
        f.ReadExactly(head);
        return head;
    }

    private static XrPlanParticle Particle(XrIndex index, XrSource source, string? variant, string role, string r, IReadOnlyDictionary<string, string> subst)
    {
        var effects = new List<XrPlanEffect>();
        var missing = new List<string>();
        var group = index.Particle(source, variant, "particles/" + r + ".pg");
        if (group is { } g)
        {
            foreach (var item in XrGroup.Parse(g.Sections).Effects)
            {
                if (!item.Has(XrGroupItem.Enabled))
                    continue;
                AddEffect(index, source, variant, item.Name, null, null, effects, missing, subst);
                foreach (var (kind, child) in item.Children)
                    AddEffect(index, source, variant, child, item.Name, kind, effects, missing, subst);
            }
            return new XrPlanParticle(role, r, g.File, true, effects, missing);
        }
        AddEffect(index, source, variant, r, null, null, effects, missing, subst);
        return new XrPlanParticle(role, r, effects.FirstOrDefault()?.File, false, effects, missing);
    }

    private static void AddEffect(XrIndex index, XrSource source, string? variant, string name, string? parent, string? kind, List<XrPlanEffect> into,
        List<string> missing, IReadOnlyDictionary<string, string> subst)
    {
        var hit = index.Particle(source, variant, "particles/" + name + ".pe");
        if (hit is not { } h)
        {
            missing.Add(name);
            return;
        }
        var eff = XrEffect.Parse(h.Sections, h.File);
        XrPlanTexture? tex = null;
        if (eff.Texture.Length > 0)
            tex = Texture(index, source, variant, eff.Texture, subst);
        into.Add(new XrPlanEffect(name, h.File, h.Library, eff.Shader, tex, parent, kind));
    }

    // A texture as xray.py's Converter.texture looks for it ("textures/<name>.dds" in the root order); when missing,
    // the substitute table, else a proposal: a file of the same folder whose name only adds a suffix ("x.2048"), else
    // the same file name in another folder.
    public static XrPlanTexture Texture(XrIndex index, XrSource source, string? variant, string name, IReadOnlyDictionary<string, string> subst)
    {
        if (index.Find(source, variant, "textures/" + name + ".dds") is { } f)
            return new XrPlanTexture(name, f.File, null, false);
        if (subst.TryGetValue(name, out var s))
            return new XrPlanTexture(name, null, s, true);
        return new XrPlanTexture(name, null, Propose(index, name), false);
    }

    public static string? Propose(XrIndex index, string name)
    {
        string key = "textures/" + name;
        int slash = name.LastIndexOf('/');
        string file = slash >= 0 ? name[(slash + 1)..] : name;
        var textures = index.Entries.Where(e => e.Kind == XrEntryKind.Texture).ToList();
        var suffixed = textures.Where(e => e.Key.StartsWith(key + ".", StringComparison.Ordinal)).OrderBy(e => e.Key.Length).FirstOrDefault();
        if (suffixed != null)
            return suffixed.Winner.File;
        var same = textures.FirstOrDefault(e => e.Name.EndsWith("/" + file, StringComparison.Ordinal) || e.Name == file);
        return same?.Winner.File;
    }
}
