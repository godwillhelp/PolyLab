using System.IO;
using System.Text.Json;

namespace PolyLab.Launcher.Core;

public static class Paths
{
    // Install root: tools\<id>\ and graft\ live beside PolyLab.exe (PBOG Packer also finds graft\ two levels up).
    public static string Root { get; set; } = Path.GetDirectoryName(Environment.ProcessPath)!;

    // The launcher's own data: settings and the release list cache.
    public static string Data { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PolyLab", "Launcher");
}

public sealed class Settings
{
    public string Lang { get; set; } = "ru";
    public DateTimeOffset? NewsReadAt { get; set; }
    public double[]? Bounds { get; set; }
    public bool Maximized { get; set; } = true;

    private static string FilePath => Path.Combine(Paths.Data, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Paths.Data);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
