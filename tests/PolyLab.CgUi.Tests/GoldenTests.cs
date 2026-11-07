using System.Text.Json;

namespace PolyLab.CgUi.Tests;

// CGUI_READBACK = path of the REST nodes?geometry=paths read-back of the CG_Dialogs V1 copy (owner's file, not in the repo).
// CGUI_READBACK_C = path of the design C read-back (plugin export readback_c.json, same shape).
public sealed class ReadbackFactAttribute : FactAttribute
{
    public ReadbackFactAttribute(string variable = "CGUI_READBACK")
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)))
            Skip = variable + " is not set";
    }
}

public class GoldenTests
{
    private static (Screen Ir, Screen Decoded) Compile(string id, string name, string variable = "CGUI_READBACK")
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Environment.GetEnvironmentVariable(variable)!));
        var ir = new FigmaReader(doc.RootElement, ["$portrait", "$matPreview", "$historyScroll", "$matScroll"], images: true)
            .Read(id, name);
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

    [ReadbackFact("CGUI_READBACK_C")]
    public void DialogC()
    {
        var (ir, decoded) = Compile("140:18689", "Dialog", "CGUI_READBACK_C");
        Assert.DoesNotContain(ir.Diags, d => d.Error);
        var nodes = Abs(ir.Root).ToList();
        var window = nodes.Single(n => n.N.Name == "Окно");
        Assert.Equal((468, 350, 984, 668), (window.X / 16, window.Y / 16, window.N.W / 16, window.N.H / 16));
        var rows = nodes.Single(n => n.N.Name == "$answers").N.Children;
        Assert.Equal(6, rows.Count);
        Assert.All(rows, r => Assert.Equal((960, 56), (r.W / 16, r.H / 16)));
        var pop = nodes.Single(n => n.N.Name == "$matPopover");
        Assert.Equal((660, 372, 600, 336), (pop.X / 16, pop.Y / 16, pop.N.W / 16, pop.N.H / 16));
        Assert.Equal("CG_Dialogs/GUI/icons/cg_dlg_portrait_bg.paa", nodes.Single(n => n.N.Name.StartsWith("img:CG_Dialogs")).N.Image!.Ref);
        Assert.Equal("cg_emblems_sq/loner", nodes.Single(n => n.N.Name == "$emblem").N.Image!.Ref);
        Assert.Equal(9, ir.Components.Count);
        Assert.All(nodes.Where(n => n.N.Name.StartsWith("Обводка")), n => Assert.All(n.N.Draws, d => Assert.IsType<Rects>(d)));
        Assert.Equal([3020, 2092], Gallery.Heights.Select(h => new Raster(decoded, h).Lines.Count));
    }

    [ReadbackFact("CGUI_READBACK_C")]
    public void MaterialsPopupAboveDim()
    {
        var (ir, _) = Compile("140:18918", "DialogMats", "CGUI_READBACK_C");
        var nodes = Abs(ir.Root).ToList();
        int dim = nodes.Single(n => n.N.Name == "$dim").N.Layer;
        var pop = nodes.Single(n => n.N.Name == "$matPopover").N;
        Assert.All(Abs(pop).Where(n => n.N.IsShape || n.N.IsWidget), n => Assert.True(n.N.Layer >= dim, n.N.Name));
    }
}
