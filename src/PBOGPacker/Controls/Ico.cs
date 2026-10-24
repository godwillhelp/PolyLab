using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace NDZ.PBOGPacker.Controls;

public sealed class Ico : FrameworkElement
{
    private enum Anim { None, Lid, KnobR, KnobL, Rot, Blink, Draw, Nudge, Drop }

    private sealed record Part(Geometry Geo, bool Fill, Anim Anim, double Len = 0);

    private static readonly Dictionary<string, Part[]> Lib = new();

    static Ico()
    {
        HorizontalAlignmentProperty.OverrideMetadata(typeof(Ico), new FrameworkPropertyMetadata(HorizontalAlignment.Center));
        VerticalAlignmentProperty.OverrideMetadata(typeof(Ico), new FrameworkPropertyMetadata(VerticalAlignment.Center));

        void Def(string name, params (string d, bool fill, Anim anim)[] parts)
        {
            Lib[name] = parts.Select(p =>
            {
                var g = Geometry.Parse(p.d);
                g.Freeze();
                var len = p.anim == Anim.Draw ? g.GetFlattenedPathGeometry().Figures.Sum(FigureLength) : 0;
                return new Part(g, p.fill, p.anim, len);
            }).ToArray();
        }

        Def("logo", ("M5,3.5 L5,20.5 M5,12 L10.5,12 L15,7 L19,7 M10.5,12 L15,17 L19,17", false, Anim.None),
            ("M17.5,5.5 h3 v3 h-3 z M17.5,15.5 h3 v3 h-3 z", true, Anim.Blink));
        Def("mods", ("M3.5,7.5 L12,3.5 L20.5,7.5 L20.5,16.5 L12,20.5 L3.5,16.5 Z M12,11.5 L12,20.5", false, Anim.None),
            ("M3.5,7.5 L12,11.5 L20.5,7.5", false, Anim.Lid));
        Def("servers", ("M3.5,4 h17 v7 h-17 z M3.5,13 h17 v7 h-17 z M13,7.5 h4 M13,16.5 h4", false, Anim.None),
            ("M6.3,6.3 h2.4 v2.4 h-2.4 z M6.3,15.3 h2.4 v2.4 h-2.4 z", true, Anim.Blink));
        Def("keys", ("M3.5,11.5 h8 v8 h-8 z M11.5,11.5 L20,3 M16.5,6.5 L19,9 M14.3,8.7 L16.3,10.7", false, Anim.Rot),
            ("M6.5,14.5 h2 v2 h-2 z", true, Anim.Rot));
        Def("env", ("M2.5,12 L6.5,12 L9,5.5 L14,18.5 L16.5,12 L21.5,12", false, Anim.Draw));
        Def("settings", ("M4,7 L20,7 M4,17 L20,17", false, Anim.None),
            ("M6,4.5 h4 v5 h-4 z", true, Anim.KnobR), ("M14,14.5 h4 v5 h-4 z", true, Anim.KnobL));
        Def("tower", ("M12,2.5 L12,5 M9,21.5 L11,5 L13,5 L15,21.5 M10.2,12 L13.8,12 M9.6,17 L14.4,17 M10.2,12 L14.4,17 M13.8,12 L9.6,17 M11,5 L13,8.5 M13,5 L11,8.5", false, Anim.None),
            ("M11,1 h2 v2 h-2 z", true, Anim.Blink), ("M5.5,6 C4,8 4,11 5.5,13 M18.5,6 C20,8 20,11 18.5,13", false, Anim.Blink));
        Def("check", ("M5,12.5 L9.5,17 L19,7.5", false, Anim.Draw));
        Def("x", ("M6.5,6.5 L17.5,17.5 M17.5,6.5 L6.5,17.5", false, Anim.None));
        Def("warn", ("M12,3.5 L21.5,20 L2.5,20 Z M12,9.5 L12,14.5", false, Anim.None), ("M11.1,16.3 h1.8 v1.8 h-1.8 z", true, Anim.Blink));
        Def("idle", ("M9,9 h6 v6 h-6 z", false, Anim.None));
        Def("skip", ("M7,12 L17,12", false, Anim.None));
        Def("stop", ("M7,7 h10 v10 h-10 z", true, Anim.None));
        Def("play", ("M8,5 L19,12 L8,19 Z", false, Anim.Nudge));
        Def("search", ("M10.5,4 A6.5,6.5 0 1 1 10.5,17 A6.5,6.5 0 1 1 10.5,4 Z M15.3,15.3 L20.5,20.5", false, Anim.Nudge));
        Def("grip", ("M8,5 h2.4 v2.4 h-2.4 z M13.6,5 h2.4 v2.4 h-2.4 z M8,10.8 h2.4 v2.4 h-2.4 z M13.6,10.8 h2.4 v2.4 h-2.4 z M8,16.6 h2.4 v2.4 h-2.4 z M13.6,16.6 h2.4 v2.4 h-2.4 z", true, Anim.None));
        Def("copy", ("M8.5,8.5 h11 v11 h-11 z M15.5,8.5 L15.5,4.5 L4.5,4.5 L4.5,15.5 L8.5,15.5", false, Anim.Nudge));
        Def("folder", ("M3.5,6 L9.5,6 L11.5,8.5 L20.5,8.5 L20.5,18.5 L3.5,18.5 Z", false, Anim.Lid));
        Def("deploy", ("M4,13.5 L4,20 L20,20 L20,13.5", false, Anim.None), ("M12,14.5 L12,4 M7.5,8.5 L12,4 L16.5,8.5", false, Anim.Drop));
        Def("pack", ("M4,10 L12,14 L20,10 M4,10 L4,18 L12,22 L20,18 L20,10", false, Anim.None), ("M12,2.5 L12,10.5 M8.8,7.5 L12,10.7 L15.2,7.5", false, Anim.Drop));
        Def("plus", ("M12,5 L12,19 M5,12 L19,12", false, Anim.Rot));
        Def("refresh", ("M19.5,12 A7.5,7.5 0 1 1 17.3,6.7 M19.5,3.5 L19.5,8 L15,8", false, Anim.Rot));
        Def("chevDown", ("M6,9.5 L12,15.5 L18,9.5", false, Anim.None));
        Def("chevRight", ("M9.5,6 L15.5,12 L9.5,18", false, Anim.Nudge));
        Def("arrow", ("M4.5,12 L18.5,12 M13,6.5 L18.5,12 L13,17.5", false, Anim.Nudge));
        Def("terminal", ("M3,4.5 h18 v15 h-18 z M12.5,15.5 L17,15.5", false, Anim.None), ("M7,10 L10,12.5 L7,15", false, Anim.Nudge));
        Def("shield", ("M12,3 L19.5,6 L19.5,12 C19.5,16.5 16.3,19.7 12,21 C7.7,19.7 4.5,16.5 4.5,12 L4.5,6 Z", false, Anim.None), ("M8.8,12 L11.1,14.3 L15.3,10", false, Anim.Draw));
        Def("cpu", ("M6.5,6.5 h11 v11 h-11 z M10,3 L10,6.5 M14,3 L14,6.5 M10,17.5 L10,21 M14,17.5 L14,21 M3,10 L6.5,10 M3,14 L6.5,14 M17.5,10 L21,10 M17.5,14 L21,14", false, Anim.None),
            ("M10,10 h4 v4 h-4 z", true, Anim.Blink));
        Def("download", ("M4,19.5 L20,19.5", false, Anim.None), ("M12,4 L12,15 M7.5,10.5 L12,15 L16.5,10.5", false, Anim.Drop));
        Def("flask", ("M9.5,3.5 L14.5,3.5 M10.5,3.5 L10.5,9.5 L5,19.5 L19,19.5 L13.5,9.5 L13.5,3.5 M7.4,15 L16.6,15", false, Anim.None));
        Def("wrench", ("M14.5,3.5 A5,5 0 0 0 9.9,10.4 L3.5,16.8 L7.2,20.5 L13.6,14.1 A5,5 0 0 0 20.5,9.5 L17.5,12.5 L14.5,11.6 L13.6,8.6 Z", false, Anim.Rot));
        Def("fingerprint", ("M8,20 C9.5,18 10,15.5 10,12 A2,2 0 0 1 14,12 C14,16 13.5,18.5 12.5,20.5 M5,16.5 C5.7,14.9 6,13.5 6,12 A6,6 0 0 1 18,12 C18,14.2 17.8,16 17.4,17.5", false, Anim.Draw));
        Def("ext", ("M13.5,4.5 L19.5,4.5 L19.5,10.5 M19.5,4.5 L11.5,12.5 M17,14 L17,19.5 L4.5,19.5 L4.5,7 L10,7", false, Anim.Nudge));
        Def("power", ("M12,3 L12,11 M7,6.5 A7,7 0 1 0 17,6.5", false, Anim.None));
        Def("globe", ("M12,3.5 A8.5,8.5 0 1 0 12,20.5 A8.5,8.5 0 1 0 12,3.5 M3.5,12 L20.5,12 M12,3.5 C9,7 9,17 12,20.5 C15,17 15,7 12,3.5", false, Anim.Rot));
        Def("discord", ("M7.2,6.8 C9.6,5.7 14.4,5.7 16.8,6.8 C18.7,9.6 19.6,12.6 19.3,16 C17.9,17.1 16.5,17.8 15,18.2 L14.1,16.6 M9.9,16.6 L9,18.2 C7.5,17.8 6.1,17.1 4.7,16 C4.4,12.6 5.3,9.6 7.2,6.8 Z M8.2,15.6 C10.6,16.7 13.4,16.7 15.8,15.6", false, Anim.None),
            ("M8.9,11.4 h2 v2.2 h-2 z M13.1,11.4 h2 v2.2 h-2 z", true, Anim.Blink));
        Def("minus", ("M6,12 L18,12", false, Anim.None));
        Def("restore", ("M6.5,9.5 h8 v8 h-8 z", false, Anim.None), ("M9.5,9.5 L9.5,6.5 L17.5,6.5 L17.5,14.5 L14.5,14.5", false, Anim.Nudge));
        Def("square", ("M6.5,6.5 h11 v11 h-11 z", false, Anim.None));
        Def("close", ("M7,7 L17,17 M17,7 L7,17", false, Anim.None));
        Def("spinner", ("M12,4 A8,8 0 1 1 4,12", false, Anim.None), ("M12,4 A8,8 0 0 1 20,12", false, Anim.None));
    }

