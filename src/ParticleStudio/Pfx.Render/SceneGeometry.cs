using System.Numerics;
using System.Runtime.InteropServices;
using Pfx.Sim;

namespace Pfx.Render;

[StructLayout(LayoutKind.Sequential)]
public struct MeshVertex
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector4 Color;
}

[StructLayout(LayoutKind.Sequential)]
public struct LineVertex
{
    public Vector3 Position;
    public Vector4 Color;
}

public enum HelperModel { None, Box, Player }

// Palette colours of prompt 5.3 (sRGB hex) as linear RGBA for the line shader.
public static class Palette
{
    public static readonly Vector4 Accent = Hex(0x2EC4D6);
    public static readonly Vector4 Path = Hex(0xD45BE0);
    public static readonly Vector4 Key = Hex(0xF0A73A);
    public static readonly Vector4 LineStrong = Hex(0x43505B);
    public static readonly Vector4 Line = Hex(0x313B44);
    public static readonly Vector4 TextHi = Hex(0xE8EEF2);
    public static readonly Vector4 Text = Hex(0xC3CDD4);
    public static readonly Vector4 TextDim = Hex(0x8494A0);
    public static readonly Vector4 Warn = Hex(0xE8B640);
    public static readonly Vector4 CurveA = Hex(0xD9E0E5);
    public static readonly Vector4 BgDeep = Hex(0x0E1215);

    public static Vector4 Hex(int rgb, float alpha = 1) =>
        new(Sky.Linear(rgb >> 16 & 0xFF, rgb >> 8 & 0xFF, rgb & 0xFF), alpha);

    public static Vector4 WithAlpha(this Vector4 c, float a) => new(c.X, c.Y, c.Z, a);
}

// The viewport's own scene (not the game's): ground with a metre grid, helper proxies and gizmo lines.
public static class SceneGeometry
{
    // Ground plane half size in metres; inside the camera's 1588 m depth range.
    public const float GroundHalf = 1000;

    public static List<MeshVertex> Ground(Vector3 albedo)
    {
        var c = new Vector4(albedo, 1);
        var n = Vector3.UnitY;
        Vector3[] p =
        {
            new(-GroundHalf, 0, -GroundHalf), new(-GroundHalf, 0, GroundHalf),
            new(GroundHalf, 0, -GroundHalf), new(GroundHalf, 0, GroundHalf),
        };
        return new[] { p[0], p[1], p[2], p[2], p[1], p[3] }.Select(v => new MeshVertex { Position = v, Normal = n, Color = c }).ToList();
    }

    // Proxies next to the effect for scale: a 1 m box, or a 1.8 m tall, 0.3 m radius cylinder (a player).
    public static List<MeshVertex> Helper(HelperModel model, Vector3 at)
    {
        var list = new List<MeshVertex>();
        var c = new Vector4(0.45f, 0.45f, 0.45f, 1);
        if (model == HelperModel.Box)
        {
            AddBox(list, at + new Vector3(0, 0.5f, 0), new Vector3(0.5f), c);
        }
        else if (model == HelperModel.Player)
        {
            const int sides = 24;
            const float r = 0.3f, h = 1.8f;
            for (int i = 0; i < sides; i++)
            {
                float a0 = i * MathF.Tau / sides, a1 = (i + 1) * MathF.Tau / sides;
                var n0 = new Vector3(MathF.Cos(a0), 0, MathF.Sin(a0));
                var n1 = new Vector3(MathF.Cos(a1), 0, MathF.Sin(a1));
                Vector3 b0 = at + n0 * r, b1 = at + n1 * r, t0 = b0 + new Vector3(0, h, 0), t1 = b1 + new Vector3(0, h, 0);
                list.AddRange(new[]
                {
                    V(b0, n0, c), V(t0, n0, c), V(b1, n1, c), V(b1, n1, c), V(t0, n0, c), V(t1, n1, c),
                    V(at + new Vector3(0, h, 0), Vector3.UnitY, c), V(t1, Vector3.UnitY, c), V(t0, Vector3.UnitY, c),
                });
            }
        }
        return list;
    }

    private static MeshVertex V(Vector3 p, Vector3 n, Vector4 c) => new() { Position = p, Normal = n, Color = c };

    private static void AddBox(List<MeshVertex> list, Vector3 centre, Vector3 half, Vector4 c)
    {
        Vector3[] normals = { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ };
        foreach (var n in normals)
        {
            var u = MathF.Abs(n.Y) > 0.5f ? Vector3.UnitX : Vector3.UnitY;
            var v = Vector3.Cross(n, u);
            Vector3 P(float a, float b) => centre + (n + u * a + v * b) * half;
            list.AddRange(new[] { V(P(-1, -1), n, c), V(P(-1, 1), n, c), V(P(1, -1), n, c), V(P(1, -1), n, c), V(P(-1, 1), n, c), V(P(1, 1), n, c) });
        }
    }

    // Metre grid on the ground around a point: 1 m lines within minorReach, 10 m lines within majorReach.
    public static void Grid(List<LineVertex> lines, Vector3 around, float minorReach, float majorReach)
    {
        AddGrid(lines, around, 1, minorReach, Palette.Line.WithAlpha(0.6f));
        AddGrid(lines, around, 10, majorReach, Palette.LineStrong);
        Segment(lines, new Vector3(around.X - majorReach, 0.002f, 0), new Vector3(around.X + majorReach, 0.002f, 0), Palette.Hex(0xE5534B, 0.7f));
        Segment(lines, new Vector3(0, 0.002f, around.Z - majorReach), new Vector3(0, 0.002f, around.Z + majorReach), Palette.Hex(0x4B8EF5, 0.7f));
    }

