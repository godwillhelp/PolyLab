using Pfx.App.Core;
using Pfx.App.Model;
using Pfx.App.Recipes;
using Pfx.App.ViewModels;
using Pfx.Core.Editing;
using Pfx.Core.Model;
using Pfx.Core.Text;

namespace Pfx.App.Views;

// Recipes and generators as project nodes (spec 3.10). A run works on a copy of the project and on the live part
// documents (RecipeScene); when it finishes, its part edits are taken back and replayed through each document's own
// history and the copy replaces the project, all inside one group of the shell history: one Ctrl+Z takes the whole run
// back. A run that fails or is refused leaves nothing behind (new in-memory parts it opened are closed again).
public partial class MainWindow : IRecipesHost
{
    public RecipesView RecipesTab => RecipesPanel;

    private void InitRecipes() => RecipesPanel.Init(this);

    private EffectDoc? DocAt(string full) =>
        _docs.FirstOrDefault(d => string.Equals(Path.GetFullPath(d.Path), Path.GetFullPath(full), StringComparison.OrdinalIgnoreCase));

    // A part's document for a run: the open one, else the file on disk opened, else a new empty part kept in memory
    // until the project is saved.
    private RecipeScene SceneFor(ProjectFile copy, List<EffectDoc> made)
    {
        var project = _project!;
        PtcDocument? OpenDoc(PartEntry e) => DocAt(project.Resolve(e.Path))?.Ptc;
        PtcDocument CreateDoc(PartEntry e)
        {
            string full = project.Resolve(e.Path);
            if (DocAt(full) is { } open)
                return open.Ptc;
            if (File.Exists(full))
            {
                Open(new[] { full });
                if (DocAt(full) is { } read)
                    return read.Ptc;
            }
            var doc = EffectDoc.Create(full);
            Register(doc);
            made.Add(doc);
            return doc.Ptc;
        }
        string? folder = project.Folder ?? copy.Parts.Select(p => Path.GetDirectoryName(project.Resolve(p.Path))).FirstOrDefault();
        return new RecipeScene(copy, OpenDoc, CreateDoc, g => _roots.Resolve(g), _roots.Tiles(), e => project.Resolve(e.Path), GamePathOf, folder);
    }

    // Runs `run` on a copy of the project; null from it = commit, a language key = refused (nothing changes).
    private bool RunRecipes(string label, Func<RecipeScene, ProjectFile, string?> run)
    {
        if (_project == null)
            NewProject();
        if (_project == null)
            return false;
        var copy = _project.Snapshot();
        var made = new List<EffectDoc>();
        var scene = SceneFor(copy, made);
        string? why;
        try
        {
            why = run(scene, copy);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or EnfSyntaxException or InvalidOperationException or UnauthorizedAccessException
                                       or KeyNotFoundException)
        {
            DropMade(made);
            Toast(Loc.F("Toast.RcpFailed", ex.Message), "err");
            return false;
        }
        if (why != null)
        {
            scene.Unwind();
            DropMade(made);
            Toast(Loc.T(why), "warn");
            return false;
        }
        scene.Unwind();
        using (_history.Group())
        {
            foreach (var doc in _docs.ToList())
            {
                var edits = scene.EditsOf(doc.Ptc).ToList();
                var entry = copy.Parts.FirstOrDefault(p => DocAt(_project.Resolve(p.Path)) == doc);
                if (entry != null && doc.Motion is not { IsPath: true } && (doc.Motion?.SpinRate ?? 0) != entry.Spin)
                    edits.Add(doc.SetMotion(entry.Spin != 0 ? new ScriptMotion(null, null, false, entry.Spin) : null));
                if (edits.Count > 0)
                    doc.Do(new CompositeEdit(edits), label);
            }
            _project.Replace(copy, label);
        }
        foreach (var doc in _docs)
            doc.LoadMaterials(_roots);
        foreach (var (key, args) in scene.Notes)
            Toast(Loc.F(key, args), "warn");
        RecipesPanel.Show();
        RebuildLayers();
        return true;
    }

