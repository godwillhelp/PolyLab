using System.Globalization;
using System.Text.Json;
using Pfx.Core.Editing;
using Pfx.Core.Model;

namespace Pfx.App.Recipes;

// A recipe the user saved from a selection (spec 3.10): its emitters go into the target part, its lights, hazes, sounds
// and screen effects into the project (new ids, the chosen state), its part script data onto the target part, and each
// exposed emitter key is a parameter (label "emitter · key", default = the value it was saved with).
public sealed class OwnRecipeRun : Recipe
{
    public const string Kind = "own";

    private readonly OwnRecipe _own;

    public OwnRecipeRun(OwnRecipe own) => _own = own;

    public override string Id => Kind;
    public override RecipeScope Scope => RecipeScope.Parts;
    public override RecipeStatus Status => RecipeStatus.Unverified;
    public override IReadOnlyList<string> Needs =>
        _own.Lights.Count + _own.Hazes.Count + _own.Sounds.Count + _own.Screens.Count > 0 || _own.Spin != 0 || _own.Near > 0 ? new[] { "script" } : Array.Empty<string>();

    public override IReadOnlyList<RecipeParam> Params =>
        _own.Exposed.Select((x, i) => new RecipeParam("p" + i, ParamKind.Text, x.Value)).Append(RecipeParam.State()).ToList();

    public static string Label(OwnRecipe own, string key) =>
        key.StartsWith('p') && int.TryParse(key[1..], NumberStyles.None, CultureInfo.InvariantCulture, out int i) && i < own.Exposed.Count
            ? own.Exposed[i].Emitter + " · " + own.Exposed[i].Key : key;

    public override void Apply(RecipeContext c)
    {
        var part = c.Parts()[0];
        var names = new Dictionary<string, string>();
        foreach (var text in _own.Emitters)
        {
            var block = (Pfx.Core.Text.EnfBlock)RecipeScene.Item(text);
            names[block.Name ?? ""] = c.AddEmitter(part.Part.Id, text);
        }
        for (int i = 0; i < _own.Exposed.Count; i++)
        {
            var x = _own.Exposed[i];
            if (!names.TryGetValue(x.Emitter, out var name))
                continue;
            var words = c.Text("p" + i).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
                continue;
            c.SetKey(part.Part.Id, name, x.Key, b => b.SetWords(x.Key, words));
        }
        T Copy<T>(T item) where T : IProjectItem
        {
            var copy = JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(item, Composition.Json), Composition.Json)!;
            copy.Id = c.NewId(item.Id);
            return copy;
        }
        var added = new List<string>();
        added.AddRange(_own.Lights.Select(l => c.AddItem(Copy(l)).Id));
        added.AddRange(_own.Hazes.Select(h => c.AddItem(Copy(h)).Id));
        added.AddRange(_own.Sounds.Select(s => c.AddItem(Copy(s)).Id));
        added.AddRange(_own.Screens.Select(s => c.AddItem(Copy(s)).Id));
        if (c.State() is { } state)
            foreach (var id in added)
                c.AddToState(state, id);
        if (_own.Spin != 0)
            c.SetField(part.Part.Id, "spin", _own.Spin);
        if (_own.Near > 0)
            c.SetField(part.Part.Id, "near", _own.Near);
    }
}
