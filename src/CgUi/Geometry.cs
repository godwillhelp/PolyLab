using System.Globalization;

namespace PolyLab.CgUi;

public readonly record struct Pt(double X, double Y)
{
    public static Pt operator +(Pt a, Pt b) => new(a.X + b.X, a.Y + b.Y);
    public static Pt operator -(Pt a, Pt b) => new(a.X - b.X, a.Y - b.Y);
    public static Pt operator *(double k, Pt a) => new(k * a.X, k * a.Y);
    public double Len => Math.Sqrt(X * X + Y * Y);
}

public static class Geometry
{
    public const double Tolerance = 1.0 / 16;

    public static int[]? AsRects(IReadOnlyList<int[]> contours, int rule)
    {
        var pieces = new List<(int X, int Y, int W, int H)>();
        int winding = 0;
        foreach (var c in contours)
        {
            int n = c.Length / 2;
            if (n < 4)
                return null;
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue;
            for (int i = 0; i < n; i++)
            {
                x0 = Math.Min(x0, c[2 * i]);
                x1 = Math.Max(x1, c[2 * i]);
                y0 = Math.Min(y0, c[2 * i + 1]);
                y1 = Math.Max(y1, c[2 * i + 1]);
            }
            if (x1 <= x0 || y1 <= y0)
                return null;
            long area2 = 0;
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                int x = c[2 * i], y = c[2 * i + 1];
                if (x != x0 && x != x1 && y != y0 && y != y1)
                    return null;
                area2 += (long)x * c[2 * j + 1] - (long)c[2 * j] * y;
            }
            if (Math.Abs(area2) != 2L * (x1 - x0) * (y1 - y0))
                return null;
            int sign = Math.Sign(area2);
            if (winding != 0 && sign != winding)
                return null;
            winding = sign;
            var add = new List<(int X, int Y, int W, int H)> { (x0, y0, x1 - x0, y1 - y0) };
            foreach (var old in pieces)
            {
                var next = new List<(int X, int Y, int W, int H)>();
                foreach (var r in add)
                {
                    bool overlap = r.X < old.X + old.W && old.X < r.X + r.W && r.Y < old.Y + old.H && old.Y < r.Y + r.H;
                    if (!overlap)
                    {
                        next.Add(r);
                        continue;
                    }
                    if (rule != 0)
                        return null;
                    int top = Math.Max(r.Y, old.Y), bottom = Math.Min(r.Y + r.H, old.Y + old.H);
                    if (old.Y > r.Y)
                        next.Add((r.X, r.Y, r.W, old.Y - r.Y));
                    if (old.Y + old.H < r.Y + r.H)
                        next.Add((r.X, old.Y + old.H, r.W, r.Y + r.H - old.Y - old.H));
                    if (old.X > r.X)
                        next.Add((r.X, top, old.X - r.X, bottom - top));
                    if (old.X + old.W < r.X + r.W)
                        next.Add((old.X + old.W, top, r.X + r.W - old.X - old.W, bottom - top));
                }
                add = next;
            }
            pieces.AddRange(add);
        }
        var boxes = new int[pieces.Count * 4];
        for (int k = 0; k < pieces.Count; k++)
            (boxes[4 * k], boxes[4 * k + 1], boxes[4 * k + 2], boxes[4 * k + 3]) = pieces[k];
        return boxes;
    }

    public static int QuadSteps(Pt p0, Pt p1, Pt p2, double tol) =>
        Math.Max(1, (int)Math.Ceiling(Math.Sqrt(2 * (p0 - 2 * p1 + p2).Len / (8 * tol))));

    public static int CubicSteps(Pt p0, Pt p1, Pt p2, Pt p3, double tol)
    {
        double dd = Math.Max((p0 - 2 * p1 + p2).Len, (p1 - 2 * p2 + p3).Len);
        return Math.Max(1, (int)Math.Ceiling(Math.Sqrt(6 * dd / (8 * tol))));
    }

    public static Pt Quad(Pt p0, Pt p1, Pt p2, double t)
    {
        double u = 1 - t;
        return u * u * p0 + 2 * u * t * p1 + t * t * p2;
    }

    public static Pt Cubic(Pt p0, Pt p1, Pt p2, Pt p3, double t)
    {
        double u = 1 - t;
        return u * u * u * p0 + 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t * p3;
    }

    public static List<List<Pt>> Flatten(string path, double tol = Tolerance)
    {
        var contours = new List<List<Pt>>();
        List<Pt>? cur = null;
        var tok = new Tokens(path);
        Pt last = default, start = default;
        while (tok.NextCommand() is char cmd)
        {
            switch (cmd)
            {
                case 'M':
                    last = start = tok.Point();
                    cur = [last];
                    contours.Add(cur);
                    while (tok.HasNumber())
                        cur.Add(last = tok.Point());
                    break;
                case 'L':
                    do cur!.Add(last = tok.Point()); while (tok.HasNumber());
                    break;
                case 'Q':
                    do
                    {
                        Pt c1 = tok.Point(), e = tok.Point();
                        int n = QuadSteps(last, c1, e, tol);
                        for (int i = 1; i <= n; i++)
                            cur!.Add(Quad(last, c1, e, (double)i / n));
                        last = e;
                    } while (tok.HasNumber());
                    break;
                case 'C':
                    do
                    {
                        Pt c1 = tok.Point(), c2 = tok.Point(), e = tok.Point();
                        int n = CubicSteps(last, c1, c2, e, tol);
                        for (int i = 1; i <= n; i++)
                            cur!.Add(Cubic(last, c1, c2, e, (double)i / n));
                        last = e;
                    } while (tok.HasNumber());
                    break;
                case 'Z':
                    last = start;
                    cur = null;
                    break;
                default:
                    throw new FormatException($"path command '{cmd}' is not supported (M L Q C Z only)");
            }
        }
        return contours;
    }

    public static int R16(double v) => (int)Math.Floor(v * 16 + 0.5);

    public static int[][] ToFixed(List<List<Pt>> contours, Func<Pt, Pt> map)
    {
        var res = new List<int[]>();
        foreach (var c in contours)
        {
            var pts = new List<int>();
            foreach (var p in c)
            {
                var q = map(p);
                int x = R16(q.X), y = R16(q.Y);
                if (pts.Count >= 2 && pts[^2] == x && pts[^1] == y)
                    continue;
                pts.Add(x);
                pts.Add(y);
            }
            if (pts.Count >= 4 && pts[0] == pts[^2] && pts[1] == pts[^1])
                pts.RemoveRange(pts.Count - 2, 2);
            if (pts.Count >= 6)
                res.Add([.. pts]);
        }
        return [.. res];
    }

    private sealed class Tokens(string s)
    {
        private int _i;

        private void Skip()
        {
            while (_i < s.Length && (char.IsWhiteSpace(s[_i]) || s[_i] == ','))
                _i++;
        }

        public char? NextCommand()
        {
            Skip();
            if (_i >= s.Length)
                return null;
            char c = s[_i];
            if (!char.IsLetter(c))
                throw new FormatException($"path: command expected at {_i}");
            _i++;
            return c;
        }

        public bool HasNumber()
        {
            Skip();
            return _i < s.Length && (char.IsDigit(s[_i]) || s[_i] is '-' or '+' or '.');
        }

        public double Number()
        {
            Skip();
            int b = _i;
            if (_i < s.Length && s[_i] is '-' or '+')
                _i++;
            while (_i < s.Length && (char.IsDigit(s[_i]) || s[_i] == '.'))
                _i++;
            if (_i < s.Length && s[_i] is 'e' or 'E')
            {
                _i++;
                if (_i < s.Length && s[_i] is '-' or '+')
                    _i++;
                while (_i < s.Length && char.IsDigit(s[_i]))
                    _i++;
            }
            return double.Parse(s.AsSpan(b, _i - b), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        public Pt Point() => new(Number(), Number());
    }
}
