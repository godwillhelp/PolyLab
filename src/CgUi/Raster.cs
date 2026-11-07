namespace PolyLab.CgUi;

public readonly record struct Box(int X0, int Y0, int X1, int Y1)
{
    public bool Empty => X1 <= X0 || Y1 <= Y0;
    public Box Clip(Box c) => new(Math.Max(X0, c.X0), Math.Max(Y0, c.Y0), Math.Min(X1, c.X1), Math.Min(Y1, c.Y1));
}

public sealed record Line(Box Box, int Argb, int Node, int Layer);
public sealed record Widget(Node Node, Box Box);
public sealed record Span(int X0, int X1, double Cov);

public sealed class Raster
{
    private readonly int _h;
    public List<Line> Lines { get; } = [];
    public List<Widget> Widgets { get; } = [];
    public int DeviceW { get; }
    public int DeviceH { get; }

    public static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);

    public static int Snap32(int v32, int h) => FloorDiv(v32 * h + 17280, 34560);

    public static int Snap(int v16, int h) => Snap32(2 * v16, h);

    public Raster(Screen s, int screenH)
    {
        _h = screenH;
        DeviceW = Snap(s.W, _h);
        DeviceH = Snap(s.H, _h);
        int index = 0;
        Walk(s.Root, 0, 0, new Box(0, 0, DeviceW, DeviceH), true, ref index);
    }

    private Box BoxOf(int x16, int y16, int w16, int h16)
    {
        int x0 = Snap(x16, _h), x1 = Snap(x16 + w16, _h), y0 = Snap(y16, _h), y1 = Snap(y16 + h16, _h);
        if (w16 > 0 && x1 == x0)
            x1 = x0 + 1;
        if (h16 > 0 && y1 == y0)
            y1 = y0 + 1;
        return new Box(x0, y0, x1, y1);
    }

    private void Walk(Node n, int px, int py, Box clip, bool shown, ref int index)
    {
        int me = index++;
        int ax = px + n.X, ay = py + n.Y;
        shown &= n.Visible;
        var box = BoxOf(ax, ay, n.W, n.H);
        if (shown)
        {
            foreach (var d in n.Draws)
            {
                switch (d)
                {
                    case FillBox f:
                        FillRect(box, f.Paint, ax, ay, n, clip, me);
                        break;
                    case StrokeBox k:
                        Stroke(k, ax, ay, n, clip, me);
                        break;
                    case Poly p:
                        FillPoly(p, ax, ay, n, clip, me);
                        break;
                }
            }
            if (n.Pattern is { } pt)
                Pattern(pt, box, clip, me, n.Layer);
            if (n.IsWidget)
                Widgets.Add(new Widget(n, box));
        }
        var inner = (n.Flags & Flags.Clip) != 0 ? clip.Clip(box) : clip;
        foreach (var c in n.Children)
            Walk(c, ax, ay, inner, shown, ref index);
    }

    private void Add(Box b, int argb, Box clip, int node, int layer)
    {
        var c = b.Clip(clip);
        if (!c.Empty && (argb >>> 24) != 0)
            Lines.Add(new Line(c, argb, node, layer));
    }

    private void Stroke(StrokeBox k, int ax, int ay, Node n, Box clip, int me)
    {
        int ox0, oy0, ox1, oy1, ix0, iy0, ix1, iy1;
        int x32 = 2 * ax, y32 = 2 * ay, r32 = 2 * (ax + n.W), b32 = 2 * (ay + n.H);
        int m = k.Align;
        ox0 = Snap32(x32 - m * k.L, _h);
        oy0 = Snap32(y32 - m * k.T, _h);
        ox1 = Snap32(r32 + m * k.R, _h);
        oy1 = Snap32(b32 + m * k.B, _h);
        ix0 = Snap32(x32 + (2 - m) * k.L, _h);
        iy0 = Snap32(y32 + (2 - m) * k.T, _h);
        ix1 = Snap32(r32 - (2 - m) * k.R, _h);
        iy1 = Snap32(b32 - (2 - m) * k.B, _h);
        if (ox1 == ox0 && r32 + m * k.R > x32 - m * k.L)
            ox1 = ox0 + 1;
        if (oy1 == oy0 && b32 + m * k.B > y32 - m * k.T)
            oy1 = oy0 + 1;
        if (k.L > 0) ix0 = Math.Max(ix0, ox0 + 1);
        if (k.R > 0) ix1 = Math.Min(ix1, ox1 - 1);
        if (k.T > 0) iy0 = Math.Max(iy0, oy0 + 1);
        if (k.B > 0) iy1 = Math.Min(iy1, oy1 - 1);
        var o = new Box(ox0, oy0, ox1, oy1);
        if (ix0 >= ix1 || iy0 >= iy1)
        {
            FillRect(o, k.Paint, ax, ay, n, clip, me);
            return;
        }
        FillRect(new Box(ox0, oy0, ox1, iy0), k.Paint, ax, ay, n, clip, me);
        FillRect(new Box(ox0, iy1, ox1, oy1), k.Paint, ax, ay, n, clip, me);
        FillRect(new Box(ox0, iy0, ix0, iy1), k.Paint, ax, ay, n, clip, me);
        FillRect(new Box(ix1, iy0, ox1, iy1), k.Paint, ax, ay, n, clip, me);
    }

    private void Pattern(PatternRec p, Box box, Box clip, int me, int layer)
    {
        var area = box.Clip(clip);
        if (p.Kind != 1)
            for (int x = box.X0; x < box.X1; x += p.Pitch)
                Add(new Box(x, box.Y0, Math.Min(x + p.LineW, box.X1), box.Y1), p.Argb, area, me, layer);
        if (p.Kind != 2)
            for (int y = box.Y0; y < box.Y1; y += p.Pitch)
                Add(new Box(box.X0, y, box.X1, Math.Min(y + p.LineW, box.Y1)), p.Argb, area, me, layer);
    }

    private Pt Local(double cx, double cy, int ax, int ay) =>
        new((cx * 17280.0 / _h - ax) / 16, (cy * 17280.0 / _h - ay) / 16);

    private Func<Pt, double> Param(Paint p, Node n)
    {
        Pt H(int i) => new(p.Handles[2 * i] / 65536.0 * n.W / 16, p.Handles[2 * i + 1] / 65536.0 * n.H / 16);
        Pt p0 = H(0), p1 = H(1), p2 = H(2);
        if (p.Kind == Paint.Linear)
        {
            var d = p1 - p0;
            double len2 = d.X * d.X + d.Y * d.Y;
            return l => len2 == 0 ? 0 : ((l.X - p0.X) * d.X + (l.Y - p0.Y) * d.Y) / len2;
        }
        Pt e1 = p1 - p0, e2 = p2 - p0;
        double det = e1.X * e2.Y - e2.X * e1.Y;
        return l =>
        {
            if (det == 0)
                return 0;
            var q = l - p0;
            double a = (q.X * e2.Y - e2.X * q.Y) / det, b = (e1.X * q.Y - q.X * e1.Y) / det;
            return Math.Sqrt(a * a + b * b);
        };
    }

    private void FillRect(Box b, Paint p, int ax, int ay, Node n, Box clip, int me)
    {
        if (p.Kind == Paint.Solid)
        {
            Add(b, p.Argb, clip, me, n.Layer);
            return;
        }
        var c = b.Clip(clip);
        if (c.Empty)
            return;
        var t = Param(p, n);
        int At(int x, int y) => p.Lut[Colour.LutIndex(t(Local(x + 0.5, y + 0.5, ax, ay)))];
        bool cols = p.Kind == Paint.Linear && p.Handles[1] == p.Handles[3];
        bool rows = p.Kind == Paint.Linear && p.Handles[0] == p.Handles[2];
        if (cols || rows)
        {
            int from = cols ? c.X0 : c.Y0, to = cols ? c.X1 : c.Y1;
            int start = from, cur = cols ? At(from, c.Y0) : At(c.X0, from);
            for (int i = from + 1; i <= to; i++)
            {
                int next = i < to ? (cols ? At(i, c.Y0) : At(c.X0, i)) : 0;
                if (i < to && next == cur)
                    continue;
                Add(cols ? new Box(start, c.Y0, i, c.Y1) : new Box(c.X0, start, c.X1, i), cur, clip, me, n.Layer);
                start = i;
                cur = next;
            }
            return;
        }
        for (int y = c.Y0; y < c.Y1; y++)
            Runs(y, [new Span(c.X0, c.X1, 1)], p, x => At(x, y), clip, me, n.Layer);
    }

    private void FillPoly(Poly poly, int ax, int ay, Node n, Box clip, int me)
    {
        double k = _h / 17280.0;
        var contours = poly.Contours.Select(c =>
        {
            var pts = new Pt[c.Length / 2];
            for (int i = 0; i < pts.Length; i++)
                pts[i] = new Pt((ax + c[2 * i]) * k, (ay + c[2 * i + 1]) * k);
            return pts;
        });
        Func<int, int, int>? colour = null;
        if (poly.Paint.Kind != Paint.Solid)
        {
            var t = Param(poly.Paint, n);
            colour = (x, y) => poly.Paint.Lut[Colour.LutIndex(t(Local(x + 0.5, y + 0.5, ax, ay)))];
        }
        foreach (var (y, spans) in Coverage(contours, poly.Rule))
        {
            if (y < clip.Y0 || y >= clip.Y1)
                continue;
            Runs(y, spans, poly.Paint, colour == null ? null : x => colour(x, y), clip, me, n.Layer);
        }
    }

    public static int Level(double cov) => Math.Clamp((int)Math.Floor(cov * 16 + 0.5), 0, 16);

    private void Runs(int y, List<Span> spans, Paint p, Func<int, int>? colour, Box clip, int me, int layer)
    {
        int runX = 0, runEnd = int.MinValue, runArgb = 0;
        foreach (var s in spans)
        {
            int level = Level(s.Cov);
            int x0 = Math.Max(s.X0, clip.X0), x1 = Math.Min(s.X1, clip.X1);
            if (level == 0 || x1 <= x0)
                continue;
            int step = colour == null ? x1 - x0 : 1;
            for (int x = x0; x < x1; x += step)
            {
                int src = colour?.Invoke(x) ?? p.Argb;
                int argb = ((((src >>> 24) * level + 8) >> 4) << 24) | (src & 0xFFFFFF);
                if (x == runEnd && argb == runArgb)
                {
                    runEnd = x + step;
                    continue;
                }
                if (runEnd > runX)
                    Add(new Box(runX, y, runEnd, y + 1), runArgb, clip, me, layer);
                runX = x;
                runEnd = x + step;
                runArgb = argb;
            }
        }
        if (runEnd > runX)
            Add(new Box(runX, y, runEnd, y + 1), runArgb, clip, me, layer);
    }

    public static SortedDictionary<int, List<Span>> Coverage(IEnumerable<Pt[]> contours, int rule)
    {
        var cells = new Dictionary<int, SortedDictionary<int, double>>();
        void Cell(int row, int col, double v)
        {
            if (!cells.TryGetValue(row, out var r))
                cells[row] = r = [];
            r[col] = r.GetValueOrDefault(col) + v;
        }
        void Piece(int row, double xa, double ya, double xb, double yb, int dir)
        {
            double dy = (yb - ya) * dir, mid = (xa + xb) / 2;
            int c = (int)Math.Floor(mid);
            double m = mid - c;
            Cell(row, c, dy * (1 - m));
            Cell(row, c + 1, dy * m);
        }
        foreach (var pts in contours)
        {
            for (int i = 0; i < pts.Length; i++)
            {
                Pt p = pts[i], q = pts[(i + 1) % pts.Length];
                if (p.Y == q.Y)
                    continue;
                int dir = q.Y > p.Y ? 1 : -1;
                var (a, b) = dir > 0 ? (p, q) : (q, p);
                for (int row = (int)Math.Floor(a.Y); row < b.Y; row++)
                {
                    double ys = Math.Max(a.Y, row), ye = Math.Min(b.Y, row + 1);
                    if (ye <= ys)
                        continue;
                    double xs = a.X + (ys - a.Y) * (b.X - a.X) / (b.Y - a.Y);
                    double xe = a.X + (ye - a.Y) * (b.X - a.X) / (b.Y - a.Y);
                    double px = xs, py = ys;
                    if (xs != xe)
                    {
                        int step = xe > xs ? 1 : -1;
                        int k = step > 0 ? (int)Math.Floor(xs) + 1 : (int)Math.Ceiling(xs) - 1;
                        for (; step > 0 ? k < xe : k > xe; k += step)
                        {
                            double yk = ys + (k - xs) * (ye - ys) / (xe - xs);
                            Piece(row, px, py, k, yk, dir);
                            px = k;
                            py = yk;
                        }
                    }
                    Piece(row, px, py, xe, ye, dir);
                }
            }
        }
        var res = new SortedDictionary<int, List<Span>>();
        foreach (var (row, r) in cells)
        {
            var spans = new List<Span>();
            double s = 0;
            int? prev = null;
            foreach (var (col, v) in r)
            {
                if (prev is int p0 && col > p0)
                {
                    double cov = rule == 0 ? Math.Min(1, Math.Abs(s)) : EvenOdd(Math.Abs(s));
                    if (cov > 0)
                        spans.Add(new Span(p0, col, cov));
                }
                s += v;
                prev = col;
            }
            res[row] = spans;
        }
        return res;
    }

    private static double EvenOdd(double s)
    {
        double f = s % 2;
        return f > 1 ? 2 - f : f;
    }
}
