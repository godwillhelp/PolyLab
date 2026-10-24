using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Pfx.App.Core;
using Pfx.App.Lint;
using Pfx.App.Textures;
using Pfx.Render;

namespace Pfx.App.Views;

// One emitter layer of the shown frame in the renderer's back-to-front order (copy 0).
public sealed record DrawRow(int Layer, string Part, string Emitter, float Key, bool MaySwap, bool Additive, bool Sorted, int Effect, int Index, double Fill);

public interface ICheckHost
{
    LintReport Lint { get; }
    BudgetReport? Budget { get; }
    bool BudgetBusy { get; }
    DrawView DrawMode { get; set; }
    IReadOnlyList<DrawRow> DrawOrder(out double fill);
    void Jump(LintIssue issue);
    void SelectDraw(DrawRow row);
    void BudgetWanted();
    void ApplyLintRecipe(IReadOnlyList<LintIssue> issues);
}

// The check panel (spec 3.11, board 5): the lint list (click = the field), the draw order of the shown frame with the
// viewport's layers / overdraw views, and the budget of the part and of the whole project.
public sealed class CheckView : UserControl
{
    private ICheckHost? _host;
    private readonly StackPanel _lint = new();
    private readonly StackPanel _order = new();
    private readonly StackPanel _budget = new();
    private readonly TextBlock _lintHead = new();
    private readonly StackPanel _lintCounts = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _orderHead = new();
    private readonly TextBlock _budgetHead = new();
    private readonly StackPanel _modes = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _legend = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private double _lastFill = double.NaN;
    private IReadOnlyList<DrawRow> _lastRows = Array.Empty<DrawRow>();
    private readonly HashSet<LintLevel> _hidden = new();
    private string _orderKey = "";

