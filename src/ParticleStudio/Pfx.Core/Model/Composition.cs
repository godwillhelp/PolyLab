using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pfx.Core.Model;

// The .ndzpfx project (spec 3.8): the parts (.ptc files) of one effect and everything the .ptc cannot hold, run by
// script in the game (spec 2.7): lights, heat hazes, sounds, screen effects, strikes, per-part script data (turn, path,
// near radius, hard stop) and the states that start them, with the anomaly settings and the export profile. Times are
// seconds, distances metres, colours linear 0..1 as the light API takes them.
public sealed class ProjectFile
{
    public const int CurrentVersion = 2;

    [JsonPropertyName("version")] public int Version { get; set; } = CurrentVersion;
    // The effect's id: names of exported files and classes start with it (letters, digits, '_').
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    // Paths relative to the project file, with '/' separators, so a project moves with its folder. Version 1 wrote
    // parts as plain paths; PartEntry reads both.
    [JsonPropertyName("parts")] public List<PartEntry> Parts { get; set; } = new();
    [JsonPropertyName("materials")] public List<string> Materials { get; set; } = new();
    [JsonPropertyName("lights")] public List<LightEntry> Lights { get; set; } = new();
    [JsonPropertyName("hazes")] public List<HazeEntry> Hazes { get; set; } = new();
    [JsonPropertyName("sounds")] public List<SoundEntry> Sounds { get; set; } = new();
    [JsonPropertyName("screens")] public List<ScreenEntry> Screens { get; set; } = new();
    [JsonPropertyName("strikes")] public List<StrikeEntry> Strikes { get; set; } = new();
    [JsonPropertyName("flashes")] public List<FlashEntry> Flashes { get; set; } = new();
    [JsonPropertyName("states")] public List<StateEntry> States { get; set; } = new();
    // Applied ready solutions and generators (spec 3.10), in the order they were added; each knows what it changed.
    [JsonPropertyName("recipes")] public List<RecipeNode> Recipes { get; set; } = new();
    [JsonPropertyName("anomaly")] public AnomalySettings Anomaly { get; set; } = new();
    [JsonPropertyName("export")] public ExportSettings Export { get; set; } = new();

    public IEnumerable<IProjectItem> Items() =>
        Parts.Cast<IProjectItem>().Concat(Lights).Concat(Hazes).Concat(Sounds).Concat(Screens).Concat(Strikes).Concat(Flashes);

    public IProjectItem? Find(string id) => Items().FirstOrDefault(i => i.Id == id);
}

public enum ItemKind { Part, Light, Haze, Sound, Screen, Strike, Flash }

// Everything a state can start: one id space over parts, lights, hazes, sounds, screen effects and strikes.
public interface IProjectItem
{
    string Id { get; set; }
    ItemKind Kind { get; }
}

[JsonConverter(typeof(PartEntryConverter))]
public sealed class PartEntry : IProjectItem
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    // Script turn of the source, degrees per second (+ = clockwise seen from above); LocalTransform 1 emitters follow.
    [JsonPropertyName("spin")] public float Spin { get; set; }
    // A drawn source path (path tool, spec 3.7 use 3): played by script, straight between keys, looping.
    [JsonPropertyName("path_keys")] public PathKeys? PathKeys { get; set; }
    // Births only while the camera is within this many metres (0 = always): one source, births scaled 0 or 1 (2.11).
    [JsonPropertyName("near")] public float Near { get; set; }
    // Seconds after the part starts at which every particle is removed at once (StopParticle IMMEDIATE); null = none.
    [JsonPropertyName("stop")] public float? Stop { get; set; }
    // Dust around the player (CG tornado): while the camera is within this many metres the part plays at the ground
    // under the camera, turned along the swirl of the project's spinning parts; 0 = an ordinary part.
    [JsonPropertyName("around")] public float Around { get; set; }
    // The swirl the around dust follows: circulation in m²/s (core rate in radians per second × core radius²) and the
    // core radius inside which the speed stops growing (own_tornado SWIRL).
    [JsonPropertyName("swirl")] public float Swirl { get; set; }
    [JsonPropertyName("swirl_min")] public float SwirlMin { get; set; }

    [JsonIgnore] public ItemKind Kind => ItemKind.Part;
}

public sealed class PathKeys
{
    [JsonPropertyName("times")] public float[] Times { get; set; } = Array.Empty<float>();
    // Positions relative to the effect origin, x y z per key.
    [JsonPropertyName("points")] public float[][] Points { get; set; } = Array.Empty<float[]>();
    [JsonPropertyName("yaw_along")] public bool YawAlong { get; set; }
}

