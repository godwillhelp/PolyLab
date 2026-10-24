using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using Pfx.App.Model;
using Pfx.App.Views;
using Pfx.Core.Editing;
using Pfx.Core.Model;
using Pfx.Render;

namespace Pfx.App.Automation;

// --compose <dir>: the P10 acceptance anomaly built from nothing inside the real window: a zharka (burner) of vanilla
// fire materials. Every step goes through the same model operations the panels call: new empty parts (NewPart),
// emitters added and their keys set as inspector edits (one undo step each, the part's history), the project made with
// New, items added and edited through CompositionDoc.Do (the project's history), sounds picked with SetSoundFile
// (X-Ray .ogg files bring range and volume), then saved. No mouse is sent; the values are typed here the way a user
// types them into the fields. Then the states are played and the viewport is photographed once per state.
// --compose-sounds <idle.ogg;blast.ogg> names the two sound files (test inputs, copied into <dir>\sounds).
public sealed class ComposeRun
{
    private const string Flame = "Graphics/Particles/materials/fire/fire_flame_01.emat";
    private const string Burst = "Graphics/Particles/materials/fire/fire_medium_camp_04.emat";
    private const string Glow = "Graphics/Particles/materials/glow/glow1.emat";
    private const string Smoke = "Graphics/Particles/materials/smoke/smoke_anim_02.emat";
    private const string Sparks = "Graphics/Particles/materials/fire/sparks1.emat";