    public CheckView()
    {
        Background = Res<Brush>("B.BgPanel");
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star), MinWidth = 240 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 220 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 230 });
        Column(grid, 0, Header(_lintHead, _lintCounts), _lint);
        // The view switch sits under the title, so a narrow column keeps the title readable.
        var orderTop = new StackPanel();
        orderTop.Children.Add(Header(_orderHead, null));
        var views = new WrapPanel { Margin = new Thickness(10, 4, 10, 2) };
        views.Children.Add(_modes);
        views.Children.Add(_legend);
        orderTop.Children.Add(views);
        Column(grid, 1, orderTop, _order);
        Column(grid, 2, Header(_budgetHead, null), _budget);
        Content = grid;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
                _host?.BudgetWanted();
        };
    }

    public void Init(ICheckHost host)
    {
        _host = host;
        Refresh();
    }

    public void Refresh()
    {
        _lintHead.Text = Loc.T("Chk.Lint");
        _lintHead.ToolTip = Loc.T("Tip.Chk.Lint");
        _orderHead.Text = Loc.T("Chk.Order");
        _orderHead.ToolTip = Loc.T("Tip.Chk.Order");
        _budgetHead.Text = Loc.T("Chk.Budget");
        _budgetHead.ToolTip = Loc.T("Tip.Chk.Budget");
        BuildModes();
        ShowLint();
        ShowBudget();
        _orderKey = "";
        Tick();
    }

    private static T Res<T>(string key) => (T)Application.Current.FindResource(key);

    private void Column(Grid grid, int column, FrameworkElement top, Panel body)
    {
        var dock = new DockPanel { Margin = new Thickness(column == 0 ? 0 : 1, 0, 0, 0) };
        if (column > 0)
        {
            var line = new Border { Width = 1, Background = Res<Brush>("B.LineSubtle") };
            DockPanel.SetDock(line, Dock.Left);
            dock.Children.Add(line);
        }
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        dock.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Grid.SetColumn(dock, column);
        grid.Children.Add(dock);
    }

    private FrameworkElement Header(TextBlock title, FrameworkElement? right)
    {
        var bar = new DockPanel { Background = Res<Brush>("B.BgRaised"), Height = 28, LastChildFill = true };
        if (right != null)
        {
            right.Margin = new Thickness(6, 0, 8, 0);
            right.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(right, Dock.Right);
            bar.Children.Add(right);
        }
        title.Style = Res<Style>("T.Hi");
        title.Margin = new Thickness(10, 0, 6, 0);
        title.VerticalAlignment = VerticalAlignment.Center;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        bar.Children.Add(title);
        return bar;
    }

    private static string LevelBrush(LintLevel level) => level switch
    {
        LintLevel.Block => "B.Err",
        LintLevel.Warn => "B.Warn",
        _ => "B.Info",
    };

    // ---- lint ----

    public void ShowLint()
    {
        _lint.Children.Clear();
        _lintCounts.Children.Clear();
        var report = _host?.Lint ?? LintReport.Empty;
        foreach (var level in new[] { LintLevel.Block, LintLevel.Warn, LintLevel.Info })
        {
            int n = report.Count(level);
            var chip = new ToggleButton
            {
                Style = Res<Style>("Btn.Seg"), IsChecked = !_hidden.Contains(level), Margin = new Thickness(0, 0, 3, 0), Padding = new Thickness(6, 0, 6, 0),
                ToolTip = Loc.F("Tip.Chk.Filter", Loc.T("Chk.Level." + level)),
            };
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = Res<Brush>(LevelBrush(level)), Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(new TextBlock { Text = n.ToString(Loc.Culture), VerticalAlignment = VerticalAlignment.Center });
            chip.Content = content;
            chip.Click += (_, _) =>
            {
                if (!_hidden.Remove(level))
                    _hidden.Add(level);
                ShowLint();
            };
            _lintCounts.Children.Add(chip);
        }
        if (report.Issues.Count == 0)
        {
            _lint.Children.Add(new TextBlock { Text = Loc.T("Chk.Clean"), Foreground = Res<Brush>("B.Ok"), Margin = new Thickness(10, 6, 10, 2), TextWrapping = TextWrapping.Wrap, ToolTip = Loc.T("Tip.Chk.Clean") });
            return;
        }
        var shown = report.Issues.Where(i => !_hidden.Contains(i.Level)).OrderBy(i => i.Level).ThenBy(i => i.Part).ThenBy(i => i.Emitter);
        foreach (var group in shown.GroupBy(i => (i.Rule, i.Part)))
        {
            var list = group.ToList();
            if (list.Count >= GroupFrom && group.Key.Part >= 0)
                _lint.Children.Add(GroupRow(report, list));
            else
                foreach (var issue in list)
                    _lint.Children.Add(LintRow(report, issue));
        }
    }

    // One rule on many emitters of one part (a whole tornado of big cards) is one row naming them all in its tooltip.
    private const int GroupFrom = 3;

    private FrameworkElement GroupRow(LintReport report, IReadOnlyList<LintIssue> issues)
    {
        var first = issues[0];
        var names = issues.Select(i => EmitterName(report, i)).ToList();
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11.5 };
        text.Inlines.Add(new System.Windows.Documents.Run(report.Parts[first.Part] + ": ") { Foreground = Res<Brush>("B.TextDim") });
        text.Inlines.Add(new System.Windows.Documents.Run(Loc.F("Chk.Group", Loc.T("Lint." + first.Rule.Id + ".Name"), issues.Count)) { Foreground = Res<Brush>("B.TextHi") });
        var tip = Loc.F("Tip.Chk.Group", Loc.F("Lint." + first.Rule.Id, first.Args), string.Join(", ", names), Loc.T("Chk.Level." + first.Level), Loc.T("Chk.Source." + first.Rule.Source));
        return Row(issues, text, Loc.F("Lint." + first.Rule.Id + ".Fix", first.Args), tip);
    }

    private FrameworkElement LintRow(LintReport report, LintIssue issue)
    {
        string where = issue.Part < 0 ? Loc.T("Chk.Project") : report.Parts[issue.Part];
        if (issue.Emitter >= 0)
            where += " · " + EmitterName(report, issue);
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11.5 };
        text.Inlines.Add(new System.Windows.Documents.Run(where + ": ") { Foreground = Res<Brush>("B.TextDim") });
        text.Inlines.Add(new System.Windows.Documents.Run(Loc.F("Lint." + issue.Rule.Id, issue.Args)) { Foreground = Res<Brush>("B.TextHi") });
        var tip = Loc.F("Tip.Chk.Issue", Loc.T("Lint." + issue.Rule.Id + ".Name"), Loc.T("Chk.Level." + issue.Level), Loc.T("Chk.Source." + issue.Rule.Source),
            issue.Key ?? Loc.T("Val.None"));
        return Row(new[] { issue }, text, Loc.F("Lint." + issue.Rule.Id + ".Fix", issue.Args), tip);
    }

    private FrameworkElement Row(IReadOnlyList<LintIssue> issues, TextBlock text, string fixText, string tip)
    {
        var issue = issues[0];
        var fix = new TextBlock { Text = fixText, TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Res<Brush>("B.TextDim") };
        var body = new StackPanel();
        body.Children.Add(text);
        body.Children.Add(fix);
        if (Pfx.App.Recipes.RecipeCatalog.ForLint.TryGetValue(issue.Rule.Id, out var recipe))
        {
            var apply = new Button
            {
                Content = Loc.F("Chk.ApplyRecipe", Loc.T("Rcp." + recipe)), ToolTip = Loc.F("Tip.Chk.ApplyRecipe", Loc.T("Rcp." + recipe), Loc.T("Rcp." + recipe + ".What")),
                HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 3, 0, 1), Padding = new Thickness(6, 0, 6, 0),
            };
            apply.Click += (_, e) =>
            {
                e.Handled = true;
                _host?.ApplyLintRecipe(issues);
            };
            body.Children.Add(apply);
        }
        var dot = new Ellipse { Width = 8, Height = 8, Fill = Res<Brush>(LevelBrush(issue.Level)), Margin = new Thickness(0, 4, 8, 0), VerticalAlignment = VerticalAlignment.Top };
        var source = new TextBlock { Text = issue.Rule.Source, FontSize = 10, Foreground = Res<Brush>("B.TextOff"), Margin = new Thickness(6, 2, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        var row = new DockPanel { Margin = new Thickness(10, 3, 10, 3) };
        DockPanel.SetDock(dot, Dock.Left);
        DockPanel.SetDock(source, Dock.Right);
        row.Children.Add(dot);
        row.Children.Add(source);
        row.Children.Add(body);
        var button = new Border
        {
            Child = row, Background = Brushes.Transparent, Cursor = Cursors.Hand, BorderBrush = Res<Brush>("B.LineSubtle"), BorderThickness = new Thickness(0, 0, 0, 1),
            ToolTip = tip,
        };
        button.MouseEnter += (_, _) => button.Background = Res<Brush>("B.BgHover");
        button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
        button.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && Up<Button>(d) != null)
                return;
            _host?.Jump(issue);
        };
        return button;
    }

    private static string EmitterName(LintReport report, LintIssue issue) =>
        issue.Emitter < report.EmitterNames[issue.Part].Count ? report.EmitterNames[issue.Part][issue.Emitter] : "#" + issue.Emitter;

    // ---- draw order ----

    private void BuildModes()
    {
        _modes.Children.Clear();
        foreach (var mode in Enum.GetValues<DrawView>())
        {
            var b = new ToggleButton
            {
                Style = Res<Style>("Btn.Seg"), Content = Loc.T("Chk.View." + mode), ToolTip = Loc.T("Tip.Chk.View." + mode),
                IsChecked = _host?.DrawMode == mode, Margin = new Thickness(0, 0, -1, 0),
            };
            b.Click += (_, _) =>
            {
                if (_host == null)
                    return;
                _host.DrawMode = mode;
                BuildModes();
            };
            _modes.Children.Add(b);
        }
        _legend.Children.Clear();
        _legend.Visibility = _host?.DrawMode == DrawView.Overdraw ? Visibility.Visible : Visibility.Collapsed;
        foreach (int layers in new[] { 1, 2, 4, 8, 16, 32 })
        {
            var c = OverdrawColor(layers);
            _legend.Children.Add(new Border { Width = 12, Height = 12, Background = new SolidColorBrush(c), Margin = new Thickness(0, 0, 3, 0), ToolTip = Loc.F("Tip.Chk.Legend", layers) });
            _legend.Children.Add(new TextBlock { Text = layers.ToString(Loc.Culture), FontSize = 10.5, Foreground = Res<Brush>("B.TextDim"), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = Loc.F("Tip.Chk.Legend", layers) });
        }
    }

    // The colour n sprite layers sum to in the overdraw view (linear sum, shown through the sRGB target).
    private static Color OverdrawColor(int layers)
    {
        byte S(float linear)
        {
            float c = Math.Clamp(linear, 0, 1);
            double s = c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;
            return (byte)Math.Round(s * 255);
        }
        var v = Renderer.OverdrawStep * layers;
        return Color.FromRgb(S(v.X), S(v.Y), S(v.Z));
    }

    // Called a few times a second while the panel shows: the order follows the camera and the particles.
    public void Tick()
    {
        if (_host == null || !IsVisible)
            return;
        var rows = _host.DrawOrder(out double fill);
        string key = string.Join("|", rows.Select(r => $"{r.Effect}.{r.Index}.{r.MaySwap}.{Math.Round(r.Key, 1)}.{Math.Round(r.Fill, 2)}")) + "|" + Math.Round(fill, 2);
        if (key != _orderKey)
        {
            _orderKey = key;
            ShowOrder(rows);
        }
        UpdateFill(fill, rows);
    }

    private void ShowOrder(IReadOnlyList<DrawRow> rows)
    {
        _order.Children.Clear();
        if (rows.Count == 0)
        {
            _order.Children.Add(new TextBlock { Text = Loc.T("Chk.NoDraws"), Foreground = Res<Brush>("B.TextDim"), Margin = new Thickness(10, 6, 10, 2), TextWrapping = TextWrapping.Wrap });
            return;
        }
        foreach (var r in rows)
        {
            var (cr, cg, cb) = Renderer.LayerColor(r.Layer);
            var grid = new Grid { Margin = new Thickness(10, 1, 10, 1), Height = 22 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            void Cell(UIElement e, int c)
            {
                Grid.SetColumn(e, c);
                grid.Children.Add(e);
            }
            Cell(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Color.FromRgb((byte)cr, (byte)cg, (byte)cb)), VerticalAlignment = VerticalAlignment.Center, ToolTip = Loc.T("Tip.Chk.Swatch") }, 0);
            Cell(new TextBlock { Text = (r.Layer + 1).ToString(Loc.Culture), Foreground = Res<Brush>("B.TextOff"), FontFamily = Res<FontFamily>("Font.Mono"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center }, 1);
            Cell(new TextBlock { Text = r.Part + " · " + r.Emitter, Foreground = Res<Brush>("B.TextHi"), FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center }, 2);
            Cell(new TextBlock { Text = Loc.F("Chk.Metres", r.Key.ToString("0.0", Loc.Culture)), Foreground = Res<Brush>("B.TextDim"), FontFamily = Res<FontFamily>("Font.Mono"), FontSize = 11, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = Loc.T("Tip.Chk.Key") }, 3);
            var (note, brush, tip) = r.Additive ? ("Chk.NoteAdd", "B.Ok", "Tip.Chk.NoteAdd")
                : r.MaySwap ? ("Chk.NoteSwap", "B.Warn", "Tip.Chk.NoteSwap")
                : r.Sorted ? ("Chk.NoteSorted", "B.Text", "Tip.Chk.NoteSorted")
                : ("", "B.TextDim", "");
            if (note.Length > 0)
                Cell(new TextBlock { Text = Loc.T(note), Foreground = Res<Brush>(brush), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, ToolTip = Loc.T(tip) }, 4);
            var row = new Border { Child = grid, Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = Loc.F("Tip.Chk.OrderRow", r.Part, r.Emitter, Math.Round(r.Fill, 2)) };
            row.MouseEnter += (_, _) => row.Background = Res<Brush>("B.BgHover");
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonUp += (_, _) => _host?.SelectDraw(r);
            _order.Children.Add(row);
        }
    }

    // ---- budget ----

    private TextBlock? _fillValue;
    private TextBlock? _fillTop;

    public void ShowBudget()
    {
        _budget.Children.Clear();
        _fillValue = null;
        _fillTop = null;
        var b = _host?.Budget;
        if (b == null)
        {
            _budget.Children.Add(Dim(_host?.BudgetBusy == true ? Loc.T("Chk.Counting") : Loc.T("Chk.NoBudget")));
            return;
        }
        void Row(string key, string value, string brush = "B.TextHi")
        {
            var row = new DockPanel { Margin = new Thickness(10, 1, 10, 1), ToolTip = Loc.T("Tip.Chk.B." + key) };
            var v = new TextBlock { Text = value, Foreground = Res<Brush>(brush), FontFamily = Res<FontFamily>("Font.Mono"), FontSize = 11.5, Margin = new Thickness(8, 0, 0, 0) };
            DockPanel.SetDock(v, Dock.Right);
            row.Children.Add(v);
            row.Children.Add(new TextBlock { Text = Loc.T("Chk.B." + key), Foreground = Res<Brush>("B.TextDim"), FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis });
            _budget.Children.Add(row);
            if (key == "Fill")
                _fillValue = v;
        }
        Row("Peak", Loc.F("Chk.B.PeakValue", b.Peak.ToString("N0", Loc.Culture), b.PeakAt.ToString("0.0", Loc.Culture)));
        Row("Fill", "…");
        _fillTop = new TextBlock { Margin = new Thickness(10, 0, 10, 2), FontSize = 10.5, Foreground = Res<Brush>("B.TextDim"), TextTrimming = TextTrimming.CharacterEllipsis };
        _budget.Children.Add(_fillTop);
        Row("Textures", Loc.F("Chk.B.TexturesValue", TextureFacts.Mb(b.TextureBytes), b.Textures));
        if (b.Project)
        {
            Row("Lights", Loc.F("Chk.B.LightsValue", b.Lights, BudgetReport.LightLimit, b.LightsTotal), b.Lights > BudgetReport.LightLimit ? "B.Err" : "B.TextHi");
            Row("Hazes", Loc.F("Chk.B.AtOnceValue", b.Hazes, b.HazesTotal));
            Row("Sounds", Loc.F("Chk.B.AtOnceValue", b.Sounds, b.SoundsTotal));
        }
        else
        {
            _budget.Children.Add(Dim(Loc.T("Chk.NoProjectBudget")));
        }
        Row("Length", Loc.F("Chk.B.LengthValue", b.Length.ToString("0.0", Loc.Culture)));
        if (!double.IsNaN(_lastFill))
            UpdateFill(_lastFill, _lastRows);
        if (b.Parts.Count > 0)
        {
            _budget.Children.Add(new TextBlock { Text = Loc.T("Chk.B.PerPart"), Style = Res<Style>("T.Hi"), FontSize = 11, Margin = new Thickness(10, 6, 10, 2), ToolTip = Loc.T("Tip.Chk.B.PerPart") });
            foreach (var p in b.Parts)
            {
                var row = new DockPanel { Margin = new Thickness(10, 1, 10, 1), ToolTip = Loc.T("Tip.Chk.B.PartRow") };
                var v = new TextBlock
                {
                    Text = Loc.F("Chk.B.PartValue", p.Peak.ToString("N0", Loc.Culture), p.MaxNum.ToString("N0", Loc.Culture), TextureFacts.Mb(p.TextureBytes)),
                    Foreground = Res<Brush>("B.Text"), FontFamily = Res<FontFamily>("Font.Mono"), FontSize = 11, Margin = new Thickness(8, 0, 0, 0),
                };
                DockPanel.SetDock(v, Dock.Right);
                row.Children.Add(v);
                row.Children.Add(new TextBlock { Text = p.Title, Foreground = Res<Brush>("B.TextDim"), FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis });
                _budget.Children.Add(row);
            }
        }
    }

    private void UpdateFill(double fill, IReadOnlyList<DrawRow> rows)
    {
        _lastFill = fill;
        _lastRows = rows;
        if (_fillValue == null)
            return;
        _fillValue.Text = Loc.F("Chk.B.FillValue", fill.ToString("0.0", Loc.Culture));
        if (_fillTop != null)
        {
            var top = rows.OrderByDescending(r => r.Fill).FirstOrDefault();
            _fillTop.Text = top == null || top.Fill <= 0 ? "" : Loc.F("Chk.B.FillTop", top.Part + " · " + top.Emitter, top.Fill.ToString("0.0", Loc.Culture));
            _fillTop.ToolTip = Loc.T("Tip.Chk.B.FillTop");
        }
    }

    private TextBlock Dim(string text) => new() { Text = text, Foreground = Res<Brush>("B.TextDim"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 6, 10, 2), FontSize = 11.5 };

    private static T? Up<T>(DependencyObject? d) where T : class
    {
        while (d != null && d is not T)
            d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }
}
