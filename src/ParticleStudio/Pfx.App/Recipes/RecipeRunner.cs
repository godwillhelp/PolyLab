using Pfx.Core.Model;

namespace Pfx.App.Recipes;

// Applies, re-applies and takes back nodes on a scene. A run that throws is unwound (part edits taken back) and the
// node's log is restored, so a failed run changes nothing; the shell commits only finished runs.
public static class RecipeRunner
{
    public static Recipe? Find(RecipeNode node) => node.Kind == OwnRecipeRun.Kind ? new OwnRecipeRun(node.Own!) : RecipeCatalog.Find(node.Kind);

    // Null on success, else the language key of the reason (nothing changed then).
    public static string? Apply(RecipeScene scene, RecipeNode node, IReadOnlyList<RecipeOp>? previous = null)
    {
        var recipe = Find(node);
        if (recipe == null)
            return "Rcp.Why.Unknown";
        var c = new RecipeContext(recipe, node, scene) { Previous = previous ?? Array.Empty<RecipeOp>() };
        if (recipe.Why(c) is { } why)
            return why;
        var log = node.Log.ToList();
        try
        {
            recipe.Apply(c);
        }
        catch (Exception)
        {
            scene.Unwind();
            node.Log.Clear();
            node.Log.AddRange(log);
            throw;
        }
        return null;
    }

    // Takes the node's changes back and applies it again with its current values (a parameter edit, regeneration).
    public static string? Reapply(RecipeScene scene, RecipeNode node)
    {
        var previous = node.Log.ToList();
        RecipeUndo.Revert(node, scene);
        return node.On ? Apply(scene, node, previous) : null;
    }
}