    private static double FigureLength(PathFigure f)
    {
        var len = 0.0;
        var p = f.StartPoint;
        foreach (var s in f.Segments)
        {
            if (s is PolyLineSegment pl)
                foreach (var q in pl.Points) { len += (q - p).Length; p = q; }
            else if (s is LineSegment l) { len += (l.Point - p).Length; p = l.Point; }
        }
        return len;
    }

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(string), typeof(Ico),
        new FrameworkPropertyMetadata("idle", FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((Ico)d).OnKindChanged()));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(Ico),
        new FrameworkPropertyMetadata(18.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(Ico),
        new FrameworkPropertyMetadata(1.6, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HotProperty = DependencyProperty.Register(
        nameof(Hot), typeof(bool), typeof(Ico), new PropertyMetadata(false, (d, e) => ((Ico)d).OnHot((bool)e.NewValue)));

    public static readonly DependencyProperty HotTProperty = DependencyProperty.Register(
        nameof(HotT), typeof(double), typeof(Ico), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DrawTProperty = DependencyProperty.Register(
        nameof(DrawT), typeof(double), typeof(Ico), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BlinkTProperty = DependencyProperty.Register(
        nameof(BlinkT), typeof(double), typeof(Ico), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AnimateInProperty = DependencyProperty.Register(
        nameof(AnimateIn), typeof(bool), typeof(Ico), new PropertyMetadata(true));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(Ico),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    public bool Hot { get => (bool)GetValue(HotProperty); set => SetValue(HotProperty, value); }
    public double HotT { get => (double)GetValue(HotTProperty); set => SetValue(HotTProperty, value); }
    public double DrawT { get => (double)GetValue(DrawTProperty); set => SetValue(DrawTProperty, value); }
    public double BlinkT { get => (double)GetValue(BlinkTProperty); set => SetValue(BlinkTProperty, value); }
    public bool AnimateIn { get => (bool)GetValue(AnimateInProperty); set => SetValue(AnimateInProperty, value); }

    private readonly RotateTransform _spin = new(0, 0, 0);

    public Ico()
    {
        Loaded += (_, _) => { OnKindChanged(); };
        Unloaded += (_, _) => _spin.BeginAnimation(RotateTransform.AngleProperty, null);
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
    }

    private static readonly Duration Fast = new(TimeSpan.FromMilliseconds(180));
    private static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };

    private void OnKindChanged()
    {
        if (!IsLoaded) return;
        if (Kind == "spinner")
        {
            RenderTransform = _spin;
            _spin.CenterX = Size / 2;
            _spin.CenterY = Size / 2;
            _spin.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, new Duration(TimeSpan.FromMilliseconds(800))) { RepeatBehavior = RepeatBehavior.Forever });
        }
        else
        {
            _spin.BeginAnimation(RotateTransform.AngleProperty, null);
            RenderTransform = Transform.Identity;
        }
        if (AnimateIn && (Kind == "check" || Kind == "shield"))
            BeginAnimation(DrawTProperty, new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(220))) { EasingFunction = Ease });
    }

    private void OnHot(bool hot)
    {
        BeginAnimation(HotTProperty, new DoubleAnimation(hot ? 1 : 0, Fast) { EasingFunction = Ease });
        if (!hot) return;
        if (Lib.TryGetValue(Kind, out var parts))
        {
            if (parts.Any(p => p.Anim == Anim.Blink))
                BeginAnimation(BlinkTProperty, new DoubleAnimationUsingKeyFrames
                {
                    KeyFrames =
                    {
                        new DiscreteDoubleKeyFrame(0.15, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(0))),
                        new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120))),
                        new DiscreteDoubleKeyFrame(0.15, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(240))),
                        new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(360))),
                    }
                });
            if (parts.Any(p => p.Anim == Anim.Draw))
                BeginAnimation(DrawTProperty, new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(260))) { EasingFunction = Ease });
        }
    }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    protected override void OnRender(DrawingContext dc)
    {
        if (!Lib.TryGetValue(Kind ?? "idle", out var parts)) return;
        var brush = Foreground;
        var k = Size / 24.0;
        var th = StrokeThickness;
        dc.PushTransform(new ScaleTransform(k, k));
        var t = HotT;
        for (var i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            Transform? tr = p.Anim switch
            {
                Anim.Lid => new TranslateTransform(0, -1.6 * t),
                Anim.KnobR => new TranslateTransform(5 * t, 0),
                Anim.KnobL => new TranslateTransform(-5 * t, 0),
                Anim.Rot => new RotateTransform(-14 * t, 12, 12),
                Anim.Nudge => new TranslateTransform(2 * t, 0),
                Anim.Drop => new TranslateTransform(0, 1.8 * t),
                _ => null
            };
            if (tr != null) dc.PushTransform(tr);
            var opacity = p.Anim == Anim.Blink ? BlinkT : 1.0;
            if (Kind == "spinner" && i == 0) opacity = 0.25;
            if (opacity < 1) dc.PushOpacity(opacity);
            if (p.Fill)
            {
                dc.DrawGeometry(brush, null, p.Geo);
            }
            else
            {
                var pen = new Pen(brush, th) { StartLineCap = PenLineCap.Square, EndLineCap = PenLineCap.Square, LineJoin = PenLineJoin.Miter };
                if (p.Anim == Anim.Draw && DrawT < 1)
                {
                    var visible = p.Len * DrawT / th;
                    pen.DashStyle = new DashStyle(new[] { Math.Max(0.001, visible), p.Len / th + 10 }, 0);
                    pen.DashCap = PenLineCap.Flat;
                    pen.StartLineCap = pen.EndLineCap = PenLineCap.Flat;
                }
                dc.DrawGeometry(null, pen, p.Geo);
            }
            if (opacity < 1) dc.Pop();
            if (tr != null) dc.Pop();
        }
        dc.Pop();
    }
}
