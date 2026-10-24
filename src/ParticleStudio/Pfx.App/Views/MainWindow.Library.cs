using System.Text.Json;
using Microsoft.Win32;
using Pfx.App.Core;
using Pfx.App.Library;
using Pfx.App.Recipes;
using Pfx.Core.Editing;
using Pfx.Core.Model;
using Pfx.Core.Text;
using Pfx.Render;

namespace Pfx.App.Views;

// The library browser's sources and actions, and the own recipes kept beside the settings (recipes\<name>.json).
public partial class MainWindow : ILibraryHost
{
    public LibraryView LibraryTab => LibraryPanel;

    private void InitLibrary()
    {
        LibraryPanel.Init(this);
        Dock.AllowDrop = true;
        Dock.PreviewDragOver += OnLibraryDragOverTabs;
    }

    // The library and the layers share a pane: a library emitter dragged over the layers tab opens that tab, so it can
    // be dropped on the list. Anywhere but the list (whose own handler allows the copy) the drag shows "no drop".
    private void OnLibraryDragOverTabs(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(LibraryDrag)))
            return;
        e.Effects = System.Windows.DragDropEffects.None;
        for (var d = e.OriginalSource as System.Windows.DependencyObject; d != null; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
        {
            if (d is AvalonDock.Controls.LayoutAnchorableTabItem { Model: var model } && model == LayersPane)
            {
                LayersPane.IsSelected = true;
                return;
            }
        }
    }

    private static string OwnFolder => Path.Combine(AppSettings.Folder, "recipes");

    // Vanilla = graphics\particles under each data root; then the X-Ray import folder, the folders of recent projects
    // and the folders the user added (O2′: nothing assumed about this PC, all from settings and roots).
    public IReadOnlyList<LibrarySource> LibrarySources()
    {
        var list = new List<LibrarySource>();
        // The roots the open parts' materials were found under count too (a settings change rebuilds the root list
        // without them, while the parts keep their loaded materials).
        foreach (var doc in _docs)
            foreach (var game in doc.Materials.Keys)
                _roots.AddRootFor(doc.Path, game);
        foreach (var root in _roots.Roots)
        {
            string vanilla = Path.Combine(root, "graphics", "particles");
            if (Directory.Exists(vanilla))
                list.Add(new LibrarySource(Loc.T("Lib.Vanilla"), vanilla));
        }
        if (Settings.ImportFolder.Length > 0 && Directory.Exists(Settings.ImportFolder))
            list.Add(new LibrarySource(Loc.T("Lib.Import"), Settings.ImportFolder));
        foreach (var f in Settings.LibraryFolders.Where(Directory.Exists))
            list.Add(new LibrarySource(Path.GetFileName(f.TrimEnd(Path.DirectorySeparatorChar)), f));
        foreach (var p in Settings.Recent.Where(r => r.EndsWith(".ndzpfx", StringComparison.OrdinalIgnoreCase) && File.Exists(r)))
            list.Add(new LibrarySource(Loc.T("Lib.Projects"), Path.GetDirectoryName(p)!));
        return list;
    }

    public IReadOnlyList<string> LibraryRoots() => _roots.Roots.ToList();

    public Pfx.Textures.RgbaImage? RenderThumb(EffectPreview preview, Camera camera, int width, int height) =>
        _viewport.RenderAlone(preview, camera, _viewport.View with { Gizmos = false, Helper = default, Order = DrawView.Normal, Ground = false, Fog = false }, width, height);

    public void OpenLibraryEffect(string path) => Open(new[] { path });

    public void AddLibraryFolder()
    {
        var dialog = new OpenFolderDialog { Title = Loc.T("Lib.PickFolder") };
        if (dialog.ShowDialog(this) != true)
            return;
        if (!Settings.LibraryFolders.Contains(dialog.FolderName, StringComparer.OrdinalIgnoreCase))
            Settings.LibraryFolders.Add(dialog.FolderName);
        if (!_launch.Automated)
            Settings.Save();
        LibraryPanel.Rescan();
    }

    // Copies one emitter of a library effect into the active part (one undo step). Its material is found through the
    // effect's own folder when no data root has it yet (as opening the effect would).
    public void AddLibraryEmitter(string path, string emitter)
    {
        var doc = _active;
        if (doc == null)
        {
            Toast(Loc.T("Toast.LibNoPart"), "warn");
            return;
        }
        var source = LibraryIndex.Emitters(path).FirstOrDefault(e => e.Name == emitter);
        if (source.Text == null)
            return;
        var emitors = doc.Ptc.EmitorsBlock;
        if (emitors == null)
        {
            Toast(Loc.T("Toast.LibNoList"), "warn");
            return;
        }
        var block = (EnfBlock)RecipeScene.Item(source.Text);
        string name = Names.Unique(emitter, doc.Ptc.Emitters.Select(e => e.Name));
        block.Header[^1] = new EnfToken(EnfTokenKind.Word, block.Header[^1].Leading, name);
        RecipeContext.Relead(block, doc.Ptc.Doc, emitors);
        if (block.FindLine("Material")?.Values.FirstOrDefault()?.Value is { } material)
            _roots.AddRootFor(path, material);
        doc.Do(new SpliceEdit<EnfItem>(emitors.Items, emitors.Items.Count, 0, new EnfItem[] { block }), Loc.F("Undo.LibAdd", name, Path.GetFileName(path)));
        doc.LoadMaterials(_roots);
        if (doc.Ptc.Emitter(name) is { } added)
            Select(doc, new[] { added.Block }, false);
        Toast(Loc.F("Toast.LibAdded", name, doc.Title), "ok");
    }

    // An emitter dropped on the layers panel joins the part of the row it was dropped on (else the active part).
    public void DropLibraryEmitter(LibraryDrag drag, ViewModels.LayerRow? target)
    {
        if (target?.Doc is { } doc && doc != _active)
            Select(doc, Array.Empty<EnfBlock>(), true);
        AddLibraryEmitter(drag.Path, drag.Emitter);
    }

    // ---- recipe files and own recipes (IRecipesHost) ----

    public string? PickRecipeFile(ParamKind kind)
    {
        var dialog = new OpenFileDialog { Filter = Loc.T(kind == ParamKind.Material ? "Filter.Emat" : "Filter.Any") };
        if (dialog.ShowDialog(this) != true)
            return null;
        if (kind == ParamKind.Material)
        {
            if (GamePathOf(dialog.FileName) is { } game)
                return game;
            Toast(Loc.T("Toast.RcpNotUnderRoot"), "warn");
            return null;
        }
        return _project?.Folder != null ? _project.Relative(dialog.FileName) : dialog.FileName;
    }

    public IReadOnlyList<(string Path, OwnRecipe Recipe)> OwnRecipes()
    {
        var list = new List<(string, OwnRecipe)>();
        if (!Directory.Exists(OwnFolder))
            return list;
        foreach (var f in Directory.GetFiles(OwnFolder, "*.json").Order())
        {
            try
            {
                if (JsonSerializer.Deserialize<OwnRecipe>(File.ReadAllBytes(f), Composition.Json) is { } r)
                    list.Add((f, r));
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
            {
            }
        }
        return list;
    }

    // Every plain key line of the selected emitters (a value or several, a word, a path): what an own recipe can show.
    public IReadOnlyList<(string Emitter, string Key, string Value)> ExposableKeys() =>
        SelectedViews().SelectMany(v => v.Block.Items.OfType<EnfLine>().Select(l => (v.Name, l.Key, string.Join(" ", l.Values.Select(t => t.Text))))).ToList();

    // The selected emitters (with the project's lights, hazes, sounds and screen effects of the states that start their
    // part, when asked) saved as an own recipe.
    public void SaveOwnRecipe(string name, bool items, IReadOnlyList<(string Emitter, string Key)> exposed)
    {
        var views = SelectedViews();
        if (views.Count == 0 || _active == null)
            return;
        var own = new OwnRecipe { Name = name.Length > 0 ? name : Loc.T("Rcp.OwnDefaultName") };
        own.Emitters.AddRange(views.Select(v => RecipeScene.Text(v.Block)));
        foreach (var (emitter, key) in exposed)
            if (ExposableKeys().FirstOrDefault(k => k.Emitter == emitter && k.Key == key) is { Value: { } value })
                own.Exposed.Add(new OwnParam { Emitter = emitter, Key = key, Value = value });
        if (_project != null && PartOf(_active) is { } partId)
        {
            var part = _project.Snapshot().Parts.First(p => p.Id == partId);
            own.Spin = part.Spin;
            own.Near = part.Near;
            if (items)
            {
                var refs = _project.File.States.Where(s => s.Items.Any(i => i.Ref == partId)).SelectMany(s => s.Items.Select(i => i.Ref)).ToHashSet();
                own.Lights.AddRange(_project.File.Lights.Where(l => refs.Contains(l.Id)));
                own.Hazes.AddRange(_project.File.Hazes.Where(h => refs.Contains(h.Id)));
                own.Sounds.AddRange(_project.File.Sounds.Where(s => refs.Contains(s.Id)));
                own.Screens.AddRange(_project.File.Screens.Where(s => refs.Contains(s.Id)));
            }
        }
        own.Note = Loc.F("Rcp.OwnWhat", own.Emitters.Count);
        Directory.CreateDirectory(OwnFolder);
        string file = Path.Combine(OwnFolder, ProjectNames.Id(own.Name) + ".json");
        for (int n = 2; File.Exists(file); n++)
            file = Path.Combine(OwnFolder, ProjectNames.Id(own.Name) + n + ".json");
        AtomicFile.Write(file, JsonSerializer.SerializeToUtf8Bytes(own, Composition.Json));
        Toast(Loc.F("Toast.RcpOwnSaved", own.Name, own.Emitters.Count, own.Exposed.Count), "ok");
    }

    public void DeleteOwnRecipe(string path)
    {
        if (!path.StartsWith(OwnFolder, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            return;
        File.Delete(path);
        Toast(Loc.F("Toast.RcpOwnDeleted", Path.GetFileNameWithoutExtension(path)), "info");
    }
}
