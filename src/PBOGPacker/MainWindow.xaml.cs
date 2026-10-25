using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using PolyLab.PBOGPacker.ViewModels;

namespace PolyLab.PBOGPacker;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is INotifyPropertyChanged o) o.PropertyChanged -= OnVm;
            if (e.NewValue is INotifyPropertyChanged n) n.PropertyChanged += OnVm;
        };
        DragEnter += OnDragEnter;
        DragOver += OnDragEnter;
        DragLeave += (_, _) => Vm.DropActive = false;
        Drop += OnDrop;
        PreviewKeyDown += OnKey;
        Loaded += (_, _) => { _lastBg = BgFront.Source; PlaceFx(); SyncMaxButton(); };
        StateChanged += (_, _) => SyncMaxButton();
        Core.Loc.Changed += SyncMaxButton;
        SizeChanged += (_, _) => PlaceFx();
        NavBar.SizeChanged += (_, _) => UpdateOrbit();
        StateChanged += (_, _) => Dispatcher.BeginInvoke(PlaceFx, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    private void SyncMaxButton()
    {
        var max = WindowState == WindowState.Maximized;
        WMaxIco.Kind = max ? "restore" : "square";
        WMax.ToolTip = Core.Loc.T(max ? "Win.Restore" : "Win.Max");
    }

    private void PlaceFx()
    {
        if (!TcBtn.IsLoaded) return;
        Fx.PulseOrigin = TcBtn.TranslatePoint(new Point(24, 24), Fx);
        UpdateOrbit();
    }

    private void UpdateOrbit()
    {
        var active = NavBar.Children.OfType<System.Windows.Controls.RadioButton>().FirstOrDefault(r => r.IsChecked == true);
        if (active == null || !active.IsLoaded) { Fx.OrbitTarget = null; return; }
        var tl = active.TranslatePoint(new Point(12, active.ActualHeight / 2 - 13), Fx);
        Fx.OrbitTarget = new Rect(tl, new Size(Math.Max(10, active.ActualWidth - 24), 26));
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
    }

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
    private ImageSource? _lastBg;

    private void OnVm(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.PageBackground))
        {
            BgBack.Source = _lastBg;
            _lastBg = BgFront.Source;
            BgFront.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(320))));
            return;
        }
        if (e.PropertyName != nameof(MainViewModel.Page)) return;
        Dispatcher.BeginInvoke(UpdateOrbit, System.Windows.Threading.DispatcherPriority.Loaded);
        FrameworkElement? page = Vm.Page switch
        {
            "build" => PgBuild,
            "servers" => PgServers,
            "keys" => PgKeys,
            "env" => PgEnv,
            "settings" => PgSettings,
            "about" => PgAbout,
            _ => null,
        };
        if (page == null) return;
        var tr = new TranslateTransform(0, 10);
        page.RenderTransform = tr;
        var d = new Duration(TimeSpan.FromMilliseconds(190));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, d) { EasingFunction = ease });
        tr.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, d) { EasingFunction = ease });
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { OnMax(sender, e); return; }
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnMin(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMax(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private static List<string> DroppedFolders(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return new();
        var files = e.Data.GetData(DataFormats.FileDrop) as string[];
        return (files ?? Array.Empty<string>()).Where(Directory.Exists).ToList();
    }

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        var ok = DroppedFolders(e).Count > 0 && !Vm.WorkshopOpen && !Vm.ResultOpen;
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        Vm.DropActive = ok;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        Vm.DropActive = false;
        if (Vm.WorkshopOpen || Vm.ResultOpen) return;
        var paths = DroppedFolders(e);
        if (paths.Count > 0) await Vm.AddModsAsync(paths);
    }

    // Ticking mods for a batch: in the tick mode a click toggles the mod instead of opening it (the logo
    // button under it included); Ctrl+click and Shift+click tick outside it too. The context menu stays.
    private void OnModClick(object sender, MouseButtonEventArgs e)
    {
        var multi = Vm.MultiSelect;
        var keys = Keyboard.Modifiers;
        if (!multi && (keys & (ModifierKeys.Control | ModifierKeys.Shift)) == 0) return;
        if (e.OriginalSource is not Visual v) return;
        if (System.Windows.Controls.ItemsControl.ContainerFromElement(ModList, v) is not System.Windows.Controls.ListBoxItem { DataContext: ModVM m }) return;
        Vm.CheckClick(m, keys.HasFlag(ModifierKeys.Shift));
        e.Handled = true;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        // The mods dialog owns the keyboard: Esc closes it, window shortcuts (Ctrl+B) must not build behind it.
        if (Vm.WorkshopOpen)
        {
            if (e.Key == Key.Escape && Vm.Workshop?.CloseCmd.CanExecute(null) == true) Vm.Workshop.CloseCmd.Execute(null);
            if (e.Key == Key.Escape || Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) e.Handled = true;
            return;
        }
        if (e.Key != Key.Escape) return;
        if (Vm.ResultOpen) Vm.ResultOpen = false;
        else if (Vm.MenuOpen || Vm.OptionsOpen || Vm.ServerMenuOpen) Vm.MenuOpen = Vm.OptionsOpen = Vm.ServerMenuOpen = false;
        else if (Vm.IsRunning) Vm.CancelCmd.Execute(null);
        else if (Vm.MultiSelect) Vm.MultiSelect = false;
        else return;
        e.Handled = true;
    }
}
