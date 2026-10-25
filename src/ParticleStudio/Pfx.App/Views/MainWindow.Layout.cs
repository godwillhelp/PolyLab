using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AvalonDock;
using AvalonDock.Layout;
using AvalonDock.Layout.Serialization;
using AvalonDock.Themes;
using Pfx.App.Automation;
using Pfx.App.Core;
using Pfx.App.ViewModels;
using Pfx.Render;

namespace Pfx.App.Views;

// Menus, docking layouts and named workspaces (spec 5.1), toasts, language switching and the automation entry points.
public partial class MainWindow
{
    public static readonly string[] Workspaces = { "emitters", "textures", "anomaly", "import" };
    // A toast stays this long: long enough to read two lines.
    private static readonly TimeSpan ToastLife = TimeSpan.FromSeconds(4.5);
    private const int ToastsShown = 3;

    private string _defaultLayout = "";
    private Popup? _toastPopup;

    private void InitShell()
    {
        var theme = new ResourceDictionary { Source = new Uri("pack://application:,,,/Particle_Studio;component/Themes/Dock.xaml") };
        theme[AvalonDock.Themes.VS2013.Themes.ResourceKeys.ControlAccentColorKey] = (Color)FindResource("C.Accent");
        Dock.Theme = new PaletteDockTheme(theme);
        SetPaneTitles();
        _defaultLayout = SerializeLayout();
        if (!_launch.Automated && File.Exists(AppSettings.LayoutPath(Settings.Workspace)))
            LoadLayout(File.ReadAllText(AppSettings.LayoutPath(Settings.Workspace)));
        else
            ApplyWorkspaceDefaults(Settings.Workspace);
        BuildMenu();
        BuildToasts();
        SettingsPanel.Show(Settings);
        WorkspaceText.Text = Loc.F("Lbl.Workspace", Loc.T("Ws." + Settings.Workspace));
        LocationChanged += (_, _) => PlaceToasts();
        SizeChanged += (_, _) => PlaceToasts();
    }

    private void SetPaneTitles()
    {
        ViewportDoc.Title = Loc.T("Pane.Viewport");
        TimelinePane.Title = Loc.T("Pane.Timeline");
        CurvesPane.Title = Loc.T("Pane.Curves");
        SlicerPane.Title = Loc.T("Pane.Slicer");
        AtlasPane.Title = Loc.T("Pane.Atlas");
        TexturePane.Title = Loc.T("Pane.Texture");
        TextPane.Title = Loc.T("Pane.Text");
        ImportDoc.Title = Loc.T("Pane.Import");
        ImportDoc.ToolTip = Loc.T("Tip.Pane.import");
        XrayPane.Title = Loc.T("Pane.Xray");
        AbPane.Title = Loc.T("Pane.Ab");
        SubstPane.Title = Loc.T("Pane.Subst");
        SettingsPane.Title = Loc.T("Pane.Settings");
        ComposePane.Title = Loc.T("Pane.Compose");
        RecipesPane.Title = Loc.T("Pane.Recipes");
        LibraryPane.Title = Loc.T("Pane.Library");
        ExportPane.Title = Loc.T("Pane.Export");
        GamePane.Title = Loc.T("Pane.Game");
        CheckPane.Title = Loc.T("Pane.Checks");
        LayersPane.Title = Loc.T("Pane.Layers");
        InspectorPane.Title = Loc.T("Pane.Inspector");
        MaterialPane.Title = Loc.T("Pane.Material");
        foreach (var a in Anchorables())
            a.ToolTip = Loc.T("Tip.Pane." + a.ContentId);
    }

    private IEnumerable<LayoutAnchorable> Anchorables() => Dock.Layout.Descendents().OfType<LayoutAnchorable>()
        .Concat(Dock.Layout.Hidden).Distinct();

    // ---- menus (built in code so a language switch rebuilds them) ----

