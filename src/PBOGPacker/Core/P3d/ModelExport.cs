using System.IO;

namespace PolyLab.PBOGPacker.Core.P3d;

// What the Models page and --odol do with a binarized model: write an editable MLOD with its model.cfg and the rvmats
// it carries inside, into <model>_mlod\ beside it unless another folder is given. The rvmats go to the path the model
// names them by, under that folder; the source model is only read.
public static class ModelExport
{
    [Flags]
    public enum Parts { Mlod = 1, Cfg = 2, Rvmats = 4, All = Mlod | Cfg | Rvmats }

    public sealed record Result(IReadOnlyList<string> Written, IReadOnlyList<string> Notes);

    public static string DefaultOut(string file) =>
        Path.Combine(Path.GetDirectoryName(file) ?? "", Path.GetFileNameWithoutExtension(file) + "_mlod");

    // The model's own folder under another output folder. Windows drops trailing spaces and dots of folder names (one
    // vanilla model is "sign_svergino_start .p3d"), so they are trimmed here.
    public static string OutUnder(string root, string file) =>
        Path.Combine(root, Path.GetFileNameWithoutExtension(file).TrimEnd(' ', '.'));

    public static Result Export(string file, string? outDir, Parts parts)
    {
        var m = Odol.Read(file);
        var name = Path.GetFileNameWithoutExtension(file);
        var dir = outDir ?? DefaultOut(file);
        var notes = new List<string>();
        var written = new List<string>();
        if (m.DayZ56 != 0) notes.Add(Loc.F("Mx.DayZ56", m.DayZ56));

        var added = new List<Mlod.Axis>();
        var needed = ModelCfg.Needed(m);
        var axes = needed ? ModelCfg.Axes(m, added, notes) : [];
        Directory.CreateDirectory(dir);
        if (parts.HasFlag(Parts.Mlod))
            written.Add(Save(Path.Combine(dir, name + ".p3d"), Mlod.Write(m, added, notes)));
        if (parts.HasFlag(Parts.Cfg))
        {
            if (needed) written.Add(Save(Path.Combine(dir, "model.cfg"), ModelCfg.Write(m, name, axes, notes)));
            else notes.Add(Loc.T("Mx.NoCfg"));
        }
        if (parts.HasFlag(Parts.Rvmats))
            // Geometry faces may name a surface (.bisurf) where others name a material; only rvmats are written out.
            foreach (var mat in m.Lods.SelectMany(l => l.Materials).DistinctBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                         .Where(x => x.Path.EndsWith(".rvmat", StringComparison.OrdinalIgnoreCase)))
            {
                var rel = mat.Path.Replace('/', '\\').Trim('\\');
                if (rel.Length == 0 || rel.Split('\\').Any(s => s is "." or ".." || s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
                    continue;
                written.Add(Save(Path.Combine(dir, rel), Rvmat.Write(mat, notes)));
            }
        return new Result(written, notes.Distinct().ToList());
    }

    // Files a model names: textures, materials and the files their stages and surfaces use, proxy models. Material marks
    // an rvmat, which the model carries inside.
    public static IEnumerable<(string Path, bool Material)> References(OdolModel m)
    {
        foreach (var l in m.Lods)
        {
            foreach (var t in l.Textures) yield return (t, false);
            foreach (var mat in l.Materials)
            {
                yield return (mat.Path, mat.Path.EndsWith(".rvmat", StringComparison.OrdinalIgnoreCase));
                if (mat.Surface.Length > 0) yield return (mat.Surface, false);
                foreach (var s in mat.Stages.Append(mat.Ti)) yield return (s.Texture, false);
            }
            foreach (var s in l.Sections) if (s.Surface.Length > 0) yield return (s.Surface, false);
            foreach (var p in l.Proxies) yield return (p.Name + ".p3d", false);
        }
    }

    private static string Save(string path, byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
        return path;
    }

    private static string Save(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }
}
