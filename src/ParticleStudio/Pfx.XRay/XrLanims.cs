using System.Buffers.Binary;

namespace Pfx.XRay;

// One light animation (engine LightAnimLibrary.cpp CLAItem): frames at Fps, colour keys by frame as u32 ARGB.
public sealed record XrLightAnim(string Name, double Fps, int Frames, IReadOnlyList<(int Frame, uint Argb)> Keys)
{
    public double Seconds => Fps > 0 ? Frames / Fps : 0;

    // CLAItem::CalculateRGB: frame = floor(fmod(t, frames / fps) * fps); a key frame gives its colour, between keys
    // the colours are lerped, after the last key the last colour holds.
    public (double R, double G, double B, double A) ColourAt(double t)
    {
        if (Keys.Count == 0 || Fps <= 0)
            return (0, 0, 0, 0);
        int frame = (int)Math.Floor(t % (Frames / Fps) * Fps);
        int i = 0;
        while (i < Keys.Count && Keys[i].Frame < frame)
            i++;
        if (i < Keys.Count && Keys[i].Frame == frame)
            return Split(Keys[i].Argb);
        if (i == Keys.Count)
            return Split(Keys[^1].Argb);
        if (i == 0)
            return Split(Keys[0].Argb);
        var (a, b) = (Keys[i - 1], Keys[i]);
        double k = (frame - a.Frame) / (double)(b.Frame - a.Frame);
        var (ar, ag, ab, aa) = Split(a.Argb);
        var (br, bg, bb, ba) = Split(b.Argb);
        return (ar + (br - ar) * k, ag + (bg - ag) * k, ab + (bb - ab) * k, aa + (ba - aa) * k);
    }

    // Time mean over integer frames (what a steady light uses, as CG_Anomaly took koster_01_electra).
    public (double R, double G, double B) Mean()
    {
        if (Frames <= 0 || Fps <= 0)
            return (0, 0, 0);
        double r = 0, g = 0, b = 0;
        for (int f = 0; f < Frames; f++)
        {
            var c = ColourAt(f / Fps);
            r += c.R;
            g += c.G;
            b += c.B;
        }
        return (r / Frames, g / Frames, b / Frames);
    }

    private static (double, double, double, double) Split(uint c) =>
        (((c >> 16) & 0xff) / 255.0, ((c >> 8) & 0xff) / 255.0, (c & 0xff) / 255.0, ((c >> 24) & 0xff) / 255.0);
}

// lanims.xr: chunk 0 = version (u16), chunk 1 = the list, items are sub-chunks 0, 1, 2...; each item has chunk 1 (name,
// f32 fps, u32 frames) and chunk 2 (u32 key count, then u32 frame + u32 colour). Version 0 files store colours
// blue-first; the engine swaps them on load (bgr2rgb), so does this reader.
public static class XrLanims
{
    public static bool Looks(ReadOnlySpan<byte> head) =>
        head.Length >= 14 && BinaryPrimitives.ReadUInt32LittleEndian(head) == 0 && BinaryPrimitives.ReadUInt32LittleEndian(head[4..]) == 2
        && BinaryPrimitives.ReadUInt32LittleEndian(head[10..]) == 1;

