using System.Text.Json;
using System.Text.RegularExpressions;

namespace PolyLab.CgUi;

public sealed partial class FigmaReader
{
    private readonly JsonElement _nodes;
    private readonly Dictionary<string, JsonElement> _meta = [];
    private readonly HashSet<string> _live;
    private readonly bool _images;
    private Screen _s = new();
    private readonly Dictionary<string, int> _componentIndex = [];
    private readonly Dictionary<string, Rgb?> _setBackground = [];
    private readonly HashSet<string> _missingSets = [];

    private readonly record struct Ctx(int Ax, int Ay, int W, int H, Rgb? D, double Opacity, bool Screen);

    private static readonly HashSet<string> Containers = ["FRAME", "GROUP", "COMPONENT", "INSTANCE", "SECTION"];
    private static readonly HashSet<string> Leaves =
        ["RECTANGLE", "ELLIPSE", "VECTOR", "LINE", "STAR", "REGULAR_POLYGON", "BOOLEAN_OPERATION"];
    private static readonly HashSet<string> BoxTypes = ["FRAME", "RECTANGLE", "COMPONENT", "INSTANCE"];

    public FigmaReader(JsonElement response, IEnumerable<string> live, bool images)
    {
        _nodes = response.GetProperty("nodes");
        _live = [.. live];
        _images = images;
        foreach (var entry in _nodes.EnumerateObject())
            if (entry.Value.TryGetProperty("components", out var comps))
                foreach (var c in comps.EnumerateObject())
                    _meta[c.Name] = c.Value;
    }

    public Screen Read(string nodeId, string name)
    {
        if (!_nodes.TryGetProperty(nodeId, out var entry) || entry.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"node {nodeId} is not in the read-back");
        var doc = entry.GetProperty("document");
        _s = new Screen { Name = name, FigmaId = nodeId };
        _componentIndex.Clear();
        _setBackground.Clear();
        _missingSets.Clear();
        var size = doc.GetProperty("size");
        _s.W = Geometry.R16(size.GetProperty("x").GetDouble());
        _s.H = Geometry.R16(size.GetProperty("y").GetDouble());

        var sets = new List<string>();
        CollectSets(doc, sets);
        var variants = new List<(string Set, JsonElement Doc)>();
        for (int k = 0; k < sets.Count; k++)
        {
            if (!_nodes.TryGetProperty(sets[k], out var se) || se.ValueKind != JsonValueKind.Object)
                continue;
            foreach (var v in se.GetProperty("document").GetProperty("children").EnumerateArray())
            {
                _componentIndex[v.GetProperty("id").GetString()!] = variants.Count;
                variants.Add((sets[k], v));
                CollectSets(v, sets);
            }
        }

        var bb = Abs(doc);
        _s.Root = Visit(doc, new Ctx(bb.X, bb.Y, _s.W, _s.H, null, 1, true))!;
        AssignLayers(_s.Root);
        for (int i = 0; i < variants.Count; i++)
        {
            var (set, v) = variants[i];
            var vb = Abs(v);
            var root = Visit(v, new Ctx(vb.X, vb.Y, 0, 0, _setBackground.GetValueOrDefault(set), 1, false))!;
            AssignLayers(root);
            string setName = _nodes.GetProperty(set).GetProperty("document").GetProperty("name").GetString()!;
            _s.Components.Add(new Component(i, setName, v.GetProperty("name").GetString()!, root));
        }
        return _s;
    }

    private void CollectSets(JsonElement e, List<string> sets)
    {
        if (Str(e, "type") == "INSTANCE" && ImageRef(e) == null
            && _meta.TryGetValue(Str(e, "componentId"), out var m) && m.TryGetProperty("componentSetId", out var set)
            && !sets.Contains(set.GetString()!))
            sets.Add(set.GetString()!);
        if (e.TryGetProperty("children", out var ch))
            foreach (var c in ch.EnumerateArray())
                CollectSets(c, sets);
    }

    private static string Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static double Num(JsonElement e, string key, double def) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : def;

    private static bool Flag(JsonElement e, string key, bool def) =>
        e.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : def;

    private static (int X, int Y, int W, int H) Abs(JsonElement e)
    {
        var b = e.GetProperty("absoluteBoundingBox");
        return (Geometry.R16(b.GetProperty("x").GetDouble()), Geometry.R16(b.GetProperty("y").GetDouble()),
            Geometry.R16(b.GetProperty("width").GetDouble()), Geometry.R16(b.GetProperty("height").GetDouble()));
    }

