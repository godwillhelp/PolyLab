using System.Text.Json;
using PolyLab.Ui.Core;

namespace Pfx.App.Core;

// User settings in %APPDATA%\PolyLab\ParticleStudio\settings.json (spec 6.3): data roots, language, recent files and the
// active workspace. Layouts sit beside it in layouts\<workspace>.xml. Public because System.Text.Json reads it by
// reflection.
public sealed class AppSettings
{
    public const int RecentMax = 12;

    public List<string> Roots { get; set; } = new();
    public string Lang { get; set; } = UiLanguage.Default;
    public List<string> Recent { get; set; } = new();
    public string Workspace { get; set; } = "emitters";
    // Tools the texture workspace runs (spec 3.2), all chosen by the user (O2′: nothing bound to one PC): texconv.exe for
    // DXT, the Python command and esrgan_cpu.py with its model for the optional CPU upscale.
    public string Texconv { get; set; } = "";
    public string Python { get; set; } = "py -3";
    public string Upscaler { get; set; } = "";
    public string UpscaleModel { get; set; } = "";
    // X-Ray import (spec 3.9): the user's xray.py (CG_AnomalyTools) that converts, the folder the imports are written
    // into (made a data root on the first import) and the game's converter.exe that unpacks .db archives.
    public string XrayScript { get; set; } = "";
    public string ImportFolder { get; set; } = "";
    public string XrConverter { get; set; } = "";
    public string LastScanFolder { get; set; } = "";
    // The CG_Anomaly export profile's defaults for new projects (spec 3.13: the donor folder assemble.py takes and the
    // generated showcase slot file); empty until the user sets them in an export.
    public string CgDonorFolder { get; set; } = "";
    public string CgSlotFile { get; set; } = "";
    // The tornado generator's bolt donor: an effect with the generator anomaly's spark emitters (CG's X-Ray conversion
    // generator_ar_idle.ptc); empty = the tornado is made without bolts and strikes.
    public string TornadoDonor { get; set; } = "";
    // The window opens maximized (owner, 2026-10-15: "make it for full screen") unless the user left it smaller; then
    // at the bounds it had (left, top, width, height in WPF units).
    public bool Maximized { get; set; } = true;
    // The live link (spec 3.12): the DayZ folder (empty = the Steam library that has it), the port the tool listens on
    // (the link mod's default, or its -polylabPfxLink=<port>) and how far in front of the game camera parts are placed.
    public string GameFolder { get; set; } = "";
    public int LinkPort { get; set; } = Pfx.Link.LinkHub.DefaultPort;
    public float LinkMark { get; set; } = 6;
    public double[]? Bounds { get; set; }
    // The library browser (spec 3.10): folders the user added (CG data, donors, own effects) and starred effect files.
    public List<string> LibraryFolders { get; set; } = new();
    public List<string> LibraryFavourites { get; set; } = new();

    // Resolved on first use, so a --settings-dir run never reads or copies the user's own folders.
    private static string? _folder, _cacheFolder;

    public static string Folder
    {
        get => _folder ??= DataFolder.Resolve(Environment.SpecialFolder.ApplicationData, "ParticleStudio", "NDZ_ParticleStudio");
        set => _folder = value;
    }

    private static string FilePath => Path.Combine(Folder, "settings.json");

    // Per-user machine data that can be rebuilt (scan caches), spec 6.3.
    public static string CacheFolder
    {
        get => _cacheFolder ??= DataFolder.Resolve(Environment.SpecialFolder.LocalApplicationData, "ParticleStudio", "NDZ_ParticleStudio");
        set => _cacheFolder = value;
    }

    public static string LayoutPath(string workspace) => Path.Combine(Folder, "layouts", workspace + ".xml");

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new() : new();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        AtomicFile.Write(FilePath, System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })));
    }

    public void AddRecent(string path)
    {
        Recent.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        Recent.Insert(0, path);
        if (Recent.Count > RecentMax)
            Recent.RemoveRange(RecentMax, Recent.Count - RecentMax);
    }
}

// Crash-safe write: the bytes go to a temporary file in the same folder, then replace the target in one rename, so a
// crash leaves either the old file or the new one, never half of it (spec 3.1).
public static class AtomicFile
{
    public static void Write(string path, byte[] bytes)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        string temp = Path.Combine(dir, "." + Path.GetFileName(path) + ".pfxtmp");
        File.WriteAllBytes(temp, bytes);
        if (File.Exists(path))
            File.Replace(temp, path, null);
        else
            File.Move(temp, path);
    }
}