public sealed class PartEntryConverter : JsonConverter<PartEntry>
{
    public override PartEntry? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            string path = reader.GetString() ?? "";
            return new PartEntry { Path = path, Id = ProjectNames.Id(System.IO.Path.GetFileNameWithoutExtension(path)) };
        }
        using var doc = JsonDocument.ParseValue(ref reader);
        return doc.RootElement.Deserialize<PartData>(options)?.ToEntry();
    }

    public override void Write(Utf8JsonWriter writer, PartEntry value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, PartData.From(value), options);

    // The same members without the converter attribute (the converter would call itself).
    private sealed class PartData
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("path")] public string Path { get; set; } = "";
        [JsonPropertyName("spin")] public float Spin { get; set; }
        [JsonPropertyName("path_keys")] public PathKeys? PathKeys { get; set; }
        [JsonPropertyName("near")] public float Near { get; set; }
        [JsonPropertyName("stop")] public float? Stop { get; set; }
        [JsonPropertyName("around")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public float Around { get; set; }
        [JsonPropertyName("swirl")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public float Swirl { get; set; }
        [JsonPropertyName("swirl_min")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public float SwirlMin { get; set; }

        public PartEntry ToEntry() => new()
        {
            Id = Id, Path = Path, Spin = Spin, PathKeys = PathKeys, Near = Near, Stop = Stop, Around = Around, Swirl = Swirl, SwirlMin = SwirlMin,
        };

        public static PartData From(PartEntry e) => new()
        {
            Id = e.Id, Path = e.Path, Spin = e.Spin, PathKeys = e.PathKeys, Near = e.Near, Stop = e.Stop, Around = e.Around, Swirl = e.Swirl, SwirlMin = e.SwirlMin,
        };
    }
}

// A script point light (ScriptedLightBase): what the CG runtime's light spec holds, plus a brightness curve and an
// X-Ray light animation (lanim) that only the generated DayZ-mod runtime plays.
public sealed class LightEntry : IProjectItem
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("offset")] public float[] Offset { get; set; } = { 0, 1, 0 };
    [JsonPropertyName("color")] public float[] Color { get; set; } = { 1, 0.6f, 0.3f };
    [JsonPropertyName("brightness")] public float Brightness { get; set; } = 1;
    [JsonPropertyName("radius")] public float Radius { get; set; } = 6;
    [JsonPropertyName("flicker_amp")] public float FlickerAmp { get; set; }
    [JsonPropertyName("flicker_speed")] public float FlickerSpeed { get; set; }
    // Seconds the light lives (0 = as long as its state), then fades out over FadeOut seconds.
    [JsonPropertyName("life")] public float Life { get; set; }
    [JsonPropertyName("fade_out")] public float FadeOut { get; set; } = 1;
    [JsonPropertyName("shadow")] public bool Shadow { get; set; }
    // Brightness multiplier keys: [seconds since start, multiplier], straight between keys; Loop repeats them.
    [JsonPropertyName("curve")] public List<float[]> Curve { get; set; } = new();
    [JsonPropertyName("curve_loop")] public bool CurveLoop { get; set; }
    [JsonPropertyName("anim")] public LightAnim? Anim { get; set; }
    // Heat haze carried by this light (EnableHeatHaze): radius in metres and power; 0 = none.
    [JsonPropertyName("haze_radius")] public float HazeRadius { get; set; }
    [JsonPropertyName("haze_power")] public float HazePower { get; set; }

    [JsonIgnore] public ItemKind Kind => ItemKind.Light;
}

// Colour frames of an X-Ray light animation (lanims.xr), played at Fps and looped.
public sealed class LightAnim
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("fps")] public float Fps { get; set; }
    [JsonPropertyName("frames")] public List<float[]> Frames { get; set; } = new();
}

// A heat haze on its own: a near-black light with EnableHeatHaze (spec 2.7), as the CG showcase's AddHaze.
public sealed class HazeEntry : IProjectItem
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("at")] public float[] At { get; set; } = { 0, 0.5f, 0 };
    [JsonPropertyName("radius")] public float Radius { get; set; } = 2;
    [JsonPropertyName("power")] public float Power { get; set; } = 0.02f;
    // Seconds it lives (0 = as long as its state) and its fade-out.
    [JsonPropertyName("life")] public float Life { get; set; }
    [JsonPropertyName("fade")] public float Fade { get; set; }

    [JsonIgnore] public ItemKind Kind => ItemKind.Haze;
}

