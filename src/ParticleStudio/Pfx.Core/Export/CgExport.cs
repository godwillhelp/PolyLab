using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pfx.Core.Model;

namespace Pfx.Core.Export;

// The "CG_Anomaly" export profile (spec 3.13), settled against assemble.py and manifest.py as they are (P10):
// - the donor folder holds the parts as particles/<type>_<state>.ptc with references under the folder's own path
//   prefix, their materials, textures and sounds; assemble.py takes it like any donor through the hook in manifest.py
//   (owner decision 2026-10-15), which reads pfx_cg.json: the donor tag and reference prefix, and per project a
//   particle wave, OWN_FX-style entries (turn, hard stop, near-only parts, strikes) and SOUNDS rows;
// - the showcase slots come from one generated file (class CG_AnomShowPfx, called once from CG_AnomShowList.Fill),
//   rebuilt from every project in pfx_cg.json;
// - each project's "config" block is the type entry of the server's config.json (archetype, shells, timing, visual).
// Release names carry only "cg": particles <type>_cg_<part>, sound sets CG_Anom_<Type>_Cg_<Sound>_SoundSet.
public static class CgExport
{
    public const string JsonName = "pfx_cg.json";
    public const string DefaultTag = "cgp";
    public const string DefaultRefs = "cg_pfx/";
    public const string SlotClass = "CG_AnomShowPfx";

    public static string MarkerName(string project) => $"pfx_owned_{project}.txt";

    public static ExportPlan Plan(ProjectFile p, string projectFolder, IExportSource source)
    {
        var cg = p.Export.Cg;
        string donor = cg.DonorFolder.Length == 0 ? "" : Path.GetFullPath(Path.Combine(projectFolder, cg.DonorFolder));
        var plan = new ExportPlan(Path.Combine(donor.Length > 0 ? donor : projectFolder, MarkerName(p.Name)));
        if (donor.Length == 0)
            plan.Errors.Add(new ExportMessage("Exp.NoDonor"));
        if (cg.SlotFile.Length == 0)
            plan.Errors.Add(new ExportMessage("Exp.NoSlotFile"));
        string type = cg.Type.Length > 0 ? cg.Type : p.Name;
        if (!ProjectNames.IsId(type))
            plan.Errors.Add(new ExportMessage("Exp.BadType", type));
        DayzModExport.Common(p, plan);
        if (!plan.CanWrite)
            return plan;

        string jsonPath = Path.Combine(donor, JsonName);
        var root = File.Exists(jsonPath) ? JsonNode.Parse(File.ReadAllText(jsonPath))?.AsObject() : null;
        if (File.Exists(jsonPath) && root?["projects"] is not JsonObject)
        {
            plan.Errors.Add(new ExportMessage("Exp.CgJsonBroken", jsonPath));
            return plan;
        }
        root ??= new JsonObject { ["tag"] = DefaultTag, ["refs"] = DefaultRefs, ["projects"] = new JsonObject() };
        string tag = root["tag"]!.GetValue<string>();
        string refs = root["refs"]!.GetValue<string>();
        var projects = root["projects"]!.AsObject();

        string infix = type == p.Name ? "cg_" : $"cg_{p.Name}_";
        string StateOf(string partId) => infix + partId;
        string Registered(string partId) => $"CG_ANOM_{ExportText.Upper(type)}_{ExportText.Upper(StateOf(partId))}";
        var assets = new AssetCopy(source, plan, refs.TrimEnd('/') + "/" + p.Name, p.Name);

        // Names other projects of the folder already produce.
        var taken = projects.Where(kv => kv.Key != p.Name)
            .SelectMany(kv => kv.Value?["wave"]?.AsArray().Select(w => w?[1]?.GetValue<string>() ?? "") ?? Array.Empty<string>())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var part in p.Parts)
        {
            string disk = Path.GetFullPath(Path.Combine(projectFolder, part.Path));
            if (!File.Exists(disk))
            {
                plan.Errors.Add(new ExportMessage("Exp.PartMissing", part.Id, disk));
                continue;
            }
            string rel = $"particles/{type}_{StateOf(part.Id)}.ptc";
            if (taken.Contains(rel))
                plan.Errors.Add(new ExportMessage("Exp.CgTaken", rel));
            plan.Add(rel, assets.Part(source.Read(disk), part.Id + ".ptc"), "particle");
        }

