using System.Text;
using Pfx.Core.Model;

namespace Pfx.Core.Export;

// The "DayZ mod" export profile (spec 3.13, O5): everything into one folder the tool owns inside the user's mod
// (<mod>\<folder>\): parts, materials and textures under the mod's game path, sounds with their shaders and sets, a
// config.cpp with its own CfgPatches and script modules, and the client runtime with the user's prefix. Two mods with
// different prefixes share no class, patch, sound or particle name.
public static class DayzModExport
{
    public const string MarkerName = "pfx_owned.txt";

    public static ExportPlan Plan(ProjectFile p, string projectFolder, IExportSource source)
    {
        var d = p.Export.Dayz;
        string modFolder = d.ModFolder.Length == 0 ? "" : Path.GetFullPath(Path.Combine(projectFolder, d.ModFolder));
        string folder = d.Folder.Trim('/', '\\');
        var plan = new ExportPlan(Path.Combine(modFolder.Length > 0 ? modFolder : projectFolder, folder, MarkerName));
        if (modFolder.Length == 0)
            plan.Errors.Add(new ExportMessage("Exp.NoModFolder"));
        if (!ExportText.IsPrefix(d.Prefix))
            plan.Errors.Add(new ExportMessage("Exp.BadPrefix", d.Prefix));
        if (!ProjectNames.IsId(folder))
            plan.Errors.Add(new ExportMessage("Exp.BadFolder", d.Folder));
        // The game path of the mod folder: as set, else its path under the data root that holds it (what a packer
        // makes the PBO prefix of a folder on the work drive), else its name.
        string dataPrefix = (d.DataPrefix.Length > 0 ? d.DataPrefix : modFolder.Length > 0 ? source.GamePath(modFolder) ?? Path.GetFileName(modFolder) : "")
            .Replace('\\', '/').Trim('/');
        if (dataPrefix.Length == 0)
            plan.Errors.Add(new ExportMessage("Exp.NoDataPrefix"));
        Common(p, plan);
        if (!plan.CanWrite)
            return plan;

        string P = d.Prefix;
        string name = p.Name;
        string gameBase = dataPrefix + "/" + folder;
        var assets = new AssetCopy(source, plan, gameBase, "");
        var partConst = new Dictionary<string, string>();
        foreach (var part in p.Parts)
        {
            string disk = Path.GetFullPath(Path.Combine(projectFolder, part.Path));
            if (!File.Exists(disk))
            {
                plan.Errors.Add(new ExportMessage("Exp.PartMissing", part.Id, disk));
                continue;
            }
            plan.Add($"particles/{FileStem(P, name, part.Id)}.ptc", assets.Part(source.Read(disk), part.Id + ".ptc"), "particle");
            partConst[part.Id] = $"{P}_{ExportText.Upper(name)}_{ExportText.Upper(part.Id)}";
        }
        var soundSets = new Dictionary<string, string>();
        var shaders = new List<SoundEntry>();
        foreach (var s in p.Sounds)
        {
            if (s.GameSet is { Length: > 0 } set)
            {
                soundSets[s.Id] = set;
                continue;
            }
            if (s.File == null || !File.Exists(Path.Combine(projectFolder, s.File)))
            {
                plan.Errors.Add(new ExportMessage("Exp.SoundMissing", s.Id, s.File ?? ""));
                continue;
            }
            byte[] ogg = source.Read(Path.GetFullPath(Path.Combine(projectFolder, s.File)));
            if (ogg.Length < 4 || Encoding.ASCII.GetString(ogg, 0, 4) != "OggS")
            {
                plan.Errors.Add(new ExportMessage("Exp.NotOgg", s.Id, s.File));
                continue;
            }
            plan.Add($"sounds/{name}_{s.Id}.ogg", ogg, "sound");
            soundSets[s.Id] = $"{P}_{ExportText.Title(name)}_{ExportText.Title(s.Id)}_SoundSet";
            shaders.Add(s);
        }

        var uses = UsesOf(p);
        plan.Add("config.cpp", ExportText.Crlf(Config(P, dataPrefix, folder, name, shaders, uses)), "config");
        var list = new StringBuilder();
        list.Append("modded class ParticleList\n{\n");
        foreach (var part in p.Parts.Where(x => partConst.ContainsKey(x.Id)))
            list.Append($"\tstatic const int {partConst[part.Id]} = RegisterParticle(\"{gameBase}/particles/\", \"{FileStem(P, name, part.Id)}\");\n");
        list.Append("}\n");
        if (uses.HasFlag(DayzRuntime.Uses.Tint))
            list.Append('\n').Append(DayzRuntime.Tint(P));
        plan.Add($"scripts/3_Game/{P}_FxList.c", ExportText.Crlf(list.ToString()), "script");
        string world = DayzRuntime.World(P, uses) + "\n" + Defs(p, P, partConst, soundSets, uses);
        plan.Add($"scripts/4_World/{P}_Fx.c", ExportText.Crlf(world), "script");
        plan.Example = Example(p, P);
        if (p.Sounds.Any(s => s.GameSet != null && s.Loop))
            plan.Notes.Add(new ExportMessage("Exp.GameSetLoop"));
        plan.Finish();
        return plan;
    }