// A sound: an .ogg file of the project (copied into the export, a sound shader and set generated) or a sound set the
// game already has (referenced by name, nothing generated).
public sealed class SoundEntry : IProjectItem
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("file")] public string? File { get; set; }
    [JsonPropertyName("game_set")] public string? GameSet { get; set; }
    [JsonPropertyName("volume")] public float Volume { get; set; } = 1;
    [JsonPropertyName("range")] public float Range { get; set; } = 50;
    [JsonPropertyName("frequency")] public float Frequency { get; set; } = 1;
    [JsonPropertyName("loop")] public bool Loop { get; set; }

    [JsonIgnore] public ItemKind Kind => ItemKind.Sound;
}

// A screen effect for the local player while within Radius of the effect (a PPE requester, whole screen): "tint"
// multiplies the screen colour by Color, fading in and out over Fade seconds.
public sealed class ScreenEntry : IProjectItem
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "tint";
    [JsonPropertyName("color")] public float[] Color { get; set; } = { 1, 0.5f, 0.2f };
    [JsonPropertyName("radius")] public float Radius { get; set; } = 10;
    [JsonPropertyName("fade")] public float Fade { get; set; } = 3;

    [JsonIgnore] public ItemKind Kind => ItemKind.Screen;
}

// Random strikes while the state runs (CG tornado): every GapMin..GapMax seconds the Part plays for Duration seconds at
// a random height 0..Height on the camera-facing side, RadiusLow at the ground to RadiusHigh at the top, with a white
// light of LightRadius and LightBrightness for the same time.
public sealed class StrikeEntry : IProjectItem
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("part")] public string Part { get; set; } = "";
    [JsonPropertyName("height")] public float Height { get; set; } = 10;
    [JsonPropertyName("radius_low")] public float RadiusLow { get; set; } = 2;
    [JsonPropertyName("radius_high")] public float RadiusHigh { get; set; } = 4;
    [JsonPropertyName("gap_min")] public float GapMin { get; set; } = 3;
    [JsonPropertyName("gap_max")] public float GapMax { get; set; } = 8;
    [JsonPropertyName("duration")] public float Duration { get; set; } = 0.2f;
    [JsonPropertyName("light_radius")] public float LightRadius { get; set; } = 30;
    [JsonPropertyName("light_brightness")] public float LightBrightness { get; set; } = 1;

    [JsonIgnore] public ItemKind Kind => ItemKind.Strike;
}

// A flash that blinds players who face it (CG_AnomFlash, the vanilla flash grenade's effect): when its state reaches the
// delay, a local player whose head sees the point At (no object between) gets the white-out if facing it within 60
// degrees and the ringing either way, at full strength within Full metres, fading to Max metres.
public sealed class FlashEntry : IProjectItem
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("at")] public float[] At { get; set; } = { 0, 1.5f, 0 };
    [JsonPropertyName("full")] public float Full { get; set; } = 3;
    [JsonPropertyName("max")] public float Max { get; set; } = 30;

    [JsonIgnore] public ItemKind Kind => ItemKind.Flash;
}

// One state of the anomaly (idle, charge, active, cooldown, linger …): what it starts and when. Duration is how long the
// state lasts before the next one (0 = until an event: the preview gives it one loop of the scene). OverIdle keeps the
// first state playing underneath (a strike over the idle look).
public sealed class StateEntry
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("duration")] public float Duration { get; set; }
    [JsonPropertyName("over_idle")] public bool OverIdle { get; set; }
    [JsonPropertyName("items")] public List<StateItem> Items { get; set; } = new();
}

public sealed class StateItem
{
    [JsonPropertyName("ref")] public string Ref { get; set; } = "";
    [JsonPropertyName("delay")] public float Delay { get; set; }
}

// The anomaly's server-side numbers (K design §2, config.json types): the machine archetype, the shells (vertical
// cylinders from the anomaly position) and the timing keys that are not state lengths.
public sealed class AnomalySettings
{
    public static readonly string[] Archetypes = { "", "burst", "burn", "pull", "teleport", "catcher" };
    public static readonly string[] ShellNames = { "sense", "hit", "pull" };

    [JsonPropertyName("archetype")] public string Archetype { get; set; } = "";
    [JsonPropertyName("shells")] public Dictionary<string, Shell> Shells { get; set; } = new();
    [JsonPropertyName("tick")] public float Tick { get; set; }
    [JsonPropertyName("hit_delay")] public float HitDelay { get; set; }
}

public sealed class Shell
{
    [JsonPropertyName("r")] public float R { get; set; }
    [JsonPropertyName("y_min")] public float YMin { get; set; }
    [JsonPropertyName("y_max")] public float YMax { get; set; }
}

