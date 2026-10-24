using System.IO;

namespace NDZ.PBOGPacker.Core;

// The GRAFT runtime the NDZ launcher keeps up to date (graft.exe, hid.dll, .version). The launcher
// passes its folder in NDZ_GRAFT_DIR; a tool installed by the launcher also finds it two levels up
// (<launcher>\tools\pbog\ -> <launcher>\graft). Its version is the one the owner published and tested,
// so it drives the ABI checks, the TAG of new C++ projects and the texts, instead of a hardcoded pin.
public static class GraftRuntime
{
    public const string Pinned = "v0.4.0";
    public const string DirEnv = "NDZ_GRAFT_DIR";
    private const string VersionFile = ".version";

    public static string? Dir
    {
        get
        {
            foreach (var d in Candidates())
                if (d != null && File.Exists(Path.Combine(d, "graft.exe"))) return d;
            return null;
        }
    }

    private static IEnumerable<string?> Candidates()
    {
        yield return Environment.GetEnvironmentVariable(DirEnv);
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (exeDir != null) yield return Path.GetFullPath(Path.Combine(exeDir, "..", "..", "graft"));
    }

    public static string? GraftExe => Dir is { } d ? Path.Combine(d, "graft.exe") : null;

    public static string? HostDll => Dir is { } d && File.Exists(Path.Combine(d, "hid.dll")) ? Path.Combine(d, "hid.dll") : null;

    // Version of the managed runtime; without one (dev machine, manual paths) the pin the tool was built for.
    public static string Version
    {
        get
        {
            try
            {
                if (Dir is { } d && File.Exists(Path.Combine(d, VersionFile)))
                {
                    var v = File.ReadAllText(Path.Combine(d, VersionFile)).Trim();
                    if (v.Length > 0) return v;
                }
            }
            catch (IOException) { }
            return Pinned;
        }
    }
}