    private void Diag(JsonElement e, bool error, string msg) =>
        _s.Diags.Add(new Diag(error, Str(e, "id"), Str(e, "name"), msg));

    private string? ImageRef(JsonElement e)
    {
        if (!_images)
            return null;
        if (Str(e, "type") == "INSTANCE" && _meta.TryGetValue(Str(e, "componentId"), out var m)
            && Str(m, "name") is { } mn && mn.StartsWith("img:", StringComparison.Ordinal))
            return mn[4..];
        string name = Str(e, "name");
        return name.StartsWith("img:", StringComparison.Ordinal) ? name[4..] : null;
    }

    private static IEnumerable<JsonElement> Paints(JsonElement e, string key) =>
        e.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().Where(p => Flag(p, "visible", true))
            : [];

    private static Rgb ColourOf(JsonElement p)
    {
        var c = p.GetProperty("color");
        return Colour.Quantise(new Rgb(c.GetProperty("r").GetDouble(), c.GetProperty("g").GetDouble(),
            c.GetProperty("b").GetDouble()));
    }

    private static double AlphaOf(JsonElement p) => Num(p, "opacity", 1) * Num(p.GetProperty("color"), "a", 1);

    private int CanvasArgb(JsonElement e, Rgb c, double a, Rgb? d)
    {
        if (a < 1 && d == null)
            Diag(e, false, "translucent paint without an ancestor fill: alpha kept, the game blends it in linear light");
        return Colour.Argb(Colour.Alpha8(c, a, d), c);
    }

    private Paint? CanvasPaint(JsonElement e, JsonElement p, double opacity, Rgb? d)
    {
        string type = Str(p, "type");
        if (Str(p, "blendMode") is var bm && bm != "" && bm != "NORMAL")
            Diag(e, false, $"paint blend mode {bm} drawn as Normal");
        switch (type)
        {
            case "SOLID":
                return Paint.Of(CanvasArgb(e, ColourOf(p), AlphaOf(p) * opacity, d));
            case "GRADIENT_LINEAR":
            case "GRADIENT_RADIAL":
                var stops = p.GetProperty("gradientStops").EnumerateArray()
                    .Select(s => (s.GetProperty("position").GetDouble(), ColourOf(s), Num(s.GetProperty("color"), "a", 1)))
                    .ToList();
                double po = Num(p, "opacity", 1) * opacity;
                if (d == null && stops.Any(s => s.Item3 * po < 1))
                    Diag(e, false, "translucent gradient without an ancestor fill: alpha kept");
                var h = p.GetProperty("gradientHandlePositions").EnumerateArray()
                    .SelectMany(q => new[] { q.GetProperty("x").GetDouble(), q.GetProperty("y").GetDouble() })
                    .Select(v => (int)Math.Floor(v * 65536 + 0.5)).ToArray();
                return new Paint
                {
                    Kind = type == "GRADIENT_LINEAR" ? Paint.Linear : Paint.Radial,
                    Handles = h,
                    Lut = Colour.Lut(stops, po, d)
                };
            case "IMAGE":
                Diag(e, true, "image fill on a node not named img: (no raster baking, D1)");
                return null;
            default:
                Diag(e, false, $"paint {type} is not supported in V2, skipped");
                return null;
        }
    }

    private int TextArgb(JsonElement e, double opacity)
    {
        foreach (var p in Paints(e, "fills"))
        {
            if (Str(p, "type") == "SOLID")
                return Colour.Argb(Colour.To8(AlphaOf(p) * opacity), ColourOf(p));
            Diag(e, false, $"text paint {Str(p, "type")} is not supported, text drawn white");
            break;
        }
        return Colour.Argb(255, 255, 255, 255);
    }

    private int Tint(JsonElement e, double opacity)
    {
        foreach (var key in new[] { "strokes", "fills" })
            foreach (var p in Paints(e, key))
                if (Str(p, "type") == "SOLID")
                    return Colour.Argb(Colour.To8(AlphaOf(p) * opacity), ColourOf(p));
        if (e.TryGetProperty("children", out var ch))
            foreach (var c in ch.EnumerateArray().Where(c => Flag(c, "visible", true)))
                if (Tint(c, opacity * Num(c, "opacity", 1)) is var t && t != 0)
                    return t;
        return 0;
    }