// Where and how the project is exported (spec 3.13); stored in the project, paths absolute or relative to it.
public sealed class ExportSettings
{
    [JsonPropertyName("profile")] public string Profile { get; set; } = "dayz";
    [JsonPropertyName("dayz")] public DayzModProfile Dayz { get; set; } = new();
    [JsonPropertyName("cg")] public CgProfile Cg { get; set; } = new();
}

public sealed class DayzModProfile
{
    // The mod's source folder on the work drive (packed to one PBO); the game sees it as DataPrefix.
    [JsonPropertyName("mod_folder")] public string ModFolder { get; set; } = "";
    [JsonPropertyName("data_prefix")] public string DataPrefix { get; set; } = "";
    // Class and file prefix of the generated scripts (letters, digits, '_', starting with a letter).
    [JsonPropertyName("prefix")] public string Prefix { get; set; } = "";
    // The folder inside the mod the tool owns.
    [JsonPropertyName("folder")] public string Folder { get; set; } = "fx";
}

public sealed class CgProfile
{
    // The donor folder assemble.py takes (manifest.py's pfx hook); the showcase slot file the tool owns.
    [JsonPropertyName("donor_folder")] public string DonorFolder { get; set; } = "";
    [JsonPropertyName("slot_file")] public string SlotFile { get; set; } = "";
    // The anomaly type word of CG (zharka, electra …): particle names become <type>_cg_<part>.
    [JsonPropertyName("type")] public string Type { get; set; } = "";
}

public static class ProjectNames
{
    // A file stem or free text as an id: lower case letters, digits and '_'; never empty, never starting with a digit.
    public static string Id(string text)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char c in text.ToLowerInvariant())
            sb.Append(c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '_');
        string id = string.Join("_", sb.ToString().Split('_', StringSplitOptions.RemoveEmptyEntries));
        if (id.Length == 0)
            id = "item";
        return char.IsDigit(id[0]) ? "x" + id : id;
    }

    public static bool IsId(string text) => text.Length > 0 && Id(text) == text;

    public static string Unique(string wanted, IEnumerable<string> taken)
    {
        var set = taken.ToHashSet(StringComparer.Ordinal);
        if (!set.Contains(wanted))
            return wanted;
        for (int n = 2; ; n++)
            if (!set.Contains(wanted + n))
                return wanted + n;
    }
}

// An effect composition: the project file and where it lives.
public sealed class Composition
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public Composition(string projectPath, ProjectFile file)
    {
        ProjectPath = Path.GetFullPath(projectPath);
        File = file;
    }

    public string ProjectPath { get; }
    public ProjectFile File { get; set; }

    public string Folder => Path.GetDirectoryName(ProjectPath)!;

    public string Resolve(string relative) => Path.GetFullPath(Path.Combine(Folder, relative.Replace('/', Path.DirectorySeparatorChar)));

    public string Relative(string fullPath) => Path.GetRelativePath(Folder, fullPath).Replace(Path.DirectorySeparatorChar, '/');

    public IEnumerable<(string Path, PtcDocument Doc)> LoadParts() =>
        File.Parts.Select(p => (p.Path, PtcDocument.Load(Resolve(p.Path))));

    public IEnumerable<(string Path, EmatDocument Doc)> LoadMaterials() =>
        File.Materials.Select(p => (p, EmatDocument.Load(Resolve(p))));

    public static ProjectFile Parse(byte[] bytes)
    {
        var file = JsonSerializer.Deserialize<ProjectFile>(bytes, Json) ?? throw new InvalidDataException("Empty project file");
        if (file.Version > ProjectFile.CurrentVersion)
            throw new InvalidDataException($"Project version {file.Version} is newer than this tool ({ProjectFile.CurrentVersion})");
        file.Version = ProjectFile.CurrentVersion;
        return file;
    }

    public static Composition Open(string projectPath) => new(projectPath, Parse(System.IO.File.ReadAllBytes(projectPath)));

    public static byte[] Bytes(ProjectFile file) => JsonSerializer.SerializeToUtf8Bytes(file, Json);

    public void Save() => SafeFile.Write(ProjectPath, Bytes(File));
}

public static class SafeFile
{
    // Writes next to the target, then swaps it in, so a crash mid-write leaves the old file intact.
    public static void Write(string path, byte[] bytes)
    {
        string temp = path + ".tmp";
        System.IO.File.WriteAllBytes(temp, bytes);
        if (System.IO.File.Exists(path))
            System.IO.File.Replace(temp, path, null);
        else
            System.IO.File.Move(temp, path);
    }
}
