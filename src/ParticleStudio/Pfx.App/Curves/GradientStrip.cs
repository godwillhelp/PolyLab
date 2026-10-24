using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Pfx.App.Curves;

// Under the curve plot for colour and alpha graphs (spec 3.6, board 11): the colour graph as the engine evaluates it
// (a bar), its alpha graph on a checkerboard (a second bar), and their keys as stops. Click a stop to pick it, drag it
// along the bar to move its time, double-click a bar to add a stop with the colour the curve has there, double-click a
// stop to set its colour (ColorPopup). A stop with a channel above 1 or below 0 has a red border: the game clamps it.
public sealed class GradientStrip : FrameworkElement
{
    private const double ColorTop = 8, BarHeight = 9, AlphaTop = 20, StopSize = 6;
    // A stop counts as hit within this many pixels of its centre.
    private const double HitRadius = 6;

    private (bool Alpha, int Key)? _drag;
    private string? _merge;
    private int _drags;

    public GradientStrip()
    {
        Height = 36;
        Cursor = Cursors.Hand;
        Focusable = true;
        FocusVisualStyle = null;
    }

    public CurveEditor? Editor { get; set; }
    // Opens the colour popup for a stop (the view places it).
    public Action<bool, int, Point>? EditStop { get; set; }

    private double Width0 => Math.Max(1, ActualWidth - CurveCanvas.Left - CurveCanvas.Right);

    private double X(double t) => CurveCanvas.Left + t * Width0;

    private double T(double x) => Math.Clamp((x - CurveCanvas.Left) / Width0, 0, 1);

    private static byte B(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle((Brush)FindResource("B.BgPanel"), null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (Editor is not { Emitter: not null } ed || ActualWidth < 60)
            return;
        var colour = ed.Curve(ed.ColorLayer);
        var alpha = ed.Curve(ed.AlphaLayer);
        double w = Width0;
        int cells = (int)Math.Clamp(w / 3, 32, 400);
        var check = new DrawingBrush(new GeometryDrawing((Brush)FindResource("B.LineStrong"), null,
            Geometry.Parse("M0,0 H4 V4 H0Z M4,4 H8 V8 H4Z"))) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 8, 8), ViewportUnits = BrushMappingMode.Absolute };
        check.Freeze();
        dc.DrawRectangle((Brush)FindResource("B.TextOff"), null, new Rect(X(0), AlphaTop, w, BarHeight));
        dc.DrawRectangle(check, null, new Rect(X(0), AlphaTop, w, BarHeight));
        for (int s = 0; s < cells; s++)
        {
            float t = (s + 0.5f) / cells;
            var c = Color.FromRgb(B(colour.Evaluate(t, 0)), B(colour.Evaluate(t, 1)), B(colour.Evaluate(t, 2)));
            var cell = new Rect(X(s / (double)cells), ColorTop, w / cells + 0.6, BarHeight);
            dc.DrawRectangle(new SolidColorBrush(c), null, cell);
            var a = Color.FromArgb(B(alpha.Evaluate(t)), 0xE8, 0xEE, 0xF2);
            dc.DrawRectangle(new SolidColorBrush(a), null, new Rect(cell.X, AlphaTop, cell.Width, BarHeight));
        }
        var outline = new Pen((Brush)FindResource("B.Line"), 1);
        dc.DrawRectangle(null, outline, new Rect(X(0), ColorTop, w, BarHeight));
        dc.DrawRectangle(null, outline, new Rect(X(0), AlphaTop, w, BarHeight));
        bool colourActive = ed.Active.Key == ed.ColorLayer.Key, alphaActive = ed.Active.Key == ed.AlphaLayer.Key;
        DrawStops(dc, ed.Keys(ed.ColorLayer), false, colourActive ? ed.Selection : null);
        DrawStops(dc, ed.Keys(ed.AlphaLayer), true, alphaActive ? ed.Selection : null);
    }

    private void DrawStops(DrawingContext dc, List<Pfx.Core.Model.GraphKey> keys, bool alpha, IReadOnlySet<int>? selected)
    {
        var err = new Pen((Brush)FindResource("B.Err"), 2);
        var normal = new Pen((Brush)FindResource("B.BgDeep"), 1.2);
        var picked = new Pen((Brush)FindResource("B.Accent"), 2);
        for (int i = 0; i < keys.Count; i++)
        {
            var v = keys[i].Values;
            bool clamped = v.Skip(1).Any(x => x > 1 || x < 0);
            var fill = alpha
                ? new SolidColorBrush(Color.FromRgb(B(v[1]), B(v[1]), B(v[1])))
                : new SolidColorBrush(Color.FromRgb(B(v[1]), B(v[2]), B(v[3])));
            double y = alpha ? AlphaTop + BarHeight + 1 : ColorTop - StopSize - 1;
            var rect = new Rect(X(keys[i].Time) - StopSize / 2, y, StopSize, StopSize);
            dc.DrawRectangle(fill, clamped ? err : selected?.Contains(i) == true ? picked : normal, rect);
        }
    }

    private (bool Alpha, int Key)? HitStop(Point p)
    {
        if (Editor is not { } ed)
            return null;
        foreach (bool alpha in new[] { false, true })
        {
            var keys = ed.Keys(alpha ? ed.AlphaLayer : ed.ColorLayer);
            double y = alpha ? AlphaTop + BarHeight + 1 + StopSize / 2 : ColorTop - 1 - StopSize / 2;
            for (int i = 0; i < keys.Count; i++)
                if (Math.Abs(X(keys[i].Time) - p.X) <= HitRadius && Math.Abs(y - p.Y) <= HitRadius + 2)
                    return (alpha, i);
        }
        return null;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Editor is not { Emitter: not null } ed)
            return;
        var p = e.GetPosition(this);
        var hit = HitStop(p);
        if (e.ClickCount == 2)
        {
            if (hit is { } h)
                EditStop?.Invoke(h.Alpha, h.Key, PointToScreen(p));
            else
                ed.AddStop(p.Y >= AlphaTop - 2, T(p.X));
            e.Handled = true;
            return;
        }
        Focus();
        if (hit is { } stop)
        {
            ed.PickStop(stop.Alpha, stop.Key);
            _drag = stop;
            _merge = "strip-drag:" + (++_drags).ToString(CultureInfo.InvariantCulture);
            CaptureMouse();
        }
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag is { } d && Editor is { } ed && _merge != null)
            ed.MoveStop(d.Alpha, d.Key, T(e.GetPosition(this).X), _merge);
    }

    // Delete removes the picked stop (a graph keeps at least one key).
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.Delete or Key.Back && Editor != null)
        {
            Editor.DeleteSelected();
            e.Handled = true;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_drag != null)
            Editor?.Seal();
        _drag = null;
        _merge = null;
        ReleaseMouseCapture();
    }
}
