using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Text;

namespace PolyLab.PBOGPacker.Core.P3d;

// Editable model (MLOD, P3DM 0x1C 0x100) rebuilt from an ODOL, as binarize 1.30 maps one to the other:
// - ODOL vertices are MLOD points split by normal and UV; vertices with the same position, hints, bone weights and
//   vertex-list selections are welded back into one point. Positions are relative to the bounding centre.
// - Faces keep their order; binarize swaps the first two and the last two corners, which is undone here. Texture,
//   material and the shadow / z-bias face flags come from the face's section.
// - Point flags come from the vertex hints: land ×0x100, decal ×0x10, fog 0x1000 / 0x2000 → 0x4000 / 0x8000, user value
//   ×0x10; the light flags are stored as user values 200 (no light), 201 (ambient), 202 (half), 203 (full).
// - A selection keeps its vertex list where binarize kept one; otherwise a bone takes its points from the vertices'
//   bone weights and any other selection the points of its faces. Weights are MLOD bytes 256 − w (255 → 1 = full).
// - The geometry LOD gets per-point masses that give back the stored mass, centre of mass and inertia (Mass); edges
//   whose two faces have different normals at a shared point become sharp edges.
public static class Mlod
{
    // Axis points the model.cfg needs that the ODOL no longer has: in the LOD of that index, or in the memory LOD (-1).
    public sealed record Axis(string Name, int Lod, Vector3[] Points);

