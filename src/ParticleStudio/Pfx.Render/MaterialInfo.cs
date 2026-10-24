using System.Numerics;
using Pfx.Core.Model;
using Pfx.Core.Schema;
using Pfx.Core.Text;
using Pfx.Sim;

namespace Pfx.Render;

// The .emat keys the particle shaders use. Missing keys take the schema default; where the schema has none, the value
// the R0 constants show for materials without the key (CameraBlendNear 0 and CameraBlendFar 1: vDistBlend 1, -0.07 of
// a material with neither; BidirLightScale 1: cDiffuseL1.w of roadflare_01; DepthScale 0, TileU/V 1).
public sealed record MaterialInfo
{
    public string? Path { get; init; }
    public bool Found { get; init; }
    public string ClassName { get; init; } = "Particle";
    public string? AlbedoMap { get; init; }
    public string? NormalMap { get; init; }
    public Vector3 Color { get; init; } = Vector3.One;
    public Vector3 Ambient { get; init; } = Vector3.One;
    public Vector3 Emissive { get; init; }
    public bool Additive { get; init; }
    public bool ZEnable { get; init; } = true;
    public float Softness { get; init; }
    public float DepthScale { get; init; }
    public float CameraBlendNear { get; init; }
    public float CameraBlendFar { get; init; } = 1;
    public float LightScale { get; init; } = 1;
    public float BidirLightScale { get; init; } = 1;
    public int TileU { get; init; } = 1;
    public int TileV { get; init; } = 1;

    // P0: a ParticleStreak material with the keys of a working Particle material draws nothing in game.
    public bool DrawsNothing => ClassName == "ParticleStreak";

    public static readonly MaterialInfo Missing = new();

    public static MaterialInfo Read(string path) => From(EmatDocument.Load(path), path);

    public static MaterialInfo From(EmatDocument emat, string path)
    {
        Vector3 V(string key, Vector3 fallback) =>
            emat.Numbers(key) is { Length: >= 3 } n ? new((float)n[0], (float)n[1], (float)n[2]) : fallback;
        float F(string key, float fallback) => (float)(emat.Number(key) ?? fallback);
        // Texture lines may carry flags after the path ("AlbedoMap "<path>" alpha", vanilla sparks1.emat).
        string? Map(string key) => emat.Block.FindLine(key)?.Values.FirstOrDefault()?.Value;
        string? zEnable = emat.Text("ZEnable");
        // A number is an index into the schema's value list (vanilla glow1.emat: BlendMode 2 = Additive_AlphaModulate).
        string? blend = emat.Text("BlendMode");
        if (int.TryParse(blend, out int index) && emat.Schema.Find(SchemaScope.Material, "BlendMode")?.Values is { } names
            && index >= 0 && index < names.Count)
            blend = names[index];
        return new MaterialInfo
        {
            Path = path,
            Found = true,
            ClassName = emat.ClassName,
            AlbedoMap = Map("AlbedoMap"),
            NormalMap = Map("NormalMap"),
            Color = V("Color", Vector3.One),
            Ambient = V("Ambient", Vector3.One),
            Emissive = V("Emissive", Vector3.Zero),
            Additive = blend == "Additive_AlphaModulate",
            ZEnable = zEnable is not ("0" or "false"),
            Softness = F("Softness", 0),
            DepthScale = F("DepthScale", 0),
            CameraBlendNear = F("CameraBlendNear", 0),
            CameraBlendFar = F("CameraBlendFar", 1),
            LightScale = F("LightScale", 1),
            BidirLightScale = F("BidirLightScale", 1),
            TileU = Math.Max(1, (int)F("TileU", 1)),
            TileV = Math.Max(1, (int)F("TileV", 1)),
        };
    }
}

// Game paths ("Graphics/Particles/...", "{GUID}CG_AnomalyData/...") are relative to a data root (the work drive or a
// mod's source folder). Roots come from the user; when none fits, the effect's own location is tried: the ancestor
// folder under which the game path exists.
public sealed class DataRoots
{
    private readonly List<string> _roots;
    private readonly Dictionary<string, MaterialInfo> _materials = new(StringComparer.OrdinalIgnoreCase);

    public DataRoots(IEnumerable<string> roots) => _roots = roots.Where(r => r.Length > 0).ToList();

    public IReadOnlyList<string> Roots => _roots;

    public string? Resolve(string gamePath)
    {
        string rel = MaterialTiles.StripGuid(gamePath).Replace('/', System.IO.Path.DirectorySeparatorChar);
        foreach (var root in _roots)
        {
            string full = System.IO.Path.Combine(root, rel);
            if (File.Exists(full))
                return full;
        }
        return null;
    }

    // Adds the first ancestor of the file under which gamePath exists, if no root resolves it yet.
    public void AddRootFor(string filePath, string gamePath)
    {
        if (Resolve(gamePath) != null)
            return;
        string rel = MaterialTiles.StripGuid(gamePath).Replace('/', System.IO.Path.DirectorySeparatorChar);
        for (var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(filePath)); dir != null;
             dir = System.IO.Path.GetDirectoryName(dir))
        {
            if (File.Exists(System.IO.Path.Combine(dir, rel)))
            {
                _roots.Add(dir);
                return;
            }
        }
    }

    public MaterialInfo Material(string? gamePath)
    {
        if (gamePath == null)
            return MaterialInfo.Missing;
        if (_materials.TryGetValue(gamePath, out var hit))
            return hit;
        var full = Resolve(gamePath);
        MaterialInfo info;
        try
        {
            info = full != null ? MaterialInfo.Read(full) : MaterialInfo.Missing;
        }
        catch (Exception e) when (e is EnfSyntaxException or InvalidDataException or IOException)
        {
            info = MaterialInfo.Missing with { Path = full };
        }
        _materials[gamePath] = info;
        return info;
    }

    // A material that is being edited and not saved yet: the preview uses it instead of the file on disk.
    public void SetMaterial(string gamePath, MaterialInfo info) => _materials[gamePath] = info;

    public Func<string, (int U, int V)?> Tiles() => material =>
        Material(material) is { Found: true } m ? (m.TileU, m.TileV) : null;
}
