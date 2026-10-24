using System.Numerics;

namespace Pfx.Sim;

// A drawn particle flight fitted with the simulator's own step (Ballistics): launch velocity, gravity multiplier and
// linear drag. Errors are the distances from each drawn point to the fitted path, in metres.
public sealed record BallisticFit(Vector3 Velocity, float GravityMultiply, float AirResistance, float[] Errors,
    Vector3[] Path, int Iterations, bool Converged)
{
    public float Rms => Errors.Length == 0 ? 0 : MathF.Sqrt(Errors.Sum(e => e * e) / Errors.Length);
    public float Max => Errors.Length == 0 ? 0 : Errors.Max();
}

// A drawn circle or helix around a centre (spec 3.7 use 4): radius, height at heading 0, rise per degree of turn,
// turn rate from the drawing time (degrees per second, + = clockwise seen from above like yaw) and the heading of the
// first point (compass, 0 = +Z).
public sealed record OrbitFit(float Radius, float Height, float RisePerDegree, float DegreesPerSecond, float StartHeading,
    float[] Errors)
{
    public float Max => Errors.Length == 0 ? 0 : Errors.Max();
}

public static class PathFit
{
    // A fit for one drag stops after this many rounds even if it still improves: a guard against input it cannot
    // settle (a round is one linear solve, one simulated path and one projection of every drawn point; journal P8
    // lists the rounds real strokes took).
    public const int MaxRounds = 200;

    // ---- geometry ----

    public static float Length(IReadOnlyList<Vector3> points)
    {
        float l = 0;
        for (int i = 1; i < points.Count; i++)
            l += Vector3.Distance(points[i - 1], points[i]);
        return l;
    }

    // Nearest point of a polyline: (distance, segment index, fraction along the segment).
    public static (float Distance, int Segment, float Along) Nearest(Vector3 p, IReadOnlyList<Vector3> line)
    {
        if (line.Count == 1)
            return (Vector3.Distance(p, line[0]), 0, 0);
        float best = float.MaxValue;
        int seg = 0;
        float along = 0;
        for (int i = 0; i + 1 < line.Count; i++)
        {
            var a = line[i];
            var ab = line[i + 1] - a;
            float len2 = ab.LengthSquared();
            float t = len2 > 0 ? Math.Clamp(Vector3.Dot(p - a, ab) / len2, 0, 1) : 0;
            float d = Vector3.DistanceSquared(p, a + ab * t);
            if (d < best)
            {
                best = d;
                seg = i;
                along = t;
            }
        }
        return (MathF.Sqrt(best), seg, along);
    }

    public static float[] Distances(IReadOnlyList<Vector3> points, IReadOnlyList<Vector3> line) =>
        points.Select(p => Nearest(p, line).Distance).ToArray();

    // N points evenly spaced along the polyline by length, first and last included.
    public static Vector3[] Resample(IReadOnlyList<Vector3> points, int n)
    {
        var result = new Vector3[n];
        float total = Length(points);
        if (n == 1 || total <= 0)
        {
            for (int k = 0; k < n; k++)
                result[k] = points[0];
            return result;
        }
        int i = 0;
        float walked = 0;
        for (int k = 0; k < n; k++)
        {
            float target = total * k / (n - 1);
            while (i + 2 < points.Count && walked + Vector3.Distance(points[i], points[i + 1]) < target)
            {
                walked += Vector3.Distance(points[i], points[i + 1]);
                i++;
            }
            float seg = Vector3.Distance(points[i], points[i + 1]);
            result[k] = seg > 0 ? Vector3.Lerp(points[i], points[i + 1], Math.Clamp((target - walked) / seg, 0, 1)) : points[i];
        }
        return result;
    }

