using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PolyLab.Ui.Core;

namespace PolyLab.PBOGPacker.Core;

// Real backend: drives the actual toolchain (cmake+clang+graft -> CfgConvert -> AddonBuilder -> DSSignFile)
// and streams each tool's output into the 8 pipeline steps. Replaces MockBackend for real builds.
// Three kinds of mods: a plain client or server mod is only packed and signed the way pboProject does it;
// a GRAFT server mod also gets its C++ plugin built and laid out next to addons\ as the GRAFT README says.
// State (registered mods/servers/settings) is a small JSON file; keys are discovered on disk.
// Runs execute off the UI thread, so every access to the state goes through _gate.
public sealed class PackBackend : IGraftBackend
{
    public sealed class State
    {
        public List<ModInfo> Mods { get; set; } = new();
        public List<ServerInfo> Servers { get; set; } = new();
        public AppSettings Settings { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    // Mirrors the DayZ Tools exclude.lst plus editor/VCS junk, so the stage is exactly what gets packed.
    private static readonly string[] SkipDirs = { ".git", ".svn", ".hg", ".vs", ".vscode", ".idea", "Source", "Temp", "Doc" };
    private static readonly string[] SkipExts =
    {
        ".pbo", ".ebo", ".bisign", ".bikey", ".biprivatekey", ".bak", ".tmp", ".dep", ".log", ".rpt", ".psd",
        ".zip", ".7z", ".scc", ".biedi",
    };
    private static readonly string[] SkipFiles = { "Thumbs.db", "desktop.ini", ".DS_Store", ".gitignore", ".gitattributes" };

    private readonly string _statePath;
    private readonly object _gate = new();
    private State _s = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _runs = new();
    private readonly ConcurrentDictionary<string, Task> _work = new();

    public event Action<PipelineStarted>? Started;
    public event Action<PipelineStep>? StepChanged;
    public event Action<PipelineLog>? Log;
    public event Action<PipelineDone>? Done;
    public event Action<PipelineError>? Failed;
    public event Action<PipelineCancelled>? Cancelled;

    // POLYLAB_STATE_DIR keeps headless test runs away from the real mod list.
    public static string StateDir => Environment.GetEnvironmentVariable("POLYLAB_STATE_DIR")
        ?? DataFolder.Resolve(Environment.SpecialFolder.ApplicationData, "PBOGPacker", "NDZ_PBOGPacker");

    public PackBackend()
    {
        var dir = StateDir;
        Directory.CreateDirectory(dir);
        _statePath = Path.Combine(dir, "state.json");
        Load();
        _ = Task.Run(SweepTemp);
    }

    private void Load()
    {
        try { if (File.Exists(_statePath)) _s = JsonSerializer.Deserialize<State>(File.ReadAllText(_statePath)) ?? new(); }
        catch (Exception) { _s = new(); }
        if (string.IsNullOrEmpty(_s.Settings.Tools.KeyDir))
            _s.Settings.Tools = DefaultTools(Path.GetDirectoryName(_statePath)!);
        _s.Settings.Pack ??= new();
        // A hand-edited kind the tool does not know falls back to a guess.
        for (var i = 0; i < _s.Mods.Count; i++)
        {
            var m = _s.Mods[i];
            if (m.Kind is not (ModKinds.Client or ModKinds.Server or ModKinds.Grafted))
                _s.Mods[i] = m with { Kind = PackLayout.IsGraftProject(m.Path) ? ModKinds.Grafted : GuessKind(m.Name) };
        }
    }

    private void Save()
    {
        lock (_gate)
            try { File.WriteAllText(_statePath, JsonSerializer.Serialize(_s, Json)); } catch (Exception) { }
    }

    private static ToolPaths DefaultTools(string stateDir) => new()
    {
        CMake = Toolchain.CMake(new ToolPaths()) ?? "",
        PboProject = "",
        KeyDir = Path.Combine(stateDir, "keys"),
    };

    // ── mods ──────────────────────────────────────────────────────────────────
    public Task<IReadOnlyList<ModInfo>> ListModsAsync()
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<ModInfo>>(_s.Mods.ToList());
    }

    // Accepts a GRAFT project (CMakeLists.txt + mod\<Name>\config.cpp) or a plain mod folder with
    // config.cpp at its root, the same folder pboProject is pointed at.
    public Task<ModInfo> AddModAsync(string? path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            throw new InvalidOperationException("папка проекта не найдена");
        if (path.Length > 3) path = path.TrimEnd('\\', '/');
        lock (_gate)
        {
            var existing = _s.Mods.FirstOrDefault(m => string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase));
            if (existing != null) return Task.FromResult(existing);
        }

        string name, kind;
        var nested = PackLayout.FindNestedMod(path);
        if (nested != null)
        {
            name = nested;
            kind = PackLayout.IsGraftProject(path) ? ModKinds.Grafted : GuessKind(name);
        }
        else if (File.Exists(Path.Combine(path, "config.cpp")))
        {
            name = Path.GetFileName(path);
            kind = GuessKind(name);
        }
        else throw new InvalidOperationException("в папке нет config.cpp: ни в корне, ни в mod\\<Имя>\\");

        var mod = new ModInfo("m" + Guid.NewGuid().ToString("N")[..8], name, "", path, Array.Empty<string>(), _s.Settings.Defaults.Key,
            new("none"), new("none"), new("none", Array.Empty<string>()), FindLogo(path, name), kind);
        var src = PackLayout.SourceDir(mod);
        mod = mod with { Version = ReadVersion(path, src), Modules = ReadModules(src) };
        lock (_gate) _s.Mods.Add(mod);
        Save();
        return Task.FromResult(mod);
    }

    // The repo's own naming: CG_PDAServer, CG_TraderServer... are server-side and go the Grafted way
    // (their own @folder per the GRAFT README), the rest go to clients.
    private static string GuessKind(string name) =>
        name.EndsWith("Server", StringComparison.OrdinalIgnoreCase) ? ModKinds.Grafted : ModKinds.Client;

    public Task RemoveModAsync(string id)
    {
        lock (_gate) _s.Mods.RemoveAll(m => m.Id == id);
        Save();
        return Task.CompletedTask;
    }

    public Task<ModInfo> SetModLogoAsync(string id, string? imagePath)
    {
        string? stored = null;
        if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
        {
            var dir = Path.Combine(Path.GetDirectoryName(_statePath)!, "logos");
            Directory.CreateDirectory(dir);
            stored = Path.Combine(dir, id + Path.GetExtension(imagePath));
            File.Copy(imagePath, stored, true);
        }
        ModInfo result;
        lock (_gate)
        {
            var i = _s.Mods.FindIndex(m => m.Id == id);
            if (i < 0) throw new InvalidOperationException("мод не найден");
            result = _s.Mods[i] = _s.Mods[i] with { Logo = stored };
        }
        Save();
        return Task.FromResult(result);
    }

    // A new kind, key or destination makes the last build, signature and deploy describe other files,
    // so they are reset: the card Deploy must not ship a PBO built for the old settings.
    // Scaffolds <mod>_cpp next to the mod (or links it, when a GRAFT project is already there) and turns
    // the mod into a Grafted one; the build state is reset, the next build makes the plugin.
    public Task<ModInfo> CreateCppProjectAsync(string id)
    {
        ModInfo m;
        lock (_gate) m = _s.Mods.FirstOrDefault(x => x.Id == id) ?? throw new InvalidOperationException("мод не найден");
        if (PackLayout.IsGraftProject(m.Path)) throw new InvalidOperationException("мод уже лежит внутри C++-проекта");
        var dir = CppScaffold.Create(m);
        ModInfo result;
        lock (_gate)
        {
            var i = _s.Mods.FindIndex(x => x.Id == id);
            result = _s.Mods[i] = _s.Mods[i] with
            {
                Cpp = dir, Kind = ModKinds.Grafted,
                Build = new("none"), Sign = new("none"), Deploy = new("none", Array.Empty<string>()),
            };
        }
        Save();
        return Task.FromResult(result);
    }

