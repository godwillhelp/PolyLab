namespace Pfx.Textures;

// Export rules learned in game (prompt 2.5, 2.11): DXT must be power-of-two on both sides (NPOT DXT renders as block
// garbage), uncompressed may be NPOT; vanilla particle textures stay at or below 2048.
public static class TextureRules
{
    public const int DefaultMaxSide = 2048;

    public static bool IsPowerOfTwo(int n) => n > 0 && (n & (n - 1)) == 0;

    // Nearest in log2, as xray.py pow2(): 600 → 512, 768 → 1024.
    public static int NearestPowerOfTwo(int n) => 1 << Math.Max(0, (int)Math.Round(Math.Log2(Math.Max(1, n)), MidpointRounding.ToEven));

    public static void RequireWritable(EddsFormat format, int width, int height)
    {
        if (format != EddsFormat.Rgba8 && !(IsPowerOfTwo(width) && IsPowerOfTwo(height)))
            throw new ArgumentException($"{format} needs power-of-two sides, got {width}x{height} (NPOT DXT renders as garbage in game)");
    }

    public static void Check(EddsFormat format, int width, int height, int maxSide)
    {
        RequireWritable(format, width, height);
        if (Math.Max(width, height) > maxSide)
            throw new ArgumentException($"{width}x{height} is over the maximum side {maxSide}");
    }
}
