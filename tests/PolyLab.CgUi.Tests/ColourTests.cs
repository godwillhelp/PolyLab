namespace PolyLab.CgUi.Tests;

public class ColourTests
{
    [Theory]
    [InlineData(255, 255, 255, 9 / 255.0, 22, 22, 22)]
    [InlineData(255, 255, 255, 0.07, 22, 22, 22)]
    [InlineData(255, 255, 255, 0.122, 22, 22, 22)]
    [InlineData(224, 163, 74, 0.09, 22, 22, 22)]
    [InlineData(224, 163, 74, 0.4, 22, 22, 22)]
    [InlineData(0, 0, 0, 0.25, 8, 14, 12)]
    [InlineData(200, 191, 178, 0.55, 22, 22, 22)]
    public void RemappedAlphaIsLocallyBestOverTheReference(int sr, int sg, int sb, double a, int dr, int dg, int db)
    {
        Rgb s = Rgb.Of8(sr, sg, sb), d = Rgb.Of8(dr, dg, db), target = s.Over(d, a);
        int a8 = Colour.Alpha8(s, a, d);
        double err = Colour.CanvasError(s, a8, d, target);
        Assert.True(a8 == 0 || err <= Colour.CanvasError(s, a8 - 1, d, target));
        Assert.True(a8 == 255 || err <= Colour.CanvasError(s, a8 + 1, d, target));
        Assert.True(err <= Colour.CanvasError(s, Colour.To8(a), d, target));
    }

    [Fact]
    public void FaintWhiteOverPanelNeedsAlphaOneAndIsHalfAStepOff()
    {
        Rgb white = Rgb.Of8(255, 255, 255), panel = Rgb.Of8(22, 22, 22);
        Assert.Equal(1, Colour.Alpha8(white, 9 / 255.0, panel));
        double off = Math.Sqrt(Colour.CanvasError(white, 1, panel, white.Over(panel, 9 / 255.0)) / 3);
        Assert.InRange(off, 1.5, 2.0);
        Assert.Equal(255, Colour.Alpha8(white, 1, null));
        Assert.Equal(128, Colour.Alpha8(white, 128 / 255.0, null));
        Assert.Equal(128, Colour.Alpha8(panel, 128 / 255.0, panel));
    }

    [Fact]
    public void LutHitsStopsAndStepsWithTheDominantChannel()
    {
        var stops = new List<(double, Rgb, double)> { (0, Rgb.Of8(25, 44, 41), 1), (0.875, Rgb.Of8(8, 14, 12), 1) };
        var lut = Colour.Lut(stops, 1, null);
        Assert.Equal(Colour.Argb(255, 25, 44, 41), lut[0]);
        Assert.Equal(Colour.Argb(255, 8, 14, 12), lut[255]);
        Assert.Equal(Colour.Argb(255, 8, 14, 12), lut[Colour.LutIndex(0.875)]);
        Assert.Equal(30 + 1, lut.Distinct().Count());
    }
}
