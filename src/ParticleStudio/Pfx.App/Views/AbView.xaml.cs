using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Pfx.App.XRay;
using Pfx.Sim.XRay;

namespace Pfx.App.Views;

public sealed class AbChip
{
    public required string Text { get; init; }
    public required string Brush { get; init; }
    public required string Tip { get; init; }
}

public sealed class AbEmitterRow
{
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public required string KindBrush { get; init; }
    public required List<AbChip> Chips { get; init; }
    public required string Summary { get; init; }
    public required string Tip { get; init; }
}

// The A/B panel: how the X-Ray original of the active part is shown next to (or over) its DayZ conversion, how many
// particles each side has now, and per X-Ray emitter every action with what the conversion did to it (xray.py
// action_status: carried over, approximated, dropped) and whether the A/B simulator plays it; xray.py's notes for the
// emitter are its tooltip. Data, not log text (spec 3.9).
public partial class AbView : UserControl
{
    private AbCompare? _ab;
    private Action? _changed;

    public AbView()
    {
        InitializeComponent();
    }

    public IReadOnlyList<AbEmitterRow> Rows => EmitterList.ItemsSource as IReadOnlyList<AbEmitterRow> ?? Array.Empty<AbEmitterRow>();

    public void Init(AbCompare ab, Action changed)
    {
        _ab = ab;
        _changed = changed;
        Show();
    }

    private void OnMode(object sender, RoutedEventArgs e)
    {
        if (_ab == null)
            return;
        _ab.Mode = Enum.Parse<AbMode>((string)((FrameworkElement)sender).Tag);
        _changed?.Invoke();
        Show();
    }

    private void OnDistanceKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            OnDistance(sender, e);
    }

    private void OnDistance(object sender, RoutedEventArgs e)
    {
        if (_ab == null)
            return;
        if (Core.Expr.TryApply(DistanceBox.Text, _ab.Distance, out double d) && d >= 0)
            _ab.Distance = (float)d;
        DistanceBox.Text = _ab.Distance.ToString("0.##", CultureInfo.InvariantCulture);
        _changed?.Invoke();
    }

    // Called once per shown frame by the shell (cheap: two numbers).
    public void ShowLive(int dayz)
    {
        if (_ab?.Record == null)
        {
            LiveText.Text = "";
            return;
        }
        LiveText.Text = Loc.F("Ab.LiveText", _ab.Live, dayz);
    }

    public void Show()
    {
        if (_ab == null)
            return;
        foreach (var (b, m) in new[] { (SideButton, AbMode.Side), (OverButton, AbMode.Over), (OffButton, AbMode.Off) })
            b.IsChecked = _ab.Mode == m;
        DistanceBox.Text = _ab.Distance.ToString("0.##", CultureInfo.InvariantCulture);
        DistanceBox.IsEnabled = _ab.Mode == AbMode.Side;
        var r = _ab.Record;
        if (r == null)
        {
            SubjectText.Text = Loc.T("Ab.None");
            EmitterList.ItemsSource = null;
            return;
        }
        var subject = new List<string> { Loc.F("Ab.Subject", r.Ref, r.Tag, r.Variant, r.Ptc.Count) };
        if (_ab.Missing != null)
            subject.Add(Loc.F("Ab.Missing", _ab.Missing));
        if (_ab.Problems.Count > 0)
            subject.Add(Loc.F("Ab.Problems", string.Join("; ", _ab.Problems)));
        SubjectText.Text = string.Join("\n", subject);
        EmitterList.ItemsSource = r.Emitters.Select(e => new AbEmitterRow
        {
            Name = e.Name + (e.Child ? " " + Loc.T("Ab.ChildMark") : ""),
            Kind = Loc.T("Ab.Kind." + e.Kind),
            KindBrush = e.Kind switch { "emitter" => "B.Ok", "haze" => "B.Info", "spawner" => "B.Warn", _ => "B.Err" },
            Chips = e.Actions.Select(a => new AbChip
            {
                Text = a.Name == a.Type ? a.Type : $"{a.Name} ({a.Type})",
                Brush = XrSpec.NotSimulated.Contains(a.Type) ? "B.TextOff" : a.DayZ switch { "fitted" => "B.Ok", "approximated" => "B.Warn", _ => "B.Err" },
                Tip = Loc.T("Ab.Status." + a.DayZ) + (a.Replayed ? "" : "\n" + Loc.T("Ab.NotReplayed")) + (XrSpec.NotSimulated.Contains(a.Type) ? "\n" + Loc.T("Ab.NotInAbTip") : ""),
            }).ToList(),
            Summary = e.Replay is { } rp ? Loc.F("Ab.Replay", rp.Particles, rp.Life.ElementAtOrDefault(0), rp.Life.ElementAtOrDefault(2), rp.Rise) : "",
            Tip = e.Notes.Count > 0 ? string.Join("\n", e.Notes.Select(n => n.Trim())) : Loc.T("Ab.NoNotes"),
        }).ToList();
    }
}