    private void BuildMenu()
    {
        MainMenu.Items.Clear();
        MenuItem Top(string key) => new() { Header = Loc.T("Menu." + key), ToolTip = Loc.T("Tip.Menu." + key) };
        MenuItem Item(string key, Action run, bool enabled = true, string gesture = "", bool? check = null)
        {
            var m = new MenuItem { Header = Loc.T(key), IsEnabled = enabled, InputGestureText = gesture, ToolTip = Loc.T("Tip." + key) };
            if (check is bool c)
                m.IsChecked = c;
            m.Click += (_, _) => run();
            return m;
        }

        var file = Top("File");
        file.Items.Add(Item("Cmd.NewProject", NewProject));
        file.Items.Add(Item("Cmd.NewTornado", NewTornado));
        file.Items.Add(Item("Cmd.OpenProject", OpenProjectDialog));
        file.Items.Add(Item("Cmd.SaveProject", () => SaveProject(false), _project != null));
        file.Items.Add(Item("Cmd.SaveProjectAs", () => SaveProject(true), _project != null));
        file.Items.Add(new Separator());
        file.Items.Add(Item("Cmd.Open", OpenDialog, true, "Ctrl+O"));
        var recent = new MenuItem { Header = Loc.T("Cmd.Recent"), ToolTip = Loc.T("Tip.Cmd.Recent"), IsEnabled = Settings.Recent.Count > 0 };
        foreach (var path in Settings.Recent)
        {
            var r = new MenuItem { Header = Path.GetFileName(path), ToolTip = path, IsEnabled = File.Exists(path) };
            r.Click += (_, _) =>
            {
                if (Pfx.Core.Model.ProjectFile.IsProject(path))
                    OpenProject(path);
                else
                    Open(new[] { path });
            };
            recent.Items.Add(r);
        }
        file.Items.Add(recent);
        file.Items.Add(new Separator());
        file.Items.Add(Item("Cmd.Save", () => Save(_active), _active != null, "Ctrl+S"));
        file.Items.Add(Item("Cmd.SaveAs", () => Save(_active, true), _active != null, "Ctrl+Shift+S"));
        file.Items.Add(Item("Cmd.SaveAll", () => { foreach (var d in _docs.ToList()) Save(d); }, _docs.Any(d => d.Dirty)));
        file.Items.Add(Item("Cmd.Close", () => { if (_active != null) CloseDoc(_active); }, _active != null));
        file.Items.Add(new Separator());
        file.Items.Add(Item("Cmd.Exit", Close));
        MainMenu.Items.Add(file);

        var edit = Top("Edit");
        edit.Items.Add(Item("Cmd.Undo", UndoActive, true, "Ctrl+Z"));
        edit.Items.Add(Item("Cmd.Redo", RedoActive, true, "Ctrl+Y"));
        edit.Items.Add(new Separator());
        edit.Items.Add(Item("Cmd.Find", () => { SearchBox.Focus(); SearchBox.SelectAll(); }, true, "Ctrl+F"));
        edit.Items.Add(Item("Cmd.Replace", () => { }, false));
        MainMenu.Items.Add(edit);

        var effect = Top("Effect");
        effect.Items.Add(Item("Cmd.EffectProps", () => { if (_active != null) Select(_active, Array.Empty<Pfx.Core.Text.EnfBlock>(), true); }, _active != null));
        effect.Items.Add(Item("Cmd.Play", TogglePlay, true, Loc.T("Key.Space")));
        effect.Items.Add(Item("Cmd.Step", () => { _pipeline.Step(); UpdateButtons(); }));
        effect.Items.Add(Item("Cmd.Restart", _pipeline.Restart));
        MainMenu.Items.Add(effect);

        var emitter = Top("Emitter");
        emitter.Items.Add(Item("Cmd.AddEmitter", AddEmitter, _active != null));
        emitter.Items.Add(Item("Cmd.Clone", CloneEmitter, _active != null, "Ctrl+D"));
        emitter.Items.Add(Item("Cmd.Rename", () => { foreach (var r in _state.Layers.OfType<EmitterRow>().Where(r => r.Doc == _active && _selected.Contains(r.Block)).Take(1)) r.Renaming = true; }, _active != null, "F2"));
        emitter.Items.Add(Item("Cmd.Delete", DeleteEmitters, _active != null, "Del"));
        MainMenu.Items.Add(emitter);

        var view = Top("View");
        foreach (var p in Presets)
            view.Items.Add(Item("View." + p, () => ApplyView(p)));
        MainMenu.Items.Add(view);

        var link = Top("Link");
        foreach (var item in LinkItems())
            link.Items.Add(item);
        MainMenu.Items.Add(link);

        var window = Top("Window");
        foreach (var ws in Workspaces)
            window.Items.Add(Item("Ws." + ws, () => SwitchWorkspace(ws), true, "", Settings.Workspace == ws));
        window.Items.Add(new Separator());
        window.Items.Add(Item("Cmd.SaveLayout", SaveLayoutNow));
        window.Items.Add(Item("Cmd.ResetLayout", ResetLayout));
        window.Items.Add(new Separator());
        foreach (var a in Anchorables())
        {
            var anchorable = a;
            var show = new MenuItem { Header = Loc.F("Cmd.ShowPane", anchorable.Title), ToolTip = Loc.T("Tip.Cmd.ShowPane") };
            show.Click += (_, _) => { anchorable.Show(); anchorable.IsActive = true; };
            window.Items.Add(show);
        }
        MainMenu.Items.Add(window);

        var help = Top("Help");
        help.Items.Add(Item("Cmd.Keys", () => Toast(Loc.T("Toast.Keys"), "info")));
        help.Items.Add(Item("Cmd.Differences", () => Toast(Loc.T("Toast.Differences"), "info")));
        MainMenu.Items.Add(help);
    }

