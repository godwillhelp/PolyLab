using System.Numerics;
using System.Text;

namespace PolyLab.PBOGPacker.Core.P3d;

// Text rvmat of a material an ODOL carries inside. Names of the stored codes were read off DayZ 1.30's own rvmats next
// to their embedded copies (shader ids, uvSource, render flag bits); a code not seen there is not guessed but reported.
// Binarize pads the stage list to the shader's size; those stages are left out.
public static class Rvmat
{
    private static readonly Dictionary<uint, string> PixelShaders = new()
    {
        [0] = "Normal", [5] = "NormalMapDiffuse", [6] = "Detail", [13] = "AlphaNoShadow", [15] = "DetailMacroAS",
        [18] = "NormalMapSpecularMap", [19] = "NormalMapDetailSpecularMap", [22] = "NormalMapSpecularDIMap",
        [23] = "NormalMapDetailSpecularDIMap", [24] = "NormalMapMacroASSpecularDIMap", [25] = "NormalMapDetailMacroASSpecularDIMap",
        [59] = "Grass", [102] = "Super", [103] = "Multi", [107] = "Tree", [110] = "Skin", [111] = "CalmWater", [114] = "TreeAdv",
        [116] = "TreeAdvTrunk", [129] = "SuperHair",
    };

    private static readonly Dictionary<uint, string> VertexShaders = new()
    {
        [0] = "Basic", [1] = "NormalMap", [2] = "NormalMapDiffuse", [3] = "Grass", [14] = "BasicAS", [15] = "NormalMapAS",
        [23] = "Super", [24] = "Multi", [25] = "Tree", [26] = "TreeNoFade", [29] = "Skin", [30] = "CalmWater", [31] = "TreeAdv",
        [32] = "TreeAdvTrunk", [35] = "TreeAdvModNormals",
    };

    private static readonly Dictionary<int, string> UvSources = new() { [0] = "none", [1] = "tex", [5] = "tex1", [6] = "WorldPos" };

    private static readonly (int Bit, string Name)[] RenderFlags =
    [
        (0, "AlwaysInShadow"), (1, "NoZWrite"), (4, "NoColorWrite"), (5, "NoAlphaWrite"), (6, "AddBlend"), (7, "AlphaTest32"),
        (8, "AlphaTest64"), (9, "AlphaTest128"), (11, "NoTIWrite"),
    ];

    private const uint Sun = 3, Fog = 1;

