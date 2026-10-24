using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Pfx.App.Textures;

// The picked tile as the game lays it on the sprite (R0, P4 shader): corner offsets are (±1 + CenterX) · ScaleX and
// (±1 + CenterY) · ScaleY half sizes from the particle, with texture v running down the screen. Drawn around the
// sprite's own centre, so the particle point (the pivot cross) sits where it lands on the picture whatever the scale:
// at u = (1 − CenterX) / 2, v = (1 − CenterY) / 2. A click or drag inside moves the pivot there; the right edge
// handle sets ScaleX, the top edge handle ScaleY. One drag is one undo step (SlicerModel merges it).
public sealed class SpriteCanvas : FrameworkElement
{
    private const double Inner = 14;
    // Handles are grabbed within this many pixels of an edge.
    private const double Grab = 7;

    private enum Mode { None, Pivot, ScaleX, ScaleY }

    private Mode _mode;
    private Brush? _checker;

    public SpriteCanvas()
    {
        ClipToBounds = true;
        Cursor = Cursors.Cross;
    }

    public SlicerModel? Model { get; set; }

    // Half the side of the sprite at scale 1, in pixels.
    private double Half => Math.Max(4, Math.Min(ActualWidth, ActualHeight) / 2 - Inner);
    private Point Centre => new(ActualWidth / 2, ActualHeight / 2);

    private Rect Quad(SlicerModel m)
    {
        double hw = m.ScaleX * Half, hh = m.ScaleY * Half;
        return new Rect(Centre.X - hw, Centre.Y - hh, 2 * hw, 2 * hh);
    }

    public Point Pivot(SlicerModel m) => new(Centre.X - m.CenterX * m.ScaleX * Half, Centre.Y - m.CenterY * m.ScaleY * Half);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(_checker ??= SheetBitmaps.Checker(this), null, new Rect(0, 0, ActualWidth, ActualHeight));
        var m = Model;
        if (m?.Emitter == null)
            return;
        var q = Quad(m);
        var unit = new Rect(Centre.X - Half, Centre.Y - Half, 2 * Half, 2 * Half);
        var dash = new Pen((Brush)FindResource("B.LineStrong"), 1) { DashStyle = DashStyles.Dash };
        dc.DrawRectangle(null, dash, unit);
        if (m.Levels is { Length: > 0 } levels)
        {
            int tile = m.Selected;
            var brush = new ImageBrush(SheetBitmaps.Of(levels[0], m.View))
            {
                ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
                Viewbox = new Rect((double)(tile % m.TileU) / m.TileU, (double)(tile / m.TileU) / m.TileV, 1.0 / m.TileU, 1.0 / m.TileV),
                Stretch = Stretch.Fill,
            };
            dc.DrawRectangle(brush, null, q);
        }
        var accent = (Brush)FindResource("B.Accent");
        dc.DrawRectangle(null, new Pen(accent, 1), q);
        dc.DrawRoundedRectangle(accent, null, new Rect(q.Right - 3, q.Y + q.Height / 2 - 7, 6, 14), 2, 2);
        dc.DrawRoundedRectangle(accent, null, new Rect(q.X + q.Width / 2 - 7, q.Y - 3, 14, 6), 2, 2);
        var p = Pivot(m);
        var path = new Pen((Brush)FindResource("B.Path"), 1.5);
        dc.DrawEllipse(null, path, p, 10, 10);
        dc.DrawLine(path, new Point(p.X - 15, p.Y), new Point(p.X + 15, p.Y));
        dc.DrawLine(path, new Point(p.X, p.Y - 15), new Point(p.X, p.Y + 15));
    }

    private Mode ModeAt(SlicerModel m, Point at)
    {
        var q = Quad(m);
        if (Math.Abs(at.X - q.Right) <= Grab && at.Y >= q.Top && at.Y <= q.Bottom)
            return Mode.ScaleX;
        if (Math.Abs(at.Y - q.Top) <= Grab && at.X >= q.Left && at.X <= q.Right)
            return Mode.ScaleY;
        return Mode.Pivot;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (Model is not { Editable: true })
            return;
        CaptureMouse();
        Press(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (IsMouseCaptured)
            Drag(e.GetPosition(this));
        else if (Model != null)
            Cursor = ModeAt(Model, e.GetPosition(this)) switch { Mode.ScaleX => Cursors.SizeWE, Mode.ScaleY => Cursors.SizeNS, _ => Cursors.Cross };
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!IsMouseCaptured)
            return;
        ReleaseMouseCapture();
        Release();
    }

    // The mouse handlers call these three; the shell checks call them too (no input is sent to the desktop).
    public void Press(Point at)
    {
        if (Model is not { } m)
            return;
        _mode = ModeAt(m, at);
        Drag(at);
    }

    public void Drag(Point at)
    {
        var m = Model;
        if (m == null)
            return;
        switch (_mode)
        {
            case Mode.Pivot:
                m.SetPivot((Centre.X - at.X) / (m.ScaleX * Half), (Centre.Y - at.Y) / (m.ScaleY * Half), true);
                break;
            case Mode.ScaleX:
                m.SetScale(Math.Abs(at.X - Centre.X) / Half, m.ScaleY, true);
                break;
            case Mode.ScaleY:
                m.SetScale(m.ScaleX, Math.Abs(Centre.Y - at.Y) / Half, true);
                break;
        }
    }

    public void Release()
    {
        _mode = Mode.None;
        Model?.EndDrag();
    }

    public Point HandleRight(SlicerModel m) => new(Quad(m).Right, Centre.Y);
    public Point HandleTop(SlicerModel m) => new(Centre.X, Quad(m).Top);
}
