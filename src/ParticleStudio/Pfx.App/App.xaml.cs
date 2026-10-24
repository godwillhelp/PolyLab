using System.Windows;
using System.Windows.Threading;
using Pfx.App.Core;
using Pfx.App.Views;

namespace Pfx.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Launch launch;
        try
        {
            launch = Launch.Parse(e.Args);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            File.WriteAllText("NDZ_ParticleStudio.error.txt", ex.Message);
            Shutdown(2);
            return;
        }
        if (launch.SettingsDir != null)
        {
            AppSettings.Folder = Path.GetFullPath(launch.SettingsDir);
            AppSettings.CacheFolder = Path.Combine(AppSettings.Folder, "cache");
        }
        var settings = AppSettings.Load();
        Loc.Set(launch.Lang ?? settings.Lang);
        if (launch.Lint != null)
        {
            var roots = launch.Roots?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? settings.Roots.ToArray();
            Shutdown(Automation.LintRun.Run(launch, roots));
            return;
        }
        if (launch.Automated)
            DispatcherUnhandledException += (_, ex) => OnAutomationCrash(launch, ex);
        var window = new MainWindow(launch, settings);
        MainWindow = window;
        window.Show();
    }

    // An automated run must end with a file that says why it stopped, never with a hanging window.
    private void OnAutomationCrash(Launch launch, DispatcherUnhandledExceptionEventArgs e)
    {
        string? dir = launch.Checks ?? launch.XrayRun ?? launch.Flipbook ?? launch.PathRun ?? launch.ReadLook;
        string target = dir != null ? Path.Combine(dir, "crash.txt") : (launch.SelfTest ?? launch.EditBench!) + ".crash.txt";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
        File.WriteAllText(target, e.Exception.ToString());
        e.Handled = true;
        Shutdown(3);
    }
}
