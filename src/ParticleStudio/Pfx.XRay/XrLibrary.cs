using System.Buffers.Binary;
using System.Globalization;
using Pfx.Sim.XRay;

namespace Pfx.XRay;

// The binary particle library particles.xr (engine PSLibrary.cpp; memory xray-particles-xr-format), read into the same
// section tables a .pe / .pg text dump gives, as xray.py read_library does: chunks "u32 id, u32 size"; top chunk 1 =
// version (u16 1), 3 = effects, 4 = groups, items are sub-chunks 0, 1, 2... until one is missing; effect chunk 0x24 holds
// the editor actions. Keys are "particles/<name>.pe|.pg", lower case, forward slashes; the first entry of a name wins.
public static class XrLibrary
{
    public const uint ChunkCompressed = 0x80000000;

    private sealed class Cursor
    {
        private readonly byte[] _b;
        public int Pos;
        public readonly int End;

        public Cursor(byte[] b, int pos, int end)
        {
            _b = b;
            Pos = pos;
            End = end;
        }

        private ReadOnlySpan<byte> Take(int n)
        {
            if (Pos + n > End)
                throw new InvalidDataException("read past the end of a chunk");
            var s = _b.AsSpan(Pos, n);
            Pos += n;
            return s;
        }

        public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
        public byte U8() => Take(1)[0];
        public float F32() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));

        // Floats as text: each f32 widened to double and written round-trip (xray.py writes repr of the same double).
        public string Floats(int n) => string.Join(",", Enumerable.Range(0, n).Select(_ => ((double)F32()).ToString("R", CultureInfo.InvariantCulture)));

        public string Text()
        {
            int stop = Array.IndexOf(_b, (byte)0, Pos, End - Pos);
            if (stop < 0)
                throw new InvalidDataException("unterminated string");
            string s = XrText.Cp1251.GetString(_b, Pos, stop - Pos);
            Pos = stop + 1;
            return s;
        }
    }

    private static Dictionary<uint, Cursor> Chunks(byte[] buf, int pos, int end)
    {
        var found = new Dictionary<uint, Cursor>();
        while (pos + 8 <= end)
        {
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos));
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos + 4));
            if ((id & ChunkCompressed) != 0)
                throw new InvalidDataException($"compressed chunk 0x{id:x}");
            if (size < 0 || pos + 8 + size > end)
                throw new InvalidDataException($"chunk 0x{id:x} runs past its parent");
            found.TryAdd(id, new Cursor(buf, pos + 8, pos + 8 + size));
            pos += 8 + size;
        }
        return found;
    }

    // Quick content check for the scanner (the first bytes): chunk 1 of two bytes holding version 1, then the effect
    // list (3) or the group list (4). The other .xr files of a gamedata folder start differently (gamemtl 0x1000,
    // senvironment / shaders chunk 0, lanims chunk 0 then 1).
    public static bool Looks(ReadOnlySpan<byte> head)
    {
        if (head.Length < 14)
            return false;
        uint next = BinaryPrimitives.ReadUInt32LittleEndian(head[10..]);
        return BinaryPrimitives.ReadUInt32LittleEndian(head) == 1 && BinaryPrimitives.ReadUInt32LittleEndian(head[4..]) == 2
            && BinaryPrimitives.ReadUInt16LittleEndian(head[8..]) == 1 && next is 3 or 4;
    }

    public static Dictionary<string, Dictionary<string, Dictionary<string, string>>> Read(byte[] buf)
    {
        var top = Chunks(buf, 0, buf.Length);
        if (!top.TryGetValue(1, out var v) || v.U16() != 1)
            throw new InvalidDataException("library version");
        var out_ = new Dictionary<string, Dictionary<string, Dictionary<string, string>>>();
        foreach (var (cid, ext, effect) in new[] { (3u, ".pe", true), (4u, ".pg", false) })
        {
            if (!top.TryGetValue(cid, out var list))
                continue;
            var items = Chunks(buf, list.Pos, list.End);
            for (uint i = 0; items.TryGetValue(i, out var item); i++)
            {
                var (name, s) = effect ? Effect(buf, item) : Group(buf, item);
                out_.TryAdd("particles/" + name.Replace('\\', '/').ToLowerInvariant() + ext, s);
            }
        }
        return out_;
    }

    private static (string, Dictionary<string, Dictionary<string, string>>) Effect(byte[] buf, Cursor item)
    {
        var ch = Chunks(buf, item.Pos, item.End);
        if (ch[1].U16() != 1)
            throw new InvalidDataException("effect version");
        string name = ch[2].Text();
        uint flags = ch.TryGetValue(5, out var fl) ? fl.U32() : 0;
        var s = new Dictionary<string, Dictionary<string, string>>
        {
            ["_effect"] = new() { ["version"] = "1", ["max_particles"] = ch[3].U32().ToString(CultureInfo.InvariantCulture), ["flags"] = flags.ToString(CultureInfo.InvariantCulture) },
        };
        if ((flags & XrEffect.Sprite) != 0)
            s["sprite"] = new() { ["shader"] = ch[7].Text(), ["texture"] = ch[7].Text() };
        if ((flags & XrEffect.Framed) != 0)
        {
            var f = ch[6];
            s["frame"] = new()
            {
                ["tex_size"] = f.Floats(2), ["reserved"] = f.Floats(2), ["dim_x"] = f.I32().ToString(CultureInfo.InvariantCulture),
                ["frame_count"] = f.I32().ToString(CultureInfo.InvariantCulture), ["speed"] = f.Floats(1),
            };
        }
        if ((flags & XrEffect.TimeLimit) != 0)
            s["timelimit"] = new() { ["value"] = ch[8].Floats(1) };
        if ((flags & XrEffect.Collision) != 0)
        {
            var c = ch[0x21];
            s["collision"] = new() { ["one_minus_friction"] = c.Floats(1), ["collide_resilence"] = c.Floats(1), ["collide_sqr_cutoff"] = c.Floats(1) };
        }
        if ((flags & XrEffect.VelocityScale) != 0)
            s["velocity_scale"] = new() { ["value"] = ch[0x22].Floats(3) };
        if ((flags & XrEffect.AlignToPath) != 0 && ch.TryGetValue(0x25, out var ap))
            s["align_to_path"] = new() { ["default_rotation"] = ap.Floats(3) };
        if (!ch.TryGetValue(0x24, out var a))
            throw new InvalidDataException($"{name}: no editor action data");
        uint count = a.U32();
        s["_effect"]["action_count"] = count.ToString(CultureInfo.InvariantCulture);
        for (int i = 0; i < count; i++)
        {
            string sect = $"action_{i:D4}";
            int tid = (int)a.U32();
            if (!XrSpec.Actions.TryGetValue(tid, out var spec) || a.U32() != 1)
                throw new InvalidDataException($"{name}: action {i} of type {tid} cannot be read");
            var act = new Dictionary<string, string>
            {
                ["action_type"] = tid.ToString(CultureInfo.InvariantCulture), ["version"] = "1", ["action_name"] = a.Text(),
                ["flags"] = a.U32().ToString(CultureInfo.InvariantCulture),
            };
            int Kinds(char k) => spec.Params.Count(p => p.Kind == k);
            for (int j = 0; j < Kinds('f'); j++)
                act[$"flt_{j:D4}"] = a.Floats(1);
            for (int j = 0; j < Kinds('v'); j++)
                act[$"vec_{j:D4}"] = a.Floats(3);
            for (int j = 0; j < Kinds('d'); j++)
                s[$"domain_{sect}_{j:D4}"] = new() { ["type"] = a.U32().ToString(CultureInfo.InvariantCulture), ["v0"] = a.Floats(3), ["v1"] = a.Floats(3), ["v2"] = a.Floats(3) };
            for (int j = 0; j < Kinds('b'); j++)
                act[$"bool_{j:D4}"] = a.U8().ToString(CultureInfo.InvariantCulture);
            for (int j = 0; j < Kinds('i'); j++)
                act[$"int_{j:D4}"] = a.I32().ToString(CultureInfo.InvariantCulture);
            s[sect] = act;
        }
        return (name, s);
    }

    private static (string, Dictionary<string, Dictionary<string, string>>) Group(byte[] buf, Cursor item)
    {
        var ch = Chunks(buf, item.Pos, item.End);
        if (ch[1].U16() != 3)
            throw new InvalidDataException("group version");
        string name = ch[2].Text();
        double limit = ch.TryGetValue(5, out var l) ? l.F32() : 0.0;
        var g = new Dictionary<string, string> { ["version"] = "3", ["flags"] = (ch.TryGetValue(3, out var f) ? f.U32() : 0).ToString(CultureInfo.InvariantCulture) };
        var s = new Dictionary<string, Dictionary<string, string>> { ["_group"] = g };
        var ends = new List<double>();
        uint count = ch.TryGetValue(4, out var e) ? e.U32() : 0;
        for (int i = 0; i < count; i++)
        {
            var names = Enumerable.Range(0, 4).Select(_ => e!.Text()).ToList();
            double t0 = e!.F32(), t1 = e.F32();
            ends.Add(t1);
            s[$"effect_{i:D4}"] = new()
            {
                ["effect_name"] = names[0], ["on_play_child"] = names[1], ["on_birth_child"] = names[2], ["on_death_child"] = names[3],
                ["time0"] = t0.ToString("R", CultureInfo.InvariantCulture), ["time1"] = t1.ToString("R", CultureInfo.InvariantCulture),
                ["flags"] = e.U32().ToString(CultureInfo.InvariantCulture),
            };
        }
        if (limit <= 0)
            limit = ends.Append(limit).Max();
        g["effects_count"] = count.ToString(CultureInfo.InvariantCulture);
        g["timelimit"] = limit.ToString("R", CultureInfo.InvariantCulture);
        return (name, s);
    }
}
