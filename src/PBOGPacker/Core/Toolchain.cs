using System.Diagnostics;
using System.IO;

namespace PolyLab.PBOGPacker.Core;

// Resolves the external tools the pipeline drives. CfgConvert + AddonBuilder + DSSignFile (official
// DayZ Tools) do what pboProject does (rapify, pack, sign): Mikero pboProject is GUI-bound and won't run headless.
public static class Toolchain
{
    public static string DayZToolsRoot =>
        Environment.GetEnvironmentVariable("DAYZ_TOOLS")
        ?? @"C:\Program Files (x86)\Steam\steamapps\common\DayZ Tools";

    public static string? AddonBuilder => Exists(Path.Combine(DayZToolsRoot, @"Bin\AddonBuilder\AddonBuilder.exe"));
    public static string? CfgConvert => Exists(Path.Combine(DayZToolsRoot, @"Bin\CfgConvert\CfgConvert.exe"));
    public static string? DsSignFile => Exists(Path.Combine(DayZToolsRoot, @"Bin\DsUtils\DSSignFile.exe"));
    public static string? DsCreateKey => Exists(Path.Combine(DayZToolsRoot, @"Bin\DsUtils\DSCreateKey.exe"));

    public static string? CMake(ToolPaths t) => Exists(t.CMake)
        ?? Which("cmake.exe")
        ?? Exists(@"C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe");

    public static string? NinjaDir(ToolPaths t)
    {
        var cm = CMake(t);
        if (cm == null) return null;
        try
        {
            var root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(cm)!)!)!;
            var d = Path.Combine(root, "Ninja");
            return Directory.Exists(d) ? d : null;
        }
        catch (Exception) { return null; }
    }

    public static string? ClangDir =>
        Directory.Exists(@"C:\Program Files\LLVM\bin") ? @"C:\Program Files\LLVM\bin" : null;

    // The launcher-managed GRAFT wins: it is the release this tool version is built for. Paths from
    // the settings are the fallback for a tool run outside the launcher (dev machine, manual setup).
    public static string? Graft(ToolPaths t) => GraftRuntime.GraftExe ?? Exists(t.Graft);
    public static string? HostDll(ToolPaths t) => GraftRuntime.GraftExe != null ? GraftRuntime.HostDll : Exists(t.HostDll);

    // vcvars64.bat via vswhere so the build inherits MSVC's INCLUDE/LIB for clang-cl.
    public static string? VcVars()
    {
        var vswhere = @"C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe";
        if (!File.Exists(vswhere)) return null;
        var install = Capture(vswhere, "-latest -property installationPath")?.Trim().Split('\n').FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(install)) return null;
        var v = Path.Combine(install, @"VC\Auxiliary\Build\vcvars64.bat");
        return File.Exists(v) ? v : null;
    }

    private static string? Exists(string? p) => !string.IsNullOrEmpty(p) && File.Exists(p) ? p : null;

    private static string? Which(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try { var p = Path.Combine(dir.Trim(), exe); if (File.Exists(p)) return p; } catch (Exception) { }
        }
        return null;
    }

    private static string? Capture(string exe, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args)
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
            if (p == null) return null;
            var o = p.StandardOutput.ReadToEnd();
            p.WaitForExit(4000);
            return o;
        }
        catch (Exception) { return null; }
    }
}
