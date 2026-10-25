using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PolyLab.Launcher.ViewModels;
using PolyLab.Ui.Core;

namespace PolyLab.Launcher;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is MainViewModel o) o.NewsShown -= OnNewsShown;
            if (e.NewValue is not MainViewModel n) return;
            n.NewsShown += OnNewsShown;
            if (!App.Shot) RestorePlacement(n);
        };
        Loaded += (_, _) => SyncMaxButton();
        StateChanged += (_, _) => SyncMaxButton();
        Loc.Changed += SyncMaxButton;
    }

    // Maximized on the first start; afterwards the last placement, if it is still on a screen.
    private void RestorePlacement(MainViewModel vm)
    {
        var s = vm.Settings;
        if (s.Bounds is [var l, var t, var w, var h] && w >= MinWidth && h >= MinHeight
            && l + w > SystemParameters.VirtualScreenLeft && t + h > SystemParameters.VirtualScreenTop
            && l < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth
            && t < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            (Left, Top, Width, Height) = (l, t, w, h);
        }
        if (s.Maximized) WindowState = WindowState.Maximized;
        Closed += (_, _) =>
        {
            s.Maximized = WindowState == WindowState.Maximized;
            var b = RestoreBounds;
            if (!b.IsEmpty) s.Bounds = [b.Left, b.Top, b.Width, b.Height];
            s.Save();
        };
    }

    private void OnNewsShown() => Dispatcher.BeginInvoke(() => NewsScroll.ScrollToEnd(), DispatcherPriority.Loaded);

    private void OnNewsBackdrop(object sender, MouseButtonEventArgs e) => ((MainViewModel)DataContext).NewsOpen = false;

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is MainViewModel { NewsOpen: true } vm)
        {
            vm.NewsOpen = false;
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    private void SyncMaxButton()
    {
        var max = WindowState == WindowState.Maximized;
        WMaxIco.Kind = max ? "restore" : "square";
        WMax.ToolTip = Loc.T(max ? "Win.Restore" : "Win.Max");
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            OnMax(sender, e);
            return;
        }
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnMin(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMax(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
    }

    // WindowStyle=None maximizes over the taskbar; clamp the maximized size to the monitor's work area.
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmGetMinMaxInfo) return IntPtr.Zero;
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return IntPtr.Zero;
        var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;
        var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        var work = info.rcWork;
        var mon = info.rcMonitor;
        mmi.ptMaxPosition.X = work.Left - mon.Left;
        mmi.ptMaxPosition.Y = work.Top - mon.Top;
        mmi.ptMaxSize.X = work.Right - work.Left;
        mmi.ptMaxSize.Y = work.Bottom - work.Top;
        var dpi = VisualTreeHelper.GetDpi(this);
        mmi.ptMinTrackSize.X = (int)Math.Ceiling(MinWidth * dpi.DpiScaleX);
        mmi.ptMinTrackSize.Y = (int)Math.Ceiling(MinHeight * dpi.DpiScaleY);
        Marshal.StructureToPtr(mmi, lParam, true);
        handled = true;
        return IntPtr.Zero;
    }

    private const int WmGetMinMaxInfo = 0x0024;
    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public NativeRect rcMonitor, rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
