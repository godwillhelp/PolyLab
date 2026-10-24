using System.Globalization;
using System.Numerics;
using System.Text;
using System.Windows.Threading;
using Pfx.App.Views;
using Pfx.App.Viewport;
using Pfx.Render;
using Pfx.Sim;

namespace Pfx.App.Automation;

// --path <dir>: the P8 acceptance flow inside the real window. The opened effect's first emitter is the one drawn
// for; the source stands at the origin. A side camera (from the east, looking west) and the upright drawing plane are
// set; a stroke (a cubic Bezier from the emitter, sampled unevenly like a hand, 0.9 s of drawing) is drawn through the
// path tool's own Press / Drag / Release; the flight use fits it and writes the keys; the effect is saved. Written to
// <dir>: path.txt (readout, keys, per-point errors), drawn.csv and fitted.csv (world metres, source at the origin) and
// a viewport frame at 5 s. The same file goes to the game, where the trail is photographed from the same side.
public sealed class PathRun
{
    public static readonly Vector3[] Bezier = { new(0, 0, 0), new(0, 4.2f, 2.2f), new(0, 3.0f, 5.8f), new(0, -0.1f, 7.2f) };
    public static readonly Vector3 Eye = new(10, 2.2f, 3.6f), Target = new(0, 2.2f, 3.6f);
    private const int Samples = 54;
    private readonly MainWindow _w;
    private readonly string _dir;
    private readonly StringBuilder _log = new();
    private TaskCompletionSource? _frame;

    public PathRun(MainWindow window, Pfx.App.Core.Launch launch)
    {
        _w = window;
        _dir = Path.GetFullPath(launch.PathRun!);
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
        File.WriteAllText(Path.Combine(_dir, "path.txt"), _log.ToString());
    }

    public static Vector3 At(float u)
    {
        float a = 1 - u;
        return Bezier[0] * (a * a * a) + Bezier[1] * (3 * a * a * u) + Bezier[2] * (3 * a * u * u) + Bezier[3] * (u * u * u);
    }

    private static string Csv(IEnumerable<Vector3> points) =>
        "x,y,z\n" + string.Concat(points.Select(p => string.Format(CultureInfo.InvariantCulture, "{0:0.#####},{1:0.#####},{2:0.#####}\n", p.X, p.Y, p.Z)));

    private async Task Run()
    {
        try
        {
            var doc = _w.Docs[0];
            var emitter = doc.Ptc.Emitters[0];
            _w.Select(doc, new[] { emitter.Block }, false);
            while (_w.Pipeline.AnyBusy || _w.Viewport.Previews.Count < _w.Docs.Count || _w.Viewport.PixelWidth == 0)
                await Frame();
            await _w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            var cam = _w.Viewport.Camera;
            cam.LookAt(Eye, Target);
            cam.Fov = Camera.DefaultFov;
            _w.SetTool("path");
            _w.SetPathPlane(PathPlane.Wall);
            _w.SetPathUse(PathUse.Flight);
            await Frame();
            // A hand slows into the bends: the parameter runs u = s - 0.12 sin(2 pi s), samples every 1/60 s.
            var world = Enumerable.Range(0, Samples).Select(i =>
            {
                float s = i / (Samples - 1f);
                return At(s - 0.12f * MathF.Sin(MathF.Tau * s));
            }).ToList();
            float w = _w.Viewport.PixelWidth, h = _w.Viewport.PixelHeight;
            var px = world.Select(p => cam.Project(p, w, h) is { } q ? new Vector2(q.X, q.Y) : throw new InvalidOperationException("stroke off screen")).ToList();
            var done = new TaskCompletionSource<PathResult?>();
            _w.PathResultReady += r => done.TrySetResult(r);
            _w.PathDrawing.Draw(px, px.Select((_, i) => i / 60f).ToList());
            var result = await done.Task ?? throw new InvalidOperationException("no fit");
            var drawn = _w.PathDrawing.Points.ToList();
            Log($"viewport {w}x{h}, camera {Eye} -> {Target}, fov {cam.Fov}, a pixel at the emitter = {cam.MetresPerPixel(Vector3.Distance(Eye, Vector3.Zero), h):0.0000} m");
            Log($"stroke: {drawn.Count} points kept of {Samples} drawn (Bezier {string.Join(" ", Bezier)})");
            Log("readout: " + result.Readout);
            foreach (var key in new[] { "Angles", "ConeAngle", "Velocity", "GravityMultiply", "AirResistance", "LifeTime" })
                Log($"  {key} {Pfx.App.Inspector.Raw.Of(emitter.Block, key)}");
            Log("error per drawn point, m (to the fitted simulator path): " + string.Join(" ", result.Errors.Select(e => e.ToString("0.0000", CultureInfo.InvariantCulture))));
            File.WriteAllText(Path.Combine(_dir, "drawn.csv"), Csv(drawn));
            File.WriteAllText(Path.Combine(_dir, "fitted.csv"), Csv(result.Line));
            _w.Save(doc);
            Log("saved " + doc.Path);
            while (_w.Pipeline.AnyBusy)
                await Frame();
            _w.Pipeline.Playing = false;
            _w.Pipeline.Seek(5);
            while (_w.Pipeline.AnySeeking || _w.Pipeline.AnyBusy)
                await Frame();
            await Frame();
            if (_w.Viewport.CaptureFrame(true) is { } image)
                Pfx.Textures.ImageFiles.Save(Path.Combine(_dir, "view_5s.png"), MainWindow.Opaque(image));
            Log("RESULT: done");
        }
        catch (Exception ex)
        {
            Log("FAILED: " + ex);
        }
        _w.Close();
    }
}
