using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Pfx.Render;
using Pfx.Textures;

namespace PfxView;

// --bench: frame costs over a fixed number of presented frames once the effect has reached its steady state.
// Measuring starts at the first frame whose GPU timestamp result has arrived (device made, textures uploaded).
public sealed class BenchRun
{
    private readonly Window _window;
    private readonly ViewportHost _viewport;
    private readonly Launch _launch;
    private readonly List<double[]> _rows = new();

    public BenchRun(Window window, ViewportHost viewport, Launch launch)
    {
        _window = window;
        _viewport = viewport;
        _launch = launch;
    }

    public void Start()
    {
        foreach (var p in _viewport.Previews)
            p.Seek(_launch.Warmup);
        _viewport.Rendered += OnRendered;
    }

    private void OnRendered()
    {
        var s = _viewport.Stats;
        if (s == null || double.IsNaN(s.GpuMs) || _viewport.Preview == null)
            return;
        _rows.Add(new[] { _viewport.FrameIntervalMs, _viewport.CpuFrameMs, s.SimMs, s.UploadMs, s.GpuMs, s.Instances });
        if (_rows.Count < _launch.Frames)
            return;
        _viewport.Rendered -= OnRendered;
        var sb = new StringBuilder();
        foreach (var e in _viewport.Previews)
            sb.AppendLine(CultureInfo.InvariantCulture, $"effect {e.FilePath} copies {e.Settings.Copies} motion {e.Settings.Motion}");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"viewport {_viewport.PixelWidth}x{_viewport.PixelHeight} frames {_rows.Count} warmup {_launch.Warmup} s");
        string[] names = { "frame interval ms", "cpu frame ms", "sim ms", "upload ms", "gpu ms", "instances" };
        for (int c = 0; c < names.Length; c++)
        {
            var v = _rows.Select(r => r[c]).OrderBy(x => x).ToArray();
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"{names[c],-18} mean {v.Average(),9:0.000}  median {v[v.Length / 2],9:0.000}  p95 {v[(int)(v.Length * 0.95)],9:0.000}  max {v[^1],9:0.000}");
        }
        // 60 back-to-back frames: one second of frames at 60 Hz.
        sb.AppendLine(CultureInfo.InvariantCulture, $"back-to-back frame ms (upper bound of GPU time) {_viewport.BackToBackMs(60):0.000}");
        File.WriteAllText(_launch.Bench!, sb.ToString());
        _window.Close();
    }
}

// --capture: the viewport frame with its overlay after --after seconds of playback (summed frame intervals), saved
// through the renderer (only the viewport's own pixels), then the window closes.
public sealed class CaptureRun
{
    private readonly Window _window;
    private readonly ViewportHost _viewport;
    private readonly Launch _launch;
    private double _played;

    public CaptureRun(Window window, ViewportHost viewport, Launch launch)
    {
        _window = window;
        _viewport = viewport;
        _launch = launch;
    }

    public void Start() => _viewport.Rendered += OnRendered;

    private void OnRendered()
    {
        _played += _viewport.FrameIntervalMs / 1000;
        if (_played < _launch.After || _viewport.Stats is not { } s || double.IsNaN(s.GpuMs))
            return;
        _viewport.Rendered -= OnRendered;
        if (_viewport.CaptureFrame(true) is { } image)
            ImageFiles.Save(_launch.Capture!, image);
        _window.Close();
    }
}
