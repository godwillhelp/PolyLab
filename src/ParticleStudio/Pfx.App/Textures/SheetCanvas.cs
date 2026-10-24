using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pfx.Textures;

namespace Pfx.App.Textures;

// Pictures for the sheet views: as stored over a checkerboard, colour times alpha on black (how an additive or
// alpha-blended sprite looks on a dark scene), alpha alone as grey. One bitmap per image and view, made on demand.
public static class SheetBitmaps
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<RgbaImage, Dictionary<SheetView, BitmapSource>> Made = new();

    public static BitmapSource Of(RgbaImage image, SheetView view)
    {
        var byView = Made.GetOrCreateValue(image);
        if (byView.TryGetValue(view, out var hit))
            return hit;
        var src = image.Pixels;
        var bgra = new byte[src.Length];
        for (int i = 0; i < src.Length; i += 4)
        {
            byte r = src[i], g = src[i + 1], b = src[i + 2], a = src[i + 3];
            switch (view)
            {
                case SheetView.Black:
                    bgra[i] = (byte)(b * a / 255);
                    bgra[i + 1] = (byte)(g * a / 255);
                    bgra[i + 2] = (byte)(r * a / 255);
                    bgra[i + 3] = 255;
                    break;
                case SheetView.Alpha:
                    bgra[i] = bgra[i + 1] = bgra[i + 2] = a;
                    bgra[i + 3] = 255;
                    break;
                default:
                    bgra[i] = b;
                    bgra[i + 1] = g;
                    bgra[i + 2] = r;
                    bgra[i + 3] = a;
                    break;
            }
        }
        var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, bgra, image.Width * 4);
        bitmap.Freeze();
        byView[view] = bitmap;
        return bitmap;
    }

    // The checkerboard behind transparent pixels: two greys of the palette in 8 px squares.
    public static Brush Checker(FrameworkElement owner)
    {
        var dark = (Brush)owner.FindResource("B.BgInput");
        var light = (Brush)owner.FindResource("B.BgHover");
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(dark, null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        var squares = new GeometryGroup();
        squares.Children.Add(new RectangleGeometry(new Rect(0, 0, 8, 8)));
        squares.Children.Add(new RectangleGeometry(new Rect(8, 8, 8, 8)));
        group.Children.Add(new GeometryDrawing(light, null, squares));
        var brush = new DrawingBrush(group) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 16, 16), ViewportUnits = BrushMappingMode.Absolute };
        brush.Freeze();
        return brush;
    }
}

// What a sheet shows: the slicer's material texture, or the atlas builder's result.
public interface ISheetSource
{
    RgbaImage[]? Levels { get; }
    int TileU { get; }
    int TileV { get; }
    SheetView View { get; }
    bool Numbers { get; }
    int Selected { get; set; }
    // The tile that plays now (-1: none) and the live particles per tile (may be empty).
    int PlayingFrame { get; }
    int[] Live { get; }
}

// The texture with the material's tile grid over it (board 12): tile numbers in the game's order (row by row from the
// top-left), the tile the first sample particle shows now (accent), the tile picked for the sprite view (path colour)
// and how many live particles of the viewport show each tile (a count in the tile's corner). A click picks a tile.
public sealed class SheetCanvas : FrameworkElement
{
    private const double Inner = 6;
    // Numbers only in tiles at least this wide on screen, so they never cover the picture.
    private const double NumberRoom = 22;

    private Brush? _checker;

    public SheetCanvas()
    {
        ClipToBounds = true;
        Cursor = Cursors.Hand;
    }

    public ISheetSource? Model { get; set; }
    public int Mip { get; set; }

    private Rect ImageRect()
    {
        var levels = Model?.Levels;
        if (levels == null || levels.Length == 0)
            return Rect.Empty;
        var top = levels[0];
        double w = Math.Max(1, ActualWidth - 2 * Inner), h = Math.Max(1, ActualHeight - 2 * Inner);
        double s = Math.Min(w / top.Width, h / top.Height);
        double iw = top.Width * s, ih = top.Height * s;
        return new Rect(Inner + (w - iw) / 2, Inner + (h - ih) / 2, iw, ih);
    }