    public Task<ModInfo> SetModPackAsync(string id, string kind, string key, string? outDir)
    {
        if (kind is not (ModKinds.Client or ModKinds.Server or ModKinds.Grafted))
            throw new InvalidOperationException("неизвестный тип мода: " + kind);
        ModInfo result;
        lock (_gate)
        {
            var i = _s.Mods.FindIndex(m => m.Id == id);
            if (i < 0) throw new InvalidOperationException("мод не найден");
            var m = _s.Mods[i];
            var next = m with
            {
                Kind = kind,
                Key = string.IsNullOrWhiteSpace(key) ? m.Key : key.Trim(),
                Out = string.IsNullOrWhiteSpace(outDir) ? null : outDir.Trim(),
            };
            var changed = next.Kind != m.Kind || next.Key != m.Key
                          || !string.Equals(PackLayout.OutRoot(next, _s.Settings.Pack), PackLayout.OutRoot(m, _s.Settings.Pack), StringComparison.OrdinalIgnoreCase);
            if (changed) next = next with { Build = new("none"), Sign = new("none"), Deploy = new("none", Array.Empty<string>()) };
            result = _s.Mods[i] = next;
        }
        Save();
        return Task.FromResult(result);
    }

    // ── servers ───────────────────────────────────────────────────────────────
    // The host is looked at again on every listing: it can be put in or taken out by hand at any time.
    public async Task<IReadOnlyList<ServerInfo>> ListServersAsync()
    {
        List<ServerInfo> list;
        string? tool;
        lock (_gate)
        {
            list = _s.Servers.ToList();
            tool = Toolchain.HostDll(_s.Settings.Tools);
        }
        var hosts = await Task.Run(() => list.Select(s => GraftDoctor.Probe(s.Path, tool)).ToList());
        var changed = false;
        lock (_gate)
            for (var i = 0; i < list.Count; i++)
            {
                if (list[i].Host == hosts[i]) continue;
                list[i] = list[i] with { Host = hosts[i] };
                var j = _s.Servers.FindIndex(s => s.Id == list[i].Id);
                if (j >= 0) _s.Servers[j] = list[i];
                changed = true;
            }
        if (changed) Save();
        return list;
    }

    public Task<ServerInfo?> AddServerAsync(string path)
    {
        if (!Directory.Exists(path)) throw new InvalidOperationException("папка сервера не найдена");
        if (path.Length > 3) path = path.TrimEnd('\\', '/');
        lock (_gate)
        {
            var existing = _s.Servers.FirstOrDefault(s => string.Equals(s.Path.TrimEnd('\\', '/'), path, StringComparison.OrdinalIgnoreCase));
            if (existing != null) return Task.FromResult<ServerInfo?>(existing);
        }
        var srv = new ServerInfo("s" + Guid.NewGuid().ToString("N")[..8], Path.GetFileName(path),
            path, ReadServerBuild(path), ReadVerifySignatures(path), GraftDoctor.Probe(path, Toolchain.HostDll(_s.Settings.Tools)));
        lock (_gate) _s.Servers.Add(srv);
        Save();
        return Task.FromResult<ServerInfo?>(srv);
    }

    public Task<ServerInfo> InstallHostAsync(string serverId)
    {
        ServerInfo srv;
        lock (_gate) srv = _s.Servers.FirstOrDefault(s => s.Id == serverId) ?? throw new InvalidOperationException("сервер не найден");
        var host = Toolchain.HostDll(_s.Settings.Tools) ?? throw new InvalidOperationException("hid.dll не найден");
        var target = Path.Combine(srv.Path, "hid.dll");
        if (GraftDoctor.Probe(srv.Path, host).State == "foreign")
            throw new InvalidOperationException("в папке сервера лежит чужая hid.dll (не GRAFT) — тулза её не затирает");
        if (File.Exists(target) && ServerLaunch.IsRunning(srv.Path))
            throw new InvalidOperationException("сервер запущен — остановите его, потом меняйте хост");
        File.Copy(host, target, true);
        return Task.FromResult(SetHost(serverId, GraftDoctor.Probe(srv.Path, host)));
    }

    public Task<ServerInfo> UninstallHostAsync(string serverId)
    {
        ServerInfo srv;
        lock (_gate) srv = _s.Servers.FirstOrDefault(s => s.Id == serverId) ?? throw new InvalidOperationException("сервер не найден");
        GraftDoctor.Uninstall(srv.Path);
        return Task.FromResult(SetHost(serverId, GraftDoctor.Probe(srv.Path, Toolchain.HostDll(_s.Settings.Tools))));
    }

    public Task<string> InstallPluginAsync(string serverId, string dll)
    {
        ServerInfo srv;
        lock (_gate) srv = _s.Servers.FirstOrDefault(s => s.Id == serverId) ?? throw new InvalidOperationException("сервер не найден");
        return Task.Run(() => GraftDoctor.InstallPlugin(srv.Path, dll));
    }

    public async Task<GraftReport> CheckGraftAsync(string serverId, string? bat)
    {
        ServerInfo srv;
        string? host, graft;
        lock (_gate)
        {
            srv = _s.Servers.FirstOrDefault(s => s.Id == serverId) ?? throw new InvalidOperationException("сервер не найден");
            host = Toolchain.HostDll(_s.Settings.Tools);
            graft = Toolchain.Graft(_s.Settings.Tools);
        }
        var items = await Task.Run(() => GraftDoctor.Run(srv, bat, host, graft));
        return new GraftReport(SetHost(serverId, GraftDoctor.Probe(srv.Path, host)), items, DateTime.Now);
    }

    private ServerInfo SetHost(string serverId, HostInfo host)
    {
        ServerInfo srv;
        lock (_gate)
        {
            var i = _s.Servers.FindIndex(s => s.Id == serverId);
            srv = _s.Servers[i] = _s.Servers[i] with { Host = host };
        }
        Save();
        return srv;
    }

    // Runs the server's own start script the way a double click in Explorer would: its own console
    // window, the server folder as the working directory. The chosen script is remembered.
    public Task<ServerLaunched> LaunchServerAsync(string serverId, string? bat)
    {
        ServerInfo srv;
        lock (_gate) srv = _s.Servers.FirstOrDefault(s => s.Id == serverId) ?? throw new InvalidOperationException("сервер не найден");
        var name = ServerLaunch.Pick(srv, bat) ?? throw new InvalidOperationException("в папке сервера нет .bat для запуска: " + srv.Path);
        var pid = ServerLaunch.Start(srv.Path, name);
        if (!string.Equals(srv.StartBat, name, StringComparison.OrdinalIgnoreCase))
        {
            lock (_gate)
            {
                var i = _s.Servers.FindIndex(s => s.Id == serverId);
                if (i >= 0) srv = _s.Servers[i] = _s.Servers[i] with { StartBat = name };
            }
            Save();
        }
        return Task.FromResult(new ServerLaunched(srv, pid));
    }

    public async Task<ServerStopped> StopServerAsync(string serverId, int scriptPid)
    {
        ServerInfo srv;
        lock (_gate) srv = _s.Servers.FirstOrDefault(s => s.Id == serverId) ?? throw new InvalidOperationException("сервер не найден");
        var (scripts, servers, forced) = await Task.Run(() => ServerLaunch.StopAsync(srv.Path, scriptPid, TimeSpan.FromSeconds(60)));
        return new ServerStopped(srv, scripts, servers, forced);
    }

    public Task<ModsSetup> GetServerModsAsync(string serverId, string bat)
    {
        ServerInfo srv;
        lock (_gate) srv = _s.Servers.FirstOrDefault(s => s.Id == serverId) ?? throw new InvalidOperationException("сервер не найден");
        var dir = _s.Settings.WorkshopDir;
        return Task.Run(() => ServerMods.Setup(srv.Path, bat, dir));
    }

    public Task<ModsSaved> SaveServerModsAsync(string serverId, string bat, IReadOnlyList<ModPick> mods, IReadOnlyList<ModPick> serverMods)
    {
        ServerInfo srv;
        lock (_gate) srv = _s.Servers.FirstOrDefault(s => s.Id == serverId) ?? throw new InvalidOperationException("сервер не найден");
        return Task.Run(() => ServerMods.Save(srv.Path, bat, mods, serverMods));
    }

    // ── keys ──────────────────────────────────────────────────────────────────
    public Task<IReadOnlyList<KeyInfo>> ListKeysAsync()
    {
        var dir = _s.Settings.Tools.KeyDir;
        var list = new List<KeyInfo>();
        if (Directory.Exists(dir))
            foreach (var f in Directory.GetFiles(dir, "*.bikey"))
                list.Add(new(Path.GetFileNameWithoutExtension(f), Fingerprint(f),
                    File.GetCreationTime(f).ToString("yyyy-MM-dd"), f));
        return Task.FromResult<IReadOnlyList<KeyInfo>>(list);
    }

