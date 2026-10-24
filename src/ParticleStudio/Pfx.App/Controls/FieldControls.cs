using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Pfx.App.Inspector;

namespace Pfx.App.Controls;

// Drag a field's label left or right to change the value (spec 5.2): one step per screen pixel, Shift x10, Alt x0.1.
// The whole drag is one undo step (merge key), sealed on release. Double-click puts the caret in the value box.
public sealed class ScrubLabel : TextBlock
{
    public static readonly DependencyProperty FieldProperty = DependencyProperty.Register(nameof(Field), typeof(FieldVm), typeof(ScrubLabel));
    public static readonly DependencyProperty AxisProperty = DependencyProperty.Register(nameof(Axis), typeof(int), typeof(ScrubLabel), new PropertyMetadata(-1));

    private static int _session;
    private Point _last;
    private string? _merge;

    public ScrubLabel()
    {
        Cursor = Cursors.SizeWE;
        Background = Brushes.Transparent;
    }

    public FieldVm? Field { get => (FieldVm?)GetValue(FieldProperty); set => SetValue(FieldProperty, value); }
    public int Axis { get => (int)GetValue(AxisProperty); set => SetValue(AxisProperty, value); }

    public event EventHandler? EditRequested;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ClickCount == 2)
        {
            EditRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }
        if (Field == null || Field.ReadOnly)
            return;
        _last = e.GetPosition(this);
        _merge = "scrub:" + Field.Key + ":" + (++_session).ToString(CultureInfo.InvariantCulture);
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!IsMouseCaptured || _merge == null || Field == null)
            return;
        var p = e.GetPosition(this);
        double dx = p.X - _last.X;
        if (Math.Abs(dx) < 1)
            return;
        _last = p;
        double mult = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) ? 0.1 : 1;
        switch (Field)
        {
            case NumberField n:
                double step = n.Step * mult;
                n.SetFromDrag(v => Snap(v + dx * step, step), _merge);
                break;
            case VectorField vf when Axis >= 0:
                double s = 0.01 * mult;
                vf.SetFromDrag(Axis, v => Snap(v + dx * s, s), _merge);
                break;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!IsMouseCaptured)
            return;
        ReleaseMouseCapture();
        _merge = null;
        Field?.Owner.Doc?.Seal();
    }

    // Values land on the drag step, so a drag never writes 0.30000000004.
    public static double Snap(double v, double step) => Math.Round(Math.Round(v / step) * step, 6);
}

