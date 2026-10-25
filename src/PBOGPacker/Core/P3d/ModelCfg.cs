using System.Globalization;
using System.Numerics;
using System.Text;

namespace PolyLab.PBOGPacker.Core.P3d;

// model.cfg of a converted model: the skeleton, the sections and the animations an ODOL keeps. Binarize drops the vertex
// lists of every visual-LOD selection once the model has a CfgModels class (an empty sections[] is enough), so the class
// is written only when the model needs one: a skeleton, animations, sections, or point lists binarize already dropped.
// Animation axes are stored per LOD as
// (position, direction) in model coordinates: rotation = first point and normalised direction of the axis selection,
// rotationX/Y/Z = its centre, translation = the full vector between its two points, taken in the order of its points.
public static class ModelCfg
{
    private static readonly string[] Types =
        ["rotation", "rotationX", "rotationY", "rotationZ", "translation", "translationX", "translationY", "translationZ", "direct", "hide"];

    // A visual-LOD selection with faces but no points shows that the source had a class too.
    public static bool Needed(OdolModel m) =>
        m.Skeleton.Length > 0 || m.Animations.Length > 0 || m.Lods.Any(l => l.Selections.Any(s => s.Sectional))
        || m.Lods.Any(l => l.Resolution < 1e4 && l.Selections.Any(s => s.Faces.Length > 0 && s.Vertices.Length == 0));

    // Memory: the axis comes from the memory LOD for every LOD (memory=1), or each LOD looks for it itself (memory=0).
    public sealed record AxisRef(string Name, bool Memory);

