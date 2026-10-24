using Pfx.App.Recipes;
using Pfx.App.ViewModels;
using Pfx.Core.Model;

namespace Pfx.App.Automation;

// P12b checks through the shell's own paths: a recipe applied to the selection changes the part and adds its node and
// layer rows, before / after takes its changes off and on, a value edit applies again, one undo per step takes it all
// back to the bytes the documents had; a recipe that makes a part opens it in memory; the dry run changes nothing; the
// library finds effects, draws a thumbnail and copies an emitter into the active part; then the P12c panel checks
// (own recipe through its buttons, a real drag from the library).
public sealed partial class ShellChecks
{
    // The parts open at the start and the project as it would be saved (a part a recipe opened later is compared apart).
    private string Bytes(IReadOnlyList<Pfx.App.Model.EffectDoc>? docs = null) =>
        string.Join("|", (docs ?? _w.Docs).Select(d => d.Path + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(d.Ptc.ToBytes()))))
        + "|" + (_w.Project != null ? System.Text.Encoding.UTF8.GetString(Composition.Bytes(_w.Project.Snapshot())) : "");

    private async Task RecipeChecks()
    {
        Log("Checks (recipes, library):");
        var doc = _w.Docs[0];
        var e = doc.Ptc.Emitters[0];
        _w.Select(doc, new[] { e.Block }, false);
        await Settled();
        string start = Bytes();
        var sort = RecipeCatalog.Find("sort_inside")!;
        var dry = _w.DryRun(sort, null);
        Expect(dry.Why == null && dry.Ops.Count > 0 && Bytes() == start, $"dry run of \"sort inside\": {dry.Ops.Count} changes listed, documents untouched");
        if (_w.Project == null)
        {
            _w.NewProject();
            await Settled();
        }
        var open = _w.Docs.ToList();
        string before = Bytes(open);
        byte[] partBefore = doc.Ptc.ToBytes();
        int steps = 0;
        _w.AddRecipe(sort);
        steps++;
        await Settled();
        var node = _w.Project?.File.Recipes.LastOrDefault();
        Expect(node != null && doc.Ptc.Emitter(e.Name)?.Number("Sort") == 1, $"\"sort inside\" applied: node {node?.Id}, Sort 1 on {e.Name}");
        Expect(_w.LayerRows.OfType<RecipeRow>().Any(r => r.Node.Id == node?.Id) && _w.LayerRows.OfType<MadeRow>().Any(r => r.Node.Id == node?.Id),
            "the layers panel lists the node and what it made");
        if (node != null)
        {
            _w.ToggleRecipe(node.Id);
            steps++;
            await Settled();
            Expect(doc.Ptc.ToBytes().AsSpan().SequenceEqual(partBefore), "\"before\" takes the change off (the part's bytes as before the recipe)");
            _w.ToggleRecipe(node.Id);
            steps++;
            await Settled();
            Expect(doc.Ptc.Emitter(e.Name)?.Number("Sort") == 1, "\"after\" applies it again");
        }
        _w.AddRecipe(RecipeCatalog.Find("haze")!);
        steps++;
        await Settled();
        var haze = _w.Project?.File.Recipes.LastOrDefault();
        if (haze != null)
        {
            _w.SetRecipeParam(haze.Id, "radius", "3");
            steps++;
            await Settled();
        }
        Expect(_w.Project?.File.Hazes.SingleOrDefault()?.Radius == 3, "a value edit of the haze node applies again (radius 3)");
        int parts = _w.Project?.File.Parts.Count ?? 0;
        _w.Select(doc, new[] { e.Block }, false);
        _w.AddRecipe(RecipeCatalog.Find("near_only")!);
        steps++;
        await Settled();
        var near = _w.Project?.File.Parts.LastOrDefault();
        Expect(_w.Project?.File.Parts.Count == parts + 1 && near?.Near == 150 && _w.Docs.Any(d => d.Path.EndsWith(near.Id + ".ptc", StringComparison.OrdinalIgnoreCase) && d.Dirty),
            $"\"near-only part\" made part {near?.Id} (in memory until saved) with near 150");
        for (int i = 0; i < steps; i++)
            _w.UndoActive();
        await Settled();
        string after = Bytes(open);
        var left = _w.Docs.Except(open).ToList();
        Expect(after == before && left.All(d => d.Ptc.Emitters.Count == 0 && _w.PartOf(d) == null),
            $"{steps} undo steps bring every part and the project back byte for byte; the recipe's new part is empty and out of the project");
        // The in-memory part of the undone recipe stays open and empty; it is not in the project and is closed here.
        foreach (var made in left)
            _w.CloseForCheck(made);

        var lib = _w.LibraryTab;
        _w.ShowPane("library");
        lib.Rescan();
        for (int i = 0; i < 1200 && lib.Scanning; i++)
            await Frame();
        Expect(lib.Entries.Count > 0, $"library: {lib.Entries.Count} effects in {lib.Entries.Select(x => x.Source).Distinct().Count()} sources");
        for (int i = 0; i < 1200 && lib.ThumbsDrawn == 0 && lib.Entries.Count > 0; i++)
            await Frame();
        Expect(lib.ThumbsDrawn > 0, $"library: {lib.ThumbsDrawn} thumbnails drawn by the viewport");
        var entry = lib.Entries.FirstOrDefault(x => Pfx.App.Library.LibraryIndex.Emitters(x.Path).Count > 0);
        if (entry != null)
        {
            var first = Pfx.App.Library.LibraryIndex.Emitters(entry.Path)[0].Name;
            _w.Select(doc, Array.Empty<Pfx.Core.Text.EnfBlock>(), true);
            int count = doc.Ptc.Emitters.Count;
            _w.AddLibraryEmitter(entry.Path, first);
            await Settled();
            Expect(doc.Ptc.Emitters.Count == count + 1, $"library emitter {first} of {entry.Name} copied into {doc.Title}");
            _w.UndoActive();
            await Settled();
            Expect(doc.Ptc.Emitters.Count == count, "undo removes the copied emitter");
        }
        await OwnRecipeChecks();
        await LibraryDragCheck();
        // A haze node stays for the screenshots of the recipes panel (its values and what it made on screen).
        _w.AddRecipe(RecipeCatalog.Find("haze")!);
        await Settled();
        _w.ShowPane("recipes");
        _w.RecipesTab.SelectNode(_w.Project?.File.Recipes.LastOrDefault()?.Id);
        await Settled();
    }
}
