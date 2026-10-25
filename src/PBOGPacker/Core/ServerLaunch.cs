using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PolyLab.PBOGPacker.Core;

// Start scripts of a server folder, starting one, and whether its DayZServer_x64.exe is running.
public static class ServerLaunch
{
    public static IReadOnlyList<string> Scripts(string serverDir)
    {
        try
        {
            if (!Directory.Exists(serverDir)) return Array.Empty<string>();
            return Directory.GetFiles(serverDir, "*.bat").Select(f => Path.GetFileName(f)!)
                .OrderBy(Rank).ThenBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception) { return Array.Empty<string>(); }
    }

    // The usual start script first: ServerStart.bat, then anything that says start/run.
    private static int Rank(string name) =>
        name.Equals("ServerStart.bat", StringComparison.OrdinalIgnoreCase) ? 0
        : name.Contains("start", StringComparison.OrdinalIgnoreCase) ? 1
        : name.Contains("run", StringComparison.OrdinalIgnoreCase) ? 2 : 3;

    public static string? Pick(ServerInfo s, string? wanted = null)
    {
        foreach (var name in new[] { wanted, s.StartBat })
            if (!string.IsNullOrEmpty(name) && File.Exists(Path.Combine(s.Path, Path.GetFileName(name))))
                return Path.GetFileName(name);
        return Scripts(s.Path).FirstOrDefault();
    }

    // Runs the script in its own console window with the server folder as working directory. Opened
    // from a launcher that keeps its tools in a kill-on-close job, this process sits in that job and so
    // would every child: the server would die with the launcher. WMI creates the process outside any job (and
    // without the launcher's environment), so that path is used whenever we are inside one.
    // Returns the pid of the script's cmd.exe, 0 when unknown.
    public static int Start(string serverDir, string bat)
    {
        var file = Path.Combine(serverDir, bat);
        if (InJob())
        {
            var pid = StartOutsideJob("cmd.exe /c \"\"" + file + "\"\"", serverDir);
            if (pid > 0) return pid;
        }
        using var p = Process.Start(new ProcessStartInfo(file) { UseShellExecute = true, WorkingDirectory = serverDir });
        return p?.Id ?? 0;
    }

    public static bool InJob() => IsProcessInJob(Process.GetCurrentProcess().Handle, IntPtr.Zero, out var inJob) && inJob;

