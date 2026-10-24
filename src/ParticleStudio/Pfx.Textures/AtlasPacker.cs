namespace Pfx.Textures;

public sealed record Atlas(RgbaImage Image, int TileU, int TileV);

// Frames → flipbook atlas. The game plays every tile row by row (prompt 2.5), so the grid holds exactly the frames:
// cols x rows = count, the pair whose grid is closest to square (on a tie, the wider one). The atlas takes the nearest
// power of two per side (at most maxSide) unless a size is given. When the sides split into whole frames each frame is
// resized on its own (no filter reaches across a tile border, the 2.11 lesson); otherwise the grid is resized as a
// whole and frames end up a fractional number of pixels wide, which the game draws cleanly (4096x2048 lightning atlas
// of 8x5 frames, 2026-10-11).
public static class AtlasPacker
{
    public static Atlas Pack(IReadOnlyList<RgbaImage> frames, int maxSide, (int W, int H)? size = null)
    {
        if (frames.Count == 0)
            throw new ArgumentException("No frames");
        if (!TextureRules.IsPowerOfTwo(maxSide))
            throw new ArgumentException($"Maximum side {maxSide} is not a power of two");
        var (cols, rows) = Grid(frames.Count, frames[0].Width, frames[0].Height);
        var (w, h) = size ?? AutoSize(frames[0].Width * cols, frames[0].Height * rows, maxSide);
        if (w > maxSide || h > maxSide)
            throw new ArgumentException($"Atlas {w}x{h} is over the maximum side {maxSide}");
        if (w % cols == 0 && h % rows == 0)
        {
            int fw = w / cols, fh = h / rows;
            var resized = frames.Select(f => f.Width == fw && f.Height == fh ? f : Resample.Lanczos(f, fw, fh)).ToList();
            return new Atlas(TextureTools.Join(resized, cols, rows), cols, rows);
        }
        var grid = TextureTools.Join(frames, cols, rows);
        return new Atlas(Resample.Lanczos(grid, w, h), cols, rows);
    }

    public static (int W, int H) AutoSize(int gridWidth, int gridHeight, int maxSide) =>
        (Math.Min(maxSide, TextureRules.NearestPowerOfTwo(gridWidth)), Math.Min(maxSide, TextureRules.NearestPowerOfTwo(gridHeight)));

    public static (int Cols, int Rows) Grid(int count, int frameWidth, int frameHeight)
    {
        (int, int) best = (count, 1);
        double bestScore = double.MaxValue;
        for (int cols = 1; cols <= count; cols++)
        {
            if (count % cols != 0)
                continue;
            double score = Math.Abs(Math.Log((double)cols * frameWidth / (count / cols * frameHeight)));
            if (score <= bestScore)
            {
                bestScore = score;
                best = (cols, count / cols);
            }
        }
        return best;
    }
}
