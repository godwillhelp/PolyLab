using System.IO;
using System.Text.Json;

namespace PolyLab.Ui.Core;

// <root>\PolyLab\<tool>. On the first start without it, the tool's old folder is copied in once, with paths stored in
// its JSON files moved into the copy; the copy is staged and moved in whole, so a broken one is redone next start.
// The old folder is only read.
public static class DataFolder
{
    public static string Resolve(Environment.SpecialFolder root, string tool, string legacy) =>
        Resolve(Environment.GetFolderPath(root), tool, legacy);

    public static string Resolve(string root, string tool, string legacy)
    {
        string dir = Path.Combine(root, "PolyLab", tool);
        string old = Path.Combine(root, legacy);
        if (Directory.Exists(dir) || !Directory.Exists(old))
            return dir;
        string stage = dir + ".copy";
        try
        {
            if (Directory.Exists(stage))
                Directory.Delete(stage, true);
            Copy(old, stage);
            string from = JsonSerializer.Serialize(old)[1..^1], to = JsonSerializer.Serialize(dir)[1..^1];
            foreach (var json in Directory.GetFiles(stage, "*.json"))
            {
                string text = File.ReadAllText(json);
                if (text.Contains(from, StringComparison.OrdinalIgnoreCase))
                    File.WriteAllText(json, text.Replace(from, to, StringComparison.OrdinalIgnoreCase));
            }
            Directory.Move(stage, dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return dir;
    }

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from))
            File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(from))
            Copy(d, Path.Combine(to, Path.GetFileName(d)));
    }
}