    private static int Sizing(string v) => v switch { "HUG" => 1, "FILL" => 2, _ => 0 };

    private static int Cons(string v) => v switch
    {
        "RIGHT" or "BOTTOM" => 1,
        "CENTER" => 2,
        "LEFT_RIGHT" or "TOP_BOTTOM" => 3,
        "SCALE" => 4,
        _ => 0
    };

    [GeneratedRegex("^pattern:(grid|scan|stripe)([0-9]+)$")]
    private static partial Regex PatternName();

    private Node? Visit(JsonElement e, Ctx ctx)
    {
        string type = Str(e, "type"), name = Str(e, "name");
        if (!Containers.Contains(type) && !Leaves.Contains(type) && type != "TEXT")
        {
            Diag(e, false, $"node type {type} is not supported, skipped");
            return null;
        }
        var abs = Abs(e);
        var rt = e.GetProperty("relativeTransform");
        bool rotated = !(rt[0][0].GetDouble() == 1 && rt[0][1].GetDouble() == 0 && rt[1][0].GetDouble() == 0
                         && rt[1][1].GetDouble() == 1);
        var size = e.GetProperty("size");
        double sw = size.GetProperty("x").GetDouble(), sh = size.GetProperty("y").GetDouble();
        double opacity = ctx.Opacity * Num(e, "opacity", 1);
        var cons = e.GetProperty("constraints");
        var n = new Node
        {
            Id = Str(e, "id"),
            Name = name,
            Binding = name.StartsWith('$') ? name : null,
            Flags = (Flag(e, "visible", true) ? Flags.Visible : 0) | (Flag(e, "clipsContent", false) ? Flags.Clip : 0)
                    | (Str(e, "layoutPositioning") == "ABSOLUTE" ? Flags.Absolute : 0)
                    | (_live.Contains(name) ? Flags.Live : 0),
            X = abs.X - ctx.Ax,
            Y = abs.Y - ctx.Ay,
            W = rotated ? abs.W : Geometry.R16(sw),
            H = rotated ? abs.H : Geometry.R16(sh),
            SizingH = Sizing(Str(e, "layoutSizingHorizontal")),
            SizingV = Sizing(Str(e, "layoutSizingVertical")),
            ConsH = Cons(Str(cons, "horizontal")),
            ConsV = Cons(Str(cons, "vertical"))
        };

        if (e.TryGetProperty("effects", out var fx) && fx.EnumerateArray().Any(f => Flag(f, "visible", true)))
            Diag(e, false, "effects (shadow, blur) are not drawn");
        if (Str(e, "blendMode") is var bm && bm is not ("" or "PASS_THROUGH" or "NORMAL"))
            Diag(e, false, $"blend mode {bm} drawn as Normal");
        if (Flag(e, "isMask", false))
            Diag(e, false, "vector mask is not supported, drawn unmasked");
        if (e.TryGetProperty("strokeDashes", out var dash) && dash.GetArrayLength() > 0 && Paints(e, "strokes").Any())
            Diag(e, false, "dashed stroke drawn solid (V2)");
        if (rotated && !Leaves.Contains(type))
            Diag(e, true, "rotation is supported on vector shapes only");

        if (PatternName().Match(name) is { Success: true } pm)
        {
            var stroke = Paints(e, "strokes").FirstOrDefault(p => Str(p, "type") == "SOLID");
            if (stroke.ValueKind == JsonValueKind.Undefined)
            {
                Diag(e, true, "pattern without a solid stroke colour");
                return n;
            }
            int argb = CanvasArgb(e, ColourOf(stroke), AlphaOf(stroke) * opacity, ctx.D);
            int kind = pm.Groups[1].Value switch { "grid" => 0, "scan" => 1, _ => 2 };
            if (kind == 0 && (argb >>> 24) < 255)
                Diag(e, false, "translucent grid: crossings blend twice");
            n.X = n.Y = 0;
            n.W = ctx.W;
            n.H = ctx.H;
            n.ConsH = n.ConsV = 3;
            n.Pattern = new PatternRec(kind, Math.Max(1, int.Parse(pm.Groups[2].Value)),
                Math.Max(1, (int)Math.Round(Num(e, "strokeWeight", 1))), argb);
            return n;
        }

        if (ImageRef(e) is { } img)
        {
            int tint = Tint(e, opacity);
            n.Image = new ImageRec(img, tint == 0 ? Colour.Argb(255, 255, 255, 255) : tint);
            return n;
        }

        if (type == "TEXT")
        {
            var st = e.GetProperty("style");
            n.Text = new TextRec(Str(e, "characters"), Geometry.R16(Num(st, "fontSize", 0)),
                Geometry.R16(Num(st, "lineHeightPx", 0)), TextArgb(e, opacity),
                Str(st, "textAlignHorizontal") switch { "CENTER" => 1, "RIGHT" => 2, "JUSTIFIED" => 3, _ => 0 },
                Str(st, "textAlignVertical") switch { "CENTER" => 1, "BOTTOM" => 2, _ => 0 },
                Str(st, "textAutoResize") switch { "HEIGHT" => 1, "WIDTH_AND_HEIGHT" => 2, "TRUNCATE" => 3, _ => 0 });
            if (e.TryGetProperty("characterStyleOverrides", out var cso) && cso.EnumerateArray().Any(v => v.GetInt32() != 0))
                Diag(e, false, "mixed text styles in one node: drawn with the base style");
            if (Num(st, "letterSpacing", 0) != 0)
                Diag(e, false, "letter spacing is not supported");
            if (Str(st, "textCase") is var tc && tc is not ("" or "ORIGINAL"))
                Diag(e, false, $"text case {tc} is not applied (bake it into the text)");
            if (Str(st, "textDecoration") is var td && td is not ("" or "NONE"))
                Diag(e, false, $"text decoration {td} is not drawn");
            if (Paints(e, "strokes").Any())
                Diag(e, false, "text strokes are not drawn");
            return n;
        }

        if (type == "INSTANCE" && _meta.TryGetValue(Str(e, "componentId"), out var meta))
        {
            int index = _componentIndex.GetValueOrDefault(Str(e, "componentId"), -1);
            if (meta.TryGetProperty("componentSetId", out var setId))
            {
                string set = setId.GetString()!;
                _setBackground.TryAdd(set, ctx.D);
                if (index < 0 && _missingSets.Add(set))
                    Diag(e, false, $"component set {set} is not in the read-back: instances compiled expanded only");
            }
            n.Instance = new InstanceRec(index, Str(meta, "name"));
        }

        if (e.TryGetProperty("layoutMode", out var lm) && lm.GetString() is "HORIZONTAL" or "VERTICAL")
            n.Layout = new LayoutRec(lm.GetString() == "VERTICAL" ? 1 : 0,
                Geometry.R16(Num(e, "itemSpacing", 0)), Geometry.R16(Num(e, "paddingTop", 0)),
                Geometry.R16(Num(e, "paddingRight", 0)), Geometry.R16(Num(e, "paddingBottom", 0)),
                Geometry.R16(Num(e, "paddingLeft", 0)),
                Str(e, "primaryAxisAlignItems") switch { "CENTER" => 1, "MAX" => 2, "SPACE_BETWEEN" => 3, _ => 0 },
                Str(e, "counterAxisAlignItems") switch { "CENTER" => 1, "MAX" => 2, "BASELINE" => 3, _ => 0 },
                Str(e, "layoutWrap") == "WRAP" ? 1 : 0, Geometry.R16(Num(e, "minHeight", 0)),
                Flag(e, "strokesIncludedInLayout", false) ? 1 : 0);

        bool box = !rotated && BoxTypes.Contains(type) && Num(e, "cornerRadius", 0) == 0
                   && (!e.TryGetProperty("rectangleCornerRadii", out var rr) || rr.EnumerateArray().All(r => r.GetDouble() == 0));
        Func<Pt, Pt> map = p => p;
        if (rotated)
        {
            double a = rt[0][0].GetDouble(), b = rt[0][1].GetDouble(), c = rt[1][0].GetDouble(), d = rt[1][1].GetDouble();
            Pt M(Pt p) => new(a * p.X + b * p.Y, c * p.X + d * p.Y);
            Pt[] corners = [M(new(0, 0)), M(new(sw, 0)), M(new(0, sh)), M(new(sw, sh))];
            var min = new Pt(corners.Min(q => q.X), corners.Min(q => q.Y));
            map = p => M(p) - min;
        }

        var under = ctx.D;
        foreach (var p in Paints(e, "fills"))
        {
            if (ctx.Screen)
            {
                _s.Backdrop ??= CanvasPaint(e, p, 1, Rgb.Of8(0, 0, 0));
                continue;
            }
            if (CanvasPaint(e, p, opacity, ctx.D) is not { } paint)
                continue;
            if (box)
                n.Draws.Add(new FillBox(paint));
            else
                AddPolys(e, n, "fillGeometry", paint, map);
            if (Str(p, "type") == "SOLID")
            {
                var c = ColourOf(p);
                under = under is { } u ? Colour.Quantise(c.Over(u, AlphaOf(p) * opacity)) : c;
            }
        }
        if (ctx.Screen && Paints(e, "strokes").Any())
            Diag(e, false, "strokes on the screen frame are preview only, not compiled");
        double weight = Num(e, "strokeWeight", 0);
        foreach (var p in ctx.Screen ? Enumerable.Empty<JsonElement>() : Paints(e, "strokes"))
        {
            if (CanvasPaint(e, p, opacity, ctx.D) is not { } paint)
                continue;
            if (box)
            {
                int w = Geometry.R16(weight);
                int t = w, r = w, b = w, l = w;
                if (e.TryGetProperty("individualStrokeWeights", out var iw))
                {
                    t = Geometry.R16(Num(iw, "top", 0));
                    r = Geometry.R16(Num(iw, "right", 0));
                    b = Geometry.R16(Num(iw, "bottom", 0));
                    l = Geometry.R16(Num(iw, "left", 0));
                }
                if (t + r + b + l > 0)
                    n.Draws.Add(new StrokeBox(paint, t, r, b, l,
                        Str(e, "strokeAlign") switch { "CENTER" => 1, "OUTSIDE" => 2, _ => 0 }));
            }
            else
                AddPolys(e, n, "strokeGeometry", paint, map);
        }

        if (Num(e, "opacity", 1) < 1 && e.TryGetProperty("children", out var kids) && kids.GetArrayLength() > 1)
            Diag(e, false, "layer opacity on a group: overlapping children blend twice (no group alpha on the canvas)");

        if (Containers.Contains(type) && e.TryGetProperty("children", out var children))
        {
            var inner = new Ctx(abs.X, abs.Y, n.W, n.H, under, opacity, false);
            foreach (var c in children.EnumerateArray())
                if (Visit(c, inner) is { } child)
                    n.Children.Add(child);
        }
        return n;
    }

