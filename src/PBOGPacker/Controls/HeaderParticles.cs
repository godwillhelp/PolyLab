using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace NDZ.PBOGPacker.Controls;

public sealed class HeaderParticles : FrameworkElement
{
    private struct Mote
    {
        public double X, Y, Vx, Vy, Size, Phase, Twinkle, Alpha;
        public bool Warm;
    }

    private struct Electron
    {
        public double Angle, Speed, Tilt, Scale, Size, Phase;
    }

    private readonly List<Mote> _motes = new();
    private readonly List<Electron> _electrons = new();
    private readonly Random _rng = new(163451);
    private readonly Stopwatch _clock = new();
    private double _last;
    private double _nextSweep = 2.5;
    private double _sweepStart = -10;
    private double _nextPulse = 1.2;
    private readonly List<double> _pulses = new();
    private bool _running;

    private Rect _orbit;
    private bool _orbitSet;
    private double _orbitAlpha;

    private static readonly Color Bone = Color.FromRgb(0xE4, 0xDA, 0xB4);
    private static readonly Color Ember = Color.FromRgb(0xDA, 0x7A, 0x2C);
    private static readonly Color Rad = Color.FromRgb(0xD6, 0xF5, 0x86);
    private static readonly Brush EmberGlow = MakeGlow(Color.FromArgb(0x70, 0xDA, 0x7A, 0x2C));
    private static readonly Brush RadGlow = MakeGlow(Color.FromArgb(0x80, 0x9C, 0xE0, 0x4E));

    public static readonly DependencyProperty PulseOriginProperty = DependencyProperty.Register(
        nameof(PulseOrigin), typeof(Point?), typeof(HeaderParticles), new PropertyMetadata(null));

    public static readonly DependencyProperty OrbitTargetProperty = DependencyProperty.Register(
        nameof(OrbitTarget), typeof(Rect?), typeof(HeaderParticles), new PropertyMetadata(null));

    public Point? PulseOrigin
    {
        get => (Point?)GetValue(PulseOriginProperty);
        set => SetValue(PulseOriginProperty, value);
    }

    public Rect? OrbitTarget
    {
        get => (Rect?)GetValue(OrbitTargetProperty);
        set => SetValue(OrbitTargetProperty, value);
    }

    public HeaderParticles()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        Loaded += (_, _) => Start();
        Unloaded += (_, _) => Stop();
        IsVisibleChanged += (_, e) => { if (e.NewValue is true) Start(); else Stop(); };
        SizeChanged += (_, _) => Populate();

