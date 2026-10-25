using System.IO.Compression;
using System.Text.Json;

namespace PolyLab.Launcher.Tests;

// POLYLAB_E2E = 1: start the built PolyLab.exe (a window) against the fake. POLYLAB_NET = 1: read the real GRAFT upstream.
public sealed class EnvFactAttribute : FactAttribute
{
    public EnvFactAttribute(string variable)
    {
        if (Environment.GetEnvironmentVariable(variable) != "1")
            Skip = variable + " is not 1";
    }
}

public sealed class Scratch : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "polylab-tests", Guid.NewGuid().ToString("N")[..12]);

    public Scratch() => Directory.CreateDirectory(Dir);

    public string Sub(string name) => Directory.CreateDirectory(Path.Combine(Dir, name)).FullName;

    public void Dispose()
    {
        try { Directory.Delete(Dir, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public static class Packages
{
    public static string RepoRoot { get; } = FindRoot();

    private static string FindRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "PolyLab.slnx"))) return d.FullName;
        throw new DirectoryNotFoundException("PolyLab.slnx above " + AppContext.BaseDirectory);
    }

#if DEBUG
    public const string Config = "Debug";
#else
    public const string Config = "Release";
#endif

    // The tool exe as the solution build left it in this configuration.
    public static string BuildOutput(string project, string exe)
    {
        var bin = Path.Combine(RepoRoot, project, "bin", Config);
        return Directory.EnumerateFiles(bin, exe, SearchOption.AllDirectories).OrderBy(p => p.Length).Select(Path.GetDirectoryName).FirstOrDefault()
            ?? throw new FileNotFoundException($"{exe} under {bin}: build the solution first");
    }

    public static string Manifest(string id, string version, string exe, string? graft = null) =>
        JsonSerializer.Serialize(graft == null ? new { id, version, exe } : (object)new { id, version, exe, graft });

    // A release zip: every file of a folder at the zip root plus tool.json.
    public static byte[] FromFolder(string folder, string manifest)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            foreach (var f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                zip.CreateEntryFromFile(f, Path.GetRelativePath(folder, f).Replace('\\', '/'), CompressionLevel.Fastest);
            using var w = new StreamWriter(zip.CreateEntry("tool.json").Open());
            w.Write(manifest);
        }
        return ms.ToArray();
    }

    public static byte[] FromFiles(params (string Name, byte[] Data)[] files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (name, data) in files)
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(data);
            }
        return ms.ToArray();
    }

    public static byte[] Text(string s) => System.Text.Encoding.UTF8.GetBytes(s);
}