    // The axis of each animation (null for direct, hide and animations that move no bone). An animation that moves its
    // bone in every LOD takes a memory-LOD selection; one that moves it only in some LODs had memory=0 and an axis
    // selection in exactly those LODs. A selection binarize kept the points of must give back the stored axis; one whose
    // points it dropped (visual LODs) gets them again, and a missing one is added; those go to added.
    public static AxisRef?[] Axes(OdolModel m, List<Mlod.Axis> added, List<string> notes)
    {
        var memory = Array.FindIndex(m.Lods, l => Odol.LodName(l.Resolution) == "Memory");
        var taken = new HashSet<string>(m.Lods.SelectMany(l => l.Selections).Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
        var refs = new AxisRef?[m.Animations.Length];
        for (var j = 0; j < refs.Length; j++)
        {
            var a = m.Animations[j];
            var plus = Enumerable.Range(0, m.Lods.Length).Where(l => a.Bone[l] >= 0 && a.Axis[l] != null).ToList();
            if (plus.Count == 0) continue;
            string NewName()
            {
                var name = Ident(a.Name) + "_axis";
                for (var k = 2; !taken.Add(name); k++) name = Ident(a.Name) + "_axis" + k;
                notes.Add(Loc.F("Mx.AxisNew", a.Name, name));
                return name;
            }
            if (a.Bone.All(b => b >= 0))
            {
                var (pos, dir) = a.Axis[plus[0]]!.Value;
                var found = memory < 0 ? null
                    : m.Lods[memory].Selections.FirstOrDefault(s => Matches(a.Type, Points(m.Lods[memory], s), pos, dir))?.Name;
                if (found == null)
                {
                    var pts = AxisPoints(a.Type, pos, dir);
                    found = added.FirstOrDefault(x => x.Lod < 0 && x.Points.SequenceEqual(pts))?.Name;
                    if (found == null) added.Add(new Mlod.Axis(found = NewName(), -1, pts));
                }
                refs[j] = new AxisRef(found, true);
                continue;
            }
            // An existing axis selection has no faces, is in every LOD that moves the bone and in none of the others.
            string? name = null;
            foreach (var cand in m.Lods[plus[0]].Selections.Where(s => s.Faces.Length == 0).Select(s => s.Name))
            {
                var ok = Enumerable.Range(0, m.Lods.Length).All(l =>
                {
                    var s = m.Lods[l].Selections.FirstOrDefault(x => x.Name.Equals(cand, StringComparison.OrdinalIgnoreCase));
                    if (!plus.Contains(l)) return s == null;
                    return s != null && s.Faces.Length == 0
                        && (s.Vertices.Length == 0 || Matches(a.Type, Points(m.Lods[l], s), a.Axis[l]!.Value.Pos, a.Axis[l]!.Value.Dir));
                });
                if (ok)
                {
                    name = cand;
                    break;
                }
            }
            name ??= NewName();
            foreach (var l in plus)
            {
                var s = m.Lods[l].Selections.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (s == null || s.Vertices.Length == 0)
                    added.Add(new Mlod.Axis(name, l, AxisPoints(a.Type, a.Axis[l]!.Value.Pos, a.Axis[l]!.Value.Dir)));
            }
            refs[j] = new AxisRef(name, false);
        }
        return refs;
    }

    private static Vector3[] AxisPoints(int type, Vector3 pos, Vector3 dir) => type switch
    {
        0 => [pos, pos + Vector3.Normalize(dir)],
        4 => [Vector3.Zero, dir],
        _ => [pos],
    };

    // The points of a selection in the order binarize meets them, each position once (the MLOD welds duplicates).
    private static Vector3[] Points(OdolLod lod, OdolSelection s) => s.Vertices.Select(v => lod.Positions[v]).Distinct().ToArray();

    private static bool Matches(int type, Vector3[] p, Vector3 pos, Vector3 dir) => type switch
    {
        0 => p.Length == 2 && Near(p[0], pos) && Near(Vector3.Normalize(p[1] - p[0]), Vector3.Normalize(dir)),
        4 => p.Length == 2 && Near(p[1] - p[0], dir),
        _ => p.Length > 0 && Near(p.Aggregate(Vector3.Zero, (sum, v) => sum + v) / p.Length, pos),
    };

    // Float32 positions carry about seven digits; normalising the direction costs a few more ulps.
    private static bool Near(Vector3 a, Vector3 b) => (a - b).Length() <= 1e-4f * Math.Max(1f, Math.Max(a.Length(), b.Length()));

    public static string Write(OdolModel m, string model, AxisRef?[] axes, List<string> notes)
    {
        var t = new StringBuilder();
        var skeleton = Ident(m.Skeleton);
        if (m.Skeleton.Length > 0)
        {
            t.Append("class CfgSkeletons\n{\n\tclass ").Append(skeleton).Append("\n\t{\n");
            t.Append("\t\tisDiscrete=").Append(m.Discrete ? 1 : 0).Append(";\n\t\tskeletonInherit=\"\";\n");
            t.Append("\t\tskeletonBones[]=\n\t\t{\n");
            for (var i = 0; i < m.Bones.Length; i++)
                t.Append("\t\t\t\"").Append(m.Bones[i].Name).Append("\",\"").Append(m.Bones[i].Parent).Append('"')
                    .Append(i < m.Bones.Length - 1 ? ",\n" : "\n");
            t.Append("\t\t};\n");
            if (m.Pivots.Length > 0) t.Append("\t\tpivotsModel=\"").Append(m.Pivots).Append("\";\n");
            t.Append("\t};\n};\n");
        }
        if (Ident(model) != model) notes.Add(Loc.F("Mx.ClassName", model, Ident(model)));
        t.Append("class CfgModels\n{\n\tclass ").Append(Ident(model)).Append("\n\t{\n");
        t.Append("\t\tskeletonName=\"").Append(skeleton).Append("\";\n\t\tsectionsInherit=\"\";\n");
        var sections = m.Lods.SelectMany(l => l.Selections).Where(s => s.Sectional).Select(s => s.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        t.Append("\t\tsections[]=").Append(sections.Count == 0 ? "{};\n" : "\n\t\t{\n\t\t\t\"" + string.Join("\",\n\t\t\t\"", sections) + "\"\n\t\t};\n");
        if (m.Animations.Length > 0)
        {
            t.Append("\t\tclass Animations\n\t\t{\n");
            for (var j = 0; j < m.Animations.Length; j++)
            {
                var a = m.Animations[j];
                var bone = a.Bone.FirstOrDefault(b => b >= 0, -1);
                if (bone < 0) notes.Add(Loc.F("Mx.NoBone", a.Name));
                t.Append("\t\t\tclass ").Append(Ident(a.Name)).Append("\n\t\t\t{\n");
                void Key(string k, string v) => t.Append("\t\t\t\t").Append(k).Append('=').Append(v).Append(";\n");
                string S(string v) => "\"" + v + "\"";
                Key("type", S(Types[a.Type]));
                Key("source", S(a.Source));
                Key("selection", S(bone >= 0 ? m.Bones[bone].Name : ""));
                if (axes[j] is { } axis)
                {
                    Key("axis", S(axis.Name));
                    Key("memory", axis.Memory ? "1" : "0");
                }
                if (a.Address is 1 or 2) Key("sourceAddress", S(a.Address == 1 ? "loop" : "mirror"));
                Key("minValue", F(a.MinValue));
                Key("maxValue", F(a.MaxValue));
                if (a.MinPhase != a.MinValue || a.MaxPhase != a.MaxValue)
                {
                    Key("minPhase", F(a.MinPhase));
                    Key("maxPhase", F(a.MaxPhase));
                }
                var v = a.Values;
                switch (a.Type)
                {
                    case <= 3:
                        Key("angle0", F(v[0]));
                        Key("angle1", F(v[1]));
                        break;
                    case <= 7:
                        Key("offset0", F(v[0]));
                        Key("offset1", F(v[1]));
                        break;
                    case 8:
                        Key("axisPos[]", "{" + F(v[0]) + "," + F(v[1]) + "," + F(v[2]) + "}");
                        Key("axisDir[]", "{" + F(v[3]) + "," + F(v[4]) + "," + F(v[5]) + "}");
                        Key("angle", F((float)(v[6] * 180 / Math.PI)));
                        Key("axisOffset", F(v[7]));
                        break;
                    default:
                        Key("hideValue", F(v[0]));
                        break;
                }
                t.Append("\t\t\t};\n");
            }
            t.Append("\t\t};\n");
        }
        t.Append("\t};\n};\n");
        return t.ToString();
    }

    // Shortest text that reads back as the same float; configs take no exponent, so small values are written out.
    public static string F(float v)
    {
        var s = v.ToString(CultureInfo.InvariantCulture);
        return s.Contains('E') ? ((double)v).ToString("0.##############################", CultureInfo.InvariantCulture) : s;
    }

    private static string Ident(string s)
    {
        var b = new StringBuilder(s.Length);
        foreach (var ch in s) b.Append(char.IsAsciiLetterOrDigit(ch) || ch == '_' ? ch : '_');
        return b.ToString();
    }
}
