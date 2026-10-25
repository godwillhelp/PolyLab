using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32.SafeHandles;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Pfx.Render;

// A tool that takes the left mouse button in the viewport (gizmo handles, path drawing). Pixels are the child window's
// device pixels, the same as the render target's. Press returns false to let the press orbit the camera instead.
public interface IViewportTool
{
    bool Press(int x, int y, bool ctrl, bool shift);
    void Drag(int x, int y, bool ctrl, bool shift);
    void Release(int x, int y);
    void Hover(int x, int y);
    // The press ended without a release (capture lost, Escape).
    void Cancel();
}

// The D3D11 viewport as a WPF child window (WPF cannot draw over it: the renderer draws the overlay). Frames are paced
// by the swap chain: its frame-latency waitable object (maximum latency 1) is signalled when the display has taken the
// last frame, and that event posts the next frame to the UI thread. WPF's own CompositionTarget.Rendering is not used:
// on this PC it fires 31.8 times per second even for an empty WPF window on a 60 Hz display (measured, P4).
// A lost device (DXGI_ERROR_DEVICE_REMOVED / RESET from Present or a D3D call) is answered by disposing the device and
// everything made from it and building them again; ForceDeviceLoss runs the same path on demand.
public sealed class ViewportHost : HwndHost
{
    private const string ClassName = "PolyLab_PfxViewport";
    private const SwapChainFlags SwapFlags = SwapChainFlags.FrameLatencyWaitableObject;
    private static readonly WndProcDelegate DefProc = Native.DefWindowProc;
    private static bool _classRegistered;

    private IntPtr _hwnd;
    private Gpu? _gpu;
    private IDXGISwapChain1? _swap;
    private IntPtr _frameLatencyHandle;
    private NativeWaitHandle? _frameWait;
    private RegisteredWaitHandle? _wait;
    private Targets? _targets;
    private Renderer? _renderer;
    private int _width, _height;
    private bool _resize;
    private bool _running;
    private bool _inFrame;
    private int _generation;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastTick = -1;
    private Drag _drag;
    private int _lastX, _lastY;
    private readonly HashSet<int> _keys = new();
    private GlyphAtlas? _atlas;
    private OverlayBuilder? _overlay;

    private enum Drag { None, Orbit, Pan, Look, Tool }

    private sealed class NativeWaitHandle : WaitHandle
    {
        public NativeWaitHandle(IntPtr handle) => SafeWaitHandle = new SafeWaitHandle(handle, false);
    }

    public Camera Camera { get; } = new();
    public TextureCache Textures { get; } = new();
    // The effects in the scene (several .ptc parts of one composition, all emitters sorted together as in game).
    public List<EffectPreview> Previews { get; } = new();
    public EffectPreview? Preview => Previews.Count > 0 ? Previews[0] : null;
    public ViewOptions View { get; set; } = new();
    public FrameStats? Stats => _renderer?.Stats;
    public IReadOnlyList<EmitterDraw> Draws => _renderer?.Draws ?? Array.Empty<EmitterDraw>();
    public int DeviceResets { get; private set; }
    // Real time between the last two rendered frames, and the CPU time the last frame took (simulation, upload,
    // draw calls and Present).
    public double FrameIntervalMs { get; private set; }
    public double CpuFrameMs { get; private set; }
    public int PixelWidth => _width;
    public int PixelHeight => _height;

    // Called before each frame with an empty overlay to fill (the host composes the localised text).
    public Action<OverlayBuilder>? ComposeOverlay { get; set; }
    // Called before each frame with an empty list of world-space tool lines to fill.
    public Action<List<LineVertex>>? ComposeLines { get; set; }
    // Sprites drawn after the particles (the X-Ray side of the A/B view), filled each frame after the previews advanced.
    public Action<SpriteBatch, float>? ComposeSprites { get; set; }
    private readonly SpriteBatch _sprites = new();
    private readonly List<LineVertex> _toolLines = new();
    // Gets the left button first (Alt held keeps it for the camera).
    public IViewportTool? Tool { get; set; }
    // A key pressed while the viewport has the keyboard focus and no fly drag runs (the shell maps its shortcuts):
    // virtual-key code, Ctrl, Shift.
    public event Action<int, bool, bool>? KeyPressed;
    // Called after each presented frame.
    public event Action? Rendered;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        var instance = Native.GetModuleHandle(null);
        if (!_classRegistered)
        {
            var wc = new Native.WndClassEx
            {
                cbSize = (uint)Marshal.SizeOf<Native.WndClassEx>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(DefProc),
                hInstance = instance,
                hCursor = Native.LoadCursor(IntPtr.Zero, Native.IdcArrow),
                lpszClassName = ClassName,
            };
            if (Native.RegisterClassEx(ref wc) == 0)
                throw new InvalidOperationException($"RegisterClassEx failed: {Marshal.GetLastWin32Error()}");
            _classRegistered = true;
        }
        _hwnd = Native.CreateWindowEx(0, ClassName, "", Native.WsChild | Native.WsVisible | Native.WsClipChildren,
            0, 0, 1, 1, hwndParent.Handle, IntPtr.Zero, instance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
        _atlas = GlyphAtlas.Build();
        _overlay = new OverlayBuilder(_atlas);
        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        _generation++;
        ReleaseDevice();
        Native.DestroyWindow(hwnd.Handle);
        _hwnd = IntPtr.Zero;
    }

