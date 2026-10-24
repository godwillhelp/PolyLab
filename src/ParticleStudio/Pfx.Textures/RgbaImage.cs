namespace Pfx.Textures;

// 8-bit RGBA pixels, rows top to bottom, 4 bytes per pixel in R, G, B, A order.
public sealed class RgbaImage
{
    public RgbaImage(int width, int height)
        : this(width, height, new byte[checked(width * height * 4)])
    {
    }

    public RgbaImage(int width, int height, byte[] pixels)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentException($"Image size {width}x{height}");
        if (pixels.Length != width * height * 4)
            throw new ArgumentException($"{pixels.Length} bytes for {width}x{height} RGBA");
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    public RgbaImage Clone() => new(Width, Height, (byte[])Pixels.Clone());

    public RgbaImage Crop(int x, int y, int width, int height)
    {
        if (x < 0 || y < 0 || x + width > Width || y + height > Height)
            throw new ArgumentOutOfRangeException(nameof(width), $"Crop {x},{y} {width}x{height} outside {Width}x{Height}");
        var output = new RgbaImage(width, height);
        for (int row = 0; row < height; row++)
            Array.Copy(Pixels, ((y + row) * Width + x) * 4, output.Pixels, row * width * 4, width * 4);
        return output;
    }

    public void Paste(RgbaImage source, int x, int y)
    {
        if (x < 0 || y < 0 || x + source.Width > Width || y + source.Height > Height)
            throw new ArgumentOutOfRangeException(nameof(source), $"Paste {source.Width}x{source.Height} at {x},{y} outside {Width}x{Height}");
        for (int row = 0; row < source.Height; row++)
            Array.Copy(source.Pixels, row * source.Width * 4, Pixels, ((y + row) * Width + x) * 4, source.Width * 4);
    }
}
