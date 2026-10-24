using System.Globalization;
using Pfx.Core.Model;

namespace Pfx.App.Recipes;

// Proven = the technique was seen working in game in the CG anomaly work (the source names where); Unverified = built
// from a measured law or a capture but not yet seen in game as a recipe; Rejected = the owner turned it down in game
// (still offered where the rejection does not apply, with the warning).
public enum RecipeStatus { Proven, Unverified, Rejected }

// What a recipe is applied to: selected emitters, whole parts, or the project (nothing has to be selected).
public enum RecipeScope { Emitters, Parts, Project }

public enum ParamKind { Number, Flag, Choice, Text, File, Material, State }

// A parameter: its key (language keys Rcp.<recipe>.<key> and Tip.Rcp.<recipe>.<key>), kind, default as an invariant
// text, the range of a number and the values of a choice.
public sealed record RecipeParam(string Key, ParamKind Kind, string Default, double Min = double.MinValue, double Max = double.MaxValue, string[]? Choices = null)
{
    public static RecipeParam Num(string key, double def, double min, double max) =>
        new(key, ParamKind.Number, def.ToString(CultureInfo.InvariantCulture), min, max);

    public static RecipeParam Flag(string key, bool def) => new(key, ParamKind.Flag, def ? "1" : "0");

    public static RecipeParam Pick(string key, string def, params string[] choices) => new(key, ParamKind.Choice, def, Choices: choices);

    public static RecipeParam Path(string key, string def = "") => new(key, ParamKind.File, def);

    // A material as the game names it (a path under a data root).
    public static RecipeParam Material(string key, string def) => new(key, ParamKind.Material, def);

    // The state an added item goes into: the anomaly panel's selected state when the node is made ("" = the first).
    public static RecipeParam State() => new("state", ParamKind.State, "");
}

// A ready solution (spec 3.10). Texts are language keys: Rcp.<Id> (name), Rcp.<Id>.What (one line), Rcp.<Id>.Source
// (the facts it rests on). Apply makes its changes through the context, which logs each for taking back.
public abstract class Recipe
{
    public abstract string Id { get; }
    public abstract RecipeScope Scope { get; }
    public virtual RecipeStatus Status => RecipeStatus.Proven;
    // What the effect then needs outside the .ptc (language keys Rcp.Need.<x>): "script" (the client runtime the export
    // generates), "light" (a dynamic light, 32 per view), "ppe" (a screen effect), "sound", "file" (a copied material).
    public virtual IReadOnlyList<string> Needs => Array.Empty<string>();
    public virtual IReadOnlyList<RecipeParam> Params => Array.Empty<RecipeParam>();
    public bool IsGenerator => Id.StartsWith("gen_", StringComparison.Ordinal);

    public RecipeParam Param(string key) => Params.FirstOrDefault(p => p.Key == key) ?? throw new KeyNotFoundException(Id + "." + key);

    // Null when the recipe can run on these targets, else the language key of the reason.
    public virtual string? Why(RecipeContext c) => Scope switch
    {
        RecipeScope.Emitters when c.Emitters().Count == 0 => "Rcp.Why.NoEmitters",
        RecipeScope.Parts when c.Parts().Count == 0 => "Rcp.Why.NoParts",
        _ => null,
    };

    public abstract void Apply(RecipeContext c);

    public Dictionary<string, string> Defaults() => Params.ToDictionary(p => p.Key, p => p.Default);
}
