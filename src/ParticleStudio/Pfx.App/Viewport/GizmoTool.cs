using System.Numerics;
using Pfx.App.Core;
using Pfx.App.Model;
using Pfx.Core.Model;
using Pfx.Render;
using Pfx.Sim;

namespace Pfx.App.Viewport;

public enum ToolKind { Move, Rotate, Scale, Shape, Path }

public enum MoveSpace { World, Source }

public enum HandleKind { MoveAxis, MoveFree, Ring, ScaleAxis, ScaleUniform, ShapeFace, ConeA, ConeB, ConeC }

// A grabbable part of a gizmo: a world polyline (one point = a dot), the axis or angle it edits and its side.
public sealed record Handle(HandleKind Kind, int Axis, int Side, Vector3[] Points, Vector4 Color);

// The move, rotate, scale and shape gizmos of the first selected emitter (spec 3.3, 5.1, 1.1 rule 4). Handles are
// drawn by the renderer's overlay at a constant size on screen and picked on the CPU by their distance in pixels
// from the cursor; a drag writes the same keys the inspector shows (Offset, Angles, ShapeSize, ConeAngle) through
// the document, one undo step per drag. With several emitters selected each gets the same change from its own
// starting value (moved by the same amount, turned by the same angle, scaled by the same factor).
// State: the documents own every value; this object owns the tool settings, the hovered handle and the drag.
public sealed class GizmoTool : IViewportTool
{
    // Handle length on screen; Blender's default gizmo size is 75 px, a little more here for the 2 px strokes.
    public const float GizmoPx = 90;
    // How far from a handle a press still takes it (the P7 sprite view uses the same 7 px).
    public const float GrabPx = 7;
    // Shape handles of a zero size sit this far from the centre so both sides stay apart and can be taken.
    private const float MinHandlePx = 14;
    // An axis that points within this of the view direction (cos 14 degrees) projects too short to aim at: hidden.
    private const float HiddenAxisCos = 0.97f;
    // Cone handles search their arc in steps of this many degrees (finer than a pixel at the gizmo size).
    private const float ConeSearchStep = 0.1f;
    private const float Rad = MathF.PI / 180f;

    public static readonly Vector4 AxisX = Palette.Hex(0xE5534B), AxisY = Palette.Hex(0x46C485), AxisZ = Palette.Hex(0x4B8EF5);
    private static readonly Vector4[] AxisColors = { AxisX, AxisY, AxisZ };

    private readonly IToolHost _host;
    private Handle? _hover;
    private DragState? _drag;
    private List<Handle> _handles = new();

    public GizmoTool(IToolHost host) => _host = host;

    public ToolKind Kind { get; set; } = ToolKind.Move;
    public MoveSpace Space { get; set; } = MoveSpace.World;
    public bool Snap { get; set; }
    public double StepMetres { get; set; } = 0.1;
    public double StepDegrees { get; set; } = 15;

    // ---- geometry ----

    private Camera Cam => _host.Viewport.Camera;
    private float W => Math.Max(1, _host.Viewport.PixelWidth);
    private float H => Math.Max(1, _host.Viewport.PixelHeight);

    public EmitterFrame? ActiveFrame()
    {
        var doc = _host.ActiveDoc;
        var sel = _host.SelectedEmitters;
        return doc == null || sel.Count == 0 ? null : EmitterFrame.Of(sel[0], _host, doc);
    }

    public Vector2? Screen(Vector3 world) => Cam.Project(world, W, H) is { } p ? new Vector2(p.X, p.Y) : null;

    // Metres one pixel spans at a world point (the gizmo is sized and rounded by it).
    public float MetresPerPixel(Vector3 at) => Cam.MetresPerPixel(Math.Max(Camera.NearPlane, Vector3.Dot(at - Cam.Eye, Cam.Forward)), H);

    private static Vector3 Axis(int k) => k switch { 0 => Vector3.UnitX, 1 => Vector3.UnitY, _ => Vector3.UnitZ };

    private Vector3[] MoveAxes(EmitterFrame f) =>
        Enumerable.Range(0, 3).Select(k => Space == MoveSpace.World ? Axis(k) : Vector3.TransformNormal(Axis(k), f.SourceRotation)).ToArray();

