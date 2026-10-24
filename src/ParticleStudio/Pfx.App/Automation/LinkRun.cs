using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows.Threading;
using Pfx.App.Core;
using Pfx.App.Views;
using Pfx.Link;

namespace Pfx.App.Automation;

// --link-bench <dir> [--link-pid <client pid>] <part.ptc>: the P11 acceptance through the tool. The link starts, the
// opened part goes to the game when the client says hello; the engine's originals are compared with the file; Velocity
// is edited twelve times through the document (as the inspector does) and each edit's read-back is timed; after an
// idle control period the shape is edited 55 times (each a reload) with the client's memory read every fifth; then a
// game frame with the viewport camera, an empty baseline series and an effect series. Everything waits on the link's
// own events; the only clocks are the idle control period and the shot's effect age (measuring inputs, not waits for
// something to settle).
public sealed class LinkRun
{
    private const int VelocityEdits = 12;
    private const int ShapeEdits = 55;
    private const int SeriesFrames = 20;
    private static readonly TimeSpan IdleWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan IdleStep = TimeSpan.FromSeconds(5);
    // How old the part's game source is when the comparison frame is taken: a test input, long enough for the effect
    // to build up several particles (a just-spawned source shows one).
    private static readonly TimeSpan ShotAge = TimeSpan.FromSeconds(3);

    private readonly MainWindow _w;
    private readonly string _dir;
    private readonly int _pid;
    private readonly StringBuilder _log = new();
    private TaskCompletionSource<LinkTiming>? _timed;
    private TaskCompletionSource<GameShot>? _shot;
    private TaskCompletionSource<IReadOnlyList<OriginalRow>>? _originals;
    private TaskCompletionSource? _inGame;
    private TaskCompletionSource? _frame;

    public LinkRun(MainWindow window, Launch launch)
    {
        _w = window;
        _dir = Path.GetFullPath(launch.LinkBench!);
        _pid = launch.LinkPid;
    }

    public void Start()
    {
        Directory.CreateDirectory(_dir);
        _w.Viewport.Rendered += () => _frame?.TrySetResult();
        _w.Live.Timed += t => _timed?.TrySetResult(t);
        _w.Live.ShotTaken += s => _shot?.TrySetResult(s);
        _w.Live.Originals += (_, rows) => _originals?.TrySetResult(rows);
        _w.Live.PartChanged += p => { if (p.InGame) _inGame?.TrySetResult(); };
        _ = Run();
    }

    private void Log(string line)
    {
        _log.AppendLine(line);
        File.WriteAllText(Path.Combine(_dir, "link.txt"), _log.ToString());
    }

    private Task Frame()
    {
        _frame = new TaskCompletionSource();
        return _frame.Task;
    }

    private double MemoryMb()
    {
        if (_pid == 0)
            return 0;
        try
        {
            using var p = Process.GetProcessById(_pid);
            return p.PrivateMemorySize64 / 1048576.0;
        }
        catch (ArgumentException)
        {
            return 0;
        }
    }

    private static string Stat(List<double> v)
    {
        var s = v.OrderBy(x => x).ToList();
        return string.Format(CultureInfo.InvariantCulture, "median {0:0.0} ms, min {1:0.0}, max {2:0.0} (n={3})", s[s.Count / 2], s[0], s[^1], s.Count);
    }

