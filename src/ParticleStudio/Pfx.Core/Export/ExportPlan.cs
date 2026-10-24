using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Pfx.Core.Model;

namespace Pfx.Core.Export;

// What the export reads: game paths resolved under the user's data roots, and file bytes with the unsaved edits of open
// documents (the export writes what the user sees, saved or not).
public interface IExportSource
{
    string? Resolve(string gamePath);
    byte[] Read(string diskPath);
    // The game path of a folder on disk: relative to the data root that holds it (the work drive), else null.
    string? GamePath(string diskFolder);
}

public enum FileState { New, Changed, Same }

// Shared files (CG profile: pfx_cg.json, the showcase slot file) are rebuilt from every project that uses them, so no
// single project's marker owns them.
public sealed record PlannedFile(string Path, byte[] Bytes, FileState State, string What, bool Shared = false);

// A message for the export panel: a localisation key and its arguments (the core has no UI strings).
public sealed record ExportMessage(string Key, params object[] Args);

// Everything one export writes, compared with what is on disk, before anything is written (spec 3.13: the panel lists
// every file new / changed / unchanged). The tool owns exactly the files of its marker: re-export replaces or removes
// only those, never a file the user added, and refuses to overwrite a file it does not own.
public sealed class ExportPlan
{
    public ExportPlan(string marker)
    {
        Marker = marker;
    }

    public string Marker { get; }
    public List<PlannedFile> Files { get; } = new();
    public List<string> Remove { get; } = new();
    public List<ExportMessage> Errors { get; } = new();
    public List<ExportMessage> Notes { get; } = new();
    // How to start the effect from the user's own code (shown in the panel, never written).
    public string? Example { get; set; }

    public bool CanWrite => Errors.Count == 0;

    public static HashSet<string> Owned(string marker)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(marker))
            foreach (var line in File.ReadAllLines(marker))
                if (line.Trim().Length > 0)
                    set.Add(line.Trim());
        return set;
    }

    // Adds a file; `relative` is under the marker's folder.
    public void Add(string relative, byte[] bytes, string what)
    {
        string root = System.IO.Path.GetDirectoryName(Marker)!;
        string path = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)));
        Files.Add(new PlannedFile(path, bytes, StateOf(path, bytes), what));
    }

    public void AddShared(string path, byte[] bytes, string what) => Files.Add(new PlannedFile(path, bytes, StateOf(path, bytes), what, true));

    private static FileState StateOf(string path, byte[] bytes) =>
        !File.Exists(path) ? FileState.New : File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes) ? FileState.Same : FileState.Changed;

    // After every file is added: what the previous export owned and this one no longer makes, and files in the way
    // that the tool does not own.
    public void Finish()
    {
        string root = System.IO.Path.GetDirectoryName(Marker)!;
        var owned = Owned(Marker);
        var now = Files.Where(f => !f.Shared).Select(f => Rel(root, f.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Files.Where(f => !f.Shared))
            if (f.State != FileState.New && !owned.Contains(Rel(root, f.Path)))
                Errors.Add(new ExportMessage("Exp.NotOwned", f.Path));
        foreach (var old in owned.Where(o => !now.Contains(o)))
        {
            string path = System.IO.Path.Combine(root, old.Replace('/', System.IO.Path.DirectorySeparatorChar));
            if (File.Exists(path))
                Remove.Add(path);
        }
    }

    public static string Rel(string root, string path) => System.IO.Path.GetRelativePath(root, path).Replace('\\', '/');

    // The marker first lists the old and the new files (an interrupted export still owns everything it may have
    // written), then the files are written and the ones no longer made removed, then the marker is cut to the new list.
    public void Write()
    {
        if (!CanWrite)
            throw new InvalidOperationException("The export plan has errors.");
        string root = System.IO.Path.GetDirectoryName(Marker)!;
        var owned = Owned(Marker);
        foreach (var f in Files.Where(f => !f.Shared))
            owned.Add(Rel(root, f.Path));
        Directory.CreateDirectory(root);
        File.WriteAllLines(Marker, owned.OrderBy(o => o, StringComparer.Ordinal));
        foreach (var f in Files.Where(f => f.State != FileState.Same))
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(f.Path)!);
            SafeFile.Write(f.Path, f.Bytes);
        }
        foreach (var r in Remove)
            File.Delete(r);
        var keep = Files.Where(f => !f.Shared).Select(f => Rel(root, f.Path)).OrderBy(o => o, StringComparer.Ordinal);
        File.WriteAllText(Marker, string.Join("\n", keep) + "\n");
    }
}

