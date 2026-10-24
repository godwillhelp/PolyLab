using System.IO;

namespace NDZ.PBOGPacker.Core;

// Where a mod is packed from and where its PBO lands. Shared by the backend and the UI so the build
// page shows exactly the folders the pipeline writes. A destination is a mod root laid out like
// pboProject output and the GRAFT README: <root>\addons\<Name>.pbo + keys\ (+ grafted\ for GRAFT).
public static class PackLayout
{
    // GRAFT keeps the mod in mod\<Name>; a plain mod folder has config.cpp at its root.
    public static string SourceDir(ModInfo m)
    {
        var nested = Path.Combine(m.Path, "mod", m.Name);
        return File.Exists(Path.Combine(nested, "config.cpp")) ? nested : m.Path;
    }

    public static string? FindNestedMod(string project)
    {
        var modRoot = Path.Combine(project, "mod");
        if (!Directory.Exists(modRoot)) return null;
        foreach (var d in Directory.GetDirectories(modRoot))
            if (File.Exists(Path.Combine(d, "config.cpp"))) return Path.GetFileName(d);
        return null;
    }

    public static bool IsGraftProject(string project) =>
        File.Exists(Path.Combine(project, "CMakeLists.txt")) && FindNestedMod(project) != null;

    // The C++ side of a mod: the mod folder itself when it is a GRAFT project (mod\<Name> inside), or a
    // project linked to a plain mod that stays where it is (Cpp, made by "Create C++ project").
    public static bool HasProject(ModInfo m) =>
        IsGraftProject(m.Path) || (m.Cpp != null && File.Exists(Path.Combine(m.Cpp, "CMakeLists.txt")));

    public static string ProjectDir(ModInfo m) =>
        m.Cpp != null && !IsGraftProject(m.Path) && File.Exists(Path.Combine(m.Cpp, "CMakeLists.txt")) ? m.Cpp : m.Path;

    public static string Configured(ModInfo m, PackSettings p)
    {
        if (!string.IsNullOrWhiteSpace(m.Out)) return m.Out!;
        return m.Kind switch
        {
            ModKinds.Client => p.ClientOut,
            ModKinds.Server => p.ServerOut,
            // A Grafted mod is a server mod too: without a folder of its own kind it takes the server one.
            _ => string.IsNullOrWhiteSpace(p.GraftedOut) ? p.ServerOut : p.GraftedOut,
        };
    }

    // Which common folder a mod of this kind follows, so "make it common" writes where it will be read.
    public static string CommonSlot(ModInfo m, PackSettings p) => m.Kind switch
    {
        ModKinds.Client => nameof(PackSettings.ClientOut),
        ModKinds.Server => nameof(PackSettings.ServerOut),
        _ => string.IsNullOrWhiteSpace(p.GraftedOut) ? nameof(PackSettings.ServerOut) : nameof(PackSettings.GraftedOut),
    };

    public static void SetCommon(PackSettings p, string slot, string dir)
    {
        switch (slot)
        {
            case nameof(PackSettings.ClientOut): p.ClientOut = dir; break;
            case nameof(PackSettings.ServerOut): p.ServerOut = dir; break;
            default: p.GraftedOut = dir; break;
        }
    }

    public static string DefaultOut(ModInfo m)
    {
        if (SourceDir(m) != m.Path) return Path.Combine(m.Path, "dist", "@" + m.Name);
        var parent = Path.GetDirectoryName(m.Path.TrimEnd('\\', '/'));
        return Path.Combine(string.IsNullOrEmpty(parent) ? m.Path : parent, "@" + m.Name);
    }

    // A game folder (or its addons) is never used as a mod root: the mod gets its own @Name inside it,
    // the separate @folder the GRAFT README describes, instead of landing in the game's own addons.
    public static string OutRoot(ModInfo m, PackSettings p)
    {
        var raw = Configured(m, p);
        if (string.IsNullOrWhiteSpace(raw)) return DefaultOut(m);
        var root = NormalizeRoot(raw);
        return IsGameRoot(root) ? Path.Combine(root, "@" + m.Name) : root;
    }