    private void DropMade(List<EffectDoc> made)
    {
        foreach (var d in made)
        {
            _docs.Remove(d);
            _state.Tabs.Remove(_state.Tabs.First(t => t.Doc == d));
            _pipeline.Remove(d);
            RemovePart(d);
        }
        if (made.Count > 0)
            RebuildLayers();
    }

    // The targets a new node gets from the selection: the selected emitters of the active part, or the parts of the
    // selected layer rows (the active part when none). A part not yet in the project joins it in the same step.
    private List<RecipeTarget> TargetsFor(Recipe recipe, ProjectFile copy)
    {
        string Id(EffectDoc doc)
        {
            var hit = copy.Parts.FirstOrDefault(p => DocAt(_project!.Resolve(p.Path)) == doc);
            if (hit != null)
                return hit.Id;
            string id = ProjectNames.Unique(ProjectNames.Id(Path.GetFileNameWithoutExtension(doc.Path)), copy.Items().Select(i => i.Id));
            copy.Parts.Add(new PartEntry { Id = id, Path = _project!.Relative(Path.GetFullPath(doc.Path)) });
            if (copy.States.Count > 0)
                copy.States[Math.Min(ComposePanel.SelectedState, copy.States.Count - 1)].Items.Add(new StateItem { Ref = id });
            return id;
        }
        var list = new List<RecipeTarget>();
        if (recipe.Scope == RecipeScope.Emitters && _active != null)
        {
            string part = Id(_active);
            list.AddRange(SelectedViews().Select(v => new RecipeTarget { Part = part, Emitter = v.Name }));
        }
        else if (recipe.Scope == RecipeScope.Parts)
        {
            var docs = Layers.List.SelectedItems.Cast<LayerRow>().Select(r => r.Doc).OfType<EffectDoc>().Distinct().ToList();
            if (docs.Count == 0 && _active != null)
                docs.Add(_active);
            list.AddRange(docs.Select(d => new RecipeTarget { Part = Id(d) }));
        }
        return list;
    }

    // ---- IRecipesHost ----

    public CompositionDoc? RecipeProject => _project;

    public string? CurrentState => _project?.File.States.ElementAtOrDefault(ComposePanel.SelectedState)?.Name;

    public void AddRecipe(Recipe recipe, OwnRecipe? own = null, IReadOnlyList<RecipeTarget>? targets = null) => AddRecipe(recipe, own, targets, null);

    // values: parameter values that differ from the recipe's defaults (a generator's donor chosen beforehand).
    public void AddRecipe(Recipe recipe, OwnRecipe? own, IReadOnlyList<RecipeTarget>? targets, IReadOnlyDictionary<string, string>? values)
    {
        string? added = null;
        RunRecipes(Loc.F("Undo.RcpAdd", RecipeName(recipe, own)), (scene, copy) =>
        {
            var node = new RecipeNode
            {
                Id = ProjectNames.Unique(own != null ? ProjectNames.Id(own.Name) : recipe.Id, copy.Recipes.Select(r => r.Id)),
                Kind = own != null ? OwnRecipeRun.Kind : recipe.Id, Own = own, Params = recipe.Defaults(),
                Targets = targets?.ToList() ?? TargetsFor(recipe, copy),
            };
            if (recipe.Params.Any(p => p.Kind == ParamKind.State) && CurrentState is { } state)
                node.Params["state"] = state;
            foreach (var (k, v) in values ?? new Dictionary<string, string>())
                node.Params[k] = v;
            copy.Recipes.Add(node);
            added = node.Id;
            return RecipeRunner.Apply(scene, node);
        });
        if (added != null && _project?.File.Recipes.Any(r => r.Id == added) == true)
        {
            RecipesPanel.SelectNode(added);
            ShowPane("recipes");
        }
    }

    public void SetRecipeParam(string nodeId, string key, string value) =>
        RunRecipes(Loc.F("Undo.RcpParam", nodeId), (scene, copy) =>
        {
            var node = copy.Recipes.First(r => r.Id == nodeId);
            node.Params[key] = value;
            return RecipeRunner.Reapply(scene, node);
        });

    public void ToggleRecipe(string nodeId) =>
        RunRecipes(Loc.F("Undo.RcpToggle", nodeId), (scene, copy) =>
        {
            var node = copy.Recipes.First(r => r.Id == nodeId);
            node.On = !node.On;
            return RecipeRunner.Reapply(scene, node);
        });

