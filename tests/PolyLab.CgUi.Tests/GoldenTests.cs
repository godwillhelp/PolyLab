using System.Text.Json;

namespace PolyLab.CgUi.Tests;

// CGUI_READBACK = path of the REST nodes?geometry=paths read-back of the CG_Dialogs copy (owner's file, not in the repo).
public sealed class ReadbackFactAttribute : FactAttribute
{
    public ReadbackFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CGUI_READBACK")))
            Skip = "CGUI_READBACK is not set";
    }
}

public class GoldenTests
{
    private static (Screen Ir, Screen Decoded) Compile(string id, string name)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Environment.GetEnvironmentVariable("CGUI_READBACK")!));
        var ir = new FigmaReader(doc.RootElement, ["$portrait", "$matPreview"], images: true).Read(id, name);
        var enc = IntStream.Encode(ir);
        return (ir, IntStream.Decode(enc.Ints, enc.Strs));
    }

    private static IEnumerable<(Node N, int X, int Y)> Abs(Node n, int px = 0, int py = 0)
    {
        int x = px + n.X, y = py + n.Y;
        yield return (n, x, y);
        foreach (var c in n.Children)
            foreach (var r in Abs(c, x, y))
                yield return r;
    }

    [ReadbackFact]
    public void DialogRun1()
    {
        var (ir, decoded) = Compile("131:205", "Dialog");
        Assert.DoesNotContain(ir.Diags, d => d.Error);
        var nodes = Abs(ir.Root).ToList();
        var panel = nodes.Single(n => n.N.Name == "Панель");
        Assert.Equal((468, 475, 984, 543), (panel.X / 16, panel.Y / 16, panel.N.W / 16, panel.N.H / 16));
        var rows = nodes.Single(n => n.N.Name == "$answers").N.Children;
        Assert.Equal([53, 53, 61, 61, 81, 53], rows.Select(r => r.H / 16));
        Assert.All(rows, r => Assert.NotNull(r.Instance));
        Assert.All(nodes.Where(n => n.N.Name.StartsWith("Уголок")), n => Assert.Equal(1, n.N.Layer));
        Assert.All(nodes.Where(n => n.N.Name == "$badge"), n => Assert.Equal(1, n.N.Layer));
        Assert.Equal(0, nodes.Single(n => n.N.Name == "Полоса").N.Layer);
        Assert.Equal(9, ir.Components.Count);
        Assert.Equal([6168, 4173], Gallery.Heights.Select(h => new Raster(decoded, h).Lines.Count));
    }

    [ReadbackFact]
    public void StatesRun1()
    {
        var (ir, decoded) = Compile("131:516", "DialogStates");
        Assert.DoesNotContain(ir.Diags, d => d.Error);
        Assert.Equal([6928, 4685], Gallery.Heights.Select(h => new Raster(decoded, h).Lines.Count));
    }
}
