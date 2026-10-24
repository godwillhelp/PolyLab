using System.Diagnostics;
using K4os.Compression.LZ4;
using Pfx.Textures;

namespace PfxTex;

// Texture harness for P2: inspect, decode, build EDDS and scan a corpus. texconv comes from --texconv or PFX_TEXCONV.
public static class Program
{
    private const string Usage = """
        pfxtex info <file>
        pfxtex decode <file> <out.png|out.tga> [level]
        pfxtex build <image> <out.edds> DXT1|DXT5|RGBA8 [--tiles UxV] [--max N] [--bleed] [--pack-alpha] [--texconv exe]
        pfxtex atlas <frame path with %02d> <count> <out.png> [--max N]
        pfxtex scan [--lz4 L09_HC|all] <folder>...
        """;

    public static int Main(string[] args)
    {
        try
        {
            switch (args.FirstOrDefault())
            {
                case "info" when args.Length == 2:
                    Info(args[1]);
                    return 0;
                case "decode" when args.Length is 3 or 4:
                    ImageFiles.Save(args[2], DecodeLevel(args[1], args.Length == 4 ? int.Parse(args[3]) : 0));
                    return 0;
                case "build" when args.Length >= 4:
                    Build(args);
                    return 0;
                case "atlas" when args.Length is 4 or 6:
                    Atlas(args);
                    return 0;
                case "scan" when args.Length >= 2:
                    return Scan(args.Skip(1).ToList());
                default:
                    Console.Error.WriteLine(Usage);
                    return 2;
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            Console.Error.WriteLine($"error: {e.Message}");
            return 1;
        }
    }

    private static RgbaImage DecodeLevel(string path, int level)
    {
        var data = File.ReadAllBytes(path);
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".paa" => PaaFile.Read(data).Decode(level),
            ".dds" or ".edds" when EddsFile.IsEdds(data) => EddsFile.Read(data).Decode(level),
            ".dds" or ".edds" => DdsFile.Read(data).Decode(level),
            _ when level == 0 => ImageFiles.Load(path),
            _ => throw new ArgumentException("Levels exist only in DDS, EDDS and PAA"),
        };
    }

    private static void Info(string path)
    {
        var data = File.ReadAllBytes(path);
        if (path.EndsWith(".paa", StringComparison.OrdinalIgnoreCase))
        {
            var paa = PaaFile.Read(data);
            Console.WriteLine($"PAA {paa.Format}, tags {string.Join(",", paa.Tags)}, {paa.Levels.Count} levels, {paa.TailBytes} bytes after the end marker");
            foreach (var l in paa.Levels)
                Console.WriteLine($"  {l.Width}x{l.Height} {(l.Lzo ? "LZO" : "raw")} {l.Data.Length}");
            return;
        }
        if (!EddsFile.IsEdds(data))
        {
            var dds = DdsFile.Read(data);
            Console.WriteLine($"DDS {dds.Header.Width}x{dds.Header.Height} {dds.Header.FormatName}, {dds.Levels.Count} levels");
            return;
        }
        var edds = EddsFile.Read(data);
        var h = edds.Header;
        Console.WriteLine($"EDDS {h.Width}x{h.Height} {h.FormatName}, {edds.Levels.Count} levels, flags 0x{h.Flags:X}, pitch {h.Pitch}, caps 0x{h.Caps:X}");
        for (int i = 0; i < edds.Levels.Count; i++)
            Console.WriteLine($"  {h.LevelWidth(i)}x{h.LevelHeight(i)} {edds.Levels[i].Tag} {edds.Levels[i].Stored.Length} / {edds.Levels[i].Data.Length}");
    }