    private int TileAt(Point p)
    {
        var m = Model;
        var r = ImageRect();
        if (m == null || r.IsEmpty || !r.Contains(p))
            return -1;
        int col = Math.Min(m.TileU - 1, (int)((p.X - r.X) / r.Width * m.TileU));
        int row = Math.Min(m.TileV - 1, (int)((p.Y - r.Y) / r.Height * m.TileV));
        return row * m.TileU + col;
    }

    public Rect TileRect(int tile)
    {
        var m = Model!;
        var r = ImageRect();
        double tw = r.Width / m.TileU, th = r.Height / m.TileV;
        return new Rect(r.X + tile % m.TileU * tw, r.Y + tile / m.TileU * th, tw, th);
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle((Brush)FindResource("B.BgInput"), null, new Rect(0, 0, ActualWidth, ActualHeight));
        var m = Model;
        var r = ImageRect();
        if (m?.Levels is not { } levels || r.IsEmpty)
            return;
        var image = levels[Math.Clamp(Mip, 0, levels.Length - 1)];
        if (m.View == SheetView.Checker)
            dc.DrawRectangle(_checker ??= SheetBitmaps.Checker(this), null, r);
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        dc.DrawImage(SheetBitmaps.Of(image, m.View), r);
        var grid = new Pen(new SolidColorBrush(Color.FromArgb(150, 0x43, 0x50, 0x5B)), 1);
        grid.Freeze();
        for (int u = 0; u <= m.TileU; u++)
        {
            double x = Math.Round(r.X + r.Width * u / m.TileU) + 0.5;
            dc.DrawLine(grid, new Point(x, r.Top), new Point(x, r.Bottom));
        }
        for (int v = 0; v <= m.TileV; v++)
        {
            double y = Math.Round(r.Y + r.Height * v / m.TileV) + 0.5;
            dc.DrawLine(grid, new Point(r.Left, y), new Point(r.Right, y));
        }
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var face = new Typeface((FontFamily)FindResource("Font.Mono"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var dim = (Brush)FindResource("B.TextDim");
        var hi = (Brush)FindResource("B.TextHi");
        var accent = (Brush)FindResource("B.Accent");
        int playing = m.PlayingFrame;
        double tileW = r.Width / m.TileU;
        for (int i = 0; i < m.TileU * m.TileV; i++)
        {
            var t = TileRect(i);
            if (i == playing)
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(36, 0x2E, 0xC4, 0xD6)), new Pen(accent, 2), Inset(t, 1));
            }
            if (i == m.Selected)
                dc.DrawRectangle(null, new Pen((Brush)FindResource("B.Path"), 1.5), Inset(t, i == playing ? 3 : 1));
            if (m.Numbers && tileW >= NumberRoom)
            {
                var text = new FormattedText(i.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 10,
                    i == playing ? hi : dim, dpi);
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(150, 0x0E, 0x12, 0x15)), null, new Rect(t.X + 2, t.Y + 2, text.Width + 4, text.Height));
                dc.DrawText(text, new Point(t.X + 4, t.Y + 2));
            }
            if (i < m.Live.Length && m.Live[i] > 0 && tileW >= NumberRoom)
            {
                var count = new FormattedText("●" + m.Live[i].ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    face, 10, (Brush)FindResource("B.Key"), dpi);
                var at = new Point(t.Right - count.Width - 4, t.Bottom - count.Height - 2);
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(150, 0x0E, 0x12, 0x15)), null, new Rect(at.X - 2, at.Y, count.Width + 4, count.Height));
                dc.DrawText(count, at);
            }
        }
        dc.DrawRectangle(null, new Pen((Brush)FindResource("B.LineStrong"), 1), Inset(r, -0.5));
    }

    private static Rect Inset(Rect r, double by) => new(r.X + by, r.Y + by, Math.Max(0, r.Width - 2 * by), Math.Max(0, r.Height - 2 * by));

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        int tile = TileAt(e.GetPosition(this));
        if (tile >= 0 && Model != null)
            Model.Selected = tile;
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int tile = TileAt(e.GetPosition(this));
        var m = Model;
        string tip = m == null || tile < 0 ? Loc.T("Tip.SlicerSheet")
            : Loc.F("Tip.SlicerTile", tile, tile / m.TileU + 1, tile % m.TileU + 1, tile < m.Live.Length ? m.Live[tile] : 0);
        if (!Equals(ToolTip, tip))
            ToolTip = tip;
    }
}
