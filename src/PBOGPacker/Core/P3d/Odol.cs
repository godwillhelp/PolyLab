using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Text;
using Pfx.Textures;

namespace PolyLab.PBOGPacker.Core.P3d;

public sealed record OdolProxy(string Name, float[] Transform, int SequenceId, int SelectionIndex, int BoneIndex, int SectionIndex);

public sealed record OdolSection(int FaceLower, int FaceUpper, int TextureIndex, uint Special, int MaterialIndex, string Surface);

public sealed record OdolSelection(string Name, int[] Faces, bool Sectional, int[] Vertices, byte[] Weights);

public sealed record OdolStage(int Filter, string Texture, int TexGen, bool WorldEnvMap);

public sealed record OdolTexGen(int UvSource, float[] Transform);

public sealed record OdolProjection(int Layer, int LayerNormal, float StartAngle, float EndAngle);

public sealed record OdolMaterial(string Path, Vector4[] Colors, float SpecularPower, uint PixelShader, uint VertexShader,
    uint MainLight, uint FogMode, string Surface, uint RenderFlags, OdolStage[] Stages, OdolTexGen[] TexGens, OdolStage Ti,
    Vector4 PlantWind, float ParallaxScale, float ShadowBias, OdolProjection TopProjection);

// Per LOD of the model: the skeleton bone the animation moves (-1: none) and, except for direct and hide, its axis.
public sealed record OdolAnimation(int Type, string Name, string Source, float MinValue, float MaxValue, float MinPhase,
    float MaxPhase, int Address, float[] Values, int[] Bone, (Vector3 Pos, Vector3 Dir)?[] Axis);

public sealed class OdolLod
{
    public float Resolution { get; init; }
    public OdolProxy[] Proxies { get; init; } = [];
    public int[] SubSkeleton { get; init; } = [];
    public string[] Textures { get; init; } = [];
    public OdolMaterial[] Materials { get; init; } = [];
    public int[][] Faces { get; init; } = [];
    public int[] FaceSection { get; init; } = [];
    public OdolSection[] Sections { get; init; } = [];
    public OdolSelection[] Selections { get; init; } = [];
    public (string Name, string Value)[] Properties { get; init; } = [];
    public (float Time, Vector3[] Points)[] Frames { get; init; } = [];
    public uint[] Clip { get; init; } = [];
    public Vector2[][] Uv { get; init; } = [];
    public Vector3[] Positions { get; init; } = [];
    public Vector3[] Normals { get; init; } = [];
    // Per vertex 12 bytes: the number of bones, then four (sub-skeleton bone, weight of 255) pairs.
    public byte[] BoneRefs { get; init; } = [];
}

public sealed class OdolModel
{
    public int Version { get; init; }
    public Vector3 BoundingCenter { get; init; }
    public Vector3 BoundsMin { get; init; }
    public Vector3 BoundsMax { get; init; }
    public Vector3 CenterOfMass { get; init; }
    public float[] InvInertia { get; init; } = [];
    public float[] Thermal { get; init; } = [];
    public uint DayZ56 { get; init; }
    public string Skeleton { get; init; } = "";
    public bool Discrete { get; init; }
    public (string Name, string Parent)[] Bones { get; init; } = [];
    public string Pivots { get; init; } = "";
    public float Mass { get; init; }
    public OdolAnimation[] Animations { get; init; } = [];
    public OdolLod[] Lods { get; init; } = [];
}

// Binarized DayZ model, versions 54-56. Layout learnt byte by byte and checked on all 10,623 models of DayZ 1.30 and the
// v54 / v55 models of donor mods: every LOD must end exactly at the address the header gives, and its rest-size field
// must point there too, so a misread field or a cut file never passes. Arrays are LZO-compressed from 1024 bytes up;
// a "condensed" array has a fill flag (one value for all) even when it is empty. v55 adds a byte before the animations,
// v56 a word after preferShadowVolume (meaning unknown, kept as DayZ56).
public static class Odol
{
    public static bool IsOdol(ReadOnlySpan<byte> head) => head.Length >= 4 && head[..4].SequenceEqual("ODOL"u8);

    public static OdolModel Read(string file) => Read(File.ReadAllBytes(file));

