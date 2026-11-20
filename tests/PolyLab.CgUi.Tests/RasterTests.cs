namespace PolyLab.CgUi.Tests;

public class RasterTests
{
    private static double Area(IEnumerable<Pt[]> contours, int rule) =>
        Raster.Coverage(contours, rule).Values.SelectMany(s => s).Sum(s => (s.X1 - s.X0) * s.Cov);

    private static double Shoelace(Pt[] p) =>
        Math.Abs(Enumerable.Range(0, p.Length).Sum(i => p[i].X * p[(i + 1) % p.Length].Y - p[(i + 1) % p.Length].X * p[i].Y)) / 2;

    private static Pt[] Rect(double x, double y, double w, double h, bool ccw = false) => ccw
        ? [new(x, y), new(x, y + h), new(x + w, y + h), new(x + w, y)]
        : [new(x, y), new(x + w, y), new(x + w, y + h), new(x, y + h)];

    [Fact]
    public void RectangleAtFractionalOffsetHasExactArea()
    {
        Assert.Equal(2.5 * 3.25, Area([Rect(0.3, 0.7, 2.5, 3.25)], 0), 12);
        var rows = Raster.Coverage([Rect(0.25, 0, 1, 1)], 0);
        Assert.Equal([new Span(0, 1, 0.75), new Span(1, 2, 0.25)], rows[0]);
    }

    [Fact]
    public void TriangleAndOrientationGiveExactArea()
    {
        Pt[] tri = [new(0.2, 0.1), new(7.9, 2.3), new(3.1, 6.6)];
        Assert.Equal(Shoelace(tri), Area([tri], 0), 10);
        Assert.Equal(Shoelace(tri), Area([tri.Reverse().ToArray()], 0), 10);
    }

    [Fact]
    public void FlattenedCircleCoverageEqualsPolygonArea()
    {
        var circle = Geometry.Flatten("M10.5 3.3C14.366 3.3 17.5 6.434 17.5 10.3C17.5 14.166 14.366 17.3 10.5 17.3" +
                                      "C6.634 17.3 3.5 14.166 3.5 10.3C3.5 6.434 6.634 3.3 10.5 3.3Z")[0].ToArray();
        Assert.Equal(Shoelace(circle), Area([circle], 0), 9);
        Assert.InRange(Math.PI * 49 - Area([circle], 0), 0, 2 * Math.PI * 7 * Geometry.Tolerance);
    }

