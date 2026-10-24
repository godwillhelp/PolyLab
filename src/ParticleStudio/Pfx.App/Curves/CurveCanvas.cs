using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Pfx.Sim;

namespace Pfx.App.Curves;

// The plot of the curve editor. Curves are the engine spline of Pfx.Sim.Graph: clamped to 0..1, with the parts the
// game cuts off shown in red and the stretches where the spline leaves the range of its two keys (under- and overshoot)
// shaded; the game's own read of the active curve is dashed over it where the two differ. Gestures follow the Workbench graph editor
// (W0): double-click adds a key, drag moves time and value with an x/y readout, Delete removes; plus box select,
// Shift to add to the selection, Ctrl+A / Ctrl+C / Ctrl+V, and a drawing mode whose stroke is baked to keys.
public sealed class CurveCanvas : FrameworkElement
{
    public static readonly DependencyProperty PlayheadProperty = DependencyProperty.Register(nameof(Playhead), typeof(double?), typeof(CurveCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    // Left room for the value labels, bottom room for the time labels; GradientStrip uses the same side margins.
    public const double Left = 34, Right = 10, Top = 8, Bottom = 18;
    // A key counts as hit within this many pixels of its centre (half the diamond's diagonal).
    private const double HitRadius = 7;

    private enum Gesture { None, Drag, Box, Draw }

    private Gesture _gesture;
    private Point _from;
    private Point _to;
    private (double T, double V)? _readout;
    private readonly List<Point> _stroke = new();

    public CurveCanvas()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        Cursor = Cursors.Cross;
    }

    public CurveEditor? Editor { get; set; }

    // Effect time 0..1 of the shown frame on master graphs (null = no playhead).
    public double? Playhead { get => (double?)GetValue(PlayheadProperty); set => SetValue(PlayheadProperty, value); }

    private Rect Plot => new(Left, Top, Math.Max(1, ActualWidth - Left - Right), Math.Max(1, ActualHeight - Top - Bottom));

    private double X(double t) => Plot.Left + t * Plot.Width;

    private double Y(double v) => Plot.Bottom - Math.Clamp(v, 0, CurveEditor.ViewTop) / CurveEditor.ViewTop * Plot.Height;

    private double T(double x) => Math.Clamp((x - Plot.Left) / Plot.Width, 0, 1);

    private double V(double y) => (Plot.Bottom - y) / Plot.Height * CurveEditor.ViewTop;

    private Brush Res(string key) => (Brush)FindResource(key);

    private static Pen Stroke(Brush b, double width, bool dashed = false)
    {
        var p = new Pen(b, width) { LineJoin = PenLineJoin.Round };
        if (dashed)
            p.DashStyle = new DashStyle(new[] { 4.0, 3.0 }, 0);
        p.Freeze();
        return p;
    }

    private void Label(DrawingContext dc, string text, Point at, Brush brush, bool right = false)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface((FontFamily)FindResource("Font.Mono"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            10, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(ft, right ? new Point(at.X - ft.Width, at.Y) : at);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var r = Plot;
        dc.DrawRectangle(Res("B.BgInput"), null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (Editor is not { } ed || ActualWidth < 60 || ActualHeight < 40)
            return;
        var minor = Stroke(Res("B.LineSubtle"), 1);
        var major = Stroke(Res("B.Line"), 1);
        var dim = Res("B.TextOff");
        for (int i = 0; i <= 20; i++)
            dc.DrawLine(i % 5 == 0 ? major : minor, new Point(X(i / 20.0), r.Top), new Point(X(i / 20.0), r.Bottom));
        var above = Res("B.Err").Clone();
        above.Opacity = 0.06;
        dc.DrawRectangle(above, null, new Rect(new Point(r.Left, Y(CurveEditor.ViewTop)), new Point(r.Right, Y(1))));
        foreach (double v in new[] { 0, 0.25, 0.5, 0.75, 1, 1.25 })
            dc.DrawLine(v == 1 ? Stroke(Res("B.ErrSoft"), 1) : v == 0 ? major : minor, new Point(r.Left, Y(v)), new Point(r.Right, Y(v)));
        foreach (double v in new[] { 0, 0.5, 1 })
            Label(dc, ed.Format(v), new Point(Left - 4, Y(v) - 7), v == 1 ? Res("B.Err") : dim, right: true);
        string unit = ed.IsMaster ? Loc.T(ed.Seconds ? "Curve.UnitSeconds" : "Curve.UnitEffect") : Loc.T("Curve.UnitLife");
        foreach (double t in new[] { 0, 0.25, 0.5, 0.75, 1 })
        {
            string text = ed.Format(t * ed.TimeScale) + (t == 1 ? " " + unit : "");
            Label(dc, text, new Point(X(t) + (t == 1 ? 0 : 2), r.Bottom + 2), dim, right: t == 1);
        }
        if (ed.Emitter == null)
            return;

        int samples = (int)Math.Clamp(r.Width / 2, 100, 600);
        var layers = ed.Layers;
        for (int pass = 0; pass < 2; pass++)
            for (int li = 0; li < layers.Count; li++)
            {
                bool active = li == ed.ActiveIndex;
                if (active != (pass == 1) || !ed.IsShown(li))
                    continue;
                var layer = layers[li];
                var g = ed.Curve(layer);
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                    for (int s = 0; s <= samples; s++)
                    {
                        double t = s / (double)samples;
                        var p = new Point(X(t), Y(g.Evaluate((float)t, layer.Channel)));
                        if (s == 0)
                            ctx.BeginFigure(p, false, false);
                        else
                            ctx.LineTo(p, true, true);
                    }
                geo.Freeze();
                dc.DrawGeometry(null, Stroke(Res(layer.Brush), active ? 2 : 1.2, !active), geo);
                if (active)
                {
                    DrawActive(dc, ed, layer, g, samples);
                    DrawGameRead(dc, layer, g, samples);
                }
            }

        if (Playhead is double ph && ed.IsMaster)
            dc.DrawLine(Stroke(Res("B.Accent"), 1), new Point(X(ph), r.Top), new Point(X(ph), r.Bottom));
        if (_gesture == Gesture.Box)
        {
            var fill = Res("B.Accent").Clone();
            fill.Opacity = 0.08;
            dc.DrawRectangle(fill, Stroke(Res("B.Accent"), 1, true), new Rect(_from, _to));
        }
        if (_gesture == Gesture.Draw && _stroke.Count > 1)
        {
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(_stroke[0], false, false);
                ctx.PolyLineTo(_stroke.Skip(1).ToList(), true, true);
            }
            geo.Freeze();
            dc.DrawGeometry(null, Stroke(Res("B.Accent"), 1.5), geo);
        }
        if (_readout is { } ro)
        {
            string text = Loc.F("Curve.Readout", ed.Format(ro.T * ed.TimeScale), ed.Format(ro.V));
            var at = new Point(Math.Min(X(ro.T) + 10, r.Right - 90), Math.Max(r.Top, Y(ro.V) - 22));
            dc.DrawRoundedRectangle(Res("B.BgDeep"), Stroke(Res("B.Line"), 1), new Rect(at, new Size(88, 16)), 3, 3);
            Label(dc, text, new Point(at.X + 5, at.Y + 1), Res("B.TextHi"));
        }
    }

    // What the game reads of the curve (CG8), dashed, where it is half a pixel or more away from the spline: the 32
    // samples of the CPU-read graphs (Graph.ReadOnCpu), else the GPU's 8-bit texels.
    private void DrawGameRead(DrawingContext dc, CurveLayer layer, Graph g, int samples)
    {
        bool cpu = Graph.ReadOnCpu(layer.Key);
        var points = new Point[samples + 1];
        double apart = 0;
        for (int s = 0; s <= samples; s++)
        {
            float t = s / (float)samples;
            float v = cpu ? g.EvaluateCpu(t, layer.Channel) : g.EvaluateGpu(t, layer.Channel);
            points[s] = new Point(X(t), Y(v));
            apart = Math.Max(apart, Math.Abs(points[s].Y - Y(g.Evaluate(t, layer.Channel))));
        }
        if (apart < 0.5)
            return;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(points[0], false, false);
            ctx.PolyLineTo(points.Skip(1).ToList(), true, true);
        }
        geo.Freeze();
        var warn = Res("B.Warn");
        dc.DrawGeometry(null, Stroke(warn, 1.5, true), geo);
        Label(dc, Loc.T("Curve.GameRead"), new Point(Plot.Right - 4, Plot.Top + 2), warn, right: true);
    }

    private void DrawActive(DrawingContext dc, CurveEditor ed, CurveLayer layer, Graph g, int samples)
    {
        var keys = ed.Keys(layer);
        int ch = layer.Channel + 1;
        var shade = Res("B.Key").Clone();
        shade.Opacity = 0.22;
        var err = Res("B.Err");
        var shadeGeo = new StreamGeometry();
        var cutGeo = new StreamGeometry();
        using (var sh = shadeGeo.Open())
        using (var cut = cutGeo.Open())
        {
            double dx = Plot.Width / samples;
            for (int s = 0; s <= samples; s++)
            {
                double t = s / (double)samples;
                double u = g.Unclamped((float)t, layer.Channel);
                int k = keys.FindLastIndex(x => x.Time <= t);
                if (k >= 0 && k + 1 < keys.Count)
                {
                    double a = keys[k].Values[ch], b = keys[k + 1].Values[ch];
                    double lo = Math.Min(a, b), hi = Math.Max(a, b);
                    double bound = u < lo ? lo : u > hi ? hi : u;
                    if (Math.Abs(bound - u) > 1e-4)
                    {
                        double y0 = Y(bound), y1 = Y(u);
                        sh.BeginFigure(new Point(X(t) - dx / 2, Math.Min(y0, y1)), true, true);
                        sh.PolyLineTo(new[] { new Point(X(t) + dx / 2, Math.Min(y0, y1)), new Point(X(t) + dx / 2, Math.Max(y0, y1)),
                            new Point(X(t) - dx / 2, Math.Max(y0, y1)) }, false, false);
                    }
                }
                if (u > 1.0005 || u < -0.0005)
                {
                    double y = Y(Math.Clamp(u, 0, 1));
                    cut.BeginFigure(new Point(X(t) - dx / 2, y), false, false);
                    cut.LineTo(new Point(X(t) + dx / 2, y), true, false);
                }
            }
        }
        shadeGeo.Freeze();
        cutGeo.Freeze();
        dc.DrawGeometry(shade, null, shadeGeo);
        dc.DrawGeometry(null, Stroke(err, 3), cutGeo);
        for (int i = 0; i < keys.Count; i++)
        {
            double v = keys[i].Values[ch];
            bool selected = ed.Selection.Contains(i);
            var c = new Point(X(keys[i].Time), Y(v));
            var diamond = new StreamGeometry();
            using (var ctx = diamond.Open())
            {
                ctx.BeginFigure(new Point(c.X, c.Y - 6), true, true);
                ctx.PolyLineTo(new[] { new Point(c.X + 6, c.Y), new Point(c.X, c.Y + 6), new Point(c.X - 6, c.Y) }, true, true);
            }
            diamond.Freeze();
            var fill = v > 1 || v < 0 ? err : selected ? Res("B.Accent") : Res("B.Key");
            dc.DrawGeometry(fill, Stroke(selected ? Res("B.TextHi") : Res("B.BgDeep"), 1.4), diamond);
        }
    }

    // ---- gestures ----

    private int HitKey(Point p)
    {
        if (Editor is not { } ed)
            return -1;
        var keys = ed.Keys(ed.Active);
        int ch = ed.Active.Channel + 1, best = -1;
        double bestD = HitRadius;
        for (int i = 0; i < keys.Count; i++)
        {
            double d = (new Point(X(keys[i].Time), Y(keys[i].Values[ch])) - p).Length;
            if (d < bestD)
            {
                bestD = d;
                best = i;
            }
        }
        return best;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        if (Editor is not { } ed || ed.Emitter == null)
            return;
        var p = e.GetPosition(this);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (e.ClickCount == 2 && !ed.Drawing)
        {
            if (HitKey(p) < 0)
                ed.AddKeyAt(T(p.X), V(p.Y));
            e.Handled = true;
            return;
        }
        _from = _to = p;
        CaptureMouse();
        if (ed.Drawing)
        {
            _gesture = Gesture.Draw;
            _stroke.Clear();
            _stroke.Add(p);
        }
        else if (HitKey(p) is var hit and >= 0)
        {
            if (!ed.Selection.Contains(hit) || shift)
                ed.Select(hit, shift);
            _gesture = Gesture.Drag;
            ed.BeginDrag();
            var key = ed.Keys(ed.Active)[hit];
            _readout = (key.Time, key.Values[ed.Active.Channel + 1]);
        }
        else
        {
            _gesture = Gesture.Box;
            if (!shift)
                ed.ClearSelection();
        }
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_gesture == Gesture.None || Editor is not { } ed)
            return;
        _to = e.GetPosition(this);
        switch (_gesture)
        {
            case Gesture.Drag:
                _readout = ed.DragTo((_to.X - _from.X) / Plot.Width, (_from.Y - _to.Y) / Plot.Height * CurveEditor.ViewTop) ?? _readout;
                break;
            case Gesture.Draw:
                if ((_to - _stroke[^1]).Length >= 2)
                    _stroke.Add(_to);
                break;
        }
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (Editor is { } ed)
        {
            switch (_gesture)
            {
                case Gesture.Drag:
                    ed.EndDrag();
                    break;
                case Gesture.Box:
                    if ((_to - _from).Length > 3)
                        ed.SelectBox(T(_from.X), V(_from.Y), T(_to.X), V(_to.Y), Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                    break;
                case Gesture.Draw:
                    ed.ApplyStroke(_stroke.Select(p => (T(p.X), Math.Max(0, V(p.Y)))).ToList());
                    break;
            }
        }
        _gesture = Gesture.None;
        _readout = null;
        _stroke.Clear();
        ReleaseMouseCapture();
        InvalidateVisual();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_gesture == Gesture.Drag)
            Editor?.EndDrag();
        _gesture = Gesture.None;
        _readout = null;
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Editor is not { } ed)
            return;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (e.Key is Key.Delete or Key.Back)
            ed.DeleteSelected();
        else if (ctrl && e.Key == Key.A)
            ed.SelectAll();
        else if (ctrl && e.Key == Key.C)
            ed.Copy();
        else if (ctrl && e.Key == Key.V)
            ed.Paste();
        else
            return;
        e.Handled = true;
    }
}
