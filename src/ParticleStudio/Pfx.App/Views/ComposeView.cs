using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Pfx.App.Core;
using Pfx.App.Model;
using Pfx.Core.Model;

namespace Pfx.App.Views;

public interface IComposeHost
{
    CompositionDoc? Project { get; }
    IReadOnlyList<EffectDoc> Docs { get; }
    bool StatesPlaying { get; set; }
    void NewProject();
    void OpenProjectDialog();
    void SaveProject(bool saveAs);
    void AddPartFile();
    void NewPartDialog();
    void AddOpenPart(EffectDoc doc);
    void PickSound(string soundId);
    void PreviewSound(string soundId);
    void SelectPart(string partId);
    void ShowPane(string id);
    void Toast(string text, string kind);
}

// The anomaly builder (board 5 / board 12, spec 3.8): the project's states and what each starts, every item of the
// composition (parts, lights, hazes, sounds, screen effects, strikes), the selected item's values and the anomaly's
// shells and timing. Rebuilt from the project after every change (the project object is replaced on each edit, so
// the panel keeps only ids: the selected state index and the selected item id). Every edit is one undo step of the
// project (number fields typed in a row merge by field).
public sealed class ComposeView : UserControl
{
    private const double LabelWidth = 112;
    private IComposeHost? _host;
    private int _state;
    private string? _item;
    private readonly StackPanel _body = new() { Margin = new Thickness(0, 0, 0, 8) };
    private readonly TextBlock _title = new();
    private readonly ToggleButton _play = new();
    private readonly ToggleButton _rings = new();
    private readonly WrapPanel _top = new() { Margin = new Thickness(8, 5, 8, 3) };