        // Near-only parts ride on another part of their state (assemble.py reads "near" only for fx parts); a strike
        // belongs to the first always-shown part of its state (the showcase starts it with that particle).
        var mainOf = new Dictionary<string, string>();
        var strikeOf = new Dictionary<string, StrikeEntry>();
        foreach (var state in p.States)
        {
            var parts = state.Items.Select(i => p.Find(i.Ref)).OfType<PartEntry>().ToList();
            var main = parts.FirstOrDefault(x => x.Near <= 0);
            foreach (var k in state.Items.Select(i => p.Find(i.Ref)).OfType<StrikeEntry>())
            {
                if (main == null)
                    plan.Errors.Add(new ExportMessage("Exp.CgStrikeAlone", state.Name, k.Id));
                else if (!strikeOf.TryAdd(main.Id, k) && strikeOf[main.Id] != k)
                    plan.Notes.Add(new ExportMessage("Exp.CgOneStrike"));
            }
            foreach (var near in parts.Where(x => x.Near > 0))
            {
                if (main == null)
                    plan.Errors.Add(new ExportMessage("Exp.CgNearAlone", state.Name, near.Id));
                else if (mainOf.TryGetValue(near.Id, out var other) && other != main.Id)
                    plan.Errors.Add(new ExportMessage("Exp.CgNearShared", near.Id));
                else
                    mainOf[near.Id] = main.Id;
            }
        }
        var waveParts = p.Parts.Where(x => !mainOf.ContainsKey(x.Id)).ToList();
        var wave = new JsonArray();
        foreach (var part in waveParts)
            wave.Add(new JsonArray(tag, $"particles/{type}_{StateOf(part.Id)}.ptc", type, StateOf(part.Id)));
        var fx = new JsonObject();
        foreach (var part in waveParts)
        {
            var nears = p.Parts.Where(x => mainOf.TryGetValue(x.Id, out var m) && m == part.Id).ToList();
            var strike = strikeOf.GetValueOrDefault(part.Id);
            if (part.Spin == 0 && part.Stop is not > 0 && nears.Count == 0 && strike == null && part.Around <= 0)
                continue;
            var entry = new JsonObject
            {
                ["ptype"] = type, ["state"] = StateOf(part.Id), ["tag"] = tag, ["spin"] = part.Spin, ["haze"] = new JsonArray(),
                ["parts"] = new JsonArray(nears.Select(n => (JsonNode)Part(n, StateOf(n.Id))).ToArray()),
            };
            if (part.Stop is > 0 and var stop)
                entry["stop"] = stop;
            if (part.Around > 0)
                entry["swirl"] = new JsonArray(part.Swirl, part.SwirlMin);
            if (strike != null)
            {
                entry["strike"] = new JsonObject
                {
                    ["ptype"] = type, ["state"] = StateOf(strike.Part),
                    ["shape"] = new JsonArray(strike.Height, strike.RadiusLow, strike.RadiusHigh),
                    ["time"] = new JsonArray(strike.GapMin, strike.GapMax, strike.Duration),
                    ["light"] = new JsonArray(strike.LightRadius, strike.LightBrightness),
                };
            }
            fx[$"{type}_{StateOf(part.Id)}"] = entry;
        }
        var sounds = new JsonArray();
        var soundSets = new Dictionary<string, string>();
        foreach (var s in p.Sounds)
        {
            if (s.GameSet is { Length: > 0 } set)
            {
                soundSets[s.Id] = set;
                continue;
            }
            string file = s.File == null ? "" : Path.GetFullPath(Path.Combine(projectFolder, s.File));
            if (!File.Exists(file))
            {
                plan.Errors.Add(new ExportMessage("Exp.SoundMissing", s.Id, s.File ?? ""));
                continue;
            }
            byte[] ogg = source.Read(file);
            if (ogg.Length < 4 || Encoding.ASCII.GetString(ogg, 0, 4) != "OggS")
            {
                plan.Errors.Add(new ExportMessage("Exp.NotOgg", s.Id, s.File ?? ""));
                continue;
            }
            string rel = $"sounds/{type}_{StateOf(s.Id)}.ogg";
            plan.Add(rel, ogg, "sound");
            var row = new JsonArray(tag, rel, type, StateOf(s.Id), s.Loop ? 1 : 0, s.Volume, s.Range);
            if (s.Frequency != 1)
                row.Add(s.Frequency);
            sounds.Add(row);
            soundSets[s.Id] = "CG_Anom_" + ExportText.Title($"{type}_{StateOf(s.Id)}") + "_SoundSet";
        }

