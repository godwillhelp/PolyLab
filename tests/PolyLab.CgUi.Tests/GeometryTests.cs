namespace PolyLab.CgUi.Tests;

public class GeometryTests
{
    private static double DistToPolyline(Pt p, IReadOnlyList<Pt> line)
    {
        double best = double.MaxValue;
        for (int i = 0; i + 1 < line.Count; i++)
        {
            Pt a = line[i], d = line[i + 1] - a;
            double len2 = d.X * d.X + d.Y * d.Y;
            double t = len2 == 0 ? 0 : Math.Clamp(((p.X - a.X) * d.X + (p.Y - a.Y) * d.Y) / len2, 0, 1);
            best = Math.Min(best, (p - (a + t * d)).Len);
        }
        return best;
    }

    [Theory]
    [InlineData(0, 0, 0, 55.2285, 44.7715, 100, 100, 100)]
    [InlineData(0, 0, 300, -40, -200, 90, 10, 3)]
    [InlineData(1, 1, 1.5, 1.2, 1.7, 1.1, 2, 2)]
    public void CubicFlatteningStaysWithinTolerance(double x0, double y0, double x1, double y1, double x2, double y2, double x3, double y3)
    {
        Pt p0 = new(x0, y0), p1 = new(x1, y1), p2 = new(x2, y2), p3 = new(x3, y3);
        var line = Geometry.Flatten(FormattableString.Invariant($"M{x0} {y0}C{x1} {y1} {x2} {y2} {x3} {y3}"))[0];
        Assert.Equal(Geometry.CubicSteps(p0, p1, p2, p3, Geometry.Tolerance) + 1, line.Count);
        for (int i = 0; i <= 4000; i++)
            Assert.True(DistToPolyline(Geometry.Cubic(p0, p1, p2, p3, i / 4000.0), line) <= Geometry.Tolerance);
    }

    [Fact]
    public void QuadFlatteningStaysWithinTolerance()
    {
        Pt p0 = new(0, 0), p1 = new(50, 120), p2 = new(100, 0);
        var line = Geometry.Flatten("M0 0Q50 120 100 0")[0];
        for (int i = 0; i <= 4000; i++)
            Assert.True(DistToPolyline(Geometry.Quad(p0, p1, p2, i / 4000.0), line) <= Geometry.Tolerance);
    }

    [Fact]
    public void PathParsesSubpathsExponentsAndImplicitLines()
    {
        var c = Geometry.Flatten("M0 0L10 0 10 10Z M1e1 2.5e-1L-3.5 .5Z");
        Assert.Equal(2, c.Count);
        Assert.Equal([new Pt(0, 0), new Pt(10, 0), new Pt(10, 10)], c[0]);
        Assert.Equal([new Pt(10, 0.25), new Pt(-3.5, 0.5)], c[1]);
        Assert.Throws<FormatException>(() => Geometry.Flatten("M0 0A1 1 0 0 0 5 5"));
    }

    [Fact]
    public void FixedPointDropsDuplicatesClosingPointAndDegenerateContours()
    {
        var c = Geometry.ToFixed([[new(0, 0), new(1, 0), new(1.001, 0), new(1, 1), new(0, 0)], [new(0, 0), new(1, 1)]], p => p);
        Assert.Single(c);
        Assert.Equal([0, 0, 16, 0, 16, 16], c[0]);
    }
}