    // The vanilla particle list also keys particles by file name alone (ParticleList.m_ParticleNames; a second mod with
    // the same name logs "Duplicate particle filename"), so the file name carries the prefix too.
    public static string FileStem(string prefix, string name, string part) => $"{prefix.ToLowerInvariant()}_{name}_{part}";

    // Checks every profile makes: an id, at least one state, references that exist.
    public static void Common(ProjectFile p, ExportPlan plan)
    {
        if (!ProjectNames.IsId(p.Name))
            plan.Errors.Add(new ExportMessage("Exp.BadName", p.Name));
        if (p.States.Count == 0)
            plan.Errors.Add(new ExportMessage("Exp.NoStates"));
        foreach (var s in p.States)
            foreach (var item in s.Items.Where(i => p.Find(i.Ref) == null))
                plan.Errors.Add(new ExportMessage("Exp.BadRef", s.Name, item.Ref));
        foreach (var k in p.Strikes.Where(k => p.Parts.All(x => x.Id != k.Part)))
            plan.Errors.Add(new ExportMessage("Exp.StrikePart", k.Id, k.Part));
        var ids = p.Items().Select(i => i.Id).ToList();
        foreach (var dup in ids.GroupBy(i => i).Where(g => g.Count() > 1))
            plan.Errors.Add(new ExportMessage("Exp.DuplicateId", dup.Key));
        foreach (var bad in ids.Where(i => !ProjectNames.IsId(i)))
            plan.Errors.Add(new ExportMessage("Exp.BadId", bad));
    }

    // Only what the states use is generated.
    public static DayzRuntime.Uses UsesOf(ProjectFile p)
    {
        var u = DayzRuntime.Uses.None;
        var used = p.States.SelectMany(s => s.Items).Select(i => p.Find(i.Ref)).OfType<IProjectItem>().ToList();
        foreach (var item in used)
        {
            switch (item)
            {
                case PartEntry part:
                    if (part.Spin != 0)
                        u |= DayzRuntime.Uses.Spin;
                    if (part.Near > 0)
                        u |= DayzRuntime.Uses.Near;
                    if (part.Stop is > 0)
                        u |= DayzRuntime.Uses.Stops;
                    if (part.PathKeys is { Times.Length: > 0 })
                        u |= DayzRuntime.Uses.Paths;
                    if (part.Around > 0)
                        u |= DayzRuntime.Uses.Around;
                    break;
                case FlashEntry:
                    u |= DayzRuntime.Uses.Flash;
                    break;
                case LightEntry light:
                    u |= DayzRuntime.Uses.Lights;
                    if (light.Curve.Count > 0)
                        u |= DayzRuntime.Uses.Curves;
                    if (light.Anim is { Frames.Count: > 0, Fps: > 0 })
                        u |= DayzRuntime.Uses.Anims;
                    break;
                case HazeEntry:
                    u |= DayzRuntime.Uses.Lights;
                    break;
                case SoundEntry:
                    u |= DayzRuntime.Uses.Sounds;
                    break;
                case StrikeEntry:
                    u |= DayzRuntime.Uses.Strikes | DayzRuntime.Uses.Lights;
                    break;
                case ScreenEntry:
                    u |= DayzRuntime.Uses.Tint;
                    break;
            }
        }
        return u;
    }