    private void CreateDevice()
    {
        _gpu = new Gpu();
        using (var factory = _gpu.Factory())
        {
            var desc = new SwapChainDescription1((uint)_width, (uint)_height, Format.B8G8R8A8_UNorm, false,
                Usage.RenderTargetOutput, 2, Scaling.Stretch, SwapEffect.FlipDiscard, AlphaMode.Ignore, SwapFlags);
            _swap = factory.CreateSwapChainForHwnd(_gpu.Device, _hwnd, desc);
            factory.MakeWindowAssociation(_hwnd, WindowAssociationFlags.IgnoreAltEnter);
        }
        using (var swap2 = _swap.QueryInterface<IDXGISwapChain2>())
        {
            swap2.MaximumFrameLatency = 1;
            _frameLatencyHandle = swap2.FrameLatencyWaitableObject;
        }
        _frameWait = new NativeWaitHandle(_frameLatencyHandle);
        _targets = new Targets(_gpu, _swap.GetBuffer<ID3D11Texture2D>(0));
        _renderer = new Renderer(_gpu, Textures, _atlas);
        _resize = false;
    }

    private void ReleaseDevice()
    {
        _wait?.Unregister(null);
        _wait = null;
        _frameWait?.Dispose();
        _frameWait = null;
        if (_frameLatencyHandle != IntPtr.Zero)
            Native.CloseHandle(_frameLatencyHandle);
        _frameLatencyHandle = IntPtr.Zero;
        _renderer?.Dispose();
        _targets?.Dispose();
        _swap?.Dispose();
        _gpu?.Dispose();
        _renderer = null;
        _targets = null;
        _swap = null;
        _gpu = null;
    }

    // Same path as a real device loss: everything device-bound is disposed and made again from CPU state.
    public void ForceDeviceLoss()
    {
        ReleaseDevice();
        DeviceResets++;
        if (_running)
            Schedule(false);
    }

