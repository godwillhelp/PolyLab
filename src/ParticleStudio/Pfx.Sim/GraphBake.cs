namespace Pfx.Sim;

// Turns an authored curve (a drawn stroke, a preset formula, other channels kept as they are) into graph keys whose
// engine spline (Graph: uniform Catmull-Rom, one-sided ends, clamped 0..1) stays within a maximum error of it. The
// error is measured against the engine spline as the game shows it (both sides clamped to 0..1), not against straight
// lines between the keys (spec 3.6).
//
// For fixed key times the engine spline is linear in the key values (each value scales one basis curve), so the
// values are a least-squares fit of the authored samples, reweighted towards the smallest maximum error (Lawson's
// iteration). Key times are spread evenly, plus the authored curve's corners: the engine's tangents are in segment
// units, so even spacing suits it. The fewest keys that meet the limit win.
public static class GraphBake
{
    // The most keys a vanilla graph uses (blood_bleeding_02 BRateMast, 282 vanilla files); more are not known to work.
    public const int MaxKeys = 16;
    // Error checks on this many intervals (plus the authored curve's corners): finer than a 1080p curve editor.
    public const int Samples = 2048;
    // Keys are written with four decimals, like the editor's other generated keys.
    private const double Rounding = 1e-4;
    // Key values may go above 1 by this much: the game reads keys above 1 as 1 (P0), so a key above 1 can hold a
    // plateau at 1, and the curve editor still shows it (its value axis reaches 1.25). Never below 0: what the game
    // does with negative keys is not measured.
    private const double ValueReach = 0.25;
    // Keys closer than one texel of the engine's 256-wide graph texture (R0) describe nothing the game can show.
    private const double MinSpacing = 1.0 / 256;
    // Lawson rounds after the plain fit (it converges slowly; the best round is kept).
    private const int LawsonRounds = 12;

    // WorstAt: the time of the largest error.
    public sealed record Result(double[] Numbers, int Keys, double Error, bool WithinLimit, double WorstAt = 0);

    // f(t, channel) is the authored value at t in 0..1; corners are times where it bends sharply (always sampled,
    // and key times while they fit in MaxKeys).
    public static Result Fit(Func<double, int, double> f, int channels, double maxError, IReadOnlyList<double>? corners = null)
    {
        var inner = (corners ?? Array.Empty<double>()).Where(t => t > 0 && t < 1).Select(Round).Distinct().ToArray();
        var grid = Enumerable.Range(0, Samples + 1).Select(i => i / (double)Samples).Concat(inner).Distinct().OrderBy(t => t).ToArray();
        var target = new double[channels][];
        for (int c = 0; c < channels; c++)
            target[c] = grid.Select(t => Math.Clamp(f(t, c), 0, 1)).ToArray();
        Result? even = null;
        for (int n = 1; n < MaxKeys; n++)
        {
            var spread = Enumerable.Range(0, n + 1).Select(i => Round(i / (double)n)).ToList();
            var withCorners = spread.Concat(inner).Distinct().OrderBy(t => t).ToList();
            var chosen = withCorners.Count <= MaxKeys ? withCorners : spread;
            var r = FitTimes(chosen, grid, target, maxError);
            if (even == null || r.Error < even.Error)
                even = r;
            if (r.WithinLimit)
                break;
        }
        // Keys where the curve is hard: halve the segment that holds the worst error until the limit is met.
        var times = new List<double> { 0, 1 };
        if (inner.Length + 2 <= MaxKeys)
            times = times.Concat(inner).Distinct().OrderBy(t => t).ToList();
        var split = FitTimes(times, grid, target, maxError);
        while (!split.WithinLimit && times.Count < MaxKeys)
        {
            int k = Math.Max(0, times.FindLastIndex(t => t <= split.WorstAt));
            k = Math.Min(k, times.Count - 2);
            double mid = Round((times[k] + times[k + 1]) / 2);
            if (mid - times[k] < MinSpacing || times[k + 1] - mid < MinSpacing)
                break;
            times.Insert(k + 1, mid);
            split = FitTimes(times, grid, target, maxError);
        }
        if (even!.WithinLimit && split.WithinLimit)
            return even.Keys <= split.Keys ? even : split;
        return even.WithinLimit ? even : split.WithinLimit ? split : even.Error <= split.Error ? even : split;
    }

    private static Result FitTimes(List<double> times, double[] grid, double[][] target, double maxError)
    {
        int n = times.Count, channels = target.Length;
        // basis[k][i]: the engine spline with key k at 1 and the others at 0, at grid point i.
        var basis = new double[n][];
        for (int k = 0; k < n; k++)
        {
            var oneHot = new double[n * 2];
            for (int j = 0; j < n; j++)
            {
                oneHot[j * 2] = times[j];
                oneHot[j * 2 + 1] = j == k ? 1 : 0;
            }
            var g = Graph.FromNumbers(oneHot, 1);
            basis[k] = grid.Select(t => (double)g.Unclamped((float)t)).ToArray();
        }
        var values = new double[channels][];
        double error = 0;
        for (int c = 0; c < channels; c++)
        {
            var weights = Enumerable.Repeat(1.0, grid.Length).ToArray();
            double[]? bestValues = null;
            double bestError = double.MaxValue;
            for (int round = 0; round <= LawsonRounds; round++)
            {
                var v = Solve(basis, target[c], weights);
                var residual = Residual(basis, v, target[c]);
                double e = residual.Max();
                if (e < bestError)
                {
                    bestError = e;
                    bestValues = v;
                }
                if (e <= maxError)
                    break;
                double sum = 0;
                for (int i = 0; i < weights.Length; i++)
                    sum += weights[i] *= residual[i] + 1e-12;
                for (int i = 0; i < weights.Length; i++)
                    weights[i] *= weights.Length / sum;
            }
            values[c] = bestValues!;
            error = Math.Max(error, bestError);
        }
        var numbers = new double[n * (channels + 1)];
        for (int k = 0; k < n; k++)
        {
            numbers[k * (channels + 1)] = times[k];
            for (int c = 0; c < channels; c++)
                numbers[k * (channels + 1) + 1 + c] = values[c][k];
        }
        // The numbers as written (rounded) are what the game reads: measure those.
        var written = Graph.FromNumbers(numbers, channels);
        error = 0;
        double worstAt = 0;
        for (int i = 0; i < grid.Length; i++)
            for (int c = 0; c < channels; c++)
            {
                double e = Math.Abs(written.Evaluate((float)grid[i], c) - target[c][i]);
                if (e > error)
                {
                    error = e;
                    worstAt = grid[i];
                }
            }
        return new Result(numbers, n, error, error <= maxError, worstAt);
    }