    // ---- layouts and workspaces ----

    private string SerializeLayout()
    {
        using var w = new StringWriter();
        new XmlLayoutSerializer(Dock).Serialize(w);
        return w.ToString();
    }

    // Panels are found again by ContentId; the viewport and panels keep their objects (the viewport keeps its device).
    internal void LoadLayout(string xml)
    {
        var content = new Dictionary<string, object>
        {
            ["viewport"] = ViewportPanel, ["timeline"] = TimelinePanel, ["curves"] = CurvesPanel, ["slicer"] = SlicerPanel, ["atlas"] = AtlasPanel,
            ["text"] = FileText, ["settings"] = SettingsPanel, ["layers"] = Layers, ["inspector"] = Inspector, ["material"] = MaterialInspector,
            ["texture"] = TexturePanel, ["import"] = ImportTab, ["xray"] = XrayIndexPanel, ["xab"] = AbComparePanel, ["xsubst"] = SubstitutesPanel,
            ["compose"] = ComposePanel, ["export"] = ExportPanel, ["gameref"] = GamePanel, ["checks"] = CheckPanel,
            ["recipes"] = RecipesPanel, ["library"] = LibraryPanel,
        };
        var s = new XmlLayoutSerializer(Dock);
        s.LayoutSerializationCallback += (_, e) =>
        {
            if (e.Model.ContentId != null && content.TryGetValue(e.Model.ContentId, out var c))
                e.Content = c;
            else
                e.Cancel = true;
        };
        try
        {
            using var r = new StringReader(xml);
            s.Deserialize(r);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Xml.XmlException)
        {
            using var r = new StringReader(_defaultLayout);
            s.Deserialize(r);
            Toast(Loc.T("Toast.LayoutBroken"), "warn");
        }
        AddMissingPanes(content);
        RebindPanes();
        SetPaneTitles();
    }

    // A layout saved before a panel existed (the P5 layouts have no timeline and no curves, the P6 ones no texture
    // panels) does not have it: each missing panel joins the pane it has in the default layout (the texture panel the
    // pane of the material, the others the pane of the file text, ahead of it).
    private void AddMissingPanes(Dictionary<string, object> content)
    {
        var present = Dock.Layout.Descendents().OfType<LayoutContent>().Concat(Dock.Layout.Hidden).Select(c => c.ContentId).ToHashSet();
        var missing = content.Keys.Where(id => id != "viewport" && !present.Contains(id)).ToList();
        if (missing.Remove("import") && Dock.Layout.Descendents().OfType<LayoutDocumentPane>().FirstOrDefault(p => p.Children.Any(c => c.ContentId == "viewport")) is { } docs)
            docs.Children.Add(new LayoutDocument { ContentId = "import", Content = content["import"], CanClose = false, CanFloat = false });
        if (missing.Count == 0)
            return;
        var panes = Dock.Layout.Descendents().OfType<LayoutAnchorablePane>().ToList();
        var bottom = panes.FirstOrDefault(p => p.Children.Any(a => a.ContentId == "text")) ?? panes.FirstOrDefault();
        var right = panes.FirstOrDefault(p => p.Children.Any(a => a.ContentId == "material")) ?? bottom;
        var top = panes.FirstOrDefault(p => p.Children.Any(a => a.ContentId == "layers")) ?? right;
        if (bottom == null || right == null || top == null)
            return;
        int at = 0;
        foreach (var id in missing)
        {
            var a = new LayoutAnchorable { ContentId = id, Content = content[id], CanClose = false };
            if (id == "library")
                top.Children.Add(a);
            else if (id is "texture" or "compose" or "recipes")
                right.Children.Add(a);
            else
                bottom.Children.Insert(Math.Min(at++, bottom.Children.Count), a);
        }
    }

