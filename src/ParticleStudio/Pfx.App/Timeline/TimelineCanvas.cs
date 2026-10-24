using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Pfx.App.Timeline;

// The timeline of the bottom dock (spec 3.6, board 9's timeline tab): a ruler in seconds, the tracks of TimelineModel
// under it with their names on the left, the playhead and the work area. Drag on the ruler or the tracks to scrub
// (the shell seeks exactly, off the UI thread); Shift+drag on the ruler sets the work area; double-click a state to
// loop it; click a part or emitter name to select it. Wheel scrolls the tracks, Ctrl+wheel zooms the time around the
// cursor.
public sealed class TimelineCanvas : FrameworkElement
{
    public static readonly DependencyProperty PlayheadProperty = DependencyProperty.Register(nameof(Playhead), typeof(double), typeof(TimelineCanvas),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public const double LabelWidth = 150, RulerHeight = 20, RowHeight = 20, RightPad = 8;

    private enum Gesture { None, Scrub, Area }

    private Gesture _gesture;
    private double _areaFrom;
    private double _scroll;

    public TimelineCanvas()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
    }

    public IReadOnlyList<TimelineTrack> Tracks { get; set; } = Array.Empty<TimelineTrack>();
    public double ViewFrom { get; set; }
    public double ViewTo { get; set; } = 10;
    public (double From, double To)? Area { get; set; }
    public bool Looping { get; set; }

    public double Playhead { get => (double)GetValue(PlayheadProperty); set => SetValue(PlayheadProperty, value); }

    // Scrub to a time; set the work area; loop a state's range; select a track's emitter or part.
    public Action<double>? Scrubbed { get; set; }
    public Action? ScrubEnded { get; set; }
    public Action<double, double>? AreaSet { get; set; }
    public Action<TimelineTrack>? TrackPicked { get; set; }

    private double Width0 => Math.Max(1, ActualWidth - LabelWidth - RightPad);

    public double X(double t) => LabelWidth + (t - ViewFrom) / Math.Max(1e-6, ViewTo - ViewFrom) * Width0;

    public double TimeAt(double x) => Math.Max(0, ViewFrom + (x - LabelWidth) / Width0 * (ViewTo - ViewFrom));

    private Brush Res(string key) => (Brush)FindResource(key);

    private void Text(DrawingContext dc, string text, Point at, Brush brush, double size = 10, double maxWidth = 0)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface((FontFamily)FindResource("Font.Ui"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        if (maxWidth > 0)
        {
            ft.MaxTextWidth = maxWidth;
            ft.MaxLineCount = 1;
            ft.Trimming = TextTrimming.CharacterEllipsis;
        }
        dc.DrawText(ft, at);
    }

    // A ruler step of 1, 2 or 5 times a power of ten that leaves at least 60 px between labels.
    private double Step()
    {
        double perPx = (ViewTo - ViewFrom) / Width0;
        double raw = perPx * 60;
        double p = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        return raw <= p ? p : raw <= 2 * p ? 2 * p : raw <= 5 * p ? 5 * p : 10 * p;
    }

    private double MaxScroll => Math.Max(0, Tracks.Count * RowHeight - (ActualHeight - RulerHeight));

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Res("B.BgPanel"), null, new Rect(0, 0, w, h));
        if (w < LabelWidth + 40)
            return;
        var line = new Pen(Res("B.LineSubtle"), 1);
        var dim = Res("B.TextOff");
        dc.DrawRectangle(Res("B.BgRaised"), null, new Rect(0, 0, w, RulerHeight));
        if (Area is { } a)
        {
            var area = Res("B.Accent").Clone();
            area.Opacity = 0.14;
            dc.DrawRectangle(area, null, new Rect(new Point(Math.Max(LabelWidth, X(a.From)), 0), new Point(Math.Min(w - RightPad, X(a.To)), h)));
            var edge = new Pen(Res("B.Accent"), Looping ? 2 : 1);
            dc.DrawLine(edge, new Point(X(a.From), 0), new Point(X(a.From), RulerHeight));
            dc.DrawLine(edge, new Point(X(a.To), 0), new Point(X(a.To), RulerHeight));
        }
        double step = Step();
        for (double t = Math.Ceiling(ViewFrom / step) * step; t <= ViewTo + 1e-9; t += step)
        {
            double x = X(t);
            dc.DrawLine(line, new Point(x, RulerHeight - 6), new Point(x, h));
            Text(dc, t.ToString("0.##", CultureInfo.InvariantCulture) + (t == 0 ? "" : " " + Loc.T("Unit.Seconds")), new Point(x + 3, 3), dim);
        }
        dc.DrawLine(new Pen(Res("B.Line"), 1), new Point(0, RulerHeight), new Point(w, RulerHeight));

