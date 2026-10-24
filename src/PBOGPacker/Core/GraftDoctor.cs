using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace NDZ.PBOGPacker.Core;

// Is GRAFT really in place on a server: the host, the build it was made for, what the start script
// lets it see, the plugins it would load, what graft.exe doctor says and what the last start logged.
public static class GraftDoctor
{
    public const string TargetBuild = "1.29.0.163709";
    public static string Release => GraftRuntime.Version;
    private const string Host = "hid.dll";
    private const string OldHost = "dwmapi.dll";
    private const string HostMark = "graft_host_version";     // export only the GRAFT host has
    private const string PluginMark = "graft_plugin_entry";   // GRAFT_PLUGIN_ENTRY_NAME

    // The cheap part, also used to keep the server card honest without a full check.
    public static HostInfo Probe(string serverDir, string? toolHost)
    {
        var dll = Path.Combine(serverDir, Host);
        if (!File.Exists(dll)) return new(false, null, "none");
        if (!Contains(dll, HostMark)) return new(false, null, "foreign");
        if (toolHost != null && File.Exists(toolHost) && SameFile(dll, toolHost)) return new(true, Release, "ok");
        return new(true, null, "other");
    }

    public static List<GraftCheck> Run(ServerInfo srv, string? bat, string? toolHost, string? graftExe)
    {
        var list = new List<GraftCheck>();
        var dir = srv.Path;
        var hostPath = Path.Combine(dir, Host);
        var host = Probe(dir, toolHost);
        var running = ServerLaunch.IsRunning(dir);

        switch (host.State)
        {
            case "none": list.Add(new("HostNone", "err", [], "install")); break;
            case "foreign": list.Add(new("HostForeign", "err", [hostPath], "open", dir)); break;
            case "ok": list.Add(new("HostOk", "ok", [Release, hostPath], "uninstall")); break;
            default:
                list.Add(toolHost == null
                    ? new("HostNoTool", "warn", [hostPath])
                    : new("HostOther", "warn", [Release], "install"));
                break;
        }
        var legacy = Path.Combine(dir, OldHost);
        if (File.Exists(legacy) && Contains(legacy, HostMark)) list.Add(new("HostLegacy", "warn", [legacy], "open", dir));
        if (running) list.Add(new("Running", "info", []));

        if (srv.Build is { Length: > 0 } b && b != "?")
            list.Add(b == TargetBuild ? new("BuildSame", "ok", [b, Release]) : new("BuildDiff", "warn", [b, Release, TargetBuild]));

        // What the host reads from the command line.
        LaunchArgs? args = null;
        if (bat == null) list.Add(new("NoBat", "warn", []));
        else
        {
            args = ServerMods.Launch(dir, bat);
            if (args.Error != null) list.Add(new("BatError", "warn", [Path.GetFileName(args.Bat), args.Error]));
            // A fully quoted switch is how DayZ takes paths with spaces; it only matters to the host when a
            // mod of that list carries a plugin. Hosts from v0.4.0 split the command line the way Windows does
            // and read such a switch, so only an older host is blind to it.
            var blind = 0;
            foreach (var key in ReadsQuoted(host) ? [] : args.Hidden)
            {
                var carriers = (key == "-mod" ? args.Mods : args.ServerMods).Select(m => Full(dir, m))
                    .Where(m => HasDlls(Path.Combine(m, "grafted"))).Select(m => Path.GetFileName(m.TrimEnd('\\'))).ToList();
                if (carriers.Count == 0) list.Add(new("ArgsQuoted", "info", [key]));
                else
                {
                    list.Add(new("QuoteBefore", "err", [key, string.Join(", ", carriers)]));
                    blind++;
                }
            }
            if (args.Error == null && blind == 0)
                list.Add(new("ArgsOk", "ok", [Path.GetFileName(args.Bat), args.Mods.Count.ToString(), args.ServerMods.Count.ToString()]));
        }

        // Plugins, looked up the way the host does: grafted\ of every listed mod, then <server>\grafted.
        var listed = new List<string>();
        if (args != null)
            foreach (var m in args.Mods.Concat(args.ServerMods))
                listed.Add(Full(dir, m));
        var plugins = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in listed)
            plugins += Plugins(Path.Combine(mod, "grafted"), "@" + Path.GetFileName(mod.TrimEnd('\\')).TrimStart('@'), list, seen);
        plugins += Plugins(Path.Combine(dir, "grafted"), "grafted", list, seen);
        try
        {
            foreach (var mod in Directory.GetDirectories(dir))
            {
                var g = Path.Combine(mod, "grafted");
                if (!Directory.Exists(g) || listed.Any(l => Same(l, mod))) continue;
                foreach (var dll in Directory.GetFiles(g, "*.dll"))
                    if (seen.Add(dll)) list.Add(new("PluginUnlisted", "warn", [Path.GetFileName(mod), Path.GetFileName(dll)], "mods"));
            }
        }
        catch (Exception) { }
        if (plugins == 0) list.Add(new("PluginsNone", "info", [], "plugin"));

