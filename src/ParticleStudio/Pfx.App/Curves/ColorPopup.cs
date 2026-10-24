using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Pfx.App.Curves;

// The colour of one stop of the gradient strip: a slider and a number per channel, a hex field, and "keep
// brightness" (spec 2.11: change the hue at constant luma; luma by the Rec. 709 weights of the values as written).
// A popup (its own window, so it also shows over the viewport); every change is live, and the whole session is one
// undo step. Closes when the user clicks elsewhere.
public sealed class ColorPopup : Popup
{
    private static readonly double[] Luma = { 0.2126, 0.7152, 0.0722 };

    private readonly CurveEditor _editor;
    private readonly bool _alpha;
    private readonly int _key;
    private readonly string _merge;
    private readonly double[] _values;
    private readonly Slider[] _sliders;
    private readonly TextBox[] _boxes;
    private readonly TextBox? _hex;
    private readonly CheckBox? _keepLuma;
    private readonly Border _swatch;
    private bool _updating;

    public ColorPopup(CurveEditor editor, bool alpha, int key, string merge)
    {
        _editor = editor;
        _alpha = alpha;
        _key = key;
        _merge = merge;
        var layer = alpha ? editor.AlphaLayer : editor.ColorLayer;
        _values = editor.Keys(layer)[key].Values.Skip(1).ToArray();
        int n = _values.Length;
        _sliders = new Slider[n];
        _boxes = new TextBox[n];
        StaysOpen = false;
        AllowsTransparency = true;
        Placement = PlacementMode.AbsolutePoint;

        var panel = new StackPanel { Width = 230 };
        var title = new TextBlock { Text = Loc.F(alpha ? "Curve.StopAlpha" : "Curve.StopColour", key + 1), Margin = new Thickness(0, 0, 0, 6) };
        title.SetResourceReference(StyleProperty, "T.Hi");
        panel.Children.Add(title);
        _swatch = new Border { Height = 18, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 0, 6), ToolTip = Loc.T("Tip.CurveSwatch") };
        panel.Children.Add(_swatch);
        string[] names = alpha ? new[] { "A" } : new[] { "R", "G", "B" };
        string[] brushes = alpha ? new[] { "B.CurveA" } : new[] { "B.CurveR", "B.CurveG", "B.CurveB" };
        for (int c = 0; c < n; c++)
        {
            int channel = c;
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            var label = new TextBlock { Text = names[c], Width = 14, VerticalAlignment = VerticalAlignment.Center, ToolTip = Loc.F("Tip.CurveChannel", names[c]) };
            label.SetResourceReference(TextBlock.ForegroundProperty, brushes[c]);
            DockPanel.SetDock(label, Dock.Left);
            row.Children.Add(label);
            var box = new TextBox { Width = 58, Margin = new Thickness(6, 0, 0, 0), ToolTip = Loc.F("Tip.CurveChannel", names[c]) };
            box.SetResourceReference(StyleProperty, "Box.Num");
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Typed(channel); };
            box.LostKeyboardFocus += (_, _) => Typed(channel);
            DockPanel.SetDock(box, Dock.Right);
            row.Children.Add(box);
            var slider = new Slider { Minimum = 0, Maximum = 1, VerticalAlignment = VerticalAlignment.Center, ToolTip = Loc.F("Tip.CurveChannel", names[c]) };
            slider.ValueChanged += (_, e) => { if (!_updating) Change(channel, e.NewValue); };
            row.Children.Add(slider);
            _sliders[c] = slider;
            _boxes[c] = box;
            panel.Children.Add(row);
        }
        if (!alpha)
        {
            var hexRow = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
            var hexLabel = new TextBlock { Text = "#", Width = 14, VerticalAlignment = VerticalAlignment.Center, ToolTip = Loc.T("Tip.CurveHex") };
            hexLabel.SetResourceReference(StyleProperty, "T.Dim");
            DockPanel.SetDock(hexLabel, Dock.Left);
            hexRow.Children.Add(hexLabel);
            _hex = new TextBox { Width = 80, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(6, 0, 0, 0), ToolTip = Loc.T("Tip.CurveHex") };
            _hex.SetResourceReference(StyleProperty, "Box.Num");
            _hex.KeyDown += (_, e) => { if (e.Key == Key.Enter) HexTyped(); };
            _hex.LostKeyboardFocus += (_, _) => HexTyped();
            hexRow.Children.Add(_hex);
            panel.Children.Add(hexRow);
            _keepLuma = new CheckBox { Content = Loc.T("Curve.KeepLuma"), Margin = new Thickness(0, 6, 0, 0), ToolTip = Loc.T("Tip.CurveKeepLuma") };
            panel.Children.Add(_keepLuma);
        }
        var hint = new TextBlock { Text = Loc.T("Curve.ClampHint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), FontSize = 10 };
        hint.SetResourceReference(StyleProperty, "T.Dim");
        panel.Children.Add(hint);
        var border = new Border { Child = panel, Padding = new Thickness(10), BorderThickness = new Thickness(1) };
        border.SetResourceReference(Border.BackgroundProperty, "B.BgRaised");
        border.SetResourceReference(Border.BorderBrushProperty, "B.Line");
        border.SetResourceReference(Border.CornerRadiusProperty, "R.Panel");
        border.SetResourceReference(Border.EffectProperty, "Fx.Popup");
        Child = border;
        Closed += (_, _) => _editor.Seal();
        Show();
    }

    private void Show()
    {
        _updating = true;
        for (int c = 0; c < _values.Length; c++)
        {
            _sliders[c].Value = Math.Clamp(_values[c], 0, 1);
            _boxes[c].Text = _values[c].ToString("0.####", CultureInfo.InvariantCulture);
            _boxes[c].SetResourceReference(Control.ForegroundProperty, _values[c] > 1 || _values[c] < 0 ? "B.Err" : "B.TextHi");
        }
        byte B(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
        var colour = _alpha ? Color.FromRgb(B(_values[0]), B(_values[0]), B(_values[0])) : Color.FromRgb(B(_values[0]), B(_values[1]), B(_values[2]));
        _swatch.Background = new SolidColorBrush(colour);
        if (_hex != null)
            _hex.Text = $"{colour.R:X2}{colour.G:X2}{colour.B:X2}";
        _updating = false;
    }

    private void Change(int channel, double value)
    {
        if (!double.IsFinite(value))
            return;
        double before = LumaOf(_values);
        _values[channel] = value;
        if (_keepLuma?.IsChecked == true && LumaOf(_values) > 0 && before > 0)
        {
            double k = before / LumaOf(_values);
            for (int c = 0; c < _values.Length; c++)
                _values[c] *= k;
        }
        _editor.SetStop(_alpha, _key, _values, _merge);
        Show();
    }

    private double LumaOf(double[] v) => _alpha ? v[0] : v[0] * Luma[0] + v[1] * Luma[1] + v[2] * Luma[2];

    private void Typed(int channel)
    {
        if (_updating)
            return;
        if (Core.Expr.TryApply(_boxes[channel].Text, _values[channel], out double v) && Math.Abs(v - _values[channel]) > 1e-12)
            Change(channel, v);
        else
            Show();
    }

    private void HexTyped()
    {
        if (_updating || _hex == null)
            return;
        string h = _hex.Text.Trim().TrimStart('#');
        if (h.Length == 6 && int.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
        {
            _values[0] = Math.Round(((rgb >> 16) & 255) / 255.0, 4);
            _values[1] = Math.Round(((rgb >> 8) & 255) / 255.0, 4);
            _values[2] = Math.Round((rgb & 255) / 255.0, 4);
            _editor.SetStop(false, _key, _values, _merge);
        }
        Show();
    }
}