    private void AddPolys(JsonElement e, Node n, string key, Paint paint, Func<Pt, Pt> map)
    {
        if (!e.TryGetProperty(key, out var geo) || geo.GetArrayLength() == 0)
        {
            Diag(e, false, $"{key} missing: request the nodes with geometry=paths");
            return;
        }
        foreach (var g in geo.EnumerateArray().GroupBy(g => Str(g, "windingRule") == "EVENODD" ? 1 : 0))
        {
            var contours = new List<int[]>();
            foreach (var path in g)
            {
                try
                {
                    contours.AddRange(Geometry.ToFixed(Geometry.Flatten(Str(path, "path")), map));
                }
                catch (FormatException ex)
                {
                    Diag(e, true, ex.Message);
                }
            }
            if (contours.Count > 0)
                n.Draws.Add(new Poly(paint, g.Key, [.. contours]));
        }
    }

    private static void AssignLayers(Node root)
    {
        var items = new List<(Node N, int X0, int Y0, int X1, int Y1, bool Ghost)>();
        void Collect(Node n, int px, int py, bool ghost)
        {
            int x = px + n.X, y = py + n.Y;
            items.Add((n, x, y, x + n.W, y + n.H, ghost));
            foreach (var c in n.Children)
                Collect(c, x, y, ghost || (!c.Visible && n.Layout != null && (c.Flags & Flags.Absolute) == 0));
        }
        Collect(root, -root.X, -root.Y, false);
        for (int i = 0; i < items.Count; i++)
        {
            var a = items[i];
            int layer = 0;
            for (int j = 0; j < i && (a.N.IsShape || a.N.IsWidget); j++)
            {
                var b = items[j];
                if (b.Ghost || !(b.N.IsShape || b.N.IsWidget) || !(a.X0 < b.X1 && b.X0 < a.X1 && a.Y0 < b.Y1 && b.Y0 < a.Y1))
                    continue;
                layer = Math.Max(layer, b.N.Layer);
                if (a.N.IsShape && b.N.IsWidget)
                    layer = Math.Max(layer, b.N.Layer + 1);
            }
            a.N.Layer = layer;
        }
    }
}
