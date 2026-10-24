using System.Numerics;

namespace Pfx.Sim;

// DayZ axes: X east, Y up, Z north. Yaw turns about +Y, positive = clockwise seen from above, so yaw 90 takes -X
// (azimuth 0 of a cone) to +Z (verified in game, prompt 2.3). Pitch about X and roll about Z with the signs seen in
// game (P3): pitch +30 tilts +Y towards -Z, roll +30 tilts +Y towards +X. The order (P8, two-angle jets in game): yaw
// comes last, after pitch and roll (yaw + pitch and yaw + roll matched within 3-7 px, the other orders were 30-54 px
// off); roll before pitch fitted a little better than the reverse (rms 10.5 vs 11.8 px) and is not verified.
public static class Orientation
{
    public static Matrix4x4 YawPitchRoll(Vector3 anglesDeg)
    {
        const float rad = MathF.PI / 180f;
        return Roll(anglesDeg.Z * rad) * Pitch(anglesDeg.Y * rad) * Yaw(anglesDeg.X * rad);
    }

    // System.Numerics multiplies row vectors (v * M), so the first rotation applied is the leftmost factor.
    public static Matrix4x4 Yaw(float a)
    {
        float c = MathF.Cos(a), s = MathF.Sin(a);
        // x' = x c + z s, z' = -x s + z c
        return new Matrix4x4(c, 0, -s, 0, 0, 1, 0, 0, s, 0, c, 0, 0, 0, 0, 1);
    }

    public static Matrix4x4 Pitch(float a) => Matrix4x4.CreateRotationX(-a);

    public static Matrix4x4 Roll(float a) => Matrix4x4.CreateRotationZ(-a);

    // Cone direction in emitter space: polar angle from +Y, azimuth 0 = -X growing towards -Z (ConeAngle 90 180 180
    // fanned from -X to -Z in game, P3: counter-clockwise from above, the other way round from yaw).
    public static Vector3 ConeDirection(float polarRad, float azimuthRad)
    {
        float sp = MathF.Sin(polarRad);
        return new Vector3(-sp * MathF.Cos(azimuthRad), MathF.Cos(polarRad), -sp * MathF.Sin(azimuthRad));
    }

    // The emitter yaw and ConeAngle that centre the emission band on a direction given in the source's space, with
    // an aperture (full angle, degrees) around it. Only the yaw of Angles is used (pitch and roll stay 0): yaw and the
    // cone were each probed alone (prompt 2.3), so the unverified order of several angles never comes in. At yaw Y an
    // emitter azimuth f points along source azimuth f - Y, so the fan [0, A] is centred on azimuth psi by Y = A/2 - psi.
    public static (float Yaw, Vector3 Cone) Aim(Vector3 direction, float apertureDeg)
    {
        const float deg = 180f / MathF.PI;
        var d = Vector3.Normalize(direction);
        float polar = MathF.Acos(Math.Clamp(d.Y, -1f, 1f)) * deg;
        float azimuth = MathF.Atan2(-d.Z, -d.X) * deg;
        float w = Math.Clamp(apertureDeg, 0, 360);
        float inner = polar - w * 0.5f, outer = polar + w * 0.5f;
        float range;
        if (w <= 0)
            range = 0;
        else if (inner <= 0 || outer >= 180)
            range = 360;
        else
            range = Math.Min(360, w / MathF.Sin(polar / deg));
        var cone = new Vector3(range, 2 * Math.Max(0, inner), 2 * Math.Min(180, outer));
        if (range >= 360)
            return (0, cone);
        return (Wrap(range * 0.5f - azimuth), cone);
    }

    // Degrees into (-180, 180].
    public static float Wrap(float deg)
    {
        deg %= 360;
        if (deg > 180)
            deg -= 360;
        else if (deg <= -180)
            deg += 360;
        return deg;
    }
}