    // Part -> emitters -> "Key values" (a quoted value is a path, a word a keyword, braces a graph).
    private static readonly (string Part, (string Emitter, string[] Keys)[] Emitters)[] Parts =
    {
        ("flames", new[]
        {
            ("tongues", new[] { "ShapeType Box", "ShapeSize 1.2 0 1.2", "ConeAngle 360 0 20", "MaxNum 80", "BirthRate 40", "BirthRateRND 20", $"Material \"{Flame}\"",
                "SizeMultiplier 1", "SizeRND 0.8", "VelAngle 1", "Velocity 1.2", "VelocityRND 1.2", "GravityMultiply 0", "Spring 0.0015", "Wind 1",
                "RandomFrame 1", "LifeTime 0.45", "LifeTimeRND 0.3", "EffectTime 1", "Repeat 1",
                "Color {0 0.8627 0.8471 0.3451 1 0.502 0.0745 0.0431}", "Alpha {0 0 0.3 0.8 0.65 0.6 1 0}", "Size {0 0 0.49 0.47 1 0.19}" }),
            ("glow", new[] { "ShapeType Point", "Offset 0 0.35 0", "MaxNum 4", "BirthRate 3", $"Material \"{Glow}\"", "SizeMultiplier 1.8", "SizeRND 0.6",
                "Velocity 0", "GravityMultiply 0", "LifeTime 1", "LifeTimeRND 0.4", "EffectTime 1", "Repeat 1",
                "Color {0 1 0.55 0.2 1 1 0.45 0.15}", "Alpha {0 0 0.3 0.18 1 0}" }),
            ("wisps", new[] { "ShapeType Box", "ShapeSize 1 0 1", "ConeAngle 360 0 10", "MaxNum 12", "BirthRate 2", $"Material \"{Smoke}\"",
                "SizeMultiplier 1.2", "SizeRND 0.8", "Velocity 1.2", "VelocityRND 0.6", "GravityMultiply 0", "Wind 1", "RandomAngle 1",
                "LifeTime 3", "LifeTimeRND 1", "EffectTime 1", "Repeat 1", "Color {0 0.25 0.22 0.2 1 0.25 0.22 0.2}", "Alpha {0 0 0.2 0.25 1 0}",
                "Size {0 0.4 1 1}" }),
        }),
        ("embers", new[]
        {
            ("sparks", new[] { "ShapeType Box", "ShapeSize 1.2 0 1.2", "ConeAngle 360 0 30", "MaxNum 30", "BirthRate 10", "BirthRateRND 4",
                $"Material \"{Sparks}\"", "SizeMultiplier 0.035", "SizeRND 0.02", "Velocity 1.6", "VelocityRND 1.6", "AirResistance 0.3",
                "GravityMultiply 0", "Spring 0.001", "Wind 1", "LifeTime 1.6", "LifeTimeRND 0.6", "EffectTime 1", "Repeat 1",
                "Color {0 0.9922 1 0.4471 1 1 0.4392 0.2157}", "Alpha {0 1 0.5 0.3 1 0}" }),
        }),
        ("column", new[]
        {
            ("fire_up", new[] { "ShapeType Box", "ShapeSize 0.7 0 0.7", "ConeAngle 360 0 8", "MaxNum 140", "BirthRate 70", $"Material \"{Burst}\"",
                "SizeMultiplier 1.6", "SizeRND 1", "RandomRotDir 1", "RotMultiplier 2", "Velocity 5", "VelocityRND 3", "AirResistance 0.8",
                "GravityMultiply 0", "AnimOnce 1", "AnimFPS 30", "LifetimeByAnim 1", "LifeTime 1.1", "EffectTime 2.4", "Repeat 0",
                "Color {0 1 0.9 0.6 1 0.6 0.15 0.05}", "Alpha {0 0 0.15 1 0.7 0.6 1 0}", "Size {0 0.4 0.4 0.9 1 1}" }),
            ("flash", new[] { "ShapeType Point", "Offset 0 1.5 0", "MaxNum 6", "BirthRate 6", $"Material \"{Glow}\"", "SizeMultiplier 7", "SizeRND 2",
                "Velocity 0", "GravityMultiply 0", "LifeTime 0.5", "LifeTimeRND 0.3", "EffectTime 0.6", "Repeat 0",
                "Color {0 1 0.6 0.25 1 1 0.5 0.2}", "Alpha {0 0.8 1 0}" }),
        }),
        ("smoke", new[]
        {
            ("black", new[] { "ShapeType Box", "ShapeSize 0.8 0 0.8", "ConeAngle 360 0 15", "MaxNum 60", "BirthRate 10", $"Material \"{Smoke}\"",
                "SizeMultiplier 2.5", "SizeRND 1.5", "Velocity 3", "VelocityRND 1.5", "AirResistance 0.6", "GravityMultiply 0", "Wind 1",
                "RandomAngle 1", "LifeTime 3.5", "LifeTimeRND 1", "EffectTime 2.4", "Repeat 0",
                "Color {0 0.12 0.1 0.09 1 0.16 0.14 0.13}", "Alpha {0 0 0.15 0.6 1 0}", "Size {0 0.3 1 1}" }),
        }),
        ("ash", new[]
        {
            ("smoulder", new[] { "ShapeType Box", "ShapeSize 1 0 1", "ConeAngle 360 0 10", "MaxNum 16", "BirthRate 3", $"Material \"{Smoke}\"",
                "SizeMultiplier 1", "SizeRND 0.6", "Velocity 0.8", "VelocityRND 0.4", "GravityMultiply 0", "Wind 1", "RandomAngle 1",
                "LifeTime 4", "LifeTimeRND 1", "EffectTime 1", "Repeat 1", "Color {0 0.3 0.28 0.26 1 0.3 0.28 0.26}", "Alpha {0 0 0.2 0.3 1 0}",
                "Size {0 0.4 1 1}" }),
            ("last_sparks", new[] { "ShapeType Box", "ShapeSize 1 0 1", "ConeAngle 360 0 30", "MaxNum 8", "BirthRate 3", $"Material \"{Sparks}\"",
                "SizeMultiplier 0.03", "SizeRND 0.015", "Velocity 1", "VelocityRND 1", "GravityMultiply 0", "Wind 1", "LifeTime 1.2",
                "LifeTimeRND 0.4", "EffectTime 1", "Repeat 1", "Color {0 1 0.5 0.2 1 1 0.35 0.12}", "Alpha {0 1 1 0}" }),
        }),
    };

    private readonly MainWindow _w;
    private readonly string _dir;
    private readonly string[] _sounds;
    private readonly StringBuilder _log = new();
    private TaskCompletionSource? _frame;

    public ComposeRun(MainWindow window, Pfx.App.Core.Launch launch)
    {
        _w = window;
        _dir = Path.GetFullPath(launch.Compose!);
        _sounds = (launch.ComposeSounds ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries);
    }

    public void Start()
    {
        Directory.CreateDirectory(_dir);
        _w.Viewport.Rendered += () => _frame?.TrySetResult();
        _ = Run();
    }

    private Task Frame()
    {
        _frame = new TaskCompletionSource();
        return _frame.Task;
    }

    private void Log(string line)
    {
        _log.AppendLine(line);
        File.WriteAllText(Path.Combine(_dir, "compose.txt"), _log.ToString());
    }