        string slotId = $"{type}_{infix.TrimEnd('_')}";
        var slot = new JsonObject { ["id"] = slotId, ["type"] = ExportText.Upper(type), ["donor"] = "CG", ["states"] = new JsonArray() };
        foreach (var state in p.States)
        {
            var st = new JsonObject
            {
                ["name"] = ExportText.Upper(state.Name), ["over_idle"] = state.OverIdle,
                ["particles"] = new JsonArray(), ["sounds"] = new JsonArray(), ["lights"] = new JsonArray(),
            };
            foreach (var item in state.Items)
            {
                var found = p.Find(item.Ref);
                if (item.Delay > 0 && found is PartEntry or SoundEntry)
                    plan.Notes.Add(new ExportMessage("Exp.CgNoDelay", state.Name, item.Ref));
                switch (found)
                {
                    case PartEntry part when part.Around > 0:
                        if (st["around"] != null)
                            plan.Notes.Add(new ExportMessage("Exp.CgOneAround", state.Name));
                        else
                            st["around"] = new JsonArray(Registered(part.Id), part.Around);
                        plan.Notes.Add(new ExportMessage("Exp.CgAroundTint", part.Id));
                        break;
                    case FlashEntry fl:
                        if (st["flash"] != null)
                            plan.Notes.Add(new ExportMessage("Exp.CgOneFlash", state.Name));
                        else
                            st["flash"] = new JsonArray(fl.Full, fl.Max, fl.At[0], fl.At[1], fl.At[2]);
                        if (item.Delay > 0)
                            plan.Notes.Add(new ExportMessage("Exp.CgNoDelay", state.Name, item.Ref));
                        break;
                    case PartEntry part when !mainOf.ContainsKey(part.Id):
                        st["particles"]!.AsArray().Add(Registered(part.Id));
                        if (part.PathKeys != null)
                            plan.Notes.Add(new ExportMessage("Exp.CgNoPath", part.Id));
                        break;
                    case SoundEntry snd when soundSets.ContainsKey(snd.Id):
                        st["sounds"]!.AsArray().Add(soundSets[snd.Id]);
                        break;
                    case LightEntry l:
                        st["lights"]!.AsArray().Add(new JsonObject
                        {
                            ["radius"] = l.Radius, ["brightness"] = l.Brightness, ["color"] = Arr(l.Color), ["offset"] = Arr(l.Offset),
                            ["flicker"] = new JsonArray(l.FlickerAmp, l.FlickerSpeed), ["life"] = l.Life, ["fade"] = l.FadeOut,
                            ["haze"] = new JsonArray(l.HazeRadius, l.HazePower), ["shadow"] = l.Shadow, ["delay"] = Math.Max(0, item.Delay), ["dark"] = false,
                        });
                        if (l.Curve.Count > 0 || l.Anim != null)
                            plan.Notes.Add(new ExportMessage("Exp.CgNoCurve", l.Id));
                        break;
                    case HazeEntry h:
                        st["lights"]!.AsArray().Add(new JsonObject
                        {
                            ["radius"] = h.Radius, ["brightness"] = 1, ["color"] = new JsonArray(0.01, 0.01, 0.01), ["offset"] = Arr(h.At),
                            ["flicker"] = new JsonArray(0, 0), ["life"] = h.Life, ["fade"] = h.Fade, ["haze"] = new JsonArray(h.Radius, h.Power),
                            ["shadow"] = false, ["delay"] = Math.Max(0, item.Delay), ["dark"] = true,
                        });
                        break;
                    case ScreenEntry sc:
                        plan.Notes.Add(new ExportMessage("Exp.CgNoScreen", sc.Id));
                        break;
                }
            }
            slot["states"]!.AsArray().Add(st);
        }
        if (p.States.Any(s => s.Duration > 0))
            plan.Notes.Add(new ExportMessage("Exp.CgManualStates"));
        // The showcase labels a state with the stringtable key STR_CG_ANOM_SHOW_S_<NAME> of its mod (found above the
        // slot file); a name without a key shows the key itself.
        string slotFull = Path.GetFullPath(Path.Combine(projectFolder, cg.SlotFile));
        for (var dir = Path.GetDirectoryName(slotFull); dir != null; dir = Path.GetDirectoryName(dir))
        {
            string table = Path.Combine(dir, "stringtable.csv");
            if (!File.Exists(table))
                continue;
            string text = File.ReadAllText(table);
            foreach (var s in p.States.Where(s => !text.Contains("\"STR_CG_ANOM_SHOW_S_" + ExportText.Upper(s.Name) + "\"", StringComparison.Ordinal)))
                plan.Notes.Add(new ExportMessage("Exp.CgNoLabel", s.Name, "STR_CG_ANOM_SHOW_S_" + ExportText.Upper(s.Name)));
            break;
        }

        var config = new JsonObject
        {
            ["archetype"] = p.Anomaly.Archetype,
            ["shells"] = new JsonObject(p.Anomaly.Shells.Where(s => s.Value.R > 0).Select(s =>
                KeyValuePair.Create(s.Key, (JsonNode?)new JsonObject { ["r"] = s.Value.R, ["yMin"] = s.Value.YMin, ["yMax"] = s.Value.YMax }))),
            ["timing"] = new JsonObject(StateCycle.Timing(p).Where(t => t.Value > 0).Select(t => KeyValuePair.Create(t.Key, (JsonNode?)JsonValue.Create(t.Value)))),
            ["visual"] = slotId,
        };

