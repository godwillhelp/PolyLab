using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Pfx.App.Curves;
using Pfx.Sim;

namespace Pfx.App.Views;

// The curve editor panel of the bottom dock: CurveEditor holds the state, this view lays it out (toolbar, layer list,
// key table, plot, colour strip) and passes gestures on.
public partial class CurvesView : UserControl
{
    private string _keySignature = "";
    private int _strips;

    public CurvesView()
    {
        InitializeComponent();
    }

    public CurveEditor Editor { get; } = new();

    public CurveCanvas Plot => Canvas;

    public void Init()
    {
        Canvas.Editor = Editor;
        Strip.Editor = Editor;
        Strip.EditStop = OpenStop;
        Editor.Changed += Refresh;
        Refresh();
    }

    public void Refresh()
    {
        var ed = Editor;
        var em = ed.Emitter;
        TargetText.Text = em == null ? Loc.T("Curve.NoEmitter")
            : ed.SelectedEmitters > 1 ? Loc.F("Curve.TargetOfMany", em.Name, ed.SelectedEmitters) : em.Name;
        TargetText.ToolTip = em == null ? Loc.T("Tip.CurveNoEmitter")
            : Loc.F(ed.SelectedEmitters > 1 ? "Tip.CurveTargetMany" : "Tip.CurveTarget", em.Name, ed.SelectedEmitters);
        LifeButton.IsChecked = !ed.IsMaster;
        MasterButton.IsChecked = ed.IsMaster;
        DrawButton.IsChecked = ed.Drawing;
        SnapButton.IsChecked = ed.Snap;
        UnitBar.Visibility = ed.IsMaster ? Visibility.Visible : Visibility.Collapsed;
        NormButton.IsChecked = !ed.Seconds;
        SecButton.IsChecked = ed.Seconds;
        bool editable = ed.Editable;
        foreach (var b in new ButtonBase[] { PresetButton, DrawButton, DeleteButton, PasteButton })
            b.IsEnabled = editable;
        CopyButton.IsEnabled = ed.HasSelection;
        DeleteButton.IsEnabled = editable && ed.HasSelection;
        PasteButton.IsEnabled = editable && ed.HasClipboard;
        if (!ErrorBox.IsKeyboardFocusWithin)
            ErrorBox.Text = ed.Format(ed.MaxError);
        BakeText.Text = ed.BakeText;
        BakeText.ToolTip = ed.BakeText.Length > 0 ? Loc.T(ed.BakeMissed ? "Tip.CurveBakedMissed" : "Tip.CurveBaked") : null;
        BakeText.SetResourceReference(TextBlock.ForegroundProperty, ed.BakeMissed ? "B.Warn" : "B.Ok");
        EmptyText.Visibility = em == null ? Visibility.Visible : Visibility.Collapsed;
        Strip.Visibility = em != null && ed.ShowsStrip ? Visibility.Visible : Visibility.Collapsed;

        var rows = new List<CurveLayerRow>();
        for (int i = 0; i < ed.Layers.Count; i++)
        {
            var layer = ed.Layers[i];
            int count = ed.Keys(layer).Count;
            bool present = ed.Present(layer), modified = ed.Modified(layer);
            string channel = layer.IsColor ? Loc.F("Tip.CurveChannelOf", "RGB"[layer.Channel]) : "";
            string state = present ? Loc.T("Tip.CurvePresent") : Loc.T("Tip.CurveDefault");
            rows.Add(new CurveLayerRow(i, Loc.T(layer.Name), layer.Brush, count.ToString(CultureInfo.InvariantCulture),
                modified ? "●" : present ? "▪" : "", modified ? "B.Key" : "B.TextOff", ed.IsShown(i), i == ed.ActiveIndex,
                Loc.F("Tip.CurveLayer", Loc.T(layer.Name), layer.Key, channel, state, Loc.T("Hint.Curve." + layer.Key))));
        }
        LayerList.ItemsSource = rows;

        var active = ed.Active;
        TimeHeader.Text = Loc.T(ed.IsMaster && ed.Seconds ? "Curve.TimeSeconds" : "Curve.Time");
        TimeHeader.ToolTip = Loc.T(ed.IsMaster ? "Tip.GraphTimeMast" : "Tip.GraphTime");
        ValueHeader.Text = active.IsColor ? Loc.F("Curve.Channel", "RGB"[active.Channel]) : Loc.T("Curve.Value");
        ValueHeader.ToolTip = Loc.T("Tip.GraphValue");
        var keys = ed.Keys(active);
        string signature = string.Join(";", keys.Select((k, i) => $"{k.Time}:{k.Values[active.Channel + 1]}:{ed.Selection.Contains(i)}"))
                           + "|" + active + "|" + ed.TimeScale;
        if (signature != _keySignature && !KeyList.IsKeyboardFocusWithin)
        {
            _keySignature = signature;
            KeyList.ItemsSource = keys.Select((k, i) =>
            {
                double v = k.Values[active.Channel + 1];
                return new CurveKeyRow(i, ed.Format(k.Time * ed.TimeScale), ed.Format(v), ed.Selection.Contains(i),
                    v > 1 || v < 0 ? "B.Err" : "B.TextHi", Loc.T(ed.IsMaster ? "Tip.GraphTimeMast" : "Tip.GraphTime"),
                    v > 1 || v < 0 ? Loc.T("Tip.GraphValueClamped") : Loc.T("Tip.GraphValue"));
            }).ToList();
        }
        Canvas.InvalidateVisual();
        Strip.InvalidateVisual();
    }

