using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Pfx.App.XRay;
using Pfx.XRay;

namespace Pfx.App.Views;

public sealed class SubstRow
{
    public required string Name { get; init; }
    public required string Value { get; init; }
    public required string Brush { get; init; }
    public required string Tip { get; init; }
    public string? Proposal { get; init; }
    public bool Saved { get; init; }
    public bool CanAccept => Proposal != null && !Saved;
}

// The substitute table (xray.py XRAY_TEX_SUBST) as the user's own data: X-Ray texture name -> a file to use instead.
// Saved in the settings folder (xray_subst.json) and sent with every import request, so xray.py uses it exactly as it
// uses its manifest table; CG's own manifest.py stays as it is. Missing textures come from the plans and imports of
// this session, or from "check" over every effect of the sources, each with a proposal where one is found.
public partial class SubstView : UserControl
{
    private XrayWorkspace? _x;
    private readonly Dictionary<string, string?> _missing = new();
    private Action<string, string>? _toast;

    public SubstView()
    {
        InitializeComponent();
    }

    public IReadOnlyList<SubstRow> Rows => RowList.ItemsSource as IReadOnlyList<SubstRow> ?? Array.Empty<SubstRow>();

    public void Init(XrayWorkspace x, Action<string, string> toast)
    {
        _x = x;
        _toast = toast;
        x.Changed += () => CheckButton.IsEnabled = x.Index != null && !x.Busy;
        Show();
    }

    // Textures a plan or import found missing: listed with the proposal the index finds for them.
    public void AddMissing(IEnumerable<string> names)
    {
        if (_x?.Index is not { } index)
            return;
        foreach (var n in names)
            if (!_x.Substitutes.ContainsKey(n))
                _missing[n] = XrAnomalyPlanner.Propose(index, n);
        Show();
    }

    public void Show()
    {
        if (_x == null)
            return;
        var rows = _x.Substitutes.OrderBy(k => k.Key).Select(k => new SubstRow
        {
            Name = k.Key, Value = k.Value, Brush = File.Exists(k.Value) ? "B.Ok" : "B.Err", Saved = true,
            Tip = File.Exists(k.Value) ? Loc.F("Tip.Sub.Saved", k.Value) : Loc.F("Tip.Sub.Gone", k.Value),
        }).Concat(_missing.Where(m => !_x.Substitutes.ContainsKey(m.Key)).OrderBy(m => m.Key).Select(m => new SubstRow
        {
            Name = m.Key, Proposal = m.Value, Brush = m.Value != null ? "B.Warn" : "B.Err",
            Value = m.Value != null ? Loc.F("Sub.Proposal", m.Value) : Loc.T("Sub.NoProposal"),
            Tip = m.Value != null ? Loc.T("Tip.Sub.Proposal") : Loc.T("Tip.Sub.NoProposal"),
        })).ToList();
        RowList.ItemsSource = rows;
        InfoText.Text = Loc.F("Sub.Info", _x.Substitutes.Count, rows.Count(r => !r.Saved));
        AcceptAllButton.IsEnabled = rows.Any(r => r.CanAccept);
        CheckButton.IsEnabled = _x.Index != null && !_x.Busy;
    }

    public void Accept(string name, string file)
    {
        if (_x == null)
            return;
        _x.Substitutes[name] = file;
        _missing.Remove(name);
        _x.SaveSubstitutes();
        Show();
    }

    private static SubstRow Row(object sender) => (SubstRow)((FrameworkElement)sender).DataContext;

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        var r = Row(sender);
        if (r.Proposal != null)
            Accept(r.Name, r.Proposal);
    }

    private void OnAcceptAll(object sender, RoutedEventArgs e)
    {
        foreach (var r in Rows.Where(r => r.CanAccept).ToList())
            Accept(r.Name, r.Proposal!);
    }

    private void OnPick(object sender, RoutedEventArgs e)
    {
        var r = Row(sender);
        var dialog = new OpenFileDialog { Filter = Loc.T("Filter.Dds"), Title = r.Name };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            Accept(r.Name, dialog.FileName);
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (_x == null)
            return;
        var r = Row(sender);
        _x.Substitutes.Remove(r.Name);
        _x.SaveSubstitutes();
        Show();
    }

    // Every effect of every source, textures looked up the way xray.py does; on a worker.
    private void OnCheck(object sender, RoutedEventArgs e) => CheckAll();

    public Task CheckAll()
    {
        if (_x?.Index is not { } index)
            return Task.CompletedTask;
        CheckButton.IsEnabled = false;
        var subst = new Dictionary<string, string>(_x.Substitutes);
        var ui = TaskScheduler.FromCurrentSynchronizationContext();
        return Task.Run(() =>
        {
            var missing = new Dictionary<string, string?>();
            foreach (var en in index.Entries.Where(en => en.Kind == XrEntryKind.Effect && en.Texture is { Length: > 0 }))
            {
                var t = XrAnomalyPlanner.Texture(index, en.Winner.Source, en.Winner.Root.Core ? null : en.Winner.Root.Id, en.Texture!, subst);
                if (t.File == null && !t.FromTable)
                    missing.TryAdd(t.Name, t.Proposal);
            }
            return missing;
        }).ContinueWith(t =>
        {
            foreach (var (k, v) in t.Result)
                _missing[k] = v;
            Show();
            _toast?.Invoke(Loc.F("Sub.Checked", t.Result.Count, t.Result.Count(m => m.Value != null)), "info");
        }, ui);
    }
}
