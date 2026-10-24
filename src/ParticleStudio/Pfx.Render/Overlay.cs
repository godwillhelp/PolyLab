using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Pfx.Render;

// A monospaced glyph atlas rasterised once by WPF (grey anti-aliasing) and drawn by the renderer, because WPF cannot
// draw over the swap chain window (airspace). Covers ASCII, Cyrillic incl. Ukrainian letters and a few signs.
public sealed class GlyphAtlas
{
    private const string Extra = "ҐґЁё×·°µ…–—±≈→▲▼■";
    private readonly Dictionary<char, int> _index = new();

    private GlyphAtlas(int cellWidth, int cellHeight, int columns, int width, int height, byte[] alpha)
    {
        CellWidth = cellWidth;
        CellHeight = cellHeight;
        Columns = columns;
        Width = width;
        Height = height;
        Alpha = alpha;
    }

    public int CellWidth { get; }
    public int CellHeight { get; }
    public int Columns { get; }
    public int Width { get; }
    public int Height { get; }
    public byte[] Alpha { get; }
    // Cell 0 is solid, for filled rectangles.
    public const int SolidCell = 0;

    public int CellOf(char c) => _index.TryGetValue(c, out var i) ? i : _index['?'];

    public Vector4 UvOf(int cell) => new(
        (cell % Columns) * CellWidth / (float)Width, (cell / Columns) * CellHeight / (float)Height,
        (cell % Columns + 1) * CellWidth / (float)Width, (cell / Columns + 1) * CellHeight / (float)Height);

    public static GlyphAtlas Build(string family = "Consolas", double sizePx = 13)
    {
        var chars = new List<char>();
        for (char c = ' '; c <= '~'; c++)
            chars.Add(c);
        for (char c = 'Ѐ'; c <= 'џ'; c++)
            chars.Add(c);
        chars.AddRange(Extra.Where(c => !chars.Contains(c)));

        var typeface = new Typeface(new FontFamily(family), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        FormattedText Text(string s) => new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, sizePx,
            Brushes.White, 1.0);
        var probe = Text("M");
        int cw = (int)Math.Ceiling(probe.WidthIncludingTrailingWhitespace);
        int ch = (int)Math.Ceiling(probe.Height);
        int count = chars.Count + 1;
        int columns = 32;
        int rows = (count + columns - 1) / columns;
        int width = columns * cw, height = rows * ch;

        var visual = new DrawingVisual();
        TextOptions.SetTextFormattingMode(visual, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(visual, TextRenderingMode.Grayscale);
        var atlas = new GlyphAtlas(cw, ch, columns, width, height, new byte[width * height]);
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, cw, ch));
            for (int i = 0; i < chars.Count; i++)
            {
                int cell = i + 1;
                atlas._index[chars[i]] = cell;
                dc.DrawText(Text(chars[i].ToString()), new Point(cell % columns * cw, cell / columns * ch));
            }
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var bgra = new byte[width * height * 4];
        bitmap.CopyPixels(bgra, width * 4, 0);
        for (int i = 0; i < width * height; i++)
            atlas.Alpha[i] = bgra[i * 4 + 3];
        return atlas;
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct OverlayVertex
{
    public Vector2 Pixel;
    public Vector2 Uv;
    public Vector4 Color;
}

public readonly record struct OverlayLine(string Text, Vector4 Color);

// Screen-space quads for one frame: text lines in panels, in device pixels from the top-left.
public sealed class OverlayBuilder
{
    private readonly GlyphAtlas _atlas;

    public OverlayBuilder(GlyphAtlas atlas) => _atlas = atlas;

    public List<OverlayVertex> Vertices { get; } = new();

    public void Clear() => Vertices.Clear();

    public void Rect(float x, float y, float w, float h, Vector4 color) => Quad(x, y, w, h, _atlas.UvOf(GlyphAtlas.SolidCell), color);

    public void Text(float x, float y, string text, Vector4 color)
    {
        foreach (char c in text)
        {
            if (c != ' ')
                Quad(x, y, _atlas.CellWidth, _atlas.CellHeight, _atlas.UvOf(_atlas.CellOf(c)), color);
            x += _atlas.CellWidth;
        }
    }

    // A block of lines with a dark panel behind it; returns the panel height.
    public float Panel(float x, float y, IReadOnlyList<OverlayLine> lines, Vector4 background)
    {
        if (lines.Count == 0)
            return 0;
        const float pad = 4;
        float w = lines.Max(l => l.Text.Length) * _atlas.CellWidth + pad * 2;
        float h = lines.Count * _atlas.CellHeight + pad * 2;
        Rect(x, y, w, h, background);
        for (int i = 0; i < lines.Count; i++)
            Text(x + pad, y + pad + i * _atlas.CellHeight, lines[i].Text, lines[i].Color);
        return h;
    }

    public float LineHeight => _atlas.CellHeight;

    // A straight stroke of the given width in pixels (gizmo handles and drawn paths: lines of the line pass are one
    // pixel wide, too thin to aim at).
    public void Line(float x0, float y0, float x1, float y1, float width, Vector4 color)
    {
        float dx = x1 - x0, dy = y1 - y0;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 1e-3f)
            return;
        float nx = -dy / len * width * 0.5f, ny = dx / len * width * 0.5f;
        var uv = _atlas.UvOf(GlyphAtlas.SolidCell);
        var mid = new Vector2((uv.X + uv.Z) * 0.5f, (uv.Y + uv.W) * 0.5f);
        OverlayVertex V(float x, float y) => new() { Pixel = new(x, y), Uv = mid, Color = color };
        var a = V(x0 + nx, y0 + ny);
        var b = V(x1 + nx, y1 + ny);
        var c = V(x0 - nx, y0 - ny);
        var d = V(x1 - nx, y1 - ny);
        Vertices.Add(a);
        Vertices.Add(b);
        Vertices.Add(c);
        Vertices.Add(c);
        Vertices.Add(b);
        Vertices.Add(d);
    }

    // A filled square centred on a point.
    public void Dot(float x, float y, float size, Vector4 color) => Rect(x - size * 0.5f, y - size * 0.5f, size, size, color);

    private void Quad(float x, float y, float w, float h, Vector4 uv, Vector4 color)
    {
        var a = new OverlayVertex { Pixel = new(x, y), Uv = new(uv.X, uv.Y), Color = color };
        var b = new OverlayVertex { Pixel = new(x + w, y), Uv = new(uv.Z, uv.Y), Color = color };
        var c = new OverlayVertex { Pixel = new(x, y + h), Uv = new(uv.X, uv.W), Color = color };
        var d = new OverlayVertex { Pixel = new(x + w, y + h), Uv = new(uv.Z, uv.W), Color = color };
        Vertices.Add(a);
        Vertices.Add(b);
        Vertices.Add(c);
        Vertices.Add(c);
        Vertices.Add(b);
        Vertices.Add(d);
    }
}
