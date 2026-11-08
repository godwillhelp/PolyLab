using System.Text;
using System.Text.Json;

namespace PolyLab.CgUi.Tests;

public class DataTests
{
    private static string EnforceUnescape(string literal)
    {
        Assert.StartsWith("\"", literal);
        Assert.EndsWith("\"", literal);
        var sb = new StringBuilder();
        for (int i = 1; i < literal.Length - 1; i++)
        {
            char c = literal[i];
            Assert.NotEqual('"', c);
            sb.Append(c == '\\' ? literal[++i] : c);
        }
        return sb.ToString();
    }

    private static Screen Sample()
    {
        var lut = Enumerable.Range(0, 256).Select(i => Colour.Argb(255 - i, i, 2, 3)).ToArray();
        var leaf = new Node
        {
            Binding = "$npcText", Flags = Flags.Visible | Flags.Absolute, X = -16, Y = 8, W = 160, H = 32, Layer = 1,
            SizingH = 2, SizingV = 1, ConsH = 3, ConsV = 4,
            Draws =
            [
                new FillBox(Paint.Of(Colour.Argb(247, 22, 22, 22))),
                new StrokeBox(Paint.Of(Colour.Argb(31, 255, 255, 255)), 16, 0, 16, 8, 2),
                new Poly(new Paint { Kind = Paint.Radial, Handles = [32768, 32768, 46341, 32768, 32768, 46341], Lut = lut }, 1,
                    [[0, 0, 16, 0, 16, 16], [1, 1, 5, 1, 5, 5, 1, 5]])
            ],
            Text = new TextRec("Здорово, \"сталкер\" \\ — ok", 304, 400, -1, 0, 1, 1),
        };
        var root = new Node
        {
            Flags = Flags.Visible | Flags.Clip, W = 30720, H = 17280,
            Layout = new LayoutRec(1, 16, 24, 29, 24, 29, 0, 1, 0, 848, 1, 4480),
            Pattern = new PatternRec(0, 3, 1, Colour.Argb(255, 33, 33, 33)),
            Instance = new InstanceRec(0, "Тип=Разговор, Состояние=Обычное"),
            Children = [leaf, new Node { Flags = Flags.Live, Image = new ImageRec("cg_lucide/lock", -1) }]
        };
        return new Screen
        {
            W = 30720, H = 17280, Root = root,
            Components = [new Component(0, "Строка ответа", "Тип=Действие", new Node { Flags = Flags.Visible, W = 12208, H = 848 })]
        };
    }

    [Fact]
    public void IntStreamRoundTripsEveryRecord()
    {
        var e = IntStream.Encode(Sample());
        var d = IntStream.Decode(e.Ints, e.Strs);
        var again = IntStream.Encode(d);
        Assert.Equal(e.Ints, again.Ints);
        Assert.Equal(e.Strs, again.Strs);
        Assert.Equal("$npcText", d.Root.Children[0].Binding);
        Assert.Equal("Строка ответа", Assert.Single(d.Components).Set);
        Assert.Equal(Op.Header, e.Ints[0]);
        Assert.Equal(3, e.Ints[5]);
    }

    [Fact]
    public void DecoderSkipsUnknownRecordsByLength()
    {
        var e = IntStream.Encode(Sample());
        int[] withUnknown = [.. e.Ints[..7], 99, 3, 7, 7, 7, .. e.Ints[7..]];
        Assert.Equal(e.Ints, IntStream.Encode(IntStream.Decode(withUnknown, e.Strs)).Ints);
    }

    [Fact]
    public void EnforceLiteralsRoundTripThroughJson()
    {
        var e = IntStream.Encode(Sample());
        string src = EnforceOut.ClassSource("CG_UIData_Test", e);
        Assert.All(src, c => Assert.InRange(c, '\n', '~'));
        var lines = src.Split('\n');
        string Member(string name) =>
            EnforceUnescape(lines.Single(l => l.Contains($" {name} = ")).Split(" = ", 2)[1].TrimEnd(';'));
        Assert.Equal(e.Ints, JsonSerializer.Deserialize<int[]>(Member("INTS")));
        Assert.Equal(e.Strs, JsonSerializer.Deserialize<string[]>(Member("STRS")));
        Assert.Contains(e.Ints, v => v < 0);
    }

    [Fact]
    public void OversizedStreamIsRefused()
    {
        var e = new Encoded(new int[EnforceOut.MaxIntsPerLiteral + 1], []);
        Assert.Throws<InvalidOperationException>(() => EnforceOut.ClassSource("X", e));
    }
}