// The parts' materials and textures copied under a new game path: every quoted reference of a .ptc or .emat that is not
// a vanilla path is resolved, copied once (by the file it resolves to) and rewritten. A Workbench resource GUID before
// a path ("{0123456789ABCDEF}path") is dropped with the old path, as assemble.py does.
public sealed class AssetCopy
{
    public static readonly Regex Ref = new("\"(\\{[0-9A-Fa-f]{16}\\})?([^\"\\r\\n]+?\\.(?:emat|edds|paa|dds|png|tga))\"",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    // Paths the game ships (the vanilla data the engine always has).
    public static readonly string[] Vanilla = { "graphics/", "dz/" };

    private readonly IExportSource _source;
    private readonly ExportPlan _plan;
    private readonly string _gameBase;
    private readonly string _diskBase;
    private readonly Dictionary<string, string> _byDisk = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _taken = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _contentNames;

    // gameBase: what the game sees for the folder the plan writes into (with '/'); diskBase: that folder relative to the
    // marker's folder ("" = the same). contentNames: every copy is named after its bytes (`<stem>_<hash>`), so a changed
    // file always gets a new path and an unchanged one keeps its path (the live link's versioned loose files: the game
    // caches these files by path, spec 2.12).
    public AssetCopy(IExportSource source, ExportPlan plan, string gameBase, string diskBase, bool contentNames = false)
    {
        _source = source;
        _plan = plan;
        _contentNames = contentNames;
        _gameBase = gameBase.TrimEnd('/') + "/";
        _diskBase = diskBase.Length == 0 ? "" : diskBase.TrimEnd('/') + "/";
    }

    public static bool IsVanilla(string gamePath)
    {
        string p = gamePath.Replace('\\', '/').TrimStart('/').ToLowerInvariant();
        return Vanilla.Any(v => p.StartsWith(v, StringComparison.Ordinal));
    }

    public static IEnumerable<string> Refs(byte[] bytes) => Ref.Matches(Encoding.Latin1.GetString(bytes)).Select(m => m.Groups[2].Value);

    // The part's text with every non-vanilla reference rewritten (materials copied on the way).
    public byte[] Part(byte[] ptc, string owner) => Rewrite(ptc, owner, Material);

    private string Material(string disk, string owner)
    {
        if (!disk.EndsWith(".emat", StringComparison.OrdinalIgnoreCase))
            return Texture(disk, owner);
        if (_byDisk.TryGetValue(disk, out var done))
            return done;
        byte[] bytes = Rewrite(_source.Read(disk), System.IO.Path.GetFileName(disk), Texture);
        string target = Place("materials", disk, bytes);
        _byDisk[disk] = target;
        _plan.Add(_diskBase + target, bytes, "material");
        return target;
    }

    private string Texture(string disk, string owner)
    {
        if (_byDisk.TryGetValue(disk, out var done))
            return done;
        byte[] bytes = _source.Read(disk);
        string target = Place("textures", disk, bytes);
        _byDisk[disk] = target;
        _plan.Add(_diskBase + target, bytes, "texture");
        return target;
    }

    private string Place(string folder, string disk, byte[] bytes)
    {
        string stem = ProjectNames.Id(System.IO.Path.GetFileNameWithoutExtension(disk));
        string ext = System.IO.Path.GetExtension(disk).ToLowerInvariant();
        if (_contentNames)
            return $"{folder}/{stem}_{ContentHash(bytes)}{ext}";
        string cand = $"{folder}/{stem}{ext}";
        for (int n = 2; _taken.Contains(cand); n++)
            cand = $"{folder}/{stem}_{n}{ext}";
        _taken.Add(cand);
        return cand;
    }

    // 12 hex digits of SHA-256: a collision among the few hundred versions of one session is out of reach.
    public static string ContentHash(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes), 0, 6).ToLowerInvariant();

    private byte[] Rewrite(byte[] bytes, string owner, Func<string, string, string> copy)
    {
        string text = Encoding.Latin1.GetString(bytes);
        string result = Ref.Replace(text, m =>
        {
            string game = m.Groups[2].Value;
            if (IsVanilla(game))
                return m.Value;
            if (_source.Resolve(m.Value.Trim('"')) is not { } disk)
            {
                _plan.Errors.Add(new ExportMessage("Exp.Missing", owner, game));
                return m.Value;
            }
            return "\"" + _gameBase + copy(disk, owner) + "\"";
        });
        return Encoding.Latin1.GetBytes(result);
    }
}

public static class ExportText
{
    public static string Num(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    public static string Vec(IReadOnlyList<float> v) => string.Join(" ", v.Take(3).Select(x => Num(x)));

    // Text files of generated code end lines with CRLF, as the vanilla and CG script files do.
    public static byte[] Crlf(string text) => Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n").Replace("\n", "\r\n"));

    public static bool IsPrefix(string prefix) => Regex.IsMatch(prefix, "^[A-Za-z][A-Za-z0-9_]*$");

    public static string Upper(string id) => id.ToUpperInvariant();

    // "zharka_burn" -> "Zharka_Burn" (sound class names like assemble.py's class_name).
    public static string Title(string id) => string.Join("_", id.Split('_').Select(p => p.Length == 0 ? p : char.ToUpperInvariant(p[0]) + p[1..]));
}
