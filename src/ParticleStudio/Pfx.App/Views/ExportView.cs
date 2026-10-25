using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Pfx.App.Core;
using Pfx.App.Model;
using Pfx.Core.Export;
using Pfx.Core.Model;

namespace Pfx.App.Views;

public interface IExportHost
{
    CompositionDoc? Project { get; }
    AppSettings Settings { get; }
    ExportSnapshot TakeSnapshot();
    void Toast(string text, string kind);
    void ExportWritten(ExportPlan plan);
    // Lint errors that stop the export (spec 3.11): the game would show garbage or nothing.
    int LintBlocking { get; }
    void ShowPane(string id);
}

// The export panel (spec 3.13): the project's profile and its settings (stored in the project), then every file the
// export would write, each new / changed / unchanged, the files it would remove (its own, no longer made), errors that
// stop it and notes on what the profile cannot carry. Planning runs on a worker after every change (the newest plan
// wins); writing is the one button, closed while the check panel lists errors. No dialog: the list is the confirmation.
public sealed class ExportView : UserControl
{
    private IExportHost? _host;
    private readonly StackPanel _settings = new() { Margin = new Thickness(0, 0, 0, 4) };
    private readonly StackPanel _result = new();
    private readonly TextBlock _summary = new();
    private readonly Button _write = new();
    private ExportPlan? _plan;
    private int _generation;
    private bool _planning;
    private bool _queued;

    public ExportView()
    {
        Background = (Brush)Application.Current.FindResource("B.BgPanel");
        var top = new WrapPanel { Margin = new Thickness(8, 5, 8, 3) };
        _write.Click += (_, _) => Write();
        _write.Style = (Style)Application.Current.FindResource("Btn.Accent");
        _write.Margin = new Thickness(0, 0, 8, 4);
        top.Children.Add(_write);
        _summary.Style = (Style)Application.Current.FindResource("T.Dim");
        _summary.VerticalAlignment = VerticalAlignment.Center;
        _summary.Margin = new Thickness(0, 0, 0, 4);
        _summary.TextWrapping = TextWrapping.Wrap;
        top.Children.Add(_summary);
        var body = new StackPanel();
        body.Children.Add(_settings);
        body.Children.Add(_result);
        var dock = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        dock.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Content = dock;
        IsVisibleChanged += (_, _) => { if (IsVisible && _queued) Invalidate(); };
    }

    public ExportPlan? Plan => _plan;
    public bool WriteEnabled => _write.IsEnabled;
    public bool Planning => _planning;
    public event Action? Planned;

    public void Init(IExportHost host)
    {
        _host = host;
        Invalidate();
    }

    // The project changed: settings shown again, a new plan on a worker when the panel is on screen.
    public void Invalidate()
    {
        ShowSettings();
        if (!IsVisible && _plan != null)
        {
            _queued = true;
            return;
        }
        _queued = false;
        Replan();
    }

    public void Replan()
    {
        if (_host?.Project is not { } doc)
        {
            _plan = null;
            ShowResult();
            return;
        }
        var file = doc.Snapshot();
        string folder = doc.Folder ?? Environment.CurrentDirectory;
        var source = _host.TakeSnapshot();
        int gen = ++_generation;
        _planning = true;
        ShowResult();
        var ui = TaskScheduler.FromCurrentSynchronizationContext();
        Task.Run(() => file.Export.Profile == "cg" ? CgExport.Plan(file, folder, source) : DayzModExport.Plan(file, folder, source)).ContinueWith(t =>
        {
            if (gen != _generation)
                return;
            _planning = false;
            if (t.Exception is { } ex)
            {
                _plan = null;
                _host.Toast(Loc.F("Toast.ExpFailed", (ex.InnerException ?? ex).Message), "err");
            }
            else
            {
                _plan = t.Result;
            }
            ShowResult();
            Planned?.Invoke();
        }, ui);
    }

    private void Edit(Action<ProjectFile> edit, string key)
    {
        _host?.Project?.Do(edit, Loc.Later("Undo.Cmp", Loc.Later(key)));
    }

