using System.Diagnostics;
using System.IO;

namespace NDZ.PBOGPacker.Core;

public sealed class MockBackend : IGraftBackend
{
    private const string ServerLive = @"C:\Program Files (x86)\Steam\steamapps\common\DayZServer";

    public bool EmptyMods { get; set; }
    public bool BrokenEnv { get; set; }
    public bool FailNext { get; set; }

    private List<ModInfo> _mods = new();
    private List<ServerInfo> _servers = new();
    private List<KeyInfo> _keys = new();
    private AppSettings _settings = new();
    private readonly Dictionary<string, CancellationTokenSource> _runs = new();
    private readonly Random _rng = new(163451);

    public event Action<PipelineStarted>? Started;
    public event Action<PipelineStep>? StepChanged;
    public event Action<PipelineLog>? Log;
    public event Action<PipelineDone>? Done;
    public event Action<PipelineError>? Failed;
    public event Action<PipelineCancelled>? Cancelled;

    public MockBackend() => Reset();

    public void Reset()
    {
        var now = DateTime.Now;
        EmptyMods = BrokenEnv = FailNext = false;
        _mods = new()
        {
            new("radio", "CG_RadioRange", "0.1.0", @"D:\Mods\CG_RadioRange", new[] { "4_World", "5_Mission" }, "CG",
                new("ok", now.AddMinutes(-42), 38400), new("ok", "CG"), new("stale", new[] { "live" }, now.AddDays(-2)), "pack://application:,,,/NDZ_PBOGPacker;component/Assets/Photos/logo_radio.jpg"),
            new("stats", "CG_ServerStats", "1.0.2", @"D:\Mods\CG_ServerStats", new[] { "5_Mission" }, "CG",
                new("ok", now.AddDays(-2), 21700), new("ok", "CG"), new("ok", new[] { "live", "pog" }, now.AddDays(-2)), "pack://application:,,,/NDZ_PBOGPacker;component/Assets/Photos/logo_stats.jpg"),
            new("time", "CG_NativeTime", "0.3.0-dev", @"D:\Mods\CG_NativeTime", new[] { "3_Game", "4_World" }, "CG",
                new("fail", now.AddHours(-3), 0, "build"), new("none"), new("none", Array.Empty<string>())),
            new("radioc", "CG_RadioRangeClient", "0.1.0", @"D:\Mods\CG_RadioRangeClient", new[] { "3_Game", "5_Mission" }, "CG",
                new("ok", now.AddMinutes(-40), 4100), new("ok", "CG"), new("ok", new[] { "live" }, now.AddMinutes(-40)), null, ModKinds.Client),
        };
        _servers = new()
        {
            new("live", "DayZServer · Steam", ServerLive, "1.29.0.163709", 2, new(true, "v0.4.0")),
            new("pog", "POG · тест", @"D:\Servers\POG", "1.29.0.163709", 0, new(false, null)),
        };
        _keys = new()
        {
            new("CG", "A3F1 9C0E 77B2 D41A 05E8 C2B9", "2026-08-02", @"D:\Mods\keys\CG.bikey"),
            new("CG_TEST", "5D02 E8A7 1B4C 90FF 3E61 7A20", "2026-09-28", @"D:\Mods\keys\CG_TEST.bikey"),
        };
        _settings = new AppSettings
        {
            Tools = new ToolPaths
            {
                CMake = @"C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe",
                PboProject = @"C:\Program Files (x86)\Mikero\DePboTools\bin\pboProject.exe",
                Graft = @"D:\Mods\tools\graft.exe",
                HostDll = @"D:\Mods\tools\hid.dll",
                KeyDir = @"D:\Mods\keys",
            },
            Defaults = new BuildDefaults { Preset = "release", Sign = true, Key = "CG", Server = "live" },
            Pack = new PackSettings { ClientOut = ServerLive + @"\@MyAddon", ServerOut = ServerLive + @"\@CG_ServerMods" },
        };
    }