        projects[p.Name] = new JsonObject { ["wave"] = wave, ["fx"] = fx, ["sounds"] = sounds, ["slot"] = slot, ["config"] = config };
        var ordered = new JsonObject
        {
            ["tag"] = tag, ["refs"] = refs,
            ["projects"] = new JsonObject(projects.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => KeyValuePair.Create(kv.Key, kv.Value?.DeepClone()))),
        };
        string json = ordered.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        plan.AddShared(jsonPath, Encoding.UTF8.GetBytes(json), "data");
        string slotPath = Path.GetFullPath(Path.Combine(projectFolder, cg.SlotFile));
        if (File.Exists(slotPath) && !File.ReadAllText(slotPath).StartsWith("class " + SlotClass, StringComparison.Ordinal))
            plan.Errors.Add(new ExportMessage("Exp.SlotNotOurs", slotPath));
        // Read back from the text, so every number is a JSON number whatever type it was built from.
        plan.AddShared(slotPath, ExportText.Crlf(SlotFile(JsonNode.Parse(json)!["projects"]!.AsObject())), "script");
        plan.Finish();
        return plan;
    }

    private static JsonObject Part(PartEntry n, string state)
    {
        var o = new JsonObject { ["state"] = state, ["spin"] = n.Spin, ["near"] = n.Near };
        if (n.Stop is > 0 and var stop)
            o["stop"] = stop;
        return o;
    }

    private static JsonArray Arr(IReadOnlyList<float> v) => new(v.Take(3).Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());

    // The showcase slots of every exported project, in the CG showcase's own API (CG_AnomalyShowcase.c,
    // CG_AnomalyShowcaseLight.c); lights are built in locals (no call chain passed as an argument).
    public static string SlotFile(JsonObject projects)
    {
        string N(JsonNode? v) => ExportText.Num(v?.GetValue<double>() ?? 0);
        string V(JsonNode? v) => string.Join(" ", v!.AsArray().Select(N));
        var w = new CodeWriter();
        w.Open($"class {SlotClass}");
        w.Open("static void Fill(array<ref CG_AnomShowSlot> slots)");
        w.Line("CG_AnomShowSlot sl;");
        w.Line("CG_AnomShowState st;");
        w.Line("CG_AnomShowLightSpec li;");
        foreach (var (_, project) in projects)
        {
            var slot = project!["slot"]!;
            w.Line($"sl = new CG_AnomShowSlot(\"{slot["id"]}\", \"{slot["type"]}\", \"{slot["donor"]}\");");
            w.Line("slots.Insert(sl);");
            foreach (var st in slot["states"]!.AsArray())
            {
                w.Line($"st = sl.Add(\"{st!["name"]}\");");
                if (st["over_idle"]!.GetValue<bool>())
                    w.Line("st.OverIdle();");
                foreach (var ptc in st["particles"]!.AsArray())
                    w.Line($"st.Ptc(ParticleList.{ptc});");
                foreach (var snd in st["sounds"]!.AsArray())
                    w.Line($"st.Snd(\"{snd}\");");
                if (st["around"] is JsonArray around)
                    w.Line($"st.Around(ParticleList.{around[0]}, {N(around[1])});");
                if (st["flash"] is JsonArray flash)
                    w.Line($"st.Flash({N(flash[0])}, {N(flash[1])}, \"{N(flash[2])} {N(flash[3])} {N(flash[4])}\");");
                foreach (var l in st["lights"]!.AsArray())
                {
                    w.Line($"li = CG_AnomShowLightSpec.Make({N(l!["radius"])}, {N(l["brightness"])}, \"{V(l["color"])}\", \"{V(l["offset"])}\");");
                    var flicker = l["flicker"]!.AsArray();
                    if (flicker[0]!.GetValue<double>() != 0 || flicker[1]!.GetValue<double>() != 0)
                        w.Line($"li.Flicker({N(flicker[0])}, {N(flicker[1])});");
                    bool dark = l["dark"]!.GetValue<bool>();
                    double life = l["life"]!.GetValue<double>();
                    if (life > 0)
                        w.Line($"li.Life({N(life)}, {N(l["fade"])});");
                    else if (!dark)
                        w.Line($"li.Fade({N(l["fade"])});");
                    var haze = l["haze"]!.AsArray();
                    if (haze[0]!.GetValue<double>() > 0)
                        w.Line($"li.Haze({N(haze[0])}, {N(haze[1])});");
                    if (l["shadow"]!.GetValue<bool>())
                        w.Line("li.Shadow();");
                    if (l["delay"]!.GetValue<double>() > 0)
                        w.Line($"li.Delay({N(l["delay"])});");
                    w.Line("st.Light(li);");
                }
            }
        }
        w.Close();
        w.Close();
        return w.ToString();
    }
}