    private static string Config(string P, string dataPrefix, string folder, string name, List<SoundEntry> sounds, DayzRuntime.Uses uses)
    {
        string scripts = dataPrefix + "/" + folder + "/scripts/";
        var required = new List<string> { "DZ_Data", "DZ_Scripts" };
        if (sounds.Count > 0)
            required.Add("DZ_Sounds_Effects");
        var w = new CodeWriter();
        w.Open("class CfgPatches");
        w.Open($"class {P}_Fx");
        w.Line("units[] = {};");
        w.Line("weapons[] = {};");
        w.Line("requiredVersion = 0.1;");
        w.Line("requiredAddons[] = {" + string.Join(", ", required.Select(r => $"\"{r}\"")) + "};");
        w.Close("};");
        w.Close("};");
        w.Blank();
        w.Open("class CfgMods");
        w.Open($"class {P}_Fx");
        w.Line($"dir = \"{dataPrefix}\";");
        w.Line($"name = \"{P} Fx\";");
        w.Line("hideName = 1;");
        w.Line("hidePicture = 1;");
        w.Line("type = \"mod\";");
        w.Line("dependencies[] = {\"Game\", \"World\"};");
        w.Open("class defs");
        w.Open("class gameScriptModule");
        w.Line("value = \"\";");
        w.Line($"files[] = {{\"{scripts}3_Game\"}};");
        w.Close("};");
        w.Open("class worldScriptModule");
        w.Line("value = \"\";");
        w.Line($"files[] = {{\"{scripts}4_World\"}};");
        w.Close("};");
        w.Close("};");
        w.Close("};");
        w.Close("};");
        if (sounds.Count > 0)
        {
            string samples = (dataPrefix + "/" + folder + "/sounds/").Replace('/', '\\');
            w.Blank();
            w.Open("class CfgSoundShaders");
            w.Open($"class {P}_Fx_Base_SoundShader");
            w.Line("volume = 1;");
            w.Line("range = 50;");
            w.Close("};");
            foreach (var s in sounds)
            {
                w.Open($"class {P}_{ExportText.Title(name)}_{ExportText.Title(s.Id)}_SoundShader: {P}_Fx_Base_SoundShader");
                w.Line($"samples[] = {{{{\"{samples}{name}_{s.Id}\", 1}}}};");
                w.Line($"volume = {ExportText.Num(s.Volume)};");
                w.Line($"range = {ExportText.Num(s.Range)};");
                if (s.Frequency != 1)
                    w.Line($"frequency = {ExportText.Num(s.Frequency)};");
                w.Close("};");
            }
            w.Close("};");
            w.Blank();
            w.Open("class CfgSoundSets");
            w.Open($"class {P}_Fx_Base_SoundSet");
            w.Line("sound3DProcessingType = \"character3DProcessingType\";");
            w.Line("volumeCurve = \"characterAttenuationCurve\";");
            w.Line("spatial = 1;");
            w.Line("doppler = 0;");
            w.Line("loop = 0;");
            w.Close("};");
            foreach (var s in sounds)
            {
                string cls = $"{P}_{ExportText.Title(name)}_{ExportText.Title(s.Id)}";
                w.Open($"class {cls}_SoundSet: {P}_Fx_Base_SoundSet");
                w.Line($"soundShaders[] = {{\"{cls}_SoundShader\"}};");
                w.Line($"loop = {(s.Loop ? 1 : 0)};");
                w.Close("};");
            }
            w.Close("};");
        }
        return w.ToString();
    }