    public static List<XrLightAnim> Read(byte[] buf)
    {
        var top = Chunks(buf, 0, buf.Length);
        int version = top.TryGetValue(0, out var v) && v.Size >= 2 ? BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(v.At)) : 0;
        var list = new List<XrLightAnim>();
        if (!top.TryGetValue(1, out var items))
            return list;
        var all = Chunks(buf, items.At, items.At + items.Size);
        for (uint i = 0; all.TryGetValue(i, out var item); i++)
        {
            var parts = Chunks(buf, item.At, item.At + item.Size);
            if (!parts.TryGetValue(1, out var common) || !parts.TryGetValue(2, out var keys))
                throw new InvalidDataException($"light animation {i} lacks its chunks");
            int p = common.At;
            int zero = Array.IndexOf(buf, (byte)0, p, common.Size);
            if (zero < 0)
                throw new InvalidDataException($"light animation {i}: unterminated name");
            string name = XrText.Cp1251.GetString(buf, p, zero - p);
            p = zero + 1;
            float fps = BinaryPrimitives.ReadSingleLittleEndian(buf.AsSpan(p));
            int frames = (int)BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(p + 4));
            int k = keys.At;
            int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(k));
            if (count < 0 || 4 + count * 8L > keys.Size)
                throw new InvalidDataException($"light animation {name}: {count} keys do not fit");
            var map = new SortedDictionary<int, uint>();
            for (int j = 0; j < count; j++)
            {
                int frame = (int)BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(k + 4 + j * 8));
                uint c = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(k + 8 + j * 8));
                if (version == 0)
                    c = (c & 0xff000000) | ((c & 0xff) << 16) | (c & 0xff00) | ((c >> 16) & 0xff);
                map[frame] = c;
            }
            list.Add(new XrLightAnim(name, fps, frames, map.Select(e => (e.Key, e.Value)).ToList()));
        }
        return list;
    }

    private static Dictionary<uint, (int At, int Size)> Chunks(byte[] buf, int pos, int end)
    {
        var found = new Dictionary<uint, (int, int)>();
        while (pos + 8 <= end)
        {
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos));
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos + 4));
            if (size < 0 || pos + 8 + size > end)
                throw new InvalidDataException($"chunk 0x{id:x} runs past its parent");
            found.TryAdd(id, (pos + 8, size));
            pos += 8 + size;
        }
        return found;
    }
}

// X-Ray sound parameters: the first user comment of the Vorbis comment packet holds "u32 version, f32 min distance,
// f32 max distance, f32 base volume (version 2+)" (xray.py ogg_params; memory xray-particle-pe-format).
public sealed record XrSoundParams(int Version, double Min, double Range, double Volume)
{
    public static bool IsOgg(ReadOnlySpan<byte> head) => head.Length >= 4 && head[..4].SequenceEqual("OggS"u8);

    public static XrSoundParams? Read(byte[] data)
    {
        var packets = Packets(data, 2);
        if (packets.Count < 2 || packets[1].Length < 11 || !packets[1].AsSpan(0, 7).SequenceEqual("\x03vorbis"u8))
            return null;
        var p = packets[1];
        long q = 11 + (long)BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(7));
        if (q + 8 > p.Length || BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan((int)q)) == 0)
            return null;
        long size = BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan((int)q + 4));
        if (size < 16 || q + 8 + size > p.Length)
            return null;
        var c = p.AsSpan((int)q + 8, (int)size);
        int version = (int)BinaryPrimitives.ReadUInt32LittleEndian(c);
        if (version is < 1 or > 3)
            return null;
        double lo = BinaryPrimitives.ReadSingleLittleEndian(c[4..]), hi = BinaryPrimitives.ReadSingleLittleEndian(c[8..]);
        double volume = version >= 2 ? BinaryPrimitives.ReadSingleLittleEndian(c[12..]) : 1.0;
        return new XrSoundParams(version, Math.Round(lo, 3), Math.Round(hi, 3), Math.Round(volume, 3));
    }

    // Ogg pages: segments of 255 continue a packet, a shorter one ends it.
    private static List<byte[]> Packets(byte[] data, int count)
    {
        var out_ = new List<byte[]>();
        var cur = new List<byte>();
        int pos = 0;
        while (pos + 27 <= data.Length && out_.Count < count)
        {
            if (!data.AsSpan(pos, 4).SequenceEqual("OggS"u8))
                return out_;
            int segs = data[pos + 26];
            if (pos + 27 + segs > data.Length)
                return out_;
            int body = pos + 27 + segs;
            for (int i = 0; i < segs; i++)
            {
                int size = data[pos + 27 + i];
                if (body + size > data.Length)
                    return out_;
                cur.AddRange(data.AsSpan(body, size).ToArray());
                body += size;
                if (size < 255)
                {
                    out_.Add(cur.ToArray());
                    cur.Clear();
                    if (out_.Count == count)
                        break;
                }
            }
            pos += 27 + segs + data.AsSpan(pos + 27, segs).ToArray().Sum(b => b);
        }
        return out_;
    }
}
