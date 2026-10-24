using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Microsoft.Win32;
using Pfx.App.Core;
using Pfx.App.Model;
using Pfx.App.ViewModels;
using Pfx.Render;
using Pfx.Sim;
using Pfx.Textures;

namespace Pfx.App.Views;

// Viewport, preview tools of spec 5.5 (circle motion, stress test, low frame rate, camera), overlay and status bar.
public partial class MainWindow
{
    // The status bar text changes at most this often: it reads the frame statistics, which change every frame.
    private const double StatusIntervalMs = 250;
    private static readonly ViewPreset[] Presets = { ViewPreset.Free, ViewPreset.Top, ViewPreset.Side, ViewPreset.Front, ViewPreset.Eye };
    // Defaults of the preview tools when switched on: Workbench's movement circle (2 m, 4 m/s, Y, W0), a 5 x 5 stress
    // grid, Workbench's 16 fps.
    private const int StressCopies = 25;
    private const float LowFpsDefault = 16;

    private readonly Stopwatch _statusClock = Stopwatch.StartNew();
    private double _lastStatus = -StatusIntervalMs;
    private string _options = "";
    private int _copies = StressCopies;
    private float _lowFps = LowFpsDefault;
    private double _lastBuildMs = -1;
    private double _lastWorkerMs = -1;

    public event Action<EffectDoc, double, double>? PreviewBuilt;

    private void InitViewport()
    {
        foreach (var p in Presets)
        {
            var b = new Button { Style = (Style)FindResource("Btn.Seg"), Tag = p, Margin = new Thickness(0, 0, -1, 0) };
            b.SetResourceReference(ContentProperty, "View." + p);
            b.SetResourceReference(ToolTipProperty, "Tip.View." + p);
            b.Click += (_, _) => ApplyView(p);
            ViewPresets.Children.Add(b);
        }
        foreach (var sky in Sky.All)
        {
            var item = new ComboBoxItem { Tag = sky };
            item.SetResourceReference(ContentProperty, "Sky." + sky.Name);
            item.SetResourceReference(ToolTipProperty, "Tip.Sky");
            SkyBox.Items.Add(item);
        }
        var v = _viewport.View;
        FogButton.IsChecked = v.Fog;
        GroundButton.IsChecked = v.Ground;
        HelperButton.IsChecked = v.Helper != HelperModel.None;
        GizmoButton.IsChecked = v.Gizmos;
        SkyBox.SelectedIndex = 0;
        _pipeline.Settings = _pipeline.Settings with { Copies = _launch.Copies, Spacing = _launch.Spacing };
        if (_launch.Copies > 1)
            _copies = _launch.Copies;
        StressButton.IsChecked = _launch.Copies > 1;
        InitTools();
    }

    public void ViewFrom(ViewPreset p) => ApplyView(p);

    internal void ApplyView(ViewPreset p)
    {
        var focus = (_viewport.Preview?.Settings.Origin ?? Vector3.Zero) + new Vector3(0, p == ViewPreset.Top ? 0 : 1, 0);
        _viewport.Camera.Apply(p, focus);
    }

