using System.Text;
using Pfx.Core.Export;
using Pfx.Core.Model;

namespace Pfx.Link;

// A part written for the game: its game path, the files this staging added, and references that did not resolve.
public sealed record StagedPart(string GamePath, int NewFiles, long NewBytes, IReadOnlyList<ExportMessage> Missing);

// The reload path of the live link (spec 3.12, P0 facts in 2.12): DayZDiag with -filePatching reads loose files from the
// game folder, and caches .ptc / .emat / .edds by path, so each version of a part goes under a new path. Every file is
// named after its bytes (AssetCopy content names): a changed part, material or texture gets a new path, an unchanged
// one keeps its path and the engine's cached copy. One folder per tool session, `<game>\PolyLab_PfxLink\live\<session>`,
// held by an open lock file and deleted when the session ends; folders of sessions that ended without cleaning up
// (their lock is free) are deleted at the next start.
public sealed class LiveStage : IDisposable
{
    public const string Root = "PolyLab_PfxLink/live";
    private const string LockName = ".lock";

    private readonly FileStream _lock;

    public LiveStage(string gameFolder)
    {
        GameFolder = gameFolder;
        Session = "s" + DateTime.Now.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "_" + Environment.ProcessId;
        Folder = Path.Combine(gameFolder, Root.Replace('/', Path.DirectorySeparatorChar), Session);
        Directory.CreateDirectory(Folder);
        _lock = new FileStream(Path.Combine(Folder, LockName), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
    }

    public string GameFolder { get; }
    public string Session { get; }
    public string Folder { get; }
    public string GamePath => Root + "/" + Session;
    public int Versions { get; private set; }
    public int FilesWritten { get; private set; }
    public long BytesWritten { get; private set; }

    // Writes what is missing of this version and returns the game path of its .ptc.
    public StagedPart Stage(string part, byte[] ptc, IExportSource source)
    {
        var plan = new ExportPlan(Path.Combine(Folder, LockName));
        var copy = new AssetCopy(source, plan, GamePath, "", contentNames: true);
        byte[] bytes = copy.Part(ptc, part);
        string name = ProjectNames.Id(part) + "_" + AssetCopy.ContentHash(bytes) + ".ptc";
        plan.Add(name, bytes, "part");
        int files = 0;
        long size = 0;
        foreach (var f in plan.Files.Where(f => !File.Exists(f.Path)))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(f.Path)!);
            SafeFile.Write(f.Path, f.Bytes);
            files++;
            size += f.Bytes.Length;
        }
        Versions++;
        FilesWritten += files;
        BytesWritten += size;
        return new StagedPart(GamePath + "/" + name, files, size, plan.Errors.ToList());
    }

    // A cheap fingerprint of what a part references outside its own text: the bytes of each non-vanilla material (open
    // material documents with their unsaved edits) and the length and time of every texture. A different stamp means
    // the part must be reloaded even when its own text did not change.
    public static string Stamp(byte[] ptc, IExportSource source)
    {
        var sb = new StringBuilder();
        foreach (var game in AssetCopy.Refs(ptc).Where(r => !AssetCopy.IsVanilla(r)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (source.Resolve(game) is not { } disk)
            {
                sb.Append(game).Append(":missing;");
                continue;
            }
            if (!disk.EndsWith(".emat", StringComparison.OrdinalIgnoreCase))
            {
                Append(sb, disk);
                continue;
            }
            byte[] emat = source.Read(disk);
            sb.Append(disk).Append(':').Append(AssetCopy.ContentHash(emat)).Append(';');
            foreach (var tex in AssetCopy.Refs(emat).Where(r => !AssetCopy.IsVanilla(r)))
            {
                if (source.Resolve(tex) is { } texDisk)
                    Append(sb, texDisk);
                else
                    sb.Append(tex).Append(":missing;");
            }
        }
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, string disk)
    {
        var info = new FileInfo(disk);
        sb.Append(disk).Append(':');
        if (info.Exists)
            sb.Append(info.Length).Append('@').Append(info.LastWriteTimeUtc.Ticks);
        sb.Append(';');
    }

    // Folders of other sessions whose lock file is free (the tool that made them is gone). Returns how many went.
    public static int RemoveStale(string gameFolder, string? keep = null)
    {
        string root = Path.Combine(gameFolder, Root.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(root))
            return 0;
        int removed = 0;
        foreach (var dir in Directory.GetDirectories(root, "s*"))
        {
            if (keep != null && string.Equals(Path.GetFileName(dir), keep, StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                string lockPath = Path.Combine(dir, LockName);
                if (File.Exists(lockPath))
                    File.Delete(lockPath);
                Directory.Delete(dir, true);
                removed++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        return removed;
    }

    public void Dispose()
    {
        _lock.Dispose();
        try
        {
            if (Directory.Exists(Folder))
                Directory.Delete(Folder, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}

// Where DayZ is installed: the user's setting, else the Steam library that holds steamapps\common\DayZ with a game exe
// (Steam's own registry key and library list, as PBOG Packer finds the workshop folder). Nothing assumes a drive or user.
public static class GameFolders
{
    public static readonly string[] Exes = { "DayZDiag_x64.exe", "DayZ_x64.exe" };

    public static string? Detect()
    {
        foreach (var lib in Libraries())
        {
            string dir = Path.Combine(lib, "steamapps", "common", "DayZ");
            if (IsGame(dir))
                return dir;
        }
        return null;
    }

    public static bool IsGame(string? dir) => !string.IsNullOrWhiteSpace(dir) && Exes.Any(e => File.Exists(Path.Combine(dir, e)));

    public static string? Diag(string? dir) => dir != null && File.Exists(Path.Combine(dir, Exes[0])) ? Path.Combine(dir, Exes[0]) : null;

    private static IEnumerable<string> Libraries()
    {
        if (Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is not string steam)
            yield break;
        steam = steam.Replace('/', '\\');
        yield return steam;
        string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        string text;
        try
        {
            text = File.ReadAllText(vdf);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, "\"path\"\\s*\"([^\"]+)\""))
            yield return m.Groups[1].Value.Replace(@"\\", @"\");
    }
}
