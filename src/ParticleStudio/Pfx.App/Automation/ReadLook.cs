using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Pfx.App.Core;
using Pfx.App.Inspector;
using Pfx.App.Lint;
using Pfx.App.Views;
using Pfx.Sim;

namespace Pfx.App.Automation;

// --read-look <dir> [--time <s>]: what the game reads of the graphs (CG8), on screen in the maximised window. For each
// opened effect and graph key, the first emitter whose game read (Graph.EvaluateCpu for the CPU-read keys, else the
// GPU texels) is off the spline is opened from its inspector thumbnail and photographed with the curve editor in front
// (read_<n>.png); then the viewport at --time (viewport.png). look.txt lists each target with its read and the largest
// gap between read and spline. With --first-frame <s> (CG10) the first-frame pass follows (FirstFrames). Then exit.
public sealed class ReadLook
{
    private const int Samples = 600;
    private readonly MainWindow _w;
    private readonly Launch _launch;
    private readonly string _dir;
    private readonly StringBuilder _log = new();
    private TaskCompletionSource? _frame;

    public ReadLook(MainWindow window, Launch launch)
    {
        _w = window;
        _launch = launch;
        _dir = Path.GetFullPath(launch.ReadLook!);
    }

    public void Start()
    {
        Directory.CreateDirectory(_dir);
        _w.Viewport.Rendered += () => _frame?.TrySetResult();
        _ = Run();
    }

    private void Log(string line)
    {
        _log.AppendLine(line);
        File.WriteAllText(Path.Combine(_dir, "look.txt"), _log.ToString());
    }

    private Task Frame()
    {
        _frame = new TaskCompletionSource();
        return _frame.Task;
    }

    private async Task Settled()
    {
        await _w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await _w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
    }

    private static float Gap(Graph g, string key, int channels)
    {
        bool cpu = Graph.ReadOnCpu(key);
        float gap = 0;
        for (int c = 0; c < channels; c++)
            for (int s = 0; s <= Samples; s++)
            {
                float t = s / (float)Samples;
                float read = cpu ? g.EvaluateCpu(t, c) : g.EvaluateGpu(t, c);
                gap = Math.Max(gap, Math.Abs(read - g.Evaluate(t, c)));
            }
        return gap;
    }

    private async Task Shot(string name)
    {
        await Settled();
        await Frame();
        await Settled();
        var root = (FrameworkElement)_w.Content;
        ShellChecks.SaveShot(_w, root, _w.ViewportArea, VisualTreeHelper.GetDpi(_w).DpiScaleX, Path.Combine(_dir, name + ".png"));
    }

    // CG10: the first-frame field typed like a user (camera options, value, Enter), the viewport at --time for 0 and the
    // given length with every emitter's births, then the check panel with its FrameLoss / ColdCut rows.
    private async Task FirstFrames(float first)
    {
        _w.CameraButton.IsChecked = true;
        _w.CameraButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        foreach (float f in new[] { 0f, first })
        {
            var box = _w.FirstFrameBox;
            box.Text = f.ToString(CultureInfo.InvariantCulture);
            box.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(box)!, 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
            await Frame();
            while (_w.Pipeline.AnyBusy || _w.Viewport.Previews.Count < _w.Docs.Count)
                await Frame();
            _w.Pipeline.Seek(_launch.Time);
            while (_w.Pipeline.AnySeeking)
                await Frame();
            string name = string.Format(CultureInfo.InvariantCulture, "first_{0:0}ms", f * 1000);
            await Shot(name);
            foreach (var p in _w.Viewport.Previews)
                Log(string.Format(CultureInfo.InvariantCulture, "{0}: field {1}, settings {2} s, at {3:0.###} s: {4}", name, box.Text,
                    p.Settings.FirstFrame, p.ShownTime,
                    string.Join(", ", p.Params.Select((e, i) => $"{e.Name} {p.Sims[0].Emitters[i].Born}/{p.Sims[0].Emitters[i].Count}"))));
        }
        _w.ShowPane("checks");
        await Frame();
        while (_w.LintBusy)
            await Frame();
        await Settled();
        var lint = _w.Lint;
        foreach (var i in lint.Issues.Where(i => i.Rule == Linter.FrameLoss || i.Rule == Linter.ColdCut))
            Log($"checks: {i.Rule.Id} {lint.Parts[i.Part]} {lint.EmitterNames[i.Part][i.Emitter]} {string.Join(" | ", i.Args)}");
        foreach (var rule in new[] { Linter.FrameLoss, Linter.ColdCut })
        {
            if (lint.Issues.FirstOrDefault(i => i.Rule == rule) is not { } issue)
                continue;
            string fix = Loc.F("Lint." + rule.Id + ".Fix", issue.Args);
            var row = InspectorView.Descendants(_w.CheckTab).OfType<TextBlock>().FirstOrDefault(t => t.Text == fix);
            row?.BringIntoView();
            await Shot("checks_" + rule.Id);
            Log($"checks_{rule.Id}: row {(row == null ? "not found" : "brought into view")}");
        }
    }

    private async Task Run()
    {
        try
        {
            while (_w.Pipeline.AnyBusy || _w.Viewport.Previews.Count < _w.Docs.Count)
                await Frame();
            _w.WindowState = WindowState.Maximized;
            _w.ShowPane("curves");
            int n = 0;
            foreach (var doc in _w.Docs)
            {
                var done = new HashSet<string>();
                foreach (var emitter in doc.Ptc.Emitters)
                {
                    _w.Select(doc, new[] { emitter.Block }, false);
                    await Settled();
                    foreach (var field in _w.EmitterInspector.AllFields.OfType<GraphField>().ToList())
                    {
                        if (done.Contains(field.Key) || field.Numbers.Length == 0)
                            continue;
                        float gap = Gap(field.Curve, field.Key, field.Channels);
                        if (gap < 1 / 255f)
                            continue;
                        done.Add(field.Key);
                        field.Open();
                        string name = $"read_{++n:00}";
                        await Shot(name);
                        string where = Graph.ReadOnCpu(field.Key) ? "CPU, 32 samples" : "GPU, 256 texels";
                        Log(string.Format(CultureInfo.InvariantCulture, "{0}: {1} {2} {3} ({4}), editor shows {5}, largest gap read vs spline {6:0.000}",
                            name, Path.GetFileName(doc.Path), emitter.Name, field.Key, where, _w.Curves.Editor.Active.Key, gap));
                    }
                }
            }
            _w.ShowPane("viewport");
            _w.Pipeline.Seek(_launch.Time);
            while (_w.Pipeline.AnySeeking)
                await Frame();
            await Shot("viewport");
            Log(string.Format(CultureInfo.InvariantCulture, "viewport: effects at {0} s", _launch.Time));
            if (_launch.FirstFrame is float first)
                await FirstFrames(first);
        }
        catch (Exception ex)
        {
            Log("look failed: " + ex);
        }
        _w.Close();
    }
}
