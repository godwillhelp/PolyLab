using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Pfx.App.Core;
using Pfx.App.Model;
using Pfx.App.Recipes;
using Pfx.Core.Model;

namespace Pfx.App.Views;

public interface IRecipesHost
{
    CompositionDoc? RecipeProject { get; }
    string? CurrentState { get; }
    void AddRecipe(Recipe recipe, OwnRecipe? own = null, IReadOnlyList<RecipeTarget>? targets = null);
    void SetRecipeParam(string nodeId, string key, string value);
    void ToggleRecipe(string nodeId);
    void RemoveRecipe(string nodeId);
    RecipePreview DryRun(Recipe recipe, OwnRecipe? own);
    void SelectRecipeTarget(RecipeOp op);
    string? PickRecipeFile(ParamKind kind);
    IReadOnlyList<(string Path, OwnRecipe Recipe)> OwnRecipes();
    void SaveOwnRecipe(string name, bool items, IReadOnlyList<(string Emitter, string Key)> exposed);
    IReadOnlyList<(string Emitter, string Key, string Value)> ExposableKeys();
    void DeleteOwnRecipe(string path);
    void Toast(string text, string kind);
}

// The ready solutions panel (board 5's left column, spec 3.10): the project's nodes on top (switch for before/after,
// values that apply again on every change, what each made, remove), the catalog below grouped by what it does, with
// the status of each recipe and, for the picked one, the facts it rests on, what it needs outside the .ptc and what it
// would change for the current selection (a dry run on copies). Own recipes are saved from the selection at the end.
public sealed class RecipesView : UserControl
{
    private const double LabelWidth = 118;
    private IRecipesHost? _host;
    private string? _node;
    private string? _picked;
    private string? _pickedOwn;
    private string _filter = "all";
    private bool _saving;
    private readonly StackPanel _body = new() { Margin = new Thickness(0, 0, 0, 8) };

    private static readonly (string Group, string[] Ids)[] Groups =
    {
        ("motion", new[] { "swirl", "one_body", "single_column", "strands", "funnel", "orbit_turn", "around" }),
        ("look", new[] { "add_light", "haze", "flash", "tint", "strikes", "ground_glow" }),
        ("parts", new[] { "near_only", "stop_split", "spawner", "strip_gate", "add_sound" }),
        ("material", new[] { "soft_ground", "palette_match", "sort_inside", "fix_order", "make_lit", "make_volumetric" }),
        ("generators", new[] { "gen_tornado", "gen_arc", "gen_burst", "gen_fog", "gen_sparks", "gen_embers", "gen_flash", "gen_vortex", "gen_heat", "gen_glow" }),
    };

    public RecipesView()
    {
        Background = Brush("B.BgPanel");
        Content = new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    public void Init(IRecipesHost host)
    {
        _host = host;
        Show();
    }

    public string? SelectedNode => _node;

    public void SelectNode(string? id)
    {
        _node = id;
        Show();
    }

    // The catalog row picked (its details and dry run shown), by recipe id.
    public void Pick(string? id)
    {
        _picked = id;
        _pickedOwn = null;
        Show();
    }

    public void Show()
    {
        _body.Children.Clear();
        if (_host == null)
            return;
        var project = _host.RecipeProject?.File;
        Nodes(project);
        Catalog();
        Own();
    }

    // ---- nodes of the project ----

    private void Nodes(ProjectFile? p)
    {
        Section("Rcp.InProject", p?.Recipes.Count ?? 0);
        if (p == null || p.Recipes.Count == 0)
        {
            _body.Children.Add(Hint(Loc.T("Rcp.NoNodes")));
            return;
        }
        if (_node != null && p.Recipes.All(r => r.Id != _node))
            _node = null;
        foreach (var n in p.Recipes)
        {
            var node = n;
            var recipe = RecipeRunner.Find(node);
            string name = recipe != null ? MainWindow.RecipeName(recipe, node.Own) : node.Kind;
            var row = new DockPanel { Margin = new Thickness(6, 1, 6, 1), Background = Brush(node.Id == _node ? "B.BgSelected" : "B.Transparent"), Cursor = Cursors.Hand };
            var remove = Tiny("Ic.Close", Loc.T("Tip.Rcp.Remove"), () => _host!.RemoveRecipe(node.Id));
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            var on = new ToggleButton
            {
                Style = Find("Btn.Seg"), IsChecked = node.On, Content = Loc.T(node.On ? "Rcp.After" : "Rcp.Before"), Margin = new Thickness(4, 0, 0, 0),
                ToolTip = Loc.T("Tip.Rcp.Toggle"), MinWidth = 54,
            };
            on.Click += (_, _) => _host!.ToggleRecipe(node.Id);
            DockPanel.SetDock(on, Dock.Right);
            row.Children.Add(on);
            if (recipe != null)
            {
                var chip = StatusChip(recipe.Status);
                DockPanel.SetDock(chip, Dock.Right);
                row.Children.Add(chip);
            }
            int made = node.Log.Count;
            row.Children.Add(new TextBlock
            {
                Text = name + "  ·  " + node.Id, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 2, 4, 2),
                Foreground = Brush(node.On ? "B.TextHi" : "B.TextOff"), ToolTip = Loc.F("Tip.Rcp.Node", name, node.Id, made),
            });
            row.MouseLeftButtonUp += (_, e) =>
            {
                if (e.OriginalSource is DependencyObject d && Up<ButtonBase>(d) != null)
                    return;
                SelectNode(node.Id == _node ? null : node.Id);
            };
            _body.Children.Add(row);
            if (node.Id == _node && recipe != null)
                Editor(p, node, recipe);
        }
    }

