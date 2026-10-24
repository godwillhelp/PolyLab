using System.Text.Json;
using System.Text.Json.Serialization;
using Pfx.App.Core;
using Pfx.XRay;

namespace Pfx.App.XRay;

// What one imported particle came from and everything of its anomaly the .ptc files cannot hold, written next to them
// as <ptype>_<state>.xray.json (the tool owns it): the source (tag, variant, group), per emitter the X-Ray actions with
// what the conversion did to each and xray.py's notes, the script-side data xray.py made (turn rates, hazes, hard stops)
// and, when imported from a zone, the zone's sounds, lights, timings, screen effect and hit settings — data for P10
// (export), listed here instead of dropped.
public sealed class ImportRecord
{
    // xray.py writes material and texture paths as "cg_xray/<tag>/...", so the stage inside the import folder carries
    // that name for the paths to resolve; the export (P10) renames them to the profile's prefix.
    public const string Prefix = "cg_xray";

    public int Version { get; set; } = 1;
    public string Tag { get; set; } = "";
    public string Variant { get; set; } = "";
    public string Ref { get; set; } = "";
    public string PType { get; set; } = "";
    public string State { get; set; } = "";
    public string? GroupFile { get; set; }
    public List<string> Ptc { get; set; } = new();
    public List<string> Files { get; set; } = new();
    public Dictionary<string, string> Hashes { get; set; } = new();
    public List<XrBridgeEmitter> Emitters { get; set; } = new();
    public JsonElement? Script { get; set; }
    public List<string> Log { get; set; } = new();
    public string? Zone { get; set; }
    public string? Role { get; set; }
    public List<SoundData> Sounds { get; set; } = new();
    public List<LightData> Lights { get; set; } = new();
    public Dictionary<string, string> Timings { get; set; } = new();
    public string? Postprocess { get; set; }
    public Dictionary<string, string> Hit { get; set; } = new();

    public sealed record SoundData(string Role, string Ref, string? File, double? Min, double? Range, double? Volume);

    public sealed record LightData(string Role, Dictionary<string, string> Keys, string? Anim, double[]? AnimMean, double? AnimSeconds);

    [JsonIgnore]
    public string Key => $"{PType}_{State}";

    public static string FileOf(string stage, string tag, string ptype, string state) => Path.Combine(stage, tag, "particles", $"{ptype}_{state}.xray.json");

    public static ImportRecord From(XrBridgeParticle p, XrBridgeResult result, XrAnomalyPlan? plan, XrImportPart part)
    {
        var r = new ImportRecord
        {
            Tag = p.Tag, Variant = p.Variant, Ref = p.Ref, PType = p.PType, State = p.State, GroupFile = p.GroupPath, Ptc = p.Ptc, Files = p.Files,
            Emitters = p.Emitters, Log = p.Log,
        };
        foreach (var f in p.Files)
            if (result.Files.TryGetValue(f, out var info))
                r.Hashes[f] = info.Sha256;
        if (result.Fx.ValueKind == JsonValueKind.Object && result.Fx.TryGetProperty(r.Key, out var fx))
            r.Script = fx.Clone();
        if (plan != null)
        {
            r.Zone = plan.Section;
            // Several roles may name one particle (electra: hit_small and entrance_small): the part's state says which.
            r.Role = (plan.Particles.FirstOrDefault(x => x.Ref == part.Ref && Views.XrayNames.State(p.Tag, x.Role) == part.State)
                      ?? plan.Particles.FirstOrDefault(x => x.Ref == part.Ref))?.Role;
            r.Sounds = plan.Sounds.Select(s => new SoundData(s.Role, s.Ref, s.File, s.Params?.Min, s.Params?.Range, s.Params?.Volume)).ToList();
            r.Lights = plan.Lights.Select(l => new LightData(l.Role, l.Keys.ToDictionary(k => k.Key, k => k.Value),
                l.Anim?.Name, l.Anim is { } a ? new[] { a.Mean().R, a.Mean().G, a.Mean().B } : null, l.Anim?.Seconds)).ToList();
            r.Timings = plan.Timings.ToDictionary(k => k.Key, k => k.Value);
            r.Postprocess = plan.Postprocess;
            r.Hit = plan.Hit.ToDictionary(k => k.Key, k => k.Value);
        }
        return r;
    }

    public void Save(string stage)
    {
        string path = FileOf(stage, Tag, PType, State);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    // The record of an opened part: the part file itself, or its main file for a split part (<main>_tN / _sN.ptc).
    public static ImportRecord? ForPart(string ptcPath)
    {
        string dir = Path.GetDirectoryName(ptcPath)!;
        string name = Path.GetFileNameWithoutExtension(ptcPath);
        for (string cand = name; cand.Length > 0;)
        {
            string file = Path.Combine(dir, cand + ".xray.json");
            if (File.Exists(file))
            {
                try
                {
                    var r = JsonSerializer.Deserialize<ImportRecord>(File.ReadAllBytes(file));
                    if (r != null && r.Ptc.Any(p => string.Equals(Path.GetFileNameWithoutExtension(p), name, StringComparison.OrdinalIgnoreCase)))
                        return r;
                }
                catch (Exception e) when (e is IOException or JsonException)
                {
                    return null;
                }
            }
            int cut = cand.LastIndexOf('_');
            cand = cut > 0 ? cand[..cut] : "";
        }
        return null;
    }
}
