namespace PolyLab.CgUi;

public static class Op
{
    public const int Header = 1, Node = 2, End = 3, Layout = 4, FillBox = 5, StrokeBox = 6, Poly = 7, Pattern = 8,
        Text = 9, Image = 10, Instance = 11, Component = 12, Rects = 13;
    public const int Version = 1;
}

public sealed record Encoded(int[] Ints, string[] Strs);

public static class IntStream
{
    public static Encoded Encode(Screen s)
    {
        var ints = new List<int>();
        var strs = new List<string>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);

        int Str(string? v)
        {
            if (v == null)
                return -1;
            if (!index.TryGetValue(v, out int i))
            {
                i = strs.Count;
                strs.Add(v);
                index[v] = i;
            }
            return i;
        }

        void Rec(int op, params int[] payload)
        {
            ints.Add(op);
            ints.Add(payload.Length);
            ints.AddRange(payload);
        }

        int[] PaintInts(Paint p) => p.Kind == Paint.Solid
            ? [Paint.Solid, p.Argb]
            : [p.Kind, .. p.Handles, p.Lut.Length, .. p.Lut];

        void Emit(Node n)
        {
            Rec(Op.Node, Str(n.Binding), n.Flags, n.X, n.Y, n.W, n.H, n.Layer, n.SizingH, n.SizingV, n.ConsH, n.ConsV);
            if (n.Layout is { } l)
                Rec(Op.Layout, l.Dir, l.Gap, l.PadT, l.PadR, l.PadB, l.PadL, l.Main, l.Cross, l.Wrap, l.MinH,
                    l.StrokesInLayout, l.MinW);
            foreach (var d in n.Draws)
            {
                switch (d)
                {
                    case FillBox f:
                        Rec(Op.FillBox, PaintInts(f.Paint));
                        break;
                    case StrokeBox k:
                        Rec(Op.StrokeBox, [.. PaintInts(k.Paint), k.T, k.R, k.B, k.L, k.Align]);
                        break;
                    case Poly p:
                        var body = new List<int>(PaintInts(p.Paint)) { p.Rule, p.Contours.Length };
                        foreach (var c in p.Contours)
                        {
                            body.Add(c.Length / 2);
                            body.AddRange(c);
                        }
                        Rec(Op.Poly, [.. body]);
                        break;
                    case Rects r:
                        Rec(Op.Rects, [.. PaintInts(r.Paint), r.Boxes.Length / 4, .. r.Boxes]);
                        break;
                }
            }
            if (n.Pattern is { } pt)
                Rec(Op.Pattern, pt.Kind, pt.Pitch, pt.LineW, pt.Argb);
            if (n.Text is { } t)
                Rec(Op.Text, Str(t.Chars), t.Size, t.LineH, t.Argb, t.AlignH, t.AlignV, t.AutoResize);
            if (n.Image is { } im)
                Rec(Op.Image, Str(im.Ref), im.Tint);
            if (n.Instance is { } ins)
                Rec(Op.Instance, ins.Component, Str(ins.Variant));
            foreach (var c in n.Children)
                Emit(c);
            Rec(Op.End);
        }

        Rec(Op.Header, Op.Version, s.W, s.H, s.Root.PreOrder().Count(), s.Components.Count);
        Emit(s.Root);
        foreach (var c in s.Components)
        {
            Rec(Op.Component, c.Index, Str(c.Set), Str(c.Variant));
            Emit(c.Root);
        }
        return new Encoded([.. ints], [.. strs]);
    }

    public static Screen Decode(int[] v, string[] strs)
    {
        int pos = 0;
        var s = new Screen();
        var stack = new Stack<Node>();
        Node? top = null;
        (int Index, int Set, int Variant)? pendingComponent = null;

        string Str(int i) => i < 0 ? "" : strs[i];
        string? Opt(int i) => i < 0 ? null : strs[i];

        while (pos < v.Length)
        {
            int op = v[pos], n = v[pos + 1], p = pos + 2;
            pos = p + n;
            var cur = stack.Count > 0 ? stack.Peek() : null;

            Paint ReadPaint()
            {
                int kind = v[p++];
                if (kind == Paint.Solid)
                    return Paint.Of(v[p++]);
                var h = v[p..(p + 6)];
                p += 6;
                int len = v[p++];
                var lut = v[p..(p + len)];
                p += len;
                return new Paint { Kind = kind, Handles = h, Lut = lut };
            }

            switch (op)
            {
                case Op.Header:
                    s.W = v[p + 1];
                    s.H = v[p + 2];
                    break;
                case Op.Node:
                    var node = new Node
                    {
                        Binding = Opt(v[p]), Flags = v[p + 1], X = v[p + 2], Y = v[p + 3], W = v[p + 4],
                        H = v[p + 5], Layer = v[p + 6], SizingH = v[p + 7], SizingV = v[p + 8], ConsH = v[p + 9],
                        ConsV = v[p + 10]
                    };
                    if (cur != null)
                        cur.Children.Add(node);
                    else if (top == null)
                        top = s.Root = node;
                    else if (pendingComponent is { } pc)
                    {
                        s.Components.Add(new Component(pc.Index, Str(pc.Set), Str(pc.Variant), node));
                        pendingComponent = null;
                    }
                    stack.Push(node);
                    break;
                case Op.End:
                    stack.Pop();
                    break;
                case Op.Layout:
                    cur!.Layout = new LayoutRec(v[p], v[p + 1], v[p + 2], v[p + 3], v[p + 4], v[p + 5], v[p + 6],
                        v[p + 7], v[p + 8], v[p + 9], v[p + 10], v[p + 11]);
                    break;
                case Op.FillBox:
                    cur!.Draws.Add(new FillBox(ReadPaint()));
                    break;
                case Op.StrokeBox:
                    var sp = ReadPaint();
                    cur!.Draws.Add(new StrokeBox(sp, v[p], v[p + 1], v[p + 2], v[p + 3], v[p + 4]));
                    break;
                case Op.Poly:
                    var pp = ReadPaint();
                    int rule = v[p++], count = v[p++];
                    var contours = new int[count][];
                    for (int i = 0; i < count; i++)
                    {
                        int pts = v[p++];
                        contours[i] = v[p..(p + 2 * pts)];
                        p += 2 * pts;
                    }
                    cur!.Draws.Add(new Poly(pp, rule, contours));
                    break;
                case Op.Rects:
                    var rp = ReadPaint();
                    int boxes = v[p++];
                    cur!.Draws.Add(new Rects(rp, v[p..(p + 4 * boxes)]));
                    break;
                case Op.Pattern:
                    cur!.Pattern = new PatternRec(v[p], v[p + 1], v[p + 2], v[p + 3]);
                    break;
                case Op.Text:
                    cur!.Text = new TextRec(Str(v[p]), v[p + 1], v[p + 2], v[p + 3], v[p + 4], v[p + 5], v[p + 6]);
                    break;
                case Op.Image:
                    cur!.Image = new ImageRec(Str(v[p]), v[p + 1]);
                    break;
                case Op.Instance:
                    cur!.Instance = new InstanceRec(v[p], Str(v[p + 1]));
                    break;
                case Op.Component:
                    pendingComponent = (v[p], v[p + 1], v[p + 2]);
                    break;
            }
        }
        return s;
    }
}
