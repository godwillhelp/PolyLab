using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PolyLab.Launcher.Core;
using PolyLab.Launcher.ViewModels;
using PolyLab.Ui.Core;

namespace PolyLab.Launcher;

public partial class App : Application
{
    // Render run (--shot): no single instance, no dialogs, window placement not restored or saved.
    public static bool Shot { get; private set; }

    private SingleInstance? _single;

    // --root <dir>     install root (tools\, graft\) instead of the exe folder
    // --data <dir>     launcher data instead of %LOCALAPPDATA%\PolyLab\Launcher
    // --shot <png>     render the window and exit; --size WxH, --lang en|ru|uk, --delay ms
    //   --state empty|installed|update|beta|offline   demo data; --news open|badge
    //   --live         real data instead of a demo state; then --install id,id and --launch id [-- tool args]
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var sep = Array.IndexOf(e.Args, "--");
        var args = sep >= 0 ? e.Args[..sep] : e.Args;
        var toolArgs = sep >= 0 ? e.Args[(sep + 1)..] : [];
        string? Arg(string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        if (Arg("--root") is { } root) Paths.Root = Path.GetFullPath(root);
        if (Arg("--data") is { } data) Paths.Data = Path.GetFullPath(data);
        var shot = Arg("--shot");
        Shot = shot != null;

        if (!Shot)
        {
            _single = new SingleInstance("PolyLab_Launcher", () => Dispatcher.BeginInvoke(WakeWindow));
            if (!_single.IsPrimary)
            {
                Shutdown();
                return;
            }
        }

        var vm = new MainViewModel();
        var win = new MainWindow { DataContext = vm };
        RelayCommand.Failed += vm.ShowError;
        DispatcherUnhandledException += (_, ex) =>
        {
            vm.ShowError(ex.Exception);
            ex.Handled = true;
        };
        if (Arg("--size") is { } size)
        {
            var wh = size.Split('x');
            win.Width = double.Parse(wh[0]);
            win.Height = double.Parse(wh[1]);
        }
        MainWindow = win;
        win.Show();

        if (!Shot)
        {
            await vm.InitAsync();
            return;
        }

        if (args.Contains("--live"))
        {
            await vm.InitAsync();
            foreach (var id in (Arg("--install") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                await vm.InstallAsync(id);
            if (Arg("--launch") is { } tool) vm.Launch(tool, toolArgs);
        }
        else vm.Demo(Arg("--state") ?? "empty", Arg("--news"));
        if (Arg("--lang") is { } lang) Loc.Set(lang);
        await Task.Delay(int.TryParse(Arg("--delay"), out var delay) ? delay : 1500);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

        var content = (FrameworkElement)win.Content;
        var dpi = VisualTreeHelper.GetDpi(content);
        var rtb = new RenderTargetBitmap((int)(content.ActualWidth * dpi.DpiScaleX), (int)(content.ActualHeight * dpi.DpiScaleY),
            96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        rtb.Render(content);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        await using (var fs = File.Create(shot!)) enc.Save(fs);
        Shutdown();
    }

    private void WakeWindow()
    {
        if (MainWindow is not { } w) return;
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Show();
        w.Activate();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _single?.Dispose();
        base.OnExit(e);
    }
}