    public static OdolModel Read(byte[] data)
    {
        if (!IsOdol(data)) throw new InvalidDataException(Loc.T("Be.OdolNot"));
        var c = new Cursor(data, Loc.T("Be.OdolHeader"));
        c.Skip(4);
        var version = c.I32();
        if (version is < 54 or > 56) throw new InvalidDataException(Loc.F("Be.OdolVersion", version));
        var lodCount = c.Count(4);
        var res = new float[lodCount];
        for (var i = 0; i < lodCount; i++) res[i] = c.F32();

        c.Skip(4 + 4 + 4 + 4 + 4 + 4 + 12 + 4 + 4 + 4);
        var bmin = c.V3();
        var bmax = c.V3();
        c.Skip(24);
        var center = c.V3();
        c.Skip(12);
        var com = c.V3();
        var invInertia = c.F32s(9);
        c.Skip(4 + 1 + 4 + 4);
        var thermal = c.F32s(5);
        c.Skip(1 + 4 + 1);
        var dayz56 = version >= 56 ? c.U32() : 0;
        c.Skip(4 + 1);
        var skeleton = c.Str();
        var discrete = false;
        var bones = Array.Empty<(string, string)>();
        var pivots = "";
        if (skeleton.Length > 0)
        {
            discrete = c.Bool();
            bones = new (string, string)[c.Count(2)];
            for (var i = 0; i < bones.Length; i++) bones[i] = (c.Str(), c.Str());
            pivots = c.Str();
        }
        c.Skip(1);
        c.Packed(4);
        var mass = c.F32();
        c.Skip(12 + 13 + 4 + 1);
        c.Str();
        c.Str();
        c.Skip(1 + 4);
        if (version >= 55) c.Skip(1);
        var anims = c.Bool() ? Animations(c, bones.Length) : [];

        var starts = new int[lodCount];
        var ends = new int[lodCount];
        for (var i = 0; i < lodCount; i++) starts[i] = c.Address();
        for (var i = 0; i < lodCount; i++) ends[i] = c.Address();
        var need = ends.Concat(starts).DefaultIfEmpty(0).Max();
        if (need > data.Length) throw new InvalidDataException(Loc.F("Be.OdolShort", need, data.Length));

        var lods = new OdolLod[lodCount];
        for (var i = 0; i < lodCount; i++)
        {
            var lc = new Cursor(data, "LOD " + LodName(res[i])) { Pos = starts[i] };
            lods[i] = Lod(lc, res[i], ends[i]);
            if (lc.Pos != ends[i]) throw new InvalidDataException(Loc.F("Be.OdolLodEnd", LodName(res[i]), lc.Pos, ends[i]));
        }
        if (anims.Length > 0 && anims[0].Bone.Length != lodCount)
            throw new InvalidDataException(Loc.F("Be.OdolAnimLods", anims[0].Bone.Length, lodCount));

        return new OdolModel
        {
            Version = version, BoundingCenter = center, BoundsMin = bmin, BoundsMax = bmax, CenterOfMass = com,
            InvInertia = invInertia, Thermal = thermal, DayZ56 = dayz56, Skeleton = skeleton, Discrete = discrete,
            Bones = bones, Pivots = pivots, Mass = mass, Animations = anims, Lods = lods,
        };
    }

    // Types: 0 rotation, 1-3 rotationX/Y/Z, 4 translation, 5-7 translationX/Y/Z (two values each), 8 direct (axis
    // position, axis direction, angle, offset), 9 hide (one value). Address: 0 clamp, 1 loop, 2 mirror.
    private static OdolAnimation[] Animations(Cursor c, int boneCount)
    {
        var n = c.Count(16);
        var head = new (int Type, string Name, string Source, float[] V, int Addr, float[] Values)[n];
        for (var i = 0; i < n; i++)
        {
            var type = c.I32();
            if (type is < 0 or > 9) throw c.Bad(type);
            var name = c.Str();
            var source = c.Str();
            var v = c.F32s(4);
            var addr = c.I32();
            head[i] = (type, name, source, v, addr, c.F32s(type == 9 ? 1 : type == 8 ? 8 : 2));
        }
        var lods = c.Count(4);
        for (var l = 0; l < lods; l++)
        {
            var b = c.Count(4);
            for (var k = 0; k < b; k++) c.Skip(4L * c.Count(4));
        }
        var bone = new int[n][];
        var axis = new (Vector3, Vector3)?[n][];
        for (var i = 0; i < n; i++)
        {
            bone[i] = new int[lods];
            axis[i] = new (Vector3, Vector3)?[lods];
        }
        for (var l = 0; l < lods; l++)
            for (var i = 0; i < n; i++)
            {
                var b = c.I32();
                if (b < -1 || b >= boneCount) throw c.Bad(b);
                bone[i][l] = b;
                if (b != -1 && head[i].Type is not (8 or 9)) axis[i][l] = (c.V3(), c.V3());
            }
        return head.Select((h, i) => new OdolAnimation(h.Type, h.Name, h.Source, h.V[0], h.V[1], h.V[2], h.V[3], h.Addr,
            h.Values, bone[i], axis[i])).ToArray();
    }

