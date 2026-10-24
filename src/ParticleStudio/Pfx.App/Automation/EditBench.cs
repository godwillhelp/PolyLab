using System.Diagnostics;
using System.Globalization;
using System.Text;
using Pfx.App.Core;
using Pfx.App.Views;

namespace Pfx.App.Automation;

// --edit-bench <file> [--time s]: how long an edit takes to reach the viewport for the opened effects. The scene is run
// to --time first (a seek, once); then a value of the first emitter is changed ten times, each time waiting for the
// rebuilt preview to replace the old one. Reports the UI-thread cost of the edit and the edit-to-screen time.
public sealed class EditBench
{
    private const int Edits = 10;

    private readonly MainWindow _w;
    private readonly Launch _launch;
    private TaskCompletionSource<(double Total, double Worker)>? _built;

    public EditBench(MainWindow window, Launch launch)
    {
        _w = window;
        _launch = launch;
    }

    public void Start()
    {
        _w.PreviewBuilt += (_, total, worker) => _built?.TrySetResult((total, worker));
        _ = Run();
    }

    private async Task NextFrame()
    {
        var f = new TaskCompletionSource();
        void Once() { f.TrySetResult(); }
        _w.Viewport.Rendered += Once;
        await f.Task;
        _w.Viewport.Rendered -= Once;
    }

    private async Task Run()
    {
        var sb = new StringBuilder();
        void Line(string s)
        {
            sb.AppendLine(s);
            File.WriteAllText(_launch.EditBench!, sb.ToString());
        }
        var first = new TaskCompletionSource();
        _w.Viewport.Rendered += () => first.TrySetResult();
        await first.Task;
        while (_w.Pipeline.AnyBusy || _w.Viewport.Previews.Count < _w.Docs.Count)
            await NextFrame();
        var seek = Stopwatch.StartNew();
        foreach (var p in _w.Viewport.Previews)
            p.Seek(_launch.Time);
        Line(string.Format(CultureInfo.InvariantCulture, "effects: {0}; copies {1}; seek to {2} s on the UI thread (setup, once): {3:0} ms; alive {4}",
            string.Join(", ", _w.Docs.Select(d => d.Title)), _launch.Copies, _launch.Time, seek.Elapsed.TotalMilliseconds, _w.Viewport.Previews.Sum(p => p.AliveTotal)));
        // The part with the most live particles: its rebuild and seek are the slowest.
        // The first frame after the jump renders every particle for the first time (P6: 168 ms of CPU for the tornado);
        // it belongs to the setup, so the edits start after it.
        var settle = Stopwatch.StartNew();
        await NextFrame();
        Line($"first frame after the seek: {settle.Elapsed.TotalMilliseconds:0} ms, CPU {_w.Viewport.CpuFrameMs:0.0} ms (setup)");
        var doc = _w.Docs.OrderByDescending(d => _w.Pipeline.PreviewOf(d)?.AliveTotal ?? 0).First();
        var emitter = doc.Ptc.Emitters[0];
        Line($"edited part: {doc.Title}, {_w.Pipeline.PreviewOf(doc)?.AliveTotal} alive, emitter {emitter.Name}");
        double baseValue = emitter.Number("Velocity") ?? 2;
        var ui = new List<double>();
        var total = new List<double>();
        var worker = new List<double>();
        for (int i = 0; i < Edits; i++)
        {
            _built = new TaskCompletionSource<(double, double)>();
            var clock = Stopwatch.StartNew();
            doc.Do(emitter.Set("Velocity", baseValue + 0.1 * (i + 1)), "bench");
            ui.Add(clock.Elapsed.TotalMilliseconds);
            var (t, w) = await _built.Task;
            total.Add(t);
            worker.Add(w);
            Line(string.Format(CultureInfo.InvariantCulture, "edit {0}: UI thread {1:0.0} ms, worker build+seek {2:0.0} ms, edit to screen {3:0.0} ms, alive {4}",
                i + 1, ui[^1], w, t, _w.Viewport.Previews.Sum(p => p.AliveTotal)));
        }
        string Stat(List<double> v) { var s = v.OrderBy(x => x).ToList(); return string.Format(CultureInfo.InvariantCulture, "median {0:0.0} ms, max {1:0.0} ms", s[s.Count / 2], s[^1]); }
        Line("UI thread per edit: " + Stat(ui));
        Line("worker build + seek: " + Stat(worker));
        Line("edit to screen: " + Stat(total));

        // Scrubbing (P6): the same times twice. Before the first pass the background fill of the rebuilt part runs to
        // its end, as it does while the user looks at the result of an edit.
        var fill = Stopwatch.StartNew();
        while (_w.Pipeline.AnyFilling)
            await NextFrame();
        Line(string.Format(CultureInfo.InvariantCulture, "background fill after the last edit: done {0:0} ms later; checkpoints {1}, {2:0.0} MB",
            fill.Elapsed.TotalMilliseconds, _w.Viewport.Previews.Sum(p => p.Checkpoints.Count), _w.Viewport.Previews.Sum(p => p.CheckpointBytes) / 1048576.0));
        _w.Pipeline.Playing = false;
        float[] times = { 7.3f, 2.1f, 9.9f, 0.5f, 5.5f, 3.3f, 8.8f, 1.2f, 6.6f, 4.4f, 9.1f, 2.9f, 12.5f, 15f };
        Pfx.Textures.RgbaImage? reference = null;
        for (int pass = 1; pass <= 2; pass++)
        {
            var screen = new List<double>();
            var work = new List<double>();
            foreach (float t in times)
            {
                double worst = 0, worstWork = 0;
                void OnSeek(Model.EffectDoc d, double w, double total) { worst = Math.Max(worst, total); worstWork = Math.Max(worstWork, w); }
                _w.Seeked += OnSeek;
                _w.Pipeline.Seek(t);
                while (_w.Pipeline.AnySeeking)
                    await NextFrame();
                _w.Seeked -= OnSeek;
                screen.Add(worst);
                work.Add(worstWork);
                if (t == 5.5f)
                {
                    var frame = _w.Viewport.CaptureFrame(false);
                    if (reference == null)
                        reference = frame;
                    else if (frame != null)
                    {
                        int differ = 0;
                        for (int i = 0; i < frame.Pixels.Length; i++)
                            if (frame.Pixels[i] != reference.Pixels[i])
                                differ++;
                        Line($"scrub to 5.5 s in pass 1 and pass 2: {differ} differing bytes of {frame.Pixels.Length}");
                    }
                }
            }
            Line($"scrub pass {pass} over {times.Length} times (request to frame on screen, worst part): " + Stat(screen) + "; worker " + Stat(work));
        }
        while (doc.Undo.CanUndo)
            doc.UndoStep();
        _w.Close();
    }
}
