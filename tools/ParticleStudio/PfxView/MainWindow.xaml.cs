using System.Globalization;
using System.Numerics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Pfx.Render;
using Pfx.Sim;
using Pfx.Textures;

namespace PfxView;

public partial class MainWindow : Window
{
    private readonly Launch _launch;
    private readonly App.Settings _settings;
    private readonly ViewportHost _viewport = new();
    private bool _loading = true;

    public MainWindow(Launch launch, App.Settings settings)
    {
        _launch = launch;
        _settings = settings;
        InitializeComponent();
        ViewportSlot.Child = _viewport;
        _viewport.View = launch.View;
        _viewport.ComposeOverlay = ComposeOverlay;

        RootsBox.Text = launch.Roots.Length > 0 ? launch.Roots : settings.Roots;
        foreach (var lang in Loc.Languages)
            LangBox.Items.Add(lang);
        LangBox.SelectedItem = Loc.Lang;
        foreach (var sky in Sky.All)
            SkyBox.Items.Add(Item("Sky." + sky.Name, sky));
        foreach (var model in Enum.GetValues<HelperModel>())
            HelperBox.Items.Add(Item("Helper." + model, model));
        foreach (var motion in Enum.GetValues<MotionKind>())
            MotionBox.Items.Add(Item("Motion." + motion, motion));
        foreach (var axis in Enum.GetValues<CircleAxis>())
            AxisBox.Items.Add(axis);
        ShowView(launch.View);
        ShowSettings(launch.FirstSettings);
        launch.ApplyCamera(_viewport.Camera);
        FovSlider.Value = _viewport.Camera.Fov;
        _loading = false;
        UpdatePlayButton();

        if (launch.Effects.Count > 0)
            Open(launch.Effects);
        if (launch.Bench != null)
            new BenchRun(this, _viewport, launch).Start();
        if (launch.SelfTest != null)
            new ViewportSelfTest(this, _viewport, launch.SelfTest).Start();
        if (launch.Capture != null)
            new CaptureRun(this, _viewport, launch).Start();
    }

    private static ComboBoxItem Item(string key, object tag)
    {
        var item = new ComboBoxItem { Tag = tag };
        item.SetResourceReference(ContentProperty, key);
        return item;
    }

