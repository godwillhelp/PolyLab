using System.Text.Json.Serialization;

namespace Pfx.Core.Model;

// A ready solution or generator applied to the project (spec 3.10): what it is, its parameter values, what it was applied
// to and the log of every change it made, so it can be applied again with other values or taken back alone. A node that
// is switched off keeps its values and targets but none of its changes (the "before" side of the before/after switch).
public sealed class RecipeNode
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    // A catalog recipe or generator id ("swirl", "gen_tornado"), or "own" for a recipe the user saved (Own holds it).
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    // Parameter values as invariant texts (numbers "0.5", switches "1" / "0", choices and paths as they are).
    [JsonPropertyName("params")] public Dictionary<string, string> Params { get; set; } = new();
    [JsonPropertyName("targets")] public List<RecipeTarget> Targets { get; set; } = new();
    [JsonPropertyName("on")] public bool On { get; set; } = true;
    [JsonPropertyName("log")] public List<RecipeOp> Log { get; set; } = new();
    [JsonPropertyName("own")] public OwnRecipe? Own { get; set; }
}

// A part (Emitter null) or one emitter of it.
public sealed class RecipeTarget
{
    [JsonPropertyName("part")] public string Part { get; set; } = "";
    [JsonPropertyName("emitter")] public string? Emitter { get; set; }
}

// One change a node made, with what it takes to undo it later (the log is replayed backwards). Texts are written as the
// file has them, leading line break and indentation included.
public sealed class RecipeOp
{
    public const string EmitterAdd = "emitter_add";
    public const string EmitterRemove = "emitter_remove";
    public const string Key = "key";
    public const string PartText = "part_text";
    public const string ItemAdd = "item_add";
    public const string ItemRemove = "item_remove";
    public const string Field = "field";
    public const string StateAdd = "state_add";
    public const string StateRemove = "state_remove";
    public const string StateNew = "state_new";
    public const string FileMade = "file";

    [JsonPropertyName("op")] public string Op { get; set; } = "";
    [JsonPropertyName("part")] public string? Part { get; set; }
    [JsonPropertyName("emitter")] public string? Emitter { get; set; }
    [JsonPropertyName("key")] public string? KeyName { get; set; }
    [JsonPropertyName("item")] public string? Item { get; set; }
    [JsonPropertyName("state")] public string? State { get; set; }
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("delay")] public float Delay { get; set; }
    [JsonPropertyName("before")] public string? Before { get; set; }
    [JsonPropertyName("after")] public string? After { get; set; }
}

// A recipe the user saved from a selection (spec 3.10 "own recipes"): emitter texts, project items and the emitter keys
// shown as parameters. A copy rides in every node made from it, so a project never depends on the user's library.
public sealed class OwnRecipe
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("note")] public string Note { get; set; } = "";
    [JsonPropertyName("emitters")] public List<string> Emitters { get; set; } = new();
    [JsonPropertyName("lights")] public List<LightEntry> Lights { get; set; } = new();
    [JsonPropertyName("hazes")] public List<HazeEntry> Hazes { get; set; } = new();
    [JsonPropertyName("sounds")] public List<SoundEntry> Sounds { get; set; } = new();
    [JsonPropertyName("screens")] public List<ScreenEntry> Screens { get; set; } = new();
    // Part script data of the source part (turn, near radius, stop), applied to the part the recipe goes into.
    [JsonPropertyName("spin")] public float Spin { get; set; }
    [JsonPropertyName("near")] public float Near { get; set; }
    [JsonPropertyName("exposed")] public List<OwnParam> Exposed { get; set; } = new();
}

// An emitter key shown as a parameter of an own recipe: the emitter (by its name in the recipe), the key and its value
// in the saved selection (the default).
public sealed class OwnParam
{
    [JsonPropertyName("emitter")] public string Emitter { get; set; } = "";
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("value")] public string Value { get; set; } = "";
}

public static class RecipeRefs
{
    // A project item got a new id: the nodes' targets and logs follow, so a node can still take its changes back.
    public static void Rename(ProjectFile p, string from, string to)
    {
        foreach (var node in p.Recipes)
        {
            foreach (var t in node.Targets.Where(t => t.Part == from))
                t.Part = to;
            foreach (var op in node.Log)
            {
                if (op.Part == from)
                    op.Part = to;
                if (op.Item == from)
                    op.Item = to;
            }
        }
    }
}