    public async Task<KeyInfo> CreateKeyAsync(string tag)
    {
        var dir = _s.Settings.Tools.KeyDir;
        Directory.CreateDirectory(dir);
        var bikey = Path.Combine(dir, tag + ".bikey");
        // A new pair under an existing tag would orphan every PBO already signed with it.
        if (File.Exists(bikey) || File.Exists(Path.Combine(dir, tag + ".biprivatekey")))
            throw new InvalidOperationException("ключ " + tag + " уже есть: " + dir);
        var create = Toolchain.DsCreateKey ?? throw new InvalidOperationException("DSCreateKey не найден (DayZ Tools)");
        var res = await ProcRunner.RunAsync(create, new[] { tag }, dir, (_, _) => { }, CancellationToken.None);
        if (res.ExitCode != 0 || !File.Exists(bikey)) throw new InvalidOperationException("не удалось создать ключ");
        return new(tag, Fingerprint(bikey), DateTime.Now.ToString("yyyy-MM-dd"), bikey);
    }

    public Task<string> ExportKeyAsync(string tag)
    {
        var p = Path.Combine(_s.Settings.Tools.KeyDir, tag + ".bikey");
        if (!File.Exists(p)) throw new InvalidOperationException("ключ не найден");
        return Task.FromResult(p);
    }

    // ── misc ──────────────────────────────────────────────────────────────────
    public Task<IReadOnlyList<DoctorItem>> DoctorAsync() => ToolDetect.DetectAsync(_s.Settings.Tools, _s.Settings.Pack.Rapify)
        .ContinueWith(t => (IReadOnlyList<DoctorItem>)t.Result);

    public Task<AppSettings> GetSettingsAsync() => Task.FromResult(_s.Settings);

    public Task SaveSettingsAsync(AppSettings settings)
    {
        lock (_gate) _s.Settings = settings;
        Save();
        return Task.CompletedTask;
    }