    private void ShowSettings()
    {
        _settings.Children.Clear();
        _write.Content = Loc.T("Exp.Write");
        _write.ToolTip = Loc.T("Tip.Exp.Write");
        if (_host?.Project is not { } doc)
        {
            _settings.Children.Add(Dim(Loc.T("Exp.NoProject")));
            return;
        }
        var x = doc.File.Export;
        var profiles = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 2, 10, 4) };
        foreach (var id in new[] { "dayz", "cg" })
        {
            var b = new ToggleButton { Style = (Style)Application.Current.FindResource("Btn.Seg"), Content = Loc.T("Exp.Profile." + id), ToolTip = Loc.T("Tip.Exp.Profile." + id), IsChecked = x.Profile == id };
            b.Click += (_, _) => { if (x.Profile != id) Edit(f => f.Export.Profile = id, "Exp.Profile"); else b.IsChecked = true; };
            profiles.Children.Add(b);
        }
        Row("Exp.Profile", profiles);
        if (x.Profile == "cg")
        {
            PathRow("Exp.DonorFolder", x.Cg.DonorFolder, true, null, v => { Edit(f => f.Export.Cg.DonorFolder = v, "Exp.DonorFolder"); Remember(s => s.CgDonorFolder = v); });
            PathRow("Exp.SlotFile", x.Cg.SlotFile, false, Loc.T("Filter.Script"), v => { Edit(f => f.Export.Cg.SlotFile = v, "Exp.SlotFile"); Remember(s => s.CgSlotFile = v); });
            TextRow("Exp.Type", x.Cg.Type.Length > 0 ? x.Cg.Type : doc.File.Name, v => Edit(f => f.Export.Cg.Type = ProjectNames.Id(v), "Exp.Type"));
        }
        else
        {
            PathRow("Exp.ModFolder", x.Dayz.ModFolder, true, null, v => Edit(f => f.Export.Dayz.ModFolder = v, "Exp.ModFolder"));
            TextRow("Exp.DataPrefix", x.Dayz.DataPrefix, v => Edit(f => f.Export.Dayz.DataPrefix = v.Trim().Replace('\\', '/').Trim('/'), "Exp.DataPrefix"));
            TextRow("Exp.Prefix", x.Dayz.Prefix, v => Edit(f => f.Export.Dayz.Prefix = v.Trim(), "Exp.Prefix"));
            TextRow("Exp.Folder", x.Dayz.Folder, v => Edit(f => f.Export.Dayz.Folder = ProjectNames.Id(v), "Exp.Folder"));
        }
    }

    private void Remember(Action<AppSettings> set)
    {
        if (_host == null)
            return;
        set(_host.Settings);
        try
        {
            _host.Settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void ShowResult()
    {
        _result.Children.Clear();
        var plan = _plan;
        int blocking = _host?.LintBlocking ?? 0;
        _write.IsEnabled = !_planning && blocking == 0 && plan is { CanWrite: true } && (plan.Files.Any(f => f.State != FileState.Same) || plan.Remove.Count > 0);
        if (blocking > 0)
        {
            var line = Line("●", "B.Err", Loc.F("Exp.LintBlocks", blocking), Loc.T("Tip.Exp.LintBlocks"));
            line.Cursor = Cursors.Hand;
            line.MouseLeftButtonUp += (_, _) => _host?.ShowPane("checks");
            _result.Children.Add(line);
        }
        if (_planning)
        {
            _summary.Text = Loc.T("Exp.Planning");
            return;
        }
        if (plan == null)
        {
            _summary.Text = "";
            return;
        }
        int n = plan.Files.Count(f => f.State == FileState.New), c = plan.Files.Count(f => f.State == FileState.Changed), s = plan.Files.Count(f => f.State == FileState.Same);
        _summary.Text = plan.CanWrite ? Loc.F("Exp.Summary", n, c, s, plan.Remove.Count) : Loc.F("Exp.SummaryErrors", plan.Errors.Count);
        foreach (var e in plan.Errors)
            _result.Children.Add(Line("●", "B.Err", Message(e), Loc.T("Tip.Exp.Error")));
        foreach (var note in plan.Notes.DistinctBy(m => m.Key + string.Join("|", m.Args)))
            _result.Children.Add(Line("●", "B.Warn", Message(note), Loc.T("Tip.Exp.Note")));
        string root = Path.GetDirectoryName(plan.Marker)!;
        foreach (var f in plan.Files.OrderBy(f => f.State).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
        {
            string state = f.State switch { FileState.New => "Exp.New", FileState.Changed => "Exp.Changed", _ => "Exp.Same" };
            string brush = f.State switch { FileState.New => "B.Ok", FileState.Changed => "B.Key", _ => "B.TextDim" };
            string rel = f.Shared ? f.Path : ExportPlan.Rel(root, f.Path);
            _result.Children.Add(Line(Loc.T(state), brush, rel + "  ·  " + Loc.T("Exp.What." + f.What),
                Loc.F("Tip.Exp.File", f.Path, Loc.T(state), f.Bytes.Length) + (f.Shared ? "\n" + Loc.T("Tip.Exp.Shared") : "")));
        }
        foreach (var r in plan.Remove)
            _result.Children.Add(Line(Loc.T("Exp.Removed"), "B.Err", ExportPlan.Rel(root, r), Loc.F("Tip.Exp.Removed", r)));
        if (plan.Example is { } example)
        {
            _result.Children.Add(new TextBlock { Text = Loc.T("Exp.Example"), Style = (Style)Application.Current.FindResource("T.Hi"), FontSize = 11.5, Margin = new Thickness(10, 8, 10, 2), ToolTip = Loc.T("Tip.Exp.Example") });
            var text = new TextBox
            {
                Text = example, IsReadOnly = true, Style = (Style)Application.Current.FindResource("Box.Num"), AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(10, 0, 10, 4), Padding = new Thickness(6, 4, 6, 4), ToolTip = Loc.T("Tip.Exp.Example"),
                VerticalContentAlignment = VerticalAlignment.Top, Height = double.NaN,
            };
            _result.Children.Add(text);
            var copy = new Button { Content = Loc.T("Exp.CopyExample"), ToolTip = Loc.T("Tip.Exp.CopyExample"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(10, 0, 10, 6) };
            copy.Click += (_, _) => { Clipboard.SetText(example); _host?.Toast(Loc.T("Toast.ExpCopied"), "info"); };
            _result.Children.Add(copy);
        }
    }

    public static string Message(ExportMessage m) => Loc.F(m.Key, m.Args);

    // A new lint result: only the write button and the blocking line depend on it.
    public void ShowLint() => ShowResult();

    public void Write()
    {
        if (_plan is not { CanWrite: true } plan || _host == null || _host.LintBlocking > 0)
            return;
        try
        {
            var clock = Stopwatch.StartNew();
            plan.Write();
            _host.Toast(Loc.F("Toast.ExpWritten", plan.Files.Count(f => f.State != FileState.Same), plan.Remove.Count, clock.Elapsed.TotalMilliseconds), "ok");
            _host.ExportWritten(plan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _host.Toast(Loc.F("Toast.ExpFailed", ex.Message), "err");
        }
        Replan();
    }

    private FrameworkElement Line(string mark, string brush, string text, string tip)
    {
        var row = new DockPanel { Margin = new Thickness(10, 1, 10, 1), Background = Brushes.Transparent, ToolTip = tip };
        var m = new TextBlock { Text = mark, Foreground = (Brush)Application.Current.FindResource(brush), Width = 76, FontSize = 11, VerticalAlignment = VerticalAlignment.Top };
        DockPanel.SetDock(m, Dock.Left);
        row.Children.Add(m);
        row.Children.Add(new TextBlock { Text = text, FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.FindResource("B.Text") });
        return row;
    }

    private static TextBlock Dim(string text) =>
        new() { Text = text, Style = (Style)Application.Current.FindResource("T.Dim"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 4, 10, 4) };

    private void Row(string key, FrameworkElement editor)
    {
        var row = new DockPanel { Margin = new Thickness(10, 1, 10, 1) };
        var label = new TextBlock
        {
            Text = Loc.T(key), Width = 130, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = Loc.T("Tip." + key), Foreground = (Brush)Application.Current.FindResource("B.Text"),
        };
        DockPanel.SetDock(label, Dock.Left);
        row.Children.Add(label);
        editor.ToolTip ??= Loc.T("Tip." + key);
        row.Children.Add(editor);
        _settings.Children.Add(row);
    }

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

    private void PathRow(string key, string value, bool folder, string? filter, Action<string> commit)
    {
        var line = new DockPanel();
        var pick = new Button { Content = "…", Width = 26, ToolTip = Loc.T("Tip.Exp.Pick"), Margin = new Thickness(4, 0, 0, 0) };
        DockPanel.SetDock(pick, Dock.Right);
        line.Children.Add(pick);
        var box = new TextBox { Text = value, ToolTip = Loc.T("Tip." + key) };
        line.Children.Add(box);
        void Done()
        {
            if (box.Text != value)
                commit(box.Text.Trim());
        }
        box.LostKeyboardFocus += (_, _) => Done();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Done(); };
        pick.Click += (_, _) =>
        {
            if (folder)
            {
                var d = new OpenFolderDialog { Title = Loc.T(key) };
                if (d.ShowDialog() == true)
                    commit(d.FolderName);
            }
            else
            {
                var d = new SaveFileDialog { Filter = filter ?? "", OverwritePrompt = false, FileName = Path.GetFileName(value) };
                if (d.ShowDialog() == true)
                    commit(d.FileName);
            }
        };
        Row(key, line);
    }
}
