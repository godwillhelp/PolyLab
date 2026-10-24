using System.Collections.Concurrent;
using Pfx.App.Model;
using Pfx.Textures;

namespace Pfx.App.Textures;

public sealed record InfoRow(string Label, string Value, string Tip, string Brush = "B.TextHi", bool Header = false);

// The rows of the texture panel (board 12, right column; board 4): the file, how it sits in video memory, where it is
// used, the frame density in metres against the 512 px/m rule of the anomaly work (2.11), and the export rules of 2.5.
public static class TextureFacts
{
    // Frame density the anomaly work settled on (2.11): the largest sprite gets 512 texels per metre, at most 2048.
    public const double PixelsPerMetre = 512;

    private static readonly ConcurrentDictionary<string, (DateTime Time, TextureSummary? Summary)> Summaries = new(StringComparer.OrdinalIgnoreCase);

    // Summaries by path, read again when the file's write time changes. Thread-safe (effect totals run on a worker).
    public static TextureSummary? SummaryOf(string path)
    {
        DateTime time;
        try
        {
            time = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        if (Summaries.TryGetValue(path, out var hit) && hit.Time == time)
            return hit.Summary;
        TextureSummary? s;
        try
        {
            s = TextureSummary.Read(path);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException or ArgumentException)
        {
            s = null;
        }
        Summaries[path] = (time, s);
        return s;
    }

    // Every texture file the effect's materials name (albedo and normal maps), once each.
    public static IReadOnlyList<string> TexturesOf(EffectDoc doc, Func<string, string?> resolve) => doc.Materials.Values
        .SelectMany(m => new[] { "AlbedoMap", "NormalMap" }.Select(k => m.Emat.Block.FindLine(k)?.Values.FirstOrDefault()?.Value))
        .OfType<string>().Select(resolve).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static string Mb(long bytes) => Loc.Num(Math.Round(bytes / 1048576.0, 2));

    public static List<InfoRow> Rows(SlicerModel s, IReadOnlyList<EffectDoc> docs, Func<string, string?> resolve, (long Bytes, int Count)? effect)
    {
        var rows = new List<InfoRow>();
        void Head(string key) => rows.Add(new InfoRow(Loc.T(key), "", "", "B.Text", true));
        void Row(string label, string value, string tip, string brush = "B.TextHi") => rows.Add(new InfoRow(Loc.T(label), value, Loc.T(tip), brush));

        Head("Tex.File");
        Row("Tex.Path", s.TextureGamePath ?? Loc.T("Val.None"), "Tip.TexPath", "B.Text");
        if (s.Summary is { } t)
        {
            Row("Tex.Format", Loc.F("Tex.FormatValue", t.Kind, t.Format), "Tip.TexFormat");
            Row("Tex.Size", Loc.F(t.PowerOfTwo ? "Tex.SizePot" : "Tex.SizeNpot", t.Width, t.Height), "Tip.TexSize", t.PowerOfTwo || !t.Compressed ? "B.TextHi" : "B.Err");
            if (t.Kind is "EDDS" or "DDS")
                Row("Tex.Enf1", Loc.T(t.Enf1 ? "Tex.Enf1Yes" : "Tex.Enf1No"), "Tip.TexEnf1", t.Enf1 ? "B.Ok" : "B.Err");
            Row("Tex.Mips", t.Kind == "EDDS" ? Loc.F("Tex.MipsEdds", t.Mips, t.Lz4Levels, t.CopyLevels) : t.Mips.ToString(Loc.Culture), "Tip.TexMips", "B.Text");
            Row("Tex.Disk", Loc.F("Tex.Bytes", t.DiskBytes.ToString("N0", Loc.Culture)), "Tip.TexDisk", "B.Text");
            Row("Tex.Gpu", Loc.F(t.GpuGuessed ? "Tex.GpuGuess" : "Tex.Gpu.Value", Mb(t.GpuBytes)), "Tip.TexGpu");
        }
        if (effect is { } e && s.Doc != null)
            Row("Tex.GpuEffect", Loc.F("Tex.GpuEffectValue", Mb(e.Bytes), e.Count), "Tip.TexGpuEffect");

        Head("Tex.Used");
        if (s.Material is { } m)
        {
            string blend = m.Emat.Text("BlendMode") ?? "AlphaBlend";
            Row("Tex.Material", Loc.F("Tex.MaterialValue", Path.GetFileName(m.DiskPath), m.Emat.ClassName, blend, Loc.Num(m.Emat.Number("Softness") ?? 0)), "Tip.TexMaterial", "B.Text");
        }
        if (s.TexturePath is { } path)
        {
            int emitters = 0, parts = 0;
            foreach (var d in docs)
            {
                int here = d.Ptc.Emitters.Count(em => em.Text("Material") is { } mat && d.Materials.TryGetValue(mat, out var md)
                    && md.Emat.Block.FindLine("AlbedoMap")?.Values.FirstOrDefault()?.Value is { } map && string.Equals(resolve(map), path, StringComparison.OrdinalIgnoreCase));
                emitters += here;
                parts += here > 0 ? 1 : 0;
            }
            Row("Tex.Users", Loc.F("Tex.UsersValue", emitters, parts), "Tip.TexUsers", "B.Text");
        }
        if (s.Emitter != null)
            Row("Tex.Emitter", s.Emitter.Name, "Tip.TexEmitter", "B.Text");

        if (s.Params is { } p && s.Summary is { } sum)
        {
            Head("Tex.Density");
            double peak = Peak(p.Size) * Peak(p.SizeMast);
            double sprite = (p.SizeMultiplier + Math.Max(0, p.SizeRnd)) * Math.Max(s.ScaleX, s.ScaleY) * peak;
            double frame = Math.Max((double)sum.Width / s.TileU, (double)sum.Height / s.TileV);
            Row("Tex.Sprite", Loc.F("Tex.SpriteValue", Loc.Num(Math.Round(sprite, 3))), "Tip.TexSprite");
            if (sprite > 0)
            {
                double ppm = frame / sprite;
                double want = Math.Min(TextureRules.DefaultMaxSide, PixelsPerMetre * sprite);
                Row("Tex.FrameDensity", Loc.F("Tex.FrameDensityValue", Loc.Num(Math.Round(frame, 1)), Loc.Num(Math.Round(ppm))),
                    "Tip.TexFrameDensity", ppm < PixelsPerMetre / 2 ? "B.Warn" : "B.TextHi");
                Row("Tex.FrameWanted", Loc.F("Tex.FrameWantedValue", Loc.Num(Math.Round(want))), "Tip.TexFrameWanted", "B.Text");
            }
        }

        Head("Tex.Check");
        if (s.Summary is { } c)
        {
            int before = rows.Count;
            if (c.Compressed && !c.PowerOfTwo)
                Row("Tex.LintNpot", Loc.T("Tex.LintNpotFix"), "Tip.TexLint", "B.Err");
            if (c.Kind == "DDS" && Path.GetExtension(s.TexturePath ?? "").Equals(".edds", StringComparison.OrdinalIgnoreCase))
                Row("Tex.LintEnf1", Loc.T("Tex.LintEnf1Fix"), "Tip.TexLint", "B.Err");
            if (Math.Max(c.Width, c.Height) > TextureRules.DefaultMaxSide)
                Row("Tex.LintBig", Loc.F("Tex.LintBigFix", TextureRules.DefaultMaxSide), "Tip.TexLint", "B.Warn");
            if (c.Width % s.TileU != 0 || c.Height % s.TileV != 0)
                Row("Tex.LintFraction", Loc.T("Tex.LintFractionFix"), "Tip.TexLint", "B.Warn");
            if (!c.Compressed && c.Format == "RGBA8" && c.PowerOfTwo)
                Row("Tex.LintRgba", Loc.F("Tex.LintRgbaFix", Mb(c.GpuBytes), Mb(TextureMemory.Edds(EddsFormat.Dxt5, c.Width, c.Height))), "Tip.TexLint", "B.Info");
            if (rows.Count == before)
                Row("Tex.LintOk", Loc.T("Tex.LintOkFix"), "Tip.TexLint", "B.Ok");
        }
        return rows;
    }

    // The largest value a graph reaches, sampled as the engine bakes it (256 texels, P4).
    private static double Peak(Pfx.Sim.Graph g)
    {
        double best = 0;
        for (int i = 0; i < 256; i++)
            best = Math.Max(best, g.Evaluate((i + 0.5f) / 256));
        return best;
    }
}
