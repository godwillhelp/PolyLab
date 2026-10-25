using System.Text.Json;
using PolyLab.Ui.Core;

namespace Pfx.Tests;

public class DataFolderTests
{
    // A Cyrillic folder name makes System.Text.Json store the paths escaped (\uXXXX), as it would for such a user name.
    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "pfx_test_Тест_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void The_old_folder_is_copied_once_with_its_paths_moved_and_left_as_it_was()
    {
        string root = NewRoot();
        string old = Path.Combine(root, "NDZ_Tool");
        string keys = Path.Combine(old, "keys");
        Directory.CreateDirectory(keys);
        Directory.CreateDirectory(Path.Combine(root, "PolyLab", "Tool.copy"));
        try
        {
            File.WriteAllText(Path.Combine(keys, "a.bikey"), "key");
            File.WriteAllText(Path.Combine(root, "PolyLab", "Tool.copy", "broken.txt"), "x");
            string state = JsonSerializer.Serialize(new { KeyDir = keys, Graft = @"D:\Tools\graft.exe" });
            File.WriteAllText(Path.Combine(old, "state.json"), state);

            string dir = DataFolder.Resolve(root, "Tool", "NDZ_Tool");

            Assert.Equal(Path.Combine(root, "PolyLab", "Tool"), dir);
            Assert.Equal("key", File.ReadAllText(Path.Combine(dir, "keys", "a.bikey")));
            var moved = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "state.json"))).RootElement;
            Assert.Equal(Path.Combine(dir, "keys"), moved.GetProperty("KeyDir").GetString());
            Assert.Equal(@"D:\Tools\graft.exe", moved.GetProperty("Graft").GetString());
            Assert.False(File.Exists(Path.Combine(dir, "broken.txt")));
            Assert.False(Directory.Exists(dir + ".copy"));
            Assert.Equal(state, File.ReadAllText(Path.Combine(old, "state.json")));
            Assert.Equal(new[] { "state.json" }, Directory.GetFiles(old).Select(Path.GetFileName));

            File.WriteAllText(Path.Combine(old, "late.txt"), "x");
            Assert.Equal(dir, DataFolder.Resolve(root, "Tool", "NDZ_Tool"));
            Assert.False(File.Exists(Path.Combine(dir, "late.txt")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Without_an_old_folder_nothing_is_created()
    {
        string root = NewRoot();
        Directory.CreateDirectory(root);
        try
        {
            string dir = DataFolder.Resolve(root, "Tool", "NDZ_Tool");
            Assert.Equal(Path.Combine(root, "PolyLab", "Tool"), dir);
            Assert.False(Directory.Exists(Path.Combine(root, "PolyLab")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
