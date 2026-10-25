using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PolyLab.PBOGPacker.Core;

// Minimal PBO table access: list what AddonBuilder actually packed, and append files it could not
// (ODOL models crash binarize). Layout: header properties behind an empty "Vers" entry, entry table
// (name + 5 u32), an empty terminator, all data in table order, then 0x00 and SHA1 of everything before it.
public static class Pbo
{
    private const uint Vers = 0x56657273;

    private sealed record Entry(string Name, uint Mime, uint Orig, uint Reserved, uint Time, uint Size);

    private sealed class Header
    {
        public List<(string Key, string Value)> Props { get; } = new();
        public List<Entry> Entries { get; } = new();
        public long DataStart { get; set; }
    }

    public static IReadOnlyList<string> Names(string pbo)
    {
        using var fs = File.OpenRead(pbo);
        return Read(fs).Entries.Select(e => e.Name).ToList();
    }

    // Rewrites the PBO with extra files stored as is; existing entries and header properties are kept
    // byte for byte, the checksum is recomputed (without the 0x00 sentinel, as DSSignFile expects).
    public static void AddFiles(string pbo, IReadOnlyList<(string Name, string File)> extra)
    {
        var tmp = pbo + ".pbogadd";
        using (var src = File.OpenRead(pbo))
        {
            var h = Read(src);
            using var dst = File.Create(tmp);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            var w = new HashingWriter(dst, sha);

            w.Name("");
            w.U32(Vers); w.U32(0); w.U32(0); w.U32(0); w.U32(0);
            foreach (var (k, v) in h.Props) { w.Name(k); w.Name(v); }
            w.Name("");
            foreach (var e in h.Entries) w.Entry(e);
            var sizes = new List<long>();
            foreach (var (name, file) in extra)
            {
                var info = new FileInfo(file);
                sizes.Add(info.Length);
                w.Entry(new Entry(name, 0, 0, 0, (uint)new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds(), (uint)info.Length));
            }
            w.Entry(new Entry("", 0, 0, 0, 0, 0));

            src.Position = h.DataStart;
            long left = h.Entries.Sum(e => (long)e.Size);
            w.Copy(src, left);
            foreach (var (_, file) in extra)
            {
                using var f = File.OpenRead(file);
                w.Copy(f, f.Length);
            }
            dst.WriteByte(0);
            dst.Write(sha.GetHashAndReset());
        }
        File.Move(tmp, pbo, true);
    }

    private static Header Read(Stream s)
    {
        var r = new BinaryReader(s, Encoding.UTF8, leaveOpen: true);
        var h = new Header();
        var first = true;
        while (true)
        {
            var name = ReadName(r);
            var e = new Entry(name, r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32());
            if (name.Length == 0 && first && e.Mime == Vers)
            {
                while (true)
                {
                    var k = ReadName(r);
                    if (k.Length == 0) break;
                    h.Props.Add((k, ReadName(r)));
                }
                first = false;
                continue;
            }
            first = false;
            if (name.Length == 0) break;
            h.Entries.Add(e);
        }
        h.DataStart = s.Position;
        return h;
    }

    private static string ReadName(BinaryReader r)
    {
        var bytes = new List<byte>();
        byte b;
        while ((b = r.ReadByte()) != 0) bytes.Add(b);
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private sealed class HashingWriter
    {
        private readonly Stream _s;
        private readonly IncrementalHash _h;
        private readonly byte[] _buf = new byte[1 << 16];

        public HashingWriter(Stream s, IncrementalHash h) { _s = s; _h = h; }

        private void Put(ReadOnlySpan<byte> b) { _s.Write(b); _h.AppendData(b); }
        public void Name(string n) { Put(Encoding.UTF8.GetBytes(n)); Put(new byte[] { 0 }); }
        public void U32(uint v) => Put(BitConverter.GetBytes(v));

        public void Entry(Entry e)
        {
            Name(e.Name);
            U32(e.Mime); U32(e.Orig); U32(e.Reserved); U32(e.Time); U32(e.Size);
        }

        public void Copy(Stream from, long count)
        {
            while (count > 0)
            {
                var n = from.Read(_buf, 0, (int)Math.Min(_buf.Length, count));
                if (n <= 0) throw new EndOfStreamException("PBO обрезан");
                Put(_buf.AsSpan(0, n));
                count -= n;
            }
        }
    }
}