    // The project's data as one class that fills the definitions on first use (no static initialisers: the order of
    // #InitGlobals between files is not defined).
    private static string Defs(ProjectFile p, string P, Dictionary<string, string> partConst, Dictionary<string, string> soundSets, DayzRuntime.Uses u)
    {
        string V(IReadOnlyList<float> v) => $"Vector({ExportText.Num(v[0])}, {ExportText.Num(v[1])}, {ExportText.Num(v[2])})";
        string N(double v) => ExportText.Num(v);
        var w = new CodeWriter();
        w.Open($"class {P}_FxDefs");
        w.Line($"protected static ref map<string, ref {P}_FxDef> s_Defs;");
        w.Blank();
        w.Open($"static {P}_FxDef Get(string name)");
        w.Line("if (!s_Defs)");
        w.Line("\tFxFill();");
        w.Line("return s_Defs.Get(name);");
        w.Close();
        w.Blank();
        w.Open("protected static void FxFill()");
        w.Line($"s_Defs = new map<string, ref {P}_FxDef>();");
        w.Line($"{P}_FxDef d = new {P}_FxDef();");
        w.Line($"s_Defs.Insert(\"{p.Name}\", d);");
        w.Line($"{P}_FxStateDef s;");
        w.Line($"{P}_FxPartDef part;");
        if (u.HasFlag(DayzRuntime.Uses.Lights))
            w.Line($"{P}_FxLightDef light;");
        if (u.HasFlag(DayzRuntime.Uses.Strikes))
            w.Line($"{P}_FxStrikeDef strike;");
        foreach (var state in p.States)
        {
            w.Line($"s = d.AddState(\"{state.Name}\", {N(state.Duration)}, {(state.OverIdle ? "true" : "false")});");
            foreach (var item in state.Items)
            {
                string delay = N(Math.Max(0, item.Delay));
                switch (p.Find(item.Ref))
                {
                    case PartEntry part when partConst.ContainsKey(part.Id):
                        w.Line($"part = s.AddPart(ParticleList.{partConst[part.Id]}, {delay});");
                        if (part.Spin != 0)
                            w.Line($"part.m_Spin = {N(part.Spin)};");
                        if (part.Near > 0)
                            w.Line($"part.m_Near = {N(part.Near)};");
                        if (part.Stop is > 0 and var stop)
                            w.Line($"part.m_Stop = {N(stop)};");
                        if (part.Around > 0)
                        {
                            w.Line($"part.m_Around = {N(part.Around)};");
                            w.Line($"part.m_Swirl = {N(part.Swirl)};");
                            w.Line($"part.m_SwirlMin = {N(part.SwirlMin)};");
                        }
                        if (part.PathKeys is { Times.Length: > 0 } keys)
                        {
                            for (int i = 0; i < keys.Times.Length && i < keys.Points.Length; i++)
                            {
                                w.Line($"part.m_PathTimes.Insert({N(keys.Times[i])});");
                                w.Line($"part.m_PathPoints.Insert({V(keys.Points[i])});");
                            }
                            if (keys.YawAlong)
                                w.Line("part.m_YawAlong = true;");
                        }
                        break;
                    case LightEntry l:
                        w.Line($"light = s.AddLight({delay}, {N(l.Radius)}, {N(l.Brightness)}, {V(l.Color)}, {V(l.Offset)});");
                        if (l.FlickerAmp != 0 || l.FlickerSpeed != 0)
                        {
                            w.Line($"light.m_FlickerAmp = {N(l.FlickerAmp)};");
                            w.Line($"light.m_FlickerSpeed = {N(l.FlickerSpeed)};");
                        }
                        if (l.Life > 0)
                            w.Line($"light.m_Life = {N(l.Life)};");
                        w.Line($"light.m_Fade = {N(l.FadeOut)};");
                        if (l.Shadow)
                            w.Line("light.m_Shadow = true;");
                        if (l.HazeRadius > 0)
                        {
                            w.Line($"light.m_HazeRadius = {N(l.HazeRadius)};");
                            w.Line($"light.m_HazePower = {N(l.HazePower)};");
                        }
                        foreach (var key in l.Curve.Where(k => k.Length >= 2))
                        {
                            w.Line($"light.m_KeyTimes.Insert({N(key[0])});");
                            w.Line($"light.m_KeyValues.Insert({N(key[1])});");
                        }
                        if (l.Curve.Count > 0 && l.CurveLoop)
                            w.Line("light.m_KeyLoop = true;");
                        if (l.Anim is { Frames.Count: > 0, Fps: > 0 } anim)
                        {
                            w.Line($"light.m_Fps = {N(anim.Fps)};");
                            foreach (var f in anim.Frames)
                                w.Line($"light.m_Frames.Insert({V(f)});");
                        }
                        break;
                    case HazeEntry h:
                        w.Line($"light = s.AddLight({delay}, {N(h.Radius)}, 1, Vector(0.01, 0.01, 0.01), {V(h.At)});");
                        w.Line($"light.m_HazeRadius = {N(h.Radius)};");
                        w.Line($"light.m_HazePower = {N(h.Power)};");
                        if (h.Life > 0)
                            w.Line($"light.m_Life = {N(h.Life)};");
                        w.Line($"light.m_Fade = {N(h.Fade)};");
                        break;
                    case SoundEntry snd when soundSets.ContainsKey(snd.Id):
                        w.Line($"s.AddSound(\"{soundSets[snd.Id]}\", {delay}, {(snd.Loop ? "true" : "false")});");
                        break;
                    case StrikeEntry k when partConst.ContainsKey(k.Part):
                        w.Line($"strike = s.AddStrike(ParticleList.{partConst[k.Part]}, {delay});");
                        w.Line($"strike.m_Height = {N(k.Height)};");
                        w.Line($"strike.m_RadiusLow = {N(k.RadiusLow)};");
                        w.Line($"strike.m_RadiusHigh = {N(k.RadiusHigh)};");
                        w.Line($"strike.m_GapMin = {N(k.GapMin)};");
                        w.Line($"strike.m_GapMax = {N(k.GapMax)};");
                        w.Line($"strike.m_Duration = {N(k.Duration)};");
                        w.Line($"strike.m_LightRadius = {N(k.LightRadius)};");
                        w.Line($"strike.m_LightBrightness = {N(k.LightBrightness)};");
                        break;
                    case ScreenEntry sc:
                        w.Line($"s.AddTint({V(sc.Color)}, {N(sc.Radius)}, {N(sc.Fade)}, {delay});");
                        break;
                    case FlashEntry fl:
                        w.Line($"s.AddFlash({V(fl.At)}, {N(fl.Full)}, {N(fl.Max)}, {delay});");
                        break;
                }
            }
        }
        w.Close();
        w.Close();
        return w.ToString();
    }

    public static string Example(ProjectFile p, string P)
    {
        string first = p.States.FirstOrDefault()?.Name ?? "idle";
        string other = p.States.Skip(1).FirstOrDefault()?.Name ?? first;
        return string.Join("\n",
            $"protected ref {P}_Fx m_Fx;",
            "",
            "// client side, e.g. when the object that shows the effect appears",
            $"m_Fx = new {P}_Fx(\"{p.Name}\", GetPosition());",
            $"m_Fx.Play(\"{first}\");",
            "",
            "// a state the server sent; seconds since it began, for a player who joins late",
            $"m_Fx.Play(\"{other}\", secondsSinceStart);",
            "",
            "// all states in order, each for its length (testing)",
            $"m_Fx.Play(\"{first}\", 0, true);",
            "",
            "m_Fx.Stop();");
    }
}