        dc.PushClip(new RectangleGeometry(new Rect(0, RulerHeight, w, Math.Max(0, h - RulerHeight))));
        for (int i = 0; i < Tracks.Count; i++)
        {
            double y = RulerHeight + i * RowHeight - _scroll;
            if (y + RowHeight < RulerHeight || y > h)
                continue;
            var track = Tracks[i];
            dc.DrawLine(line, new Point(0, y + RowHeight), new Point(w, y + RowHeight));
            double indent = track.Kind == TrackKind.Emitter ? 18 : 8;
            var nameBrush = track.Kind switch { TrackKind.Part => Res("B.TextHi"), TrackKind.Script => Res("B.TextOff"), TrackKind.State => Res("B.Key"), _ => Res("B.Text") };
            Text(dc, track.Name, new Point(indent, y + 4), nameBrush, 11, LabelWidth - indent - 6);
            dc.PushClip(new RectangleGeometry(new Rect(LabelWidth, y, Width0, RowHeight)));
            foreach (var s in track.Spans)
            {
                var b = Res(s.Brush).Clone();
                b.Opacity = s.Opacity;
                var r = new Rect(new Point(X(s.From), y + 5), new Point(Math.Max(X(s.From) + 1, X(s.To)), y + RowHeight - 5));
                dc.DrawRoundedRectangle(b, null, r, 3, 3);
                if (s.Label != null && r.Width > 30)
                    Text(dc, s.Label, new Point(r.X + 4, y + 4), Res("B.TextHi"), 10, r.Width - 6);
            }
            foreach (var m in track.Marks)
                dc.DrawLine(new Pen(Res(m.Brush), 2), new Point(X(m.At), y + 3), new Point(X(m.At), y + RowHeight - 3));
            dc.Pop();
        }
        dc.Pop();
        dc.DrawLine(new Pen(Res("B.LineSubtle"), 1), new Point(LabelWidth, 0), new Point(LabelWidth, h));
        double px = X(Playhead);
        if (px >= LabelWidth && px <= w)
        {
            dc.DrawLine(new Pen(Res("B.Accent"), 1), new Point(px, 0), new Point(px, h));
            dc.DrawRoundedRectangle(Res("B.Accent"), null, new Rect(px - 5, 0, 10, 10), 2, 2);
        }
        if (MaxScroll > 0)
        {
            double view = h - RulerHeight, total = Tracks.Count * RowHeight;
            double thumb = Math.Max(16, view * view / total);
            double top = RulerHeight + (view - thumb) * _scroll / MaxScroll;
            dc.DrawRoundedRectangle(Res("B.LineStrong"), null, new Rect(w - 5, top, 3, thumb), 1.5, 1.5);
        }
    }

    private TimelineTrack? TrackAt(Point p)
    {
        int i = (int)Math.Floor((p.Y - RulerHeight + _scroll) / RowHeight);
        return p.Y > RulerHeight && i >= 0 && i < Tracks.Count ? Tracks[i] : null;
    }

    // The hovered span's or mark's words, else the track's.
    public string? TipAt(Point p)
    {
        if (p.Y <= RulerHeight)
            return p.X > LabelWidth ? Loc.T("Tip.Ruler") : null;
        if (TrackAt(p) is not { } track)
            return null;
        if (p.X <= LabelWidth)
            return track.Tip;
        double t = TimeAt(p.X);
        double slack = (ViewTo - ViewFrom) / Width0 * 4;
        if (track.Marks.FirstOrDefault(m => Math.Abs(m.At - t) <= slack) is { } mark)
            return mark.Tip;
        return track.Spans.FirstOrDefault(s => t >= s.From && t <= s.To)?.Tip ?? track.Tip;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        var p = e.GetPosition(this);
        if (p.X <= LabelWidth)
        {
            if (TrackAt(p) is { } picked)
                TrackPicked?.Invoke(picked);
            return;
        }
        double t = TimeAt(p.X);
        if (e.ClickCount == 2 && TrackAt(p) is { Kind: TrackKind.State } st && st.Spans.FirstOrDefault(s => t >= s.From && t <= s.To) is { } span)
        {
            AreaSet?.Invoke(span.From, span.To);
            return;
        }
        CaptureMouse();
        if (p.Y <= RulerHeight && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            _gesture = Gesture.Area;
            _areaFrom = t;
        }
        else
        {
            _gesture = Gesture.Scrub;
            Scrubbed?.Invoke(t);
        }
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        if (_gesture == Gesture.None)
        {
            string? tip = TipAt(p);
            if (!Equals(ToolTip, tip ?? Loc.T("Tip.Timeline")))
                ToolTip = tip ?? Loc.T("Tip.Timeline");
            return;
        }
        double t = TimeAt(p.X);
        if (_gesture == Gesture.Scrub)
            Scrubbed?.Invoke(t);
        else
        {
            Area = (Math.Min(_areaFrom, t), Math.Max(_areaFrom, t));
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_gesture == Gesture.Area && Area is { } a && a.To - a.From > 1e-3)
            AreaSet?.Invoke(a.From, a.To);
        if (_gesture == Gesture.Scrub)
            ScrubEnded?.Invoke();
        _gesture = Gesture.None;
        ReleaseMouseCapture();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            double at = TimeAt(e.GetPosition(this).X);
            // One wheel notch (120) scales the shown time by 1.25.
            double k = Math.Pow(1.25, -e.Delta / 120.0);
            double from = Math.Max(0, at - (at - ViewFrom) * k), to = from + (ViewTo - ViewFrom) * k;
            if (to - from >= 0.05 && to - from <= 3600)
            {
                ViewFrom = from;
                ViewTo = to;
            }
        }
        else
        {
            _scroll = Math.Clamp(_scroll - e.Delta / 120.0 * RowHeight * 2, 0, MaxScroll);
        }
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _scroll = Math.Clamp(_scroll, 0, MaxScroll);
    }
}