    public void RemoveRecipe(string nodeId) =>
        RunRecipes(Loc.F("Undo.RcpRemove", nodeId), (scene, copy) =>
        {
            var node = copy.Recipes.First(r => r.Id == nodeId);
            RecipeUndo.Revert(node, scene);
            copy.Recipes.Remove(node);
            return null;
        });

    // What a recipe would change for the current selection, run on throwaway copies of the parts (nothing on screen
    // changes): the panel lists it before the recipe is added.
    public RecipePreview DryRun(Recipe recipe, OwnRecipe? own)
    {
        if (_project == null && _docs.Count == 0)
            return new RecipePreview("Rcp.Why.NoParts", Array.Empty<RecipeOp>(), Array.Empty<RecipeTarget>());
        var copy = _project?.Snapshot() ?? new ProjectFile { States = { new StateEntry { Name = "idle" } } };
        var clones = new Dictionary<string, PtcDocument>(StringComparer.OrdinalIgnoreCase);
        string Full(PartEntry e) => _project != null ? _project.Resolve(e.Path) : Path.GetFullPath(e.Path);
        PtcDocument? Open(PartEntry e)
        {
            string full = Full(e);
            if (clones.TryGetValue(full, out var c))
                return c;
            var doc = DocAt(full);
            return doc == null ? null : clones[full] = PtcDocument.From(EnfDocument.Parse(doc.Ptc.ToBytes()));
        }
        PtcDocument Create(PartEntry e) => Open(e) ?? (clones[Full(e)] = PtcDocument.From(EnfDocument.Parse("EffectDef {\r\n Emitors {\r\n }\r\n}\r\n")));
        var targets = _project != null ? TargetsFor(recipe, copy) : new List<RecipeTarget>();
        var node = new RecipeNode { Id = recipe.Id, Kind = own != null ? OwnRecipeRun.Kind : recipe.Id, Own = own, Params = recipe.Defaults(), Targets = targets };
        if (CurrentState is { } state && recipe.Params.Any(p => p.Kind == ParamKind.State))
            node.Params["state"] = state;
        // Material copies would be written by a real run; the dry run resolves materials but writes into a scratch folder.
        string scratch = Path.Combine(Path.GetTempPath(), "ndz_pfx_dry_" + Environment.ProcessId);
        var scene = new RecipeScene(copy, Open, Create, g => _roots.Resolve(g), _roots.Tiles(),
            e => Path.Combine(scratch, Path.GetFileName(e.Path)), _ => null, _project?.Folder);
        try
        {
            string? why = RecipeRunner.Apply(scene, node);
            return new RecipePreview(why, node.Log.ToList(), targets);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or EnfSyntaxException or InvalidOperationException or KeyNotFoundException)
        {
            return new RecipePreview("Rcp.Why.Failed", Array.Empty<RecipeOp>(), targets);
        }
        finally
        {
            if (Directory.Exists(scratch))
                Directory.Delete(scratch, true);
        }
    }

    public static string RecipeName(Recipe recipe, OwnRecipe? own) => own?.Name ?? Loc.T("Rcp." + recipe.Id);

    // The checks close an empty in-memory part without the second click a dirty part needs.
    internal void CloseForCheck(EffectDoc doc)
    {
        _pendingClose.Add(doc);
        CloseDoc(doc);
    }

    public void SelectRecipeTarget(RecipeOp op)
    {
        if (_project == null)
            return;
        var part = op.Part != null ? _project.File.Parts.FirstOrDefault(p => p.Id == op.Part) : null;
        if (part != null && DocOfPart(part) is { } doc)
        {
            var block = op.Emitter != null ? doc.Ptc.Emitter(op.Emitter)?.Block : null;
            Select(doc, block != null ? new[] { block } : Array.Empty<EnfBlock>(), block == null);
            return;
        }
        if (op.Item != null && _project.File.Find(op.Item) is { } item)
        {
            ComposePanel.SelectItem(item.Id);
            ShowPane("compose");
        }
    }
}

public sealed record RecipePreview(string? Why, IReadOnlyList<RecipeOp> Ops, IReadOnlyList<RecipeTarget> Targets);
