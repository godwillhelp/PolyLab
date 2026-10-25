using System.Text.Json;
using System.Windows;
using Pfx.Render;
using Pfx.Textures;

namespace PfxView;

public partial class App : Application
{
    // Settings of this test app: data roots and language (%APPDATA%\PolyLab\ParticleStudio, spec 6.3).
    public sealed class Settings
    {
        public string Roots { get; set; } = "";
        public string Lang { get; set; } = "ru";

        private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PolyLab", "ParticleStudio", "PfxView.json");

        public static Settings Load()
        {
            try
            {
                return File.Exists(FilePath) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new() : new();
            }
            catch (Exception e) when (e is IOException or JsonException)
            {
                return new();
            }
        }

        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Launch launch;
        try
        {
            launch = Launch.Parse(e.Args);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            File.WriteAllText("PfxView.error.txt", ex.Message);
            Shutdown(2);
            return;
        }
        if (launch.Shot != null)
        {
            Shutdown(RunShot(launch));
            return;
        }
        var settings = Settings.Load();
        Loc.Set(launch.Lang ?? settings.Lang);
        var window = new MainWindow(launch, settings);
        window.Closed += (_, _) => Shutdown();
        window.Show();
    }

    // One offscreen frame: the scene at --time with the --eye/--look camera, saved as PNG.
    private static int RunShot(Launch l)
    {
        try
        {
            using var gpu = new Gpu();
            var textures = new TextureCache();
            using var renderer = new Renderer(gpu, textures, null);
            using var targets = Targets.Offscreen(gpu, l.Width, l.Height);
            var roots = new DataRoots(l.RootList);
            var previews = new List<EffectPreview>();
            // Seeking runs every simulation from 0 to --time: the cost of a timeline jump.
            var seek = System.Diagnostics.Stopwatch.StartNew();
            if (!l.Empty)
            {
                foreach (var (path, settings) in l.Effects)
                {
                    var preview = EffectPreview.Load(path, roots, settings);
                    preview.Seek(l.Time);
                    previews.Add(preview);
                }
            }
            double seekMs = seek.Elapsed.TotalMilliseconds;
            var camera = new Camera();
            l.ApplyCamera(camera);
            renderer.Render(targets, camera, previews, l.View, null);
            ImageFiles.Save(l.Shot!, renderer.Capture(targets));
            if (l.Report != null)
            {
                var lines = previews.SelectMany(p => Enumerable.Range(0, p.Params.Count).Select(e => Renderer.Describe(p, e, l.View.Sky))).ToList();
                lines.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "load and seek to {0} s: {1:0.0} ms, {2} particles", l.Time, seekMs, previews.Sum(p => p.AliveTotal)));
                File.WriteAllLines(l.Report, lines);
            }
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(l.Shot + ".error.txt", ex.ToString());
            return 1;
        }
    }
}