    // The "textures" workspace without a saved layout of its own: the default layout with the slicer and the texture
    // panel in front and a taller bottom dock (the slicer needs the room; the curves keep theirs elsewhere).
    private const double TexturesDockHeight = 380;

    private void ApplyWorkspaceDefaults(string ws)
    {
        if (ws == "anomaly")
        {
            ComposePane.IsSelected = true;
            ExportPane.IsSelected = true;
            return;
        }
        if (ws == "import")
        {
            ImportDoc.IsSelected = true;
            XrayPane.IsSelected = true;
            return;
        }
        if (ws != "textures")
            return;
        if (SlicerPane.Parent is LayoutAnchorablePane bottom)
            bottom.DockHeight = new GridLength(TexturesDockHeight);
        SlicerPane.IsSelected = true;
        TexturePane.IsSelected = true;
    }

    // After a layout load the Layout* objects are new; the named fields must point at them again.
    private void RebindPanes()
    {
        var all = Dock.Layout.Descendents().OfType<LayoutContent>().Concat(Dock.Layout.Hidden).ToList();
        LayoutContent? Find(string id) => all.FirstOrDefault(c => c.ContentId == id);
        if (Find("viewport") is LayoutDocument doc) ViewportDoc = doc;
        if (Find("timeline") is LayoutAnchorable tl) TimelinePane = tl;
        if (Find("curves") is LayoutAnchorable cu) CurvesPane = cu;
        if (Find("slicer") is LayoutAnchorable sl) SlicerPane = sl;
        if (Find("atlas") is LayoutAnchorable at) AtlasPane = at;
        if (Find("texture") is LayoutAnchorable tx) TexturePane = tx;
        if (Find("text") is LayoutAnchorable t) TextPane = t;
        if (Find("settings") is LayoutAnchorable s) SettingsPane = s;
        if (Find("layers") is LayoutAnchorable l) LayersPane = l;
        if (Find("inspector") is LayoutAnchorable i) InspectorPane = i;
        if (Find("material") is LayoutAnchorable m) MaterialPane = m;
        if (Find("import") is LayoutDocument im) ImportDoc = im;
        if (Find("xray") is LayoutAnchorable xr) XrayPane = xr;
        if (Find("xab") is LayoutAnchorable ab) AbPane = ab;
        if (Find("xsubst") is LayoutAnchorable su) SubstPane = su;
        if (Find("compose") is LayoutAnchorable co) ComposePane = co;
        if (Find("export") is LayoutAnchorable ex) ExportPane = ex;
        if (Find("gameref") is LayoutAnchorable gr) GamePane = gr;
        if (Find("checks") is LayoutAnchorable ck) CheckPane = ck;
        if (Find("recipes") is LayoutAnchorable rc) RecipesPane = rc;
        if (Find("library") is LayoutAnchorable lb) LibraryPane = lb;
    }

    internal void SwitchWorkspace(string ws)
    {
        if (ws == Settings.Workspace)
            return;
        SaveLayoutNow();
        Settings.Workspace = ws;
        string path = AppSettings.LayoutPath(ws);
        bool saved = File.Exists(path);
        LoadLayout(saved ? File.ReadAllText(path) : _defaultLayout);
        if (!saved)
            ApplyWorkspaceDefaults(ws);
        WorkspaceText.Text = Loc.F("Lbl.Workspace", Loc.T("Ws." + ws));
        if (!_launch.Automated)
            Settings.Save();
        BuildMenu();
        Toast(Loc.F("Toast.Workspace", Loc.T("Ws." + ws)), "info");
    }