    private static void AddGrid(List<LineVertex> lines, Vector3 around, float step, float reach, Vector4 color)
    {
        float x0 = MathF.Floor((around.X - reach) / step) * step, x1 = MathF.Ceiling((around.X + reach) / step) * step;
        float z0 = MathF.Floor((around.Z - reach) / step) * step, z1 = MathF.Ceiling((around.Z + reach) / step) * step;
        const float y = 0.001f;
        for (float x = x0; x <= x1 + step * 0.5f; x += step)
            Segment(lines, new Vector3(x, y, z0), new Vector3(x, y, z1), color);
        for (float z = z0; z <= z1 + step * 0.5f; z += step)
            Segment(lines, new Vector3(x0, y, z), new Vector3(x1, y, z), color);
    }

    public static void Segment(List<LineVertex> lines, Vector3 a, Vector3 b, Vector4 color)
    {
        lines.Add(new LineVertex { Position = a, Color = color });
        lines.Add(new LineVertex { Position = b, Color = color });
    }

    // The emission band of ConeAngle A B C: directions at polar B/2 and C/2 from the emitter +Y over the azimuth range
    // [0, A] from -X towards -Z (prompt 2.3), drawn as rays of the given length and the two rims.
    public static void Cone(List<LineVertex> lines, Vector3 origin, Matrix4x4 axes, Vector3 coneAngle, float length, Vector4 color)
    {
        const float rad = MathF.PI / 180f;
        float range = Math.Clamp(coneAngle.X, 0, 360);
        int steps = range <= 0 ? 1 : Math.Max(2, (int)(range / 15) + 1);
        foreach (float polarDeg in new[] { coneAngle.Y * 0.5f, coneAngle.Z * 0.5f })
        {
            Vector3? previous = null;
            for (int i = 0; i < steps; i++)
            {
                float az = steps == 1 ? 0 : range * i / (steps - 1);
                var dir = Vector3.TransformNormal(Orientation.ConeDirection(polarDeg * rad, az * rad), axes);
                var tip = origin + dir * length;
                Segment(lines, origin, tip, color);
                if (previous is { } p)
                    Segment(lines, p, tip, color);
                previous = tip;
            }
        }
    }

    // The emit volume of an emitter (EmitterParams.SpawnHalfExtent, the same extents the simulator spawns in), as
    // wire frames; a cross for Point and Cone.
    public static void Shape(List<LineVertex> lines, Vector3 origin, Matrix4x4 axes, EmitterParams p, Vector4 color)
    {
        var half = p.SpawnHalfExtent;
        Vector3 W(Vector3 local) => origin + Vector3.TransformNormal(local, axes);
        switch (p.Shape)
        {
            case ShapeKind.Box:
                for (int i = 0; i < 12; i++)
                {
                    int axis = i / 4, k = i % 4;
                    var a = new Vector3();
                    var b = new Vector3();
                    float s1 = (k & 1) != 0 ? 1 : -1, s2 = (k & 2) != 0 ? 1 : -1;
                    a[axis] = -1;
                    b[axis] = 1;
                    a[(axis + 1) % 3] = b[(axis + 1) % 3] = s1;
                    a[(axis + 2) % 3] = b[(axis + 2) % 3] = s2;
                    Segment(lines, W(a * half), W(b * half), color);
                }
                break;
            case ShapeKind.Sphere:
            case ShapeKind.Ellipse:
            case ShapeKind.OnSphere:
                Ellipse(lines, W, half, color);
                break;
            default:
                const float s = 0.05f;
                Segment(lines, W(new(-s, 0, 0)), W(new(s, 0, 0)), color);
                Segment(lines, W(new(0, -s, 0)), W(new(0, s, 0)), color);
                Segment(lines, W(new(0, 0, -s)), W(new(0, 0, s)), color);
                break;
        }
    }

    private static void Ellipse(List<LineVertex> lines, Func<Vector3, Vector3> world, Vector3 r, Vector4 color)
    {
        const int n = 32;
        for (int plane = 0; plane < 3; plane++)
        {
            for (int i = 0; i < n; i++)
            {
                float a0 = i * MathF.Tau / n, a1 = (i + 1) * MathF.Tau / n;
                Vector3 P(float a) => plane switch
                {
                    0 => new Vector3(MathF.Cos(a) * r.X, MathF.Sin(a) * r.Y, 0),
                    1 => new Vector3(0, MathF.Cos(a) * r.Y, MathF.Sin(a) * r.Z),
                    _ => new Vector3(MathF.Cos(a) * r.X, 0, MathF.Sin(a) * r.Z),
                };
                Segment(lines, world(P(a0)), world(P(a1)), color);
            }
        }
    }

    // The movement circle's path (SourceMotion.Circle).
    public static void Circle(List<LineVertex> lines, SourceMotion.Circle circle, Vector4 color)
    {
        const int n = 64;
        float period = circle.Speed > 0 ? MathF.Tau * circle.Radius / circle.Speed : 1;
        for (int i = 0; i < n; i++)
            Segment(lines, circle.At(period * i / n).Item1, circle.At(period * (i + 1) / n).Item1, color);
    }
}