    private void Editor(ProjectFile p, RecipeNode node, Recipe recipe)
    {
        var box = new StackPanel { Margin = new Thickness(10, 2, 6, 8) };
        _body.Children.Add(new Border { BorderBrush = Brush("B.Accent"), BorderThickness = new Thickness(2, 0, 0, 0), Margin = new Thickness(6, 0, 6, 4), Child = box });
        void Add(UIElement e) => box.Children.Add(e);
        Add(Hint(Loc.T("Rcp." + recipe.Id + ".What"), 0));
        Add(Facts(recipe));
        if (recipe.IsGenerator)
            Add(Hint(Loc.T("Rcp.RegenerateHint"), 0));
        if (node.Targets.Count > 0)
            Add(Readout("Rcp.Targets", string.Join(", ", node.Targets.Select(t => t.Emitter != null ? t.Part + " · " + t.Emitter : t.Part))));
        foreach (var prm in recipe.Params)
            Add(ParamRow(p, node, recipe, prm));
        Add(new TextBlock { Text = Loc.F("Rcp.Made", node.Log.Count), Style = Find("T.Dim"), FontSize = 11, Margin = new Thickness(0, 6, 0, 2), ToolTip = Loc.T("Tip.Rcp.Made") });
        if (!node.On)
            Add(Hint(Loc.T("Rcp.OffHint"), 0));
        foreach (var op in node.Log.Take(40))
        {
            var o = op;
            var line = new TextBlock
            {
                Text = "· " + Describe(op), TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11, Cursor = Cursors.Hand, Foreground = Brush("B.Text"),
                ToolTip = Loc.T("Tip.Rcp.MadeRow"), Margin = new Thickness(0, 0, 0, 1),
            };
            line.MouseLeftButtonUp += (_, _) => _host!.SelectRecipeTarget(o);
            Add(line);
        }
        if (node.Log.Count > 40)
            Add(Hint(Loc.F("Rcp.More", node.Log.Count - 40), 0));
    }