    // Douglas-Peucker: the indices of the points kept so that no dropped point lies further than tolerance from the
    // kept polyline (first and last always kept).
    public static List<int> Simplify(IReadOnlyList<Vector3> points, float tolerance)
    {
        var keep = new bool[points.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int, int)>();
        stack.Push((0, points.Count - 1));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            float worst = -1;
            int at = -1;
            for (int i = a + 1; i < b; i++)
            {
                float d = Nearest(points[i], new[] { points[a], points[b] }).Distance;
                if (d > worst)
                {
                    worst = d;
                    at = i;
                }
            }
            if (at >= 0 && worst > tolerance)
            {
                keep[at] = true;
                stack.Push((a, at));
                stack.Push((at, b));
            }
        }
        return Enumerable.Range(0, points.Count).Where(i => keep[i]).ToList();
    }

    // ---- ballistic flight (spec 3.7 use 2) ----

    // The drawn points are the wanted flight of one particle born at start (the first drawn point is replaced by it)
    // and dying at the last drawn point after `life` seconds: the life fixes the time scale, which the shape of a
    // path alone does not (the same curve is flown faster with a stronger gravity). For a given drag the simulator's
    // flight is linear in the launch velocity and the gravity multiplier (Flight), so for given point times those come
    // from one least-squares solve; each point's time then moves to the nearest point of the new path, until the rms
    // distance improves by less than tolerance. The drag is searched on a log grid and refined by golden section.
    public static BallisticFit Ballistic(IReadOnlyList<Vector3> drawn, Vector3 start, float life, SimOptions o, float tolerance,
        CancellationToken cancel = default)
    {
        if (drawn.Count < 3)
            throw new ArgumentException("A flight needs at least three drawn points.");
        int steps = Ballistics.Steps(life, o.Step);
        float end = (steps - 1) * o.Step;
        if (end <= 0)
            throw new ArgumentException("The particle life is shorter than two simulation steps.");
        var q = drawn.ToArray();
        q[0] = start;
        int rounds = 0;
        bool converged = true;
        Solution Solve(float k)
        {
            var s = AtDrag(q, start, life, end, k, o, tolerance, cancel);
            rounds += s.Rounds;
            converged &= s.Converged;
            return s;
        }
        // Drag from none to 50 / life (the speed falls to e^-50 of the launch: nothing of the path is left to shape),
        // 24 log steps from 0.01 / life; the best grid point and its neighbours bracket the golden-section search.
        var grid = new List<float> { 0 };
        for (int i = 0; i < 24; i++)
            grid.Add(0.01f / life * MathF.Pow(5000, i / 23f));
        var tried = grid.Select(k => (K: k, S: Solve(k))).ToList();
        int best = 0;
        for (int i = 1; i < tried.Count; i++)
            if (tried[i].S.Rms < tried[best].S.Rms)
                best = i;
        float lo = tried[Math.Max(0, best - 1)].K, hi = tried[Math.Min(tried.Count - 1, best + 1)].K;
        var bestSolution = tried[best];
        // Golden section until a drag step moves the end of the flight less than the tolerance (about k v life^2 / 2).
        const float golden = 0.618034f;
        float a = hi - golden * (hi - lo), b = lo + golden * (hi - lo);
        var sa = Solve(a);
        var sb = Solve(b);
        float speed = Math.Max(bestSolution.S.Velocity.Length(), 1);
        while ((hi - lo) * speed * life * life * 0.5f >= tolerance && !cancel.IsCancellationRequested)
        {
            if (sa.Rms < sb.Rms)
            {
                hi = b;
                b = a;
                sb = sa;
                a = hi - golden * (hi - lo);
                sa = Solve(a);
            }
            else
            {
                lo = a;
                a = b;
                sa = sb;
                b = lo + golden * (hi - lo);
                sb = Solve(b);
            }
        }
        if (sa.Rms < bestSolution.S.Rms)
            bestSolution = (a, sa);
        if (sb.Rms < bestSolution.S.Rms)
            bestSolution = (b, sb);
        var (drag, sol) = bestSolution;
        var fitted = Ballistics.Path(start, sol.Velocity, sol.Gravity, drag, life, o);
        return new BallisticFit(sol.Velocity, sol.Gravity, drag, Distances(drawn, fitted), fitted, rounds, converged);
    }

    private sealed record Solution(Vector3 Velocity, float Gravity, float Rms, int Rounds, bool Converged);

    // For one drag k: alternate the linear solve and the nearest-point times (times start in proportion to the length
    // along the stroke), at most MaxRounds rounds.
    private static Solution AtDrag(Vector3[] q, Vector3 start, float life, float end, float k, SimOptions o, float tolerance, CancellationToken cancel)
    {
        int m = q.Length;
        var (alpha, beta) = Flight(k, Ballistics.Steps(life, o.Step), o.Step);
        var times = new float[m];
        float total = Length(q), walked = 0;
        for (int i = 1; i < m; i++)
        {
            walked += Vector3.Distance(q[i - 1], q[i]);
            times[i] = total > 0 ? end * walked / total : end * i / (m - 1);
        }
        Vector3 v = default;
        float g = 0, last = float.MaxValue;
        int rounds = 0;
        while (rounds < MaxRounds && !cancel.IsCancellationRequested)
        {
            rounds++;
            (v, g) = Linear(q, times, start, alpha, beta, o);
            var path = Ballistics.Path(start, v, g, k, life, o);
            for (int i = 1; i < m - 1; i++)
            {
                var (_, seg, along) = Nearest(q[i], path);
                times[i] = Math.Clamp((seg + along) * o.Step, times[i - 1], end);
            }
            float rms = MathF.Sqrt(Distances(q, path).Sum(e => e * e) / m);
            if (last - rms < tolerance)
                return new Solution(v, g, Math.Min(rms, last), rounds, true);
            last = rms;
        }
        return new Solution(v, g, last, rounds, false);
    }

    // The simulator's step (Ballistics.Step) from start with launch velocity v and gravity multiplier g under drag k
    // (decay d = e^-k dt per step) gives p_n = start + v alpha_n + g (0, -G, 0) beta_n, because
    // v_n = d^n v + a dt s_n with s_n = d + .. + d^n, and p_n adds dt v_j for j = 1..n: alpha_n = dt s_n,
    // beta_n = dt^2 (s_1 + .. + s_n).
    private static (double[] Alpha, double[] Beta) Flight(float k, int steps, float dt)
    {
        double d = Math.Exp(-k * dt), dn = 1, s = 0;
        var alpha = new double[steps];
        var beta = new double[steps];
        for (int n = 1; n < steps; n++)
        {
            dn *= d;
            s += dn;
            alpha[n] = dt * s;
            beta[n] = beta[n - 1] + dt * dt * s;
        }
        return (alpha, beta);
    }

    private static double Interp(double[] f, float t, float dt)
    {
        float x = t / dt;
        int i = Math.Clamp((int)x, 0, f.Length - 1);
        return i + 1 >= f.Length ? f[^1] : f[i] + (f[i + 1] - f[i]) * (x - i);
    }

    // Least squares for v (x and z one unknown each) and (v.y, g) from the rows of all points but the start.
    private static (Vector3 V, float G) Linear(Vector3[] q, float[] t, Vector3 start, double[] alpha, double[] beta, SimOptions o)
    {
        double saa = 0, sx = 0, sz = 0, a12 = 0, a22 = 0, b1 = 0, b2 = 0;
        for (int i = 1; i < q.Length; i++)
        {
            double a = Interp(alpha, t[i], o.Step), h = -o.Gravity * Interp(beta, t[i], o.Step);
            var d = q[i] - start;
            saa += a * a;
            sx += a * d.X;
            sz += a * d.Z;
            a12 += a * h;
            a22 += h * h;
            b1 += a * d.Y;
            b2 += h * d.Y;
        }
        double det = saa * a22 - a12 * a12;
        double vy = det != 0 ? (b1 * a22 - b2 * a12) / det : (saa > 0 ? b1 / saa : 0);
        double g = det != 0 ? (saa * b2 - a12 * b1) / det : 0;
        return (new Vector3(saa > 0 ? (float)(sx / saa) : 0, (float)vy, saa > 0 ? (float)(sz / saa) : 0), (float)g);
    }

    // ---- orbit (spec 3.7 use 4) ----

    // Least squares with the centre fixed (the source turns about its own position): radius = mean horizontal
    // distance; height and rise per degree by a straight-line fit of height over the unwrapped heading.
    public static OrbitFit Orbit(IReadOnlyList<Vector3> points, IReadOnlyList<float> times, Vector3 centre)
    {
        const float deg = 180f / MathF.PI;
        int n = points.Count;
        var heading = new float[n];
        var radius = new float[n];
        for (int i = 0; i < n; i++)
        {
            var d = points[i] - centre;
            radius[i] = MathF.Sqrt(d.X * d.X + d.Z * d.Z);
            float h = MathF.Atan2(d.X, d.Z) * deg;
            if (i > 0)
                h = heading[i - 1] + Orientation.Wrap(h - heading[i - 1]);
            heading[i] = h;
        }
        float r = radius.Average();
        double mh = heading.Average(), my = points.Average(p => p.Y - centre.Y);
        double shh = 0, shy = 0;
        for (int i = 0; i < n; i++)
        {
            shh += (heading[i] - mh) * (heading[i] - mh);
            shy += (heading[i] - mh) * (points[i].Y - centre.Y - my);
        }
        float rise = shh > 0 ? (float)(shy / shh) : 0;
        float height = (float)(my - rise * mh);
        float duration = times[^1] - times[0];
        float rate = duration > 0 ? (heading[^1] - heading[0]) / duration : 0;
        var errors = new float[n];
        for (int i = 0; i < n; i++)
        {
            float dr = radius[i] - r;
            float dy = points[i].Y - centre.Y - (height + rise * heading[i]);
            errors[i] = MathF.Sqrt(dr * dr + dy * dy);
        }
        return new OrbitFit(r, height, rise, rate, heading[0], errors);
    }
}
