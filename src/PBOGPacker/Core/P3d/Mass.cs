using System.Numerics;

namespace PolyLab.PBOGPacker.Core.P3d;

// Point masses for the geometry LOD of a converted model. An ODOL keeps only the total mass M, the centre of mass c and
// the inverse inertia tensor, which binarize computes over the geometry LOD's points as c = Σ m p / M and
// I = Σ m (|r|² E − r rᵀ), r = p − c. Of all non-negative masses that give back these ten numbers, the one closest to an
// even spread m0 is wanted: minimise |m − m0|² subject to A m = b, m ≥ 0, whose solution is m = max(0, m0 + Aᵀλ) for ten
// multipliers λ, found by a semismooth Newton iteration on A·max(0, m0 + Aᵀλ) = b. Near the edge of what is reachable
// the iteration can stall; then Lawson-Hanson NNLS, which meets the numbers whenever any non-negative masses can (with
// few points carrying the mass), is taken if it does better. Some vanilla models store values no non-negative masses
// give (inertia with negative entries, a centre of mass outside the geometry); then the total mass and the centre of
// mass are met on their own and the inertia follows from them.
public static class Mass
{
    public sealed record Result(float[] Masses, double MassError, double ComError, double InertiaError);

    // The iteration converges in a handful of steps; the cap only stops a case that has no exact solution.
    private const int MaxSteps = 100;

    // A fit counts as exact when its scaled misfit is at the rounding of the stored floats (about seven digits).
    private const double Exact = 1e-6;

    public static Result Solve(IReadOnlyList<Vector3> points, float mass, Vector3 com, float[] invInertia)
    {
        var inertia = Invert(invInertia);
        var (a, b) = System(points, mass, com, inertia == null ? 4 : 10, inertia);
        var m = Fit(a, b, mass / (double)points.Count);
        if (inertia != null && Misfit(a, b, m) > Exact)
        {
            var (a4, b4) = System(points, mass, com, 4, null);
            m = Fit(a4, b4, mass / (double)points.Count);
        }
        // A centre of mass outside the geometry cannot be met; scaling to the stored total keeps the closest one found.
        var sum = m.Sum();
        if (sum > 0 && Math.Abs(sum - mass) > Exact * mass)
            for (var k = 0; k < m.Length; k++) m[k] *= mass / sum;

        double total = m.Sum(), cx = 0, cy = 0, cz = 0;
        for (var k = 0; k < m.Length; k++)
        {
            cx += m[k] * points[k].X;
            cy += m[k] * points[k].Y;
            cz += m[k] * points[k].Z;
        }
        var centre = total > 0 ? new Vector3((float)(cx / total), (float)(cy / total), (float)(cz / total)) : Vector3.Zero;
        var inertiaError = 0.0;
        if (inertia != null)
        {
            var (ai, bi) = System(points, mass, com, 10, inertia);
            double diff = 0, norm = 0;
            for (var i = 4; i < 10; i++)
            {
                var v = -bi[i];
                for (var k = 0; k < m.Length; k++) v += ai[i][k] * m[k];
                diff += v * v;
                norm += bi[i] * bi[i];
            }
            inertiaError = norm > 0 ? Math.Sqrt(diff / norm) : 0;
        }
        return new Result(m.Select(v => (float)v).ToArray(), mass > 0 ? Math.Abs(total - mass) / mass : 0, (centre - com).Length(),
            inertiaError);
    }

    // The equations A m = b (total mass, the three moments, the six inertia terms about c), each row scaled to unit
    // length so that kg, kg·m and kg·m² weigh the same.
    private static (double[][] A, double[] B) System(IReadOnlyList<Vector3> points, float mass, Vector3 com, int rows, double[,]? inertia)
    {
        var n = points.Count;
        var a = new double[rows][];
        var b = new double[rows];
        for (var i = 0; i < rows; i++) a[i] = new double[n];
        for (var k = 0; k < n; k++)
        {
            double x = points[k].X, y = points[k].Y, z = points[k].Z;
            double rx = x - com.X, ry = y - com.Y, rz = z - com.Z;
            a[0][k] = 1;
            a[1][k] = x;
            a[2][k] = y;
            a[3][k] = z;
            if (rows < 10) continue;
            a[4][k] = ry * ry + rz * rz;
            a[5][k] = rx * rx + rz * rz;
            a[6][k] = rx * rx + ry * ry;
            a[7][k] = -rx * ry;
            a[8][k] = -rx * rz;
            a[9][k] = -ry * rz;
        }
        b[0] = mass;
        b[1] = mass * com.X;
        b[2] = mass * com.Y;
        b[3] = mass * com.Z;
        if (rows == 10 && inertia != null)
        {
            b[4] = inertia[0, 0];
            b[5] = inertia[1, 1];
            b[6] = inertia[2, 2];
            b[7] = inertia[0, 1];
            b[8] = inertia[0, 2];
            b[9] = inertia[1, 2];
        }
        for (var i = 0; i < rows; i++)
        {
            var norm = Math.Sqrt(a[i].Sum(v => v * v));
            if (norm == 0) continue;
            for (var k = 0; k < n; k++) a[i][k] /= norm;
            b[i] /= norm;
        }
        return (a, b);
    }