    private void OnViewOptions(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || SkyBox.SelectedItem is not ComboBoxItem { Tag: Sky sky })
            return;
        bool skyChanged = _viewport.View.Sky != sky;
        _viewport.View = _viewport.View with
        {
            Sky = sky,
            Fog = FogButton.IsChecked == true,
            Ground = GroundButton.IsChecked == true,
            Helper = HelperButton.IsChecked == true ? HelperModel.Player : HelperModel.None,
            Gizmos = GizmoButton.IsChecked == true,
        };
        if (skyChanged)
            RequestLint();
    }

    // Preview tools: the button switches the tool on or off and shows its settings in the options bar.
    private void OnPreviewTool(object sender, RoutedEventArgs e)
    {
        var button = (ToggleButton)sender;
        string tool = (string)button.Tag;
        var s = _pipeline.Settings;
        bool on = button.IsChecked == true;
        switch (tool)
        {
            case "circle":
                _pipeline.Settings = s with { Motion = on ? MotionKind.Circle : MotionKind.Still };
                break;
            case "stress":
                _pipeline.Settings = s with { Copies = on ? _copies : 1 };
                break;
            case "lowfps":
                _pipeline.Settings = s with { LowFps = on ? _lowFps : 0 };
                break;
        }
        ShowOptions(on || tool == "camera" ? tool : "select");
        if (tool == "camera" && !on)
            ShowOptions("select");
    }

    // "select" = whatever viewport tool is active (a preview tool switched off hands the bar back to it).
    private void ShowOptions(string what)
    {
        if (what == "select")
            what = _toolTag;
        _options = what;
        var s = _pipeline.Settings;
        CircleOptions.Visibility = what == "circle" ? Visibility.Visible : Visibility.Collapsed;
        StressOptions.Visibility = what == "stress" ? Visibility.Visible : Visibility.Collapsed;
        LowFpsOptions.Visibility = what == "lowfps" ? Visibility.Visible : Visibility.Collapsed;
        CameraOptions.Visibility = what == "camera" ? Visibility.Visible : Visibility.Collapsed;
        ShowToolOptions(what);
        OptionsTitle.Text = Loc.T("Opt." + what);
        string N(float f) => f.ToString("0.###", CultureInfo.InvariantCulture);
        RadiusBox.Text = N(s.CircleRadius);
        SpeedBox.Text = N(s.CircleSpeed);
        AxisX.IsChecked = s.CircleAxis == CircleAxis.X;
        AxisY.IsChecked = s.CircleAxis == CircleAxis.Y;
        AxisZ.IsChecked = s.CircleAxis == CircleAxis.Z;
        CopiesBox.Text = _copies.ToString(CultureInfo.InvariantCulture);
        SpacingBox.Text = N(s.Spacing);
        LowFpsBox.Text = N(_lowFps);
        FovBox.Text = N(_viewport.Camera.Fov);
        SeedBox.Text = s.Seed.ToString(CultureInfo.InvariantCulture);
        StepBox.Text = N(s.Step);
        FirstFrameBox.Text = N(s.FirstFrame);
        CameraButton.IsChecked = what == "camera";
    }

    private void OnAxis(object sender, RoutedEventArgs e)
    {
        var axis = Enum.Parse<CircleAxis>((string)((FrameworkElement)sender).Tag);
        _pipeline.Settings = _pipeline.Settings with { CircleAxis = axis };
        ShowOptions(_options);
    }

    private void OnOptionKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            CommitOption((TextBox)sender);
    }

    private void OnOptionLost(object sender, KeyboardFocusChangedEventArgs e) => CommitOption((TextBox)sender);

    // Out-of-range input is clamped and said in a toast; unreadable input keeps the previous value.
    private void CommitOption(TextBox box)
    {
        var s = _pipeline.Settings;
        bool ok = double.TryParse(box.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v);
        float F(double min, double max, string label)
        {
            double c = Math.Clamp(v, min, max);
            if (c != v)
                Toast(Loc.F("Toast.Clamped", Loc.T(label), Loc.Num(c)), "warn");
            return (float)c;
        }
        if (ok && CommitToolOption(box, v, F))
        {
        }
        else if (ok)
        {
            if (box == RadiusBox) s = s with { CircleRadius = F(0.1, 100, "Lbl.Radius") };
            else if (box == SpeedBox) s = s with { CircleSpeed = F(0.1, 100, "Lbl.Speed") };
            else if (box == CopiesBox) { _copies = (int)F(1, 400, "Lbl.Copies"); if (s.Copies > 1) s = s with { Copies = _copies }; }
            else if (box == SpacingBox) s = s with { Spacing = F(0.5, 1000, "Lbl.Spacing") };
            else if (box == LowFpsBox) { _lowFps = F(1, 60, "Lbl.LowFps"); if (s.LowFps > 0) s = s with { LowFps = _lowFps }; }
            else if (box == FovBox) _viewport.Camera.Fov = F(5, 120, "Lbl.Fov");
            else if (box == SeedBox) s = s with { Seed = (ulong)F(0, uint.MaxValue, "Lbl.Seed") };
            else if (box == StepBox) s = s with { Step = F(0.001, 0.5, "Lbl.StepSize") };
            else if (box == FirstFrameBox) s = s with { FirstFrame = F(0, EffectSim.MaxFrame, "Lbl.FirstFrame") };
        }
        if (s != _pipeline.Settings)
            _pipeline.Settings = s;
        ShowOptions(_options);
    }

    private void OnPlay(object sender, RoutedEventArgs e) => TogglePlay();

    public void TogglePlay()
    {
        _pipeline.Playing = !_pipeline.Playing;
        UpdateButtons();
    }

    private void OnRestart(object sender, RoutedEventArgs e) => _pipeline.Restart();

    private void OnStep(object sender, RoutedEventArgs e) => StepPreview();

    private void OnCapture(object sender, RoutedEventArgs e)
    {
        var image = _viewport.CaptureFrame(true);
        if (image == null)
            return;
        var dialog = new SaveFileDialog { Filter = "PNG (*.png)|*.png", FileName = "view.png" };
        if (dialog.ShowDialog(this) != true)
            return;
        ImageFiles.Save(dialog.FileName, Opaque(image));
        Toast(Loc.F("Toast.ShotSaved", dialog.FileName), "ok");
    }

    // Alpha blending leaves the target's alpha below 1 under particles; viewers would composite the PNG over white.
    public static RgbaImage Opaque(RgbaImage image)
    {
        for (int i = 3; i < image.Pixels.Length; i += 4)
            image.Pixels[i] = 255;
        return image;
    }

    private void OnPreviewBuilt(EffectDoc doc, double totalMs, double workerMs)
    {
        _lastBuildMs = totalMs;
        _lastWorkerMs = workerMs;
        PreviewBuilt?.Invoke(doc, totalMs, workerMs);
    }

    private readonly Stopwatch _frameClock = Stopwatch.StartNew();
    private double _lastFrame;

    private void OnRendered()
    {
        _pipeline.Tick();
        TimelinePanel.Tick();
        TickSounds();
        double now0 = _frameClock.Elapsed.TotalSeconds, dt = now0 - _lastFrame;
        _lastFrame = now0;
        if (SlicerPanel.IsVisible)
            _slicer.Tick(dt, _slicer.Doc != null ? _pipeline.PreviewOf(_slicer.Doc) : null);
        AtlasPanel.Tick(dt);
        CurvesPanel.Plot.Playhead = MasterPlayhead();
        double now = _statusClock.Elapsed.TotalMilliseconds;
        if (now - _lastStatus < StatusIntervalMs)
            return;
        _lastStatus = now;
        UpdateStatus();
        CheckPanel.Tick();
    }

    // Effect time 0..1 of the curve editor's emitter in the shown frame (its master graphs' x).
    private double? MasterPlayhead()
    {
        var ed = CurvesPanel.Editor;
        if (!ed.IsMaster || ed.Doc == null || ed.Emitter == null || _pipeline.PreviewOf(ed.Doc) is not { Sims.Count: > 0 } p)
            return null;
        int i = ed.Doc.Ptc.Emitters.ToList().FindIndex(e => e.Block == ed.Emitter.Block);
        return i >= 0 && i < p.Sims[0].Emitters.Count ? p.Sims[0].Emitters[i].EffectFraction : null;
    }

    private double _lastSeekMs = -1;
    private double _lastSeekWorkerMs;

    private void OnSeeked(EffectDoc doc, double workerMs, double totalMs)
    {
        _lastSeekMs = totalMs;
        _lastSeekWorkerMs = workerMs;
        Seeked?.Invoke(doc, workerMs, totalMs);
    }

    public event Action<EffectDoc, double, double>? Seeked;

    private void UpdateStatus()
    {
        _state.Status.Clear();
        var stats = _viewport.Stats;
        int alive = _viewport.Previews.Sum(p => p.AliveTotal);
        _state.Status.Add(new StatusItem(Loc.F("Status.Particles", alive), Loc.T("Tip.Status.Particles"), "B.Text"));
        string gpu = stats == null || double.IsNaN(stats.GpuMs) ? Loc.T("Ov.Na") : Loc.F("Ov.Ms", stats.GpuMs);
        _state.Status.Add(new StatusItem(Loc.F("Status.Frame", _viewport.FrameIntervalMs, _viewport.CpuFrameMs, gpu), Loc.T("Tip.Status.Frame")));
        if (_pipeline.AnyBusy)
            _state.Status.Add(new StatusItem(Loc.T("Status.Building"), Loc.T("Tip.Status.Building"), "B.Accent"));
        else if (_lastBuildMs >= 0)
            _state.Status.Add(new StatusItem(Loc.F("Status.Built", _lastBuildMs), Loc.F("Tip.Status.Built", _lastWorkerMs)));
        if (_lastSeekMs >= 0)
            _state.Status.Add(new StatusItem(Loc.F("Status.Seek", _lastSeekMs), Loc.F("Tip.Status.Seek", _lastSeekWorkerMs)));
        if (_docs.Count > 0)
            _state.Status.Add(LintStatus());
        int dirty = _docs.Count(d => d.Dirty);
        if (dirty > 0)
            _state.Status.Add(new StatusItem(Loc.F("Status.Dirty", dirty), Loc.T("Tip.Status.Dirty"), "B.Key"));
        _state.Status.Add(new StatusItem(Loc.F("Status.Roots", _roots.Roots.Count), string.Join("\n", _roots.Roots.DefaultIfEmpty(Loc.T("Val.None")))));
    }

    private void ComposeOverlay(OverlayBuilder o)
    {
        var previews = _viewport.Previews;
        var lines = new List<OverlayLine>();
        if (previews.Count == 0)
        {
            lines.Add(new(Loc.T(_docs.Count > 0 ? "Ov.Building" : "Ov.NoEffect"), Palette.Text));
        }
        else
        {
            var first = previews[0];
            var stats = _viewport.Stats;
            string gpu = stats == null || double.IsNaN(stats.GpuMs) ? Loc.T("Ov.Na") : Loc.F("Ov.Ms", stats.GpuMs);
            string state = (first.Playing ? "" : Loc.T("Ov.Paused")) + (first.Settings.LowFps > 0 ? Loc.F("Ov.LowFps", first.Settings.LowFps) : "");
            lines.Add(new(Loc.F("Ov.Time", first.ShownTime, state), Palette.TextHi));
            lines.Add(new(Loc.F("Ov.Frame", _viewport.FrameIntervalMs, _viewport.CpuFrameMs, stats?.SimMs ?? 0, stats?.UploadMs ?? 0, gpu), Palette.Text));
            lines.Add(new(Loc.F("Ov.Particles", previews.Sum(p => p.AliveTotal), stats?.Draws ?? 0, first.Sims.Count), Palette.Text));
            if (first.Settings.Motion == MotionKind.Circle)
                lines.Add(new(Loc.F("Ov.Circle", first.Settings.CircleRadius, first.Settings.CircleSpeed, first.Settings.CircleAxis), Palette.Path));
            if (_active?.Motion is { } motion)
                lines.Add(new(motion.IsPath ? Loc.F("Ov.ScriptPath", motion.Points!.Length, motion.Times![^1]) : Loc.F("Ov.ScriptSpin", motion.SpinRate), Palette.Path));
            if (_viewport.DeviceResets > 0)
                lines.Add(new(Loc.F("Ov.Resets", _viewport.DeviceResets), Palette.TextDim));
            lines.AddRange(ProjectOverlayLines());
            const int EmitterLines = 5;
            int room = Math.Min(EmitterLines, Math.Max(0, (int)((_viewport.PixelHeight - 3 * o.LineHeight - 40) / o.LineHeight) - lines.Count - 1));
            int swaps = _viewport.Draws.Count(d => d.Copy == 0 && d.MaySwap);
            lines.Add(new(Loc.F("Ov.Emitters", previews.Sum(p => p.Params.Count), swaps), Palette.Text));
            // The layers panel lists every emitter; the overlay names only those the game shows differently.
            var emitterLines = new List<OverlayLine>();
            for (int i = 0; i < previews.Count; i++)
            {
                var p = previews[i];
                for (int e = 0; e < p.Params.Count; e++)
                {
                    var flags = Flags(p, e, false);
                    if (flags.Count > 0)
                        emitterLines.Add(new(Loc.F("Ov.Emitter", p.Params[e].Name, p.Sims[0].Emitters[e].Count, p.Params[e].MaxNum) + " · " + string.Join(" · ", flags), Palette.Warn));
                }
            }
            if (emitterLines.Count > room)
            {
                int shown = Math.Max(0, room - 1);
                int hidden = emitterLines.Count - shown;
                emitterLines = emitterLines.Take(shown).ToList();
                emitterLines.Add(new(Loc.F("Ov.More", hidden), Palette.TextDim));
            }
            lines.AddRange(emitterLines);
        }
        var panel = Palette.BgDeep.WithAlpha(0.75f);
        o.Panel(8, 8, lines, panel);
        o.Panel(8, _viewport.PixelHeight - o.LineHeight - 16, new[] { new OverlayLine(Loc.T("Ov.Help"), Palette.TextDim) }, panel);
        ComposeProjectOverlay(o);
        // The tool's handles over the text panels: they are what the cursor aims at.
        ComposeTools(o);
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
