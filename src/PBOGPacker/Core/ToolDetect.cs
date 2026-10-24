using System.Diagnostics;
using System.IO;

namespace NDZ.PBOGPacker.Core;

// Real environment detection for the "Окружение" tab: probes the actual toolchain on this machine
// (versions where a tool answers --version, presence otherwise) instead of the mocked list. Pure
// read-only; never launches builds. Version parsing is best-effort - a present-but-unparsed tool is
// still reported Ok with "ok", a missing one is Err with a fix hint.
public static class ToolDetect
{

    public static async Task<List<DoctorItem>> DetectAsync(ToolPaths t, bool rapify = true)
    {
        var items = new List<DoctorItem>();

        // clang-cl: on PATH or the default LLVM location.
        var clang = Which("clang-cl.exe") ?? Exists(@"C:\Program Files\LLVM\bin\clang-cl.exe");
        var clangVer = clang != null ? Ver(clang, "--version", "clang version ") : null;
        items.Add(new("clang", "clang-cl", null, clang != null ? CheckState.Ok : CheckState.Err,
            clangVer ?? "", clang ?? "", "Fix.Clang", "winget install LLVM.LLVM"));

        // cmake: settings path, PATH, or the VS-bundled one.
        var cmake = FirstFile(t.CMake, Which("cmake.exe"),
            @"C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe");
        items.Add(new("cmake", "CMake", null, cmake != null ? CheckState.Ok : CheckState.Err,
            cmake != null ? Ver(cmake, "--version", "cmake version ") ?? "ok" : "", cmake ?? "",
            cmake == null ? "Fix.CMake" : null, cmake == null ? "winget install Kitware.CMake" : null));

        // ninja: PATH or bundled next to the VS cmake.
        var ninja = Which("ninja.exe") ?? (cmake != null ? SiblingNinja(cmake) : null);
        items.Add(new("ninja", "Ninja", null, ninja != null ? CheckState.Ok : CheckState.Warn,
            ninja != null ? Ver(ninja, "--version", "") ?? "ok" : "", ninja ?? ""));

        // Visual Studio + Windows SDK (via vswhere).
        var (vsPath, vsVer) = VsWhere();
        items.Add(new("vs", null, "Env.Vs", vsPath != null ? CheckState.Ok : CheckState.Err,
            vsVer ?? "", vsPath ?? "", vsPath == null ? "Fix.Vs" : null, null));

        // AddonBuilder + CfgConvert (DayZ Tools) pack and rapify every kind of mod, pboProject-style.
        var ab = Toolchain.AddonBuilder;
        items.Add(new("pbo", "AddonBuilder · DayZ Tools", null, ab != null ? CheckState.Ok : CheckState.Err,
            ab != null ? FileVer(ab) ?? "ok" : "", ab ?? "", ab == null ? "Fix.Pbo" : null, null));
        var cfg = Toolchain.CfgConvert;
        items.Add(new("cfgconv", "CfgConvert · DayZ Tools", null, cfg != null ? CheckState.Ok : rapify ? CheckState.Err : CheckState.Warn,
            cfg != null ? "ok" : "", cfg ?? "", cfg == null ? "Fix.Pbo" : null, null));

        // DSSignFile from DayZ Tools (env DAYZ_TOOLS or the default Steam path).
        var dayzTools = Environment.GetEnvironmentVariable("DAYZ_TOOLS");
        var dssign = FirstFile(
            dayzTools != null ? Path.Combine(dayzTools, @"Bin\DsUtils\DSSignFile.exe") : null,
            @"C:\Program Files (x86)\Steam\steamapps\common\DayZ Tools\Bin\DsUtils\DSSignFile.exe");
        items.Add(new("dssign", "DSSignFile", null, dssign != null ? CheckState.Ok : CheckState.Warn,
            dssign != null ? "ok" : "", dssign ?? ""));

        // graft.exe carries no --version flag and no file version info, so the pin is owner-tracked.
        // Cross-reference the co-shipped hid.dll version when readable; otherwise report presence.
        var expected = GraftDoctor.Release;
        var graft = Toolchain.Graft(t);
        var graftVer = graft != null ? FileVer(graft) : null;
        var graftState = graft == null ? CheckState.Err
            : graftVer != null && !graftVer.Contains(expected) ? CheckState.Warn : CheckState.Ok;
        items.Add(new("graft", "graft.exe", null, graftState,
            graftVer ?? (graft != null ? expected : ""), graft ?? "", "Fix.Graft",
            "https://github.com/KRdayzmodding/KR_GRAFTED/releases/tag/" + expected, true));

        // host hid.dll for the server.
        var host = Toolchain.HostDll(t);
        items.Add(new("host", null, "Env.HostTool", host != null ? CheckState.Ok : CheckState.Warn,
            host != null ? FileVer(host) ?? expected : "", host ?? ""));

        // P: work-drive: only model binarization reads the game data (P:\DZ) from it.
        var pdrive = Directory.Exists(@"P:\");
        items.Add(new("pdrive", null, "Env.PDrive", pdrive ? CheckState.Ok : CheckState.Warn,
            pdrive ? @"P:\" : "", "", pdrive ? null : "Fix.PDrive", "subst P: <folder>"));

        await Task.CompletedTask;
        return items;
    }

    private static string? Exists(string p) => File.Exists(p) ? p : null;

    private static string? FirstFile(params string?[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrEmpty(c) && File.Exists(c));

    private static string? Which(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try { var p = Path.Combine(dir.Trim(), exe); if (File.Exists(p)) return p; }
            catch (Exception) { }
        }
        return null;
    }

    private static string? SiblingNinja(string cmakeExe)
    {
        try
        {
            var root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(cmakeExe)!)!)!;
            var n = Path.Combine(root, "Ninja", "ninja.exe");
            return File.Exists(n) ? n : null;
        }
        catch (Exception) { return null; }
    }

    private static string? Ver(string exe, string arg, string prefix)
    {
        var outp = Capture(exe, arg);
        if (outp == null) return null;
        var line = outp.Split('\n').FirstOrDefault(l => l.Contains(prefix, StringComparison.OrdinalIgnoreCase)) ?? outp.Split('\n')[0];
        line = line.Trim();
        if (prefix.Length > 0)
        {
            var i = line.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (i >= 0) line = line[(i + prefix.Length)..].Trim();
        }
        var token = line.Split(' ', '(', ',')[0].Trim();
        return string.IsNullOrEmpty(token) ? null : token;
    }

    private static string? FileVer(string path)
    {
        try { var v = FileVersionInfo.GetVersionInfo(path).ProductVersion ?? FileVersionInfo.GetVersionInfo(path).FileVersion; return string.IsNullOrWhiteSpace(v) ? null : v; }
        catch (Exception) { return null; }
    }

    private static (string? path, string? ver) VsWhere()
    {
        var vswhere = @"C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe";
        if (!File.Exists(vswhere)) return (null, null);
        var outp = Capture(vswhere, "-latest -property installationPath");
        var path = outp?.Trim().Split('\n').FirstOrDefault()?.Trim();
        var ver = Capture(vswhere, "-latest -property catalog_productDisplayVersion")?.Trim().Split('\n').FirstOrDefault()?.Trim();
        return (string.IsNullOrEmpty(path) ? null : path, string.IsNullOrEmpty(ver) ? null : ver);
    }

    private static string? Capture(string exe, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p == null) return null;
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(4000)) { try { p.Kill(true); } catch (Exception) { } return null; }
            return string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
        }
        catch (Exception) { return null; }
    }
}