    public ComposeView()
    {
        Background = Brush("B.BgPanel");
        var top = _top;
        _title.Style = Find("T.Hi");
        _title.Margin = new Thickness(0, 0, 10, 4);
        _title.VerticalAlignment = VerticalAlignment.Center;
        top.Children.Add(_title);
        top.Children.Add(Button("Cmp.New", () => _host?.NewProject()));
        top.Children.Add(Button("Cmp.Open", () => _host?.OpenProjectDialog()));
        top.Children.Add(Button("Cmp.Save", () => _host?.SaveProject(false)));
        top.Children.Add(Button("Cmp.SaveAs", () => _host?.SaveProject(true)));
        _play.Style = Find("Btn.Seg");
        _play.Margin = new Thickness(6, 0, 4, 4);
        _play.Click += (_, _) =>
        {
            if (_host != null)
                _host.StatesPlaying = _play.IsChecked == true;
        };
        top.Children.Add(_play);
        _rings.Style = Find("Btn.Seg");
        _rings.Margin = new Thickness(0, 0, 4, 4);
        top.Children.Add(_rings);
        top.Children.Add(Button("Cmp.Export", () => _host?.ShowPane("export")));
        var dock = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        dock.Children.Add(new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Content = dock;
    }

    public void Init(IComposeHost host)
    {
        _host = host;
        Show();
    }

    // The far rings of the client LOD (60 m lights, 120 m idle visuals) in the viewport.
    public bool FarRings => _rings.IsChecked == true;

    public void SetFarRings(bool on) => _rings.IsChecked = on;

    public string? SelectedItem => _item;
    public int SelectedState => _state;

    public void SelectItem(string? id)
    {
        _item = id;
        Show();
    }

    public void SelectState(int index)
    {
        _state = index;
        Show();
    }

    private CompositionDoc? Doc => _host?.Project;

    private void Edit(Action<ProjectFile> edit, Func<string> label, string? merge = null)
    {
        if (Doc == null)
            return;
        Doc.Do(edit, label, merge);
    }

    public void Show()
    {
        // Texts follow the language (the buttons are made once).
        foreach (var b in _top.Children.OfType<Button>())
            if (b.Tag is string key)
            {
                b.Content = Loc.T(key);
                b.ToolTip = Loc.T("Tip." + key);
            }
        _play.Content = Loc.T("Cmp.PlayStates");
        _play.ToolTip = Loc.T("Tip.Cmp.PlayStates");
        _play.IsChecked = _host?.StatesPlaying == true;
        _play.IsEnabled = Doc is { File.States.Count: > 0 };
        _rings.Content = Loc.T("Cmp.FarRings");
        _rings.ToolTip = Loc.T("Tip.Cmp.FarRings");
        _body.Children.Clear();
        var doc = Doc;
        if (doc == null)
        {
            _title.Text = Loc.T("Cmp.NoProject");
            _title.ToolTip = Loc.T("Tip.Cmp.NoProject");
            _body.Children.Add(Hint(Loc.T("Cmp.NoProjectHint")));
            return;
        }
        var p = doc.File;
        _title.Text = doc.Title + (doc.Dirty ? " ●" : "");
        _title.ToolTip = doc.Path ?? Loc.T("Tip.Cmp.Unsaved");
        _state = Math.Clamp(_state, 0, Math.Max(0, p.States.Count - 1));
        if (_item != null && p.Find(_item) == null)
            _item = null;

        Section("Cmp.Project");
        TextRow("Cmp.Name", p.Name, v => { string id = ProjectNames.Id(v); Edit(f => f.Name = id, Loc.Later("Undo.Cmp", Loc.Later("Cmp.Name"))); });

        States(p);
        Items(p);
        if (_item != null && p.Find(_item) is { } item)
            Properties(p, item);
        Anomaly(p);
    }

    // ---- states ----

    private void States(ProjectFile p)
    {
        Section("Cmp.States");
        var chips = new WrapPanel { Margin = new Thickness(10, 2, 10, 4) };
        for (int i = 0; i < p.States.Count; i++)
        {
            int at = i;
            var s = p.States[i];
            var chip = new ToggleButton
            {
                Style = Find("Btn.Seg"), IsChecked = i == _state, Margin = new Thickness(0, 0, 4, 4),
                Content = StateLabel(s.Name) + (s.Duration > 0 ? " · " + Loc.F("Cmp.Seconds", Loc.Num(s.Duration)) : ""),
                ToolTip = Loc.F("Tip.Cmp.StateChip", StateLabel(s.Name), s.Items.Count),
            };
            chip.Click += (_, _) => SelectState(at);
            chips.Children.Add(chip);
        }
        chips.Children.Add(Button("Cmp.AddState", () =>
        {
            string name = ProjectNames.Unique(NextStateName(p), p.States.Select(s => s.Name));
            Edit(f => f.States.Add(new StateEntry { Name = name, Duration = f.States.Count == 0 ? 0 : 2 }), Loc.Later("Undo.CmpAddState", name));
            _state = p.States.Count;
            Show();
        }));
        _body.Children.Add(chips);
        if (p.States.Count == 0)
        {
            _body.Children.Add(Hint(Loc.T("Cmp.NoStatesHint")));
            return;
        }
        var st = p.States[_state];
        int si = _state;
        // A free name, with the K design's machine states one click away.
        TextRow("Cmp.StateName", st.Name, v =>
        {
            string id = ProjectNames.Id(v);
            if (id != st.Name)
                Edit(f => f.States[si].Name = id, Loc.Later("Undo.Cmp", Loc.Later("Cmp.StateName")));
        });
        var names = new WrapPanel { Margin = new Thickness(10 + LabelWidth, 0, 10, 2) };
        foreach (var n in StateNames.Where(n => n != st.Name && p.States.All(s => s.Name != n)))
        {
            var chip = new Button { Content = StateLabel(n), ToolTip = Loc.F("Tip.Cmp.StateNamePick", n), Margin = new Thickness(0, 0, 3, 3), Padding = new Thickness(5, 0, 5, 0) };
            chip.Click += (_, _) => Edit(f => f.States[si].Name = n, Loc.Later("Undo.Cmp", Loc.Later("Cmp.StateName")));
            names.Children.Add(chip);
        }
        _body.Children.Add(names);
        NumberRow("Cmp.Duration", st.Duration, 0, 3600, v => Edit(f => f.States[si].Duration = (float)v, Loc.Later("Undo.Cmp", Loc.Later("Cmp.Duration")), "dur" + si));
        CheckRow("Cmp.OverIdle", st.OverIdle, v => Edit(f => f.States[si].OverIdle = v, Loc.Later("Undo.Cmp", Loc.Later("Cmp.OverIdle"))), si > 0);
        var moves = new WrapPanel { Margin = new Thickness(10 + LabelWidth, 0, 10, 4) };
        moves.Children.Add(Button("Cmp.StateLeft", () => { Edit(f => Swap(f.States, si, si - 1), Loc.Later("Undo.CmpMoveState")); _state = si - 1; Show(); }, si > 0));
        moves.Children.Add(Button("Cmp.StateRight", () => { Edit(f => Swap(f.States, si, si + 1), Loc.Later("Undo.CmpMoveState")); _state = si + 1; Show(); }, si < p.States.Count - 1));
        moves.Children.Add(Button("Cmp.RemoveState", () => { Edit(f => f.States.RemoveAt(si), Loc.Later("Undo.CmpRemoveState", st.Name)); _state = Math.Max(0, si - 1); Show(); }));
        _body.Children.Add(moves);

        SubHeader("Cmp.StateItems");
        if (st.Items.Count == 0)
            _body.Children.Add(Hint(Loc.T("Cmp.NoItemsHint")));
        for (int i = 0; i < st.Items.Count; i++)
        {
            int ii = i;
            var it = st.Items[i];
            var found = p.Find(it.Ref);
            var row = new DockPanel { Margin = new Thickness(10, 1, 10, 1) };
            var remove = Tiny("Ic.Close", Loc.T("Tip.Cmp.RemoveFromState"), () => Edit(f => f.States[si].Items.RemoveAt(ii), Loc.Later("Undo.CmpRemoveItem", it.Ref)));
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            var delay = NumberBox(it.Delay, 0, 3600, v => Edit(f => f.States[si].Items[ii].Delay = (float)v, Loc.Later("Undo.Cmp", Loc.Later("Cmp.Delay")), $"delay{si}.{ii}"));
            delay.Width = 52;
            delay.ToolTip = Loc.T("Tip.Cmp.Delay");
            DockPanel.SetDock(delay, Dock.Right);
            row.Children.Add(delay);
            var delayLabel = new TextBlock { Text = Loc.T("Cmp.Delay"), Style = Find("T.Dim"), FontSize = 11, Margin = new Thickness(6, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = Loc.T("Tip.Cmp.Delay") };
            DockPanel.SetDock(delayLabel, Dock.Right);
            row.Children.Add(delayLabel);
            var name = new TextBlock
            {
                Text = (found != null ? KindLabel(found.Kind) + " · " : "") + it.Ref, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = Brush(found == null ? "B.Err" : it.Ref == _item ? "B.Accent" : "B.Text"), Cursor = Cursors.Hand,
                ToolTip = found == null ? Loc.F("Tip.Cmp.MissingRef", it.Ref) : Loc.T("Tip.Cmp.PickItem"),
            };
            name.MouseLeftButtonUp += (_, _) => SelectItem(it.Ref);
            row.Children.Add(name);
            _body.Children.Add(row);
        }
        var add = new ComboBox { ToolTip = Loc.T("Tip.Cmp.AddToState"), Margin = new Thickness(10, 3, 10, 4) };
        add.Items.Add(new ComboBoxItem { Content = Loc.T("Cmp.AddToState"), IsEnabled = false });
        foreach (var item in p.Items().Where(i => st.Items.All(x => x.Ref != i.Id)))
            add.Items.Add(new ComboBoxItem { Content = KindLabel(item.Kind) + " · " + item.Id, Tag = item.Id });
        add.SelectedIndex = 0;
        add.SelectionChanged += (_, _) =>
        {
            if (add.SelectedItem is ComboBoxItem { Tag: string id })
                Edit(f => f.States[si].Items.Add(new StateItem { Ref = id }), Loc.Later("Undo.CmpAddItem", id));
        };
        _body.Children.Add(add);
    }

    private static readonly string[] StateNames = { "idle", "charge", "active", "cooldown", "linger", "hit", "blowout" };

    private static string NextStateName(ProjectFile p) => StateNames.FirstOrDefault(n => p.States.All(s => s.Name != n)) ?? "state";

    public static string StateLabel(string name) => Loc.TryT("State." + name) ?? name;

    private static void Swap<T>(List<T> list, int a, int b) => (list[a], list[b]) = (list[b], list[a]);

    // ---- items ----

    private void Items(ProjectFile p)
    {
        Section("Cmp.Items");
        var adds = new WrapPanel { Margin = new Thickness(10, 2, 10, 4) };
        var partMenu = new ComboBox { ToolTip = Loc.T("Tip.Cmp.AddPart"), Margin = new Thickness(0, 0, 4, 4), MinWidth = 110 };
        partMenu.Items.Add(new ComboBoxItem { Content = Loc.T("Cmp.AddPart"), IsEnabled = false });
        partMenu.Items.Add(new ComboBoxItem { Content = Loc.T("Cmp.AddPartNew"), Tag = "new", ToolTip = Loc.T("Tip.Cmp.AddPartNew") });
        partMenu.Items.Add(new ComboBoxItem { Content = Loc.T("Cmp.AddPartFile"), Tag = "file" });
        foreach (var d in _host!.Docs.Where(d => p.Parts.All(x => Doc!.Resolve(x.Path) != System.IO.Path.GetFullPath(d.Path))))
            partMenu.Items.Add(new ComboBoxItem { Content = d.Title, Tag = d, ToolTip = d.Path });
        partMenu.SelectedIndex = 0;
        partMenu.SelectionChanged += (_, _) =>
        {
            if (partMenu.SelectedItem is ComboBoxItem { Tag: "file" })
                _host.AddPartFile();
            else if (partMenu.SelectedItem is ComboBoxItem { Tag: "new" })
                _host.NewPartDialog();
            else if (partMenu.SelectedItem is ComboBoxItem { Tag: EffectDoc d })
                _host.AddOpenPart(d);
        };
        adds.Children.Add(partMenu);
        adds.Children.Add(Button("Cmp.AddLight", () => AddItem(f => f.Lights.Add(new LightEntry { Id = NewId(f, "light") }), "light")));
        adds.Children.Add(Button("Cmp.AddHaze", () => AddItem(f => f.Hazes.Add(new HazeEntry { Id = NewId(f, "haze") }), "haze")));
        adds.Children.Add(Button("Cmp.AddSound", () => AddItem(f => f.Sounds.Add(new SoundEntry { Id = NewId(f, "sound") }), "sound")));
        adds.Children.Add(Button("Cmp.AddScreen", () => AddItem(f => f.Screens.Add(new ScreenEntry { Id = NewId(f, "screen") }), "screen")));
        adds.Children.Add(Button("Cmp.AddStrike", () => AddItem(f => f.Strikes.Add(new StrikeEntry { Id = NewId(f, "strike"), Part = f.Parts.FirstOrDefault()?.Id ?? "" }), "strike"),
            p.Parts.Count > 0));
        adds.Children.Add(Button("Cmp.AddFlash", () => AddItem(f => f.Flashes.Add(new FlashEntry { Id = NewId(f, "flash") }), "flash")));
        _body.Children.Add(adds);
        if (!p.Items().Any())
            _body.Children.Add(Hint(Loc.T("Cmp.NoItems")));
        foreach (var item in p.Items())
        {
            var it = item;
            int states = p.States.Count(s => s.Items.Any(x => x.Ref == it.Id));
            var row = new Border
            {
                Padding = new Thickness(10, 3, 10, 3), Background = Brush(it.Id == _item ? "B.BgSelected" : "B.Transparent"), Cursor = Cursors.Hand,
                ToolTip = Loc.F("Tip.Cmp.ItemRow", KindLabel(it.Kind), it.Id, states),
            };
            var line = new DockPanel();
            var used = new TextBlock { Text = states == 0 ? Loc.T("Cmp.Unused") : Loc.F("Cmp.InStates", states), Style = Find("T.Dim"), FontSize = 11 };
            DockPanel.SetDock(used, Dock.Right);
            line.Children.Add(used);
            line.Children.Add(new TextBlock { Text = KindLabel(it.Kind) + " · " + it.Id + Summary(it), TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Brush(it.Id == _item ? "B.TextHi" : "B.Text") });
            row.Child = line;
            row.MouseLeftButtonUp += (_, _) =>
            {
                SelectItem(it.Id);
                if (it is PartEntry)
                    _host.SelectPart(it.Id);
            };
            _body.Children.Add(row);
        }
    }

    private static string NewId(ProjectFile f, string stem) => ProjectNames.Unique(stem, f.Items().Select(i => i.Id));

    private void AddItem(Action<ProjectFile> add, string stem)
    {
        if (Doc == null)
            return;
        string id = NewId(Doc.File, stem);
        Edit(add, Loc.Later("Undo.CmpAddItem", id));
        _item = id;
        Show();
    }

    private static string Summary(IProjectItem item) => item switch
    {
        PartEntry part => (part.Spin != 0 ? " · " + Loc.F("Cmp.SumSpin", Loc.Num(part.Spin)) : "") + (part.Near > 0 ? " · " + Loc.F("Cmp.SumNear", Loc.Num(part.Near)) : "")
                          + (part.Stop is > 0 ? " · " + Loc.F("Cmp.SumStop", Loc.Num(part.Stop.Value)) : "")
                          + (part.Around > 0 ? " · " + Loc.F("Cmp.SumAround", Loc.Num(part.Around)) : ""),
        LightEntry l => " · " + Loc.F("Cmp.SumLight", Loc.Num(l.Radius), Loc.Num(l.Brightness)),
        HazeEntry h => " · " + Loc.F("Cmp.SumHaze", Loc.Num(h.Radius), Loc.Num(h.Power)),
        SoundEntry s => " · " + (s.GameSet ?? System.IO.Path.GetFileName(s.File ?? "")) + (s.Loop ? " · " + Loc.T("Cmp.Loop") : ""),
        ScreenEntry sc => " · " + Loc.F("Cmp.SumScreen", Loc.Num(sc.Radius)),
        StrikeEntry k => " · " + k.Part,
        FlashEntry fl => " · " + Loc.F("Cmp.SumFlash", Loc.Num(fl.Full), Loc.Num(fl.Max)),
        _ => "",
    };

    public static string KindLabel(ItemKind kind) => Loc.T("Kind." + kind);

    // ---- the selected item ----

    private void Properties(ProjectFile p, IProjectItem item)
    {
        Section("Cmp.Properties");
        string id = item.Id;
        T? Get<T>(ProjectFile f) where T : class, IProjectItem => f.Find(id) as T;
        void Set<T>(string key, Action<T> change, string? merge = null) where T : class, IProjectItem =>
            Edit(f => { if (Get<T>(f) is { } x) change(x); }, Loc.Later("Undo.Cmp", Loc.Later(key)), merge == null ? null : id + "." + merge);

        TextRow("Cmp.Id", id, v =>
        {
            string nid = ProjectNames.Id(v);
            if (nid == id || p.Find(nid) != null)
                return;
            Edit(f =>
            {
                f.Find(id)!.Id = nid;
                foreach (var s in f.States)
                    foreach (var x in s.Items.Where(x => x.Ref == id))
                        x.Ref = nid;
                foreach (var k in f.Strikes.Where(k => k.Part == id))
                    k.Part = nid;
                RecipeRefs.Rename(f, id, nid);
            }, Loc.Later("Undo.CmpRename", id, nid));
            _item = nid;
            Show();
        });
        switch (item)
        {
            case PartEntry part:
                Readout("Cmp.File", part.Path);
                NumberRow("Cmp.Spin", part.Spin, -3600, 3600, v => Set<PartEntry>("Cmp.Spin", x => x.Spin = (float)v, "spin"));
                if (part.PathKeys != null)
                    Readout("Cmp.PathKeys", Loc.F("Cmp.PathKeysValue", part.PathKeys.Times.Length, Loc.Num(part.PathKeys.Times.LastOrDefault())));
                NumberRow("Cmp.Near", part.Near, 0, 5000, v => Set<PartEntry>("Cmp.Near", x => x.Near = (float)v, "near"));
                NumberRow("Cmp.Stop", part.Stop ?? 0, 0, 3600, v => Set<PartEntry>("Cmp.Stop", x => x.Stop = v > 0 ? (float)v : null, "stop"));
                NumberRow("Cmp.Around", part.Around, 0, 5000, v => Set<PartEntry>("Cmp.Around", x => x.Around = (float)v, "around"));
                if (part.Around > 0)
                {
                    NumberRow("Cmp.Swirl", part.Swirl, -1e6, 1e6, v => Set<PartEntry>("Cmp.Swirl", x => x.Swirl = (float)v, "swirl"));
                    NumberRow("Cmp.SwirlMin", part.SwirlMin, 0, 1000, v => Set<PartEntry>("Cmp.SwirlMin", x => x.SwirlMin = (float)v, "swirlmin"));
                }
                _body.Children.Add(Hint(Loc.T("Cmp.PartHint")));
                break;
            case LightEntry l:
                VecRow("Cmp.Color", l.Color, 0, 1, (i, v) => Set<LightEntry>("Cmp.Color", x => x.Color[i] = (float)v, "color" + i), true);
                NumberRow("Cmp.Brightness", l.Brightness, 0, 100000, v => Set<LightEntry>("Cmp.Brightness", x => x.Brightness = (float)v, "bright"));
                NumberRow("Cmp.Radius", l.Radius, 0.01, 1000, v => Set<LightEntry>("Cmp.Radius", x => x.Radius = (float)v, "radius"));
                VecRow("Cmp.Offset", l.Offset, -1000, 1000, (i, v) => Set<LightEntry>("Cmp.Offset", x => x.Offset[i] = (float)v, "offset" + i), false);
                NumberRow("Cmp.FlickerAmp", l.FlickerAmp, 0, 1, v => Set<LightEntry>("Cmp.FlickerAmp", x => x.FlickerAmp = (float)v, "famp"));
                NumberRow("Cmp.FlickerSpeed", l.FlickerSpeed, 0, 100, v => Set<LightEntry>("Cmp.FlickerSpeed", x => x.FlickerSpeed = (float)v, "fspeed"));
                NumberRow("Cmp.Life", l.Life, 0, 3600, v => Set<LightEntry>("Cmp.Life", x => x.Life = (float)v, "life"));
                NumberRow("Cmp.FadeOut", l.FadeOut, 0, 60, v => Set<LightEntry>("Cmp.FadeOut", x => x.FadeOut = (float)v, "fade"));
                CheckRow("Cmp.Shadow", l.Shadow, v => Set<LightEntry>("Cmp.Shadow", x => x.Shadow = v), true);
                NumberRow("Cmp.HazeRadius", l.HazeRadius, 0, 100, v => Set<LightEntry>("Cmp.HazeRadius", x => x.HazeRadius = (float)v, "hr"));
                NumberRow("Cmp.HazePower", l.HazePower, 0, 1, v => Set<LightEntry>("Cmp.HazePower", x => x.HazePower = (float)v, "hp"));
                CurveRow(l, keys => Set<LightEntry>("Cmp.Curve", x => x.Curve = keys));
                CheckRow("Cmp.CurveLoop", l.CurveLoop, v => Set<LightEntry>("Cmp.CurveLoop", x => x.CurveLoop = v), l.Curve.Count > 0);
                if (l.Anim != null)
                    Readout("Cmp.Anim", Loc.F("Cmp.AnimValue", l.Anim.Name, l.Anim.Frames.Count, Loc.Num(l.Anim.Fps)));
                break;
            case HazeEntry h:
                VecRow("Cmp.At", h.At, -1000, 1000, (i, v) => Set<HazeEntry>("Cmp.At", x => x.At[i] = (float)v, "at" + i), false);
                NumberRow("Cmp.HazeRadius", h.Radius, 0.01, 100, v => Set<HazeEntry>("Cmp.HazeRadius", x => x.Radius = (float)v, "r"));
                NumberRow("Cmp.HazePower", h.Power, 0, 1, v => Set<HazeEntry>("Cmp.HazePower", x => x.Power = (float)v, "p"));
                NumberRow("Cmp.Life", h.Life, 0, 3600, v => Set<HazeEntry>("Cmp.Life", x => x.Life = (float)v, "life"));
                NumberRow("Cmp.FadeOut", h.Fade, 0, 60, v => Set<HazeEntry>("Cmp.FadeOut", x => x.Fade = (float)v, "fade"));
                _body.Children.Add(Hint(Loc.T("Cmp.HazeHint")));
                break;
            case SoundEntry s:
                var file = new DockPanel();
                var pick = Button("Cmp.PickSound", () => _host!.PickSound(id));
                DockPanel.SetDock(pick, Dock.Right);
                file.Children.Add(pick);
                var play = Button("Cmp.PlaySound", () => _host!.PreviewSound(id), s.File != null);
                DockPanel.SetDock(play, Dock.Right);
                file.Children.Add(play);
                file.Children.Add(new TextBlock { Text = s.File ?? Loc.T("Val.None"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = s.File ?? Loc.T("Tip.Cmp.SoundFile") });
                Row("Cmp.SoundFile", file);
                TextRow("Cmp.GameSet", s.GameSet ?? "", v => Set<SoundEntry>("Cmp.GameSet", x => x.GameSet = v.Trim().Length > 0 ? v.Trim() : null));
                NumberRow("Cmp.Volume", s.Volume, 0, 20, v => Set<SoundEntry>("Cmp.Volume", x => x.Volume = (float)v, "vol"));
                NumberRow("Cmp.Range", s.Range, 1, 5000, v => Set<SoundEntry>("Cmp.Range", x => x.Range = (float)v, "range"));
                NumberRow("Cmp.Frequency", s.Frequency, 0.1, 4, v => Set<SoundEntry>("Cmp.Frequency", x => x.Frequency = (float)v, "freq"));
                CheckRow("Cmp.Loop", s.Loop, v => Set<SoundEntry>("Cmp.Loop", x => x.Loop = v), true);
                break;
            case ScreenEntry sc:
                VecRow("Cmp.TintColor", sc.Color, 0, 1, (i, v) => Set<ScreenEntry>("Cmp.TintColor", x => x.Color[i] = (float)v, "c" + i), true);
                NumberRow("Cmp.Radius", sc.Radius, 0.1, 5000, v => Set<ScreenEntry>("Cmp.Radius", x => x.Radius = (float)v, "r"));
                NumberRow("Cmp.Fade", sc.Fade, 0, 60, v => Set<ScreenEntry>("Cmp.Fade", x => x.Fade = (float)v, "f"));
                _body.Children.Add(Hint(Loc.T("Cmp.ScreenHint")));
                break;
            case FlashEntry fl:
                VecRow("Cmp.At", fl.At, -1000, 1000, (i, v) => Set<FlashEntry>("Cmp.At", x => x.At[i] = (float)v, "at" + i), false);
                NumberRow("Cmp.FlashFull", fl.Full, 0, 500, v => Set<FlashEntry>("Cmp.FlashFull", x => x.Full = (float)v, "full"));
                NumberRow("Cmp.FlashMax", fl.Max, 0.1, 1000, v => Set<FlashEntry>("Cmp.FlashMax", x => x.Max = (float)v, "max"));
                _body.Children.Add(Hint(Loc.T("Cmp.FlashHint")));
                break;
            case StrikeEntry k:
                var parts = new ComboBox { ToolTip = Loc.T("Tip.Cmp.StrikePart"), MinWidth = 120 };
                foreach (var part in p.Parts)
                    parts.Items.Add(part.Id);
                parts.SelectedItem = k.Part;
                parts.SelectionChanged += (_, _) => { if (parts.SelectedItem is string pid && pid != k.Part) Set<StrikeEntry>("Cmp.StrikePart", x => x.Part = pid); };
                Row("Cmp.StrikePart", parts);
                NumberRow("Cmp.Height", k.Height, 0, 1000, v => Set<StrikeEntry>("Cmp.Height", x => x.Height = (float)v, "h"));
                NumberRow("Cmp.RadiusLow", k.RadiusLow, 0, 1000, v => Set<StrikeEntry>("Cmp.RadiusLow", x => x.RadiusLow = (float)v, "rl"));
                NumberRow("Cmp.RadiusHigh", k.RadiusHigh, 0, 1000, v => Set<StrikeEntry>("Cmp.RadiusHigh", x => x.RadiusHigh = (float)v, "rh"));
                NumberRow("Cmp.GapMin", k.GapMin, 0.05, 3600, v => Set<StrikeEntry>("Cmp.GapMin", x => x.GapMin = (float)v, "g0"));
                NumberRow("Cmp.GapMax", k.GapMax, 0.05, 3600, v => Set<StrikeEntry>("Cmp.GapMax", x => x.GapMax = (float)v, "g1"));
                NumberRow("Cmp.StrikeTime", k.Duration, 0.01, 60, v => Set<StrikeEntry>("Cmp.StrikeTime", x => x.Duration = (float)v, "d"));
                NumberRow("Cmp.LightRadius", k.LightRadius, 0, 1000, v => Set<StrikeEntry>("Cmp.LightRadius", x => x.LightRadius = (float)v, "lr"));
                NumberRow("Cmp.Brightness", k.LightBrightness, 0, 100000, v => Set<StrikeEntry>("Cmp.Brightness", x => x.LightBrightness = (float)v, "lb"));
                break;
        }
        var actions = new WrapPanel { Margin = new Thickness(10 + LabelWidth, 4, 10, 4) };
        actions.Children.Add(Button("Cmp.RemoveItem", () =>
        {
            Edit(f =>
            {
                switch (f.Find(id))
                {
                    case PartEntry x: f.Parts.Remove(x); break;
                    case LightEntry x: f.Lights.Remove(x); break;
                    case HazeEntry x: f.Hazes.Remove(x); break;
                    case SoundEntry x: f.Sounds.Remove(x); break;
                    case ScreenEntry x: f.Screens.Remove(x); break;
                    case StrikeEntry x: f.Strikes.Remove(x); break;
                    case FlashEntry x: f.Flashes.Remove(x); break;
                }
                foreach (var s in f.States)
                    s.Items.RemoveAll(x => x.Ref == id);
            }, Loc.Later("Undo.CmpRemoveItem", id));
            _item = null;
            Show();
        }));
        _body.Children.Add(actions);
    }

    // The brightness curve as "seconds multiplier" pairs, with its shape drawn beside the field.
    private void CurveRow(LightEntry l, Action<List<float[]>> set)
    {
        var box = new TextBox
        {
            Style = Find("Box.Num"), Text = string.Join("; ", l.Curve.Select(k => Loc.Num(k[0]) + " " + Loc.Num(k[1]))), ToolTip = Loc.T("Tip.Cmp.Curve"),
        };
        void Commit()
        {
            var keys = new List<float[]>();
            foreach (var pair in box.Text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var nums = pair.Replace(',', '.').Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (nums.Length != 2 || !float.TryParse(nums[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float t)
                    || !float.TryParse(nums[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float k) || t < 0 || k < 0)
                {
                    _host?.Toast(Loc.T("Toast.CmpCurveBad"), "warn");
                    return;
                }
                keys.Add(new[] { t, k });
            }
            keys.Sort((a, b) => a[0].CompareTo(b[0]));
            if (!keys.Select(k => k[0] + ":" + k[1]).SequenceEqual(l.Curve.Select(k => k[0] + ":" + k[1])))
                set(keys);
        }
        box.LostKeyboardFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
        var line = new DockPanel();
        var thumb = CurveThumb(l.Curve);
        DockPanel.SetDock(thumb, Dock.Right);
        line.Children.Add(thumb);
        line.Children.Add(box);
        Row("Cmp.Curve", line);
    }

    private static FrameworkElement CurveThumb(List<float[]> keys)
    {
        var canvas = new Canvas { Width = 54, Height = 20, Margin = new Thickness(4, 0, 0, 0), Background = Brush("B.BgInput"), ClipToBounds = true };
        if (keys.Count > 0)
        {
            float end = Math.Max(keys[^1][0], 1e-3f), top = Math.Max(keys.Max(k => k[1]), 1e-3f);
            var line = new Polyline { Stroke = Brush("B.Accent"), StrokeThickness = 1.2 };
            foreach (var k in keys)
                line.Points.Add(new Point(2 + k[0] / end * 50, 18 - k[1] / top * 16));
            canvas.Children.Add(line);
        }
        return canvas;
    }

    // ---- anomaly ----

    private void Anomaly(ProjectFile p)
    {
        Section("Cmp.Anomaly");
        var arch = new ComboBox { ToolTip = Loc.T("Tip.Cmp.Archetype"), MinWidth = 140 };
        foreach (var a in AnomalySettings.Archetypes)
            arch.Items.Add(new ComboBoxItem { Content = Loc.T("Arch." + (a.Length == 0 ? "none" : a)), Tag = a });
        arch.SelectedIndex = Math.Max(0, Array.IndexOf(AnomalySettings.Archetypes, p.Anomaly.Archetype));
        arch.SelectionChanged += (_, _) =>
        {
            if (arch.SelectedItem is ComboBoxItem { Tag: string a } && a != p.Anomaly.Archetype)
                Edit(f => f.Anomaly.Archetype = a, Loc.Later("Undo.Cmp", Loc.Later("Cmp.Archetype")));
        };
        Row("Cmp.Archetype", arch);
        var grid = new Grid { Margin = new Thickness(10, 2, 10, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LabelWidth) });
        for (int c = 0; c < 3; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition());
        string[] heads = { "Cmp.ShellR", "Cmp.ShellYMin", "Cmp.ShellYMax" };
        for (int c = 0; c < 3; c++)
        {
            var h = new TextBlock { Text = Loc.T(heads[c]), Style = Find("T.Dim"), FontSize = 11, ToolTip = Loc.T("Tip." + heads[c]), Margin = new Thickness(4, 0, 0, 2) };
            Grid.SetColumn(h, c + 1);
            grid.Children.Add(h);
        }
        int r = 1;
        foreach (var name in AnomalySettings.ShellNames)
        {
            grid.RowDefinitions.Add(new RowDefinition());
            var shell = p.Anomaly.Shells.GetValueOrDefault(name) ?? new Shell();
            var label = new TextBlock { Text = Loc.T("Shell." + name), VerticalAlignment = VerticalAlignment.Center, ToolTip = Loc.T("Tip.Shell." + name) };
            Grid.SetRow(label, r);
            grid.Children.Add(label);
            float[] values = { shell.R, shell.YMin, shell.YMax };
            for (int c = 0; c < 3; c++)
            {
                int cc = c;
                string n = name;
                var box = NumberBox(values[c], c == 0 ? 0 : -100, c == 0 ? 500 : 500, v => Edit(f =>
                {
                    var s = f.Anomaly.Shells.GetValueOrDefault(n) ?? new Shell();
                    if (cc == 0) s.R = (float)v; else if (cc == 1) s.YMin = (float)v; else s.YMax = (float)v;
                    f.Anomaly.Shells[n] = s;
                }, Loc.Later("Undo.Cmp", Loc.Later("Shell." + n)), $"shell.{n}.{cc}"));
                box.Margin = new Thickness(4, 1, 0, 1);
                box.ToolTip = Loc.T("Tip.Shell." + n) + "\n" + Loc.T("Tip." + heads[c]);
                Grid.SetRow(box, r);
                Grid.SetColumn(box, c + 1);
                grid.Children.Add(box);
            }
            r++;
        }
        _body.Children.Add(grid);
        NumberRow("Cmp.Tick", p.Anomaly.Tick, 0, 60, v => Edit(f => f.Anomaly.Tick = (float)v, Loc.Later("Undo.Cmp", Loc.Later("Cmp.Tick")), "tick"));
        NumberRow("Cmp.HitDelay", p.Anomaly.HitDelay, 0, 60, v => Edit(f => f.Anomaly.HitDelay = (float)v, Loc.Later("Undo.Cmp", Loc.Later("Cmp.HitDelay")), "hitdelay"));
        var timing = StateCycle.Timing(p).Where(t => t.Value > 0).Select(t => $"{t.Key} {Loc.Num(t.Value)}");
        Readout("Cmp.Timing", string.Join(" · ", timing.DefaultIfEmpty(Loc.T("Val.None"))));
    }

    // ---- building blocks ----

    private void Section(string key)
    {
        var head = new Border
        {
            Background = Brush("B.BgRaised"), BorderBrush = Brush("B.LineSubtle"), BorderThickness = new Thickness(0, 1, 0, 1), Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(0, 6, 0, 2), ToolTip = Loc.T("Tip." + key),
            Child = new TextBlock { Text = Loc.T(key), Style = Find("T.Hi"), FontSize = 11.5 },
        };
        _body.Children.Add(head);
    }

    private void SubHeader(string key) =>
        _body.Children.Add(new TextBlock { Text = Loc.T(key), Style = Find("T.Dim"), FontSize = 11, Margin = new Thickness(10, 4, 10, 2), ToolTip = Loc.T("Tip." + key) });

    private static TextBlock Hint(string text) =>
        new() { Text = text, Style = Find("T.Dim"), FontSize = 10.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 2, 10, 4) };

    private void Row(string key, FrameworkElement editor)
    {
        var row = new DockPanel { Margin = new Thickness(10, 1, 10, 1) };
        var label = new TextBlock
        {
            Text = Loc.T(key), Width = LabelWidth, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = Loc.T("Tip." + key), Foreground = Brush("B.Text"),
        };
        DockPanel.SetDock(label, Dock.Left);
        row.Children.Add(label);
        if (editor.ToolTip == null)
            editor.ToolTip = Loc.T("Tip." + key);
        row.Children.Add(editor);
        _body.Children.Add(row);
    }

    private void Readout(string key, string value) =>
        Row(key, new TextBlock { Text = value, Style = Find("T.Dim"), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = Loc.T("Tip." + key) + "\n" + value });

    private void TextRow(string key, string value, Action<string> commit)
    {
        var box = new TextBox { Text = value, ToolTip = Loc.T("Tip." + key) };
        void Done()
        {
            if (box.Text != value)
                commit(box.Text);
        }
        box.LostKeyboardFocus += (_, _) => Done();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Done(); };
        Row(key, box);
    }

    private void CheckRow(string key, bool value, Action<bool> commit, bool enabled)
    {
        var box = new CheckBox { IsChecked = value, IsEnabled = enabled, VerticalAlignment = VerticalAlignment.Center, ToolTip = Loc.T("Tip." + key) };
        box.Click += (_, _) => commit(box.IsChecked == true);
        Row(key, box);
    }

    private void NumberRow(string key, double value, double min, double max, Action<double> commit)
    {
        var box = NumberBox(value, min, max, commit);
        box.ToolTip = Loc.T("Tip." + key) + "\n" + Loc.F("Tip.Cmp.Range", Loc.Num(min), Loc.Num(max));
        Row(key, box);
    }

    // x y z (or r g b) in one row with axis colours; a colour row shows its swatch.
    private void VecRow(string key, float[] v, double min, double max, Action<int, double> commit, bool color)
    {
        var grid = new Grid();
        string[] axes = color ? new[] { "B.CurveR", "B.CurveG", "B.CurveB" } : new[] { "B.AxisX", "B.AxisY", "B.AxisZ" };
        for (int i = 0; i < 3; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            int axis = i;
            var box = NumberBox(v[i], min, max, x => commit(axis, x));
            box.BorderBrush = Brush(axes[i]);
            box.Margin = new Thickness(i == 0 ? 0 : 3, 0, 0, 0);
            box.ToolTip = Loc.T("Tip." + key) + "\n" + Loc.T((color ? "Axis.C" : "Axis.") + i) + "\n" + Loc.F("Tip.Cmp.Range", Loc.Num(min), Loc.Num(max));
            Grid.SetColumn(box, i);
            grid.Children.Add(box);
        }
        if (color)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var c = Color.FromRgb(Srgb(v[0]), Srgb(v[1]), Srgb(v[2]));
            var swatch = new Border { Width = 20, Height = 18, Margin = new Thickness(4, 0, 0, 0), Background = new SolidColorBrush(c), BorderBrush = Brush("B.LineStrong"), BorderThickness = new Thickness(1), ToolTip = Loc.T("Tip.Cmp.Swatch") };
            Grid.SetColumn(swatch, 3);
            grid.Children.Add(swatch);
        }
        Row(key, grid);
    }

    private static byte Srgb(float linear)
    {
        double l = Math.Clamp(linear, 0, 1);
        double s = l <= 0.0031308 ? l * 12.92 : 1.055 * Math.Pow(l, 1 / 2.4) - 0.055;
        return (byte)Math.Round(s * 255);
    }

    // A number field that takes expressions (Expr) and clamps out-of-range input with a note instead of refusing it.
    private TextBox NumberBox(double value, double min, double max, Action<double> commit)
    {
        var box = new TextBox { Style = Find("Box.Num"), Text = Loc.Num(value), MinWidth = 40 };
        void Done()
        {
            if (!Expr.TryApply(box.Text, value, out double v))
            {
                box.Text = Loc.Num(value);
                return;
            }
            double c = Math.Clamp(v, min, max);
            if (c != v)
                _host?.Toast(Loc.F("Toast.CmpClamped", Loc.Num(v), Loc.Num(c)), "warn");
            if (Math.Abs(c - value) > 1e-9)
                commit(c);
            else
                box.Text = Loc.Num(value);
        }
        box.LostKeyboardFocus += (_, _) => Done();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Done(); };
        return box;
    }

    private Button Button(string key, Action run, bool enabled = true)
    {
        var b = new Button { Content = Loc.T(key), ToolTip = Loc.T("Tip." + key), Margin = new Thickness(0, 0, 4, 4), IsEnabled = enabled, Tag = key };
        b.Click += (_, _) => run();
        return b;
    }

    private static Button Tiny(string icon, string tip, Action run)
    {
        var b = new Button { Style = Find("Btn.Tiny"), Width = 18, Height = 18, ToolTip = tip, Margin = new Thickness(4, 0, 0, 0) };
        b.Content = new System.Windows.Shapes.Path { Data = (Geometry)Application.Current.FindResource(icon), Style = Find("Ico.Small"), Width = 8, Height = 8 };
        b.Click += (_, _) => run();
        return b;
    }

    private static Style Find(string key) => (Style)Application.Current.FindResource(key);

    private static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);
}
