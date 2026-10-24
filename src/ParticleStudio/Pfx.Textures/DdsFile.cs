namespace Pfx.Textures;

// Plain DDS: header, then every mip level uncompressed in the file, largest first.
public sealed class DdsFile
{
    private DdsFile(DdsHeader header, List<byte[]> levels)
    {
        Header = header;
        Levels = levels;
    }

    public DdsHeader Header { get; }
    public IReadOnlyList<byte[]> Levels { get; }

    public static DdsFile Read(byte[] data)
    {
        var header = DdsHeader.Parse(data);
        if (header.IsEnf1)
            throw new InvalidDataException("ENF1 at offset 36: an EDDS, not a plain DDS");
        var levels = new List<byte[]>();
        int at = header.Size;
        for (int i = 0; i < header.MipCount; i++)
        {
            int size = header.LevelBytes(i);
            if (at + size > data.Length)
                throw new InvalidDataException($"DDS level {i} runs past the end of the file");
            levels.Add(data.AsSpan(at, size).ToArray());
            at += size;
        }
        if (at != data.Length)
            throw new InvalidDataException($"DDS has {data.Length - at} bytes after its {header.MipCount} levels");
        return new DdsFile(header, levels);
    }

    public RgbaImage Decode(int level = 0) => Header.Decode(level, Levels[level]);

    public static byte[] WritePlainBgra(RgbaImage image)
    {
        var header = DdsHeader.ForPlainBgra(image.Width, image.Height);
        var output = new byte[header.Size + image.Pixels.Length];
        header.Bytes.CopyTo(output, 0);
        ImageFiles.ToBgra(image).CopyTo(output, header.Size);
        return output;
    }
}