        Doctor(dir, graftExe, plugins, list);
        Journal(dir, args?.Profiles, hostPath, host.Installed, list);
        return list;
    }

    // The server runs the tool's own host (same file), and that host is v0.4.0 or newer.
    private static bool ReadsQuoted(HostInfo host)
    {
        if (host.State != "ok") return false;
        var m = Regex.Match(Release, @"^v?(\d+)\.(\d+)");
        return m.Success && (int.Parse(m.Groups[1].Value) > 0 || int.Parse(m.Groups[2].Value) >= 4);
    }

    private static bool HasDlls(string dir)
    {
        try { return Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.dll").Any(); }
        catch (Exception) { return false; }
    }

    private static int Plugins(string grafted, string where, List<GraftCheck> list, HashSet<string> seen)
    {
        var n = 0;
        try
        {
            if (!Directory.Exists(grafted)) return 0;
            foreach (var dll in Directory.GetFiles(grafted, "*.dll").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                if (!seen.Add(dll)) continue;
                if (Contains(dll, PluginMark))
                {
                    list.Add(new("Plugin", "ok", [Path.GetFileName(dll), where], "open", grafted));
                    n++;
                }
                else list.Add(new("PluginNotGraft", "warn", [Path.GetFileName(dll), where], "open", grafted));
            }
        }
        catch (Exception) { }
        return n;
    }

    // graft.exe doctor opens every plugin against the host ABI and finds native name clashes — only it
    // can; the host and journal lines it prints are covered above (and it looks for the journal next to
    // the exe only, missing -profiles=).
    private static void Doctor(string dir, string? graftExe, int plugins, List<GraftCheck> list)
    {
        if (graftExe == null || !File.Exists(graftExe))
        {
            list.Add(new("DoctorMissing", "info", []));
            return;
        }
        var issues = new List<string>();
        try
        {
            var psi = new ProcessStartInfo(graftExe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            psi.ArgumentList.Add("doctor");
            psi.ArgumentList.Add(dir);
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEndAsync();
            var outp = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(20000))
            {
                try { p.Kill(true); } catch (Exception) { }
                list.Add(new("DoctorFailed", "warn", ["timeout"]));
                return;
            }
            foreach (var line in (outp.Result + "\n" + err.Result).Split('\n').Select(l => l.Trim()))
            {
                if (!line.StartsWith("[!]")) continue;
                var body = line[3..].Trim().TrimStart('!').Trim();
                if (body.Contains("хост не установлен") || body.Contains("не наша") || body.Contains(OldHost) || body.Contains(PluginMark)) continue;
                issues.Add(body);
            }
        }
        catch (Exception e)
        {
            list.Add(new("DoctorFailed", "warn", [e.Message]));
            return;
        }
        foreach (var i in issues) list.Add(new("DoctorIssue", "err", [i]));
        if (issues.Count == 0 && plugins > 0) list.Add(new("DoctorOk", "ok", [plugins.ToString()]));
    }

    // graft_<start>.log goes to the -profiles= folder (else next to the exe): one file per start.
    private static void Journal(string dir, string? profiles, string hostPath, bool installed, List<GraftCheck> list)
    {
        var where = new List<string>();
        if (profiles != null) where.Add(Full(dir, profiles));
        where.Add(dir);
        string? last = null;
        foreach (var d in where.Distinct(StringComparer.OrdinalIgnoreCase))
            try
            {
                if (!Directory.Exists(d)) continue;
                foreach (var f in Directory.GetFiles(d, "graft_*.log"))
                    if (last == null || string.CompareOrdinal(Path.GetFileName(f), Path.GetFileName(last)) > 0) last = f;
            }
            catch (Exception) { }

        if (last == null)
        {
            if (installed) list.Add(new("LogNone", "info", [where[0]]));
            return;
        }
        var started = File.GetCreationTime(last);
        var m = Regex.Match(Path.GetFileName(last), @"graft_(\d{4})-(\d\d)-(\d\d)_(\d\d)-(\d\d)-(\d\d)\.log$");
        if (m.Success)
            started = new DateTime(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value),
                int.Parse(m.Groups[4].Value), int.Parse(m.Groups[5].Value), int.Parse(m.Groups[6].Value));
        var when = started.ToString("dd.MM.yyyy HH:mm");
        string[] lines;
        try { lines = File.ReadAllLines(last); }
        catch (Exception e)
        {
            list.Add(new("LogUnreadable", "warn", [last, e.Message], "open", last));
            return;
        }
        static string Body(string l) { var i = l.IndexOf("| ", StringComparison.Ordinal); return i < 0 ? l : l[(i + 2)..]; }
        var bodies = lines.Select(Body).ToList();
        // Hosts before v0.3.0 logged in Russian ("graft armed", "нативов", host row "(хост)"), later ones in English
        // (host row "graft v0 … natives").
        var armed = bodies.Any(l => l.StartsWith("graft armed", StringComparison.Ordinal) ||
                                    l.StartsWith("engine points found", StringComparison.Ordinal));
        var loaded = bodies.Count(l => (l.Contains("нативов") || l.Contains(" natives ")) && !l.Contains("(хост)") &&
                                       Regex.IsMatch(l, @"^\s+\S+\s+v\d") && !Regex.IsMatch(l, @"^\s+graft\s+v0\s"));
        var complaints = bodies.Where(l => l.StartsWith('!')).Select(l => l[1..].Trim()).ToList();

        if (installed && File.Exists(hostPath) && File.GetLastWriteTime(hostPath) > started)
            list.Add(new("LogOld", "info", [Path.GetFileName(last), when, File.GetLastWriteTime(hostPath).ToString("dd.MM.yyyy HH:mm")], "open", last));
        if (armed) list.Add(new("LogArmed", "ok", [when, loaded.ToString()], "open", last));
        else list.Add(new("LogNotArmed", "err", [when], "open", last));
        if (complaints.Count > 0)
            list.Add(new("LogComplaints", "warn", [complaints.Count.ToString(), string.Join("; ", complaints.Take(3))], "open", last));
    }

    // A ready plugin goes to <server>\grafted: the host scans it on every start, whatever the -mod list.
    // Only a 64-bit DLL with the GRAFT entry point is taken; a running server keeps the old file locked.
    public static string InstallPlugin(string serverDir, string dll)
    {
        if (!File.Exists(dll)) throw new InvalidOperationException("файл не найден: " + dll);
        if (!dll.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("плагин GRAFT — это .dll");
        if (!Contains(dll, PluginMark)) throw new InvalidOperationException("в " + Path.GetFileName(dll) + " нет точки входа graft_plugin_entry — это не плагин GRAFT");
        if (!IsX64(dll)) throw new InvalidOperationException(Path.GetFileName(dll) + " собран не под x64 — сервер DayZ его не загрузит");
        var dir = Path.Combine(serverDir, "grafted");
        var target = Path.Combine(dir, Path.GetFileName(dll));
        if (Same(Path.GetFullPath(dll), Path.GetFullPath(target))) return target;
        if (File.Exists(target) && ServerLaunch.IsRunning(serverDir))
            throw new InvalidOperationException("сервер запущен и держит " + Path.GetFileName(target) + " — остановите его, потом заменяйте плагин");
        Directory.CreateDirectory(dir);
        File.Copy(dll, target, true);
        return target;
    }

    private static bool IsX64(string dll)
    {
        try
        {
            using var f = File.OpenRead(dll);
            using var r = new BinaryReader(f);
            f.Position = 0x3C;
            f.Position = r.ReadInt32() + 4;
            return r.ReadUInt16() == 0x8664;
        }
        catch (Exception) { return false; }
    }

    // Removes the host only when it is GRAFT's; an empty <server>\grafted goes with it, as graft uninstall does.
    public static void Uninstall(string serverDir)
    {
        var dll = Path.Combine(serverDir, Host);
        if (!File.Exists(dll)) return;
        if (!Contains(dll, HostMark)) throw new InvalidOperationException("hid.dll в папке сервера — не GRAFT, тулза её не трогает");
        if (ServerLaunch.IsRunning(serverDir)) throw new InvalidOperationException("сервер запущен — остановите его, потом снимайте хост");
        File.Delete(dll);
        var g = Path.Combine(serverDir, "grafted");
        try { if (Directory.Exists(g) && !Directory.EnumerateFileSystemEntries(g).Any()) Directory.Delete(g); }
        catch (Exception) { }
    }

    private static string Full(string dir, string p)
    {
        try { return Path.GetFullPath(Path.IsPathRooted(p) ? p : Path.Combine(dir, p)); }
        catch (Exception) { return p; }
    }

    private static bool Same(string a, string b) =>
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    // The export names are plain ASCII in the image; reading it as data never runs DllMain.
    private static bool Contains(string file, string mark)
    {
        try { return Encoding.Latin1.GetString(File.ReadAllBytes(file)).Contains(mark, StringComparison.Ordinal); }
        catch (Exception) { return false; }
    }

    private static bool SameFile(string a, string b)
    {
        try
        {
            if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
            return SHA256.HashData(File.ReadAllBytes(a)).AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(b)));
        }
        catch (Exception) { return false; }
    }
}