    private static Task Lag(int ms = 60) => Task.Delay(ms);

    public async Task<IReadOnlyList<ModInfo>> ListModsAsync() { await Lag(); return EmptyMods ? Array.Empty<ModInfo>() : _mods.ToList(); }

    public async Task<ModInfo> AddModAsync(string? path)
    {
        await Lag();
        if (EmptyMods)
        {
            EmptyMods = false;
            return _mods[0];
        }
        var name = string.IsNullOrEmpty(path) ? "CG_ZoneClock" : Path.GetFileName(path.TrimEnd('\\', '/'));
        var existing = _mods.FirstOrDefault(m => m.Name == name);
        if (existing != null) return existing;
        var mod = new ModInfo("m" + DateTime.Now.Ticks, name, "0.0.1", path ?? @"D:\Mods\" + name, new[] { "4_World" }, "CG",
            new("none"), new("none"), new("none", Array.Empty<string>()), FindLogo(path, name));
        _mods.Add(mod);
        return mod;
    }

    public async Task RemoveModAsync(string id) { await Lag(); _mods.RemoveAll(m => m.Id == id); }

    public async Task<ModInfo> SetModLogoAsync(string id, string? imagePath)
    {
        await Lag();
        var i = _mods.FindIndex(m => m.Id == id);
        string? stored = null;
        if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NDZ_PBOGPacker", "logos");
            Directory.CreateDirectory(dir);
            stored = Path.Combine(dir, id + "_" + DateTime.Now.Ticks + Path.GetExtension(imagePath));
            File.Copy(imagePath, stored, true);
        }
        _mods[i] = _mods[i] with { Logo = stored };
        return _mods[i];
    }

    public async Task<ModInfo> SetModPackAsync(string id, string kind, string key, string? outDir)
    {
        await Lag();
        var i = _mods.FindIndex(m => m.Id == id);
        _mods[i] = _mods[i] with
        {
            Kind = kind,
            Key = string.IsNullOrWhiteSpace(key) ? _mods[i].Key : key,
            Out = string.IsNullOrWhiteSpace(outDir) ? null : outDir,
        };
        return _mods[i];
    }

    private static string? FindLogo(string? dir, string name)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
        foreach (var sub in new[] { "", Path.Combine("mod", name) })
            foreach (var file in new[] { "logo.png", "logo.jpg", "icon.png", "icon.jpg" })
            {
                var p = Path.Combine(dir, sub, file);
                if (File.Exists(p)) return p;
            }
        return null;
    }
    public async Task<IReadOnlyList<ServerInfo>> ListServersAsync() { await Lag(); return _servers.ToList(); }

    public async Task<ServerInfo?> AddServerAsync(string path)
    {
        await Lag();
        var s = new ServerInfo("s" + DateTime.Now.Ticks, Path.GetFileName(path.TrimEnd('\\')), path, "1.29.0.163709", 2, new(false, null));
        _servers.Add(s);
        return s;
    }

    public async Task<ServerInfo> InstallHostAsync(string serverId)
    {
        await Task.Delay(900);
        var i = _servers.FindIndex(s => s.Id == serverId);
        _servers[i] = _servers[i] with { Host = new(true, "v0.4.0", "ok") };
        return _servers[i];
    }

    public async Task<ServerInfo> UninstallHostAsync(string serverId)
    {
        await Task.Delay(400);
        var i = _servers.FindIndex(s => s.Id == serverId);
        _servers[i] = _servers[i] with { Host = new(false, null, "none") };
        return _servers[i];
    }

    public async Task<ModInfo> CreateCppProjectAsync(string id)
    {
        await Task.Delay(500);
        var i = _mods.FindIndex(m => m.Id == id);
        _mods[i] = _mods[i] with { Cpp = CppScaffold.DefaultDir(_mods[i]), Kind = ModKinds.Grafted };
        return _mods[i];
    }

    public async Task<string> InstallPluginAsync(string serverId, string dll)
    {
        await Task.Delay(400);
        return System.IO.Path.Combine(_servers.First(s => s.Id == serverId).Path, "grafted", System.IO.Path.GetFileName(dll));
    }

    // Demo servers are not touched: the report is read from disk as it is, host state as the demo says.
    public async Task<GraftReport> CheckGraftAsync(string serverId, string? bat)
    {
        await Lag();
        var srv = _servers.First(s => s.Id == serverId);
        var items = System.IO.Directory.Exists(srv.Path)
            ? await Task.Run(() => GraftDoctor.Run(srv, bat, _settings.Tools.HostDll, _settings.Tools.Graft))
            : new List<GraftCheck> { new(srv.Host.Installed ? "HostOk" : "HostNone", srv.Host.Installed ? "ok" : "err", srv.Host.Installed ? ["v0.4.0", srv.Path + @"\hid.dll"] : [], srv.Host.Installed ? null : "install") };
        return new GraftReport(srv, items, DateTime.Now);
    }

    public async Task<ServerLaunched> LaunchServerAsync(string serverId, string? bat)
    {
        await Task.Delay(300);
        var i = _servers.FindIndex(s => s.Id == serverId);
        _servers[i] = _servers[i] with { StartBat = bat ?? ServerLaunch.Pick(_servers[i]) };
        return new ServerLaunched(_servers[i], 0);
    }

    public async Task<ServerStopped> StopServerAsync(string serverId, int scriptPid)
    {
        await Task.Delay(1500);
        return new ServerStopped(_servers.First(s => s.Id == serverId), 0, 1, false);
    }

    public async Task<ModsSetup> GetServerModsAsync(string serverId, string bat)
    {
        await Lag();
        var srv = _servers.First(s => s.Id == serverId);
        return await Task.Run(() => ServerMods.Setup(srv.Path, bat, _settings.WorkshopDir));
    }

    public async Task<ModsSaved> SaveServerModsAsync(string serverId, string bat, IReadOnlyList<ModPick> mods, IReadOnlyList<ModPick> serverMods)
    {
        await Task.Delay(400);
        var srv = _servers.First(s => s.Id == serverId);
        return new ModsSaved(Path.Combine(srv.Path, bat), null, mods.Count, 0, Array.Empty<string>(), serverMods.Count);
    }

    public async Task<IReadOnlyList<KeyInfo>> ListKeysAsync() { await Lag(); return _keys.ToList(); }

    public async Task<KeyInfo> CreateKeyAsync(string tag)
    {
        await Task.Delay(300);
        string Hex() => _rng.Next(65536).ToString("X4");
        var k = new KeyInfo(tag, string.Join(' ', Enumerable.Range(0, 6).Select(_ => Hex())), DateTime.Now.ToString("yyyy-MM-dd"),
            Path.Combine(_settings.Tools.KeyDir, tag + ".bikey"));
        _keys.Add(k);
        return k;
    }

    public async Task<string> ExportKeyAsync(string tag) { await Lag(); return _keys.First(k => k.Tag == tag).Path; }

    public async Task<IReadOnlyList<DoctorItem>> DoctorAsync()
    {
        // Real environment probe for normal runs; the mocked "broken" list stays for --shot demos.
        if (!BrokenEnv) return await ToolDetect.DetectAsync(_settings.Tools);

        await Task.Delay(900);
        var b = BrokenEnv;
        var t = _settings.Tools;
        return new List<DoctorItem>
        {
            new("clang", "clang-cl", null, b ? CheckState.Err : CheckState.Ok, b ? "" : "23.1.2", @"C:\Program Files\LLVM\bin\clang-cl.exe", "Fix.Clang", "winget install LLVM.LLVM"),
            new("cmake", "CMake", null, CheckState.Ok, "3.31.6", t.CMake),
            new("ninja", "Ninja", null, CheckState.Ok, "1.12.1", @"…\CMake\Ninja\ninja.exe"),
            new("vs", null, "Env.Vs", CheckState.Ok, "10.0.26100", @"C:\Program Files\Microsoft Visual Studio\2022\Community"),
            new("pbo", "AddonBuilder · DayZ Tools", null, CheckState.Ok, "ok", @"C:\Program Files (x86)\Steam\steamapps\common\DayZ Tools\Bin\AddonBuilder\AddonBuilder.exe"),
            new("cfgconv", "CfgConvert · DayZ Tools", null, CheckState.Ok, "ok", @"C:\Program Files (x86)\Steam\steamapps\common\DayZ Tools\Bin\CfgConvert\CfgConvert.exe"),
            new("dssign", "DSSignFile", null, CheckState.Ok, "ok", @"C:\Program Files (x86)\Steam\steamapps\common\DayZ Tools\Bin\DsUtils\DSSignFile.exe"),
            new("graft", "graft.exe", null, b ? CheckState.Warn : CheckState.Ok, b ? "v0.1.4 ≠ v0.4.0" : "v0.4.0", t.Graft, "Fix.Graft",
                "https://github.com/KRdayzmodding/KR_GRAFTED/releases/tag/v0.4.0", true),
            new("host", null, "Env.HostTool", CheckState.Ok, "v0.4.0", t.HostDll),
            new("pdrive", null, "Env.PDrive", CheckState.Ok, @"P:\", "", "Fix.PDrive", "subst P: <folder>"),
        };
    }

    public async Task<AppSettings> GetSettingsAsync() { await Lag(); return _settings; }
    public async Task SaveSettingsAsync(AppSettings settings) { await Lag(); _settings = settings; }

    public Task OpenPathAsync(string path)
    {
        if (Directory.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        else if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        return Task.CompletedTask;
    }

    public async Task<string> RunPipelineAsync(string modId, PipelineOptions options)
    {
        await Lag(20);
        var runId = "r" + DateTime.Now.Ticks.ToString("x");
        var cts = new CancellationTokenSource();
        _runs[runId] = cts;
        _ = RunAsync(runId, modId, options, cts.Token);
        return runId;
    }

    public Task CancelAsync(string runId)
    {
        if (_runs.TryGetValue(runId, out var cts) && !cts.IsCancellationRequested)
        {
            cts.Cancel();
            Cancelled?.Invoke(new PipelineCancelled(runId));
        }
        return Task.CompletedTask;
    }

    private static readonly Dictionary<string, int> Dur = new()
    {
        ["env"] = 700, ["build"] = 5200, ["protogen"] = 1000, ["pack"] = 2300,
        ["sign"] = 700, ["assemble"] = 600, ["deploy"] = 1500, ["host"] = 600,
    };

    private async Task RunAsync(string runId, string modId, PipelineOptions o, CancellationToken ct)
    {
        try
        {
            var m = _mods.First(x => x.Id == modId);
            var srv = _servers.FirstOrDefault(s => s.Id == o.Server) ?? _servers[0];
            await Task.Delay(40, ct);
            Started?.Invoke(new PipelineStarted(runId, Steps.Ids));
            var failAt = BrokenEnv ? "env" : (FailNext && o.Only == null ? (m.Kind == ModKinds.Grafted ? "build" : "pack") : null);
            var root = PackLayout.OutRoot(m, _settings.Pack);
            var fromIdx = o.FromStep == null ? 0 : Array.IndexOf(Steps.Ids, o.FromStep);
            var total = Stopwatch.StartNew();
            for (var i = 0; i < Steps.Ids.Length; i++)
            {
                var id = Steps.Ids[i];
                var off = !PackBackend.StepApplies(id, m.Kind, true)
                          || (id == "sign" && !o.Sign) || (id == "deploy" && !o.Deploy) || (id == "host" && !o.Host)
                          || (o.Only != null && !o.Only.Contains(id)) || (i < fromIdx && id != "env");
                if (off)
                {
                    StepChanged?.Invoke(new PipelineStep(runId, id, StepStatus.Skipped, 0));
                    await Task.Delay(60, ct);
                    continue;
                }
                StepChanged?.Invoke(new PipelineStep(runId, id, StepStatus.Running, 0));
                var sw = Stopwatch.StartNew();
                var fail = failAt == id;
                var lines = Lines(id, fail, m, o, srv, root);
                foreach (var (level, line) in lines)
                {
                    await Task.Delay((int)(Dur[id] / (double)lines.Count * (0.5 + _rng.NextDouble())), ct);
                    Log?.Invoke(new PipelineLog(runId, id, line, level));
                }
                if (fail)
                {
                    StepChanged?.Invoke(new PipelineStep(runId, id, StepStatus.Error, sw.ElapsedMilliseconds));
                    if (id == "build")
                    {
                        FailNext = false;
                        Replace(m with { Build = new("fail", DateTime.Now, 0, "build") });
                        Failed?.Invoke(new PipelineError(runId, id, "error: no member named 'SetFrequencyByIndex' in 'graft::ItemTransmitter'",
                            "src/natives/frequency.cpp:41:9", lines.TakeLast(8).Select(l => new LogEntry(l.Item1, l.Item2)).ToList(), "Fix.Build", null));
                    }
                    else if (id == "pack")
                    {
                        FailNext = false;
                        Replace(m with { Build = new("fail", DateTime.Now, 0, "pack") });
                        Failed?.Invoke(new PipelineError(runId, id, "config.cpp не бинаризуется: config.cpp", Path.Combine(m.Path, "config.cpp"),
                            lines.TakeLast(4).Select(l => new LogEntry(l.Item1, l.Item2)).ToList(), "Fix.Rapify", null));
                    }
                    else
                    {
                        Failed?.Invoke(new PipelineError(runId, id, "clang-cl is not installed", @"C:\Program Files\LLVM\bin\clang-cl.exe",
                            lines.Select(l => new LogEntry(l.Item1, l.Item2)).ToList(), "Fix.Clang", "winget install LLVM.LLVM"));
                    }
                    return;
                }
                StepChanged?.Invoke(new PipelineStep(runId, id, StepStatus.Done, sw.ElapsedMilliseconds));
            }
            var now = DateTime.Now;
            if (o.Only == null)
                m = m with { Build = new("ok", now, total.ElapsedMilliseconds), Sign = o.Sign ? new("ok", m.Key) : new("none") };
            if (o.Deploy)
            {
                var list = m.Deploy.State == "none" ? new List<string>() : m.Deploy.Servers.ToList();
                if (!list.Contains(srv.Id)) list.Add(srv.Id);
                m = m with { Deploy = new("ok", list, now) };
            }
            Replace(m);
            if (o.Host && m.Kind == ModKinds.Grafted)
            {
                var si = _servers.FindIndex(s => s.Id == srv.Id);
                _servers[si] = srv with { Host = new(true, "v0.4.0") };
            }
            var onServer = PackLayout.IsUnder(root, srv.Path);
            var dir = onServer ? root : Path.Combine(srv.Path, Path.GetFileName(root));
            var modLine = PackLayout.LaunchKey(m.Kind) + Path.GetRelativePath(srv.Path, dir);
            Done?.Invoke(new PipelineDone(runId, modLine, "DayZServer_x64.exe -config=serverDZ.cfg -port=2302 -profiles=profiles " + modLine,
                m.Kind == ModKinds.Grafted ? 39118 : 18422, root, o.Deploy || onServer ? dir : "", total.ElapsedMilliseconds));
        }
        catch (OperationCanceledException) { }
        finally { _runs.Remove(runId); }
    }

    private void Replace(ModInfo m)
    {
        var i = _mods.FindIndex(x => x.Id == m.Id);
        if (i >= 0) _mods[i] = m;
    }

    private static List<(LogLevel, string)> Lines(string id, bool fail, ModInfo m, PipelineOptions o, ServerInfo srv, string root)
    {
        var I = LogLevel.Info; var C = LogLevel.Cmd; var K = LogLevel.Ok; var W = LogLevel.Warn; var E = LogLevel.Err;
        var leaf = Path.GetFileName(root);
        var grafted = m.Kind == ModKinds.Grafted;
        switch (id)
        {
            case "env" when fail:
                return new()
                {
                    (C, "[NDZ] doctor"),
                    (E, @"clang-cl: not found (C:\Program Files\LLVM\bin\clang-cl.exe)"),
                    (W, "graft.exe v0.1.4 != graft.boot v0.4.0"),
                    (E, "[NDZ] ERROR: clang-cl is not installed. Install it (winget install LLVM.LLVM) or add the VS 'C++ Clang tools' component, then re-run."),
                };
            case "env" when !grafted:
                return new()
                {
                    (K, @"ok  AddonBuilder (DayZ Tools)  C:\Program Files (x86)\Steam\steamapps\common\DayZ Tools\Bin\AddonBuilder\AddonBuilder.exe"),
                    (K, @"ok  CfgConvert (DayZ Tools)  C:\Program Files (x86)\Steam\steamapps\common\DayZ Tools\Bin\CfgConvert\CfgConvert.exe"),
                    (K, @"ok  DSSignFile (DayZ Tools)  C:\Program Files (x86)\Steam\steamapps\common\DayZ Tools\Bin\DsUtils\DSSignFile.exe"),
                    (I, "исходники: " + m.Path),
                    (I, $@"куда: {root}\addons\{m.Name}.pbo"),
                    (I, "префикс PBO: " + m.Name),
                };
            case "env":
                return new()
                {
                    (C, "[NDZ] doctor"),
                    (I, "clang-cl 23.1.2 · cmake 3.31.6 · ninja 1.12.1 · Windows SDK 10.0.26100"),
                    (I, "AddonBuilder ok · CfgConvert ok · DSSignFile ok · graft v0.4.0 (graft.boot v0.4.0)"),
                    (I, $@"куда: {root}\addons\{m.Name}.pbo"),
                };
            case "pack" when fail:
                return new()
                {
                    (I, "исходники: 23 файлов, 61,4 КБ"),
                    (C, "CfgConvert -bin config.cpp"),
                    (W, $@"File {m.Path}\config.cpp, line 41: /CfgMods/{m.Name}.defs: '}}' encountered instead of ';'"),
                    (W, $@"Error reading config file '{m.Path}\config.cpp'"),
                };
            case "build" when fail:
                return new()
                {
                    (C, $"[NDZ] cmake configure + build (preset: {o.Preset})"),
                    (I, "-- The CXX compiler identification is Clang 23.1.2 with MSVC-like command-line"),
                    (I, "-- graft.boot: graft_import KR_GRAFTED TAG v0.4.0"),
                    (I, "-- Configuring done (1.8s) · Generating done (0.2s)"),
                    (I, $"[1/9] Building CXX object CMakeFiles/{m.Name}.dir/src/plugin.cpp.obj"),
                    (I, $"[3/9] Building CXX object CMakeFiles/{m.Name}.dir/src/natives/frequency.cpp.obj"),
                    (E, $"FAILED: CMakeFiles/{m.Name}.dir/src/natives/frequency.cpp.obj"),
                    (E, "src/natives/frequency.cpp(41,9): error: no member named 'SetFrequencyByIndex' in 'graft::ItemTransmitter'"),
                    (I, "   41 |     tx->SetFrequencyByIndex(idx);"),
                    (I, "      |     ~~~~^"),
                    (E, "1 error generated."),
                    (E, "ninja: build stopped: subcommand failed."),
                    (E, "[NDZ] ERROR: cmake build failed"),
                };
            case "build":
                return new()
                {
                    (C, $"[NDZ] cmake configure + build (preset: {o.Preset})"),
                    (I, "-- The CXX compiler identification is Clang 23.1.2 with MSVC-like command-line"),
                    (I, "-- graft.boot: graft_import KR_GRAFTED TAG v0.4.0"),
                    (I, $"-- graft_plugin({m.Name} VERSION {m.Version} MODULES {string.Join(' ', m.Modules)})"),
                    (I, "-- Configuring done (1.9s) · Generating done (0.2s)"),
                    (I, $"[1/9] Building CXX object CMakeFiles/{m.Name}.dir/src/plugin.cpp.obj"),
                    (I, $"[3/9] Building CXX object CMakeFiles/{m.Name}.dir/src/hooks/transmitter.cpp.obj"),
                    (W, "src/hooks/transmitter.cpp(88,17): warning: unused variable 'prevIdx' [-Wunused-variable]"),
                    (I, $"[6/9] Building CXX object CMakeFiles/{m.Name}.dir/src/natives/frequency.cpp.obj"),
                    (I, $"[9/9] Linking CXX shared library {m.Name}.grafted.dll"),
                    (K, $@"[NDZ] plugin DLL: {m.Path}\build\{o.Preset}\{m.Name}.grafted.dll"),
                };
            case "protogen":
            {
                var l = new List<(LogLevel, string)> { (C, $"graft protogen --plugin {m.Name}.grafted.dll --out {m.Name}.scripts") };
                var counts = new[] { 6, 2, 3 };
                for (var i = 0; i < m.Modules.Count; i++) l.Add((I, $"  {m.Modules[i]}: {counts[i % 3]} natives -> grafted_natives_{m.Name}.c"));
                l.Add((K, $@"[NDZ] syncing declarations -> mod\{m.Name}\scripts"));
                return l;
            }
            case "pack":
                return new()
                {
                    (I, grafted ? "исходники: 14 файлов, 38,2 КБ" : "исходники: 23 файлов, 61,4 КБ"),
                    (C, "CfgConvert -bin config.cpp"),
                    (K, "config.bin: 1"),
                    (C, "AddonBuilder -packonly -prefix=" + m.Name),
                    (I, "Packing... Build Successful"),
                    (K, $@"PBO: {root}\addons\{m.Name}.pbo ({(grafted ? "39 118" : "18 422")} б)"),
                };
            case "sign":
                return new() { (C, $"DSSignFile {m.Key}"), (K, $"подпись: {m.Name}.pbo.{m.Key}.bisign") };
            case "assemble":
            {
                var parts = "addons/" + (o.Sign ? " + keys/" : "") + (grafted ? " + grafted/" : "");
                return new() { (K, $"{leaf}: {parts}"), (I, root) };
            }
            case "deploy":
            {
                var l = new List<(LogLevel, string)>();
                if (PackLayout.IsUnder(root, srv.Path)) l.Add((I, "папка мода уже на сервере: " + root));
                else l.Add((K, $@"{leaf} -> {srv.Path}\{leaf}"));
                if (o.Sign) l.Add((K, $@"ключ {m.Key}.bikey -> {srv.Path}\keys"));
                l.Add((srv.VerifySignatures == 2 ? K : W, $"serverDZ.cfg: verifySignatures = {srv.VerifySignatures}"));
                return l;
            }
            default:
                return srv.Host.Installed
                    ? new() { (I, $"hid.dll {srv.Host.Version} already at {srv.Path}"), (K, "host up to date, copy skipped") }
                    : new() { (C, $@"copy tools\hid.dll ""{srv.Path}\hid.dll"""), (K, "GRAFT host v0.4.0 installed next to DayZServer_x64.exe") };
        }
    }
}
