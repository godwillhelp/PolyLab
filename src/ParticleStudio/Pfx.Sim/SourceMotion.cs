using System.Numerics;

namespace Pfx.Sim;

public enum CircleAxis { X, Y, Z }

// Where the particle source is at effect time t: position, yaw (degrees, + = clockwise seen from above, prompt 2.3)
// and velocity (analytic, used by VelAffect and BirthRateVel).
public abstract record SourceMotion
{
    public abstract (Vector3 Position, float Yaw, Vector3 Velocity) At(float t);

    public static SourceMotion Still(Vector3 position = default, float yaw = 0) => new Line(position, Vector3.Zero, yaw);

    public sealed record Line(Vector3 Start, Vector3 Velocity, float Yaw) : SourceMotion
    {
        public override (Vector3, float, Vector3) At(float t) => (Start + Velocity * t, Yaw, Velocity);
    }

    public sealed record Turning(Vector3 Position, float Yaw0, float DegreesPerSecond) : SourceMotion
    {
        public override (Vector3, float, Vector3) At(float t) => (Position, Yaw0 + DegreesPerSecond * t, Vector3.Zero);
    }

    // The Workbench movement simulation (prompt 2.13): a circle of Radius at Speed m/s in the plane normal to Axis,
    // starting at Center + Radius along the first in-plane axis. Orientation stays fixed.
    public sealed record Circle(Vector3 Center, float Radius, float Speed, CircleAxis Axis) : SourceMotion
    {
        public override (Vector3, float, Vector3) At(float t)
        {
            if (Radius <= 0)
                return (Center, 0, Vector3.Zero);
            float w = Speed / Radius;
            float c = MathF.Cos(w * t), s = MathF.Sin(w * t);
            (Vector3 u, Vector3 v) = Axis switch
            {
                CircleAxis.X => (Vector3.UnitY, Vector3.UnitZ),
                CircleAxis.Y => (Vector3.UnitX, Vector3.UnitZ),
                _ => (Vector3.UnitX, Vector3.UnitY),
            };
            return (Center + Radius * (c * u + s * v), 0, Speed * (-s * u + c * v));
        }
    }

    // A drawn source path (spec 3.7 use 3): Points at Times (seconds from 0, increasing), straight between keys,
    // starting over after the last key. With YawAlongPath the source faces its direction of travel (yaw = compass
    // heading, 0 = +Z north, + = clockwise seen from above); otherwise it keeps Yaw.
    public sealed record Keyframed(float[] Times, Vector3[] Points, bool YawAlongPath, float Yaw) : SourceMotion
    {
        public override (Vector3, float, Vector3) At(float t)
        {
            if (Points.Length == 1 || Times[^1] <= 0)
                return (Points[0], Yaw, Vector3.Zero);
            float u = t % Times[^1];
            if (u < 0)
                u += Times[^1];
            int i = Array.BinarySearch(Times, u);
            i = i >= 0 ? Math.Min(i, Times.Length - 2) : Math.Clamp(~i - 1, 0, Times.Length - 2);
            float span = Times[i + 1] - Times[i];
            var velocity = span > 0 ? (Points[i + 1] - Points[i]) / span : Vector3.Zero;
            var position = Points[i] + velocity * (u - Times[i]);
            float yaw = YawAlongPath && velocity.X * velocity.X + velocity.Z * velocity.Z > 1e-12f
                ? MathF.Atan2(velocity.X, velocity.Z) * (180f / MathF.PI)
                : Yaw;
            return (position, yaw, velocity);
        }
    }
}