    // Queues the next frame: after the swap chain signals (normal pacing) or at once (no swap chain yet). A newer
    // schedule makes older pending ones void (generation), so there is only ever one frame chain.
    private void Schedule(bool waitForSwapChain)
    {
        int generation = ++_generation;
        _wait?.Unregister(null);
        _wait = null;
        if (waitForSwapChain && _frameWait != null)
        {
            _wait = ThreadPool.RegisterWaitForSingleObject(_frameWait,
                (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Render, () => Tick(generation)), null, Timeout.Infinite, true);
        }
        else
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Render, () => Tick(generation));
        }
    }

    private void Tick(int generation)
    {
        if (generation != _generation || _hwnd == IntPtr.Zero)
            return;
        if (_width <= 0 || _height <= 0)
        {
            // Nothing to draw into; WM_SIZE starts the chain again.
            _running = false;
            return;
        }
        double now = _clock.Elapsed.TotalSeconds;
        float dt = _lastTick < 0 ? 0 : (float)(now - _lastTick);
        _lastTick = now;
        RenderFrame(dt);
        // A device loss or a frame scheduled inside this one (Rendered handlers) has already queued the next frame.
        if (generation == _generation)
            Schedule(_swap != null);
    }

    public void RenderFrame(float dt)
    {
        // A Rendered handler that resizes the window pumps messages; a frame never starts inside another.
        if (_hwnd == IntPtr.Zero || _width <= 0 || _height <= 0 || _inFrame)
            return;
        _inFrame = true;
        try
        {
            RenderFrameCore(dt);
        }
        finally
        {
            _inFrame = false;
        }
    }

    private void RenderFrameCore(float dt)
    {
        var clock = Stopwatch.StartNew();
        FrameIntervalMs = dt * 1000;
        ApplyFlyKeys(dt);
        try
        {
            if (_gpu == null)
                CreateDevice();
            if (_resize)
            {
                _targets!.Dispose();
                _swap!.ResizeBuffers(0, (uint)_width, (uint)_height, Format.Unknown, SwapFlags).CheckError();
                _targets = new Targets(_gpu!, _swap.GetBuffer<ID3D11Texture2D>(0));
                _resize = false;
            }
            foreach (var preview in Previews)
                preview.Advance(dt);
            _overlay!.Clear();
            ComposeOverlay?.Invoke(_overlay);
            _toolLines.Clear();
            ComposeLines?.Invoke(_toolLines);
            _sprites.Clear();
            ComposeSprites?.Invoke(_sprites, dt);
            _renderer!.Render(_targets!, Camera, Previews, View, _overlay, _toolLines, _sprites);
            _renderer.Stats.SimMs = Previews.Sum(p => p.LastSimMs);
            var hr = _swap!.Present(1);
            if (hr == Vortice.DXGI.ResultCode.DeviceRemoved || hr == Vortice.DXGI.ResultCode.DeviceReset)
            {
                ReleaseDevice();
                DeviceResets++;
                return;
            }
            hr.CheckError();
        }
        catch (SharpGenException ex) when (ex.ResultCode == Vortice.DXGI.ResultCode.DeviceRemoved
                                           || ex.ResultCode == Vortice.DXGI.ResultCode.DeviceReset)
        {
            ReleaseDevice();
            DeviceResets++;
            return;
        }
        CpuFrameMs = clock.Elapsed.TotalMilliseconds;
        Rendered?.Invoke();
    }

    // Renderer.BackToBackMs on an offscreen target of the viewport's size (benchmarks).
    public double? BackToBackMs(int frames)
    {
        if (_gpu == null || _renderer == null || _targets == null)
            return null;
        using var offscreen = Targets.Offscreen(_gpu, _targets.Width, _targets.Height);
        return _renderer.BackToBackMs(offscreen, Camera, Previews, View, frames);
    }

    // The current frame rendered again into an offscreen target of the viewport's size (the simulation does not move).
    public Pfx.Textures.RgbaImage? CaptureFrame(bool withOverlay)
    {
        if (_gpu == null || _renderer == null || _targets == null)
            return null;
        using var offscreen = Targets.Offscreen(_gpu, _targets.Width, _targets.Height);
        _renderer.Render(offscreen, Camera, Previews, View, withOverlay ? _overlay : null, _toolLines, _sprites);
        return _renderer.Capture(offscreen);
    }

    // One effect drawn alone into an offscreen target of the given size with its own camera (library thumbnails): the
    // same renderer and textures as the viewport, no overlay, tool lines or markers. Null while there is no device.
    public Pfx.Textures.RgbaImage? RenderAlone(EffectPreview preview, Camera camera, ViewOptions view, int width, int height)
    {
        if (_gpu == null || _renderer == null)
            return null;
        using var offscreen = Targets.Offscreen(_gpu, width, height);
        _renderer.Render(offscreen, camera, new[] { preview }, view, null, new List<LineVertex>(), new SpriteBatch());
        return _renderer.Capture(offscreen);
    }

    // Fly mode: W/S forward and back, A/D sideways, Q/E down and up while the right button is held.
    private void ApplyFlyKeys(float dt)
    {
        if (_drag != Drag.Look || _keys.Count == 0)
            return;
        float speed = 3 * (_keys.Contains(Native.VkShift) ? 4 : 1) * dt;
        var move = new Vector3(
            (_keys.Contains('D') ? 1 : 0) - (_keys.Contains('A') ? 1 : 0),
            (_keys.Contains('E') ? 1 : 0) - (_keys.Contains('Q') ? 1 : 0),
            (_keys.Contains('W') ? 1 : 0) - (_keys.Contains('S') ? 1 : 0));
        Camera.Fly(move * speed);
    }

    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        int x = (short)(lParam.ToInt64() & 0xFFFF), y = (short)(lParam.ToInt64() >> 16 & 0xFFFF);
        switch (msg)
        {
            case Native.WmSize:
                _width = (int)(lParam.ToInt64() & 0xFFFF);
                _height = (int)(lParam.ToInt64() >> 16 & 0xFFFF);
                _resize = _gpu != null;
                if (!_running && _width > 0 && _height > 0)
                {
                    _running = true;
                    Schedule(false);
                }
                break;
            case Native.WmLButtonDown:
                bool shiftDown = (wParam.ToInt64() & Native.MkShift) != 0;
                if (Native.GetKeyState(Native.VkMenu) >= 0 && Tool != null && Tool.Press(x, y, (wParam.ToInt64() & Native.MkControl) != 0, shiftDown))
                    StartDrag(Drag.Tool, x, y);
                else
                    StartDrag(shiftDown ? Drag.Pan : Drag.Orbit, x, y);
                handled = true;
                break;
            case Native.WmMButtonDown:
                StartDrag(Drag.Pan, x, y);
                handled = true;
                break;
            case Native.WmRButtonDown:
                StartDrag(Drag.Look, x, y);
                handled = true;
                break;
            case Native.WmLButtonUp:
            case Native.WmMButtonUp:
            case Native.WmRButtonUp:
                var ended = _drag;
                _drag = Drag.None;
                Native.ReleaseCapture();
                if (ended == Drag.Tool)
                    Tool?.Release(x, y);
                handled = true;
                break;
            case Native.WmCaptureChanged:
                if (_drag == Drag.Tool)
                {
                    _drag = Drag.None;
                    Tool?.Cancel();
                }
                break;
            case Native.WmMouseMove:
                if (_drag == Drag.Tool)
                    Tool?.Drag(x, y, (wParam.ToInt64() & Native.MkControl) != 0, (wParam.ToInt64() & Native.MkShift) != 0);
                else if (_drag == Drag.None)
                    Tool?.Hover(x, y);
                else
                    MoveDrag(x, y);
                handled = true;
                break;
            case Native.WmMouseWheel:
                int delta = (short)(wParam.ToInt64() >> 16 & 0xFFFF);
                // One wheel notch (120) = 10 % closer or further.
                Camera.Zoom(MathF.Pow(0.9f, delta / 120f));
                handled = true;
                break;
            case Native.WmKeyDown:
                int vk = (int)wParam.ToInt64();
                _keys.Add(vk);
                if (_drag == Drag.Tool && vk == Native.VkEscape)
                {
                    _drag = Drag.None;
                    Native.ReleaseCapture();
                    Tool?.Cancel();
                }
                else if (_drag != Drag.Look)
                {
                    KeyPressed?.Invoke(vk, Native.GetKeyState(Native.VkControl) < 0, Native.GetKeyState(Native.VkShift) < 0);
                }
                handled = true;
                break;
            case Native.WmKeyUp:
                _keys.Remove((int)wParam.ToInt64());
                handled = true;
                break;
            case Native.WmKillFocus:
                _keys.Clear();
                break;
        }
        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    private void StartDrag(Drag drag, int x, int y)
    {
        _drag = drag;
        _lastX = x;
        _lastY = y;
        Native.SetFocus(_hwnd);
        Native.SetCapture(_hwnd);
    }

    private void MoveDrag(int x, int y)
    {
        int dx = x - _lastX, dy = y - _lastY;
        _lastX = x;
        _lastY = y;
        // Rotation 0.3 degrees per pixel; panning keeps the point under the cursor at the target distance.
        switch (_drag)
        {
            case Drag.Orbit:
                Camera.Orbit(dx * 0.3f, -dy * 0.3f);
                break;
            case Drag.Look:
                Camera.Look(dx * 0.3f, -dy * 0.3f);
                break;
            case Drag.Pan:
                float metresPerPixel = 2 * Camera.Distance * MathF.Tan(Camera.Fov * MathF.PI / 360f) / Math.Max(1, _height);
                Camera.Pan(-dx * metresPerPixel, dy * metresPerPixel);
                break;
        }
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static class Native
    {
        public const int WsChild = 0x40000000, WsVisible = 0x10000000, WsClipChildren = 0x02000000;
        public const int WmSize = 0x0005, WmKillFocus = 0x0008, WmKeyDown = 0x0100, WmKeyUp = 0x0101, WmMouseMove = 0x0200;
        public const int WmLButtonDown = 0x0201, WmLButtonUp = 0x0202, WmRButtonDown = 0x0204, WmRButtonUp = 0x0205;
        public const int WmMButtonDown = 0x0207, WmMButtonUp = 0x0208, WmMouseWheel = 0x020A;
        public const int WmCaptureChanged = 0x0215;
        public const long MkShift = 0x0004, MkControl = 0x0008;
        public const int VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12, VkEscape = 0x1B;
        public static readonly IntPtr IdcArrow = 32512;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WndClassEx
        {
            public uint cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string? lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern ushort RegisterClassEx(ref WndClassEx wc);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style, int x, int y,
            int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyWindow(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string? name);

        [DllImport("user32.dll")]
        public static extern IntPtr SetFocus(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern IntPtr SetCapture(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("user32.dll")]
        public static extern short GetKeyState(int key);
    }
}