    private static OdolLod Lod(Cursor c, float res, int end)
    {
        var proxies = new OdolProxy[c.Count(64)];
        for (var i = 0; i < proxies.Length; i++)
            proxies[i] = new OdolProxy(c.Str(), c.F32s(12), c.I32(), c.I32(), c.I32(), c.I32());
        var sub = c.Ints();
        var skelToSub = c.Count(4);
        for (var i = 0; i < skelToSub; i++) c.Skip(4L * c.Count(4));
        var vertexCount = c.Count(0, 12);
        c.Skip(4 + 4 + 4 + 40);
        var textures = new string[c.Count(1)];
        for (var i = 0; i < textures.Length; i++) textures[i] = c.Str();
        var materials = new OdolMaterial[c.Count(64)];
        for (var i = 0; i < materials.Length; i++) materials[i] = Material(c);
        c.Packed(2);
        c.Packed(2);

        var faces = new int[c.Count(7)][];
        c.Skip(4 + 2);
        for (var i = 0; i < faces.Length; i++)
        {
            var n = c.U8();
            if (n is < 3 or > 4) throw c.Bad(n);
            var f = faces[i] = new int[n];
            for (var k = 0; k < n; k++)
            {
                f[k] = c.U16();
                if (f[k] >= vertexCount) throw c.Bad(f[k]);
            }
        }

        var offsets = new int[faces.Length + 1];
        for (var i = 0; i < faces.Length; i++) offsets[i + 1] = offsets[i] + 2 + 2 * faces[i].Length;
        var faceSection = new int[faces.Length];
        Array.Fill(faceSection, -1);
        var sections = new OdolSection[c.Count(30)];
        for (var i = 0; i < sections.Length; i++)
        {
            var lo = c.I32();
            var hi = c.I32();
            c.Skip(12);
            var tex = (int)c.I16();
            var special = c.U32();
            var mat = c.I32();
            var surf = mat == -1 ? c.Str() : "";
            c.Skip(4L * c.Count(4));
            if (tex < -1 || tex >= textures.Length || mat < -1 || mat >= materials.Length) throw c.Bad(tex);
            // Sections address faces in the engine's layout: 2 bytes per face plus 2 per vertex.
            var first = Array.BinarySearch(offsets, lo);
            var last = Array.BinarySearch(offsets, hi);
            if (first < 0 || last < first) throw new InvalidDataException(Loc.F("Be.OdolSection", LodName(res), i + 1));
            Array.Fill(faceSection, i, first, last - first);
            sections[i] = new OdolSection(lo, hi, tex, special, mat, surf);
        }

        var selections = new OdolSelection[c.Count(30)];
        for (var i = 0; i < selections.Length; i++)
        {
            var name = c.Str();
            var sf = c.Shorts();
            c.Skip(4);
            var sectional = c.Bool();
            c.Packed(4);
            var sv = c.Shorts();
            var w = c.Packed(1);
            if (sf.Any(f => f >= faces.Length) || sv.Any(v => v >= vertexCount) || w.Length != 0 && w.Length != sv.Length)
                throw new InvalidDataException(Loc.F("Be.OdolSelection", LodName(res), name));
            selections[i] = new OdolSelection(name, sf, sectional, sv, w);
        }

        var props = new (string, string)[c.Count(2)];
        for (var i = 0; i < props.Length; i++) props[i] = (c.Str(), c.Str());
        var frames = new (float, Vector3[])[c.Count(8)];
        for (var i = 0; i < frames.Length; i++)
        {
            var t = c.F32();
            var pts = new Vector3[c.Count(12)];
            for (var k = 0; k < pts.Length; k++) pts[k] = c.V3();
            frames[i] = (t, pts);
        }
        c.Skip(12 + 1);
        if (c.Pos + c.I32() != end) throw new InvalidDataException(Loc.F("Be.OdolLodEnd", LodName(res), c.Pos - 4, end));

        var clip = c.Condensed(4, vertexCount, (d, k) => BinaryPrimitives.ReadUInt32LittleEndian(d[(4 * k)..]));
        var uvs = new List<Vector2[]> { Uv(c, vertexCount) };
        var uvSets = c.Count(1);
        for (var i = 1; i < uvSets; i++) uvs.Add(Uv(c, vertexCount));
        var posData = c.Packed(12, vertexCount);
        var pos = new Vector3[vertexCount];
        for (var k = 0; k < vertexCount; k++) pos[k] = V3(posData, 12 * k);
        var normals = c.Condensed(4, vertexCount, (d, k) => Normal(BinaryPrimitives.ReadUInt32LittleEndian(d[(4 * k)..])));
        c.Packed(8, vertexCount, true);
        var boneRefs = c.Packed(12, vertexCount, true);
        c.Packed(32, vertexCount, true);
        foreach (var p in proxies)
            if (p.SelectionIndex < 0 || p.SelectionIndex >= selections.Length || p.SectionIndex >= sections.Length)
                throw new InvalidDataException(Loc.F("Be.OdolSelection", LodName(res), p.Name));

        return new OdolLod
        {
            Resolution = res, Proxies = proxies, SubSkeleton = sub, Textures = textures, Materials = materials, Faces = faces,
            FaceSection = faceSection, Sections = sections, Selections = selections, Properties = props, Frames = frames, Clip = clip, Uv = uvs.ToArray(),
            Positions = pos, Normals = normals, BoneRefs = boneRefs,
        };
    }