        var tilts = new[] { -11.0, 0.0, 11.0 };
        for (var plane = 0; plane < tilts.Length; plane++)
            for (var i = 0; i < 4; i++)
                _electrons.Add(new Electron
                {
                    Angle = i * Math.PI / 2 + plane * 0.7 + _rng.NextDouble() * 0.4,
                    Speed = (1.3 + _rng.NextDouble() * 1.1) * (plane % 2 == 0 ? 1 : -1),
                    Tilt = tilts[plane] * Math.PI / 180,
                    Scale = 0.92 + _rng.NextDouble() * 0.16,
                    Size = 1.3 + _rng.NextDouble() * 0.9,
                    Phase = _rng.NextDouble() * Math.PI * 2,
                });
    }

    private static Brush MakeGlow(Color c)
    {
        var b = new RadialGradientBrush(new GradientStopCollection
        {
            new(c, 0),
            new(Color.FromArgb(0, c.R, c.G, c.B), 1),
        });
        b.Freeze();
        return b;
    }

    private static bool MotionAllowed => SystemParameters.ClientAreaAnimation;

    private void Start()
    {
        Populate();
        if (_running || !MotionAllowed) { InvalidateVisual(); return; }
        _running = true;
        _clock.Restart();
        _last = 0;
        CompositionTarget.Rendering += OnFrame;
    }

    private void Stop()
    {
        if (!_running) return;
        _running = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private int Target => (int)Math.Clamp(ActualWidth / 10, 40, 220);

    private void Populate()
    {
        if (ActualWidth <= 0) return;
        _motes.RemoveAll(m => m.X > ActualWidth + 20);
        while (_motes.Count < Target) _motes.Add(NewMote(true));
    }

    private Mote NewMote(bool anywhere)
    {
        var warm = _rng.NextDouble() < 0.09;
        return new Mote
        {
            X = anywhere ? _rng.NextDouble() * ActualWidth : -8,
            Y = _rng.NextDouble() * ActualHeight,
            Vx = 5 + _rng.NextDouble() * (warm ? 14 : 20),
            Vy = -(1 + _rng.NextDouble() * (warm ? 7 : 4)),
            Size = warm ? 1.5 + _rng.NextDouble() * 1.5 : 0.8 + _rng.NextDouble() * 1.4,
            Phase = _rng.NextDouble() * Math.PI * 2,
            Twinkle = 0.6 + _rng.NextDouble() * 2.2,
            Alpha = warm ? 0.7 + _rng.NextDouble() * 0.3 : 0.28 + _rng.NextDouble() * 0.45,
            Warm = warm,
        };
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var t = _clock.Elapsed.TotalSeconds;
        var dt = Math.Min(0.05, t - _last);
        _last = t;
        if (dt <= 0) return;

        var w = ActualWidth;
        var h = ActualHeight;
        for (var i = _motes.Count - 1; i >= 0; i--)
        {
            var m = _motes[i];
            m.X += m.Vx * dt;
            m.Y += (m.Vy + Math.Sin(t * 0.9 + m.Phase) * 4) * dt;
            if (m.X > w + 10 || m.Y < -10 || m.Y > h + 10)
            {
                m = NewMote(false);
                m.Y = _rng.NextDouble() * h;
            }
            _motes[i] = m;
        }

        for (var i = 0; i < _electrons.Count; i++)
        {
            var el = _electrons[i];
            el.Angle += el.Speed * dt;
            _electrons[i] = el;
        }

        if (OrbitTarget is { } target)
        {
            if (!_orbitSet) { _orbit = target; _orbitSet = true; }
            var k = 1 - Math.Exp(-dt * 9);
            _orbit = new Rect(
                _orbit.X + (target.X - _orbit.X) * k,
                _orbit.Y + (target.Y - _orbit.Y) * k,
                Math.Max(1, _orbit.Width + (target.Width - _orbit.Width) * k),
                Math.Max(1, _orbit.Height + (target.Height - _orbit.Height) * k));
            _orbitAlpha = Math.Min(1, _orbitAlpha + dt * 3);
        }
        else
        {
            _orbitAlpha = Math.Max(0, _orbitAlpha - dt * 3);
        }

        if (t >= _nextSweep)
        {
            _sweepStart = t;
            _nextSweep = t + 8 + _rng.NextDouble() * 5;
        }
        if (t >= _nextPulse && PulseOrigin != null)
        {
            _pulses.Add(t);
            _nextPulse = t + 3.6 + _rng.NextDouble() * 1.6;
        }
        _pulses.RemoveAll(p => t - p > 2.4);
        InvalidateVisual();
    }

    private Point OrbitPoint(in Electron el, double angle, out double depth)
    {
        var cx = _orbit.X + _orbit.Width / 2;
        var cy = _orbit.Y + _orbit.Height / 2;
        var rx = (_orbit.Width / 2 + 14) * el.Scale;
        var ry = (_orbit.Height / 2 + 5) * el.Scale;
        var x = Math.Cos(angle) * rx;
        var y = Math.Sin(angle) * ry;
        depth = Math.Sin(angle);
        var c = Math.Cos(el.Tilt);
        var s = Math.Sin(el.Tilt);
        return new Point(cx + x * c - y * s, cy + x * s + y * c);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var t = _clock.Elapsed.TotalSeconds;
        var w = ActualWidth;
        var h = ActualHeight;

        var sweep = (t - _sweepStart) / 2.6;
        if (sweep is >= 0 and <= 1)
        {
            var x = -260 + (w + 520) * sweep;
            var band = new LinearGradientBrush(new GradientStopCollection
            {
                new(Color.FromArgb(0, 0xF3, 0xEE, 0xDD), 0),
                new(Color.FromArgb(0x16, 0xF3, 0xEE, 0xDD), 0.5),
                new(Color.FromArgb(0, 0xF3, 0xEE, 0xDD), 1),
            }, 0);
            dc.PushTransform(new SkewTransform(-18, 0, x, h));
            dc.DrawRectangle(band, null, new Rect(x - 130, 0, 260, h));
            dc.Pop();
        }

        if (PulseOrigin is { } o)
        {
            foreach (var p in _pulses)
            {
                var k = (t - p) / 2.4;
                var r = 10 + k * 90;
                var a = (byte)(Math.Pow(1 - k, 1.6) * 170);
                dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(a, Bone.R, Bone.G, Bone.B)), 1.4), o, r, r);
                if (r > 22)
                    dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb((byte)(a / 3), Ember.R, Ember.G, Ember.B)), 1), o, r - 9, r - 9);
            }
        }

        foreach (var m in _motes)
        {
            var tw = 0.55 + 0.45 * Math.Sin(t * m.Twinkle + m.Phase);
            var fade = Math.Clamp(1.15 - m.Y / h * 0.45, 0.35, 1);
            var a = Math.Clamp(m.Alpha * tw * fade, 0, 1);
            var c = m.Warm ? Ember : Bone;
            var p = new Point(m.X, m.Y);
            if (m.Warm)
            {
                dc.PushOpacity(a);
                dc.DrawEllipse(EmberGlow, null, p, m.Size * 4.5, m.Size * 4.5);
                dc.Pop();
            }
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(a * 255), c.R, c.G, c.B)), null, p, m.Size, m.Size);
        }

        if (_orbitAlpha <= 0 || !_orbitSet) return;
        foreach (var el in _electrons)
        {
            var dir = Math.Sign(el.Speed);
            for (var k = 6; k >= 1; k--)
            {
                var tp = OrbitPoint(el, el.Angle - dir * k * 0.075, out var td);
                var ta = _orbitAlpha * (0.3 + 0.35 * (td + 1)) * (1 - k / 7.0) * 0.45;
                var ts = el.Size * (0.75 + 0.2 * (td + 1) / 2) * (1 - k * 0.09);
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(ta * 255), Rad.R, Rad.G, Rad.B)), null, tp, ts, ts);
            }
            var pt = OrbitPoint(el, el.Angle, out var depth);
            var front = (depth + 1) / 2;
            var flicker = 0.82 + 0.18 * Math.Sin(t * 17 + el.Phase);
            var alpha = _orbitAlpha * (0.3 + 0.7 * front) * flicker;
            var size = el.Size * (0.75 + 0.4 * front);
            dc.PushOpacity(alpha);
            dc.DrawEllipse(RadGlow, null, pt, size * 5, size * 5);
            dc.Pop();
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(alpha * 255), Rad.R, Rad.G, Rad.B)), null, pt, size, size);
        }
    }
}