    public static string Write(OdolMaterial m, List<string> notes)
    {
        var t = new StringBuilder();
        void Color(string key, int i) => t.Append(key).Append("[]={").Append(ModelCfg.F(m.Colors[i].X)).Append(',')
            .Append(ModelCfg.F(m.Colors[i].Y)).Append(',').Append(ModelCfg.F(m.Colors[i].Z)).Append(',')
            .Append(ModelCfg.F(m.Colors[i].W)).Append("};\n");
        Color("ambient", 1);
        Color("diffuse", 2);
        Color("forcedDiffuse", 3);
        Color("emmisive", 0);
        Color("specular", 4);
        t.Append("specularPower=").Append(ModelCfg.F(m.SpecularPower)).Append(";\n");
        // Written only where they differ from what binarize stores for a material without them.
        if (m.Colors[5] != new Vector4(0, 0, 0, 1)) Color("treeColorization", 5);
        if (m.PlantWind != new Vector4(0, 0, 0.3f, 0.99f))
            t.Append("plantWind[]={").Append(string.Join(",", new[] { m.PlantWind.X, m.PlantWind.Y, m.PlantWind.Z, m.PlantWind.W }.Select(ModelCfg.F)))
                .Append("};\n");
        if (m.ShadowBias != 0) t.Append("shadowBias=").Append(ModelCfg.F(m.ShadowBias)).Append(";\n");
        if (m.ParallaxScale != -1) t.Append("parallaxScale=").Append(ModelCfg.F(m.ParallaxScale)).Append(";\n");
        var top = m.TopProjection;
        if (top.Layer != -1) t.Append("multiTopProjectionLayer=").Append(top.Layer).Append(";\n");
        if (top.LayerNormal != -1) t.Append("multiTopProjectionLayerNormal=").Append(top.LayerNormal).Append(";\n");
        if (top.StartAngle != 30) t.Append("degAngleTopProjectionStart=").Append(ModelCfg.F(top.StartAngle)).Append(";\n");
        if (top.EndAngle != 45) t.Append("degAngleTopProjectionEnd=").Append(ModelCfg.F(top.EndAngle)).Append(";\n");
        if (PixelShaders.TryGetValue(m.PixelShader, out var ps)) t.Append("PixelShaderID=\"").Append(ps).Append("\";\n");
        else notes.Add(Loc.F("Mx.RvmatCode", m.Path, "PixelShaderID", m.PixelShader));
        if (VertexShaders.TryGetValue(m.VertexShader, out var vs)) t.Append("VertexShaderID=\"").Append(vs).Append("\";\n");
        else notes.Add(Loc.F("Mx.RvmatCode", m.Path, "VertexShaderID", m.VertexShader));
        if (m.MainLight != Sun) notes.Add(Loc.F("Mx.RvmatCode", m.Path, "mainLight", m.MainLight));
        if (m.FogMode != Fog) notes.Add(Loc.F("Mx.RvmatCode", m.Path, "fogMode", m.FogMode));
        if (m.Surface.Length > 0) t.Append("surfaceInfo=\"").Append(m.Surface).Append("\";\n");
        var flags = RenderFlags.Where(f => (m.RenderFlags & 1u << f.Bit) != 0).Select(f => "\"" + f.Name + "\"").ToList();
        if (flags.Count > 0) t.Append("renderFlags[]={").Append(string.Join(",", flags)).Append("};\n");
        var unknown = RenderFlags.Aggregate(m.RenderFlags, (u, f) => u & ~(1u << f.Bit));
        if (unknown != 0) notes.Add(Loc.F("Mx.RvmatCode", m.Path, "renderFlags", "0x" + unknown.ToString("X")));

        // Stage 0 is the face's own texture; a material names it only when it sets one itself, and its texture generator is
        // the material's own uvSource / uvTransform when that differs from the default.
        if (m.Stages[0].Texture.Length == 0 && m.Stages[0].TexGen < m.TexGens.Length
            && m.TexGens[m.Stages[0].TexGen] is { } face && (face.UvSource != 0 || !face.Transform.SequenceEqual(FaceTransform)))
            UvGen(t, m, face, "", "uvSource", notes);
        for (var i = 0; i < m.Stages.Length; i++)
            if (m.Stages[i].Texture.Length > 0 && (i == 0 || !Padding(m.Stages[i]))) Stage(t, m, "Stage" + i, m.Stages[i], notes);
        if (m.Ti.Texture.Length > 0) t.Append("class StageTI\n{\n\ttexture=\"").Append(m.Ti.Texture).Append("\";\n};\n");
        return t.ToString();
    }

    // Stages binarize adds to fill the shader's list: no texture, or the face's texture generator without filtering.
    private static bool Padding(OdolStage s) => s.Texture.Length == 0 || s.TexGen == 0 && s.Filter == 0;

    private static void Stage(StringBuilder t, OdolMaterial m, string name, OdolStage s, List<string> notes)
    {
        t.Append("class ").Append(name).Append("\n{\n\ttexture=\"").Append(s.Texture).Append("\";\n");
        if (s.WorldEnvMap) t.Append("\tuseWorldEnvMap=\"true\";\n");
        if (s.TexGen >= 0 && s.TexGen < m.TexGens.Length) UvGen(t, m, m.TexGens[s.TexGen], "\t", name + ".uvSource", notes);
        t.Append("};\n");
    }

    private static readonly float[] FaceTransform = [1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0];

    private static void UvGen(StringBuilder t, OdolMaterial m, OdolTexGen g, string tab, string what, List<string> notes)
    {
        if (UvSources.TryGetValue(g.UvSource, out var src)) t.Append(tab).Append("uvSource=\"").Append(src).Append("\";\n");
        else notes.Add(Loc.F("Mx.RvmatCode", m.Path, what, g.UvSource));
        if (g.UvSource == 0) return;
        var x = g.Transform;
        string V(int k) => "{" + ModelCfg.F(x[k]) + "," + ModelCfg.F(x[k + 1]) + "," + ModelCfg.F(x[k + 2]) + "}";
        t.Append(tab).Append("class uvTransform\n").Append(tab).Append("{\n")
            .Append(tab).Append("\taside[]=").Append(V(0)).Append(";\n").Append(tab).Append("\tup[]=").Append(V(3)).Append(";\n")
            .Append(tab).Append("\tdir[]=").Append(V(6)).Append(";\n").Append(tab).Append("\tpos[]=").Append(V(9)).Append(";\n")
            .Append(tab).Append("};\n");
    }
}
