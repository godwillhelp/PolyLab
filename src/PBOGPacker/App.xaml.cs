using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PolyLab.PBOGPacker.Core;
using PolyLab.PBOGPacker.ViewModels;

namespace PolyLab.PBOGPacker;

public partial class App : Application
{
    public static void Fail(Exception e)
    {
        var diag = Environment.GetEnvironmentVariable("POLYLAB_DIAG");
        if (diag != null)
            try { System.IO.File.AppendAllText(diag, $"{e.GetType().Name}: {e.Message}\n{e}\n\n"); } catch { }
        var vm = (Current?.MainWindow?.DataContext) as MainViewModel;
        vm?.Toast("err", e.GetType().Name, e.Message);
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, ex) => { Fail(ex.Exception); ex.Handled = true; };
        // The command-line modes below never load the settings; the window replaces this with the saved language.
        Loc.Set(PolyLab.Ui.Core.UiLanguage.Default);

        var args = e.Args;
        string? Arg(string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        if (Arg("--doctor") is { } dout)
        {
            var items = await ToolDetect.DetectAsync(new ToolPaths());
            File.WriteAllLines(dout, items.Select(d =>
                $"{d.Id,-8} {d.State,-4} {(string.IsNullOrEmpty(d.Version) ? "-" : d.Version),-14} {d.Path}"));
            Shutdown();
            return;
        }

        if (Arg("--pipeline") is { } plog && Arg("--project") is { } proj)
        {
            var b = new PackBackend();
            var lines = new System.Collections.Generic.List<string>();
            b.Started += s => lines.Add("START " + string.Join(",", s.Steps));
            b.StepChanged += s => lines.Add($"STEP {s.Step,-9} {s.Status} {s.Ms}ms");
            b.Log += l => lines.Add($"    [{l.Step}/{l.Level}] {l.Line}");
            var tcs = new TaskCompletionSource<bool>();
            b.Done += d =>
            {
                lines.Add($"DONE {d.ModLine} pbo={d.PboBytes}b dir={d.ModDir} srv={d.ServerDir} {d.Ms}ms");
                foreach (var dup in d.Duplicates ?? Array.Empty<string>()) lines.Add("DUPLICATE " + dup);
                tcs.TrySetResult(true);
            };
            b.Failed += f => { lines.Add($"FAILED [{f.Step}] {f.Message} @ {f.Where} hint={f.Hint}"); foreach (var t in f.LogTail) lines.Add("    tail: " + t.Line); tcs.TrySetResult(false); };
            // Exit code for scripts: 0 done, 1 a step failed, 2 no result in time, 3 exception.
            var exitCode = 2;
            try
            {
                var mod = await b.AddModAsync(proj);
                if (Arg("--kind") != null || Arg("--out") != null || Arg("--key") != null)
                    mod = await b.SetModPackAsync(mod.Id, Arg("--kind") ?? mod.Kind, Arg("--key") ?? mod.Key, Arg("--out") ?? mod.Out);
                if (args.Contains("--cpp")) mod = await b.CreateCppProjectAsync(mod.Id);
                lines.Add($"MOD {mod.Name} kind={mod.Kind} key={mod.Key} path={mod.Path} cpp={mod.Cpp}");
                var sign = !args.Contains("--nosign");
                if (sign && !(await b.ListKeysAsync()).Any(k => k.Tag == mod.Key))
                    try { await b.CreateKeyAsync(mod.Key); } catch (Exception ex) { lines.Add("key: " + ex.Message); }
                var srv = Arg("--server") is { } sdir ? await b.AddServerAsync(sdir) : null;
                var opts = new PipelineOptions(Arg("--preset") ?? "release", Sign: sign, Deploy: srv != null, Host: false, Server: srv?.Id ?? "",
                    FromStep: Arg("--from"));
                await b.RunPipelineAsync(mod.Id, opts);
                await Task.WhenAny(tcs.Task, Task.Delay(240000));
                await Task.WhenAny(b.DrainAsync(), Task.Delay(15000));
                if (tcs.Task.IsCompleted) exitCode = tcs.Task.Result ? 0 : 1;
            }
            catch (Exception ex) { lines.Add("ERROR " + ex.Message); exitCode = 3; }
            File.WriteAllLines(plog, lines);
            Shutdown(exitCode);
            return;
        }

        if (Arg("--launch") is { } lout && Arg("--server") is { } ldir)
        {
            var b = new PackBackend();
            try
            {
                var s = await b.AddServerAsync(ldir);
                var r = await b.LaunchServerAsync(s!.Id, Arg("--bat"));
                await Task.Delay(3000);
                File.WriteAllText(lout, $"LAUNCHED {r.Server.StartBat} pid={r.ScriptPid} injob={ServerLaunch.InJob()} running={ServerLaunch.IsRunning(r.Server.Path)}");
            }
            catch (Exception ex) { File.WriteAllText(lout, "ERROR " + ex.Message); }
            Shutdown();
            return;
        }

        // --stoptest <out> --server <dir> [--bat x.bat] [--grace s]: starts the script, waits for the server,
        // stops it the way the Stop button does and reports what is left (no state file is touched).
        if (Arg("--stoptest") is { } tout && Arg("--server") is { } tdir)
        {
            var lines = new List<string>();
            try
            {
                var bat = Arg("--bat") ?? ServerLaunch.Scripts(tdir).First();
                var pid = ServerLaunch.Start(tdir, bat);
                for (var i = 0; i < 40 && !ServerLaunch.IsRunning(tdir); i++) await Task.Delay(250);
                lines.Add($"started script={pid} alive={ServerLaunch.IsAlive(pid)} servers={ServerLaunch.ServerPids(tdir).Count}");
                var grace = TimeSpan.FromSeconds(int.TryParse(Arg("--grace"), out var g) ? g : 60);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var r = await ServerLaunch.StopAsync(tdir, pid, grace);
                lines.Add($"stopped scripts={r.Scripts} servers={r.Servers} forced={r.Forced} ms={sw.ElapsedMilliseconds}");
                await Task.Delay(3000);
                lines.Add($"after 3s: script alive={ServerLaunch.IsAlive(pid)} servers={ServerLaunch.ServerPids(tdir).Count}");
            }
            catch (Exception ex) { lines.Add("ERROR " + ex.Message); }
            File.WriteAllLines(tout, lines);
            Shutdown();
            return;
        }

        // --graftcheck <out> --server <dir> [--bat x.bat]: the "Check GRAFT" report as text.
        if (Arg("--graftcheck") is { } gout && Arg("--server") is { } gdir)
        {
            var lines = new System.Collections.Generic.List<string>();
            try
            {
                var tools = (await new PackBackend().GetSettingsAsync()).Tools;
                var host = Toolchain.HostDll(tools);
                var srv = new ServerInfo("x", System.IO.Path.GetFileName(gdir), gdir, Arg("--build") ?? "?", 2, GraftDoctor.Probe(gdir, host));
                if (Arg("--plugin") is { } pl) lines.Add("PLUGIN -> " + GraftDoctor.InstallPlugin(gdir, pl));
                lines.Add("HOST " + srv.Host);
                var bat = Arg("--bat") ?? ServerLaunch.Scripts(gdir).FirstOrDefault();
                foreach (var c in GraftDoctor.Run(srv, bat, host, Toolchain.Graft(tools)))
                    lines.Add($"{c.State,-4} {c.Key} [{string.Join(" | ", c.Args)}] {c.Action} {c.Target}");
            }
            catch (Exception ex) { lines.Add("ERROR " + ex); }
            File.WriteAllLines(gout, lines);
            Shutdown();
            return;
        }

        if (Arg("--mods") is { } mout && Arg("--server") is { } mdir)
        {
            var lines = new System.Collections.Generic.List<string>();
            try
            {
                var bat = Arg("--bat") ?? ServerLaunch.Scripts(mdir).FirstOrDefault() ?? "ServerStart.bat";
                var st = ServerMods.Setup(mdir, bat, Arg("--workshop"));
                lines.Add($"BAT {st.Bat}");
                lines.Add($"WORKSHOP {st.WorkshopDir} available={st.Available.Count}");
                if (st.Error != null) lines.Add("ERROR " + st.Error);
                if (st.Warning != null) lines.Add("WARNING " + st.Warning);
                lines.Add($"DELAYED {st.Delayed} BUSY {st.ScriptBusy}");
                foreach (var a in st.Available) lines.Add($"AV {a.Name} | id={a.Id} keys={a.Keys} | {a.Target}");
                foreach (var a in st.Local ?? Array.Empty<WorkshopMod>()) lines.Add($"LOC {a.Name} | id={a.Id} keys={a.Keys} | {a.Path}");
                foreach (var m in st.CurrentServer ?? Array.Empty<ModEntry>()) lines.Add($"SRV {(m.WorkshopPath != null ? "ws " : "loc")} {(m.Exists ? "ok  " : "miss")} {m.Name} | {m.Text}");
                foreach (var m in st.Current) lines.Add($"CUR {(m.WorkshopPath != null ? "ws " : "loc")} {(m.Exists ? "ok  " : "miss")} {m.Name} | {m.Text}");
                // --set "CF|VPP" replaces -mod, --setsrv "@X|..." replaces -serverMod ("" empties it).
                if (Arg("--set") is { } || Arg("--setsrv") is { })
                {
                    var known = st.Available.Concat(st.Local ?? Array.Empty<WorkshopMod>()).ToList();
                    List<ModPick> Picks(string list) => list.Split('|', StringSplitOptions.RemoveEmptyEntries).Select(x =>
                    {
                        var ws = known.FirstOrDefault(a => a.Name.StartsWith(x.TrimStart('@'), StringComparison.OrdinalIgnoreCase));
                        return ws != null ? new ModPick(ws.Path, ws.Target) : new ModPick(x, null);
                    }).ToList();
                    var picks = Arg("--set") is { } set ? Picks(set) : st.Current.Select(m => new ModPick(m.Text, null)).ToList();
                    var srvPicks = Arg("--setsrv") is { } ss ? Picks(ss) : null;
                    var saved = ServerMods.Save(mdir, bat, picks, srvPicks);
                    lines.Add($"SAVED mods={saved.Mods} server={saved.ServerMods} keys={saved.KeysCopied} backup={saved.Backup} nokeys={string.Join(",", saved.NoKeys)}");
                }
            }
            catch (Exception ex) { lines.Add("ERROR " + ex.Message); }
            File.WriteAllLines(mout, lines);
            Shutdown();
            return;
        }

        if (args.Contains("--seed"))
        {
            var b = new PackBackend();
            try { if (Arg("--project") is { } pr) await b.AddModAsync(pr); } catch (Exception) { }
            try { if (Arg("--server") is { } sv) await b.AddServerAsync(sv); } catch (Exception) { }
            try { await b.CreateKeyAsync("CG"); } catch (Exception) { }
            Shutdown();
            return;
        }

        // Real backend for real use; the mock stays for --shot demos/screenshots.
        // --backend real|mock forces one (used to screenshot real tabs).
        IGraftBackend backend = Arg("--backend") switch
        {
            "real" => new PackBackend(),
            "mock" => new MockBackend(),
            _ => Arg("--shot") != null ? new MockBackend() : new PackBackend(),
        };
        var vm = new MainViewModel(backend);
        var win = new MainWindow { DataContext = vm };
        var size = Arg("--size");
        if (size != null)
        {
            var wh = size.Split('x');
            win.Width = double.Parse(wh[0]);
            win.Height = double.Parse(wh[1]);
        }
        MainWindow = win;
        win.Show();
        await vm.InitAsync();

        var shot = Arg("--shot");
        if (shot == null) return;

        if (Arg("--lang") is { } lang) Loc.Set(lang);
        if (Arg("--mod") is { } modName) vm.SelectedMod = vm.Mods.FirstOrDefault(m => m.Name == modName) ?? vm.SelectedMod;
        var state = Arg("--state") ?? "idle";
        switch (state)
        {
            case "empty":
                vm.DemoEmpty = true;
                break;
            case "brokenenv":
                vm.DemoEnv = true;
                break;
            case "run":
                await vm.StartAsync(null);
                await Task.Delay(3600);
                break;
            case "done":
                await vm.StartAsync(null);
                await Task.Delay(14500);
                break;
            case "error":
                vm.DemoFail = true;
                await vm.StartAsync(null);
                await Task.Delay(6500);
                break;
            case "journal":
                vm.JournalCmd.Execute(null);
                break;
            case "menu":
                vm.MenuOpen = true;
                break;
        }
        if (Arg("--page") is { } page) vm.Page = page;
        if (args.Contains("--opts")) vm.OptionsOpen = true;
        if (Arg("--srvstate") is { } sst && vm.Servers.FirstOrDefault() is { } ss0)
        {
            await Task.Delay(800);
            ss0.IsRunning = true;
            if (sst == "armed") ss0.StopArmed = true;
            if (sst == "stopping") ss0.IsStopping = true;
        }
        if (args.Contains("--workshop") && vm.Servers.FirstOrDefault(s => s.HasScripts) is { } ws)
        {
            await vm.OpenWorkshopAsync(ws);
            if (Arg("--search") is { } q && vm.Workshop != null) vm.Workshop.Search = q;
            if (Arg("--wsource") is { } wsrc && vm.Workshop != null) vm.Workshop.Source = wsrc;
            if (Arg("--wtarget") is { } wdst && vm.Workshop != null) vm.Workshop.Target = wdst;
        }
        if (Arg("--check") is { } ck)
        {
            vm.MultiSelect = true;
            foreach (var cn in ck.Split('|'))
                if (vm.Mods.FirstOrDefault(m => m.Name == cn) is { } cm) vm.CheckClick(cm, false);
        }
        if (args.Contains("--batch"))
        {
            if (args.Contains("--failone")) vm.DemoFail = true;
            _ = vm.StartBatchAsync();
            if (int.TryParse(Arg("--batchwait"), out var bw)) await Task.Delay(bw);
        }
        if (args.Contains("--batchdetail") && vm.Result?.Rows.FirstOrDefault(r => r.HasError) is { } brow) vm.BatchDetailCmd.Execute(brow);
        if (Arg("--addmods") is { } am) await vm.AddModsAsync(am.Split('|'));
        if (Arg("--setcommon") is { } sc) await vm.SetCommonForShotAsync(sc);
        if (args.Contains("--makecommon") && vm.MakeCommonCmd.CanExecute(null)) { vm.MakeCommonCmd.Execute(null); await Task.Delay(800); }
        if (args.Contains("--gcheck") && vm.Servers.FirstOrDefault() is { } gs)
        {
            vm.CheckGraftCmd.Execute(gs);
            while (gs.IsChecking || !gs.HasReport) await Task.Delay(200);
        }
        if (args.Contains("--max"))
        {
            win.WindowState = WindowState.Maximized;
            await Task.Delay(600);
            var src = PresentationSource.FromVisual(win);
            var tl = win.PointToScreen(new Point(0, 0));
            var br = win.PointToScreen(new Point(win.ActualWidth, win.ActualHeight));
            var wa = SystemParameters.WorkArea;
            var k = src?.CompositionTarget?.TransformToDevice.M11 ?? 1;
            File.WriteAllText(shot + ".txt",
                $"window px: {tl.X},{tl.Y} - {br.X},{br.Y}{Environment.NewLine}work area px: {wa.Left * k},{wa.Top * k} - {wa.Right * k},{wa.Bottom * k}{Environment.NewLine}");
        }
        if (args.Contains("--scrollend")) foreach (var sv in Descendants(win).OfType<System.Windows.Controls.ScrollViewer>()) sv.ScrollToEnd();
        await Task.Delay(int.TryParse(Arg("--delay"), out var delay) ? delay : 1500);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

        var root = (FrameworkElement)win.Content;
        var dpi = VisualTreeHelper.GetDpi(root);
        var rtb = new RenderTargetBitmap((int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY),
            96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        rtb.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        await using (var fs = File.Create(shot)) enc.Save(fs);

        // Popups live in their own windows and never reach the main render: save each open one next to it.
        var n = 0;
        foreach (var popup in Descendants(win).OfType<System.Windows.Controls.Primitives.Popup>().Where(p => p.IsOpen && p.Child is FrameworkElement))
        {
            var child = (FrameworkElement)popup.Child;
            var pr = new RenderTargetBitmap((int)(child.ActualWidth * dpi.DpiScaleX), (int)(child.ActualHeight * dpi.DpiScaleY),
                96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            pr.Render(child);
            var pe = new PngBitmapEncoder();
            pe.Frames.Add(BitmapFrame.Create(pr));
            await using var pf = File.Create(shot + ".popup" + ++n + ".png");
            pe.Save(pf);
        }
        Shutdown();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            yield return c;
            foreach (var d in Descendants(c)) yield return d;
        }
    }
}