    private FrameworkElement ParamRow(ProjectFile p, RecipeNode node, Recipe recipe, RecipeParam prm)
    {
        string label = node.Own != null && prm.Kind != ParamKind.State ? OwnRecipeRun.Label(node.Own, prm.Key) : Loc.T(ParamKey(recipe, prm));
        string tip = node.Own != null && prm.Kind != ParamKind.State ? Loc.T("Tip.Rcp.OwnParam") : Loc.T("Tip." + ParamKey(recipe, prm));
        string value = node.Params.TryGetValue(prm.Key, out var v) ? v : prm.Default;
        void Commit(string text)
        {
            if (text != value)
                _host!.SetRecipeParam(node.Id, prm.Key, text);
        }
        FrameworkElement editor;
        switch (prm.Kind)
        {
            case ParamKind.Number:
            {
                double now = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
                var tb = new TextBox { Style = Find("Box.Num"), Text = Loc.Num(now), ToolTip = tip + "\n" + Loc.F("Tip.Cmp.Range", Loc.Num(prm.Min), Loc.Num(prm.Max)) };
                void Done()
                {
                    if (!Expr.TryApply(tb.Text, now, out double x))
                    {
                        tb.Text = Loc.Num(now);
                        return;
                    }
                    double c = Math.Clamp(x, prm.Min, prm.Max);
                    if (c != x)
                        _host!.Toast(Loc.F("Toast.CmpClamped", Loc.Num(x), Loc.Num(c)), "warn");
                    if (Math.Abs(c - now) > 1e-12)
                        Commit(c.ToString("R", CultureInfo.InvariantCulture));
                    else
                        tb.Text = Loc.Num(now);
                }
                tb.LostKeyboardFocus += (_, _) => Done();
                tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) Done(); };
                editor = tb;
                break;
            }
            case ParamKind.Flag:
            {
                var cb = new CheckBox { IsChecked = value == "1", VerticalAlignment = VerticalAlignment.Center, ToolTip = tip };
                cb.Click += (_, _) => Commit(cb.IsChecked == true ? "1" : "0");
                editor = cb;
                break;
            }
            case ParamKind.State:
            {
                var combo = new ComboBox { ToolTip = tip, MinWidth = 100 };
                foreach (var s in p.States)
                    combo.Items.Add(new ComboBoxItem { Content = ComposeView.StateLabel(s.Name), Tag = s.Name });
                combo.SelectedIndex = Math.Max(0, p.States.FindIndex(s => s.Name == value));
                combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is ComboBoxItem { Tag: string name }) Commit(name); };
                editor = combo;
                break;
            }
            default:
            {
                var tb = new TextBox { Text = value, ToolTip = tip + (prm.Kind is ParamKind.File or ParamKind.Material ? "\n" + value : "") };
                tb.LostKeyboardFocus += (_, _) => Commit(tb.Text.Trim());
                tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(tb.Text.Trim()); };
                if (prm.Kind is ParamKind.File or ParamKind.Material)
                {
                    var line = new DockPanel();
                    var pick = Tiny("Ic.Folder", Loc.T(prm.Kind == ParamKind.Material ? "Tip.Rcp.PickMaterial" : "Tip.Rcp.PickFile"), () =>
                    {
                        if (_host!.PickRecipeFile(prm.Kind) is { } path)
                            Commit(path);
                    });
                    DockPanel.SetDock(pick, Dock.Right);
                    line.Children.Add(pick);
                    line.Children.Add(tb);
                    editor = line;
                }
                else
                {
                    editor = tb;
                }
                break;
            }
        }
        return Row(label, tip, editor);
    }

    private static string ParamKey(Recipe recipe, RecipeParam prm) => prm.Kind == ParamKind.State ? "Rcp.Param.state" : "Rcp." + recipe.Id + "." + prm.Key;

    // One line per change of the log, in plain words.
    public static string Describe(RecipeOp op) => op.Op switch
    {
        RecipeOp.EmitterAdd => Loc.F("Rcp.Op.EmitterAdd", op.Emitter, op.Part),
        RecipeOp.EmitterRemove => Loc.F("Rcp.Op.EmitterRemove", op.Emitter, op.Part),
        RecipeOp.Key => Loc.F(op.After == null ? "Rcp.Op.KeyRemove" : "Rcp.Op.Key", Loc.TryT("F." + op.KeyName) ?? op.KeyName, op.Emitter ?? op.Part),
        RecipeOp.PartText => Loc.F("Rcp.Op.PartText", op.Part),
        RecipeOp.ItemAdd => Loc.F("Rcp.Op.ItemAdd", op.Item),
        RecipeOp.ItemRemove => Loc.F("Rcp.Op.ItemRemove", op.Item),
        RecipeOp.Field => Loc.F("Rcp.Op.Field", Loc.T("Rcp.Field." + op.KeyName), op.Item, op.After),
        RecipeOp.StateAdd => Loc.F("Rcp.Op.StateAdd", op.Item, ComposeView.StateLabel(op.State ?? "")),
        RecipeOp.StateRemove => Loc.F("Rcp.Op.StateRemove", op.Item, ComposeView.StateLabel(op.State ?? "")),
        RecipeOp.StateNew => Loc.F("Rcp.Op.StateNew", ComposeView.StateLabel(op.State ?? "")),
        RecipeOp.FileMade => Loc.F("Rcp.Op.File", Path.GetFileName(op.Before ?? "")),
        _ => op.Op,
    };

    // ---- catalog ----

    private void Catalog()
    {
        Section("Rcp.Catalog", RecipeCatalog.All.Count);
        var chips = new WrapPanel { Margin = new Thickness(10, 2, 10, 4) };
        foreach (var f in new[] { "all", "proven", "generators" })
        {
            string key = f;
            var chip = new ToggleButton { Style = Find("Btn.Seg"), IsChecked = _filter == f, Content = Loc.T("Rcp.Filter." + f), ToolTip = Loc.T("Tip.Rcp.Filter." + f), Margin = new Thickness(0, 0, 4, 2) };
            chip.Click += (_, _) => { _filter = key; Show(); };
            chips.Children.Add(chip);
        }
        _body.Children.Add(chips);
        foreach (var (group, ids) in Groups)
        {
            var list = ids.Select(RecipeCatalog.Find).OfType<Recipe>()
                .Where(r => _filter == "all" || (_filter == "proven" ? r.Status == RecipeStatus.Proven : r.IsGenerator)).ToList();
            if (list.Count == 0)
                continue;
            _body.Children.Add(new TextBlock { Text = Loc.T("Rcp.Group." + group), Style = Find("T.Dim"), FontSize = 11, Margin = new Thickness(10, 6, 10, 2), ToolTip = Loc.T("Tip.Rcp.Group." + group) });
            foreach (var r in list)
                CatalogRow(r, null, r.Id == _picked);
        }
    }

    private void CatalogRow(Recipe r, OwnRecipe? own, bool picked)
    {
        string name = MainWindow.RecipeName(r, own);
        var row = new DockPanel { Margin = new Thickness(6, 0, 6, 0), Background = Brush(picked ? "B.BgSelected" : "B.Transparent"), Cursor = Cursors.Hand };
        var add = new Button { Style = Find("Btn.Accent"), Content = Loc.T("Rcp.Add"), Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(4, 2, 0, 2), ToolTip = Loc.T("Tip.Rcp.Add." + r.Scope) };
        add.Click += (_, _) => _host!.AddRecipe(r, own);
        DockPanel.SetDock(add, Dock.Right);
        row.Children.Add(add);
        var chip = StatusChip(r.Status);
        DockPanel.SetDock(chip, Dock.Right);
        row.Children.Add(chip);
        var texts = new StackPanel { Margin = new Thickness(4, 3, 4, 3) };
        texts.Children.Add(new TextBlock { Text = name, Foreground = Brush("B.TextHi"), TextTrimming = TextTrimming.CharacterEllipsis });
        texts.Children.Add(new TextBlock
        {
            Text = own != null ? (own.Note.Length > 0 ? own.Note : Loc.F("Rcp.OwnWhat", own.Emitters.Count)) : Loc.T("Rcp." + r.Id + ".What"),
            Style = Find("T.Dim"), FontSize = 10.5, TextTrimming = TextTrimming.CharacterEllipsis,
        });
        row.Children.Add(texts);
        row.ToolTip = name + "\n" + (own == null ? Loc.T("Rcp." + r.Id + ".What") + "\n" + Loc.T("Rcp." + r.Id + ".Source") : Loc.T("Tip.Rcp.Own")) + "\n" + Loc.T("Tip.Rcp.Pick");
        row.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && Up<ButtonBase>(d) != null)
                return;
            if (own != null)
            {
                _pickedOwn = picked ? null : own.Name;
                _picked = null;
            }
            else
            {
                _picked = picked ? null : r.Id;
                _pickedOwn = null;
            }
            Show();
        };
        _body.Children.Add(row);
        if (picked)
            Details(r, own);
    }

    // The picked recipe: its sources, what it needs and what it would change now (dry run on copies).
    private void Details(Recipe r, OwnRecipe? own)
    {
        var box = new StackPanel { Margin = new Thickness(10, 0, 6, 6) };
        _body.Children.Add(new Border { BorderBrush = Brush("B.LineStrong"), BorderThickness = new Thickness(2, 0, 0, 0), Margin = new Thickness(6, 0, 6, 4), Child = box });
        if (own == null)
            box.Children.Add(Facts(r));
        var preview = _host!.DryRun(r, own);
        if (preview.Why != null)
        {
            box.Children.Add(new TextBlock { Text = Loc.T(preview.Why), Foreground = Brush("B.Warn"), TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 2, 0, 2) });
            return;
        }
        box.Children.Add(new TextBlock { Text = Loc.F("Rcp.WillChange", preview.Ops.Count), Style = Find("T.Dim"), FontSize = 11, Margin = new Thickness(0, 4, 0, 1), ToolTip = Loc.T("Tip.Rcp.WillChange") });
        foreach (var op in preview.Ops.Take(12))
            box.Children.Add(new TextBlock { Text = "· " + Describe(op), FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = Describe(op) });
        if (preview.Ops.Count > 12)
            box.Children.Add(Hint(Loc.F("Rcp.More", preview.Ops.Count - 12), 0));
    }

    private FrameworkElement Facts(Recipe r)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = Loc.T("Rcp.Status." + r.Status) + " · " + Loc.T("Rcp." + r.Id + ".Source"), TextWrapping = TextWrapping.Wrap, FontSize = 10.5,
            Foreground = Brush(StatusBrush(r.Status)), Margin = new Thickness(0, 2, 0, 2), ToolTip = Loc.T("Tip.Rcp.Status." + r.Status),
        });
        if (r.Needs.Count > 0)
            panel.Children.Add(new TextBlock
            {
                Text = Loc.T("Rcp.Needs") + " " + string.Join(", ", r.Needs.Select(n => Loc.T("Rcp.Need." + n))), Style = Find("T.Dim"), FontSize = 10.5,
                TextWrapping = TextWrapping.Wrap, ToolTip = Loc.T("Tip.Rcp.Needs"),
            });
        return panel;
    }

    // ---- own recipes ----

    private void Own()
    {
        var own = _host!.OwnRecipes();
        Section("Rcp.Own", own.Count);
        foreach (var (path, recipe) in own)
        {
            CatalogRow(new OwnRecipeRun(recipe), recipe, _pickedOwn == recipe.Name);
            if (_pickedOwn == recipe.Name)
            {
                var del = Button("Rcp.OwnDelete", () => { _host!.DeleteOwnRecipe(path); _pickedOwn = null; Show(); });
                del.Margin = new Thickness(16, 0, 0, 6);
                del.HorizontalAlignment = HorizontalAlignment.Left;
                _body.Children.Add(del);
            }
        }
        if (!_saving)
        {
            var start = Button("Rcp.OwnSave", () => { _saving = true; Show(); });
            start.Margin = new Thickness(10, 4, 10, 4);
            start.HorizontalAlignment = HorizontalAlignment.Left;
            _body.Children.Add(start);
            return;
        }
        var keys = _host.ExposableKeys();
        if (keys.Select(k => k.Emitter).Distinct().Count() == 0)
        {
            _body.Children.Add(Hint(Loc.T("Rcp.OwnNoSelection")));
            var cancel = Button("Rcp.OwnCancel", () => { _saving = false; Show(); });
            cancel.Margin = new Thickness(10, 0, 10, 4);
            cancel.HorizontalAlignment = HorizontalAlignment.Left;
            _body.Children.Add(cancel);
            return;
        }
        var nameBox = new TextBox { Text = Loc.T("Rcp.OwnDefaultName"), ToolTip = Loc.T("Tip.Rcp.OwnName") };
        _body.Children.Add(Row(Loc.T("Rcp.OwnName"), Loc.T("Tip.Rcp.OwnName"), nameBox));
        var items = new CheckBox { Content = Loc.T("Rcp.OwnItems"), ToolTip = Loc.T("Tip.Rcp.OwnItems"), Margin = new Thickness(10, 2, 10, 2) };
        _body.Children.Add(items);
        _body.Children.Add(new TextBlock { Text = Loc.T("Rcp.OwnExpose"), Style = Find("T.Dim"), FontSize = 11, Margin = new Thickness(10, 4, 10, 2), ToolTip = Loc.T("Tip.Rcp.OwnExpose") });
        var checks = new List<(CheckBox Box, string Emitter, string Key)>();
        var wrap = new WrapPanel { Margin = new Thickness(10, 0, 10, 4) };
        foreach (var (emitter, key, value) in keys)
        {
            var cb = new CheckBox { Content = emitter + " · " + (Loc.TryT("F." + key) ?? key) + " = " + value, Margin = new Thickness(0, 0, 10, 2), ToolTip = Loc.T("Tip.Rcp.OwnExposeKey") + "\n" + key };
            checks.Add((cb, emitter, key));
            wrap.Children.Add(cb);
        }
        _body.Children.Add(wrap);
        var buttons = new WrapPanel { Margin = new Thickness(10, 2, 10, 6) };
        buttons.Children.Add(Button("Rcp.OwnSaveNow", () =>
        {
            _host.SaveOwnRecipe(nameBox.Text.Trim(), items.IsChecked == true, checks.Where(c => c.Box.IsChecked == true).Select(c => (c.Emitter, c.Key)).ToList());
            _saving = false;
            Show();
        }));
        buttons.Children.Add(Button("Rcp.OwnCancel", () => { _saving = false; Show(); }));
        _body.Children.Add(buttons);
    }

    // ---- building blocks ----

    public static string StatusBrush(RecipeStatus s) => s switch
    {
        RecipeStatus.Proven => "B.Ok",
        RecipeStatus.Unverified => "B.Warn",
        _ => "B.Err",
    };

    private static Border StatusChip(RecipeStatus s) => new()
    {
        BorderBrush = Brush(StatusBrush(s)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 0, 5, 1),
        Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = Loc.T("Tip.Rcp.Status." + s),
        Child = new TextBlock { Text = Loc.T("Rcp.Status." + s), FontSize = 10, Foreground = Brush(StatusBrush(s)) },
    };

    private void Section(string key, int count)
    {
        _body.Children.Add(new Border
        {
            Background = Brush("B.BgRaised"), BorderBrush = Brush("B.LineSubtle"), BorderThickness = new Thickness(0, 1, 0, 1), Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(0, 6, 0, 2), ToolTip = Loc.T("Tip." + key),
            Child = new TextBlock { Text = Loc.T(key) + (count > 0 ? "  " + count : ""), Style = Find("T.Hi"), FontSize = 11.5 },
        });
    }

    private static TextBlock Hint(string text, double left = 10) =>
        new() { Text = text, Style = Find("T.Dim"), FontSize = 10.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(left, 2, 10, 4) };

    private static FrameworkElement Row(string label, string tip, FrameworkElement editor)
    {
        var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
        var l = new TextBlock { Text = label, Width = LabelWidth, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = tip, Foreground = Brush("B.Text") };
        DockPanel.SetDock(l, Dock.Left);
        row.Children.Add(l);
        editor.ToolTip ??= tip;
        row.Children.Add(editor);
        return row;
    }

    private static FrameworkElement Readout(string key, string value) =>
        Row(Loc.T(key), Loc.T("Tip." + key), new TextBlock { Text = value, Style = Find("T.Dim"), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = value });

    private static Button Button(string key, Action run)
    {
        var b = new Button { Content = Loc.T(key), ToolTip = Loc.T("Tip." + key), Margin = new Thickness(0, 0, 4, 4) };
        b.Click += (_, _) => run();
        return b;
    }

    private static Button Tiny(string icon, string tip, Action run)
    {
        var b = new Button { Style = Find("Btn.Tiny"), Width = 20, Height = 20, ToolTip = tip, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        b.Content = new System.Windows.Shapes.Path { Data = (Geometry)Application.Current.FindResource(icon), Style = Find("Ico.Small"), Width = 9, Height = 9 };
        b.Click += (_, _) => run();
        return b;
    }

    private static T? Up<T>(DependencyObject? d) where T : class
    {
        while (d != null && d is not T)
            d = VisualTreeHelper.GetParent(d);
        return d as T;
    }

    private static Style Find(string key) => (Style)Application.Current.FindResource(key);

    private static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);
}
