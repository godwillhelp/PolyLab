using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Microsoft.Win32;
using Pfx.App.Core;
using Pfx.App.Inspector;
using Pfx.App.Model;
using Pfx.App.Preview;
using Pfx.App.Textures;
using Pfx.App.ViewModels;
using Pfx.Core.Editing;
using Pfx.Core.Model;
using Pfx.Core.Text;
using Pfx.Render;

namespace Pfx.App.Views;

// The shell: documents (open .ptc parts of one scene), selection, edits and the panels around the viewport. State
// owners: documents own the file text and undo history (EffectDoc), the pipeline owns the previews, the viewport owns
// the device and camera, AppSettings owns roots, language, recent files and the workspace.
public partial class MainWindow : Window, ILayersHost, ISettingsHost, ITimelineHost, IAtlasHost, ITextureHost
{
    private readonly Launch _launch;
    private readonly List<EffectDoc> _docs = new();
    private readonly ShellState _state = new();
    private readonly ViewportHost _viewport = new();
    private readonly PreviewPipeline _pipeline;
    private readonly InspectorVm _inspector = new(FieldScope.Emitter);
    private readonly InspectorVm _effectInspector = new(FieldScope.Effect);
    private readonly InspectorVm _material = new(FieldScope.Material);
    private DataRoots _roots;
    private EffectDoc? _active;
    private readonly List<EnfBlock> _selected = new();
    private bool _partSelected;
    private string? _clipboard;
    private bool _closeConfirmed;
    private readonly SlicerModel _slicer;

    public MainWindow(Launch launch, AppSettings settings)
    {
        _launch = launch;
        Settings = settings;
        Current = this;
        InitializeComponent();
        _roots = new DataRoots(RootList());
        _pipeline = new PreviewPipeline(_viewport, () => _roots.Roots.ToList());
        _pipeline.Built += OnPreviewBuilt;
        _pipeline.Failed += (doc, ex) => Toast(Loc.F("Toast.PreviewFailed", doc.Title, ex.Message), "err");
        ViewportSlot.Child = _viewport;
        _viewport.ComposeOverlay = ComposeOverlay;
        _viewport.Rendered += OnRendered;
        Layers.Host = this;
        SettingsPanel.Host = this;
        TabStrip.ItemsSource = _state.Tabs;
        Layers.List.ItemsSource = _state.Layers;
        StatusItems.ItemsSource = _state.Status;
        foreach (var vm in new[] { _inspector, _effectInspector, _material })
        {
            vm.Resolve = p => _roots.Resolve(p);
            vm.Toast += Toast;
            vm.ShowInText += ShowInText;
        }
        Inspector.DataContext = _inspector;
        MaterialInspector.DataContext = _material;
        InspectorView.CopyValue = (f, text) => { _clipboard = text; Clipboard.SetText(text); Toast(Loc.F("Toast.Copied", f.Label), "info"); };
        InspectorView.PasteValue = () => _clipboard;
        InspectorView.FindSame = FindSame;
        _inspector.OpenCurve += OpenCurve;
        CurvesPanel.Init();
        CurvesPanel.Editor.Toast += Toast;
        TimelinePanel.Host = this;
        TimelinePanel.Init();
        _slicer = new SlicerModel(p => _roots.Resolve(p), _viewport.Textures);
        _slicer.Toast += Toast;
        _slicer.Changed += () => TexturePanel.Invalidate();
        SlicerPanel.Init(_slicer);
        AtlasPanel.Host = this;
        AtlasPanel.Builder.Toast += Toast;
        AtlasPanel.Init();
        TexturePanel.Host = this;
        _pipeline.Seeked += OnSeeked;
        Loc.Changed += OnLanguage;
        PreviewKeyDown += OnKeys;
        Closing += OnClosing;
        Closed += (_, _) => SaveLayoutOnExit();
        Closed += (_, _) => StopLinkOnExit();
        if (!launch.Automated)
            RestorePlacement();
        InitViewport();
        InitLink();
        InitShell();
        InitXray();
        InitComposition();
        InitChecks();
        InitRecipes();
        InitLibrary();
        var files = launch.Files.Count > 0 ? launch.Files : new List<string>();
        if (files.Count > 0)
            Open(files);
        UpdateAll();
        Loaded += (_, _) => StartAutomation();
    }