    // Material layout version 20 (the only one DayZ writes). Colours in file order: emissive, ambient, diffuse, forced
    // diffuse, specular, treeColorization; then plantWind and a copy of specular. The shader ids sit after a block of
    // 44 bytes (top projection layer, start and end angle, layer normal at 0, 1, 2 and 4); the older slots stay zero.
    private static OdolMaterial Material(Cursor c)
    {
        var path = c.Str();
        var v = c.I32();
        if (v != 20) throw c.Bad(v);
        var colors = new Vector4[6];
        for (var i = 0; i < 6; i++) colors[i] = new Vector4(c.F32(), c.F32(), c.F32(), c.F32());
        var wind = new Vector4(c.F32(), c.F32(), c.F32(), c.F32());
        c.Skip(16);
        var power = c.F32();
        var parallax = c.F32();
        var shadowBias = c.F32();
        c.Skip(16 + 4 + 4);
        var layer = c.I32();
        var start = c.F32();
        var end = c.F32();
        c.Skip(4);
        var layerNormal = c.I32();
        c.Skip(20);
        var ps = c.U32();
        var vs = c.U32();
        var mainLight = c.U32();
        var fog = c.U32();
        var surface = c.Str();
        c.Skip(4);
        var flags = c.U32();
        var stages = new OdolStage[c.Count(9)];
        var gens = new OdolTexGen[c.Count(52)];
        for (var i = 0; i < stages.Length; i++) stages[i] = Stage(c);
        for (var i = 0; i < gens.Length; i++) gens[i] = new OdolTexGen(c.I32(), c.F32s(12));
        return new OdolMaterial(path, colors, power, ps, vs, mainLight, fog, surface, flags, stages, gens, Stage(c), wind, parallax,
            shadowBias, new OdolProjection(layer, layerNormal, start, end));
    }

    private static OdolStage Stage(Cursor c)
    {
        var filter = c.I32();
        var tex = c.Str();
        var gen = c.I32();
        return new OdolStage(filter, tex, gen, c.Bool());
    }

    // Min and max of the set, then per vertex two signed 16-bit values spread over that range.
    private static Vector2[] Uv(Cursor c, int vertexCount)
    {
        var min = new Vector2(c.F32(), c.F32());
        var max = new Vector2(c.F32(), c.F32());
        return c.Condensed(4, vertexCount, (d, k) =>
        {
            var u = BinaryPrimitives.ReadInt16LittleEndian(d[(4 * k)..]);
            var w = BinaryPrimitives.ReadInt16LittleEndian(d[(4 * k + 2)..]);
            return new Vector2(min.X + (max.X - min.X) * ((u + 32767) / 65534f), min.Y + (max.Y - min.Y) * ((w + 32767) / 65534f));
        });
    }

    // Three signed 10-bit fields, stored negated.
    private static Vector3 Normal(uint p)
    {
        static float F(uint v) => (v > 511 ? (int)v - 1024 : (int)v) / -511f;
        return new Vector3(F(p & 0x3FF), F(p >> 10 & 0x3FF), F(p >> 20 & 0x3FF));
    }

