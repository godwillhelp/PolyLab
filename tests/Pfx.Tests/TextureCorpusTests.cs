using Pfx.Textures;
using Xunit.Abstractions;

namespace Pfx.Tests;

public class TextureCorpusTests
{
    private readonly ITestOutputHelper _out;

    public TextureCorpusTests(ITestOutputHelper output)
    {
        _out = output;
    }

    private static readonly string[] Extensions = { ".edds", ".dds", ".paa" };

    // Roots may nest (P:\graphics and P:\graphics\particles), so files are counted once.
    private static List<string> Files() =>
        Environment.GetEnvironmentVariable("PFX_CORPUS")!.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(r => Directory.EnumerateFiles(r, "*", SearchOption.AllDirectories))
            .Where(p => Extensions.Contains(Path.GetExtension(p).ToLowerInvariant()))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    [CorpusFact]
    public void Every_texture_decodes_and_every_edds_writes_back_byte_identical()
    {
        var counts = new SortedDictionary<string, int>();
        var failures = new List<string>();
        foreach (var path in Files())
        {
            try
            {
                var data = File.ReadAllBytes(path);
                string kind;
                if (path.EndsWith(".paa", StringComparison.OrdinalIgnoreCase))
                {
                    var paa = PaaFile.Read(data);
                    for (int i = 0; i < paa.Levels.Count; i++)
                        paa.Decode(i);
                    kind = "paa " + paa.Format;
                }
                else if (EddsFile.IsEdds(data))
                {
                    var edds = EddsFile.Read(data);
                    for (int i = 0; i < edds.Levels.Count; i++)
                        edds.Decode(i);
                    if (!edds.ToBytes().AsSpan().SequenceEqual(data))
                        failures.Add("write differs: " + path);
                    kind = "edds " + edds.Header.FormatName;
                }
                else
                {
                    var dds = DdsFile.Read(data);
                    for (int i = 0; i < dds.Levels.Count; i++)
                        dds.Decode(i);
                    kind = "dds " + dds.Header.FormatName;
                }
                counts[kind] = counts.GetValueOrDefault(kind) + 1;
            }
            catch (Exception e) when (e is InvalidDataException or NotSupportedException)
            {
                failures.Add($"{path}: {e.Message}");
            }
        }
        foreach (var (kind, n) in counts)
            _out.WriteLine($"{n,5} {kind}");
        foreach (var f in failures.Take(20))
            _out.WriteLine(f);
        Assert.Empty(failures);
    }

    [CorpusFact]
    public void Writer_headers_equal_every_mip_chain_header_of_the_same_format()
    {
        int same = 0, single = 0, stalePitch = 0;
        var differ = new List<string>();
        foreach (var path in Files().Where(p => p.EndsWith(".edds", StringComparison.OrdinalIgnoreCase)))
        {
            var edds = EddsFile.Read(File.ReadAllBytes(path));
            var h = edds.Header;
            EddsFormat? format = h.Block switch
            {
                BlockFormat.Dxt1 => EddsFormat.Dxt1,
                BlockFormat.Dxt5 => EddsFormat.Dxt5,
                BlockFormat.None when h.BitCount == 32 && h.AMask == 0xFF000000 => EddsFormat.Rgba8,
                _ => null,
            };
            if (format == null)
                continue;
            if (h.Caps != 0x401008)
            {
                single++;
                continue;
            }
            var ours = DdsHeader.ForEdds(format.Value, h.Width, h.Height, h.MipCount, edds.Levels[0].Data.Length);
            if (ours.Bytes.AsSpan().SequenceEqual(h.Bytes))
                same++;
            else if (StalePitchAfterMipCut(ours, h))
                stalePitch++;
            else
                differ.Add($"{path}: caps 0x{h.Caps:X}, flags 0x{h.Flags:X}, mips field {BitConverter.ToUInt32(h.Bytes, 28)}");
        }
        _out.WriteLine($"{same} headers equal the writer's; {stalePitch} differ only by a pitch left from a cut top mip; " +
                       $"{single} without the mip-chain caps (0x401008) not compared");
        foreach (var d in differ)
            _out.WriteLine("differs: " + d);
        Assert.True(same > 0);
        Assert.Empty(differ);
    }

    // texcap.py drops top mips of DXT EDDS without rewriting the pitch, so it still names the old top level's size.
    private static bool StalePitchAfterMipCut(DdsHeader ours, DdsHeader theirs)
    {
        var a = (byte[])ours.Bytes.Clone();
        BitConverter.GetBytes(theirs.Pitch).CopyTo(a, 20);
        if (!a.AsSpan().SequenceEqual(theirs.Bytes) || theirs.Block == BlockFormat.None)
            return false;
        for (int k = 1; k <= 4; k++)
            if (theirs.Pitch == BlockCodec.Bytes(theirs.Block, theirs.Width << k, theirs.Height << k))
                return true;
        return false;
    }
}