    public Task OpenPathAsync(string path)
    {
        try { if (Directory.Exists(path) || File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception) { }
        return Task.CompletedTask;
    }

    // ── pipeline ────────────────────────────────────────────────────────────────
    public Task<string> RunPipelineAsync(string modId, PipelineOptions options)
    {
        var runId = "r" + DateTime.Now.Ticks.ToString("x");
        var cts = new CancellationTokenSource();
        _runs[runId] = cts;
        // Off the dispatcher: staging a big mod copies the whole tree and must not freeze the window.
        _work[runId] = Task.Run(() => RunAsync(runId, modId, options, cts.Token));
        return Task.FromResult(runId);
    }

    // Done fires before the temp folder is cleaned up; a headless caller waits for that before exiting.
    public Task DrainAsync() => Task.WhenAll(_work.Values);

    public Task CancelAsync(string runId)
    {
        if (_runs.TryGetValue(runId, out var cts) && !cts.IsCancellationRequested)
        {
            cts.Cancel();
            Cancelled?.Invoke(new PipelineCancelled(runId));
        }
        return Task.CompletedTask;
    }

    // host belongs to the Grafted method; build and protogen also need a C++ project to build. A Grafted
    // server mod without one is only laid out the GRAFT way (its own @folder, -serverMod=).
    public static bool StepApplies(string step, string kind, bool project) => step switch
    {
        "build" or "protogen" => kind == ModKinds.Grafted && project,
        "host" => kind == ModKinds.Grafted,
        _ => true,
    };

    private async Task RunAsync(string runId, string modId, PipelineOptions o, CancellationToken ct)
    {
        ModInfo? mod;
        ServerInfo? srv;
        List<ServerInfo> servers;
        lock (_gate)
        {
            mod = _s.Mods.FirstOrDefault(m => m.Id == modId);
            srv = _s.Servers.FirstOrDefault(s => s.Id == o.Server);
            servers = _s.Servers.ToList();
        }
        var total = Stopwatch.StartNew();
        var step = "env";
        RunCtx? ctx = null;
        try
        {
            if (mod == null) throw new InvalidOperationException("мод не найден");
            Started?.Invoke(new PipelineStarted(runId, Steps.Ids));

            ctx = new RunCtx(runId, mod, o, srv, _s.Settings);
            var fromIdx = o.FromStep == null ? 0 : Array.IndexOf(Steps.Ids, o.FromStep);

            for (var i = 0; i < Steps.Ids.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                var id = Steps.Ids[i];
                var off = !StepApplies(id, mod.Kind, ctx.Project)
                          || (id == "sign" && !o.Sign) || (id == "deploy" && !o.Deploy) || (id == "host" && !o.Host)
                          || (o.Only != null && !o.Only.Contains(id)) || (i < fromIdx && id != "env");
                if (off) { StepChanged?.Invoke(new PipelineStep(runId, id, StepStatus.Skipped, 0)); continue; }

                step = id;
                StepChanged?.Invoke(new PipelineStep(runId, id, StepStatus.Running, 0));
                var sw = Stopwatch.StartNew();
                PipelineError? err;
                try { err = await Step(id, ctx, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { err = Err(ctx, id, e.Message); }
                if (err != null)
                {
                    StepChanged?.Invoke(new PipelineStep(runId, id, StepStatus.Error, sw.ElapsedMilliseconds));
                    Failed?.Invoke(err);
                    UpdateMod(modId, m => m with { Build = new("fail", DateTime.Now, 0, id) });
                    return;
                }
                StepChanged?.Invoke(new PipelineStep(runId, id, StepStatus.Done, sw.ElapsedMilliseconds));
            }

            var now = DateTime.Now;
            var ms = total.ElapsedMilliseconds;
            UpdateMod(modId, m =>
            {
                if (o.Only == null)
                    m = m with { Build = new("ok", now, ms), Sign = o.Sign ? new("ok", m.Key) : new("none") };
                // A mod counts as deployed on every registered server its files ended up in.
                var list = m.Deploy.State == "none" ? new List<string>() : m.Deploy.Servers.ToList();
                var before = list.Count;
                foreach (var s in servers)
                    if (!list.Contains(s.Id) && (PackLayout.IsUnder(ctx.Root, s.Path) || (ctx.DeployedDir != null && s.Id == srv?.Id)))
                        list.Add(s.Id);
                if (list.Count > before || (ctx.DeployedDir != null && list.Count > 0))
                    m = m with { Deploy = new("ok", list, now) };
                return m;
            });

            // Files already inside a registered server count too, even with deploy off.
            if (ctx.Srv == null || !PackLayout.IsUnder(ctx.Root, ctx.Srv.Path))
                foreach (var s in servers.Where(s => PackLayout.IsUnder(ctx.Root, s.Path)))
                    FindDuplicates(ctx, s.Path);

            var (modLine, serverDir) = Launch(ctx, servers);
            long bytes = File.Exists(ctx.Pbo) ? new FileInfo(ctx.Pbo).Length : 0;
            var cmd = ("DayZServer_x64.exe -config=serverDZ.cfg -port=2302 -profiles=profiles " + modLine).TrimEnd();
            Done?.Invoke(new PipelineDone(runId, modLine, cmd, bytes, ctx.Root, serverDir ?? "", ms, ctx.Duplicates.Distinct().ToList()));
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Failed?.Invoke(new PipelineError(runId, step, e.Message, null, Array.Empty<LogEntry>(), null, null));
        }
        finally
        {
            _runs.TryRemove(runId, out _);
            // binarize workers and the virus scanner can hold the fresh files for a moment after exit.
            for (var i = 0; ctx != null && Directory.Exists(ctx.Tmp) && i < 10; i++)
            {
                if (i > 0) await Task.Delay(500);
                DropTmp(ctx.Tmp);
            }
            _work.TryRemove(runId, out _);
        }
    }

    private sealed class RunCtx
    {
        public RunCtx(string runId, ModInfo mod, PipelineOptions o, ServerInfo? srv, AppSettings s)
        {
            RunId = runId; Mod = mod; O = o; Srv = srv; S = s;
            Src = PackLayout.SourceDir(mod);
            Root = PackLayout.OutRoot(mod, s.Pack);
            Project = PackLayout.HasProject(mod);
            Proj = PackLayout.ProjectDir(mod);
            Tmp = Path.Combine(Path.GetTempPath(), "pbog_" + runId);
        }
        public string RunId { get; }
        public ModInfo Mod { get; }
        public PipelineOptions O { get; }
        public ServerInfo? Srv { get; }
        public AppSettings S { get; }
        public string Src { get; }
        public string Root { get; }
        public string Tmp { get; }
        public bool Grafted => Mod.Kind == ModKinds.Grafted;
        public bool Project { get; }
        public string Proj { get; }
        public bool Plugin => Grafted && Project;
        public string AddonsDir => Path.Combine(Root, "addons");
        public string Pbo => Path.Combine(AddonsDir, Mod.Name + ".pbo");
        public string DllName => Mod.Name + ".grafted.dll";
        public string BiKey => Path.Combine(S.Tools.KeyDir, Mod.Key + ".bikey");
        public string? Dll { get; set; }
        public string? DeployedDir { get; set; }
        public List<string> Duplicates { get; } = new();
    }

    private void L(RunCtx c, string step, string line, LogLevel lvl) => Log?.Invoke(new PipelineLog(c.RunId, step, line, lvl));

    private static PipelineError Err(RunCtx c, string step, string msg, string? where = null,
        IEnumerable<string>? tail = null, string? hint = null, string? fix = null) =>
        new(c.RunId, step, msg, where, (tail ?? Array.Empty<string>()).Select(l => new LogEntry(LogLevel.Err, l)).ToList(), hint, fix);

    private async Task<PipelineError?> Step(string id, RunCtx c, CancellationToken ct) => id switch
    {
        "env" => Env(c),
        "build" => await Build(c, ct),
        "protogen" => Protogen(c),
        "pack" => await Pack(c, ct),
        "sign" => await Sign(c, ct),
        "assemble" => Assemble(c),
        "deploy" => Deploy(c),
        "host" => Host(c),
        _ => null,
    };

    private PipelineError? Env(RunCtx c)
    {
        var t = c.S.Tools;
        var need = new List<(string tool, string? path, string fix, string? cmd)>();
        if (c.Plugin)
        {
            need.Add(("cmake", Toolchain.CMake(t), "Fix.CMake", null));
            need.Add(("clang-cl", Toolchain.ClangDir, "Fix.Clang", "winget install LLVM.LLVM"));
            need.Add(("vcvars64.bat (Visual Studio)", Toolchain.VcVars(), "Fix.Vs", null));
        }
        need.Add(("AddonBuilder (DayZ Tools)", Toolchain.AddonBuilder, "Fix.Pbo", null));
        if (c.S.Pack.Rapify) need.Add(("CfgConvert (DayZ Tools)", Toolchain.CfgConvert, "Fix.Pbo", null));
        if (c.O.Sign) need.Add(("DSSignFile (DayZ Tools)", Toolchain.DsSignFile, "Fix.Pbo", null));
        foreach (var (tool, path, fix, cmd) in need)
        {
            L(c, "env", (path != null ? "ok  " : "нет ") + tool + (path != null ? "  " + path : ""), path != null ? LogLevel.Ok : LogLevel.Err);
            if (path == null) return Err(c, "env", "не найден: " + tool, null, null, fix, cmd);
        }

        if (c.Grafted && !c.Project)
            L(c, "env", "метод Grafted без C++-проекта: мод раскладывается по README GRAFT, плагин не собирается", LogLevel.Info);
        if (c.Plugin)
        {
            var tag = GraftTag(c.Proj);
            if (tag == null)
                L(c, "env", "в CMakeLists.txt не найден graft_import(... TAG ...) — совместимость с хостом покажет проверка после сборки", LogLevel.Warn);
            else if (tag != GraftDoctor.Release)
                L(c, "env", $"CMakeLists.txt: graft_import TAG {tag}, а хост тулзы — {GraftDoctor.Release}. Примет ли хост плагин (ABI), покажет проверка после сборки; чтобы получить правки релиза, закрепите TAG {GraftDoctor.Release}", LogLevel.Warn);
            else L(c, "env", "graft_import TAG " + tag + " — та же версия, что у хоста", LogLevel.Ok);
        }
        if (!File.Exists(Path.Combine(c.Src, "config.cpp")))
            return Err(c, "env", "нет config.cpp в папке мода", c.Src);
        var configured = PackLayout.Configured(c.Mod, c.S.Pack);
        if (!string.IsNullOrWhiteSpace(configured) && !PackLayout.IsRooted(configured))
            return Err(c, "env", "папка вывода задана не полным путём: " + configured, null, null, "Fix.OutRelative");
        if (PackLayout.IsUnder(c.Root, c.Src))
            return Err(c, "env", "папка вывода лежит внутри исходников мода: PBO упаковал бы сам себя", c.Root, null, "Fix.OutInside");
        List<ModInfo> mods;
        lock (_gate) mods = _s.Mods.ToList();
        if (PackLayout.SourcesOf(c.Root, mods) is { } owner)
            return Err(c, "env", "папка вывода — исходники мода " + owner + ": PBO легли бы в чужой мод", c.Root, null, "Fix.OutInside");

        var (prefix, from) = PackLayout.Prefix(c.Mod);
        L(c, "env", "исходники: " + c.Src, LogLevel.Info);
        if (!string.IsNullOrWhiteSpace(configured) && !string.Equals(PackLayout.NormalizeRoot(configured), c.Root, StringComparison.OrdinalIgnoreCase))
            L(c, "env", "это папка игры — мод получит свою @папку внутри неё, как в README GRAFT", LogLevel.Info);
        L(c, "env", "куда: " + c.Pbo, LogLevel.Info);
        L(c, "env", "префикс PBO: " + prefix + " (" + from + ")", LogLevel.Info);
        return null;
    }

    private async Task<PipelineError?> Build(RunCtx c, CancellationToken ct)
    {
        var cmake = Toolchain.CMake(c.S.Tools)!;
        var vcvars = Toolchain.VcVars();
        var ninjaDir = Toolchain.NinjaDir(c.S.Tools);
        var clangDir = Toolchain.ClangDir;
        // A temp bat sets MSVC env (vcvars) + clang-cl + ninja on PATH, then runs the preset build.
        // chcp 65001: cmd and vcvars otherwise print in the OEM code page, which reads as garbage.
        var bat = Path.Combine(Path.GetTempPath(), "pbogbuild_" + c.RunId + ".bat");
        var extraPath = string.Join(";", new[] { clangDir, ninjaDir }.Where(p => p != null));
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("chcp 65001 >nul");
        sb.AppendLine("set \"PATH=%ProgramFiles(x86)%\\Microsoft Visual Studio\\Installer;%PATH%\"");
        if (vcvars != null) sb.AppendLine($"call \"{vcvars}\" >nul || exit /b 1");
        sb.AppendLine($"set \"PATH={extraPath};%PATH%\"");
        sb.AppendLine("set \"CC=clang-cl\"");
        sb.AppendLine("set \"CXX=clang-cl\"");
        sb.AppendLine($"cd /d \"{c.Proj}\"");
        sb.AppendLine($"\"{cmake}\" --preset {c.O.Preset} || exit /b 1");
        sb.AppendLine($"\"{cmake}\" --build --preset {c.O.Preset} || exit /b 1");
        await File.WriteAllTextAsync(bat, sb.ToString(), ct);
        try
        {
            L(c, "build", "cmake --preset " + c.O.Preset + " (+ build)", LogLevel.Cmd);
            var r = await ProcRunner.RunAsync("cmd.exe", new[] { "/c", bat }, c.Proj,
                (line, err) => L(c, "build", line, err ? LogLevel.Warn : LogLevel.Info), ct);
            if (r.ExitCode != 0)
                return Err(c, "build", "сборка не удалась (код " + r.ExitCode + ")", null, r.Tail, "Fix.Build");
        }
        finally { try { File.Delete(bat); } catch (Exception) { } }

        c.Dll = FindDll(c);
        if (c.Dll == null)
            return Err(c, "build", "не найдена собранная DLL: " + c.DllName);
        L(c, "build", "DLL: " + c.Dll, LogLevel.Ok);
        return await CheckAbi(c, ct);
    }

    // graft.exe of the tool is built from the same ABI as its hid.dll: "graft list" opens the plugin the
    // way the host will and names the reason it would be turned away (ABI/layout, not x64, no entry).
    private async Task<PipelineError?> CheckAbi(RunCtx c, CancellationToken ct)
    {
        var graft = Toolchain.Graft(c.S.Tools);
        if (graft == null)
        {
            L(c, "build", "graft.exe не найден — совместимость плагина с хостом не проверена", LogLevel.Warn);
            return null;
        }
        var game = Path.Combine(c.Tmp, "abi");
        try
        {
            Directory.CreateDirectory(Path.Combine(game, "grafted"));
            File.Copy(c.Dll!, Path.Combine(game, "grafted", c.DllName), true);
            var lines = new List<string>();
            var r = await ProcRunner.RunAsync(graft, new[] { "list", game }, null, (line, _) => lines.Add(line), ct);
            var bad = lines.FirstOrDefault(l => l.Contains(c.DllName) && l.Contains("<-"));
            if (bad != null)
            {
                var why = bad[(bad.IndexOf("<-", StringComparison.Ordinal) + 2)..].Trim();
                return Err(c, "build", $"хост GRAFT {GraftDoctor.Release} не примет плагин: {why}", c.Dll, null, "Fix.GraftAbi");
            }
            var ok = lines.FirstOrDefault(l => l.Contains(c.DllName));
            L(c, "build", ok != null ? "хост " + GraftDoctor.Release + " примет плагин: " + ok.Trim() : "graft list не ответил про плагин (код " + r.ExitCode + ")",
                ok != null ? LogLevel.Ok : LogLevel.Warn);
            return null;
        }
        finally { try { Directory.Delete(game, true); } catch (Exception) { } }
    }

    private static string? GraftTag(string project)
    {
        try
        {
            var m = Regex.Match(File.ReadAllText(Path.Combine(project, "CMakeLists.txt")), @"graft_import\s*\([^)]*?\bTAG\s+""?([^\s"")]+)", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }
        catch (Exception) { return null; }
    }

    private PipelineError? Protogen(RunCtx c)
    {
        // graft_plugin runs protogen during the build. With SCRIPTS_DIR the declarations land in the mod;
        // without it (the README example) in <Name>.scripts next to the DLL — they go into the mod here,
        // where the PBO is packed from, as the README says to copy them.
        SyncDeclarations(c);
        var decls = Directory.Exists(c.Src)
            ? Directory.GetFiles(c.Src, "grafted_natives_" + c.Mod.Name + ".c", SearchOption.AllDirectories)
            : Array.Empty<string>();
        if (decls.Length == 0)
            return Err(c, "protogen", "нет сгенерированных объявлений ни в mod\\...\\scripts, ни рядом с DLL (" + c.Mod.Name + ".scripts)", c.Src, null, "Fix.Decls");
        foreach (var d in decls) L(c, "protogen", "объявления: " + d.Substring(c.Src.Length).TrimStart('\\'), LogLevel.Ok);
        return null;
    }

    private void SyncDeclarations(RunCtx c)
    {
        var built = BuiltScripts(c);
        if (built == null) return;
        var scripts = Directory.GetDirectories(c.Src).FirstOrDefault(d => Path.GetFileName(d).Equals("scripts", StringComparison.OrdinalIgnoreCase))
                      ?? Path.Combine(c.Src, "scripts");
        foreach (var f in Directory.GetFiles(built, "grafted_natives_*.c", SearchOption.AllDirectories))
        {
            var dst = Path.Combine(scripts, Path.GetRelativePath(built, f));
            if (File.Exists(dst) && File.ReadAllBytes(dst).AsSpan().SequenceEqual(File.ReadAllBytes(f))) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(f, dst, true);
            L(c, "protogen", "объявления из сборки -> " + Path.GetRelativePath(c.Src, dst), LogLevel.Info);
        }
    }

    // <Name>.scripts written by the build of this preset (out\<preset> or build\<preset>), the newest
    // anywhere in the project otherwise; never inside the mod or the output root.
    private static string? BuiltScripts(RunCtx c)
    {
        try
        {
            var all = new DirectoryInfo(c.Proj).GetDirectories(c.Mod.Name + ".scripts", SearchOption.AllDirectories)
                .Where(d => !PackLayout.IsUnder(d.FullName, c.Src) && !PackLayout.IsUnder(d.FullName, c.Root))
                .OrderByDescending(d => d.LastWriteTimeUtc).ToList();
            var preset = all.FirstOrDefault(d => PackLayout.IsUnder(d.FullName, Path.Combine(c.Proj, "out", c.O.Preset))
                                                 || PackLayout.IsUnder(d.FullName, Path.Combine(c.Proj, "build", c.O.Preset)));
            return (preset ?? all.FirstOrDefault())?.FullName;
        }
        catch (Exception) { return null; }
    }

    // pboProject-style pack. The source is copied without junk into an isolated project folder at the
    // path its prefix names, so the source stays untouched and binarize.exe never scans the whole P:\
    // (one broken foreign config there fails every build). With binarization on, AddonBuilder does what
    // pboProject does: config.cpp -> config.bin, MLOD p3d -> ODOL; rvmat are rapified here, everything
    // else is copied as is via -include, and ODOL models (they crash binarize) are appended afterwards.
    // With it off the folder is packed as is (-packonly). The finished PBO is checked against the stage
    // and only then replaces <root>\addons\<Name>.pbo.
    private async Task<PipelineError?> Pack(RunCtx c, CancellationToken ct)
    {
        var ab = Toolchain.AddonBuilder;
        if (ab == null) return Err(c, "pack", "AddonBuilder не найден (DayZ Tools)", null, null, "Fix.Pbo");
        var (prefix, _) = PackLayout.Prefix(c.Mod);
        var segs = prefix.Split('\\');
        if (segs.Any(s => s is "." or ".." || s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            return Err(c, "pack", "недопустимый префикс PBO: " + prefix, c.Src);
        var proj = Path.Combine(c.Tmp, "proj");
        var stage = Path.Combine(proj, prefix);
        var abOut = Path.Combine(c.Tmp, "out");
        var abTemp = Path.Combine(c.Tmp, "ab");
        DropTmp(c.Tmp);
        Directory.CreateDirectory(abOut);
        Directory.CreateDirectory(abTemp);

        var skipped = new List<string>();
        var (files, size) = await Task.Run(() => StageCopy(c.Src, stage, skipped), ct);
        L(c, "pack", $"исходники: {files} файлов, {size / 1024.0:0.#} КБ", LogLevel.Info);
        foreach (var s in skipped.Take(8)) L(c, "pack", "не пакуется: " + s, LogLevel.Info);
        if (skipped.Count > 8) L(c, "pack", "…и ещё " + (skipped.Count - 8), LogLevel.Info);

        var args = new List<string> { stage, abOut, "-clear", "-prefix=" + prefix, "-temp=" + abTemp };
        var odol = new List<(string Name, string File)>();
        var full = c.S.Pack.Rapify;
        if (full)
        {
            var err = await CheckConfigs(c, proj, stage, ct) ?? await RapifyMaterials(c, stage, ct);
            if (err != null) return err;
            var models = Directory.GetFiles(stage, "*.p3d", SearchOption.AllDirectories);
            var mlod = models.Where(IsMlod).ToList();
            // binarize dies on models that are already ODOL; pboProject ships those as they are.
            var aside = Path.Combine(c.Tmp, "odol");
            foreach (var p in models.Where(IsOdol))
            {
                var rel = Path.GetRelativePath(stage, p);
                var keep = Path.Combine(aside, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(keep)!);
                File.Move(p, keep);
                odol.Add((rel, keep));
            }
            if (odol.Count > 0) L(c, "pack", $"модели ODOL: {odol.Count} — уже бинаризованы, кладутся как есть", LogLevel.Info);
            if (mlod.Count > 0)
            {
                L(c, "pack", $"модели MLOD: {mlod.Count} — бинаризуются в ODOL (binarize.exe)", LogLevel.Info);
                await LinkReferences(c, proj, segs[0], mlod, ct);
            }
            var include = Path.Combine(c.Tmp, "include.lst");
            await File.WriteAllTextAsync(include, string.Join(";", DirectCopyPatterns(stage)), ct);
            args.Add("-project=" + proj);
            args.Add("-include=" + include);
            L(c, "pack", "AddonBuilder -prefix=" + prefix + " (бинаризация)", LogLevel.Cmd);
        }
        else
        {
            args.Add("-packonly");
            L(c, "pack", "AddonBuilder -packonly -prefix=" + prefix, LogLevel.Cmd);
        }

        var errors = new List<string>();
        var r = await ProcRunner.RunAsync(ab, args, null, (line, isErr) => AddonBuilderLine(c, line, isErr, errors), ct);
        // AddonBuilder names the PBO after the staged folder and answers 0 even on "Build failed":
        // only the PBO on disk counts.
        var built = Path.Combine(abOut, segs[^1] + ".pbo");
        if (r.ExitCode != 0 || !File.Exists(built))
            return Err(c, "pack", "AddonBuilder не собрал PBO", null, errors.Count > 0 ? errors.TakeLast(12) : r.Tail, "Fix.AddonBuilder");
        if (odol.Count > 0) await Task.Run(() => Pbo.AddFiles(built, odol), ct);

        var missing = Missing(stage, built, full, odol.Select(o => o.Name));
        if (missing.Count > 0)
            return Err(c, "pack", $"в PBO не попали файлы мода: {missing.Count}", c.Src, missing.Take(12), "Fix.AddonBuilder");

        Directory.CreateDirectory(c.AddonsDir);
        var err2 = Replace(c, "pack", built, c.Pbo);
        if (err2 != null) return err2;
        // The old signatures describe the old PBO; leaving them would make the server reject the mod.
        RemoveSignatures(c.AddonsDir, c.Mod.Name);
        L(c, "pack", "PBO: " + c.Pbo + " (" + new FileInfo(c.Pbo).Length + " б)", LogLevel.Ok);
        return null;
    }

    // Copy under a temporary name, then swap: a locked target (running server or game) leaves the old
    // PBO and its signatures exactly as they were.
    private static PipelineError? Replace(RunCtx c, string step, string from, string to)
    {
        var tmp = to + ".pbogtmp";
        try
        {
            File.Copy(from, tmp, true);
            File.Move(tmp, to, true);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(tmp); } catch (Exception) { }
            return Err(c, step, "не удалось записать " + Path.GetFileName(to) + ": " + e.Message, to, null, "Fix.Locked");
        }
    }

    // Every staged file must reach the PBO: AddonBuilder drops what binarize did not produce and
    // -include did not match, without a word. config.cpp ships as config.bin; model.cfg and .hpp only
    // feed the conversion.
    private static List<string> Missing(string stage, string pbo, bool full, IEnumerable<string> added)
    {
        var have = new HashSet<string>(Pbo.Names(pbo).Select(n => n.Replace('/', '\\')), StringComparer.OrdinalIgnoreCase);
        foreach (var a in added) have.Add(a);
        var missing = new List<string>();
        foreach (var f in Directory.GetFiles(stage, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(stage, f);
            var name = Path.GetFileName(f);
            if (full)
            {
                if (name.Equals("model.cfg", StringComparison.OrdinalIgnoreCase)) continue;
                var ext = Path.GetExtension(f);
                if (ext.Equals(".hpp", StringComparison.OrdinalIgnoreCase)) continue;
                if (ext.Equals(".cpp", StringComparison.OrdinalIgnoreCase))
                {
                    if (!name.Equals("config.cpp", StringComparison.OrdinalIgnoreCase)) continue;
                    rel = Path.ChangeExtension(rel, ".bin");
                }
            }
            if (!have.Contains(rel)) missing.Add(rel);
        }
        return missing;
    }

    // Every config.cpp is run through CfgConvert first: a syntax error then points at the source file
    // instead of drowning in AddonBuilder output. AddonBuilder itself does the conversion that ships.
    // It runs from the project folder, where absolute "\prefix\..." includes resolve.
    private async Task<PipelineError?> CheckConfigs(RunCtx c, string proj, string stage, CancellationToken ct)
    {
        var cc = Toolchain.CfgConvert;
        if (cc == null) return Err(c, "pack", "CfgConvert не найден (DayZ Tools)", null, null, "Fix.Pbo");
        var configs = Directory.GetFiles(stage, "config.cpp", SearchOption.AllDirectories);
        var probe = Path.Combine(c.Tmp, "cfgcheck.bin");
        foreach (var cpp in configs)
        {
            var rel = Path.GetRelativePath(stage, cpp);
            L(c, "pack", "CfgConvert -bin " + rel, LogLevel.Cmd);
            var r = await ProcRunner.RunAsync(cc, new[] { "-bin", "-dst", probe, cpp }, proj,
                (line, _) => L(c, "pack", ToSource(c, stage, line), LogLevel.Warn), ct);
            if (r.ExitCode != 0 || !File.Exists(probe))
                return Err(c, "pack", "config.cpp не бинаризуется: " + rel, Path.Combine(c.Src, rel),
                    r.Tail.Select(l => ToSource(c, stage, l)), "Fix.Rapify");
            File.Delete(probe);
        }
        L(c, "pack", "конфиги в порядке: " + configs.Length, LogLevel.Ok);
        return null;
    }

    // pboProject ships every rvmat rapified; binarize only touches the ones a model references.
    private async Task<PipelineError?> RapifyMaterials(RunCtx c, string stage, CancellationToken ct)
    {
        var cc = Toolchain.CfgConvert!;
        var done = 0;
        foreach (var mat in Directory.GetFiles(stage, "*.rvmat", SearchOption.AllDirectories))
        {
            if (IsRapified(mat)) continue;
            var rel = Path.GetRelativePath(stage, mat);
            var r = await ProcRunner.RunAsync(cc, new[] { "-bin", "-dst", mat, mat }, Path.GetDirectoryName(mat),
                (line, _) => L(c, "pack", ToSource(c, stage, line), LogLevel.Warn), ct);
            if (r.ExitCode != 0)
                return Err(c, "pack", "rvmat не бинаризуется: " + rel, Path.Combine(c.Src, rel),
                    r.Tail.Select(l => ToSource(c, stage, l)), "Fix.Rapify");
            done++;
        }
        if (done > 0) L(c, "pack", "rvmat бинаризованы: " + done, LogLevel.Ok);
        return null;
    }

    // Tool messages name the staged copy, which is gone by the time anyone reads them.
    private static string ToSource(RunCtx c, string stage, string line) =>
        line.Replace(stage, c.Src, StringComparison.OrdinalIgnoreCase);

    // binarize only sees the isolated project: link the game data (DZ) and every top-level P:\ folder
    // the models and materials point into, so their textures and rvmat resolve as under pboProject.
    private async Task LinkReferences(RunCtx c, string proj, string own, IEnumerable<string> mlod, CancellationToken ct)
    {
        var work = PackLayout.WorkRoot(c.Src);
        var tops = new SortedSet<string>(StringComparer.OrdinalIgnoreCase) { "DZ" };
        if (work != null)
        {
            var files = mlod.Concat(Directory.GetFiles(Path.Combine(proj, own), "*.rvmat", SearchOption.AllDirectories));
            foreach (var f in files)
                foreach (Match m in RefPath.Matches(Encoding.Latin1.GetString(File.ReadAllBytes(f))))
                    tops.Add(m.Groups[1].Value);
        }
        foreach (var top in tops)
        {
            if (top.Equals(own, StringComparison.OrdinalIgnoreCase)) continue;
            var target = top.Equals("DZ", StringComparison.OrdinalIgnoreCase) ? FindGameData(c.Src)
                : work != null ? Path.Combine(work, top) : null;
            if (target == null || !Directory.Exists(target))
            {
                if (top == "DZ") L(c, "pack", "папка DZ не найдена — ссылки моделей на данные игры не проверятся", LogLevel.Warn);
                continue;
            }
            var link = Path.Combine(proj, top);
            var mk = await ProcRunner.RunAsync("cmd.exe", new[] { "/c", "mklink", "/J", link, target }, proj, (_, _) => { }, ct);
            L(c, "pack", mk.ExitCode == 0 ? "для binarize подключено: " + target : "не удалось подключить " + target,
                mk.ExitCode == 0 ? LogLevel.Info : LogLevel.Warn);
        }
    }

    // The extracted game data (DZ) binarize resolves model references against: P:\DZ or a DZ folder
    // next to the mod (P:\ is usually a subst of such a folder).
    private static string? FindGameData(string src)
    {
        for (var d = Path.GetDirectoryName(src); !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d))
            if (File.Exists(Path.Combine(d, "DZ", "data", "config.cpp"))) return Path.Combine(d, "DZ");
        return File.Exists(@"P:\DZ\data\config.cpp") ? @"P:\DZ" : null;
    }

    private static readonly Regex RefPath =new(@"([A-Za-z0-9_\-]+)\\[^\x00-\x1F""]{1,200}?\.(?:paa|rvmat|tga|png|p3d|bisurf|emat|edds)", RegexOptions.IgnoreCase);

    // Masks for AddonBuilder -include: everything binarize does not produce itself is copied as is.
    // config.cpp is converted by AddonBuilder, MLOD models by binarize (ODOL ones were set aside);
    // model.cfg only feeds binarize and stays out of the PBO, as with pboProject.
    private static IEnumerable<string> DirectCopyPatterns(string stage)
    {
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cpp", ".hpp", ".p3d" };
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.GetFiles(stage, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(f);
            var ext = Path.GetExtension(f);
            if (ext.Length == 0 || ext.Equals(".cfg", StringComparison.OrdinalIgnoreCase))
            {
                if (!name.Equals("model.cfg", StringComparison.OrdinalIgnoreCase)) set.Add("*" + name);
            }
            else if (!skip.Contains(ext)) set.Add("*" + ext.ToLowerInvariant());
        }
        return set;
    }

    // AddonBuilder prefixes each line with a timestamp and a level; binarize output arrives as "!>".
    private static readonly Regex AbLine = new(@"^\[[^\]]*\]\s*\[(\w+)\s*\]:\s?(.*)$");
    private static readonly string[] AbNoise =
    {
        "===", "---", "Updating base class", "Syncing file", "File sync done", "WELCOME TO ADDON BUILDER",
        "Version:", "DayZ Tools path", "-      found: '", "Builder starting", "Deleting empty folders",
    };

    private void AddonBuilderLine(RunCtx c, string raw, bool isErr, List<string> errors)
    {
        var m = AbLine.Match(raw);
        var tag = m.Success ? m.Groups[1].Value : "";
        var text = (m.Success ? m.Groups[2].Value : raw).Trim();
        if (text.StartsWith("!>")) text = text[2..].Trim();
        if (text.Length == 0) return;
        var missingTool = text.Contains("not found:", StringComparison.OrdinalIgnoreCase);
        if (!missingTool && AbNoise.Any(n => text.Contains(n, StringComparison.OrdinalIgnoreCase))) return;
        var level = missingTool || tag.StartsWith("ERR", StringComparison.OrdinalIgnoreCase) ? LogLevel.Err
            : tag.StartsWith("WARN", StringComparison.OrdinalIgnoreCase) || isErr ? LogLevel.Warn : LogLevel.Info;
        if (level == LogLevel.Err) errors.Add(text);
        L(c, "pack", text, level);
    }

    private static bool IsMlod(string p3d) => Magic(p3d) == "MLOD";
    private static bool IsOdol(string p3d) => Magic(p3d) == "ODOL";
    private static bool IsRapified(string file) => Magic(file) == "\0raP";

    private static string Magic(string file)
    {
        try
        {
            using var fs = File.OpenRead(file);
            var b = new byte[4];
            return fs.Read(b, 0, 4) == 4 ? Encoding.ASCII.GetString(b) : "";
        }
        catch (Exception) { return ""; }
    }

    // Junctions (game data, other mods) go first and on their own: a recursive delete must never
    // walk into what they point at.
    private static void DropTmp(string tmp)
    {
        try
        {
            if (!Directory.Exists(tmp)) return;
            var proj = Path.Combine(tmp, "proj");
            if (Directory.Exists(proj))
                foreach (var d in Directory.GetDirectories(proj))
                    if (new DirectoryInfo(d).LinkTarget != null) Directory.Delete(d, false);
            Directory.Delete(tmp, true);
        }
        catch (Exception) { }
    }

    // Leftovers of runs killed with the app; a day old means no live run can own them.
    private static void SweepTemp()
    {
        try
        {
            foreach (var d in Directory.GetDirectories(Path.GetTempPath(), "pbog_r*"))
                if (Directory.GetLastWriteTimeUtc(d) < DateTime.UtcNow.AddDays(-1)) DropTmp(d);
        }
        catch (Exception) { }
    }

    private async Task<PipelineError?> Sign(RunCtx c, CancellationToken ct)
    {
        var sign = Toolchain.DsSignFile;
        if (sign == null) return Err(c, "sign", "DSSignFile не найден (DayZ Tools)", null, null, "Fix.Pbo");
        var priv = Path.Combine(c.S.Tools.KeyDir, c.Mod.Key + ".biprivatekey");
        if (!File.Exists(priv))
            return Err(c, "sign", "нет приватного ключа " + c.Mod.Key + ".biprivatekey — создайте ключ на вкладке «Ключи»", c.S.Tools.KeyDir);
        if (!File.Exists(c.Pbo))
            return Err(c, "sign", "нет PBO для подписи — повторите с шага «Упаковка PBO»", c.Pbo);
        RemoveSignatures(c.AddonsDir, c.Mod.Name);
        L(c, "sign", "DSSignFile " + c.Mod.Key, LogLevel.Cmd);
        var r = await ProcRunner.RunAsync(sign, new[] { priv, c.Pbo }, c.AddonsDir,
            (line, err) => L(c, "sign", line, err ? LogLevel.Warn : LogLevel.Info), ct);
        var bisign = c.Pbo + "." + c.Mod.Key + ".bisign";
        if (r.ExitCode != 0 || !File.Exists(bisign))
            return Err(c, "sign", "подпись не создана", null, r.Tail);
        L(c, "sign", "подпись: " + Path.GetFileName(bisign), LogLevel.Ok);
        return null;
    }

    private PipelineError? Assemble(RunCtx c)
    {
        if (!File.Exists(c.Pbo)) return Err(c, "assemble", "нет PBO — повторите с шага «Упаковка PBO»", c.Pbo);
        var parts = "addons/";
        if (c.O.Sign)
        {
            if (File.Exists(c.BiKey))
            {
                var keysDir = Path.Combine(c.Root, "keys");
                Directory.CreateDirectory(keysDir);
                File.Copy(c.BiKey, Path.Combine(keysDir, c.Mod.Key + ".bikey"), true);
                parts += " + keys/";
            }
            else L(c, "assemble", "нет " + c.Mod.Key + ".bikey в папке ключей — keys\\ не собран", LogLevel.Warn);
        }
        var graftDir = Path.Combine(c.Root, "grafted");
        if (c.Plugin)
        {
            c.Dll ??= FindDll(c);
            if (c.Dll == null) return Err(c, "assemble", "не найдена DLL плагина — повторите с шага «Сборка»", c.Proj);
            Directory.CreateDirectory(graftDir);
            File.Copy(c.Dll, Path.Combine(graftDir, c.DllName), true);
            parts += " + grafted/";
        }
        // Without the Grafted method the plugin must go, or the host keeps loading it from grafted\.
        else if (!c.Grafted && DropPlugin(graftDir, c.DllName)) L(c, "assemble", "убран старый плагин: grafted\\" + c.DllName, LogLevel.Info);
        L(c, "assemble", Path.GetFileName(c.Root) + ": " + parts, LogLevel.Ok);
        L(c, "assemble", c.Root, LogLevel.Info);
        return null;
    }

    // Copies only this mod's files (not the whole shared @folder) into <server>\<root name>; when the
    // destination already is inside the server there is nothing to copy. The .bikey always goes to
    // <server>\keys so verifySignatures accepts the mod.
    private PipelineError? Deploy(RunCtx c)
    {
        if (c.Srv == null) { L(c, "deploy", "сервер не выбран — деплой пропущен", LogLevel.Warn); return null; }
        if (!Directory.Exists(c.Srv.Path)) return Err(c, "deploy", "папка сервера не найдена: " + c.Srv.Path);
        if (!File.Exists(c.Pbo)) return Err(c, "deploy", "PBO ещё не собран", c.Pbo);
        var bisign = c.Pbo + "." + c.Mod.Key + ".bisign";
        if (c.O.Sign && !File.Exists(bisign))
            return Err(c, "deploy", "PBO не подписан ключом " + c.Mod.Key + " — повторите с шага «Подпись»", bisign);

        if (PackLayout.IsUnder(c.Root, c.Srv.Path))
        {
            L(c, "deploy", "папка мода уже на сервере: " + c.Root, LogLevel.Info);
            c.DeployedDir = c.Root;
        }
        else
        {
            var dst = Path.Combine(c.Srv.Path, Path.GetFileName(c.Root));
            var addons = Path.Combine(dst, "addons");
            Directory.CreateDirectory(addons);
            var err = Replace(c, "deploy", c.Pbo, Path.Combine(addons, Path.GetFileName(c.Pbo)));
            if (err != null) return err;
            RemoveSignatures(addons, c.Mod.Name);
            foreach (var sig in Directory.GetFiles(c.AddonsDir, c.Mod.Name + ".pbo.*.bisign"))
                File.Copy(sig, Path.Combine(addons, Path.GetFileName(sig)), true);
            var dll = Path.Combine(c.Root, "grafted", c.DllName);
            if (c.Grafted && File.Exists(dll))
            {
                Directory.CreateDirectory(Path.Combine(dst, "grafted"));
                err = Replace(c, "deploy", dll, Path.Combine(dst, "grafted", c.DllName));
                if (err != null) return err;
            }
            else if (!c.Grafted && DropPlugin(Path.Combine(dst, "grafted"), c.DllName))
                L(c, "deploy", "убран старый плагин с сервера: grafted\\" + c.DllName, LogLevel.Info);
            var modKey = Path.Combine(c.Root, "keys", c.Mod.Key + ".bikey");
            if (File.Exists(modKey))
            {
                Directory.CreateDirectory(Path.Combine(dst, "keys"));
                File.Copy(modKey, Path.Combine(dst, "keys", c.Mod.Key + ".bikey"), true);
            }
            c.DeployedDir = dst;
            L(c, "deploy", Path.GetFileName(c.Root) + " -> " + dst, LogLevel.Ok);
        }

        if (c.O.Sign && File.Exists(c.BiKey))
        {
            var srvKeys = Path.Combine(c.Srv.Path, "keys");
            Directory.CreateDirectory(srvKeys);
            File.Copy(c.BiKey, Path.Combine(srvKeys, c.Mod.Key + ".bikey"), true);
            L(c, "deploy", "ключ " + c.Mod.Key + ".bikey -> " + srvKeys, LogLevel.Ok);
        }
        if (c.Mod.Kind == ModKinds.Client && !c.O.Sign && c.Srv.VerifySignatures == 2)
            L(c, "deploy", "сервер проверяет подписи (verifySignatures = 2), а мод не подписан — игроков с ним не пустит", LogLevel.Warn);
        FindDuplicates(c, c.Srv.Path);
        return null;
    }

    // The old layout put mods straight into the game: <server>\addons is loaded without any switch and
    // <server>\grafted is always scanned by the GRAFT host, so a copy left there loads twice.
    private void FindDuplicates(RunCtx c, string server)
    {
        var mine = c.DeployedDir ?? c.Root;
        foreach (var old in new[] { Path.Combine(server, "addons", c.Mod.Name + ".pbo"), Path.Combine(server, "grafted", c.DllName) })
        {
            if (!File.Exists(old) || PackLayout.IsUnder(old, mine) || c.Duplicates.Contains(old, StringComparer.OrdinalIgnoreCase)) continue;
            c.Duplicates.Add(old);
            L(c, "deploy", "на сервере осталась старая копия, она загрузится вместе с новой: " + old, LogLevel.Warn);
        }
    }

    private PipelineError? Host(RunCtx c)
    {
        if (c.Srv == null) { L(c, "host", "сервер не выбран — хост не ставится", LogLevel.Warn); return null; }
        var host = Toolchain.HostDll(c.S.Tools);
        if (host == null) return Err(c, "host", "hid.dll не найден");
        var target = Path.Combine(c.Srv.Path, "hid.dll");
        if (File.Exists(target)) { L(c, "host", "хост уже на месте: " + target, LogLevel.Warn); return null; }
        File.Copy(host, target, true);
        L(c, "host", "хост установлен: " + target, LogLevel.Ok);
        return null;
    }

    // Launch switch for the finished mod: relative to the server when the files are inside it, an
    // absolute path otherwise. GRAFT reads -mod=/-serverMod= only in the form -key="path", so the
    // quotes go after '=' and never before the key.
    private static (string ModLine, string? ServerDir) Launch(RunCtx c, IEnumerable<ServerInfo> servers)
    {
        var dir = c.DeployedDir ?? c.Root;
        var host = new[] { c.Srv }.Concat(servers).FirstOrDefault(s => s != null && PackLayout.IsUnder(dir, s.Path));
        string arg;
        if (host != null)
        {
            arg = Path.GetRelativePath(host.Path, dir);
            if (arg == ".") return ("", host.Path);
        }
        else arg = dir;
        var line = PackLayout.LaunchKey(c.Mod.Kind) + (arg.Contains(' ') ? "\"" + arg + "\"" : arg);
        return (line, host != null ? dir : null);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────
    private void UpdateMod(string id, Func<ModInfo, ModInfo> change)
    {
        lock (_gate)
        {
            var i = _s.Mods.FindIndex(x => x.Id == id);
            if (i < 0) return;
            _s.Mods[i] = change(_s.Mods[i]);
        }
        Save();
    }

    private static void RemoveSignatures(string addons, string name)
    {
        if (!Directory.Exists(addons)) return;
        foreach (var f in Directory.GetFiles(addons, name + ".pbo.*.bisign")) File.Delete(f);
    }

    private static bool DropPlugin(string graftDir, string dll)
    {
        var p = Path.Combine(graftDir, dll);
        if (!File.Exists(p)) return false;
        File.Delete(p);
        if (!Directory.EnumerateFileSystemEntries(graftDir).Any()) Directory.Delete(graftDir);
        return true;
    }

    private static (int Files, long Bytes) StageCopy(string src, string dst, List<string> skipped)
    {
        int files = 0;
        long bytes = 0;
        void Walk(string dir)
        {
            foreach (var d in Directory.GetDirectories(dir))
            {
                if (SkipDirs.Contains(Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
                {
                    skipped.Add(Path.GetRelativePath(src, d) + "\\");
                    continue;
                }
                Walk(d);
            }
            foreach (var f in Directory.GetFiles(dir))
            {
                var name = Path.GetFileName(f);
                if (PackLayout.IsPrefixFile(name)) continue;
                if (SkipFiles.Contains(name, StringComparer.OrdinalIgnoreCase)
                    || SkipExts.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase))
                {
                    skipped.Add(Path.GetRelativePath(src, f));
                    continue;
                }
                var target = Path.Combine(dst, Path.GetRelativePath(src, f));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(f, target, true);
                files++;
                bytes += new FileInfo(f).Length;
            }
        }
        Directory.CreateDirectory(dst);
        Walk(src);
        return (files, bytes);
    }

    private static IReadOnlyList<string> ReadModules(string src)
    {
        var scripts = Path.Combine(src, "scripts");
        return Directory.Exists(scripts)
            ? Directory.GetDirectories(scripts).Select(Path.GetFileName).Where(n => n != null).Select(n => n!).OrderBy(n => n).ToArray()
            : Array.Empty<string>();
    }

    private static string ReadVersion(string project, string src)
    {
        try
        {
            var cml = Path.Combine(project, "CMakeLists.txt");
            if (File.Exists(cml))
            {
                var m = Regex.Match(File.ReadAllText(cml), @"graft_plugin[\s\S]*?VERSION\s+(\S+)");
                if (m.Success) return m.Groups[1].Value;
            }
            foreach (var f in new[] { Path.Combine(src, "mod.cpp"), Path.Combine(src, "config.cpp") })
            {
                if (!File.Exists(f)) continue;
                var m = Regex.Match(File.ReadAllText(f), @"(?<![A-Za-z_])version\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase);
                if (m.Success) return m.Groups[1].Value;
            }
        }
        catch (Exception) { }
        return "0.1.0";
    }

    private static string ReadServerBuild(string path)
    {
        try
        {
            var exe = Path.Combine(path, "DayZServer_x64.exe");
            return File.Exists(exe) ? FileVersionInfo.GetVersionInfo(exe).ProductVersion ?? "?" : "?";
        }
        catch (Exception) { return "?"; }
    }

    private static int ReadVerifySignatures(string path)
    {
        try
        {
            var cfg = Directory.GetFiles(path, "*.cfg").FirstOrDefault();
            if (cfg == null) return 0;
            var m = Regex.Match(File.ReadAllText(cfg), @"verifySignatures\s*=\s*(\d)");
            return m.Success ? int.Parse(m.Groups[1].Value) : 0;
        }
        catch (Exception) { return 0; }
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

    private static string Fingerprint(string bikey)
    {
        try
        {
            var h = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(bikey))[..6];
            return string.Join(' ', h.Select(b => b.ToString("X2"))).Insert(0, "").Replace(" ", " ");
        }
        catch (Exception) { return ""; }
    }

    // The plugin of the preset just built (out\<preset> or build\<preset>), never a copy laid out in some
    // grafted\ folder; the newest match anywhere else is the fallback for custom output layouts.
    private static string? FindDll(RunCtx c)
    {
        try
        {
            var all = new DirectoryInfo(c.Proj).GetFiles(c.DllName, SearchOption.AllDirectories)
                .Where(f => !string.Equals(f.Directory?.Name, "grafted", StringComparison.OrdinalIgnoreCase)
                            && !PackLayout.IsUnder(f.FullName, c.Root))
                .OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            var preset = all.FirstOrDefault(f => PackLayout.IsUnder(f.FullName, Path.Combine(c.Proj, "out", c.O.Preset))
                                                 || PackLayout.IsUnder(f.FullName, Path.Combine(c.Proj, "build", c.O.Preset)));
            return (preset ?? all.FirstOrDefault())?.FullName;
        }
        catch (Exception) { return null; }
    }
}