    public static byte[] Write(OdolModel m, IReadOnlyList<Axis> axes, List<string> notes)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms, Encoding.Latin1);
        var memory = Array.FindIndex(m.Lods, l => Odol.LodName(l.Resolution) == "Memory");
        var extra = axes.Any(a => a.Lod < 0) && memory < 0;
        w.Write("MLOD"u8);
        w.Write(0x101);
        w.Write(m.Lods.Length + (extra ? 1 : 0));
        for (var i = 0; i < m.Lods.Length; i++)
            Lod(w, m, m.Lods[i], axes.Where(a => a.Lod == i || a.Lod < 0 && i == memory).ToList(), notes);
        if (extra) Lod(w, m, new OdolLod { Resolution = 1e15f }, axes.Where(a => a.Lod < 0).ToList(), notes);
        return ms.ToArray();
    }

    private static void Lod(BinaryWriter w, OdolModel m, OdolLod lod, IReadOnlyList<Axis> axes, List<string> notes)
    {
        var n = lod.Positions.Length;
        var center = m.BoundingCenter;
        var frames = lod.Frames.Where(f => f.Points.Length == n).ToArray();
        if (frames.Length < lod.Frames.Length) notes.Add(Loc.F("Mx.Frames", Odol.LodName(lod.Resolution), lod.Frames.Length - frames.Length));

        var bones = new (int Bone, byte W)[n][];
        for (var v = 0; v < n; v++)
        {
            bones[v] = [];
            if (lod.BoneRefs.Length == 0) continue;
            var at = 12 * v;
            var count = Math.Min(4, BinaryPrimitives.ReadInt32LittleEndian(lod.BoneRefs.AsSpan(at)));
            var list = new List<(int, byte)>();
            for (var k = 0; k < count; k++)
            {
                var sub = lod.BoneRefs[at + 4 + 2 * k];
                var weight = lod.BoneRefs[at + 5 + 2 * k];
                if (sub < lod.SubSkeleton.Length && weight > 0) list.Add((lod.SubSkeleton[sub], weight));
            }
            bones[v] = list.ToArray();
        }
        var lists = new List<(int Sel, byte W)>?[n];
        for (var s = 0; s < lod.Selections.Length; s++)
        {
            var sel = lod.Selections[s];
            for (var i = 0; i < sel.Vertices.Length; i++)
                (lists[sel.Vertices[i]] ??= new()).Add((s, sel.Weights.Length > 0 ? sel.Weights[i] : (byte)255));
        }

        // A proxy triangle keeps its own points: proxies in the same place sharing points come back from binarize as one.
        var proxyFace = new int[n];
        Array.Fill(proxyFace, -1);
        foreach (var p in lod.Proxies)
            foreach (var f in lod.Selections[p.SelectionIndex].Faces)
                foreach (var v in lod.Faces[f]) proxyFace[v] = f;
        // Binarize tells a point's vertices apart by normal and first UV only: two that agree on those but differ in another
        // UV set stay separate points, or binarize leaves one of them out of that set's range and wraps its value.
        var pointOf = new int[n];
        var firstVertex = new List<int>();
        var keys = new Dictionary<string, List<int>>();
        var carried = new List<Dictionary<(Vector3, Vector2), int>>();
        var key = new StringBuilder();
        for (var v = 0; v < n; v++)
        {
            var p = lod.Positions[v];
            key.Clear().Append(BitConverter.SingleToInt32Bits(p.X)).Append(',').Append(BitConverter.SingleToInt32Bits(p.Y))
                .Append(',').Append(BitConverter.SingleToInt32Bits(p.Z)).Append('|').Append(lod.Clip[v] & ~0x3Fu);
            foreach (var (b, bw) in bones[v].OrderBy(x => x.Bone)) key.Append('|').Append(b).Append(':').Append(bw);
            if (lists[v] != null) foreach (var (s, sw) in lists[v]!) key.Append("|s").Append(s).Append(':').Append(sw);
            foreach (var f in frames) key.Append("|f").Append(f.Points[v].ToString());
            if (proxyFace[v] >= 0) key.Append("|p").Append(proxyFace[v]);
            var k = key.ToString();
            if (!keys.TryGetValue(k, out var candidates)) keys[k] = candidates = new();
            var corner = (lod.Normals[v], lod.Uv[0][v]);
            var point = candidates.FirstOrDefault(c => !carried[c].TryGetValue(corner, out var other) || SameUv(lod, other, v), -1);
            if (point < 0)
            {
                point = firstVertex.Count;
                firstVertex.Add(v);
                carried.Add(new());
                candidates.Add(point);
            }
            carried[point].TryAdd(corner, v);
            pointOf[v] = point;
        }
        var points = firstVertex.Select(v => (Pos: lod.Positions[v] + center, Flags: PointFlags(lod.Clip[v]))).ToList();
        // Hidden points leave no trace in the vertices, only in the sections of their faces: a point is hidden when every
        // face using it sits in such a section.
        var hiddenFaces = new int[points.Count];
        var allFaces = new int[points.Count];
        for (var f = 0; f < lod.Faces.Length; f++)
        {
            var hidden = lod.FaceSection[f] >= 0 && (lod.Sections[lod.FaceSection[f]].Special & HiddenSection) != 0;
            foreach (var v in lod.Faces[f].Select(v => pointOf[v]).Distinct())
            {
                allFaces[v]++;
                if (hidden) hiddenFaces[v]++;
            }
        }
        for (var p = 0; p < points.Count; p++)
            if (allFaces[p] > 0 && hiddenFaces[p] == allFaces[p]) points[p] = points[p] with { Flags = points[p].Flags | HiddenPoint };
        var axisFirst = points.Count;
        foreach (var a in axes) points.AddRange(a.Points.Select(p => (p + center, 0u)));

        var normals = new List<Vector3>();
        var normalIndex = new Dictionary<Vector3, int>();
        var normalOf = new int[n];
        for (var v = 0; v < n; v++)
        {
            if (!normalIndex.TryGetValue(lod.Normals[v], out var ni))
            {
                normalIndex[lod.Normals[v]] = ni = normals.Count;
                normals.Add(lod.Normals[v]);
            }
            normalOf[v] = ni;
        }

        w.Write("P3DM"u8);
        w.Write(0x1C);
        w.Write(0x100);
        w.Write(points.Count);
        w.Write(normals.Count);
        w.Write(lod.Faces.Length);
        w.Write(0);
        foreach (var (pos, flags) in points)
        {
            w.Write(pos.X);
            w.Write(pos.Y);
            w.Write(pos.Z);
            w.Write(flags);
        }
        foreach (var nv in normals)
        {
            w.Write(nv.X);
            w.Write(nv.Y);
            w.Write(nv.Z);
        }
        var corners = new int[lod.Faces.Length][];
        for (var f = 0; f < lod.Faces.Length; f++)
        {
            var src = lod.Faces[f];
            var c = corners[f] = src.Length == 4 ? [src[1], src[0], src[3], src[2]] : [src[1], src[0], src[2]];
            w.Write(c.Length);
            for (var k = 0; k < 4; k++)
            {
                var v = k < c.Length ? c[k] : -1;
                w.Write(v < 0 ? 0 : pointOf[v]);
                w.Write(v < 0 ? 0 : normalOf[v]);
                w.Write(v < 0 ? 0f : lod.Uv[0][v].X);
                w.Write(v < 0 ? 0f : lod.Uv[0][v].Y);
            }
            var s = lod.FaceSection[f] >= 0 ? lod.Sections[lod.FaceSection[f]] : null;
            w.Write(s == null ? 0u : FaceFlags(s.Special));
            Z(w, s is { TextureIndex: >= 0 } ? lod.Textures[s.TextureIndex] : "");
            Z(w, s == null ? "" : s.MaterialIndex >= 0 ? lod.Materials[s.MaterialIndex].Path : s.Surface);
        }

        w.Write("TAGG"u8);
        for (var set = 0; set < lod.Uv.Length; set++)
        {
            var data = new byte[4 + corners.Sum(c => 8 * c.Length)];
            BinaryPrimitives.WriteInt32LittleEndian(data, set);
            var at = 4;
            foreach (var c in corners)
                foreach (var v in c)
                {
                    BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(at), lod.Uv[set][v].X);
                    BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(at + 4), lod.Uv[set][v].Y);
                    at += 8;
                }
            Tag(w, "#UVSet#", data);
        }
        foreach (var (name, value) in lod.Properties)
        {
            var data = new byte[128];
            Encoding.Latin1.GetBytes(name.Length > 63 ? name[..63] : name).CopyTo(data, 0);
            Encoding.Latin1.GetBytes(value.Length > 63 ? value[..63] : value).CopyTo(data, 64);
            Tag(w, "#Property#", data);
        }
        // Mikero's tools expect the mass tag in the geometry LOD even for a model without mass.
        if (Odol.LodName(lod.Resolution) == "Geometry")
        {
            var mass = Array.Empty<float>();
            if (m.Mass > 0 && firstVertex.Count > 0)
            {
                var r = Mass.Solve(firstVertex.Select(v => lod.Positions[v]).ToList(), m.Mass, m.CenterOfMass, m.InvInertia);
                mass = r.Masses;
                var size = Math.Max(1f, (m.BoundsMax - m.BoundsMin).Length());
                if (r.MassError > 1e-6 || r.ComError > 1e-6 * size || r.InertiaError > 1e-6)
                    notes.Add(Loc.F("Mx.MassInexact", r.MassError.ToString("0.###E+0", Loc.Culture),
                        r.ComError.ToString("0.###E+0", Loc.Culture), r.InertiaError.ToString("0.###E+0", Loc.Culture)));
            }
            var data = new byte[4 * points.Count];
            for (var p = 0; p < mass.Length; p++) BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(4 * p), mass[p]);
            Tag(w, "#Mass#", data);
        }
        if (SharpEdges(corners, pointOf, normalOf) is { Length: > 0 } sharp) Tag(w, "#SharpEdges#", sharp);

        var faceCount = lod.Faces.Length;
        var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var axisAt = new int[axes.Count];
        for (var a = 1; a < axes.Count; a++) axisAt[a] = axisAt[a - 1] + axes[a - 1].Points.Length;
        for (var s = 0; s < lod.Selections.Length; s++)
        {
            var sel = lod.Selections[s];
            named.Add(sel.Name);
            var data = new byte[points.Count + faceCount];
            foreach (var f in sel.Faces) data[points.Count + f] = 1;
            var bone = Array.FindIndex(m.Bones, b => b.Name.Equals(sel.Name, StringComparison.OrdinalIgnoreCase));
            if (sel.Vertices.Length > 0)
                for (var i = 0; i < sel.Vertices.Length; i++)
                    Select(data, pointOf[sel.Vertices[i]], sel.Weights.Length > 0 ? sel.Weights[i] : (byte)255);
            else if (bone >= 0 && lod.BoneRefs.Length > 0)
                for (var v = 0; v < n; v++)
                    foreach (var (b, bw) in bones[v])
                        if (b == bone) Select(data, pointOf[v], bw);
            if (sel.Vertices.Length == 0 && (bone < 0 || lod.BoneRefs.Length == 0))
                foreach (var f in sel.Faces)
                    foreach (var v in corners[f]) Select(data, pointOf[v], 255);
            for (var a = 0; a < axes.Count; a++)
                if (axes[a].Name.Equals(sel.Name, StringComparison.OrdinalIgnoreCase))
                    for (var k = 0; k < axes[a].Points.Length; k++) data[axisFirst + axisAt[a] + k] = 1;
            Tag(w, sel.Name, data);
        }
        // Bones that move vertices of this LOD but have no selection of their name in it.
        foreach (var bone in bones.SelectMany(b => b).Select(b => b.Bone).Distinct().Order())
        {
            var name = m.Bones[bone].Name;
            if (!named.Add(name)) continue;
            var data = new byte[points.Count + faceCount];
            for (var v = 0; v < n; v++)
                foreach (var (b, bw) in bones[v])
                    if (b == bone) Select(data, pointOf[v], bw);
            Tag(w, name, data);
        }
        for (var a = 0; a < axes.Count; a++)
        {
            if (!named.Add(axes[a].Name)) continue;
            var data = new byte[points.Count + faceCount];
            for (var k = 0; k < axes[a].Points.Length; k++) data[axisFirst + axisAt[a] + k] = 1;
            Tag(w, axes[a].Name, data);
        }
        foreach (var (time, framePoints) in frames)
        {
            var data = new byte[4 + 12 * points.Count];
            BinaryPrimitives.WriteSingleLittleEndian(data, time);
            for (var p = 0; p < points.Count; p++)
            {
                var pos = p < firstVertex.Count ? framePoints[firstVertex[p]] + center : points[p].Pos;
                BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(4 + 12 * p), pos.X);
                BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(8 + 12 * p), pos.Y);
                BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(12 + 12 * p), pos.Z);
            }
            Tag(w, "#Animation#", data);
        }
        Tag(w, "#EndOfFile#", []);
        w.Write(lod.Resolution);
    }

    private const uint HiddenSection = 0x400000, HiddenPoint = 0x1000000;

    private static bool SameUv(OdolLod lod, int a, int b)
    {
        for (var s = 1; s < lod.Uv.Length; s++)
            if (lod.Uv[s][a] != lod.Uv[s][b]) return false;
        return true;
    }

    private static void Select(byte[] data, int point, byte weight)
    {
        if (weight == 0) return;
        var b = weight == 255 ? (byte)1 : (byte)(256 - weight);
        if (data[point] == 0 || b < data[point]) data[point] = b;
    }

    private static uint PointFlags(uint clip)
    {
        var f = (clip >> 8 & 0xF) | (clip >> 12 & 3) << 8 | (clip >> 14 & 3) << 12;
        var user = clip >> 20 & 0xFF;
        return f | user switch { 200 => 0x10u, 201 => 0x20u, 202 => 0x80u, 203 => 0x40u, _ => user << 16 };
    }

    // Section flags 0x20 / 0x40 are the faces' "no shadow" / "is shadow", bits 26-27 the z-bias.
    private static uint FaceFlags(uint special)
    {
        var f = (special >> 26 & 3) << 8;
        if ((special & 0x20) != 0) f |= 0x10;
        if ((special & 0x40) != 0) f |= 0x8;
        return f;
    }

    private static byte[] SharpEdges(int[][] corners, int[] pointOf, int[] normalOf)
    {
        var seen = new Dictionary<(int, int), (int Na, int Nb, bool Sharp)>();
        foreach (var c in corners)
            for (var k = 0; k < c.Length; k++)
            {
                int va = c[k], vb = c[(k + 1) % c.Length];
                int pa = pointOf[va], pb = pointOf[vb];
                if (pa == pb) continue;
                if (pa > pb) (pa, pb, va, vb) = (pb, pa, vb, va);
                var here = (normalOf[va], normalOf[vb]);
                if (!seen.TryGetValue((pa, pb), out var e)) seen[(pa, pb)] = (here.Item1, here.Item2, false);
                else if (!e.Sharp && (e.Na, e.Nb) != here) seen[(pa, pb)] = e with { Sharp = true };
            }
        var sharp = seen.Where(e => e.Value.Sharp).Select(e => e.Key).ToList();
        var data = new byte[8 * sharp.Count];
        for (var i = 0; i < sharp.Count; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8 * i), sharp[i].Item1);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8 * i + 4), sharp[i].Item2);
        }
        return data;
    }

    private static void Tag(BinaryWriter w, string name, byte[] data)
    {
        w.Write((byte)1);
        Z(w, name);
        w.Write(data.Length);
        w.Write(data);
    }

    private static void Z(BinaryWriter w, string s)
    {
        w.Write(Encoding.Latin1.GetBytes(s));
        w.Write((byte)0);
    }
}