    private async Task Run()
    {
        try
        {
            var doc = _w.Docs.FirstOrDefault() ?? throw new InvalidOperationException("--link-bench needs one .ptc to open");
            _inGame = new TaskCompletionSource();
            _w.StartLink();
            Log($"link: state {_w.Live.Hub.State}, port {_w.Live.Hub.Port}, stage {_w.Live.Stage?.Folder}");
            await _inGame.Task;
            var client = _w.Live.Hub.Client!;
            var part = _w.Live.PartOf(doc)!;
            Log($"client {client.Name} {client.Width}x{client.Height} filePatching={client.FilePatching} autotest={client.AutoTest} profiles={client.Profiles}");
            Log($"game enum ({client.Params.Count}): {string.Join(" ", client.Params)}");
            Log($"part {part.Name} in game: {part.GamePath}, emitters {string.Join(",", part.GameEmitters)}");

            _originals = new TaskCompletionSource<IReadOnlyList<OriginalRow>>();
            _w.Live.RequestOriginals(doc);
            var rows = await _originals.Task;
            foreach (var r in rows)
                Log($"original {r.Emitter}.{r.Key}: file [{r.File}] game [{r.Game}] {(r.Matches ? "same" : "DIFFERS")}");
            Log($"originals: {rows.Count(r => r.Matches)} of {rows.Count} equal");

            var emitter = doc.Ptc.Emitters[0];
            double v0 = emitter.Number("Velocity") ?? 2;
            var sets = new List<double>();
            for (int i = 0; i < VelocityEdits; i++)
            {
                _timed = new TaskCompletionSource<LinkTiming>();
                doc.Do(emitter.Set("Velocity", Math.Round(v0 + 0.5 * (i + 1), 3)), "link bench");
                var t = await _timed.Task;
                sets.Add(t.Ms);
                Log(string.Format(CultureInfo.InvariantCulture, "velocity edit {0}: {1} {2:0.0} ms, {3}, read back equal {4}", i + 1, t.Kind, t.Ms, t.What, t.Matches));
            }
            Log("velocity edit -> game read-back: " + Stat(sets));

            for (var at = TimeSpan.Zero; at <= IdleWindow; at += IdleStep)
            {
                Log(string.Format(CultureInfo.InvariantCulture, "idle {0:0} s: client private memory {1:0.0} MB", at.TotalSeconds, MemoryMb()));
                if (at < IdleWindow)
                    await Task.Delay(IdleStep);
            }
            doc.Do(emitter.Set("ShapeType", 1), "link bench box");
            _timed = new TaskCompletionSource<LinkTiming>();
            await _timed.Task;
            double before = MemoryMb();
            var reloads = new List<double>();
            for (int i = 0; i < ShapeEdits; i++)
            {
                _timed = new TaskCompletionSource<LinkTiming>();
                double s = Math.Round(0.2 + 0.05 * i, 3);
                doc.Do(emitter.Set("ShapeSize", s, 0.2, s), "link bench shape");
                var t = await _timed.Task;
                reloads.Add(t.Ms);
                if ((i + 1) % 5 == 0)
                    Log(string.Format(CultureInfo.InvariantCulture, "shape edit {0}: {1} {2:0.0} ms; client private memory {3:0.0} MB; stage {4} versions, {5} files, {6} bytes",
                        i + 1, t.Kind, t.Ms, MemoryMb(), _w.Live.Stage!.Versions, _w.Live.Stage.FilesWritten, _w.Live.Stage.BytesWritten));
            }
            Log("shape edit -> new version spawned in game: " + Stat(reloads));
            Log(string.Format(CultureInfo.InvariantCulture, "memory over {0} reloads: {1:0.0} -> {2:0.0} MB", ShapeEdits, before, MemoryMb()));

            _w.ApplyView(Pfx.Render.ViewPreset.Free);
            _w.Viewport.Camera.Distance = 7;
            for (int i = 0; i < 3; i++)
                await Frame();
            await Task.Delay(ShotAge);
            _shot = new TaskCompletionSource<GameShot>();
            _w.GameShot("accept");
            var shot = await _shot.Task;
            Log(string.Format(CultureInfo.InvariantCulture, "game shot {0}: {1}x{2}, game time {3} ms, frame {4:0.0} ms, round trip {5:0} ms, game fov {6:0.0000} rad ({7:0.0} deg; viewport {8:0.0} deg vertical), file {9}",
                shot.Name, shot.Width, shot.Height, shot.GameTime, shot.FrameMs, shot.RoundTripMs, shot.Fov, shot.Fov * 180 / Math.PI, _w.Viewport.Camera.Fov, shot.File));
            await _w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Log(string.Format(CultureInfo.InvariantCulture, "viewport frame beside it: preview seeked to the game source's age {0:0.00} s", _w.GamePanelView.LastViewAge));
            File.Copy(shot.File, Path.Combine(_dir, "game_accept.bmp"), true);
            if (_w.Viewport.CaptureFrame(false) is { } view)
                Pfx.Textures.ImageFiles.Save(Path.Combine(_dir, "view_accept.png"), MainWindow.Opaque(view));

            foreach (bool baseline in new[] { true, false })
            {
                if (!baseline)
                    _w.Live.Resync();
                if (!baseline)
                {
                    _inGame = new TaskCompletionSource();
                    await _inGame.Task;
                }
                var done = new TaskCompletionSource();
                int got = 0;
                void Count(GameShot _) { if (++got >= SeriesFrames) done.TrySetResult(); }
                _w.Live.ShotTaken += Count;
                _w.GameSeries(SeriesFrames, baseline);
                await done.Task;
                _w.Live.ShotTaken -= Count;
                await _w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                var st = _w.GamePanelView.LastSeries;
                Log(string.Format(CultureInfo.InvariantCulture, "{0} series: {1} frames, identical pairs {2}, mean |diff| {3:0.00}, jumping >= 40: {4:0.000} % (max {5:0.000} %)",
                    baseline ? "empty baseline" : "effect", st?.Frames, st?.IdenticalPairs, st?.MeanDiff, st?.JumpPercent, st?.MaxJumpPercent));
            }

            _w.WindowState = System.Windows.WindowState.Maximized;
            _w.ShowPane("gameref");
            await _w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            await Frame();
            await Frame();
            var root = (System.Windows.FrameworkElement)_w.Content;
            double dpi = System.Windows.Media.VisualTreeHelper.GetDpi(_w).DpiScaleX;
            ShellChecks.SaveShot(_w, root, _w.ViewportArea, dpi, Path.Combine(_dir, "tool_gameref.png"));
            Log($"tool shot: window {_w.ActualWidth:0}x{_w.ActualHeight:0}; panel log: {string.Join(" | ", _w.GamePanelView.LogLines)}");
            while (doc.Undo.CanUndo)
                doc.UndoStep();
            Log("RESULT: done");
        }
        catch (Exception ex)
        {
            Log("FAILED: " + ex);
        }
        _w.Close();
    }
}