    private static void Build(string[] args)
    {
        var format = Enum.Parse<EddsFormat>(args[3], true);
        int tileU = 1, tileV = 1, max = TextureRules.DefaultMaxSide;
        bool bleed = false, packAlpha = false;
        string? texconvPath = Environment.GetEnvironmentVariable("PFX_TEXCONV");
        for (int i = 4; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--tiles":
                    var t = args[++i].Split('x');
                    tileU = int.Parse(t[0]);
                    tileV = int.Parse(t[1]);
                    break;
                case "--max":
                    max = int.Parse(args[++i]);
                    break;
                case "--bleed":
                    bleed = true;
                    break;
                case "--pack-alpha":
                    packAlpha = true;
                    break;
                case "--texconv":
                    texconvPath = args[++i];
                    break;
                default:
                    throw new ArgumentException($"Unknown option {args[i]}");
            }
        }
        var image = ImageFiles.Load(args[1]);
        if (bleed)
            image = TextureTools.Bleed(image);
        if (packAlpha)
            image = TextureTools.PackIntensity(image);
        var texconv = format == EddsFormat.Rgba8 ? null : new Texconv(texconvPath ?? throw new ArgumentException("DXT needs --texconv or PFX_TEXCONV"));
        var watch = Stopwatch.StartNew();
        var edds = EddsFile.Build(image, format, tileU, tileV, max, texconv);
        var bytes = edds.ToBytes();
        File.WriteAllBytes(args[2], bytes);
        Console.WriteLine($"{args[2]}: {image.Width}x{image.Height} {format}, tiles {tileU}x{tileV}, {edds.Levels.Count} levels " +
                          $"({string.Join(" ", edds.Levels.Select(l => l.Tag.Trim()))}), {bytes.Length} bytes, {watch.ElapsedMilliseconds} ms");
    }

    private static void Atlas(string[] args)
    {
        int count = int.Parse(args[2]);
        int max = args.Length == 6 && args[4] == "--max" ? int.Parse(args[5]) : TextureRules.DefaultMaxSide;
        var frames = Enumerable.Range(0, count).Select(i => ImageFiles.Load(args[1].Replace("%02d", i.ToString("D2")))).ToList();
        var atlas = AtlasPacker.Pack(frames, max);
        ImageFiles.Save(args[3], atlas.Image);
        Console.WriteLine($"{args[3]}: {atlas.Image.Width}x{atlas.Image.Height}, TileU {atlas.TileU}, TileV {atlas.TileV}");
    }

    private static readonly string[] Extensions = { ".edds", ".dds", ".paa" };

    private static int Scan(List<string> args)
    {
        var levels = new List<LZ4Level>();
        if (args[0] == "--lz4")
        {
            levels = args[1] == "all"
                ? new List<LZ4Level> { LZ4Level.L00_FAST, LZ4Level.L03_HC, LZ4Level.L06_HC, LZ4Level.L09_HC, LZ4Level.L10_OPT, LZ4Level.L12_MAX }
                : new List<LZ4Level> { Enum.Parse<LZ4Level>(args[1]) };
            args = args.Skip(2).ToList();
        }
        var counts = new SortedDictionary<string, int>();
        var errors = new List<string>();
        int notIdentical = 0, eddsCount = 0;
        long decodedPixels = 0, chunkCount = 0;
        var tagSame = new int[levels.Count];
        var bytesSame = new int[levels.Count];
        var mineBytes = new long[levels.Count];
        long vanillaLz4Bytes = 0;
        var choiceDiffs = new List<string>[levels.Count];
        for (int i = 0; i < levels.Count; i++)
            choiceDiffs[i] = new List<string>();
        var decodeTime = new Stopwatch();
        foreach (var root in args)
        {
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                         .Where(p => Extensions.Contains(Path.GetExtension(p).ToLowerInvariant())))
            {
                string ext = Path.GetExtension(path).ToLowerInvariant();
                try
                {
                    var data = File.ReadAllBytes(path);
                    decodeTime.Start();
                    string kind;
                    if (ext == ".paa")
                    {
                        var paa = PaaFile.Read(data);
                        for (int i = 0; i < paa.Levels.Count; i++)
                            decodedPixels += paa.Decode(i).Pixels.Length / 4;
                        kind = $"paa {paa.Format}{(paa.Levels.Any(l => l.Lzo) ? " lzo" : "")}";
                    }
                    else if (EddsFile.IsEdds(data))
                    {
                        var edds = EddsFile.Read(data);
                        for (int i = 0; i < edds.Levels.Count; i++)
                            decodedPixels += edds.Decode(i).Pixels.Length / 4;
                        decodeTime.Stop();
                        eddsCount++;
                        kind = $"{ext[1..]} {edds.Header.FormatName}";
                        if (!edds.ToBytes().AsSpan().SequenceEqual(data))
                        {
                            notIdentical++;
                            errors.Add("write differs: " + path);
                        }
                        foreach (var level in edds.Levels)
                        {
                            chunkCount++;
                            if (level.Tag == EddsLevel.Lz4)
                                vanillaLz4Bytes += level.Stored.Length;
                            for (int k = 0; k < levels.Count; k++)
                            {
                                var mine = Lz4Chunk.Encode(level.Data, levels[k]);
                                string tag = mine == null ? EddsLevel.Copy : EddsLevel.Lz4;
                                if (mine != null && !Lz4Chunk.Decode(mine).AsSpan().SequenceEqual(level.Data))
                                    errors.Add($"own LZ4 chunk does not decode back: {path}");
                                if (tag == level.Tag)
                                    tagSame[k]++;
                                else
                                    choiceDiffs[k].Add($"{path} {level.Data.Length} B: vanilla {level.Tag.Trim()} {level.Stored.Length}, ours {tag.Trim()} {(mine?.Length ?? level.Data.Length)}");
                                if ((mine ?? level.Data).AsSpan().SequenceEqual(level.Stored))
                                    bytesSame[k]++;
                                if (level.Tag == EddsLevel.Lz4)
                                    mineBytes[k] += mine?.Length ?? level.Data.Length;
                            }
                        }
                    }
                    else
                    {
                        var dds = DdsFile.Read(data);
                        for (int i = 0; i < dds.Levels.Count; i++)
                            decodedPixels += dds.Decode(i).Pixels.Length / 4;
                        kind = $"{ext[1..]} (plain) {dds.Header.FormatName}";
                    }
                    decodeTime.Stop();
                    counts[kind] = counts.GetValueOrDefault(kind) + 1;
                }
                catch (Exception e) when (e is InvalidDataException or NotSupportedException or ArgumentException)
                {
                    decodeTime.Stop();
                    errors.Add($"{path}: {e.Message}");
                }
            }
        }
        foreach (var (kind, n) in counts)
            Console.WriteLine($"{n,5} {kind}");
        Console.WriteLine($"decoded {counts.Values.Sum()} files, {decodedPixels / 1e6:F1} Mpx (all levels) in {decodeTime.ElapsedMilliseconds} ms " +
                          "(reading + decompressing + decoding, file reads excluded)");
        Console.WriteLine($"EDDS written back byte-identical: {eddsCount - notIdentical} of {eddsCount}");
        for (int k = 0; k < levels.Count; k++)
        {
            Console.WriteLine($"{levels[k]}: COPY/LZ4 choice same as vanilla on {tagSame[k]} of {chunkCount} chunks, chunk bytes identical on {bytesSame[k]}, " +
                              $"vanilla LZ4 chunks {vanillaLz4Bytes} B vs ours {mineBytes[k]} B ({100.0 * mineBytes[k] / Math.Max(1, vanillaLz4Bytes):F1} %)");
            foreach (var d in choiceDiffs[k].Take(10))
                Console.WriteLine("  " + d);
        }
        foreach (var e in errors.Take(40))
            Console.WriteLine(e);
        Console.WriteLine($"{errors.Count} errors");
        return errors.Count == 0 ? 0 : 1;
    }
}
