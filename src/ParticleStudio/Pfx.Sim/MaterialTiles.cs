using Pfx.Core.Model;
using Pfx.Core.Text;

namespace Pfx.Sim;

// Finds an emitter's .emat under data roots (the folders a game path is relative to) and reads TileU / TileV.
public static class MaterialTiles
{
    public static Func<string, (int U, int V)?> Resolver(IEnumerable<string> roots)
    {
        var list = roots.ToList();
        var cache = new Dictionary<string, (int, int)?>(StringComparer.OrdinalIgnoreCase);
        return material =>
        {
            string rel = StripGuid(material);
            if (cache.TryGetValue(rel, out var hit))
                return hit;
            (int, int)? found = null;
            foreach (var root in list)
            {
                string path = Path.Combine(root, rel);
                if (!File.Exists(path))
                    continue;
                try
                {
                    var emat = EmatDocument.Load(path);
                    found = ((int)(emat.Number("TileU") ?? 1), (int)(emat.Number("TileV") ?? 1));
                }
                catch (Exception e) when (e is EnfSyntaxException or InvalidDataException)
                {
                    found = null;
                }
                break;
            }
            cache[rel] = found;
            return found;
        };
    }

    // "{009C2EBAACD2D72A}Graphics/..." -> "Graphics/..."
    public static string StripGuid(string path) =>
        path.StartsWith('{') && path.IndexOf('}') is > 0 and var end ? path[(end + 1)..] : path;
}