    private static void Select(ComboBox box, object tag) =>
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, tag)) ?? box.Items[0];

    private void ShowView(ViewOptions v)
    {
        Select(SkyBox, v.Sky);
        FogBox.IsChecked = v.Fog;
        GroundBox.IsChecked = v.Ground;
        Select(HelperBox, v.Helper);
        GizmoBox.IsChecked = v.Gizmos;
    }

    private void ShowSettings(PreviewSettings s)
    {
        string N(float f) => f.ToString("0.#####", CultureInfo.InvariantCulture);
        SeedBox.Text = s.Seed.ToString(CultureInfo.InvariantCulture);
        StepBox.Text = N(s.Step);
        Select(MotionBox, s.Motion);
        RadiusBox.Text = N(s.CircleRadius);
        SpeedBox.Text = N(s.CircleSpeed);
        AxisBox.SelectedItem = s.CircleAxis;
        TurnBox.Text = N(s.TurnRate);
        CopiesBox.Text = s.Copies.ToString(CultureInfo.InvariantCulture);
        SpacingBox.Text = N(s.Spacing);
        LowFpsBox.Text = N(s.LowFps);
    }

    private PreviewSettings CurrentSettings()
    {
        var old = _viewport.Preview?.Settings ?? _launch.Preview;
        // Out-of-range or unreadable input keeps the previous value (the field shows it again).
        float F(TextBox box, float previous, float min, float max) =>
            float.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v, min, max) : previous;
        return old with
        {
            Seed = ulong.TryParse(SeedBox.Text, out var seed) ? seed : old.Seed,
            Step = F(StepBox, old.Step, 0.001f, 0.5f),
            Motion = MotionBox.SelectedItem is ComboBoxItem { Tag: MotionKind m } ? m : old.Motion,
            CircleRadius = F(RadiusBox, old.CircleRadius, 0, 100),
            CircleSpeed = F(SpeedBox, old.CircleSpeed, 0, 100),
            CircleAxis = AxisBox.SelectedItem is CircleAxis a ? a : old.CircleAxis,
            TurnRate = F(TurnBox, old.TurnRate, -3600, 3600),
            Copies = int.TryParse(CopiesBox.Text, out var copies) ? Math.Clamp(copies, 1, 400) : old.Copies,
            Spacing = F(SpacingBox, old.Spacing, 0.1f, 1000),
            LowFps = F(LowFpsBox, old.LowFps, 0, 240),
        };
    }

    // Replaces the scene with these effects (several parts of one composition share the scene).
    public void Open(IReadOnlyList<(string Path, PreviewSettings Settings)> effects)
    {
        var roots = new DataRoots(RootsBox.Text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var loaded = new List<EffectPreview>();
        foreach (var (path, settings) in effects)
        {
            try
            {
                loaded.Add(EffectPreview.Load(path, roots, settings));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or Pfx.Core.Text.EnfSyntaxException or UnauthorizedAccessException)
            {
                StatusText.Text = Loc.F("Msg.OpenFailed", path, ex.Message);
                return;
            }
        }
        _viewport.Previews.Clear();
        _viewport.Previews.AddRange(loaded);
        Title = $"{string.Join(", ", loaded.Select(p => Path.GetFileName(p.FilePath)))} — {Loc.T("Title")}";
        StatusText.Text = string.Join(";", roots.Roots);
        UpdatePlayButton();
    }

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "DayZ particle effect (*.ptc)|*.ptc", Multiselect = true };
        if (dialog.ShowDialog(this) == true)
        {
            var settings = CurrentSettings();
            Open(dialog.FileNames.Select(f => (f, settings)).ToList());
        }
    }

    private void OnCommitKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        if (sender == RootsBox)
            OnRootsCommit(sender, e);
        else
            OnSettingsCommit(sender, e);
    }

    private void OnRootsCommit(object sender, RoutedEventArgs e)
    {
        if (_settings.Roots == RootsBox.Text)
            return;
        _settings.Roots = RootsBox.Text;
        _settings.Save();
        if (_viewport.Previews.Count > 0)
            Open(_viewport.Previews.Select(p => (p.FilePath, p.Settings)).ToList());
    }

    private void OnSettingsCommit(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        var s = CurrentSettings();
        if (_viewport.Preview is { } first && s != first.Settings)
        {
            // The fields show the first effect. Motion fields go to every effect only when they were changed, so
            // parts that turn at their own rates (command line) keep them.
            var f = first.Settings;
            bool motion = s.Motion != f.Motion || s.TurnRate != f.TurnRate || s.CircleRadius != f.CircleRadius
                          || s.CircleSpeed != f.CircleSpeed || s.CircleAxis != f.CircleAxis;
            foreach (var p in _viewport.Previews)
            {
                var n = p.Settings with { Seed = s.Seed, Step = s.Step, LowFps = s.LowFps, Copies = s.Copies, Spacing = s.Spacing };
                if (motion)
                    n = n with { Motion = s.Motion, TurnRate = s.TurnRate, CircleRadius = s.CircleRadius, CircleSpeed = s.CircleSpeed, CircleAxis = s.CircleAxis };
                p.Settings = n;
            }
        }
        ShowSettings(s);
    }

    private void OnViewOptions(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        _viewport.View = _viewport.View with
        {
            Sky = SkyBox.SelectedItem is ComboBoxItem { Tag: Sky sky } ? sky : Sky.Noon,
            Fog = FogBox.IsChecked == true,
            Ground = GroundBox.IsChecked == true,
            Helper = HelperBox.SelectedItem is ComboBoxItem { Tag: HelperModel h } ? h : HelperModel.None,
            Gizmos = GizmoBox.IsChecked == true,
        };
    }

    private void OnPlay(object sender, RoutedEventArgs e)
    {
        bool play = _viewport.Preview?.Playing == false;
        foreach (var p in _viewport.Previews)
            p.Playing = play;
        UpdatePlayButton();
    }

    private void UpdatePlayButton() =>
        PlayButton.SetResourceReference(ContentProperty, _viewport.Preview?.Playing == false ? "Btn.Play" : "Btn.Pause");

    private void OnStep(object sender, RoutedEventArgs e)
    {
        foreach (var p in _viewport.Previews)
        {
            p.Playing = false;
            p.StepOnce();
        }
        UpdatePlayButton();
    }

    private void OnRestart(object sender, RoutedEventArgs e)
    {
        foreach (var p in _viewport.Previews)
            p.Restart();
    }

    private void OnView(object sender, RoutedEventArgs e)
    {
        var preset = Enum.Parse<ViewPreset>((string)((Button)sender).Tag);
        var focus = (_viewport.Preview?.Settings.Origin ?? Vector3.Zero) + new Vector3(0, preset == ViewPreset.Top ? 0 : 1, 0);
        _viewport.Camera.Apply(preset, focus);
    }

    private void OnFov(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // The slider coerces its value while the XAML is still loading, before FovText exists.
        if (FovText == null)
            return;
        _viewport.Camera.Fov = (float)e.NewValue;
        FovText.Text = _viewport.Camera.Fov.ToString("0", CultureInfo.InvariantCulture);
    }

    private void OnLang(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LangBox.SelectedItem is not string lang)
            return;
        Loc.Set(lang);
        _settings.Lang = lang;
        _settings.Save();
    }

    private void OnShot(object sender, RoutedEventArgs e)
    {
        var image = _viewport.CaptureFrame(true);
        if (image == null)
            return;
        var dialog = new SaveFileDialog { Filter = "PNG (*.png)|*.png", FileName = "view.png" };
        if (dialog.ShowDialog(this) != true)
            return;
        ImageFiles.Save(dialog.FileName, image);
        StatusText.Text = Loc.F("Msg.Saved", dialog.FileName);
    }

    private void OnDeviceLoss(object sender, RoutedEventArgs e) => _viewport.ForceDeviceLoss();

    private void ComposeOverlay(OverlayBuilder o)
    {
        var previews = _viewport.Previews;
        var lines = new List<OverlayLine>();
        var text = Palette.Text;
        var dim = Palette.TextDim;
        if (previews.Count == 0)
        {
            lines.Add(new(Loc.T("Ov.NoEffect"), text));
        }
        else
        {
            var first = previews[0];
            var stats = _viewport.Stats;
            string gpu = stats == null || double.IsNaN(stats.GpuMs) ? Loc.T("Ov.Na") : Loc.F("Ov.Ms", stats.GpuMs);
            string names = string.Join(", ", previews.Select(p => Path.GetFileNameWithoutExtension(p.FilePath)));
            string state = (first.Playing ? "" : Loc.T("Ov.Paused"))
                           + (first.Settings.LowFps > 0 ? Loc.F("Ov.LowFps", first.Settings.LowFps) : "");
            lines.Add(new(Loc.F("Ov.Time", names, first.ShownTime, state), Palette.TextHi));
            lines.Add(new(Loc.F("Ov.Frame", _viewport.FrameIntervalMs, _viewport.CpuFrameMs, stats?.SimMs ?? 0, stats?.UploadMs ?? 0, gpu), text));
            lines.Add(new(Loc.F("Ov.Particles", previews.Sum(p => p.AliveTotal), stats?.Draws ?? 0, first.Sims.Count), text));
            if (_viewport.DeviceResets > 0)
                lines.Add(new(Loc.F("Ov.Resets", _viewport.DeviceResets), dim));
            // One line per emitter of copy 0: at most EmitterLines, and only as many as fit above the help line.
            const int EmitterLines = 12;
            int room = Math.Min(EmitterLines, Math.Max(0, (int)((_viewport.PixelHeight - 3 * o.LineHeight - 40) / o.LineHeight) - lines.Count - 1));
            int swapCount = _viewport.Draws.Count(d => d.Copy == 0 && d.MaySwap);
            lines.Add(new(Loc.F("Ov.Emitters", previews.Sum(p => p.Params.Count), swapCount), text));
            var emitterLines = new List<OverlayLine>();
            for (int i = 0; i < previews.Count; i++)
            {
                var p = previews[i];
                var swaps = _viewport.Draws.Where(d => d.Effect == i && d.Copy == 0 && d.MaySwap).Select(d => d.Emitter).ToHashSet();
                var main = p.Sims[0];
                for (int e = 0; e < p.Params.Count; e++)
                {
                    var flags = Flags(p, e, swaps.Contains(e));
                    string line = Loc.F("Ov.Emitter", p.Params[e].Name, main.Emitters[e].Count, p.Params[e].MaxNum);
                    if (flags.Count > 0)
                        line += " · " + string.Join(" · ", flags);
                    emitterLines.Add(new(line, flags.Count > 0 ? Palette.Warn : dim));
                }
            }
            if (emitterLines.Count > room)
            {
                int shown = Math.Max(0, room - 1);
                int hidden = emitterLines.Count - shown;
                emitterLines = emitterLines.Take(shown).ToList();
                emitterLines.Add(new(Loc.F("Ov.More", hidden), dim));
            }
            lines.AddRange(emitterLines);
        }
        var panel = Palette.BgDeep.WithAlpha(0.75f);
        o.Panel(8, 8, lines, panel);
        o.Panel(8, _viewport.PixelHeight - o.LineHeight - 16, new[] { new OverlayLine(Loc.T("Ov.Help"), dim) }, panel);
    }

    private static List<string> Flags(EffectPreview p, int e, bool maySwap)
    {
        var flags = new List<string>();
        foreach (KnownDifference d in Enum.GetValues<KnownDifference>())
            if (d != KnownDifference.None && p.Params[e].Differences.HasFlag(d))
                flags.Add(Loc.T("Flag." + d));
        var m = p.Materials[e];
        if (p.Params[e].Material != null && !m.Found)
            flags.Add(Loc.T("Flag.MaterialMissing"));
        else if (m.AlbedoMap != null && p.AlbedoPaths[e] == null)
            flags.Add(Loc.T("Flag.TextureMissing"));
        if (m.DrawsNothing)
            flags.Add(Loc.T("Flag.Streak"));
        if (maySwap)
            flags.Add(Loc.T("Flag.MaySwap"));
        return flags;
    }
}