    // A pack destination must not be mod sources: a folder with config.cpp, or anything inside a mod the
    // tool knows. Returns the name of the mod it would pack into, null when the folder is fine.
    public static string? SourcesOf(string dir, IEnumerable<ModInfo> mods)
    {
        var root = NormalizeRoot(dir);
        foreach (var m in mods)
            if (IsUnder(root, SourceDir(m))) return m.Name;
        return File.Exists(Path.Combine(root, "config.cpp")) ? Path.GetFileName(root) : null;
    }

    public static bool IsRooted(string raw) =>
        Path.IsPathRooted(Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"')));

    // Accepts both the mod root and its addons folder: "...\@MyAddon\addons" means "...\@MyAddon".
    public static string NormalizeRoot(string raw)
    {
        var p = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'));
        try { p = Path.GetFullPath(p); } catch (Exception) { }
        if (p.Length > 3) p = p.TrimEnd('\\', '/');
        if (string.Equals(Path.GetFileName(p), "addons", StringComparison.OrdinalIgnoreCase))
            p = Path.GetDirectoryName(p) ?? p;
        return p;
    }

    public static string LaunchKey(string kind) => kind == ModKinds.Client ? "-mod=" : "-serverMod=";

    public static bool IsGameRoot(string dir) =>
        File.Exists(Path.Combine(dir, "DayZServer_x64.exe")) || File.Exists(Path.Combine(dir, "DayZ_x64.exe"));

    // True when path is root itself or lies inside it.
    public static bool IsUnder(string path, string root)
    {
        try
        {
            var a = Path.GetFullPath(path).TrimEnd('\\', '/') + "\\";
            var b = Path.GetFullPath(root).TrimEnd('\\', '/') + "\\";
            return a.StartsWith(b, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) { return false; }
    }

    // $PBOPREFIX$ holds either the bare prefix or key=value lines; Mikero ExtractPbo ends them with ';'.
    public static string? ReadPrefix(string src)
    {
        foreach (var name in new[] { "$PBOPREFIX$", "$PBOPREFIX$.txt", "$PREFIX$" })
        {
            var f = Path.Combine(src, name);
            if (!File.Exists(f)) continue;
            try
            {
                foreach (var line in File.ReadAllLines(f))
                {
                    var t = line.Trim();
                    if (t.Length == 0 || t.StartsWith("//")) continue;
                    var eq = t.IndexOf('=');
                    if (eq >= 0 && !t[..eq].Trim().Equals("prefix", StringComparison.OrdinalIgnoreCase)) continue;
                    var v = CleanPrefix(eq < 0 ? t : t[(eq + 1)..]);
                    if (v.Length > 0) return v;
                }
            }
            catch (Exception) { }
        }
        return null;
    }

    private static string CleanPrefix(string v) =>
        string.Join('\\', v.Trim().TrimEnd(';').Trim().Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries));

    // The folder addon paths are counted from: P:\ itself, or the folder holding the extracted game
    // data (P:\ is usually a subst of it). AddonBuilder and pboProject derive a missing prefix from it.
    public static string? WorkRoot(string src)
    {
        if (IsUnder(src, @"P:\")) return @"P:\";
        for (var d = Path.GetDirectoryName(src); !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d))
            if (File.Exists(Path.Combine(d, "DZ", "data", "config.cpp"))) return d;
        return null;
    }

    // $PBOPREFIX$ first; a GRAFT mod is named by its folder; a plain mod under the work root gets its
    // path relative to it (pboProject convention); anything else its folder name.
    public static (string Prefix, string From) Prefix(ModInfo m)
    {
        var src = SourceDir(m);
        var file = ReadPrefix(src);
        if (file != null) return (file, "$PBOPREFIX$");
        if (src == m.Path && WorkRoot(src) is { } work)
        {
            var rel = Path.GetRelativePath(work, src);
            if (rel != "." && !rel.StartsWith("..") && !Path.IsPathRooted(rel)) return (CleanPrefix(rel), work);
        }
        return (m.Name, "имя папки");
    }

    public static bool IsPrefixFile(string fileName) =>
        fileName.Equals("$PBOPREFIX$", StringComparison.OrdinalIgnoreCase)
        || fileName.Equals("$PBOPREFIX$.txt", StringComparison.OrdinalIgnoreCase)
        || fileName.Equals("$PREFIX$", StringComparison.OrdinalIgnoreCase);
}