    private static int StartOutsideJob(string commandLine, string dir)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
            if (type == null) return 0;
            dynamic locator = Activator.CreateInstance(type)!;
            dynamic wmi = locator.ConnectServer(".", @"root\cimv2");
            dynamic startup = wmi.Get("Win32_ProcessStartup").SpawnInstance_();
            startup.ShowWindow = 1;
            dynamic cls = wmi.Get("Win32_Process");
            dynamic input = cls.Methods_.Item("Create").InParameters.SpawnInstance_();
            input.CommandLine = commandLine;
            input.CurrentDirectory = dir;
            input.ProcessStartupInformation = startup;
            dynamic output = cls.ExecMethod_("Create", input);
            return (int)output.ReturnValue == 0 ? Convert.ToInt32(output.ProcessId) : 0;
        }
        catch (Exception) { return 0; }
    }

    // A cmd.exe executing this very script (double-clicked, or started by us through WMI): both put the
    // full path on the command line. Run from an open console with a relative name it stays unseen.
    public static bool ScriptRunning(string bat)
    {
        foreach (var p in Process.GetProcessesByName("cmd"))
            using (p)
                if (CommandLine(p.Id) is { } line && line.Contains(bat, StringComparison.OrdinalIgnoreCase))
                    return true;
        return false;
    }

    // ProcessCommandLineInformation (60): a UNICODE_STRING header followed by the text, same-user access is enough.
    private static string? CommandLine(int pid)
    {
        var h = OpenProcess(0x1000, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            NtQueryInformationProcess(h, 60, IntPtr.Zero, 0, out var need);
            if (need <= 0) return null;
            var buf = Marshal.AllocHGlobal(need);
            try
            {
                if (NtQueryInformationProcess(h, 60, buf, need, out _) != 0) return null;
                var len = (ushort)Marshal.ReadInt16(buf);
                var text = Marshal.ReadIntPtr(buf, IntPtr.Size);
                return text == IntPtr.Zero ? null : Marshal.PtrToStringUni(text, len / 2);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch (Exception) { return null; }
        finally { CloseHandle(h); }
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, IntPtr info, int length, out int returned);

    public static bool IsAlive(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (Exception) { return false; }
    }

    public static bool IsRunning(string serverDir) => ServerPids(serverDir).Count > 0;

    // Compares files, not path strings: a server registered as P:\... (subst), through a junction or
    // at a drive root still matches the image path Windows reports.
    public static List<int> ServerPids(string serverDir)
    {
        var exe = Path.Combine(serverDir, "DayZServer_x64.exe");
        var id = FileId(exe);
        var dir = Normalize(serverDir);
        var pids = new List<int>();
        foreach (var p in Process.GetProcessesByName("DayZServer_x64"))
            using (p)
            {
                var image = ImagePath(p.Id);
                if (image == null) continue;
                if (id != null ? FileId(image) == id : string.Equals(Normalize(Path.GetDirectoryName(image)!), dir, StringComparison.OrdinalIgnoreCase))
                    pids.Add(p.Id);
            }
        return pids;
    }

    // Stopping: the start scripts of the folder go first, so a restart loop (goto start) cannot bring the
    // server back; then every server window gets WM_CLOSE, the same as its close button, and DayZ saves
    // the world and exits. A server still up after the grace time (or one without a window) is killed.
    public static async Task<(int Scripts, int Servers, bool Forced)> StopAsync(string serverDir, int scriptPid, TimeSpan grace)
    {
        var scripts = KillScripts(serverDir, scriptPid);
        var pids = ServerPids(serverDir);
        if (pids.Count == 0) return (scripts, 0, false);
        var windows = pids.Sum(CloseWindows);
        var clock = Stopwatch.StartNew();
        while (windows > 0 && clock.Elapsed < grace && ServerPids(serverDir).Count > 0) await Task.Delay(500);
        var left = ServerPids(serverDir);
        var killed = 0;
        foreach (var pid in left)
        {
            Process p;
            try { p = Process.GetProcessById(pid); }
            catch (ArgumentException) { continue; }
            using (p)
            {
                if (p.HasExited) continue;
                p.Kill();
                p.WaitForExit(10000);
                killed++;
            }
        }
        return (scripts, pids.Count, killed > 0);
    }

    // cmd.exe running one of the folder's .bat files: started by us (the pid) or double-clicked (the full
    // path is on its command line). Only the script dies; a server it started with "start" keeps running.
    private static int KillScripts(string serverDir, int scriptPid)
    {
        var bats = Scripts(serverDir).Select(b => Path.Combine(serverDir, b)).ToList();
        var n = 0;
        foreach (var p in Process.GetProcessesByName("cmd"))
            using (p)
            {
                var line = p.Id == scriptPid ? "" : CommandLine(p.Id);
                if (line == null || (line.Length > 0 && !bats.Any(b => line.Contains(b, StringComparison.OrdinalIgnoreCase)))) continue;
                try
                {
                    p.Kill();
                    n++;
                }
                catch (Exception) { }
            }
        return n;
    }

    private static int CloseWindows(int pid)
    {
        var n = 0;
        EnumWindows((h, _) =>
        {
            if (GetWindowThreadProcessId(h, out var owner) != 0 && owner == pid && IsWindowVisible(h) && PostMessage(h, WmClose, IntPtr.Zero, IntPtr.Zero))
                n++;
            return true;
        }, IntPtr.Zero);
        return n;
    }

    private const uint WmClose = 0x0010;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static string Normalize(string p)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)); }
        catch (Exception) { return p; }
    }

    private static (uint, uint, uint)? FileId(string path)
    {
        try
        {
            using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return GetFileInformationByHandle(h, out var info)
                ? (info.VolumeSerialNumber, info.FileIndexHigh, info.FileIndexLow)
                : null;
        }
        catch (Exception) { return null; }
    }

    // PROCESS_QUERY_LIMITED_INFORMATION also works for a server started elevated (MainModule throws).
    private static string? ImagePath(int pid)
    {
        var h = OpenProcess(0x1000, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            var size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
        }
        finally { CloseHandle(h); }
    }

    // FILETIME is two DWORDs, 4-byte aligned: Pack = 4 keeps the native layout.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation info);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