    private async Task Run()
    {
        try
        {
            if (_sounds.Length != 2 || !_sounds.All(File.Exists))
                throw new InvalidOperationException("--compose-sounds needs two existing .ogg files (idle;blast)");
            string particles = Path.Combine(_dir, "particles");
            Directory.CreateDirectory(particles);
            foreach (var (part, emitters) in Parts)
            {
                string path = Path.Combine(particles, part + ".ptc");
                var doc = _w.NewPart(path) ?? throw new InvalidOperationException("part not created: " + path);
                foreach (var (name, keys) in emitters)
                {
                    doc.Do(doc.Ptc.AddEmitter(name), "add " + name);
                    var e = doc.Ptc.Emitter(name)!;
                    foreach (var line in keys)
                        doc.Do(Edit(e, line), name + " " + line.Split(' ')[0]);
                }
                Log($"part {part}: {emitters.Length} emitters, {emitters.Sum(x => x.Keys.Length)} keys, undo steps {doc.UndoCount}");
            }
            _w.NewProject();
            var p = _w.Project ?? throw new InvalidOperationException("no project");
            string sounds = Path.Combine(_dir, "sounds");
            Directory.CreateDirectory(sounds);
            var copies = _sounds.Select(s => Path.Combine(sounds, Path.GetFileName(s))).ToList();
            for (int i = 0; i < 2; i++)
                File.Copy(_sounds[i], copies[i], true);
            _w.SaveProjectTo(Path.Combine(_dir, "zharka.ndzpfx"));

            p.Do(f =>
            {
                f.Name = "zharka";
                f.States.Clear();
                f.Lights.Add(new LightEntry { Id = "glow", Color = new[] { 1f, 0.45f, 0.15f }, Brightness = 1.5f, Radius = 6, Offset = new[] { 0f, 0.6f, 0f },
                    FlickerAmp = 0.6f, FlickerSpeed = 5, FadeOut = 1 });
                f.Lights.Add(new LightEntry { Id = "blast", Color = new[] { 1f, 0.55f, 0.2f }, Brightness = 60, Radius = 16, Offset = new[] { 0f, 1.6f, 0f },
                    Life = 2.6f, FadeOut = 0.6f, Curve = { new[] { 0f, 0.3f }, new[] { 0.12f, 1f }, new[] { 2.2f, 0.7f } } });
                f.Lights.Add(new LightEntry { Id = "ember_light", Color = new[] { 1f, 0.35f, 0.1f }, Brightness = 0.8f, Radius = 4, Offset = new[] { 0f, 0.4f, 0f },
                    FlickerAmp = 0.4f, FlickerSpeed = 3, FadeOut = 2 });
                f.Hazes.Add(new HazeEntry { Id = "heat", At = new[] { 0f, 0.6f, 0f }, Radius = 1.6f, Power = 0.012f });
                f.Hazes.Add(new HazeEntry { Id = "blast_heat", At = new[] { 0f, 1.5f, 0f }, Radius = 2.6f, Power = 0.03f, Life = 2.6f, Fade = 0.6f });
                f.Sounds.Add(new SoundEntry { Id = "burn", Loop = true });
                f.Sounds.Add(new SoundEntry { Id = "roar" });
                f.Screens.Add(new ScreenEntry { Id = "heat_tint", Color = new[] { 1f, 0.8f, 0.6f }, Radius = 4, Fade = 1 });
                f.Parts.Single(x => x.Id == "embers").Near = 60;
                f.Parts.Single(x => x.Id == "column").Stop = 3;
                f.States.Add(new StateEntry { Name = "idle", Items = { new() { Ref = "flames" }, new() { Ref = "embers" }, new() { Ref = "glow" }, new() { Ref = "heat" }, new() { Ref = "burn" } } });
                f.States.Add(new StateEntry { Name = "active", Duration = 3.5f, Items = { new() { Ref = "column" }, new() { Ref = "smoke", Delay = 0.3f },
                    new() { Ref = "blast" }, new() { Ref = "blast_heat" }, new() { Ref = "roar" }, new() { Ref = "heat_tint" } } });
                f.States.Add(new StateEntry { Name = "linger", Duration = 5, Items = { new() { Ref = "ash" }, new() { Ref = "ember_light" } } });
                f.Anomaly.Archetype = "burn";
                f.Anomaly.Tick = 0.5f;
                f.Anomaly.Shells["sense"] = new Shell { R = 4, YMin = -1, YMax = 3 };
                f.Anomaly.Shells["hit"] = new Shell { R = 1.6f, YMin = -0.5f, YMax = 2.5f };
            }, "zharka: lights, haze, sounds, states");
            _w.SetSoundFile("burn", copies[0]);
            _w.SetSoundFile("roar", copies[1]);
            _w.SaveProjectTo(null);
            var file = p.File;
            Log($"project {p.Path}: parts {file.Parts.Count}, lights {file.Lights.Count}, hazes {file.Hazes.Count}, sounds {file.Sounds.Count}, " +
                $"screens {file.Screens.Count}, states {string.Join(" ", file.States.Select(s => s.Name + "=" + s.Duration))}");
            foreach (var s in file.Sounds)
                Log($"  sound {s.Id}: {s.File} range {s.Range} volume {s.Volume} loop {s.Loop}");
            Log($"history: project {p.UndoCount} steps; parts {string.Join(", ", _w.Docs.Select(d => d.Title + " " + d.UndoCount))}");

            // The states in the preview, one frame in each state, the window maximized (owner: tools are for full screen).
            _w.WindowState = System.Windows.WindowState.Maximized;
            await Frame();
            while (_w.Pipeline.AnyBusy || _w.Viewport.Previews.Count < _w.Docs.Count || _w.Viewport.PixelWidth == 0)
                await Frame();
            await _w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            _w.Viewport.Camera.LookAt(new Vector3(4.5f, 2.4f, -4.5f), new Vector3(0, 1.3f, 0));
            _w.ComposeTab.SetFarRings(false);
            _w.StatesPlaying = true;
            var timeline = _w.ProjectTimeline ?? throw new InvalidOperationException("no timeline");
            Log($"cycle {timeline.Length:0.00} s: " + string.Join(", ", timeline.States.Select(s => $"{s.Name} {s.From:0.00}-{s.To:0.00}")));
            foreach (var r in timeline.Runs)
                Log($"  run {r.Kind} {r.Ref} {r.Start:0.00}-{r.End:0.00}" + (r.HardStop is double h ? $" hard {h:0.00}" : ""));
            _w.Pipeline.Playing = false;
            foreach (var (name, at) in new[] { ("idle", 2.0), ("active", 0.7), ("active_late", 2.9), ("linger", 2.0) })
            {
                var span = timeline.States.First(s => name.StartsWith(s.Name));
                float t = (float)(span.From + at);
                while (_w.Pipeline.AnyBusy)
                    await Frame();
                _w.Pipeline.Seek(t);
                while (_w.Pipeline.AnySeeking || _w.Pipeline.AnyBusy)
                    await Frame();
                await Frame();
                await Frame();
                int alive = _w.Viewport.Previews.Sum(v => v.AliveTotal);
                Log($"shot {name} at {t:0.00} s: alive {alive}");
                if (_w.Viewport.CaptureFrame(true) is { } image)
                    Pfx.Textures.ImageFiles.Save(Path.Combine(_dir, $"view_{name}.png"), MainWindow.Opaque(image));
            }
            // The whole tool as the owner sees it, maximized: the anomaly panel with the timeline, then with the export.
            _w.Pipeline.Seek((float)(timeline.States[1].From + 0.7));
            while (_w.Pipeline.AnySeeking || _w.Pipeline.AnyBusy)
                await Frame();
            foreach (var panes in new[] { "compose+timeline", "compose+export" })
            {
                foreach (var pane in panes.Split('+'))
                    _w.ShowPane(pane);
                await _w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                await Frame();
                await Frame();
                var root = (System.Windows.FrameworkElement)_w.Content;
                double dpi = System.Windows.Media.VisualTreeHelper.GetDpi(_w).DpiScaleX;
                ShellChecks.SaveShot(_w, root, _w.ViewportArea, dpi, Path.Combine(_dir, $"tool_{panes.Replace('+', '_')}.png"));
                Log($"tool shot {panes}: window {_w.ActualWidth:0}x{_w.ActualHeight:0}");
            }
            Log("RESULT: done");
        }
        catch (Exception ex)
        {
            Log("FAILED: " + ex);
        }
        _w.Close();
    }

    // One inspector-style edit of a key: numbers, a keyword, a quoted path or a graph in braces.
    public static IEdit Edit(KeyedBlock block, string line)
    {
        int space = line.IndexOf(' ');
        string key = line[..space], value = line[(space + 1)..].Trim();
        if (value.StartsWith('"'))
            return block.SetString(key, value.Trim('"'));
        if (value.StartsWith('{'))
            return block.SetGraph(key, Numbers(value.Trim('{', '}')));
        var nums = Numbers(value);
        return nums.Length > 0 ? block.Set(key, nums) : block.SetWord(key, value);
    }

    private static double[] Numbers(string text)
    {
        var parts = Regex.Split(text.Trim(), "\\s+");
        var list = new List<double>();
        foreach (var p in parts)
            if (double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                list.Add(d);
            else
                return Array.Empty<double>();
        return list.ToArray();
    }
}