    private void SaveLayoutNow()
    {
        if (_launch.Automated)
            return;
        string path = AppSettings.LayoutPath(Settings.Workspace);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.Write(path, System.Text.Encoding.UTF8.GetBytes(SerializeLayout()));
    }

    private void SaveLayoutOnExit()
    {
        try
        {
            SaveLayoutNow();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void ResetLayout()
    {
        LoadLayout(_defaultLayout);
        ApplyWorkspaceDefaults(Settings.Workspace);
        Toast(Loc.T("Toast.LayoutReset"), "info");
    }

    // ---- toasts: a popup at the bottom right (its own window, so it shows over the viewport's child window) ----

    private void BuildToasts()
    {
        var list = new ItemsControl { ItemsSource = _state.Toasts, Focusable = false };
        var template = new DataTemplate(typeof(Toast));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, FindResource("B.BgDeep"));
        border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("Brush") { Converter = new Controls.ThemeBrush() });
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1, 1, 1, 1));
        border.SetValue(Border.CornerRadiusProperty, FindResource("R.Panel"));
        border.SetValue(Border.PaddingProperty, new Thickness(10, 7, 10, 7));
        border.SetValue(Border.MarginProperty, new Thickness(0, 6, 0, 0));
        border.SetValue(Border.WidthProperty, 320.0);
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Text"));
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        text.SetValue(TextBlock.ForegroundProperty, FindResource("B.Text"));
        text.SetValue(TextBlock.FontSizeProperty, FindResource("F.Dense"));
        border.AppendChild(text);
        template.VisualTree = border;
        list.ItemTemplate = template;
        _toastPopup = new Popup
        {
            Child = list, AllowsTransparency = true, Placement = PlacementMode.Relative, PlacementTarget = (UIElement)Content,
            IsOpen = false, Focusable = false, StaysOpen = true,
        };
    }

    public void Toast(string text, string kind)
    {
        var t = new Toast(text, kind);
        _state.Toasts.Add(t);
        while (_state.Toasts.Count > ToastsShown)
            _state.Toasts.RemoveAt(0);
        var timer = new DispatcherTimer { Interval = ToastLife };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _state.Toasts.Remove(t);
            PlaceToasts();
        };
        timer.Start();
        PlaceToasts();
    }

    private void PlaceToasts()
    {
        if (_toastPopup == null || Content is not FrameworkElement root)
            return;
        bool show = _state.Toasts.Count > 0 && IsVisible && WindowState != WindowState.Minimized;
        if (!show)
        {
            _toastPopup.IsOpen = false;
            return;
        }
        _toastPopup.Child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _toastPopup.Child.DesiredSize;
        _toastPopup.HorizontalOffset = Math.Max(0, root.ActualWidth - size.Width - 14);
        _toastPopup.VerticalOffset = Math.Max(0, root.ActualHeight - size.Height - 34);
        _toastPopup.IsOpen = false;
        _toastPopup.IsOpen = true;
    }

    public bool ToastShown => _toastPopup?.IsOpen == true;
    public IReadOnlyList<LayerRow> LayerRows => _state.Layers;
    public bool PanesBound => new object?[] { ViewportDoc?.Content, LayersPane?.Content, InspectorPane?.Content, MaterialPane?.Content, TextPane?.Content,
        SettingsPane?.Content, TimelinePane?.Content, CurvesPane?.Content, SlicerPane?.Content, AtlasPane?.Content, TexturePane?.Content,
        ImportDoc?.Content, XrayPane?.Content, AbPane?.Content, SubstPane?.Content, ComposePane?.Content, ExportPane?.Content, CheckPane?.Content,
        RecipesPane?.Content, LibraryPane?.Content }.All(c => c != null);

    // ---- language ----

    private void OnLanguage()
    {
        SetPaneTitles();
        BuildMenu();
        SettingsPanel.Show(Settings);
        WorkspaceText.Text = Loc.F("Lbl.Workspace", Loc.T("Ws." + Settings.Workspace));
        ShowOptions(_options);
        foreach (var r in _state.Layers)
            r.Refresh();
        ShowInspectors();
        _effectInspector.Refresh();
        CurvesPanel.Refresh();
        SlicerPanel.Refresh();
        AtlasPanel.Refresh();
        TexturePanel.Invalidate();
        TimelinePanel.Rebuild();
        if (_xray.LastScan is { } scan)
            ImportTab.ShowScan(scan);
        XrayIndexPanel.Rebuild();
        AbComparePanel.Show();
        SubstitutesPanel.Show();
        ComposePanel.Show();
        RecipesPanel.Show();
        LibraryPanel.Rescan();
        ExportPanel.Invalidate();
        CheckPanel.Refresh();
        ShowLinkState();
        UpdateTitle();
        UpdateButtons();
        UpdateStatus();
    }

    // ---- tools and search ----

    private void OnTool(object sender, RoutedEventArgs e) => SetTool((string)((FrameworkElement)sender).Tag);

    private void OnSearch(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var vm in new[] { _inspector, _effectInspector, _material })
            vm.Query = SearchBox.Text;
        int n = _inspector.MatchCount + _material.MatchCount;
        SearchCount.Text = SearchBox.Text.Length == 0 ? "" : n > 0 ? Loc.F("Search.Found", n) : Loc.T("Search.None");
        SearchCount.Foreground = (Brush)FindResource(SearchBox.Text.Length > 0 && n == 0 ? "B.Err" : "B.TextDim");
        if (n > 0 && _inspector.MatchCount > 0)
            InspectorPane.IsActive = true;
        else if (_material.MatchCount > 0)
            MaterialPane.IsActive = true;
    }

    private void OnSearchKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            SearchBox.Text = "";
            Keyboard.ClearFocus();
        }
    }

    // ---- automation (command line) ----

    private void StartAutomation()
    {
        if (_launch.Checks != null)
            new ShellChecks(this, _launch).Start();
        else if (_launch.SelfTest != null)
            new ViewportSelfTest(this, _viewport, _launch.SelfTest).Start();
        else if (_launch.EditBench != null)
            new EditBench(this, _launch).Start();
        else if (_launch.Flipbook != null)
            new FlipbookRun(this, _launch).Start();
        else if (_launch.PathRun != null)
            new PathRun(this, _launch).Start();
        else if (_launch.XrayRun != null)
            new XrayRun(this, _launch).Start();
        else if (_launch.Compose != null)
            new ComposeRun(this, _launch).Start();
        else if (_launch.Export != null)
            new ExportRun(this, _launch).Start();
        else if (_launch.Zone != null)
            new ZoneRun(this, _launch).Start();
        else if (_launch.LinkBench != null)
            new LinkRun(this, _launch).Start();
        else if (_launch.Tornado != null)
            new TornadoRun(this, _launch).Start();
        else if (_launch.Proof != null)
            new ProofRun(this, _launch).Start();
        else if (_launch.ReadLook != null)
            new ReadLook(this, _launch).Start();
    }

    public LayersView LayersPanel => Layers;
    public CurvesView Curves => CurvesPanel;
    public bool CurvesPaneActive => CurvesPane.IsSelected || CurvesPane.IsActive;
    public string DefaultLayout => _defaultLayout;
    public IEnumerable<string> PaneIds => Anchorables().Select(a => a.ContentId);
    public InspectorView InspectorPanel => Inspector;
    public InspectorView MaterialPanel => MaterialInspector;
    public FrameworkElement ViewportArea => ViewportSlot;
    public void ShowPane(string id)
    {
        if (id == "import" || id == "viewport")
        {
            (id == "import" ? ImportDoc : ViewportDoc).IsSelected = true;
            return;
        }
        if (Anchorables().FirstOrDefault(a => a.ContentId == id) is { } a)
        {
            a.Show();
            a.IsActive = true;
        }
    }
}

// AvalonDock's DictionaryTheme is abstract; this one carries the palette dictionary of Themes/Dock.xaml.
public sealed class PaletteDockTheme : DictionaryTheme
{
    public PaletteDockTheme(ResourceDictionary dictionary) : base(dictionary)
    {
    }
}