    [Fact]
    public void WindingRulesOnNestedSquares()
    {
        Pt[] outer = Rect(0, 0, 6, 6), innerSame = Rect(2, 2, 2, 2), innerOpposite = Rect(2, 2, 2, 2, ccw: true);
        Assert.Equal(36, Area([outer, innerSame], 0), 12);
        Assert.Equal(32, Area([outer, innerSame], 1), 12);
        Assert.Equal(32, Area([outer, innerOpposite], 0), 12);
    }

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(0.1, 0)]
    [InlineData(0.2, 4)]
    [InlineData(0.5, 8)]
    [InlineData(0.8, 12)]
    [InlineData(0.999, 16)]
    [InlineData(1.0, 16)]
    public void CoverageQuantisesToQuarterLevels(double cov, int level) => Assert.Equal(level, Raster.Level(cov));

    [Theory]
    [InlineData(0, 720, 0)]
    [InlineData(16, 720, 1)]
    [InlineData(24, 720, 1)]
    [InlineData(-24, 720, -1)]
    [InlineData(-8, 1080, 0)]
    [InlineData(1920 * 16, 2160, 3840)]
    [InlineData(1000 * 16 + 8, 1080, 1001)]
    public void SnapIsRoundHalfUpInIntMaths(int v16, int h, int expected) => Assert.Equal(expected, Raster.Snap(v16, h));

    private static Screen OneNode(int x, int y, int w, int h, params Draw[] draws)
    {
        var node = new Node { Flags = Flags.Visible, X = x, Y = y, W = w, H = h, Draws = [.. draws] };
        return new Screen { W = 1920 * 16, H = 1080 * 16, Root = new Node { Flags = Flags.Visible, W = 1920 * 16, H = 1080 * 16, Children = [node] } };
    }

    private static long Pixels(IEnumerable<Line> lines) => lines.Sum(l => (long)(l.Box.X1 - l.Box.X0) * (l.Box.Y1 - l.Box.Y0));

    [Fact]
    public void HairRuleKeepsSubPixelBoxesAtOnePixel()
    {
        var r = new Raster(OneNode(17 * 16, 16, 763 * 16, 16, new FillBox(Paint.Of(-1))), 720);
        var line = Assert.Single(r.Lines);
        Assert.Equal(new Box(11, 1, 520, 2), line.Box);
    }

    [Theory]
    [InlineData(0, 1080)]
    [InlineData(0, 720)]
    [InlineData(1, 720)]
    [InlineData(2, 720)]
    public void StrokeBoxSidesNeverOverlap(int align, int screenH)
    {
        var s = OneNode(101 * 16, 37 * 16, 52 * 16, 52 * 16, new StrokeBox(Paint.Of(0x1FFFFFFF), 16, 16, 16, 16, align));
        var lines = new Raster(s, screenH).Lines;
        Assert.Equal(4, lines.Count);
        var covered = new HashSet<(int, int)>();
        foreach (var l in lines)
            for (int y = l.Box.Y0; y < l.Box.Y1; y++)
                for (int x = l.Box.X0; x < l.Box.X1; x++)
                    Assert.True(covered.Add((x, y)), $"pixel {x},{y} drawn twice");
        int outer = (lines.Max(l => l.Box.X1) - lines.Min(l => l.Box.X0)) * (lines.Max(l => l.Box.Y1) - lines.Min(l => l.Box.Y0));
        Assert.True(Pixels(lines) < outer);
    }

    [Fact]
    public void InsideStrokeAt1080IsTheOnePixelFrame()
    {
        var lines = new Raster(OneNode(0, 0, 52 * 16, 52 * 16, new StrokeBox(Paint.Of(-1), 16, 16, 16, 16, 0)), 1080).Lines;
        Assert.Equal([new Box(0, 0, 52, 1), new Box(0, 51, 52, 52), new Box(0, 1, 1, 51), new Box(51, 1, 52, 51)],
            lines.Select(l => l.Box));
    }

    [Fact]
    public void TranslucentPolygonEdgesScaleAlphaByLevel()
    {
        var poly = new Poly(Paint.Of(Colour.Argb(128, 255, 0, 0)), 0, [[4, 0, 36, 0, 36, 16, 4, 16]]);
        var lines = new Raster(OneNode(0, 0, 64, 16, poly), 1080).Lines;
        Assert.Equal([(0, 1, 96), (1, 2, 128), (2, 3, 32)],
            lines.Select(l => (l.Box.X0, l.Box.X1, Colour.Split(l.Argb).A)));
    }

    [Fact]
    public void GridPatternUsesDevicePitch()
    {
        var node = new Node { Flags = Flags.Visible, W = 30 * 16, H = 9 * 16, Pattern = new PatternRec(0, 3, 1, -1) };
        var s = new Screen { W = 1920 * 16, H = 1080 * 16, Root = new Node { Flags = Flags.Visible, W = 1920 * 16, H = 1080 * 16, Children = [node] } };
        Assert.Equal(10 + 3, new Raster(s, 1080).Lines.Count);
        Assert.Equal(7 + 2, new Raster(s, 720).Lines.Count);
    }

    [Fact]
    public void StripePatternDrawsColumnsOnly()
    {
        var node = new Node { Flags = Flags.Visible, W = 30 * 16, H = 9 * 16, Pattern = new PatternRec(2, 2, 1, -1) };
        var s = new Screen { W = 1920 * 16, H = 1080 * 16, Root = new Node { Flags = Flags.Visible, W = 1920 * 16, H = 1080 * 16, Children = [node] } };
        var lines = new Raster(s, 1080).Lines;
        Assert.Equal(15, lines.Count);
        Assert.All(lines, l => Assert.Equal((1, 0, 9), (l.Box.X1 - l.Box.X0, l.Box.Y0, l.Box.Y1)));
    }
}
