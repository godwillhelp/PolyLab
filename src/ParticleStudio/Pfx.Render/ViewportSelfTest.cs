using System.Text;
using System.Windows;
using System.Windows.Threading;
using Pfx.Textures;

namespace Pfx.Render;

// --selftest of tools/PfxView and of the shell: forced device loss (same frame before and after), resize and minimise,
// each step waiting for the viewport's next presented frame.
public sealed class ViewportSelfTest
{
    private readonly Window _window;
    private readonly ViewportHost _viewport;
    private readonly string _out;
    private readonly StringBuilder _log = new();
    private int _step;
    private int _frames;
    private RgbaImage? _before;
    private (int W, int H) _size;

    public ViewportSelfTest(Window window, ViewportHost viewport, string output)
    {
        _window = window;
        _viewport = viewport;
        _out = output;
    }

    public void Start() => _viewport.Rendered += OnRendered;

    // Written as it goes, so a run that stops shows the last step reached.
    private void Log(string line)
    {
        _log.AppendLine(line);
        File.WriteAllText(_out, _log.ToString());
    }

    private void OnRendered()
    {
        _frames++;
        switch (_step)
        {
            case 0:
                // Wait until the first GPU result is in: the device, swap chain and textures exist.
                if (_viewport.Stats is not { } s || double.IsNaN(s.GpuMs))
                    return;
                foreach (var p in _viewport.Previews)
                    p.Playing = false;
                _before = _viewport.CaptureFrame(false);
                Log($"frame {_frames}: captured {_before?.Width}x{_before?.Height} before the device loss, resets {_viewport.DeviceResets}");
                _viewport.ForceDeviceLoss();
                _step = 1;
                break;
            case 1:
                var after = _viewport.CaptureFrame(false);
                Log($"frame {_frames}: device re-created (resets {_viewport.DeviceResets}), {Compare(_before, after)}");
                // Step and size first: a window resize sends WM_SIZE at once. A maximised window (the shell opens so)
                // only keeps Width and Height as its restore size, so it is restored first.
                _step = 2;
                _size = (_viewport.PixelWidth, _viewport.PixelHeight);
                _window.WindowState = WindowState.Normal;
                _window.Width = 1000;
                _window.Height = 600;
                break;
            case 2:
                if ((_viewport.PixelWidth, _viewport.PixelHeight) == _size)
                    return;
                Log($"frame {_frames}: resized {_size.W}x{_size.H} -> {_viewport.PixelWidth}x{_viewport.PixelHeight}");
                _step = 3;
                _size = (_viewport.PixelWidth, _viewport.PixelHeight);
                _window.Width = 1600;
                _window.Height = 900;
                break;
            case 3:
                if ((_viewport.PixelWidth, _viewport.PixelHeight) == _size)
                    return;
                Log($"frame {_frames}: resized {_size.W}x{_size.H} -> {_viewport.PixelWidth}x{_viewport.PixelHeight}");
                _step = 4;
                _window.StateChanged += OnState;
                _window.WindowState = WindowState.Minimized;
                break;
            case 5:
                Log($"frame {_frames}: presented after restore at {_viewport.PixelWidth}x{_viewport.PixelHeight}, resets {_viewport.DeviceResets}");
                var restored = _viewport.CaptureFrame(false);
                Log($"capture after restore: {restored?.Width}x{restored?.Height}");
                _viewport.Rendered -= OnRendered;
                File.WriteAllText(_out, _log.ToString());
                _window.Close();
                break;
        }
    }

    private int _minimisedAt;

    private void OnState(object? sender, EventArgs e)
    {
        if (_window.WindowState == WindowState.Minimized)
        {
            _minimisedAt = _frames;
            Log($"frame {_frames}: minimised, viewport {_viewport.PixelWidth}x{_viewport.PixelHeight}");
            // Restore once WPF has finished processing the minimise (the dispatcher queue below input priority).
            _window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => _window.WindowState = WindowState.Normal);
        }
        else if (_window.WindowState == WindowState.Normal && _step == 4)
        {
            _window.StateChanged -= OnState;
            Log($"frame {_frames}: restored ({_frames - _minimisedAt} frames presented while minimised)");
            _step = 5;
        }
    }

    private static string Compare(RgbaImage? a, RgbaImage? b)
    {
        if (a == null || b == null)
            return "capture missing";
        if (a.Width != b.Width || a.Height != b.Height)
            return $"size differs {a.Width}x{a.Height} vs {b.Width}x{b.Height}";
        int differing = 0, max = 0;
        for (int i = 0; i < a.Pixels.Length; i += 4)
        {
            int d = 0;
            for (int c = 0; c < 4; c++)
                d = Math.Max(d, Math.Abs(a.Pixels[i + c] - b.Pixels[i + c]));
            if (d > 0)
                differing++;
            max = Math.Max(max, d);
        }
        return $"{differing} of {a.Width * a.Height} pixels differ, largest channel difference {max}";
    }
}