    private static Vector3[] EmitterAxes(EmitterFrame f) => Enumerable.Range(0, 3).Select(k => Vector3.Normalize(Vector3.TransformNormal(Axis(k), f.Axes))).ToArray();

    // The axis each Angles value turns about in world space (gimbal): yaw about the source's up, pitch about X after
    // the yaw, roll about Z after yaw and pitch, as Orientation.YawPitchRoll composes them.
    public static Vector3 RingAxis(EmitterFrame f, int k)
    {
        var a = f.Angles;
        return k switch
        {
            0 => Vector3.TransformNormal(Vector3.UnitY, f.SourceRotation),
            1 => Vector3.TransformNormal(Vector3.UnitX, Orientation.Yaw(a.X * Rad) * f.SourceRotation),
            _ => Vector3.TransformNormal(Vector3.UnitZ, Orientation.Pitch(a.Y * Rad) * Orientation.Yaw(a.X * Rad) * f.SourceRotation),
        };
    }

    private static Vector3[] Ring(Vector3 o, Vector3 axis, float r)
    {
        var u = Vector3.Normalize(Vector3.Cross(axis, MathF.Abs(axis.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
        var v = Vector3.Cross(axis, u);
        const int n = 64;
        var pts = new Vector3[n + 1];
        for (int i = 0; i <= n; i++)
        {
            float t = i * MathF.Tau / n;
            pts[i] = o + (u * MathF.Cos(t) + v * MathF.Sin(t)) * r;
        }
        return pts;
    }

    public float ConeLength(EmitterFrame f) => GizmoPx * 1.2f * MetresPerPixel(f.Origin);

    public static Vector3 ConePoint(EmitterFrame f, float polarDeg, float azimuthDeg, float length) =>
        f.Origin + Vector3.TransformNormal(Orientation.ConeDirection(polarDeg * Rad, azimuthDeg * Rad), f.Axes) * length;

    // Where each shape handle sits along its emitter axis (half extent, at least MinHandlePx from the centre).
    private float ShapeReach(EmitterFrame f, int k)
    {
        var half = f.Params.SpawnHalfExtent;
        return Math.Max(f.Params.Shape == ShapeKind.Sphere ? half.X : half[k], MinHandlePx * MetresPerPixel(f.Origin));
    }

    public static bool HasSize(ShapeKind s) => s is ShapeKind.Box or ShapeKind.Sphere or ShapeKind.Ellipse or ShapeKind.OnSphere;

    public List<Handle> Build(EmitterFrame f)
    {
        var list = new List<Handle>();
        var o = f.Origin;
        if (Cam.Project(o, W, H) == null)
            return list;
        float len = GizmoPx * MetresPerPixel(o);
        var view = Vector3.Normalize(o - Cam.Eye);
        switch (Kind)
        {
            case ToolKind.Move:
            {
                var axes = MoveAxes(f);
                for (int k = 0; k < 3; k++)
                    if (MathF.Abs(Vector3.Dot(axes[k], view)) < HiddenAxisCos)
                        list.Add(new Handle(HandleKind.MoveAxis, k, 1, new[] { o + axes[k] * len * 0.15f, o + axes[k] * len }, AxisColors[k]));
                list.Add(new Handle(HandleKind.MoveFree, 3, 0, new[] { o }, Palette.TextHi));
                break;
            }
            case ToolKind.Rotate:
            {
                int[] colour = { 1, 0, 2 };
                for (int k = 0; k < 3; k++)
                    list.Add(new Handle(HandleKind.Ring, k, 0, Ring(o, RingAxis(f, k), len), AxisColors[colour[k]]));
                break;
            }
            case ToolKind.Scale:
            {
                var axes = EmitterAxes(f);
                for (int k = 0; k < 3; k++)
                    if (MathF.Abs(Vector3.Dot(axes[k], view)) < HiddenAxisCos)
                        list.Add(new Handle(HandleKind.ScaleAxis, k, 1, new[] { o + axes[k] * len * 0.15f, o + axes[k] * len }, AxisColors[k]));
                list.Add(new Handle(HandleKind.ScaleUniform, 3, 0, new[] { o }, Palette.TextHi));
                break;
            }
            case ToolKind.Shape:
            {
                if (HasSize(f.Params.Shape))
                {
                    var axes = EmitterAxes(f);
                    for (int k = 0; k < 3; k++)
                        foreach (int side in new[] { 1, -1 })
                            list.Add(new Handle(HandleKind.ShapeFace, k, side, new[] { o + axes[k] * side * ShapeReach(f, k) }, AxisColors[k]));
                }
                var c = f.Params.ConeAngle;
                float cl = ConeLength(f);
                list.Add(new Handle(HandleKind.ConeB, 0, 0, new[] { ConePoint(f, c.Y * 0.5f, c.X * 0.5f, cl) }, Palette.Accent));
                list.Add(new Handle(HandleKind.ConeC, 0, 0, new[] { ConePoint(f, c.Z * 0.5f, c.X * 0.5f, cl * 1.08f) }, Palette.Accent));
                list.Add(new Handle(HandleKind.ConeA, 0, 0, new[] { ConePoint(f, Math.Max(c.Y, c.Z) * 0.5f, c.X, cl * 1.16f) }, Palette.Key));
                break;
            }
        }
        return list;
    }

    // ---- drawing ----

    public void Compose(OverlayBuilder o, List<LineVertex> lines)
    {
        _handles.Clear();
        if (Kind == ToolKind.Path || ActiveFrame() is not { } f)
            return;
        _handles = Build(f);
        if (Kind == ToolKind.Shape)
        {
            SceneGeometry.Cone(lines, f.Origin, f.Axes, f.Params.ConeAngle, ConeLength(f), Palette.Accent.WithAlpha(0.8f));
            SceneGeometry.Shape(lines, f.Origin, f.Axes, f.Params, Palette.CurveA);
        }
        var active = _drag?.Handle ?? _hover;
        foreach (var h in _handles)
        {
            bool hot = active != null && Same(h, active);
            var colour = hot ? Palette.Accent.WithAlpha(1) : h.Color;
            DrawHandle(o, h, colour, hot ? 3.5f : 2);
        }
        if (active != null && _handles.FirstOrDefault(h => Same(h, active)) is { } shown && Screen(shown.Points[^1]) is { } at)
        {
            var text = Label(f, shown);
            o.Panel(at.X + 10, at.Y - o.LineHeight - 6, new[] { new OverlayLine(text, Palette.TextHi) }, Palette.BgDeep.WithAlpha(0.8f));
        }
        if (Kind == ToolKind.Shape && Note(f) is { } note && Screen(f.Origin) is { } p)
            o.Panel(p.X + 12, p.Y + 10, new[] { new OverlayLine(note, Palette.Warn) }, Palette.BgDeep.WithAlpha(0.8f));
    }

    private static bool Same(Handle a, Handle b) => a.Kind == b.Kind && a.Axis == b.Axis && a.Side == b.Side;

    private void DrawHandle(OverlayBuilder o, Handle h, Vector4 colour, float width)
    {
        if (h.Points.Length == 1)
        {
            if (Screen(h.Points[0]) is { } p)
                o.Dot(p.X, p.Y, width * 3 + 3, colour);
            return;
        }
        for (int i = 0; i + 1 < h.Points.Length; i++)
            if (Screen(h.Points[i]) is { } a && Screen(h.Points[i + 1]) is { } b)
                o.Line(a.X, a.Y, b.X, b.Y, width, colour);
        if (h.Kind is HandleKind.MoveAxis or HandleKind.ScaleAxis && Screen(h.Points[^1]) is { } tip)
            o.Dot(tip.X, tip.Y, h.Kind == HandleKind.ScaleAxis ? 8 : 6, colour);
    }

    // What the hovered or dragged handle edits, with its value now (the inspector shows the same number).
    public string Label(EmitterFrame f, Handle h)
    {
        string xyz = "XYZ"[Math.Min(h.Axis, 2)].ToString();
        var v = f.View;
        string N(double d) => Loc.Num(d);
        return h.Kind switch
        {
            HandleKind.MoveAxis or HandleKind.MoveFree => Loc.F("Gz.Offset", N(ToolEdits.Vec(v, "Offset")[0]), N(ToolEdits.Vec(v, "Offset")[1]), N(ToolEdits.Vec(v, "Offset")[2])),
            HandleKind.Ring => Loc.F("Gz.Ring" + h.Axis, N(ToolEdits.Vec(v, "Angles")[h.Axis])),
            HandleKind.ScaleAxis or HandleKind.ShapeFace => f.Params.Shape == ShapeKind.Sphere
                ? Loc.F("Gz.Diameter", N(ToolEdits.Vec(v, "ShapeSize")[0]))
                : Loc.F("Gz.Size", xyz, N(ToolEdits.Vec(v, "ShapeSize")[Math.Min(h.Axis, 2)])),
            HandleKind.ScaleUniform => Loc.F("Gz.SizeAll", N(ToolEdits.Vec(v, "ShapeSize")[0]), N(ToolEdits.Vec(v, "ShapeSize")[1]), N(ToolEdits.Vec(v, "ShapeSize")[2])),
            HandleKind.ConeA => Loc.F("Gz.ConeA", N(ToolEdits.Vec(v, "ConeAngle")[0])),
            HandleKind.ConeB => Loc.F("Gz.ConeB", N(ToolEdits.Vec(v, "ConeAngle")[1])),
            _ => Loc.F("Gz.ConeC", N(ToolEdits.Vec(v, "ConeAngle")[2])),
        };
    }

    // Honest limits of the shape (prompt 2.3): a Point has no size; sized Cone and OnSphere draw nothing in game.
    public static string? Note(EmitterFrame f) => f.Params.Shape switch
    {
        ShapeKind.Point => Loc.T("Gz.Note.Point"),
        ShapeKind.Cone or ShapeKind.OnSphere when f.Params.ShapeSize != Vector3.Zero => Loc.T("Gz.Note.Invisible"),
        ShapeKind.Cone => Loc.T("Gz.Note.Cone"),
        _ => null,
    };

    // ---- picking ----

    public Handle? Pick(float x, float y)
    {
        Handle? best = null;
        float bestD = GrabPx;
        var m = new Vector2(x, y);
        foreach (var h in _handles)
        {
            float d = float.MaxValue;
            if (h.Points.Length == 1)
            {
                if (Screen(h.Points[0]) is { } p)
                    // Dots win over a line passing under them (a cone handle on its rim, the centre over the axes).
                    d = Vector2.Distance(m, p) - 3;
            }
            else
            {
                for (int i = 0; i + 1 < h.Points.Length; i++)
                    if (Screen(h.Points[i]) is { } a && Screen(h.Points[i + 1]) is { } b)
                        d = Math.Min(d, SegmentDistance(m, a, b));
            }
            if (d < bestD)
            {
                bestD = d;
                best = h;
            }
        }
        return best;
    }

    private static float SegmentDistance(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float l2 = ab.LengthSquared();
        float t = l2 > 0 ? Math.Clamp(Vector2.Dot(p - a, ab) / l2, 0, 1) : 0;
        return Vector2.Distance(p, a + ab * t);
    }

    // ---- input ----

    private sealed class DragState
    {
        public required Handle Handle;
        public required EffectDoc Doc;
        public required EmitterFrame Frame;
        public required string Merge;
        public required int UndoAtPress;
        public required List<(EmitterView View, double[] Offset, double[] Angles, double[] Size, double[] Cone)> Start;
        public Vector3 Axis;
        public float S0;
        public Vector3 Hit0;
        public Vector2 Press;
        public Vector2 Centre;
        // Ring drags: degrees of the value per radian around the centre (angle mode) or per pixel along the tangent.
        public bool AngleMode;
        public float PerRadian;
        public Vector2 Tangent;
        public float DegPerPx;
        public float LastAngle;
        public float Turned;
    }

    public bool Press(int x, int y, bool ctrl, bool shift)
    {
        if (Kind == ToolKind.Path || _host.ActiveDoc is not { } doc || ActiveFrame() is not { } f)
            return false;
        _handles = Build(f);
        if (Pick(x, y) is not { } h)
            return false;
        var start = _host.SelectedEmitters.Where(v => !doc.Locked.Contains(v.Block))
            .Select(v => (v, ToolEdits.Vec(v, "Offset"), ToolEdits.Vec(v, "Angles"), ToolEdits.Vec(v, "ShapeSize"), ToolEdits.Vec(v, "ConeAngle"))).ToList();
        if (start.Count == 0)
        {
            _host.Toast(Loc.T("Toast.GizmoLocked"), "warn");
            return true;
        }
        var d = new DragState
        {
            Handle = h, Doc = doc, Frame = f, Start = start, Merge = "gizmo:" + Guid.NewGuid().ToString("N"), UndoAtPress = doc.UndoCount,
            Press = new Vector2(x, y), Centre = Screen(f.Origin) ?? new Vector2(x, y),
        };
        var (eye, dir) = Cam.Ray(x, y, W, H);
        switch (h.Kind)
        {
            case HandleKind.MoveAxis:
                d.Axis = MoveAxes(f)[h.Axis];
                d.S0 = AxisParam(f.Origin, d.Axis, eye, dir) ?? 0;
                break;
            case HandleKind.MoveFree:
                d.Hit0 = PlaneHit(f.Origin, Cam.Forward, eye, dir) ?? f.Origin;
                break;
            case HandleKind.ScaleAxis:
                d.Axis = EmitterAxes(f)[h.Axis];
                d.S0 = AxisParam(f.Origin, d.Axis, eye, dir) ?? GizmoPx * MetresPerPixel(f.Origin);
                break;
            case HandleKind.ShapeFace:
                d.Axis = EmitterAxes(f)[h.Axis] * h.Side;
                d.S0 = AxisParam(f.Origin, d.Axis, eye, dir) ?? 0;
                break;
            case HandleKind.Ring:
                PrepareRing(d, f, new Vector2(x, y));
                break;
        }
        _drag = d;
        _hover = h;
        return true;
    }

    // The sense of a ring is taken from Orientation itself: the ring point under the cursor is turned by +1 degree of
    // its value and both are projected. Its turn around the projected centre gives degrees per radian; a ring seen
    // edge-on barely turns around the centre, so its value follows the cursor along the projected motion instead.
    private void PrepareRing(DragState d, EmitterFrame f, Vector2 mouse)
    {
        var h = d.Handle;
        var nearest = h.Points.OrderBy(p => Screen(p) is { } s ? Vector2.Distance(s, mouse) : float.MaxValue).First();
        Matrix4x4.Invert(f.Axes, out var toLocal);
        var local = Vector3.TransformNormal(nearest - f.Origin, toLocal);
        var turned = f.Angles;
        turned[h.Axis] += 1;
        var moved = f.Origin + Vector3.TransformNormal(local, f.AxesWith(turned));
        var a = Screen(nearest) ?? mouse;
        var b = Screen(moved) ?? mouse;
        var disp = b - a;
        float r = Vector2.Distance(a, d.Centre);
        float turn = Orientation.Wrap((MathF.Atan2(b.Y - d.Centre.Y, b.X - d.Centre.X) - MathF.Atan2(a.Y - d.Centre.Y, a.X - d.Centre.X)) / Rad) * Rad;
        d.AngleMode = r > 4 && MathF.Abs(turn) * r >= 0.5f * disp.Length();
        if (d.AngleMode)
        {
            d.PerRadian = 1 / turn;
            d.LastAngle = MathF.Atan2(mouse.Y - d.Centre.Y, mouse.X - d.Centre.X);
        }
        else
        {
            float l = disp.Length();
            d.Tangent = l > 1e-4f ? disp / l : Vector2.UnitX;
            d.DegPerPx = l > 1e-4f ? 1 / l : 1;
        }
    }

    public static float? AxisParam(Vector3 o, Vector3 a, Vector3 e, Vector3 d)
    {
        var w = o - e;
        float b = Vector3.Dot(a, d);
        float denom = 1 - b * b;
        if (denom < 1e-4f)
            return null;
        return (b * Vector3.Dot(d, w) - Vector3.Dot(a, w)) / denom;
    }

    public static Vector3? PlaneHit(Vector3 point, Vector3 normal, Vector3 e, Vector3 d)
    {
        float dn = Vector3.Dot(d, normal);
        if (MathF.Abs(dn) < 1e-5f)
            return null;
        float t = Vector3.Dot(point - e, normal) / dn;
        return t > 0 ? e + d * t : null;
    }

    public void Drag(int x, int y, bool ctrl, bool shift)
    {
        if (_drag is not { } d)
            return;
        bool snap = Snap ^ ctrl;
        var (eye, dir) = Cam.Ray(x, y, W, H);
        var f = d.Frame;
        double mpp = MetresPerPixel(f.Origin);
        double precision = ToolEdits.Precision(mpp);
        var makers = new List<Func<Pfx.Core.Editing.IEdit?>>();
        string label;
        switch (d.Handle.Kind)
        {
            case HandleKind.MoveAxis:
            case HandleKind.MoveFree:
            {
                Vector3 world;
                if (d.Handle.Kind == HandleKind.MoveAxis)
                {
                    if (AxisParam(f.Origin, d.Axis, eye, dir) is not float s)
                        return;
                    double delta = s - d.S0;
                    if (snap)
                        delta = ToolEdits.Round(delta, StepMetres);
                    world = d.Axis * (float)delta;
                }
                else
                {
                    if (PlaneHit(f.Origin, Cam.Forward, eye, dir) is not { } hit)
                        return;
                    world = hit - d.Hit0;
                    if (snap)
                        world = new Vector3((float)ToolEdits.Round(world.X, StepMetres), (float)ToolEdits.Round(world.Y, StepMetres), (float)ToolEdits.Round(world.Z, StepMetres));
                }
                var local = f.ToSource(world);
                foreach (var e in d.Start)
                    makers.Add(ToolEdits.SetVec(e.View, "Offset", Shift(e.Offset, local, precision)));
                label = Loc.T("Undo.GizmoMove");
                break;
            }
            case HandleKind.Ring:
            {
                float deg;
                double perPx;
                if (d.AngleMode)
                {
                    float a = MathF.Atan2(y - d.Centre.Y, x - d.Centre.X);
                    d.Turned += Orientation.Wrap((a - d.LastAngle) / Rad) * Rad;
                    d.LastAngle = a;
                    deg = d.Turned * d.PerRadian;
                    perPx = MathF.Abs(d.PerRadian) / Math.Max(1, Vector2.Distance(new Vector2(x, y), d.Centre));
                }
                else
                {
                    deg = Vector2.Dot(new Vector2(x, y) - d.Press, d.Tangent) * d.DegPerPx;
                    perPx = d.DegPerPx;
                }
                double delta = snap ? ToolEdits.Round(deg, StepDegrees) : deg;
                double step = ToolEdits.Precision(perPx);
                int k = d.Handle.Axis;
                foreach (var e in d.Start)
                {
                    var v = (double[])e.Angles.Clone();
                    v[k] = ToolEdits.Round(e.Angles[k] + delta, step);
                    makers.Add(ToolEdits.SetVec(e.View, "Angles", v));
                }
                label = Loc.T("Undo.GizmoRotate");
                break;
            }
            case HandleKind.ScaleAxis:
            case HandleKind.ScaleUniform:
            {
                double factor;
                if (d.Handle.Kind == HandleKind.ScaleAxis)
                {
                    if (AxisParam(f.Origin, d.Axis, eye, dir) is not float s || MathF.Abs(d.S0) < 1e-6f)
                        return;
                    factor = Math.Max(0, s / d.S0);
                }
                else
                {
                    // The centre is pressed on the centre, so no distance ratio exists: right or up grows, left or
                    // down shrinks, one gizmo length of travel = twice the size (a UI mapping like the orbit's
                    // 0.3 degrees per pixel).
                    factor = Math.Max(0, 1 + (x - d.Press.X - (y - d.Press.Y)) / GizmoPx);
                }
                foreach (var e in d.Start)
                {
                    var v = (double[])e.Size.Clone();
                    bool sphere = EmitterParams.ParseShape(e.View.Text("ShapeType")) == ShapeKind.Sphere;
                    for (int c = 0; c < 3; c++)
                    {
                        bool touched = d.Handle.Kind == HandleKind.ScaleUniform || (sphere ? c == 0 : c == d.Handle.Axis);
                        if (touched)
                            v[c] = ToolEdits.Round(e.Size[c] * factor, snap ? StepMetres : precision);
                    }
                    makers.Add(ToolEdits.SetVec(e.View, "ShapeSize", v));
                }
                label = Loc.T("Undo.GizmoScale");
                break;
            }
            case HandleKind.ShapeFace:
            {
                if (AxisParam(f.Origin, d.Axis, eye, dir) is not float s)
                    return;
                double grow = 2 * (s - d.S0);
                foreach (var e in d.Start)
                {
                    var v = (double[])e.Size.Clone();
                    bool sphere = EmitterParams.ParseShape(e.View.Text("ShapeType")) == ShapeKind.Sphere;
                    int c = sphere ? 0 : d.Handle.Axis;
                    v[c] = Math.Max(0, ToolEdits.Round(e.Size[c] + grow, snap ? StepMetres : precision));
                    makers.Add(ToolEdits.SetVec(e.View, "ShapeSize", v));
                }
                label = Loc.T("Undo.GizmoShape");
                break;
            }
            default:
            {
                int k = d.Handle.Kind == HandleKind.ConeA ? 0 : d.Handle.Kind == HandleKind.ConeB ? 1 : 2;
                double value = ConeValue(f, d.Handle.Kind, new Vector2(x, y));
                if (snap)
                    value = ToolEdits.Round(value, StepDegrees);
                value = Math.Clamp(value, 0, 360);
                foreach (var e in d.Start)
                {
                    var v = (double[])e.Cone.Clone();
                    v[k] = value;
                    makers.Add(ToolEdits.SetVec(e.View, "ConeAngle", v));
                }
                label = Loc.T("Undo.GizmoCone");
                break;
            }
        }
        ToolEdits.Steps(d.Doc, label, d.Merge, makers);
    }

    // Start value moved by a source-space delta; components the move does not change keep their value exactly.
    private static double[] Shift(double[] start, Vector3 delta, double precision)
    {
        var v = (double[])start.Clone();
        for (int c = 0; c < 3; c++)
            if (MathF.Abs(delta[c]) > 1e-7f)
                v[c] = ToolEdits.Round(start[c] + delta[c], precision);
        return v;
    }

    // The cone value whose handle lands nearest the cursor: A sweeps the azimuth 0..360 on the outer rim, B and C the
    // polar angle 0..180 at the middle azimuth; values are full apertures (twice the polar angle, prompt 2.3).
    public double ConeValue(EmitterFrame f, HandleKind kind, Vector2 mouse)
    {
        var c = f.Params.ConeAngle;
        float len = ConeLength(f);
        float best = float.MaxValue, at = 0;
        int n = (int)(kind == HandleKind.ConeA ? 360 / ConeSearchStep : 180 / ConeSearchStep);
        for (int i = 0; i <= n; i++)
        {
            float a = i * ConeSearchStep;
            var p = kind == HandleKind.ConeA ? ConePoint(f, Math.Max(c.Y, c.Z) * 0.5f, a, len) : ConePoint(f, a, c.X * 0.5f, len);
            if (Screen(p) is { } s && Vector2.DistanceSquared(s, mouse) < best)
            {
                best = Vector2.DistanceSquared(s, mouse);
                at = a;
            }
        }
        return Math.Round(kind == HandleKind.ConeA ? at : at * 2, 1);
    }

    public void Release(int x, int y)
    {
        if (_drag is not { } d)
            return;
        _drag = null;
        d.Doc.Seal();
    }

    // Escape or a lost capture: the drag's step is taken back.
    public void Cancel()
    {
        if (_drag is not { } d)
            return;
        _drag = null;
        d.Doc.Seal();
        if (d.Doc.UndoCount > d.UndoAtPress)
            d.Doc.UndoStep();
    }

    public void Hover(int x, int y)
    {
        if (Kind == ToolKind.Path || ActiveFrame() is not { } f)
        {
            _hover = null;
            return;
        }
        _handles = Build(f);
        _hover = Pick(x, y);
    }
}