    private static double Misfit(double[][] a, double[] b, double[] m)
    {
        double sum = 0, scale = 0;
        for (var i = 0; i < b.Length; i++)
        {
            var v = -b[i];
            for (var k = 0; k < m.Length; k++) v += a[i][k] * m[k];
            sum += v * v;
            scale += b[i] * b[i];
        }
        return scale > 0 ? Math.Sqrt(sum / scale) : 0;
    }

    private static double[] Fit(double[][] a, double[] b, double m0)
    {
        var m = Newton(a, b, m0);
        if (Misfit(a, b, m) <= Exact) return m;
        var sparse = Nnls(a, b);
        return Misfit(a, b, sparse) < Misfit(a, b, m) ? sparse : m;
    }

    private static double[] Newton(double[][] a, double[] b, double m0)
    {
        var rows = b.Length;
        var n = a[0].Length;
        var lambda = new double[rows];
        var m = new double[n];
        double Residual(double[] lam, double[] res)
        {
            for (var k = 0; k < n; k++)
            {
                var v = m0;
                for (var i = 0; i < rows; i++) v += a[i][k] * lam[i];
                m[k] = Math.Max(0, v);
            }
            var sum = 0.0;
            for (var i = 0; i < rows; i++)
            {
                var v = -b[i];
                for (var k = 0; k < n; k++) v += a[i][k] * m[k];
                res[i] = v;
                sum += v * v;
            }
            return Math.Sqrt(sum);
        }

        var f = new double[rows];
        var trial = new double[rows];
        var ft = new double[rows];
        var scale = Math.Sqrt(b.Sum(v => v * v));
        var r = Residual(lambda, f);
        for (var step = 0; step < MaxSteps && r > scale * 1e-12; step++)
        {
            var j = new double[rows, rows];
            for (var k = 0; k < n; k++)
            {
                if (m[k] <= 0) continue;
                for (var p = 0; p < rows; p++)
                    for (var q = 0; q < rows; q++) j[p, q] += a[p][k] * a[q][k];
            }
            var delta = SolveSym(j, f.Select(v => -v).ToArray());
            var t = 1.0;
            double rt;
            while (true)
            {
                for (var i = 0; i < rows; i++) trial[i] = lambda[i] + t * delta[i];
                rt = Residual(trial, ft);
                if (rt < (1 - t / 2) * r || t < 1e-10) break;
                t /= 2;
            }
            if (rt >= r) break;
            Array.Copy(trial, lambda, rows);
            Array.Copy(ft, f, rows);
            r = rt;
        }
        Residual(lambda, f);
        return m;
    }

