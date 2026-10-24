using System.Numerics;

namespace Pfx.Render;

public enum ViewPreset { Free, Top, Side, Front, Eye }

// Orbit camera in DayZ axes (X east, Y up, Z north, left-handed). Yaw is a compass heading in degrees (0 = looking
// north, + = clockwise seen from above, like the game), pitch is up from the horizon. Fly mode moves the target with
// the eye. Basis as in the P3 probe camera (trail.py): right = (d.z, 0, -d.x), up = d x right.
public sealed class Camera
{
    // R0 capture: near plane 0.07 m, depth range 1588 m (vDepthRange of the DayZDiag frames).
    public const float NearPlane = 0.07f;
    public const float FarPlane = 1588f;
    // The vanilla Particle Editor's FOV dialog: default 45, range 5–120 (W0).
    public const float DefaultFov = 45f, MinFov = 5f, MaxFov = 120f;
    // Player eye height for the eye-level preset (prompt 3.5).
    public const float EyeHeight = 1.7f;

    public Vector3 Target { get; set; } = new(0, 1, 0);
    public float Yaw { get; set; } = 30;
    public float Pitch { get; set; } = -20;
    public float Distance { get; set; } = 8;

    private float _fov = DefaultFov;

    // Vertical field of view in degrees.
    public float Fov
    {
        get => _fov;
        set => _fov = Math.Clamp(value, MinFov, MaxFov);
    }

    public Vector3 Forward
    {
        get
        {
            float y = Yaw * MathF.PI / 180f, p = Pitch * MathF.PI / 180f;
            return new Vector3(MathF.Sin(y) * MathF.Cos(p), MathF.Sin(p), MathF.Cos(y) * MathF.Cos(p));
        }
    }

    public Vector3 Eye => Target - Forward * Distance;

    public (Vector3 Right, Vector3 Up, Vector3 Forward) Basis()
    {
        var d = Forward;
        var r = new Vector3(d.Z, 0, -d.X);
        r = r.LengthSquared() > 1e-12f ? Vector3.Normalize(r) : new Vector3(MathF.Cos(Yaw * MathF.PI / 180f), 0, -MathF.Sin(Yaw * MathF.PI / 180f));
        return (r, Vector3.Cross(d, r), d);
    }

    // Row-vector view matrix (System.Numerics convention: v * M).
    public Matrix4x4 View()
    {
        var (r, u, f) = Basis();
        var e = Eye;
        return new Matrix4x4(
            r.X, u.X, f.X, 0,
            r.Y, u.Y, f.Y, 0,
            r.Z, u.Z, f.Z, 0,
            -Vector3.Dot(r, e), -Vector3.Dot(u, e), -Vector3.Dot(f, e), 1);
    }

    // Left-handed perspective with reverse Z (near -> 1, far -> 0), as the game's depth buffer (R0).
    public Matrix4x4 Projection(float aspect)
    {
        float ys = 1f / MathF.Tan(Fov * MathF.PI / 360f);
        float xs = ys / aspect;
        float a = NearPlane / (NearPlane - FarPlane);
        float b = -FarPlane * a;
        return new Matrix4x4(
            xs, 0, 0, 0,
            0, ys, 0, 0,
            0, 0, a, 1,
            0, 0, b, 0);
    }

    // Focal length in pixels for an image height (the inverse of FovFromFocal).
    public float FocalPixels(float heightPx) => heightPx * 0.5f / MathF.Tan(Fov * MathF.PI / 360f);

    // A world point in pixels from the top-left of a width x height image, with its depth along the view (metres);
    // null when it lies behind the near plane.
    public Vector3? Project(Vector3 world, float width, float height)
    {
        var (r, u, f) = Basis();
        var d = world - Eye;
        float z = Vector3.Dot(d, f);
        if (z < NearPlane)
            return null;
        float focal = FocalPixels(height);
        return new Vector3(width * 0.5f + Vector3.Dot(d, r) / z * focal, height * 0.5f - Vector3.Dot(d, u) / z * focal, z);
    }

    // The ray from the eye through a pixel (unit direction).
    public (Vector3 Origin, Vector3 Direction) Ray(float px, float py, float width, float height)
    {
        var (r, u, f) = Basis();
        float focal = FocalPixels(height);
        var dir = f + r * ((px - width * 0.5f) / focal) - u * ((py - height * 0.5f) / focal);
        return (Eye, Vector3.Normalize(dir));
    }

    // Metres one pixel spans at a view depth.
    public float MetresPerPixel(float depth, float heightPx) => depth / FocalPixels(heightPx);

    // Vertical field of view for a focal length in pixels at a given image height (P3: f = 719 px at 1080).
    public static float FovFromFocal(float focalPx, float heightPx) => 2f * MathF.Atan(heightPx * 0.5f / focalPx) * 180f / MathF.PI;

    public void LookAt(Vector3 eye, Vector3 target)
    {
        var d = target - eye;
        float len = d.Length();
        if (len < 1e-6f)
            return;
        d /= len;
        Target = target;
        Distance = len;
        Pitch = MathF.Asin(Math.Clamp(d.Y, -1f, 1f)) * 180f / MathF.PI;
        Yaw = MathF.Atan2(d.X, d.Z) * 180f / MathF.PI;
    }

    public void Orbit(float dYaw, float dPitch)
    {
        Yaw += dYaw;
        Pitch = Math.Clamp(Pitch + dPitch, -89.9f, 89.9f);
    }

    // Turns the view around the eye (fly mode): the eye stays, the target moves.
    public void Look(float dYaw, float dPitch)
    {
        var eye = Eye;
        Orbit(dYaw, dPitch);
        Target = eye + Forward * Distance;
    }

    public void Pan(float rightMetres, float upMetres)
    {
        var (r, u, _) = Basis();
        Target += r * rightMetres + u * upMetres;
    }

    public void Zoom(float factor) => Distance = Math.Clamp(Distance * factor, 0.1f, FarPlane * 0.5f);

    public void Fly(Vector3 local)
    {
        var (r, u, f) = Basis();
        Target += r * local.X + u * local.Y + f * local.Z;
    }

    // Presets around a point of interest (usually the effect source); distance kept from the current view.
    public void Apply(ViewPreset preset, Vector3 focus)
    {
        switch (preset)
        {
            case ViewPreset.Top:
                Target = focus;
                Yaw = 0;
                Pitch = -89.9f;
                break;
            case ViewPreset.Side:
                Target = focus;
                Yaw = -90;
                Pitch = 0;
                break;
            case ViewPreset.Front:
                Target = focus;
                Yaw = 0;
                Pitch = 0;
                break;
            case ViewPreset.Eye:
                Target = new Vector3(focus.X, EyeHeight, focus.Z);
                Yaw = 0;
                Pitch = 0;
                break;
            default:
                Target = focus;
                Yaw = 30;
                Pitch = -20;
                break;
        }
    }
}