    private void OnKind(object sender, RoutedEventArgs e) =>
        Editor.SetKind((string)((FrameworkElement)sender).Tag == "mast" ? CurveKind.Master : CurveKind.Life);

    private void OnUnit(object sender, RoutedEventArgs e) => Editor.Seconds = (string)((FrameworkElement)sender).Tag == "sec";

    private void OnDraw(object sender, RoutedEventArgs e) => Editor.Drawing = DrawButton.IsChecked == true;

    private void OnSnap(object sender, RoutedEventArgs e) => Editor.Snap = SnapButton.IsChecked == true;

    private void OnCopy(object sender, RoutedEventArgs e) => Editor.Copy();

    private void OnPaste(object sender, RoutedEventArgs e) => Editor.Paste();

    private void OnDelete(object sender, RoutedEventArgs e) => Editor.DeleteSelected();

    private void OnAddKey(object sender, RoutedEventArgs e) => Editor.AddKey();

    private void OnPresets(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = PresetButton, Placement = PlacementMode.Bottom };
        foreach (var name in CurvePresets.Names)
        {
            var item = new MenuItem { Header = Loc.T("Preset." + name), ToolTip = Loc.F("Tip.Preset." + name, Loc.T(Editor.Active.Name)) };
            string preset = name;
            item.Click += (_, _) => Editor.ApplyPreset(preset);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void OnShow(object sender, RoutedEventArgs e) => Editor.ToggleShown((int)((FrameworkElement)sender).Tag);

    private void OnLayer(object sender, RoutedEventArgs e) => Editor.SetActive((int)((FrameworkElement)sender).Tag);

    private void OnKeyRow(object sender, MouseButtonEventArgs e) =>
        Editor.Select((int)((FrameworkElement)sender).Tag, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));

    private void OnCellFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is CurveKeyRow row && !row.Selected)
            Editor.Select(row.Index, false);
    }

    private void OnCellKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Commit((TextBox)sender);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _keySignature = "";
            Keyboard.ClearFocus();
            Refresh();
            e.Handled = true;
        }
    }

    private void OnCellLost(object sender, KeyboardFocusChangedEventArgs e) => Commit((TextBox)sender);

    // Only a changed text is an edit (leaving a box without typing writes nothing).
    private void Commit(TextBox box)
    {
        if (box.DataContext is not CurveKeyRow row)
            return;
        bool time = (string)box.Tag == "t";
        string before = time ? row.Time : row.Value;
        if (box.Text.Trim() == before)
            return;
        _keySignature = "";
        if (time)
            Editor.SetTime(row.Index, box.Text);
        else
            Editor.SetValue(row.Index, box.Text);
    }

    private void OnErrorKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            CommitError();
    }

    private void OnErrorLost(object sender, KeyboardFocusChangedEventArgs e) => CommitError();

    private void CommitError()
    {
        if (Core.Expr.TryApply(ErrorBox.Text, Editor.MaxError, out double v) && double.IsFinite(v))
            Editor.MaxError = v;
        ErrorBox.Text = Editor.Format(Editor.MaxError);
    }

    private void OpenStop(bool alpha, int key, Point screen)
    {
        var popup = new ColorPopup(Editor, alpha, key, "stop-popup:" + (++_strips).ToString(CultureInfo.InvariantCulture))
        {
            HorizontalOffset = screen.X,
            VerticalOffset = screen.Y - 240,
        };
        popup.IsOpen = true;
    }
}