    // Lawson-Hanson: least squares over non-negative masses. Each outer step frees the mass with the steepest gain, the
    // inner loop steps back to the boundary when the free least-squares fit turns a mass negative.
    private static double[] Nnls(double[][] a, double[] b)
    {
        var rows = b.Length;
        var n = a[0].Length;
        var x = new double[n];
        var free = new bool[n];
        var tol = 1e-12 * Math.Sqrt(b.Sum(v => v * v));
        double[] Gradient()
        {
            var res = new double[rows];
            for (var i = 0; i < rows; i++)
            {
                res[i] = b[i];
                for (var k = 0; k < n; k++) res[i] -= a[i][k] * x[k];
            }
            var g = new double[n];
            for (var k = 0; k < n; k++)
                for (var i = 0; i < rows; i++) g[k] += a[i][k] * res[i];
            return g;
        }
        double[] FreeFit()
        {
            var idx = Enumerable.Range(0, n).Where(k => free[k]).ToArray();
            var g = new double[idx.Length, idx.Length];
            var h = new double[idx.Length];
            for (var p = 0; p < idx.Length; p++)
            {
                for (var i = 0; i < rows; i++) h[p] += a[i][idx[p]] * b[i];
                for (var q = 0; q < idx.Length; q++)
                    for (var i = 0; i < rows; i++) g[p, q] += a[i][idx[p]] * a[i][idx[q]];
            }
            var s = SolveSym(g, h);
            var z = new double[n];
            for (var p = 0; p < idx.Length; p++) z[idx[p]] = s[p];
            return z;
        }

        for (var outer = 0; outer < 3 * n; outer++)
        {
            var w = Gradient();
            var j = -1;
            for (var k = 0; k < n; k++)
                if (!free[k] && w[k] > tol && (j < 0 || w[k] > w[j])) j = k;
            if (j < 0) break;
            free[j] = true;
            while (true)
            {
                var z = FreeFit();
                var alpha = 1.0;
                var stop = -1;
                for (var k = 0; k < n; k++)
                    if (free[k] && z[k] <= 0 && x[k] / (x[k] - z[k]) < alpha)
                    {
                        alpha = x[k] / (x[k] - z[k]);
                        stop = k;
                    }
                if (stop < 0)
                {
                    x = z;
                    break;
                }
                // The mass that reached zero leaves the free set even if rounding left it a hair above.
                for (var k = 0; k < n; k++) x[k] += alpha * (z[k] - x[k]);
                x[stop] = 0;
                for (var k = 0; k < n; k++)
                    if (free[k] && x[k] <= 0)
                    {
                        free[k] = false;
                        x[k] = 0;
                    }
            }
        }
        return x;
    }

    private static double[,]? Invert(float[] s)
    {
        double a = s[0], b = s[1], c = s[2], d = s[3], e = s[4], f = s[5], g = s[6], h = s[7], k = s[8];
        var det = a * (e * k - f * h) - b * (d * k - f * g) + c * (d * h - e * g);
        if (!(Math.Abs(det) > 0) || double.IsNaN(det)) return null;
        return new[,]
        {
            { (e * k - f * h) / det, (c * h - b * k) / det, (b * f - c * e) / det },
            { (f * g - d * k) / det, (a * k - c * g) / det, (c * d - a * f) / det },
            { (d * h - e * g) / det, (b * g - a * h) / det, (a * e - b * d) / det },
        };
    }

    // Gaussian elimination with full pivoting; a pivot at rounding level means the equation repeats others (a flat or
    // linear geometry LOD), so its unknown stays 0 and the step is the least-norm one in the other directions.
    private static double[] SolveSym(double[,] j, double[] rhs)
    {
        var n = rhs.Length;
        var m = (double[,])j.Clone();
        var y = (double[])rhs.Clone();
        var col = Enumerable.Range(0, n).ToArray();
        var max = 0.0;
        for (var i = 0; i < n; i++) max = Math.Max(max, Math.Abs(m[i, i]));
        var rank = 0;
        for (; rank < n; rank++)
        {
            int pr = rank, pc = rank;
            for (var r = rank; r < n; r++)
                for (var c = rank; c < n; c++)
                    if (Math.Abs(m[r, c]) > Math.Abs(m[pr, pc])) { pr = r; pc = c; }
            if (!(Math.Abs(m[pr, pc]) > max * 1e-14)) break;
            for (var c = 0; c < n; c++) (m[rank, c], m[pr, c]) = (m[pr, c], m[rank, c]);
            (y[rank], y[pr]) = (y[pr], y[rank]);
            for (var r = 0; r < n; r++) (m[r, rank], m[r, pc]) = (m[r, pc], m[r, rank]);
            (col[rank], col[pc]) = (col[pc], col[rank]);
            for (var r = rank + 1; r < n; r++)
            {
                var factor = m[r, rank] / m[rank, rank];
                if (factor == 0) continue;
                for (var c = rank; c < n; c++) m[r, c] -= factor * m[rank, c];
                y[r] -= factor * y[rank];
            }
        }
        var x = new double[n];
        for (var i = rank - 1; i >= 0; i--)
        {
            var v = y[i];
            for (var c = i + 1; c < rank; c++) v -= m[i, c] * x[c];
            x[i] = v / m[i, i];
        }
        var result = new double[n];
        for (var i = 0; i < n; i++) result[col[i]] = x[i];
        return result;
    }
}