    private static Vector3 V3(byte[] d, int at) => new(BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(at)),
        BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(at + 4)), BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(at + 8)));

    // Object Builder's names for the LOD resolutions.
    public static string LodName(float r)
    {
        if (r < 1e4) return r.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        if (r < 2e4) return "ShadowVolume " + (r - 1e4).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        foreach (var (v, name) in SpecialLods)
            if (Math.Abs(r - v) <= v * 1e-4) return name;
        return r.ToString("0.###E+0", System.Globalization.CultureInfo.InvariantCulture);
    }

    public static readonly (double Res, string Name)[] SpecialLods =
    {
        (1e13, "Geometry"), (2e13, "Geometry Buoyancy"), (4e13, "Geometry PhysX"), (1e15, "Memory"), (2e15, "LandContact"),
        (3e15, "Roadway"), (4e15, "Paths"), (5e15, "HitPoints"), (6e15, "View Geometry"), (7e15, "Fire Geometry"),
    };

    // Reads are bounds-checked; a count is plausible only when that many of its smallest elements still fit in the file
    // (LZO1X turns one input byte into at most 255 output bytes), so a misread field fails at once instead of allocating
    // gigabytes.
    private sealed class Cursor(byte[] d, string where)
    {
        public int Pos;

        private ReadOnlySpan<byte> Take(int n)
        {
            if (n < 0 || Pos > d.Length - n) throw new InvalidDataException(Loc.F("Be.OdolCut", where, Pos));
            var s = d.AsSpan(Pos, n);
            Pos += n;
            return s;
        }

        public InvalidDataException Bad(long value) => new(Loc.F("Be.OdolValue", where, value, Pos));

        public void Skip(long n) => Take(checked((int)n));
        public byte U8() => Take(1)[0];
        public bool Bool() => U8() != 0;
        public short I16() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));
        public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        public float F32() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));
        public Vector3 V3() => new(F32(), F32(), F32());

        public float[] F32s(int n)
        {
            var a = new float[n];
            for (var i = 0; i < n; i++) a[i] = F32();
            return a;
        }

        public int Count(int minSize, int packedSize = 0)
        {
            var n = I32();
            if (n < 0 || (long)n * minSize > d.Length - Pos || (long)n * packedSize > 256L * (d.Length - Pos) + 1024) throw Bad(n);
            return n;
        }

        // An array that may be LZO-compressed; per vertex ones hold exactly one element per vertex (or none if optional).
        public byte[] Packed(int size, int vertexCount = -1, bool optional = false)
        {
            var n = Count(0, size);
            if (vertexCount >= 0 && n != vertexCount && !(optional && n == 0)) throw Bad(n);
            return Compressed(n * size);
        }

        public int Address()
        {
            var a = I32();
            if (a < 0) throw Bad(a);
            return a;
        }

        public string Str()
        {
            var end = Array.IndexOf(d, (byte)0, Pos);
            if (end < 0) throw new InvalidDataException(Loc.F("Be.OdolCut", where, Pos));
            var s = Encoding.Latin1.GetString(d, Pos, end - Pos);
            Pos = end + 1;
            return s;
        }

        public byte[] Compressed(int size)
        {
            if (size < 1024) return Take(size).ToArray();
            try
            {
                var o = Lzo1x.Decompress(d.AsSpan(Pos), size, out var used);
                Pos += used;
                return o;
            }
            catch (InvalidDataException) { throw new InvalidDataException(Loc.F("Be.OdolLzo", where, Pos)); }
        }

        public int[] Ints()
        {
            var a = new int[Count(4)];
            for (var i = 0; i < a.Length; i++) a[i] = I32();
            return a;
        }

        public int[] Shorts()
        {
            var b = Packed(2);
            var a = new int[b.Length / 2];
            for (var i = 0; i < a.Length; i++) a[i] = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(2 * i));
            return a;
        }

        public delegate T Element<T>(ReadOnlySpan<byte> data, int index);

        public T[] Condensed<T>(int size, int vertexCount, Element<T> read)
        {
            var n = I32();
            if (n != vertexCount) throw Bad(n);
            var a = new T[n];
            if (Bool())
            {
                var one = read(Take(size), 0);
                Array.Fill(a, one);
                return a;
            }
            var b = Compressed(n * size);
            for (var i = 0; i < n; i++) a[i] = read(b, i);
            return a;
        }
    }
}