    // Weighted least squares of the basis against the target, values rounded and kept within 0 .. 1 + ValueReach.
    private static double[] Solve(double[][] basis, double[] target, double[] weights)
    {
        int n = basis.Length;
        var a = new double[n, n + 1];
        for (int r = 0; r < n; r++)
        {
            for (int c = r; c < n; c++)
            {
                double s = 0;
                for (int i = 0; i < target.Length; i++)
                    s += weights[i] * basis[r][i] * basis[c][i];
                a[r, c] = s;
                a[c, r] = s;
            }
            double b = 0;
            for (int i = 0; i < target.Length; i++)
                b += weights[i] * basis[r][i] * target[i];
            a[r, n] = b;
        }
        for (int p = 0; p < n; p++)
        {
            int pivot = p;
            for (int r = p + 1; r < n; r++)
                if (Math.Abs(a[r, p]) > Math.Abs(a[pivot, p]))
                    pivot = r;
            for (int c = 0; c <= n; c++)
                (a[p, c], a[pivot, c]) = (a[pivot, c], a[p, c]);
            if (Math.Abs(a[p, p]) < 1e-15)
                continue;
            for (int r = 0; r < n; r++)
            {
                if (r == p)
                    continue;
                double m = a[r, p] / a[p, p];
                for (int c = p; c <= n; c++)
                    a[r, c] -= m * a[p, c];
            }
        }
        var v = new double[n];
        for (int k = 0; k < n; k++)
            v[k] = Math.Abs(a[k, k]) < 1e-15 ? 0 : Math.Clamp(Round(a[k, n] / a[k, k]), 0, 1 + ValueReach);
        return v;
    }

    // |engine (clamped) - target| at every grid point.
    private static double[] Residual(double[][] basis, double[] values, double[] target)
    {
        var r = new double[target.Length];
        for (int i = 0; i < r.Length; i++)
        {
            double s = 0;
            for (int k = 0; k < values.Length; k++)
                s += values[k] * basis[k][i];
            r[i] = Math.Abs(Math.Clamp(s, 0, 1) - target[i]);
        }
        return r;
    }

    private static double Round(double v) => Math.Round(v / Rounding) * Rounding;

    // A polyline through (t, v) points (a drawn stroke), sorted by t; flat beyond its ends.
    public static Func<double, double> Polyline(IReadOnlyList<(double T, double V)> points)
    {
        var p = points.Where(q => double.IsFinite(q.T) && double.IsFinite(q.V)).OrderBy(q => q.T).ToArray();
        if (p.Length == 0)
            return _ => 1;
        return t =>
        {
            if (t <= p[0].T)
                return p[0].V;
            for (int i = 1; i < p.Length; i++)
                if (t <= p[i].T)
                {
                    double span = p[i].T - p[i - 1].T;
                    return span <= 0 ? p[i].V : p[i - 1].V + (p[i].V - p[i - 1].V) * (t - p[i - 1].T) / span;
                }
            return p[^1].V;
        };
    }
}

// The curve editor's presets as authored curves (spec 3.6: fade in-out, pulse, flash, grow), baked like a drawing.
// Shapes follow the approved board 11 (its key lists 0 0 0.2 1 0.8 1 1 0 · 0 0.5 0.25 1 … · 0 0 0.05 1 0.3 0.2 1 0 · 0 0.1 1 1);
// here they are the curves those keys stand for, so the bake decides the keys the engine needs.
public static class CurvePresets
{
    public static readonly string[] Names = { "fade", "pulse", "flash", "grow" };

    public static (Func<double, double> Curve, double[] Corners) Get(string name) => name switch
    {
        // Straight ramps in and out over the first and last fifth.
        "fade" => (t => Math.Min(1, Math.Min(t / 0.2, (1 - t) / 0.2)), new[] { 0.2, 0.8 }),
        // Two smooth beats between 0.5 and 1.
        "pulse" => (t => 0.75 - 0.25 * Math.Cos(4 * Math.PI * t), Array.Empty<double>()),
        // Peak 1 at 0.05, about 0.2 at 0.3: a bell, narrow before the peak (width 0.025) and wide after it (0.2), smooth
        // at the peak, so the engine spline can follow it.
        "flash" => (t => Math.Exp(-Math.Pow((t - 0.05) / (t < 0.05 ? 0.025 : 0.2), 2)), Array.Empty<double>()),
        // From 0.1 to 1, fast first and settling (ease-out).
        "grow" => (t => 0.1 + 0.9 * (1 - (1 - t) * (1 - t)), Array.Empty<double>()),
        _ => throw new ArgumentException("Unknown preset " + name),
    };
}
