using Pfx.Core.Export;
using Pfx.Render;

namespace Pfx.App.Model;

// What an export reads, taken on the UI thread so a worker can plan without touching live documents: the open parts'
// and changed materials' current bytes (the export writes what the user sees, saved or not) and the data roots.
public sealed class ExportSnapshot : IExportSource
{
    private readonly Dictionary<string, byte[]> _open = new(StringComparer.OrdinalIgnoreCase);
    private readonly DataRoots _roots;

    public ExportSnapshot(IEnumerable<EffectDoc> docs, IEnumerable<string> roots)
    {
        foreach (var d in docs)
        {
            _open[Path.GetFullPath(d.Path)] = d.Ptc.ToBytes();
            foreach (var m in d.Materials.Values.Where(m => m.Dirty))
                _open[Path.GetFullPath(m.DiskPath)] = m.Emat.ToBytes();
        }
        _roots = new DataRoots(roots.ToList());
    }

    public string? Resolve(string gamePath) => _roots.Resolve(gamePath);

    public string? GamePath(string diskFolder)
    {
        string full = Path.GetFullPath(diskFolder).TrimEnd(Path.DirectorySeparatorChar);
        foreach (var root in _roots.Roots)
        {
            string r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (full.StartsWith(r, StringComparison.OrdinalIgnoreCase) && full.Length > r.Length)
                return full[r.Length..].Replace(Path.DirectorySeparatorChar, '/');
        }
        return null;
    }

    public byte[] Read(string diskPath) => _open.TryGetValue(Path.GetFullPath(diskPath), out var bytes) ? bytes : File.ReadAllBytes(diskPath);
}
