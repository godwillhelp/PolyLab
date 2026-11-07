using System.Text.Json.Serialization;

namespace PolyLab.CgUi;

public static class Flags
{
    public const int Visible = 1, Clip = 2, Absolute = 4, Live = 8;
}

public sealed class Paint
{
    public const int Solid = 0, Linear = 1, Radial = 2;
    public int Kind { get; init; }
    public int Argb { get; init; }
    public int[] Handles { get; init; } = [];
    public int[] Lut { get; init; } = [];

    public static Paint Of(int argb) => new() { Kind = Solid, Argb = argb };
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "draw")]
[JsonDerivedType(typeof(FillBox), "fillbox")]
[JsonDerivedType(typeof(StrokeBox), "strokebox")]
[JsonDerivedType(typeof(Poly), "poly")]
public abstract record Draw(Paint Paint);
public sealed record FillBox(Paint Paint) : Draw(Paint);
public sealed record StrokeBox(Paint Paint, int T, int R, int B, int L, int Align) : Draw(Paint);
public sealed record Poly(Paint Paint, int Rule, int[][] Contours) : Draw(Paint);

public sealed record LayoutRec(int Dir, int Gap, int PadT, int PadR, int PadB, int PadL, int Main, int Cross, int Wrap,
    int MinH, int StrokesInLayout);
public sealed record PatternRec(int Kind, int Pitch, int LineW, int Argb);
public sealed record TextRec(string Chars, int Size, int LineH, int Argb, int AlignH, int AlignV, int AutoResize);
public sealed record ImageRec(string Ref, int Tint);
public sealed record InstanceRec(int Component, string Variant);

public sealed class Node
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Binding { get; set; }
    public int Flags { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }
    public int Layer { get; set; }
    public int SizingH { get; set; }
    public int SizingV { get; set; }
    public int ConsH { get; set; }
    public int ConsV { get; set; }
    public LayoutRec? Layout { get; set; }
    public List<Draw> Draws { get; set; } = [];
    public PatternRec? Pattern { get; set; }
    public TextRec? Text { get; set; }
    public ImageRec? Image { get; set; }
    public InstanceRec? Instance { get; set; }
    public List<Node> Children { get; set; } = [];

    [JsonIgnore] public bool Visible => (Flags & CgUi.Flags.Visible) != 0;
    [JsonIgnore] public bool IsWidget => Text != null || Image != null || (Flags & CgUi.Flags.Live) != 0;
    [JsonIgnore] public bool IsShape => Draws.Count > 0 || Pattern != null;

    public IEnumerable<Node> PreOrder()
    {
        yield return this;
        foreach (var c in Children)
            foreach (var n in c.PreOrder())
                yield return n;
    }
}

public sealed record Component(int Index, string Set, string Variant, Node Root);

public sealed record Diag(bool Error, string NodeId, string NodeName, string Message);

public sealed class Screen
{
    public string Name { get; set; } = "";
    public string FigmaId { get; set; } = "";
    public int W { get; set; }
    public int H { get; set; }
    public Paint? Backdrop { get; set; }
    public Node Root { get; set; } = new();
    public List<Component> Components { get; set; } = [];
    public List<Diag> Diags { get; set; } = [];
}
