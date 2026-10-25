using System.Numerics;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Pfx.App.Lint;
using Pfx.App.Views;
using Pfx.Render;

namespace Pfx.App.Automation;

// --tornado <dir> [--tornado-donor <ptc>]: the P12 checks in the real window. The tornado generator writes its parts
// and project into <dir>\tornado (the shell's own GenerateTornado), the states play, the lint and the budget run on
// their workers, the viewport is photographed from afar in the normal, layers and overdraw views and the window with
// the check panel; then a part with a missing material is opened and the export must refuse to write. Logs to
// <dir>\tornado.txt.
public sealed class TornadoRun
{
    // A view that holds the whole 65 m funnel and its cap: target at mid height, 220 m back, a little from above.
    private static readonly Vector3 Target = new(0, 45, 0);
    private const float Distance = 220;

    private readonly MainWindow _w;
    private readonly string _dir;
    private readonly string? _donor;
    private readonly StringBuilder _log = new();
    private TaskCompletionSource? _frame;

    public TornadoRun(MainWindow window, Pfx.App.Core.Launch launch)
    {
        _w = window;
        _dir = Path.GetFullPath(launch.Tornado!);
        _donor = launch.TornadoDonor;
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
        File.WriteAllText(Path.Combine(_dir, "tornado.txt"), _log.ToString());
    }

    private async Task Settle()
    {
        while (_w.Pipeline.AnyBusy || _w.Pipeline.AnySeeking || _w.LintBusy || _w.BudgetBusy || _w.Viewport.Previews.Count < _w.Docs.Count)
            await Frame();
        await _w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        await Frame();
        await Frame();
    }

    private void LogLint()
    {
        var report = _w.Lint;
        Log($"lint {_w.LastLintMs:0.0} ms: {report.Count(LintLevel.Block)} block, {report.Count(LintLevel.Warn)} warn, {report.Count(LintLevel.Info)} info");
        foreach (var g in report.Issues.GroupBy(i => i.Rule.Id).OrderBy(g => g.Key))
            Log($"  {g.Key} x{g.Count()}: " + string.Join("; ", g.Take(3).Select(i =>
                (i.Part >= 0 ? report.Parts[i.Part] : "project") + (i.Emitter >= 0 ? "/" + report.EmitterNames[i.Part][i.Emitter] : "") + " [" + string.Join(", ", i.Args) + "]")));
    }

    private async Task Run()
    {
        try
        {
            _w.WindowState = WindowState.Maximized;
            await Frame();
            string folder = Path.Combine(_dir, "tornado");
            var project = _w.GenerateTornado(folder, _donor) ?? throw new InvalidOperationException("generator refused " + folder);
            Log($"generated {project}: {_w.Docs.Count} parts open, donor {_donor ?? "none"}");
            _w.StatesPlaying = true;
            _w.ShowPane("checks");
            await Settle();
            _w.Pipeline.Seek(12);
            _w.Pipeline.Playing = false;
            await Settle();
            LogLint();
            var b = _w.Budget ?? throw new InvalidOperationException("no budget");
            Log($"budget {_w.LastBudgetMs:0.0} ms: peak {b.Peak} at {b.PeakAt:0.0} s of {b.Length:0.0} s, textures {b.TextureBytes / 1048576.0:0.00} MB in {b.Textures}, " +
                $"lights {b.Lights}/{BudgetReport.LightLimit} (total {b.LightsTotal}), hazes {b.Hazes}, sounds {b.Sounds}");
            foreach (var p in b.Parts)
                Log($"  {p.Title}: peak {p.Peak} at {p.PeakAt:0.0} s, MaxNum {p.MaxNum}, textures {p.TextureBytes / 1048576.0:0.00} MB");

            _w.Viewport.Camera.Target = Target;
            _w.Viewport.Camera.Distance = Distance;
            _w.Viewport.Camera.Pitch = -8;
            foreach (var mode in Enum.GetValues<DrawView>())
            {
                _w.DrawMode = mode;
                _w.CheckTab.Refresh();
                await Settle();
                var rows = _w.DrawOrder(out double fill);
                Log($"view {mode}: {rows.Count} layers, {rows.Count(r => r.MaySwap)} may swap, screen fill x{fill:0.00}");
                if (_w.Viewport.CaptureFrame(true) is { } image)
                    Pfx.Textures.ImageFiles.Save(Path.Combine(_dir, $"view_{mode}.png"), MainWindow.Opaque(image));
                var root = (FrameworkElement)_w.Content;
                double dpi = System.Windows.Media.VisualTreeHelper.GetDpi(_w).DpiScaleX;
                ShellChecks.SaveShot(_w, root, _w.ViewportArea, dpi, Path.Combine(_dir, $"tool_{mode}.png"));
            }
            _w.DrawMode = DrawView.Normal;

            string bad = Path.Combine(_dir, "missing_material.ptc");
            File.WriteAllText(bad, "EffectDef {\r\n Emitors {\r\n  EmitorDef lost {\r\n   Material \"pfx_lint/none.emat\"\r\n  }\r\n }\r\n}\r\n");
            _w.Open(new[] { bad });
            _w.ShowPane("export");
            await Settle();
            bool blocked = _w.LintBlocking > 0 && !_w.ExportTab.WriteEnabled;
            Log($"bad part: {_w.LintBlocking} blocking, export write {(_w.ExportTab.WriteEnabled ? "enabled" : "disabled")}");
            {
                var root = (FrameworkElement)_w.Content;
                double dpi = System.Windows.Media.VisualTreeHelper.GetDpi(_w).DpiScaleX;
                ShellChecks.SaveShot(_w, root, _w.ViewportArea, dpi, Path.Combine(_dir, "tool_export_blocked.png"));
            }
            Log(blocked ? "RESULT: done" : "FAILED: export not blocked");
        }
        catch (Exception ex)
        {
            Log("FAILED: " + ex);
        }
        _w.Close();
    }
}