    public static MainWindow? Current { get; private set; }
    public AppSettings Settings { get; }
    public ViewportHost Viewport => _viewport;
    public PreviewPipeline Pipeline => _pipeline;
    public IReadOnlyList<EffectDoc> Docs => _docs;
    public InspectorVm EmitterInspector => _inspector;
    public InspectorVm MaterialVm => _material;
    public InspectorVm EffectVm => _effectInspector;
    public EffectDoc? ActiveDoc => _active;
    public SlicerModel Slicer => _slicer;
    public SlicerView SlicerView => SlicerPanel;
    public AtlasView AtlasView => AtlasPanel;
    public TextureView TextureView => TexturePanel;

    private IReadOnlyList<string> RootList() =>
        (_launch.Roots?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? Settings.Roots.ToArray()).ToList();

    // ---- documents ----

    public void Open(IEnumerable<string> paths)
    {
        EffectDoc? first = null;
        foreach (var path in paths)
        {
            var full = Path.GetFullPath(path);
            if (_docs.FirstOrDefault(d => string.Equals(d.Path, full, StringComparison.OrdinalIgnoreCase)) is { } open)
            {
                first ??= open;
                continue;
            }
            EffectDoc doc;
            try
            {
                doc = EffectDoc.Open(full);
            }
            catch (EnfSyntaxException ex)
            {
                Toast(Loc.F("Toast.ParseError", Path.GetFileName(full), ex.Message), "err");
                continue;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                Toast(Loc.F("Toast.OpenFailed", Path.GetFileName(full), ex.Message), "err");
                continue;
            }
            Register(doc);
            Settings.AddRecent(full);
            first ??= doc;
        }
        if (!_launch.Automated)
            Settings.Save();
        RebuildLayers();
        if (first != null && _docs.Count == 1)
            ApplyView(ViewPreset.Free);
        if (first != null)
            Select(first, first.Ptc.Emitters.Count > 0 ? new[] { first.Ptc.Emitters[0].Block } : Array.Empty<EnfBlock>(), first.Ptc.Emitters.Count == 0);
        BuildMenu();
        UpdateAll();
        TimelinePanel.Rebuild();
    }

    // A document joins the scene: materials, tab, preview, live link.
    private void Register(EffectDoc doc)
    {
        doc.LoadMaterials(_roots);
        doc.Changed += () => OnDocChanged(doc);
        _docs.Add(doc);
        _state.Tabs.Add(new DocTab(doc));
        _pipeline.Add(doc);
        PushPart(doc, Pfx.Link.LinkHub.Now);
    }

    private void OnOpen(object sender, RoutedEventArgs e) => OpenDialog();

    private void OpenDialog()
    {
        var dialog = new OpenFileDialog { Filter = Loc.T("Filter.Ptc"), Multiselect = true };
        if (dialog.ShowDialog(this) == true)
            Open(dialog.FileNames);
    }

    // A dirty part needs a second click on its close button: the tab turns red with a hint (no message box).
    private void CloseDoc(EffectDoc doc)
    {
        var tab = _state.Tabs.First(t => t.Doc == doc);
        if (doc.Dirty && !_pendingClose.Contains(doc))
        {
            _pendingClose.Add(doc);
            Toast(Loc.F("Toast.CloseDirty", doc.Title), "warn");
            return;
        }
        _pendingClose.Remove(doc);
        _history.Forget(doc);
        _docs.Remove(doc);
        _state.Tabs.Remove(tab);
        _pipeline.Remove(doc);
        RemovePart(doc);
        if (_active == doc)
        {
            _active = _docs.FirstOrDefault();
            _selected.Clear();
            _partSelected = _active != null;
        }
        RebuildLayers();
        UpdateAll();
        ShowCurves();
        TimelinePanel.Rebuild();
    }