// Slider + value for the keys the engine registers as sliders (CenterX/Y, ScaleX/Y): track, fill from the minimum,
// a tick at the engine default, the thumb. Dragging is one undo step.
public sealed class ValueSlider : FrameworkElement
{
    public static readonly DependencyProperty FieldProperty = DependencyProperty.Register(nameof(Field), typeof(NumberField), typeof(ValueSlider),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(ValueSlider),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private static int _session;
    private string? _merge;

    public ValueSlider()
    {
        Height = 20;
        Cursor = Cursors.Hand;
        FocusVisualStyle = null;
    }

    public NumberField? Field { get => (NumberField?)GetValue(FieldProperty); set => SetValue(FieldProperty, value); }
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    private (double Lo, double Hi) Range => Field?.Min is double lo && Field.Max is double hi && hi > lo ? (lo, hi) : (0, 1);

    private double X(double v)
    {
        var (lo, hi) = Range;
        return 6 + Math.Clamp((v - lo) / (hi - lo), 0, 1) * Math.Max(0, ActualWidth - 12);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double y = ActualHeight / 2;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var track = (Brush)FindResource("B.BgInput");
        var line = new Pen((Brush)FindResource("B.Line"), 1);
        dc.DrawRoundedRectangle(track, line, new Rect(6, y - 2, Math.Max(0, ActualWidth - 12), 4), 2, 2);
        if (Field == null)
            return;
        var accent = (Brush)FindResource(Field.Mixed ? "B.LineStrong" : "B.Accent");
        dc.DrawRoundedRectangle(accent, null, new Rect(6, y - 2, Math.Max(0, X(Value) - 6), 4), 2, 2);
        double dx = X(Field.Default);
        dc.DrawRectangle((Brush)FindResource("B.TextDim"), null, new Rect(dx - 0.5, y - 7, 1, 14));
        if (!Field.Mixed)
            dc.DrawEllipse((Brush)FindResource("B.TextHi"), new Pen(accent, 2), new Point(X(Value), y), 6, 6);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (Field == null || Field.ReadOnly)
            return;
        _merge = "slider:" + Field.Key + ":" + (++_session).ToString(CultureInfo.InvariantCulture);
        CaptureMouse();
        Drag(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (IsMouseCaptured)
            Drag(e.GetPosition(this).X);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!IsMouseCaptured)
            return;
        ReleaseMouseCapture();
        _merge = null;
        Field?.Owner.Doc?.Seal();
    }

    private void Drag(double x)
    {
        if (Field == null || _merge == null)
            return;
        var (lo, hi) = Range;
        double f = Math.Clamp((x - 6) / Math.Max(1, ActualWidth - 12), 0, 1);
        double v = ScrubLabel.Snap(lo + f * (hi - lo), Field.Step);
        Field.SetFromDrag(_ => v, _merge);
    }
}

// Value + spread as one bar: the left handle is the value, the right one value + spread (the game's range, P3). The
// scale grows with the values so both handles stay reachable.
public sealed class RangeBar : FrameworkElement
{
    public static readonly DependencyProperty FieldProperty = DependencyProperty.Register(nameof(Field), typeof(PairField), typeof(RangeBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty VersionProperty = DependencyProperty.Register(nameof(Version), typeof(object), typeof(RangeBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static int _session;
    private string? _merge;
    private bool _rnd;
    private double _scale = 1;

    public RangeBar()
    {
        Height = 14;
        Cursor = Cursors.SizeWE;
    }

    public PairField? Field { get => (PairField?)GetValue(FieldProperty); set => SetValue(FieldProperty, value); }
    public object? Version { get => GetValue(VersionProperty); set => SetValue(VersionProperty, value); }

    private double Scale(PairField f)
    {
        double top = Math.Max(Math.Abs(f.Base.Value), Math.Abs(f.Base.Value + f.Rnd.Value));
        return Math.Max(1, Math.Pow(10, Math.Ceiling(Math.Log10(Math.Max(1e-6, top * 1.25)))));
    }

    private double X(double v) => 4 + Math.Clamp((v + _scale) / (2 * _scale), 0, 1) * Math.Max(0, ActualWidth - 8);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        double y = ActualHeight / 2;
        dc.DrawRoundedRectangle((Brush)FindResource("B.BgInput"), new Pen((Brush)FindResource("B.Line"), 1), new Rect(4, y - 3, Math.Max(0, ActualWidth - 8), 6), 3, 3);
        if (Field is not { } f || f.Base.Mixed || f.Rnd.Mixed)
            return;
        if (!IsMouseCaptured)
            _scale = Scale(f);
        double x0 = X(f.Base.Value), x1 = X(f.Base.Value + f.Rnd.Value);
        dc.DrawRectangle((Brush)FindResource("B.TextDim"), null, new Rect(X(0) - 0.5, y - 5, 1, 10));
        dc.DrawRoundedRectangle((Brush)FindResource("B.AccentPress"), null, new Rect(x0, y - 3, Math.Max(0, x1 - x0), 6), 3, 3);
        var handle = (Brush)FindResource("B.AccentHover");
        dc.DrawRoundedRectangle(handle, null, new Rect(x0 - 3, y - 6, 6, 12), 2, 2);
        dc.DrawRoundedRectangle(handle, null, new Rect(x1 - 3, y - 6, 6, 12), 2, 2);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (Field is not { } f || f.ReadOnly)
            return;
        _scale = Scale(f);
        double x = e.GetPosition(this).X;
        _rnd = Math.Abs(x - X(f.Base.Value + f.Rnd.Value)) < Math.Abs(x - X(f.Base.Value));
        _merge = "range:" + f.Key + ":" + (++_session).ToString(CultureInfo.InvariantCulture);
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!IsMouseCaptured || Field is not { } f || _merge == null)
            return;
        double frac = Math.Clamp((e.GetPosition(this).X - 4) / Math.Max(1, ActualWidth - 8), 0, 1);
        double v = ScrubLabel.Snap(-_scale + frac * 2 * _scale, f.Base.Step);
        if (_rnd)
            f.Rnd.SetFromDrag(_ => Math.Max(0, ScrubLabel.Snap(v - f.Base.Value, f.Rnd.Step)), _merge);
        else
            f.Base.SetFromDrag(_ => v, _merge);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!IsMouseCaptured)
            return;
        ReleaseMouseCapture();
        _merge = null;
        Field?.Owner.Doc?.Seal();
        InvalidateVisual();
    }
}

// Mini graph in the row: the engine's curve (uniform Catmull-Rom, clamped 0..1, P3) for value graphs, the colour
// gradient as the game evaluates it for Color graphs; parts the clamp cuts are marked red.
public sealed class GraphThumb : FrameworkElement
{
    public static readonly DependencyProperty FieldProperty = DependencyProperty.Register(nameof(Field), typeof(GraphField), typeof(GraphThumb),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    // Bound to the field's numbers: a new array after every refresh makes the thumbnail draw again.
    public static readonly DependencyProperty VersionProperty = DependencyProperty.Register(nameof(Version), typeof(object), typeof(GraphThumb),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public GraphField? Field { get => (GraphField?)GetValue(FieldProperty); set => SetValue(FieldProperty, value); }
    public object? Version { get => GetValue(VersionProperty); set => SetValue(VersionProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRoundedRectangle((Brush)FindResource("B.BgInput"), new Pen((Brush)FindResource("B.Line"), 1), new Rect(0, 0, w, h), 3, 3);
        if (Field is not { } f || w < 8)
            return;
        var g = f.Curve;
        const int Samples = 48;
        if (f.IsColor)
        {
            for (int s = 0; s < Samples; s++)
            {
                float t = (s + 0.5f) / Samples;
                var c = Color.FromRgb(B(g.Evaluate(t, 0)), B(g.Evaluate(t, 1)), B(g.Evaluate(t, 2)));
                dc.DrawRectangle(new SolidColorBrush(c), null, new Rect(2 + s * (w - 4) / Samples, 2, (w - 4) / Samples + 0.6, h - 4));
            }
            return;
        }
        string key = f.Key;
        var stroke = (Brush)FindResource(key.StartsWith("Size", StringComparison.Ordinal) ? "B.CurveSize"
            : key.StartsWith("Rotation", StringComparison.Ordinal) ? "B.CurveRot" : key.StartsWith("BRate", StringComparison.Ordinal) ? "B.Accent" : "B.CurveA");
        var geo = new StreamGeometry();
        var clipped = new List<Point>();
        using (var ctx = geo.Open())
        {
            for (int s = 0; s <= Samples; s++)
            {
                float t = s / (float)Samples;
                float u = g.Unclamped(t);
                var p = new Point(3 + t * (w - 6), h - 3 - Math.Clamp(u, 0, 1) * (h - 6));
                if (s == 0)
                    ctx.BeginFigure(p, false, false);
                else
                    ctx.LineTo(p, true, true);
                if (u < -0.0005f || u > 1.0005f)
                    clipped.Add(p);
            }
        }
        geo.Freeze();
        dc.DrawGeometry(null, new Pen(stroke, 1.4), geo);
        var err = (Brush)FindResource("B.Err");
        foreach (var p in clipped)
            dc.DrawEllipse(err, null, p, 1.3, 1.3);
    }

    private static byte B(float v) => (byte)Math.Round(Math.Clamp(v, 0f, 1f) * 255);
}