    private readonly HashSet<EffectDoc> _pendingClose = new();

    private void OnTabClick(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is DocTab tab)
            Select(tab.Doc, Array.Empty<EnfBlock>(), true);
    }

    private void OnTabClose(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is DocTab tab)
            CloseDoc(tab.Doc);
        e.Handled = true;
    }

    public void Save(EffectDoc? doc, bool saveAs = false)
    {
        if (doc == null)
            return;
        string? path = null;
        if (saveAs)
        {
            var dialog = new SaveFileDialog { Filter = Loc.T("Filter.Ptc"), FileName = doc.Title, InitialDirectory = Path.GetDirectoryName(doc.Path) };
            if (dialog.ShowDialog(this) != true)
                return;
            path = dialog.FileName;
        }
        try
        {
            int n = doc.Save(path);
            Toast(n == 0 ? Loc.F("Toast.SavedNothing", doc.Title) : Loc.F("Toast.Saved", doc.Title, n), "ok");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Toast(Loc.F("Toast.SaveFailed", doc.Title, ex.Message), "err");
        }
    }

    private void RestorePlacement()
    {
        if (Settings.Bounds is [var l, var t, var w, var h] && w >= MinWidth && h >= MinHeight
            && l + w > SystemParameters.VirtualScreenLeft && t + h > SystemParameters.VirtualScreenTop
            && l < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth && t < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            (Left, Top, Width, Height) = (l, t, w, h);
        }
        if (Settings.Maximized)
            WindowState = WindowState.Maximized;
        Closed += (_, _) =>
        {
            Settings.Maximized = WindowState == WindowState.Maximized;
            var b = RestoreBounds;
            if (!b.IsEmpty)
                Settings.Bounds = new[] { b.Left, b.Top, b.Width, b.Height };
            try
            {
                Settings.Save();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        };
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        bool project = _project is { Dirty: true };
        if (_launch.Automated || _closeConfirmed || (!_docs.Any(d => d.Dirty) && !project))
            return;
        e.Cancel = true;
        _closeConfirmed = true;
        Toast(Loc.F("Toast.ExitDirty", _docs.Count(d => d.Dirty) + (project ? 1 : 0)), "warn");
    }

    // ---- selection ----

    public void LayersSelected(IReadOnlyList<LayerRow> rows)
    {
        if (rows.Count == 0)
            return;
        var last = rows[^1];
        switch (last)
        {
            case PartRow part:
                Select(part.Part, Array.Empty<EnfBlock>(), true, fromLayers: true);
                return;
            case RecipeRow recipe:
                RecipesPanel.SelectNode(recipe.Node.Id);
                ShowPane("recipes");
                return;
            case MadeRow made:
                SelectRecipeTarget(made.Op);
                return;
            case EmitterRow emitter:
                var doc = emitter.Part;
                Select(doc, rows.OfType<EmitterRow>().Where(r => r.Part == doc).Select(r => r.Block).ToList(), false, fromLayers: true);
                return;
        }
    }

    public void Select(EffectDoc doc, IReadOnlyList<EnfBlock> emitters, bool part, bool fromLayers = false)
    {
        _active = doc;
        _selected.Clear();
        _selected.AddRange(emitters);
        _partSelected = part || emitters.Count == 0;
        if (!fromLayers)
            Layers.SetSelection(_state.Layers.Where(r => r is EmitterRow er ? _selected.Contains(er.Block) : r is PartRow pr && _partSelected && pr.Doc == doc));
        foreach (var t in _state.Tabs)
            t.Active = t.Doc == doc;
        ShowInspectors();
        ShowCurves();
        ShowFileText();
        ShowAbFor(doc);
        UpdateTitle();
        UpdateButtons();
    }

    // The curve editor follows the first selected emitter (several selected: it says so and edits that one).
    private void ShowCurves()
    {
        var views = SelectedViews();
        CurvesPanel.Editor.Show(_active, _partSelected ? null : views.FirstOrDefault(), views.Count);
        _slicer.Show(_active, _partSelected ? null : views.FirstOrDefault(), views.Count);
        AtlasPanel.Refresh();
    }

    private void OpenCurve(FieldVm field)
    {
        CurvesPanel.Editor.Focus(field.Key);
        ShowPane("curves");
    }

    private IReadOnlyList<EmitterView> SelectedViews() =>
        _active == null ? Array.Empty<EmitterView>() : _active.Ptc.Emitters.Where(e => _selected.Contains(e.Block)).ToList();

    private void ShowInspectors()
    {
        var doc = _active;
        if (doc == null)
        {
            _inspector.Clear(Loc.T("Empty.Inspector"));
            _material.Clear(Loc.T("Empty.Material"));
            Inspector.DataContext = _inspector;
            return;
        }
        var views = SelectedViews();
        if (_partSelected || views.Count == 0)
        {
            _effectInspector.Show(doc, new[] { new Target(doc.Ptc, doc.OriginalOf(doc.Ptc.Block)) }, doc.Title, Loc.T("Ins.Effect"));
            Inspector.DataContext = _effectInspector;
            _material.Clear(Loc.T("Empty.Material"));
            return;
        }
        Inspector.DataContext = _inspector;
        string title = views.Count == 1 ? views[0].Name : Loc.F("Ins.Many", views.Count);
        string sub = views.All(v => doc.Locked.Contains(v.Block)) ? Loc.T("Ins.Locked") : views.Count > 1 ? Loc.T("Ins.Shared") : Loc.T("Ins.Emitter");
        _inspector.Show(doc, views.Select(v => new Target(v, doc.OriginalOf(v.Block))).ToList(), title, sub);
        var materials = views.Select(v => v.Text("Material")).Distinct().ToList();
        if (materials.Count == 1 && materials[0] is { } game && doc.Materials.TryGetValue(game, out var m))
        {
            int users = doc.Ptc.Emitters.Count(e => e.Text("Material") == game);
            _material.Show(doc, new[] { new Target(m.Emat, m.OriginalRoot) }, Path.GetFileName(m.DiskPath),
                users > 1 ? Loc.F("Ins.MaterialShared", users) : m.Emat.ClassName);
        }
        else
        {
            _material.Clear(materials.Count > 1 ? Loc.T("Empty.MaterialMixed") : materials.FirstOrDefault() is { } missing ? Loc.F("Empty.MaterialMissing", missing) : Loc.T("Empty.MaterialNone"));
        }
    }

    // ---- edits from the layers panel ----

    public string? RenameProblem(EmitterRow row, string name)
    {
        var others = row.Part.Ptc.Emitters.Where(e => e.Block != row.Block).Select(e => e.Name);
        return Names.Problem(name, others) is { } key ? Loc.T(key) : null;
    }

    public void RenameEmitter(EmitterRow row, string name)
    {
        if (row.View is not { } v)
            return;
        string old = v.Name;
        row.Part.Do(v.Rename(name), Loc.Later("Undo.Rename", old, name));
    }

    public void AddEmitter()
    {
        var doc = _active;
        if (doc == null)
            return;
        string name = Names.Unique("emitor1", doc.Ptc.Emitters.Select(e => e.Name));
        doc.Do(doc.Ptc.AddEmitter(name), Loc.Later("Undo.Add", name));
        var added = doc.Ptc.Emitter(name);
        if (added != null)
            Select(doc, new[] { added.Block }, false);
    }

    public void CloneEmitter()
    {
        var views = SelectedViews();
        if (_active == null || views.Count != 1)
            return;
        var doc = _active;
        string name = Names.Unique(views[0].Name + "_cloned", doc.Ptc.Emitters.Select(e => e.Name));
        doc.Do(doc.Ptc.CloneEmitter(views[0], name), Loc.Later("Undo.Clone", views[0].Name));
        if (doc.Ptc.Emitter(name) is { } clone)
            Select(doc, new[] { clone.Block }, false);
    }

    // Several removals in one step, built from the last emitter backwards so earlier indices stay valid.
    public void DeleteEmitters()
    {
        var doc = _active;
        if (doc == null)
            return;
        var views = SelectedViews().Where(v => !doc.Locked.Contains(v.Block)).ToList();
        if (views.Count == 0)
            return;
        var all = doc.Ptc.Emitters.ToList();
        var edits = views.OrderByDescending(v => all.FindIndex(e => e.Block == v.Block)).Select(v => doc.Ptc.RemoveEmitter(v)).ToList();
        int keep = all.FindIndex(e => e.Block == views[0].Block);
        doc.Do(new CompositeEdit(edits), Loc.Later("Undo.Delete", string.Join(", ", views.Select(v => v.Name))));
        var left = doc.Ptc.Emitters;
        Select(doc, left.Count > 0 ? new[] { left[Math.Clamp(keep, 0, left.Count - 1)].Block } : Array.Empty<EnfBlock>(), left.Count == 0);
        Toast(Loc.F("Toast.Deleted", views.Count), "info");
    }

    public void MoveEmitter(EmitterRow row, EmitterRow target)
    {
        if (row.View is not { } v || target.View is not { } t)
            return;
        int to = row.Part.Ptc.Emitters.ToList().FindIndex(e => e.Block == t.Block);
        row.Part.Do(row.Part.Ptc.MoveEmitter(v, to), Loc.Later("Undo.Move", v.Name));
    }

    // ---- ITimelineHost ----

    public IReadOnlyList<Pfx.Sim.EmitterParams> ParamsOf(EffectDoc doc) =>
        doc.Ptc.Emitters.Select(e => Pfx.Sim.EmitterParams.From(e, TilesOf(doc))).ToList();

    // Tiles from the open material documents (unsaved grid edits count), else from the files.
    private Func<string, (int U, int V)?> TilesOf(EffectDoc doc)
    {
        var disk = _roots.Tiles();
        return material => doc.Materials.TryGetValue(material, out var m)
            ? (Math.Max(1, (int)(m.Emat.Number("TileU") ?? 1)), Math.Max(1, (int)(m.Emat.Number("TileV") ?? 1)))
            : disk(material);
    }

    // ---- IAtlasHost / ITextureHost ----

    public string? Resolve(string gamePath) => _roots.Resolve(gamePath);

    public void OpenInBuilder()
    {
        if (_slicer.Levels is not { Length: > 0 } levels || _slicer.TexturePath == null)
            return;
        if (AtlasPanel.Builder.AddSheet(Path.GetFileNameWithoutExtension(_slicer.TexturePath), levels[0], _slicer.TileU, _slicer.TileV)
            && _slicer.Params is { AnimFps: > 0 } p)
            AtlasPanel.Builder.Fps = p.AnimFps;
        ShowPane("atlas");
    }

    public Pfx.Textures.Texconv? TexconvTool(out string? why)
    {
        string path = _launch.Texconv ?? Settings.Texconv;
        why = path.Length == 0 ? Loc.T("Why.NoTexconv") : !File.Exists(path) ? Loc.F("Why.FileMissing", path) : null;
        return why == null ? new Pfx.Textures.Texconv(path) : null;
    }

    public Pfx.Textures.CpuUpscaler? UpscalerTool(out string? why)
    {
        string bridge = Payload.Ensure(Payload.Upscale) ?? Path.Combine(AppContext.BaseDirectory, "Tools", Payload.Upscale);
        why = Settings.Upscaler.Length == 0 ? Loc.T("Why.NoUpscaler") : !File.Exists(Settings.Upscaler) ? Loc.F("Why.FileMissing", Settings.Upscaler)
            : Settings.UpscaleModel.Length == 0 ? Loc.T("Why.NoModel") : !File.Exists(Settings.UpscaleModel) ? Loc.F("Why.FileMissing", Settings.UpscaleModel)
            : !File.Exists(Path.ChangeExtension(Settings.UpscaleModel, ".bin")) ? Loc.F("Why.FileMissing", Path.ChangeExtension(Settings.UpscaleModel, ".bin"))
            : !File.Exists(bridge) ? Loc.F("Why.FileMissing", bridge) : Settings.Python.Trim().Length == 0 ? Loc.T("Why.NoPython") : null;
        return why == null ? new Pfx.Textures.CpuUpscaler(Settings.Python, bridge, Settings.Upscaler, Settings.UpscaleModel) : null;
    }

    // A file the builder wrote: the viewport's decoded copy goes, every part using it builds again, the slicer reloads.
    public void TextureWritten(string path)
    {
        string full = Path.GetFullPath(path);
        _viewport.Textures.Forget(full);
        foreach (var d in _docs)
            if (TextureFacts.TexturesOf(d, Resolve).Any(t => string.Equals(Path.GetFullPath(t), full, StringComparison.OrdinalIgnoreCase)))
                _pipeline.Request(d);
        if (_slicer.TexturePath != null && string.Equals(Path.GetFullPath(_slicer.TexturePath), full, StringComparison.OrdinalIgnoreCase))
            _slicer.Reload();
        AtlasPanel.Refresh();
    }

    public void ToolsChanged()
    {
        if (!_launch.Automated)
            Settings.Save();
        AtlasPanel.Refresh();
    }

    public void RestartPreview() => _pipeline.Restart();

    public void StepPreview()
    {
        _pipeline.Step();
        UpdateButtons();
    }

    public void PickTrack(EffectDoc doc, EnfBlock? emitter) =>
        Select(doc, emitter != null ? new[] { emitter } : Array.Empty<EnfBlock>(), emitter == null);

    // One history over the project and every open part (ShellHistory): the newest step goes first, wherever it was made.
    internal void UndoActive() => _history.Undo();

    internal void RedoActive() => _history.Redo();

    // ---- refresh after a document change ----

    private void OnDocChanged(EffectDoc doc)
    {
        long changedAt = Pfx.Link.LinkHub.Now;
        _history.Sync(doc);
        // A structural change (emitter added, removed, moved, undone) rebuilds the rows; selection keeps its blocks.
        var blocks = doc.Ptc.Emitters.Select(e => e.Block).ToList();
        var rows = _state.Layers.OfType<EmitterRow>().Where(r => r.Doc == doc).Select(r => r.Block).ToList();
        if (!blocks.SequenceEqual(rows))
        {
            doc.LoadMaterials(_roots);
            RebuildLayers();
            if (doc == _active)
            {
                _selected.RemoveAll(b => !blocks.Contains(b));
                Layers.SetSelection(_state.Layers.Where(r => r is EmitterRow er && _selected.Contains(er.Block)));
            }
        }
        foreach (var r in _state.Layers.Where(r => r.Doc == doc))
            RefreshRow(r);
        foreach (var t in _state.Tabs.Where(t => t.Doc == doc))
            t.Refresh();
        if (doc == _active)
        {
            ShowInspectors();
            ShowCurves();
            CurvesPanel.Editor.Refresh();
            _slicer.Refresh();
            AtlasPanel.Refresh();
            ShowFileText();
        }
        _pipeline.Request(doc);
        PushPart(doc, changedAt);
        RequestLint();
        TimelinePanel.Invalidate();
        UpdateTitle();
        UpdateButtons();
    }

    private void RebuildLayers()
    {
        _state.Layers.Clear();
        foreach (var doc in _docs)
        {
            _state.Layers.Add(new PartRow(doc));
            foreach (var e in doc.Ptc.Emitters)
            {
                var row = new EmitterRow(doc, e.Block);
                row.Changed += () => OnRowFlag(row);
                _state.Layers.Add(row);
            }
        }
        // Recipe nodes after the parts, each with what it made (the panel lists at most this many changes per node).
        foreach (var node in _project?.File.Recipes ?? new List<RecipeNode>())
        {
            var recipe = Pfx.App.Recipes.RecipeRunner.Find(node);
            _state.Layers.Add(new RecipeRow(node, recipe != null ? RecipeName(recipe, node.Own) : node.Kind,
                recipe != null ? RecipesView.StatusBrush(recipe.Status) : "B.TextDim"));
            foreach (var op in node.Log.Where(o => o.Op is RecipeOp.EmitterAdd or RecipeOp.ItemAdd or RecipeOp.Key or RecipeOp.Field or RecipeOp.PartText).Take(LayerMadeRows))
                _state.Layers.Add(new MadeRow(node, op, RecipesView.Describe(op)));
        }
        foreach (var r in _state.Layers)
            RefreshRow(r);
    }

    // A tornado node makes hundreds of changes; the layers list shows the first ones, the recipes panel lists them all.
    private const int LayerMadeRows = 12;

    private static void RefreshRow(LayerRow r)
    {
        if (r is EmitterRow er && er.View is { } v)
        {
            var original = er.Part.OriginalOf(er.Block);
            var sb = new System.Text.StringBuilder();
            er.Block.Write(sb);
            string now = sb.ToString();
            string? then = null;
            if (original != null)
            {
                var o = new System.Text.StringBuilder();
                original.Write(o);
                then = o.ToString();
            }
            er.Modified = then != now;
        }
        r.Refresh();
    }

    // Eye and solo change only the preview; the lock changes what the inspector may edit.
    private void OnRowFlag(EmitterRow row)
    {
        _pipeline.Request(row.Part);
        foreach (var r in _state.Layers.OfType<EmitterRow>().Where(r => r.Doc == row.Doc))
            r.Refresh();
        if (row.Doc == _active)
            ShowInspectors();
    }

    private void UpdateAll()
    {
        foreach (var t in _state.Tabs)
            t.Refresh();
        EmptyHint.Visibility = _docs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ViewportSlot.Visibility = _docs.Count == 0 ? Visibility.Hidden : Visibility.Visible;
        if (_active == null)
            ShowInspectors();
        ShowFileText();
        UpdateTitle();
        UpdateButtons();
        RequestLint();
    }

    private void UpdateTitle()
    {
        string name = _active == null ? "" : _active.Title + (_active.Dirty ? " *" : "") + " — ";
        Title = name + Loc.T("App.Title");
        UndoText.Text = _history.Top?.UndoLabel is { } u ? Loc.F("Status.Undo", u) : Loc.T("Status.NoUndo");
    }

    private void UpdateButtons()
    {
        Layers.CloneButton.IsEnabled = _active != null && SelectedViews().Count == 1;
        Layers.DeleteButton.IsEnabled = _active != null && SelectedViews().Any(v => !_active.Locked.Contains(v.Block));
        PlayIcon.Data = (System.Windows.Media.Geometry)FindResource(_pipeline.Playing ? "Ic.Pause" : "Ic.Play");
        PlayText.Text = Loc.T(_pipeline.Playing ? "Btn.Playing" : "Btn.Paused");
    }

    // ---- file text panel (read-only; the selected field's line is highlighted) ----

    private void ShowFileText()
    {
        FileText.Text = _active == null ? "" : EnfLexer.FileEncoding.GetString(_active.Ptc.ToBytes());
    }

    private void ShowInText(FieldVm f)
    {
        if (_active == null || f.Targets.FirstOrDefault()?.Block is not { } target || target.Doc != _active.Ptc.Doc)
        {
            Toast(Loc.T("Toast.TextOtherFile"), "info");
            return;
        }
        ShowFileText();
        var item = target.Block.Items.FirstOrDefault(i => EnfEdits.KeyOf(i).Equals(f.Key, StringComparison.OrdinalIgnoreCase));
        if (item == null)
            return;
        int at = TextLocator.OffsetOf(_active.Ptc.Doc, item);
        if (at < 0)
            return;
        int end = FileText.Text.IndexOf('\n', at);
        TextPane.Show();
        TextPane.IsActive = true;
        FileText.Focus();
        FileText.Select(at, (end < 0 ? FileText.Text.Length : end) - at);
        FileText.ScrollToLine(FileText.GetLineIndexFromCharacterIndex(at));
    }

    private void FindSame(FieldVm f)
    {
        var target = f.Targets.FirstOrDefault();
        if (target == null)
            return;
        string? value = Raw.Of(target.Block.Block, f.Key);
        int count = 0, files = 0;
        foreach (var doc in _docs)
        {
            int before = count;
            var blocks = f.Scope switch
            {
                FieldScope.Emitter => doc.Ptc.Emitters.Select(e => e.Block),
                FieldScope.Effect => new[] { doc.Ptc.Block },
                _ => doc.Materials.Values.Select(m => m.Emat.Block),
            };
            count += blocks.Count(b => Raw.Of(b, f.Key) == value);
            if (count > before)
                files++;
        }
        Toast(Loc.F("Toast.FindSame", f.Label, value ?? Loc.T("Val.GameDefault"), count, files), "info");
    }

    public string? GamePathOf(string file)
    {
        string full = Path.GetFullPath(file);
        foreach (var root in _roots.Roots)
        {
            string r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (full.StartsWith(r, StringComparison.OrdinalIgnoreCase))
                return full[r.Length..].Replace(Path.DirectorySeparatorChar, '/');
        }
        return null;
    }

    // ---- keyboard ----

    private void OnKeys(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        bool typing = Keyboard.FocusedElement is TextBox;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (ctrl && key == Key.O) { OpenDialog(); e.Handled = true; }
        else if (ctrl && key == Key.S) { Save(_active, shift); e.Handled = true; }
        else if (ctrl && key == Key.F) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
        else if (ctrl && key == Key.Z && !typing) { UndoActive(); e.Handled = true; }
        else if (ctrl && key == Key.Y && !typing) { RedoActive(); e.Handled = true; }
        else if (ctrl && key == Key.D && !typing) { CloneEmitter(); e.Handled = true; }
        else if (key == Key.Space && !typing && Keyboard.FocusedElement is not ButtonBase) { TogglePlay(); e.Handled = true; }
        else if (!ctrl && shift && !typing && key == Key.R) { SetTool("recipe"); e.Handled = true; }
        else if (!ctrl && !typing && KeyInterop.VirtualKeyFromKey(key) is int vk && ToolKeys.TryGetValue(vk, out var tool)) { SetTool(tool); e.Handled = true; }
    }

    // ---- ISettingsHost ----

    public void RootsChanged()
    {
        if (!_launch.Automated)
            Settings.Save();
        _roots = new DataRoots(RootList());
        foreach (var d in _docs)
        {
            d.LoadMaterials(_roots);
            _pipeline.Request(d);
        }
        ShowInspectors();
        RequestLint();
        LibraryPanel.Rescan();
    }

    public void LanguageChanged(string lang)
    {
        Loc.Set(lang);
        Settings.Lang = lang;
        if (!_launch.Automated)
            Settings.Save();
    }
}

// Character offset of an item in the written file text (the same concatenation EnfDocument.Write does).
public static class TextLocator
{
    public static int OffsetOf(EnfDocument doc, EnfItem target)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var item in doc.Items)
            if (Walk(item, target, sb) is int at)
                return at;
        return -1;
    }

    private static int? Walk(EnfItem item, EnfItem target, System.Text.StringBuilder sb)
    {
        if (item == target)
            return sb.Length + item.First.Leading.Length;
        if (item is EnfBlock b)
        {
            foreach (var t in b.Header)
                sb.Append(t.Leading).Append(t.Text);
            sb.Append(b.Open.Leading).Append(b.Open.Text);
            foreach (var child in b.Items)
                if (Walk(child, target, sb) is int at)
                    return at;
            sb.Append(b.Close.Leading).Append(b.Close.Text);
        }
        else
        {
            item.Write(sb);
        }
        return null;
    }
}
