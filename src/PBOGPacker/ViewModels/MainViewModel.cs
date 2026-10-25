using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using PolyLab.PBOGPacker.Core;
using PolyLab.Ui.Core;

namespace PolyLab.PBOGPacker.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private sealed class Run
    {
        public string RunId = "";
        public string ModId = "";
        public string Origin = "main";
        public PipelineOptions Options = null!;
        public string Status = "running";
        public string? ErrStep;
        public readonly Stopwatch Clock = Stopwatch.StartNew();
        public long Ms;
    }

    private readonly IGraftBackend _backend;
    private readonly Dictionary<string, Run> _runs = new();
    private (string Origin, string ModId, PipelineOptions Options)? _pending;
    private Run? _current;
    private readonly DispatcherTimer _tick;
    private DateTime _lastAgo = DateTime.Now;

    private string _page = "build";
    private ModVM? _selected;
    private string _preset = "release";
    private bool _sign = true, _deploy = true, _host = true;
    private ServerVM? _server;
    private string _logFilter = "all";
    private bool _autoScroll = true;
    private bool _journal;
    private bool _checking;
    private DateTime? _checkedAt;
    private ResultVM? _result;
    private bool _resultOpen, _menuOpen, _optionsOpen, _serverMenuOpen, _dropActive;
    private string _newKeyTag = "";
    private AppSettings _settings = new();

    public MainViewModel(IGraftBackend backend)
    {
        _backend = backend;
        Steps = new ObservableCollection<StepVM>(Core.Steps.Ids.Select((id, i) => new StepVM(id, i, this)));
        LogView = CollectionViewSource.GetDefaultView(Log);
        LogView.Filter = o => o is LogLineVM l && (_logFilter == "all" || (_logFilter == "warn" ? l.Level is "warn" or "err" : l.Level == "err"));

        _backend.Started += e => Ui(() => OnStarted(e));
        _backend.StepChanged += e => Ui(() => OnStep(e));
        _backend.Log += e => Ui(() => OnLog(e));
        _backend.Done += e => Ui(() => _ = OnDone(e));
        _backend.Failed += e => Ui(() => _ = OnFailed(e));
        _backend.Cancelled += e => Ui(() => OnCancelled(e));

        _tick = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => OnTick(), Application.Current.Dispatcher);
        _tick.Start();

        Loc.Changed += () =>
        {
            foreach (var m in Mods) m.RaiseAll();
            foreach (var s in Steps) s.Touch();
            foreach (var s in Servers) s.RaiseAll();
            foreach (var d in Doctor) d.RaiseAll();
            RaiseAll();
        };

        NavCmd = new RelayCommand(p => { Page = p as string ?? "build"; MenuOpen = false; });
        SelectModCmd = new RelayCommand(p => { if (p is ModVM m) SelectedMod = m; Page = "build"; });
        AddModCmd = new RelayCommand(_ => AddModAsync(null));
        AddServerCmd = new RelayCommand(_ => AddServerAsync());
        BuildCmd = new RelayCommand(_ => StartAsync(null), _ => SelectedMod != null && !IsRunning);
        CancelCmd = new RelayCommand(_ => StopAsync(), _ => IsRunning);
        MultiCmd = new RelayCommand(_ => MultiSelect = !MultiSelect, _ => !IsEmpty);
        CheckAllCmd = new RelayCommand(_ => SetChecks(true), _ => Mods.Any(m => !m.IsChecked));
        CheckNoneCmd = new RelayCommand(_ => SetChecks(false), _ => CheckedCount > 0);
        PackCheckedCmd = new RelayCommand(_ => StartBatchAsync(), _ => CheckedCount > 0 && !IsRunning);
        BatchDetailCmd = new RelayCommand(p =>
        {
            if (p is not BatchRowVM { Error: { } err } || Result is not { IsBatch: true } sum) return;
            err.Back = sum;
            Result = err;
        });
        BatchBackCmd = new RelayCommand(_ => { if (Result?.Back is { } b) Result = b; });
        DeployCmd = new RelayCommand(p => DeployAsync(p as ModVM ?? SelectedMod), p => (p as ModVM ?? SelectedMod)?.CanDeploy == true);
        OpenCmd = new RelayCommand(p => p is string s ? _backend.OpenPathAsync(s) : Task.CompletedTask);
        RecheckCmd = new RelayCommand(_ => RecheckAsync(true), _ => !IsChecking);
        InstallHostCmd = new RelayCommand(p => InstallHostAsync(p as ServerVM));
        CheckGraftCmd = new RelayCommand(p => CheckGraftAsync(p as ServerVM), p => p is ServerVM { IsChecking: false });
        CloseReportCmd = new RelayCommand(p => (p as ServerVM)?.ClearReport());
        GraftActionCmd = new RelayCommand(p => GraftActionAsync(p as GraftCheckVM));
        InstallPluginCmd = new RelayCommand(p => InstallPluginAsync(p as ServerVM), p => p is ServerVM { IsBusy: false });
        CreateCppCmd = new RelayCommand(_ => CreateCppAsync(), _ => SelectedMod is { CanCreateCpp: true } && IsIdle);
        OpenWorkshopCmd = new RelayCommand(p => OpenWorkshopAsync(p as ServerVM), p => p is ServerVM { HasScripts: true } && !WorkshopOpen);
        LaunchServerCmd = new RelayCommand(p => LaunchServerAsync(p as ServerVM), p => p is ServerVM { CanLaunch: true });
        StopServerCmd = new RelayCommand(p => StopServerAsync(p as ServerVM), p => p is ServerVM { CanStop: true });
        CreateKeyCmd = new RelayCommand(_ => CreateKeyAsync());
        ExportKeyCmd = new RelayCommand(async p =>
        {
            if (p is not KeyVM k) return;
            Toast("ok", Loc.T("T.Exported"), await _backend.ExportKeyAsync(k.Tag));
        });
        CopyCmd = new RelayCommand(p => Copy(p as string));
        CopyLogCmd = new RelayCommand(_ => Copy(string.Join(Environment.NewLine, Log.Select(l => $"{l.Time} [{l.StepId}] {l.Text}"))));
        SaveSettingsCmd = new RelayCommand(async _ =>
        {
            await _backend.SaveSettingsAsync(_settings);
            OptionsChanged();
            Toast("ok", Loc.T("T.Saved"), ConfigPath);
        });
        PickOutCmd = new RelayCommand(_ => PickOutAsync(), _ => IsIdle);
        PickCommonCmd = new RelayCommand(_ => PickCommonAsync(), _ => SelectedMod != null && IsIdle);
        MakeCommonCmd = new RelayCommand(_ => SelectedMod is { } m && m.Info.Out is { } o ? SetCommonAsync(m, o) : Task.CompletedTask,
            _ => HasOutOverride && IsIdle);
        ResetOutCmd = new RelayCommand(_ => SelectedMod is { } m ? SetPackAsync(m, m.Kind, m.Key, null) : Task.CompletedTask,
            _ => HasOutOverride && IsIdle);
        LangCmd = new RelayCommand(p =>
        {
            var lang = p as string ?? UiLanguage.Next(Loc.Lang);
            _settings.Language = lang;
            Loc.Set(lang);
            MenuOpen = false;
        });
        ToggleStepCmd = new RelayCommand(p =>
        {
            if (p is not StepVM s) return;
            s.IsOpen = !s.IsOpen;
            s.AutoOpened = false;
        });
        FilterCmd = new RelayCommand(p => LogFilter = p as string ?? "all");
        JournalCmd = new RelayCommand(_ => ShowJournal = !ShowJournal);
        CloseResultCmd = new RelayCommand(_ => ResultOpen = false);
        ShowResultCmd = new RelayCommand(_ => ResultOpen = Result != null, _ => Result != null);
        RetryCmd = new RelayCommand(_ =>
        {
            ResultOpen = false;
            var r = Result;
            if (r == null) return Task.CompletedTask;
            SelectedMod = Mods.FirstOrDefault(m => m.Id == r.ModId) ?? SelectedMod;
            return StartAsync(r.Step);
        });
        MenuCmd = new RelayCommand(_ => MenuOpen = !MenuOpen);
        OptionsCmd = new RelayCommand(_ => OptionsOpen = !OptionsOpen);
        ServerMenuCmd = new RelayCommand(_ => ServerMenuOpen = !ServerMenuOpen);
        SelectServerCmd = new RelayCommand(p => { if (p is ServerVM s) SelectedServer = s; ServerMenuOpen = false; });
        PresetCmd = new RelayCommand(p => Preset = p as string ?? "release");
        QuitCmd = new RelayCommand(_ => Application.Current.Shutdown());
        ToastCloseCmd = new RelayCommand(p => { if (p is ToastVM t) Toasts.Remove(t); });
        BrowseCmd = new RelayCommand(p => Browse(p as string));
        PickLogoCmd = new RelayCommand(async p =>
        {
            if (p is not ModVM m) return;
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = Loc.T("Logo.Filter") + "|*.png;*.jpg;*.jpeg;*.bmp;*.gif" };
            if (dlg.ShowDialog() != true) return;
            m.Update(await _backend.SetModLogoAsync(m.Id, dlg.FileName));
        });
        ClearLogoCmd = new RelayCommand(async p =>
        {
            if (p is ModVM m) m.Update(await _backend.SetModLogoAsync(m.Id, null));
        });
        DemoResetCmd = new RelayCommand(async _ =>
        {
            if (_backend is not MockBackend mock) return;
            mock.Reset();
            MenuOpen = false;
            Result = null;
            await RefreshAsync();
            SelectedMod = Mods.FirstOrDefault();
            RaiseDemo();
            await RecheckAsync(false);
        });
    }

    private static void Ui(Action a)
    {
        var d = Application.Current.Dispatcher;
        if (d.CheckAccess()) a(); else d.BeginInvoke(a);
    }

    public ObservableCollection<ModVM> Mods { get; } = new();
    public ObservableCollection<ServerVM> Servers { get; } = new();
    public ObservableCollection<KeyVM> Keys { get; } = new();
    public ObservableCollection<DoctorVM> Doctor { get; } = new();
    public ObservableCollection<StepVM> Steps { get; }
    public ObservableCollection<LogLineVM> Log { get; } = new();
    public ICollectionView LogView { get; }
    public ObservableCollection<ToastVM> Toasts { get; } = new();

    public ICommand NavCmd { get; }
    public ICommand SelectModCmd { get; }
    public ICommand AddModCmd { get; }
    public ICommand AddServerCmd { get; }
    public ICommand BuildCmd { get; }
    public ICommand CancelCmd { get; }
    public ICommand DeployCmd { get; }
    public ICommand OpenCmd { get; }
    public ICommand RecheckCmd { get; }
    public ICommand InstallHostCmd { get; }
    public ICommand CheckGraftCmd { get; }
    public ICommand CloseReportCmd { get; }
    public ICommand GraftActionCmd { get; }
    public ICommand InstallPluginCmd { get; }
    public ICommand PickCommonCmd { get; }
    public ICommand MakeCommonCmd { get; }
    public ICommand CreateCppCmd { get; }
    public ICommand LaunchServerCmd { get; }
    public ICommand StopServerCmd { get; }
    public ICommand OpenWorkshopCmd { get; }
    public ICommand MultiCmd { get; }
    public ICommand CheckAllCmd { get; }
    public ICommand CheckNoneCmd { get; }
    public ICommand PackCheckedCmd { get; }
    public ICommand BatchDetailCmd { get; }
    public ICommand BatchBackCmd { get; }

    private ModsVM? _workshop;
    private bool _workshopOpen;
    public ModsVM? Workshop { get => _workshop; private set => Set(ref _workshop, value); }
    public bool WorkshopOpen
    {
        get => _workshopOpen;
        set { if (Set(ref _workshopOpen, value)) CommandManager.InvalidateRequerySuggested(); }
    }

    public async Task SetWorkshopDirAsync(string dir)
    {
        _settings.WorkshopDir = dir;
        await _backend.SaveSettingsAsync(_settings);
        Raise(nameof(Settings));
        Raise(nameof(WorkshopAuto));
    }

    public async Task OpenWorkshopAsync(ServerVM? s)
    {
        if (s?.SelectedBat is not { } bat) return;
        Workshop = new ModsVM(_backend, this, s, bat);
        WorkshopOpen = true;
        await Workshop.LoadAsync();
    }
    public ICommand CreateKeyCmd { get; }
    public ICommand ExportKeyCmd { get; }
    public ICommand CopyCmd { get; }
    public ICommand CopyLogCmd { get; }
    public ICommand SaveSettingsCmd { get; }
    public ICommand LangCmd { get; }
    public ICommand ToggleStepCmd { get; }
    public ICommand FilterCmd { get; }
    public ICommand JournalCmd { get; }
    public ICommand CloseResultCmd { get; }
    public ICommand ShowResultCmd { get; }
    public ICommand RetryCmd { get; }
    public ICommand MenuCmd { get; }
    public ICommand OptionsCmd { get; }
    public ICommand ServerMenuCmd { get; }
    public ICommand SelectServerCmd { get; }
    public ICommand PresetCmd { get; }
    public ICommand QuitCmd { get; }
    public ICommand ToastCloseCmd { get; }
    public ICommand BrowseCmd { get; }
    public ICommand DemoResetCmd { get; }
    public ICommand PickLogoCmd { get; }
    public ICommand ClearLogoCmd { get; }
    public ICommand PickOutCmd { get; }
    public ICommand ResetOutCmd { get; }

    public string PageBackground => $"pack://application:,,,/PBOG_Packer;component/Assets/Photos/bg_{Page}.jpg";

    public IReadOnlyList<PhotoCredit> Credits { get; } = LoadCredits();

    private static IReadOnlyList<PhotoCredit> LoadCredits()
    {
        try
        {
            var res = Application.GetResourceStream(new Uri("pack://application:,,,/PBOG_Packer;component/Assets/Photos/credits.json"));
            if (res == null) return Array.Empty<PhotoCredit>();
            using var s = res.Stream;
            return System.Text.Json.JsonSerializer.Deserialize<List<PhotoCredit>>(s) ?? new List<PhotoCredit>();
        }
        catch
        {
            return Array.Empty<PhotoCredit>();
        }
    }

    public string DiscordUrl => "https://discord.gg/3dBy8Rq8ht";
    public string GraftUrl => "https://github.com/KRdayzmodding/KR_GRAFTED";
    public string SeraphUrl => "https://github.com/6wingSerap";
    public string AboutVersion => Loc.F("About.Version", AppVersion, GraftDoctor.Release);

    public string Page
    {
        get => _page;
        set
        {
            if (!Set(ref _page, value)) return;
            OptionsOpen = ServerMenuOpen = false;
            Raise(nameof(PageBackground));
            OptionsChanged();
            if (value == "servers")
            {
                foreach (var s in Servers) s.Rescan();
                _ = CheckServersAsync();
            }
        }
    }

    public ModVM? SelectedMod
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            ShowJournal = _current != null && value != null && _current.ModId == value.Id && (_current.Status == "running" || _journal);
            RaiseRunState();
            Raise(nameof(HasMod));
            OptionsChanged();
            RaiseEnv();
        }
    }

    public bool HasMod => SelectedMod != null;
    public bool IsEmpty => Mods.Count == 0;

    public string Preset { get => _preset; set { if (Set(ref _preset, value)) OptionsChanged(); } }
    public bool Sign { get => _sign; set { if (Set(ref _sign, value)) OptionsChanged(); } }
    public bool Deploy { get => _deploy; set { if (Set(ref _deploy, value)) OptionsChanged(); } }
    public bool Host { get => _host; set { if (Set(ref _host, value)) OptionsChanged(); } }

    public ServerVM? SelectedServer
    {
        get => _server;
        set { if (Set(ref _server, value)) OptionsChanged(); }
    }

    public string KeyText => Sign ? SelectedMod?.Key ?? "—" : Loc.T("Info.Unsigned");
    public string ServerText => SelectedServer?.Name ?? "—";

    private void OptionsChanged()
    {
        foreach (var s in Steps) s.Touch();
        foreach (var n in new[]
                 {
                     nameof(KeyText), nameof(ServerText), nameof(SegStates), nameof(ModSide), nameof(UseGrafted), nameof(ModKey),
                     nameof(DestText), nameof(DestShort), nameof(HasOutOverride), nameof(DestHint), nameof(KindLine), nameof(BigSub),
                     nameof(CommonKind), nameof(PickCommonText), nameof(MakeCommonText),
                     nameof(BuildsPlugin), nameof(IsGraftedMod), nameof(GraftNoProject), nameof(GraftToggleEnabled), nameof(KeyChoices), nameof(NoKeys),
                 })
            Raise(n);
        SelectedMod?.RaiseAll();
    }

    public bool IsStepOff(string id) => _current == null || _current.ModId != SelectedMod?.Id
        ? (id == "sign" && !Sign) || (id == "deploy" && !Deploy) || (id == "host" && !Host)
          || !PackBackend.StepApplies(id, SelectedMod?.Kind ?? ModKinds.Grafted, SelectedMod?.HasProject ?? true)
        : false;

    public string StepTool(string id)
    {
        var m = SelectedMod;
        if (m == null) return "";
        if (!PackBackend.StepApplies(id, m.Kind, m.HasProject)) return Loc.T(m.IsGrafted ? "Tool.NoProject" : "Tool.GraftOnly");
        var srv = (SelectedServer?.Path ?? "DayZServer").Split('\\').Last();
        var leaf = m.DestLeaf;
        var bin = _settings.Pack.Rapify;
        return id switch
        {
            "env" => m.BuildsPlugin ? "clang-cl · cmake · AddonBuilder · graft" : bin ? "AddonBuilder · CfgConvert · DSSignFile" : "AddonBuilder · DSSignFile",
            "build" => $"cmake --build --preset {Preset}",
            "protogen" => $"graft protogen → grafted_natives_{m.Name}.c",
            "pack" => $@"AddonBuilder → {leaf}\addons\{m.Name}.pbo" + (bin ? " · binarize" : " · -packonly"),
            "sign" => $"DSSignFile · {m.Key}.biprivatekey",
            "assemble" => m.BuildsPlugin ? $@"{leaf}\{{addons, keys, grafted}}" : $@"{leaf}\{{addons, keys}}",
            "deploy" => $@"→ {srv}\{leaf}",
            _ => $@"hid.dll → {srv}\DayZServer_x64.exe",
        };
    }

    // ── packing type and destination of the selected mod ────────────────────────
    public bool IsGraftedMod => SelectedMod?.IsGrafted ?? false;
    public bool BuildsPlugin => SelectedMod?.BuildsPlugin ?? false;
    public bool GraftNoProject => IsGraftedMod && !BuildsPlugin;
    public bool GraftToggleEnabled => SelectedMod?.IsClient == false && IsIdle;

    // Key picker as segment buttons: a ComboBox inside a StaysOpen=False popup closes the popup on pick.
    public IReadOnlyList<ChoiceVM> KeyChoices => Keys
        .Select(k => new ChoiceVM(k.Tag, k.Tag == SelectedMod?.Key, t => ModKey = t)).ToList();
    public bool NoKeys => Keys.Count == 0;

    public string ModSide
    {
        get => SelectedMod?.IsClient == true ? ModKinds.Client : ModKinds.Server;
        set
        {
            var m = SelectedMod;
            if (m == null || value == ModSide) return;
            _ = SwitchSideAsync(m, value == ModKinds.Client ? ModKinds.Client : ModKinds.Grafted);
        }
    }

    private async Task SwitchSideAsync(ModVM m, string kind)
    {
        await SetPackAsync(m, kind, m.Key, m.Info.Out);
        if (kind == ModKinds.Client || !_settings.Pack.AutoCpp || !m.CanCreateCpp) return;
        _cppFailed.Clear();
        if (await AutoCppAsync(m.Info) is { } info)
        {
            m.Update(info);
            OptionsChanged();
            Toast("ok", Loc.T("T.CppCreated"), info.Cpp ?? "");
        }
        else if (_cppFailed.Count > 0) Toast("warn", Loc.T("T.CppErr"), string.Join("; ", _cppFailed));
    }

    // The automatic C++ project of a server mod. A mod that cannot have one (no Scripts, a name that is
    // not a valid plugin name) stays as it is: the reason is shown, adding the mod is not undone.
    private readonly List<string> _cppFailed = new();

    private async Task<ModInfo?> AutoCppAsync(ModInfo mod)
    {
        if (!_settings.Pack.AutoCpp || mod.Kind == ModKinds.Client || PackLayout.HasProject(mod) || !System.IO.Directory.Exists(mod.Path))
            return null;
        try { return await _backend.CreateCppProjectAsync(mod.Id); }
        catch (Exception e)
        {
            _cppFailed.Add(mod.Name + ": " + e.Message);
            return null;
        }
    }

    public bool UseGrafted
    {
        get => SelectedMod?.IsGrafted ?? false;
        set
        {
            var m = SelectedMod;
            if (m == null || value == UseGrafted) return;
            _ = SetPackAsync(m, value ? ModKinds.Grafted : ModKinds.Server, m.Key, m.Info.Out);
        }
    }

    public string? ModKey
    {
        get => SelectedMod?.Key;
        set
        {
            var m = SelectedMod;
            if (m == null || string.IsNullOrEmpty(value) || value == m.Key) return;
            _ = SetPackAsync(m, m.Kind, value, m.Info.Out);
        }
    }

    public string DestText => SelectedMod?.Dest ?? "—";
    public bool HasOutOverride => !string.IsNullOrWhiteSpace(SelectedMod?.Info.Out);
    public string DestHint => Loc.F(HasOutOverride ? "Opt.OutOwn"
        : SelectedMod != null && !string.IsNullOrWhiteSpace(PackLayout.Configured(SelectedMod.Info with { Out = null }, _settings.Pack)) ? "Opt.OutCommon"
        : "Opt.OutDefault", CommonKind);

    // "client" or "server" mods: the group whose common folder this mod follows.
    public string CommonKind => Loc.T(SelectedMod?.Kind == ModKinds.Client ? "Opt.CommonClient" : "Opt.CommonServer");
    public string PickCommonText => Loc.F("Opt.PickCommon", CommonKind);
    public string MakeCommonText => Loc.F("Opt.MakeCommon", CommonKind);

    // Long paths keep their tail: the @folder name matters more than the drive.
    public string DestShort
    {
        get
        {
            var parts = DestText.Split('\\');
            return parts.Length > 3 ? @"…\" + string.Join(@"\", parts[^2..]) : DestText;
        }
    }

    public string KindLine => SelectedMod == null ? "—"
        : SelectedMod.BuildsPlugin ? SelectedMod.KindText + " · " + Preset : SelectedMod.KindText;

    public string BigSub => Loc.T(SelectedMod?.BuildsPlugin == false ? "Big.PackSub" : "Big.BuildSub");

    private async Task SetPackAsync(ModVM m, string kind, string key, string? outDir)
    {
        var oldDest = m.Dest;
        try { m.Update(await _backend.SetModPackAsync(m.Id, kind, key, outDir)); }
        catch (Exception e) { Toast("err", Loc.T("T.PackErr"), e.Message); }
        OptionsChanged();
        RaiseEnv();
        // The previous destination keeps its PBO; a server started with both folders loads the mod twice.
        if (!string.Equals(oldDest, m.Dest, StringComparison.OrdinalIgnoreCase)
            && System.IO.File.Exists(System.IO.Path.Combine(oldDest, "addons", m.Name + ".pbo")))
            Toast("warn", Loc.T("T.OldCopy"), oldDest);
    }

    // The chosen folder becomes the common one for the mod's kind: every mod of that kind without a
    // folder of its own (new ones included) packs there at once; this mod drops its own folder for it.
    private async Task PickCommonAsync()
    {
        var m = SelectedMod;
        if (m == null) return;
        var current = PackLayout.Configured(m.Info with { Out = null }, _settings.Pack);
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = PickCommonText + (current.Length > 0 ? "  (" + current + ")" : "") };
        dlg.InitialDirectory = PickerStart(m, current);
        if (dlg.ShowDialog() != true) return;
        await SetCommonAsync(m, dlg.FolderName);
    }

    // Where the folder picker opens: the folder set now if it exists, else the selected server (the @folders
    // live there), else next to the mod. Never the mod sources: the picker preselects what it opens on.
    private string? PickerStart(ModVM m, string current)
    {
        foreach (var d in new[] { current, SelectedServer?.Path, System.IO.Path.GetDirectoryName(m.Dest) })
            if (!string.IsNullOrWhiteSpace(d) && System.IO.Directory.Exists(d) && PackLayout.SourcesOf(d, Mods.Select(x => x.Info)) == null)
                return PackLayout.NormalizeRoot(d);
        return null;
    }

    private bool RejectSources(string dir)
    {
        if (PackLayout.SourcesOf(dir, Mods.Select(x => x.Info)) is not { } owner) return false;
        Toast("err", Loc.T("T.OutIsSources"), Loc.F("T.OutIsSourcesSub", PackLayout.NormalizeRoot(dir), owner));
        return true;
    }

    public Task SetCommonForShotAsync(string dir) => SelectedMod is { } m ? SetCommonAsync(m, dir) : Task.CompletedTask;

    private async Task SetCommonAsync(ModVM m, string dir)
    {
        if (RejectSources(dir)) return;
        PackLayout.SetCommon(_settings.Pack, PackLayout.CommonSlot(m.Info, _settings.Pack), dir.Trim());
        await _backend.SaveSettingsAsync(_settings);
        if (m.Info.Out != null) await SetPackAsync(m, m.Kind, m.Key, null);
        foreach (var x in Mods) x.RaiseAll();
        Raise(nameof(Settings));
        OptionsChanged();
        var same = Mods.Count(x => x.Kind == ModKinds.Client == (m.Kind == ModKinds.Client) && x.Info.Out == null);
        Toast("ok", Loc.F("T.CommonSet", CommonKind), Loc.F("T.CommonSetSub", PackLayout.NormalizeRoot(dir), same));
    }

    private async Task PickOutAsync()
    {
        var m = SelectedMod;
        if (m == null) return;
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = Loc.T("Opt.OutPick") + "  (" + m.Name + ")" };
        dlg.InitialDirectory = PickerStart(m, m.Info.Out ?? "");
        if (dlg.ShowDialog() != true) return;
        if (RejectSources(dlg.FolderName)) return;
        await SetPackAsync(m, m.Kind, m.Key, dlg.FolderName);
    }

    // A batch counts as running from its first mod to its summary, gaps between mods included.
    public bool IsRunning => _current?.Status == "running" || _batch != null;
    public bool IsIdle => !IsRunning;

    private static string Spread(string s) => string.Join((char)0x2009, s.ToCharArray());
    public string CapBuild => Spread(Loc.T("Tile.Build"));
    public string CapSign => Spread(Loc.T("Tile.Sign"));
    public string CapDeploy => Spread(Loc.T("Tile.Deploy"));
    public string CapRun => Spread(Loc.T("Run.Title"));
    public string? RunningModId => _current?.Status == "running" ? _current.ModId : null;
    public bool RunIsForSelected => _current != null && _current.ModId == SelectedMod?.Id;

    public bool ShowJournal
    {
        get => _journal;
        set { if (Set(ref _journal, value)) Raise(nameof(ShowTiles)); }
    }

    public bool ShowTiles => !ShowJournal;

    public string LogFilter
    {
        get => _logFilter;
        set { if (Set(ref _logFilter, value)) LogView.Refresh(); }
    }

    public bool AutoScroll { get => _autoScroll; set => Set(ref _autoScroll, value); }
    public int WarnCount => Log.Count(l => l.Level == "warn");
    public int ErrCount => Log.Count(l => l.Level == "err");
    public string LinesText => Loc.F("Log.Lines", Log.Count);
    public bool LogEmpty => Log.Count == 0;

    public string RunKind => !RunIsForSelected ? "idle" : _current!.Status switch
    {
        "running" => "run",
        "done" => "ok",
        "error" => "err",
        _ => "cancel",
    };

    public string RunText => !RunIsForSelected ? Loc.T("Run.Idle") : _current!.Status switch
    {
        "running" => Loc.T("Run.Running"),
        "done" => Loc.T("Run.Done"),
        "error" => Loc.F("Run.Error", Loc.T("Step." + _current.ErrStep)),
        _ => Loc.T("Run.Cancelled"),
    };

    public string ElapsedText => RunIsForSelected ? Loc.Clock(_current!.Status == "running" ? _current.Clock.ElapsedMilliseconds : _current.Ms) : "00:00.0";

    public int DoneCount => Steps.Count(s => s.Status is StepStatus.Done or StepStatus.Skipped or StepStatus.Error);

    public string StepLine
    {
        get
        {
            var active = Steps.FirstOrDefault(s => s.Status == StepStatus.Running);
            return active != null ? Loc.F("Run.Step", active.Index + 1, active.Title) : RunText;
        }
    }

    public IEnumerable<string> SegStates => Steps.Select(s => RunIsForSelected ? s.Kind : (s.Kind == "off" ? "skip" : "idle")).ToList();

    private void RaiseRunState()
    {
        Raise(nameof(IsRunning));
        Raise(nameof(IsIdle));
        Raise(nameof(RunIsForSelected));
        Raise(nameof(RunKind));
        Raise(nameof(RunText));
        Raise(nameof(ElapsedText));
        Raise(nameof(StepLine));
        Raise(nameof(DoneCount));
        Raise(nameof(SegStates));
        Raise(nameof(WarnCount));
        Raise(nameof(ErrCount));
        Raise(nameof(LinesText));
        Raise(nameof(LogEmpty));
        Raise(nameof(RunningModId));
        Raise(nameof(RunModLine));
        Raise(nameof(BatchFraction));
        Raise(nameof(PackCheckedTip));
        Raise(nameof(CancelTip));
        Raise(nameof(GraftToggleEnabled));
        foreach (var m in Mods) { m.Raise2(); }
        CommandManager.InvalidateRequerySuggested();
    }

    public bool IsChecking { get => _checking; private set { if (Set(ref _checking, value)) RaiseEnv(); } }
    public int EnvIssues => IsChecking ? 0 : Doctor.Count(d => d.Item.State != CheckState.Ok);
    public string EnvKind => IsChecking ? "run" : Doctor.Any(d => d.Item.State == CheckState.Err) ? "err" : EnvIssues > 0 ? "warn" : "ok";
    public string EnvIcon => EnvKind switch { "run" => "spinner", "ok" => "shield", "warn" => "warn", _ => "x" };
    public string EnvStatus => IsChecking ? Loc.T("Tc.Checking") : EnvIssues > 0 ? Loc.F("Tc.Bad", EnvIssues) : Loc.T("Tc.Ok");
    public string EnvAnswer => IsChecking ? Loc.T("Env.Checking") : EnvIssues > 0 ? Loc.F("Env.No", EnvIssues) : Loc.T("Env.Yes");
    public string EnvChecked => _checkedAt == null || IsChecking ? "…" : Loc.F("Env.Checked", _checkedAt.Value.ToString("HH:mm:ss"));
    public string EnvBanner => Loc.F("Env.Banner", EnvIssues);
    public bool EnvBad => EnvIssues > 0;

    private IEnumerable<DoctorVM> ModDoctor => SelectedMod?.BuildsPlugin != false
        ? Doctor
        : Doctor.Where(d => d.Item.Id is "pbo" || (d.Item.Id == "cfgconv" && _settings.Pack.Rapify) || (d.Item.Id == "dssign" && Sign));
    public int ModEnvIssues => IsChecking ? 0 : ModDoctor.Count(d => d.Item.State != CheckState.Ok);
    public bool ModEnvBad => ModEnvIssues > 0;
    public string ModEnvBanner => Loc.F("Env.Banner", ModEnvIssues);
    public string OkN => IsChecking ? "–" : Doctor.Count(d => d.Item.State == CheckState.Ok).ToString();
    public string WarnN => IsChecking ? "–" : Doctor.Count(d => d.Item.State == CheckState.Warn).ToString();
    public string ErrN => IsChecking ? "–" : Doctor.Count(d => d.Item.State == CheckState.Err).ToString();

    private void RaiseEnv()
    {
        foreach (var n in new[] { nameof(EnvIssues), nameof(EnvKind), nameof(EnvIcon), nameof(EnvStatus), nameof(EnvAnswer), nameof(EnvChecked), nameof(EnvBanner), nameof(EnvBad), nameof(OkN), nameof(WarnN), nameof(ErrN), nameof(ModEnvIssues), nameof(ModEnvBad), nameof(ModEnvBanner) })
            Raise(n);
    }

    public ResultVM? Result { get => _result; private set => Set(ref _result, value); }
    public bool ResultOpen { get => _resultOpen; set => Set(ref _resultOpen, value); }
    public bool MenuOpen { get => _menuOpen; set => Set(ref _menuOpen, value); }
    public bool OptionsOpen { get => _optionsOpen; set => Set(ref _optionsOpen, value); }
    public bool ServerMenuOpen { get => _serverMenuOpen; set => Set(ref _serverMenuOpen, value); }
    public bool DropActive { get => _dropActive; set => Set(ref _dropActive, value); }
    public string NewKeyTag { get => _newKeyTag; set => Set(ref _newKeyTag, value); }
    public AppSettings Settings { get => _settings; private set => Set(ref _settings, value); }
    public string LangCode { get => Loc.Lang; set => LangCmd.Execute(value); }
    public IReadOnlyList<string> Presets { get; } = new[] { "release", "debug" };
    public IEnumerable<string> KeyTags => Keys.Select(k => k.Tag);

    public string WorkshopAuto => Loc.F("Set.WorkshopHint", ServerMods.DefaultWorkshopDir() ?? "—");
    public string ToolsDir => System.IO.Path.GetDirectoryName(Toolchain.Graft(_settings.Tools) ?? _settings.Tools.Graft) ?? "";
    public string GraftReleases => "https://github.com/KRdayzmodding/KR_GRAFTED/releases";
    public string ConfigPath => System.IO.Path.Combine(PackBackend.StateDir, "state.json");

    public string AppVersion => "0.1.1";
    public string FooterVersion => "GRAFT " + GraftDoctor.Release + " · DayZ " + GraftDoctor.TargetBuild;
    public string FooterGraft => "GRAFT " + GraftDoctor.Release;
    public string FooterBuild => "DayZ " + GraftDoctor.TargetBuild;

    public bool IsDemo => _backend is MockBackend;
    private MockBackend? Mock => _backend as MockBackend;

    public bool DemoEmpty
    {
        get => Mock?.EmptyMods ?? false;
        set { if (Mock == null) return; Mock.EmptyMods = value; _ = RefreshAsync(); Raise(); }
    }

    public bool DemoEnv
    {
        get => Mock?.BrokenEnv ?? false;
        set { if (Mock == null) return; Mock.BrokenEnv = value; _ = RecheckAsync(false); Raise(); }
    }

    public bool DemoFail
    {
        get => Mock?.FailNext ?? false;
        set { if (Mock == null) return; Mock.FailNext = value; Raise(); }
    }

    private void RaiseDemo()
    {
        Raise(nameof(DemoEmpty));
        Raise(nameof(DemoEnv));
        Raise(nameof(DemoFail));
    }

    public async Task InitAsync()
    {
        Settings = await _backend.GetSettingsAsync();
        Loc.Set(Settings.Language);
        _preset = Settings.Defaults.Preset;
        _sign = Settings.Defaults.Sign;
        await RefreshAsync();
        SelectedMod = Mods.FirstOrDefault();
        SelectedServer = Servers.FirstOrDefault(s => s.Id == Settings.Defaults.Server) ?? Servers.FirstOrDefault();
        _ = RecheckAsync(false);
    }

    public async Task RefreshAsync()
    {
        var mods = await _backend.ListModsAsync();
        var servers = await _backend.ListServersAsync();
        var keys = await _backend.ListKeysAsync();

        Sync(Servers, servers, s => s.Id, (vm, i) => vm.Update(i), i => new ServerVM(i, this));
        Keys.Clear();
        foreach (var k in keys) Keys.Add(new KeyVM(k, this));
        Sync(Mods, mods, m => m.Id, (vm, i) => vm.Update(i), i => new ModVM(i, this));

        if (SelectedMod == null || !Mods.Contains(SelectedMod)) SelectedMod = Mods.FirstOrDefault();
        if (SelectedServer == null || !Servers.Contains(SelectedServer)) SelectedServer = Servers.FirstOrDefault();
        foreach (var s in Servers) s.RaiseAll();
        Raise(nameof(IsEmpty));
        if (IsEmpty) MultiSelect = false;
        RaiseChecks();
        Raise(nameof(KeyTags));
        Raise(nameof(KeyChoices));
        Raise(nameof(NoKeys));
        RaiseDemo();
    }

    private static void Sync<TVm, TInfo>(ObservableCollection<TVm> col, IReadOnlyList<TInfo> items, Func<TInfo, string> id,
        Action<TVm, TInfo> update, Func<TInfo, TVm> create) where TVm : class
    {
        var ids = items.Select(id).ToHashSet();
        foreach (var vm in col.ToList())
        {
            var vmId = vm switch { ModVM m => m.Id, ServerVM s => s.Id, _ => "" };
            if (!ids.Contains(vmId)) col.Remove(vm);
        }
        foreach (var info in items)
        {
            var existing = col.FirstOrDefault(v => (v switch { ModVM m => m.Id, ServerVM s => s.Id, _ => "" }) == id(info));
            if (existing != null) update(existing, info); else col.Add(create(info));
        }
    }

    public Task AddModAsync(string? path) => AddModsAsync(path == null ? null : new[] { path });

    // Several folders in one go (Ctrl/Shift in the dialog, or dropped together): added one after another,
    // one that fails does not stop the rest, and a single summary says what happened to each.
    public async Task AddModsAsync(IReadOnlyList<string>? paths)
    {
        if (paths == null)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = Loc.T("Side.AddMany"), Multiselect = true };
            if (dlg.ShowDialog() != true) return;
            paths = dlg.FolderNames;
        }
        if (paths.Count == 0) return;
        var before = Mods.Select(m => m.Id).ToHashSet();
        var added = new List<ModInfo>();
        var cpp = new List<string>();
        _cppFailed.Clear();
        var known = new List<string>();
        var failed = new List<string>();
        foreach (var p in paths)
        {
            try
            {
                var mod = await _backend.AddModAsync(p);
                if (before.Contains(mod.Id) || added.Any(a => a.Id == mod.Id)) known.Add(mod.Name);
                else
                {
                    if (await AutoCppAsync(mod) is { } withCpp)
                    {
                        mod = withCpp;
                        cpp.Add(mod.Name);
                    }
                    added.Add(mod);
                }
            }
            catch (Exception e) { failed.Add(System.IO.Path.GetFileName(p.TrimEnd('\\', '/')) + ": " + e.Message); }
        }
        await RefreshAsync();
        var first = added.FirstOrDefault()?.Id;
        if (first != null) SelectedMod = Mods.FirstOrDefault(m => m.Id == first) ?? SelectedMod;
        if (added.Count > 0 || known.Count > 0) Page = "build";

        if (_cppFailed.Count > 0) Toast("warn", Loc.T("T.CppErr"), string.Join("; ", _cppFailed));
        if (paths.Count == 1)
        {
            if (added.Count == 1)
                Toast("ok", Loc.F("T.Added", added[0].Name), cpp.Count > 0 ? Loc.F("T.AddedCpp", added[0].Cpp) : added[0].Path);
            else if (known.Count == 1) Toast("ok", Loc.F("T.AlreadyAdded", known[0]), paths[0]);
            else Toast("err", Loc.T("T.AddErr"), failed.FirstOrDefault() ?? "");
            return;
        }
        var parts = new List<string>();
        if (added.Count > 0) parts.Add(string.Join(", ", added.Select(a => a.Name)));
        if (cpp.Count > 0) parts.Add(Loc.F("T.AddCpp", string.Join(", ", cpp)));
        if (known.Count > 0) parts.Add(Loc.F("T.AddKnown", string.Join(", ", known)));
        if (failed.Count > 0) parts.Add(Loc.F("T.AddFailed", string.Join("; ", failed)));
        Toast(failed.Count > 0 ? (added.Count > 0 ? "warn" : "err") : "ok",
            Loc.F("T.AddedMany", added.Count, paths.Count), string.Join(" · ", parts));
    }

    private async Task AddServerAsync()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = Loc.T("Act.AddServer") };
        if (dlg.ShowDialog() != true) return;
        var srv = await _backend.AddServerAsync(dlg.FolderName);
        await RefreshAsync();
        if (srv == null) return;
        SelectedServer ??= Servers.FirstOrDefault(s => s.Id == srv.Id);
        Toast("ok", Loc.F("T.ServerAdded", srv.Name), srv.Path);
    }

    private PipelineOptions CurrentOptions(string? from = null, IReadOnlyList<string>? only = null) =>
        new(Preset, Sign, Deploy, Host, SelectedServer?.Id ?? "", from, only);

    public async Task StartAsync(string? fromStep)
    {
        var m = SelectedMod;
        if (m == null || IsRunning) return;
        Page = "build";
        var opts = CurrentOptions(fromStep);
        _pending = ("main", m.Id, opts);
        await _backend.RunPipelineAsync(m.Id, opts);
    }

    private async Task DeployAsync(ModVM? m)
    {
        if (m == null || !m.CanDeploy) return;
        m.IsBusy = true;
        var opts = new PipelineOptions(Preset, Sign, true, true, SelectedServer?.Id ?? "", null, new[] { "deploy", "host" });
        _pending = ("card", m.Id, opts);
        await _backend.RunPipelineAsync(m.Id, opts);
    }

    // ── several mods in one go ──────────────────────────────────────────────────
    // Ticked mods are packed one after another, each by its own settings (kind, key, folder) and the
    // options set now (preset, signing, deploy, host, server). One that fails does not stop the rest;
    // Cancel stops the batch. The summary lists every mod; a failed one opens its full error.
    private sealed class Batch
    {
        public Batch(IReadOnlyList<ModVM> mods, PipelineOptions options)
        {
            Mods = mods;
            Options = options;
        }

        public IReadOnlyList<ModVM> Mods { get; }
        public PipelineOptions Options { get; }
        public int Index;
        public bool Stopped;
        public readonly List<BatchRowVM> Rows = new();
        public readonly Stopwatch Clock = Stopwatch.StartNew();
    }

    private Batch? _batch;
    private bool _multi;
    private ModVM? _anchor;
    private readonly Dictionary<string, TaskCompletionSource<string?>> _waits = new();
    private readonly Dictionary<string, string?> _ended = new();

    public bool MultiSelect
    {
        get => _multi;
        set
        {
            if (!Set(ref _multi, value)) return;
            if (!value) SetChecks(false);
            _anchor = null;
            RaiseChecks();
        }
    }

    public int CheckedCount => Mods.Count(m => m.IsChecked);
    public bool IsBatching => _batch != null;
    public string MultiTip => Loc.T(IsEmpty ? "Tip.MultiEmpty" : "Tip.Multi");
    public string CheckedText => Loc.F("Side.Checked", CheckedCount, Mods.Count);
    public string PackCheckedText => Loc.F("Batch.Pack", CheckedCount);
    public string ModsCountText => Loc.F("Side.Mods", Mods.Count);
    public bool ShowPackChecked => MultiSelect && !IsBatching;
    public string BatchTitle => Loc.F("Batch.Running", BatchProgress);

    public string PackCheckedTip
    {
        get
        {
            if (IsBatching) return Loc.T("Tip.BatchBusy");
            if (IsRunning) return Loc.T("Tip.BatchRunning");
            var names = Mods.Where(m => m.IsChecked).Select(m => m.Name).ToList();
            if (names.Count == 0) return Loc.T("Tip.BatchNone");
            return Loc.F("Tip.Batch", string.Join(", ", names), OptionsLine(CurrentOptions()));
        }
    }

    // The options every mod of the batch shares, as one line.
    private string OptionsLine(PipelineOptions o)
    {
        var srv = Servers.FirstOrDefault(s => s.Id == o.Server)?.Name;
        return Loc.F("Batch.Opts", o.Preset, Loc.T(o.Sign ? "Batch.On" : "Batch.Off"),
            o.Deploy && srv != null ? srv : Loc.T("Batch.Off"), Loc.T(o.Host ? "Batch.On" : "Batch.Off"));
    }

    public string BatchProgress => _batch == null ? "" : Loc.F("Batch.Progress", Math.Min(_batch.Index + 1, _batch.Mods.Count), _batch.Mods.Count);
    public string BatchModName => _batch == null || _batch.Index >= _batch.Mods.Count ? "" : _batch.Mods[_batch.Index].Name;
    public double BatchFraction => _batch == null ? 0 : (double)_batch.Rows.Count / _batch.Mods.Count;
    public string RunModLine => _batch == null ? SelectedMod?.Name ?? "" : BatchModName + "  ·  " + BatchProgress;
    public string CancelTip => Loc.T(IsBatching ? "Tip.CancelBatch" : "Tip.Cancel");

    public bool IsQueued(ModVM m) => _batch != null && !_batch.Stopped
        && _batch.Mods.Skip(_batch.Index + 1).Contains(m) && RunningModId != m.Id;

    private void SetChecks(bool on)
    {
        foreach (var m in Mods) m.IsChecked = on;
        RaiseChecks();
    }

    private void RaiseChecks()
    {
        foreach (var n in new[] { nameof(CheckedCount), nameof(CheckedText), nameof(PackCheckedText), nameof(PackCheckedTip), nameof(MultiTip), nameof(ModsCountText), nameof(ShowPackChecked) })
            Raise(n);
        CommandManager.InvalidateRequerySuggested();
    }

    // A click on a mod in the list while ticking: Ctrl or the tick mode toggles one, Shift ticks the range
    // from the last one clicked. Ctrl/Shift outside the tick mode switch it on and take the open mod along.
    public void CheckClick(ModVM m, bool range)
    {
        if (!MultiSelect)
        {
            MultiSelect = true;
            _anchor = SelectedMod;
            if (SelectedMod != null && SelectedMod != m) SelectedMod.IsChecked = true;
        }
        var a = _anchor != null ? Mods.IndexOf(_anchor) : -1;
        if (range && a >= 0)
        {
            var b = Mods.IndexOf(m);
            for (var i = Math.Min(a, b); i <= Math.Max(a, b); i++) Mods[i].IsChecked = true;
        }
        else m.IsChecked = !m.IsChecked;
        _anchor = m;
        RaiseChecks();
    }

    private void RaiseBatch()
    {
        foreach (var n in new[] { nameof(IsBatching), nameof(BatchProgress), nameof(BatchTitle), nameof(BatchModName), nameof(BatchFraction), nameof(RunModLine), nameof(CancelTip), nameof(PackCheckedTip), nameof(ShowPackChecked) })
            Raise(n);
        RaiseRunState();
    }

    private Task StopAsync()
    {
        if (_batch != null) _batch.Stopped = true;
        return _current is { Status: "running" } c ? _backend.CancelAsync(c.RunId) : Task.CompletedTask;
    }

    // A batch run is over (done, failed or cancelled); message is set when it failed before it started.
    private void Ended(string runId, string? message = null)
    {
        if (_waits.Remove(runId, out var t)) t.TrySetResult(message);
        else if (_batch != null) _ended[runId] = message;
    }

    private Task<string?> WaitRun(string runId)
    {
        if (_ended.Remove(runId, out var msg)) return Task.FromResult(msg);
        var t = new TaskCompletionSource<string?>();
        _waits[runId] = t;
        return t.Task;
    }

    public async Task StartBatchAsync()
    {
        var mods = Mods.Where(m => m.IsChecked).ToList();
        if (mods.Count == 0 || IsRunning) return;
        var opts = CurrentOptions();
        var b = new Batch(mods, opts);
        _batch = b;
        _ended.Clear();
        ResultOpen = false;
        Page = "build";
        for (b.Index = 0; b.Index < mods.Count; b.Index++)
        {
            var m = mods[b.Index];
            RaiseBatch();
            if (b.Stopped)
            {
                b.Rows.Add(new BatchRowVM { ModId = m.Id, Name = m.Name, Kind = "skip", Text = Loc.T("Batch.RowSkip") });
                continue;
            }
            string? failed;
            try
            {
                _pending = ("batch", m.Id, opts);
                failed = await WaitRun(await _backend.RunPipelineAsync(m.Id, opts));
            }
            catch (Exception e) { failed = e.Message; }
            if (failed != null)
                b.Rows.Add(new BatchRowVM { ModId = m.Id, Name = m.Name, Kind = "err", Text = Loc.F("Batch.RowErr", Loc.T("Step.env"), failed) });
        }
        _batch = null;
        _ended.Clear();
        RaiseBatch();
        foreach (var m in Mods) m.RaiseAll();
        ShowBatchResult(b);
        await Task.Delay(700);
        if (_batch == null && Result is { IsBatch: true }) ResultOpen = true;
    }

    private void ShowBatchResult(Batch b)
    {
        var ok = b.Rows.Count(r => r.Kind == "ok");
        var bad = b.Rows.Count(r => r.Kind == "err");
        var total = b.Mods.Count;
        var line = LaunchLine(b.Rows.Where(r => r.Kind == "ok").Select(r => r.ModLine));
        Result = new ResultVM
        {
            Ok = ok == total,
            IsBatch = true,
            ModName = Loc.T("Batch.Name"),
            Title = ok == total ? Loc.F("Batch.OkTitle", total) : Loc.F("Batch.PartTitle", ok, total),
            Text = Loc.F("Batch.Text", OptionsLine(b.Options)),
            Rows = b.Rows.ToList(),
            ModLine = line,
            CmdLine = ("DayZServer_x64.exe -config=serverDZ.cfg -port=2302 -profiles=profiles " + line).TrimEnd(),
            Stats = Loc.F("Batch.Stats", Loc.Sec(b.Clock.ElapsedMilliseconds), ok, bad, total - ok - bad),
        };
        if (Settings.CopyLaunchLine && line.Length > 0) TrySetClipboard(line);
        Toast(ok == total ? "ok" : ok > 0 ? "warn" : "err", Result.Title, line.Length > 0 ? line : Loc.T("Batch.NoLine"));
    }

    // The launch switches of the packed mods merged into one -mod= and one -serverMod= list.
    private static string LaunchLine(IEnumerable<string> lines)
    {
        var keys = new List<string>();
        var values = new Dictionary<string, List<string>>();
        foreach (var l in lines)
        {
            var eq = l.IndexOf('=');
            if (eq <= 0) continue;
            var key = l[..eq].Trim('"');
            var value = l[(eq + 1)..].Trim('"');
            if (!values.TryGetValue(key, out var list))
            {
                keys.Add(key);
                values[key] = list = new List<string>();
            }
            if (!list.Contains(value, StringComparer.OrdinalIgnoreCase)) list.Add(value);
        }
        return string.Join(" ", keys.Select(k => ServerMods.Token(k, string.Join(";", values[k]))));
    }

    private void OnStarted(PipelineStarted e)
    {
        var p = _pending ?? ("main", SelectedMod?.Id ?? "", CurrentOptions());
        _pending = null;
        var run = new Run { RunId = e.RunId, Origin = p.Origin, ModId = p.ModId, Options = p.Options };
        _runs[e.RunId] = run;
        if (run.Origin == "card") return;
        _current = run;
        Log.Clear();
        foreach (var s in Steps)
        {
            s.Status = StepStatus.Idle;
            s.Ms = 0;
            s.IsOpen = false;
            s.Lines.Clear();
        }
        SelectedMod = Mods.FirstOrDefault(m => m.Id == run.ModId) ?? SelectedMod;
        ShowJournal = true;
        RaiseRunState();
    }

    private void OnStep(PipelineStep e)
    {
        if (!_runs.TryGetValue(e.RunId, out var run) || run != _current) return;
        var s = Steps.First(x => x.Id == e.Step);
        s.Status = e.Status;
        s.Ms = e.Ms;
        if (e.Status == StepStatus.Running)
        {
            foreach (var o in Steps.Where(o => o.AutoOpened && o != s)) { o.IsOpen = false; o.AutoOpened = false; }
            if (!s.IsOpen) { s.IsOpen = true; s.AutoOpened = true; }
        }
        else if (e.Status == StepStatus.Done && s.AutoOpened)
        {
            s.IsOpen = false;
            s.AutoOpened = false;
        }
        RaiseRunState();
    }

    private void OnLog(PipelineLog e)
    {
        if (!_runs.TryGetValue(e.RunId, out var run) || run != _current) return;
        var line = new LogLineVM(run.Clock.ElapsedMilliseconds, e.Step, e.Line, e.Level);
        Log.Add(line);
        var s = Steps.First(x => x.Id == e.Step);
        s.Lines.Add(line);
        if (s.Lines.Count > 14) s.Lines.RemoveAt(0);
        Raise(nameof(WarnCount));
        Raise(nameof(ErrCount));
        Raise(nameof(LinesText));
        Raise(nameof(LogEmpty));
        LogAppended?.Invoke();
    }

    public event Action? LogAppended;

    private async Task OnDone(PipelineDone e)
    {
        if (!_runs.TryGetValue(e.RunId, out var run)) return;
        run.Status = "done";
        run.Ms = e.Ms;
        await RefreshAsync();
        var mod = Mods.FirstOrDefault(m => m.Id == run.ModId);
        if (run.Origin == "card")
        {
            if (mod != null) mod.IsBusy = false;
            Toast("ok", Loc.F("T.Deployed", mod?.Name), e.ServerDir);
            RaiseRunState();
            return;
        }
        if (run.Origin == "batch")
        {
            _batch?.Rows.Add(new BatchRowVM
            {
                ModId = run.ModId, Name = mod?.Name ?? "", Kind = "ok", Dir = e.ModDir, ModLine = e.ModLine,
                Text = e.Duplicates is { Count: > 0 } dup ? Loc.F("Batch.RowDup", e.ModDir, dup.Count) : e.ModDir,
            });
            RaiseRunState();
            Ended(run.RunId);
            return;
        }
        var srv = Servers.FirstOrDefault(s => s.Id == run.Options.Server);
        var done = Steps.Count(s => s.Status == StepStatus.Done);
        var onServer = !string.IsNullOrEmpty(e.ServerDir);
        var grafted = mod?.BuildsPlugin ?? true;
        string title, text;
        if (grafted)
        {
            title = Loc.T(onServer ? "Res.OkTitle" : "Res.OkTitleLocal");
            text = onServer
                ? Loc.F("Res.OkSub", mod?.Name, mod?.Version, run.Options.Sign ? mod?.Key : Loc.T("Info.Unsigned"), srv?.Name)
                : Loc.F("Res.OkSubLocal", mod?.Name, e.ModDir);
        }
        else
        {
            title = Loc.T("Res.OkTitlePack");
            text = (run.Options.Sign ? Loc.F("Res.PackSigned", mod?.Name, e.ModDir, mod?.Key) : Loc.F("Res.PackUnsigned", mod?.Name, e.ModDir))
                   + " " + Loc.T(mod?.IsClient == true ? "Res.TailClient" : "Res.TailServer");
        }
        Result = new ResultVM
        {
            Ok = true,
            ModId = run.ModId,
            ModName = mod?.Name ?? "",
            Title = title,
            Text = text,
            ModLine = e.ModLine,
            Warning = e.Duplicates is { Count: > 0 } d ? Loc.F("Res.Duplicate", string.Join(Environment.NewLine, d)) : "",
            CmdLine = e.CmdLine,
            Stats = Loc.F("Res.Stats", Loc.Sec(e.Ms), Loc.Kb(e.PboBytes), done),
            ModDir = e.ModDir,
            ServerDir = e.ServerDir,
            Deployed = onServer,
        };
        if (Settings.CopyLaunchLine && e.ModLine.Length > 0) TrySetClipboard(e.ModLine);
        Toast("ok", Loc.F("T.Done", mod?.Name, Loc.Sec(e.Ms)), e.ModLine.Length > 0 ? e.ModLine : e.ModDir);
        RaiseRunState();
        await Task.Delay(700);
        if (_current == run) ResultOpen = true;
    }

    private async Task OnFailed(PipelineError e)
    {
        if (!_runs.TryGetValue(e.RunId, out var run))
        {
            // Failed before it started (the mod is gone): a batch waiting on it goes on.
            Ended(e.RunId, e.Message);
            return;
        }
        run.Status = "error";
        run.ErrStep = e.Step;
        run.Ms = run.Clock.ElapsedMilliseconds;
        await RefreshAsync();
        var mod = Mods.FirstOrDefault(m => m.Id == run.ModId);
        if (run.Origin == "card")
        {
            if (mod != null) mod.IsBusy = false;
            Toast("err", Loc.F("T.Err", Loc.T("Step." + e.Step)), e.Message);
            RaiseRunState();
            return;
        }
        var stepTitle = Loc.T("Step." + e.Step);
        var s = Steps.First(x => x.Id == e.Step);
        s.IsOpen = true;
        s.AutoOpened = false;
        if (run.Origin == "batch")
        {
            _batch?.Rows.Add(new BatchRowVM
            {
                ModId = run.ModId, Name = mod?.Name ?? "", Kind = "err",
                Text = Loc.F("Batch.RowErr", stepTitle, e.Message), Error = ErrorResult(run, mod, e),
            });
            RaiseRunState();
            Ended(run.RunId);
            return;
        }
        Result = ErrorResult(run, mod, e);
        Toast("err", Loc.F("T.Err", stepTitle), e.Message);
        RaiseRunState();
        await Task.Delay(1000);
        if (_current == run) ResultOpen = true;
    }

    private ResultVM ErrorResult(Run run, ModVM? mod, PipelineError e)
    {
        var idx = Array.IndexOf(Core.Steps.Ids, e.Step);
        var stepTitle = Loc.T("Step." + e.Step);
        return new ResultVM
        {
            Ok = false,
            ModId = run.ModId,
            ModName = mod?.Name ?? "",
            Title = Loc.T("Res.ErrTitle"),
            Step = e.Step,
            StepAt = Loc.F("Res.ErrAt", idx + 1, stepTitle),
            Message = e.Message,
            Where = e.Where ?? "",
            Tail = e.LogTail.Select(l => new LogLineVM(l)).ToList(),
            Hint = e.Hint != null && Application.Current.TryFindResource(e.Hint) is string h ? h : e.Hint ?? "",
            FixCmd = e.FixCmd,
            RetryText = Loc.F("Act.Retry", stepTitle),
        };
    }

    private void OnCancelled(PipelineCancelled e)
    {
        if (!_runs.TryGetValue(e.RunId, out var run)) return;
        run.Status = "cancelled";
        run.Ms = run.Clock.ElapsedMilliseconds;
        foreach (var s in Steps.Where(s => s.Status == StepStatus.Running)) s.Status = StepStatus.Skipped;
        var mod = Mods.FirstOrDefault(m => m.Id == run.ModId);
        if (run.Origin == "card" && mod != null) mod.IsBusy = false;
        if (run.Origin == "batch")
        {
            if (_batch != null)
            {
                _batch.Stopped = true;
                _batch.Rows.Add(new BatchRowVM { ModId = run.ModId, Name = mod?.Name ?? "", Kind = "cancel", Text = Loc.T("Batch.RowCancel") });
            }
            RaiseRunState();
            Ended(run.RunId);
            return;
        }
        Toast("warn", Loc.T("T.Cancel"), mod?.Name ?? "");
        RaiseRunState();
    }

    private void OnTick()
    {
        if (IsRunning)
        {
            Raise(nameof(ElapsedText));
        }
        if (Page == "servers" && (DateTime.Now - _lastProbe).TotalSeconds > 3) _ = CheckServersAsync();
        if ((DateTime.Now - _lastAgo).TotalSeconds > 30)
        {
            _lastAgo = DateTime.Now;
            foreach (var m in Mods) m.RaiseAll();
        }
    }

    public async Task RecheckAsync(bool toast)
    {
        IsChecking = true;
        foreach (var d in Doctor) { d.Checking = true; d.RaiseAll(); }
        try
        {
            var items = await _backend.DoctorAsync();
            Doctor.Clear();
            foreach (var i in items) Doctor.Add(new DoctorVM(i));
            _checkedAt = DateTime.Now;
        }
        finally
        {
            IsChecking = false;
        }
        if (toast)
        {
            if (EnvIssues > 0) Toast("err", Loc.F("T.EnvBad", EnvIssues), "");
            else Toast("ok", Loc.T("T.EnvOk"), "");
        }
    }

    private async Task LaunchServerAsync(ServerVM? s)
    {
        if (s == null || !s.CanLaunch) return;
        s.IsStarting = true;
        s.LaunchedAt = DateTime.Now;
        try
        {
            var r = await _backend.LaunchServerAsync(s.Id, s.SelectedBat);
            s.Update(r.Server);
            s.ScriptPid = r.ScriptPid;
            Toast("ok", Loc.F("T.ServerStarted", r.Server.Name), System.IO.Path.Combine(r.Server.Path, r.Server.StartBat ?? ""));
        }
        catch (Exception e)
        {
            s.IsStarting = false;
            s.LaunchedAt = null;
            Toast("err", Loc.T("T.ServerFail"), e.Message);
            return;
        }
        // Watch closely until the server is up or the start is given up on.
        for (var i = 0; i < StartTimeout && s.IsStarting; i++)
        {
            await Task.Delay(1000);
            await CheckServersAsync();
        }
    }

    private const int StartTimeout = 45;

    private async Task StopServerAsync(ServerVM? s)
    {
        if (s == null || !s.CanStop) return;
        if (!s.StopArmed)
        {
            s.StopArmed = true;
            await Task.Delay(4000);
            s.StopArmed = false;
            return;
        }
        s.StopArmed = false;
        s.IsStopping = true;
        try
        {
            var r = await _backend.StopServerAsync(s.Id, s.ScriptPid);
            s.ScriptPid = 0;
            s.LaunchedAt = null;
            s.IsStarting = false;
            if (r.Forced) Toast("warn", Loc.F("T.ServerKilled", s.Name), Loc.T("T.ServerKilledSub"));
            else if (r.Servers > 0) Toast("ok", Loc.F("T.ServerStopped", s.Name), s.Path);
            else if (r.Scripts > 0) Toast("ok", Loc.F("T.ServerLoopStopped", s.Name), s.Path);
            else Toast("ok", Loc.F("T.ServerNotRunning", s.Name), s.Path);
        }
        catch (Exception e) { Toast("err", Loc.T("T.ServerStopFail"), Loc.F("T.ServerStopFailSub", e.Message)); }
        finally { s.IsStopping = false; }
        await CheckServersAsync();
    }

    // Which registered servers are up right now; polled only while the Servers page is open.
    private bool _probing;
    private DateTime _lastProbe;

    private async Task CheckServersAsync()
    {
        if (_probing) return;
        _probing = true;
        _lastProbe = DateTime.Now;
        try
        {
            var items = Servers.Select(s => (s, s.Path, s.ScriptPid)).ToList();
            var up = await Task.Run(() => items.Select(x => (x.s, ServerLaunch.IsRunning(x.Path), ServerLaunch.IsAlive(x.ScriptPid))).ToList());
            var now = DateTime.Now;
            foreach (var (s, running, script) in up)
            {
                s.IsRunning = running;
                if (running) s.LaunchedAt = null;
                if (!script) s.ScriptPid = 0;
                // A restart-loop script between two server runs, or a start still on its way.
                s.IsStarting = !running && (script || (s.LaunchedAt is { } at && (now - at).TotalSeconds < StartTimeout));
            }
        }
        catch (Exception) { }
        finally { _probing = false; }
    }

    private async Task InstallHostAsync(ServerVM? s)
    {
        if (s == null) return;
        s.IsBusy = true;
        try
        {
            var info = await _backend.InstallHostAsync(s.Id);
            s.Update(info);
            Toast("ok", Loc.T("T.HostOk"), info.Path);
        }
        catch (Exception e) { Toast("err", Loc.T("T.HostErr"), e.Message); }
        finally { s.IsBusy = false; }
        if (s.HasReport) await CheckGraftAsync(s);
    }

    private async Task UninstallHostAsync(ServerVM s)
    {
        s.IsBusy = true;
        try
        {
            s.Update(await _backend.UninstallHostAsync(s.Id));
            Toast("ok", Loc.T("T.HostRemoved"), s.Path);
        }
        catch (Exception e) { Toast("err", Loc.T("T.HostErr"), e.Message); }
        finally { s.IsBusy = false; }
        if (s.HasReport) await CheckGraftAsync(s);
    }

    private async Task CreateCppAsync()
    {
        if (SelectedMod is not { } m) return;
        try
        {
            var info = await _backend.CreateCppProjectAsync(m.Id);
            m.Update(info);
            OptionsChanged();
            Toast("ok", Loc.T("T.CppCreated"), info.Cpp ?? "");
        }
        catch (Exception e) { Toast("err", Loc.T("T.CppErr"), e.Message); }
    }

    private async Task InstallPluginAsync(ServerVM? s)
    {
        if (s == null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = Loc.T("Gc.PluginPick"), Filter = Loc.T("Gc.PluginFilter") + "|*.dll" };
        if (dlg.ShowDialog() != true) return;
        s.IsBusy = true;
        try
        {
            var path = await _backend.InstallPluginAsync(s.Id, dlg.FileName);
            Toast("ok", Loc.T("T.PluginOk"), path);
        }
        catch (Exception e) { Toast("err", Loc.T("T.PluginErr"), e.Message); }
        finally { s.IsBusy = false; }
        await CheckGraftAsync(s);
    }

    private async Task CheckGraftAsync(ServerVM? s)
    {
        if (s == null || s.IsChecking) return;
        s.IsChecking = true;
        try
        {
            var r = await _backend.CheckGraftAsync(s.Id, s.SelectedBat);
            // No plugin on the server, but a Grafted mod with a C++ project here builds one.
            if (r.Items.Any(c => c.Key == "PluginsNone"))
                r = r with { Items = r.Items.Concat(Mods.Where(m => m.BuildsPlugin)
                    .Select(m => new GraftCheck("BuildPlugin", "info", [m.Name], "build", m.Id))).ToList() };
            s.SetReport(r);
        }
        catch (Exception e) { Toast("err", Loc.T("T.CheckErr"), e.Message); }
        finally { s.IsChecking = false; }
    }

    private async Task GraftActionAsync(GraftCheckVM? c)
    {
        if (c == null) return;
        switch (c.Check.Action)
        {
            case "install": await InstallHostAsync(c.Server); break;
            case "uninstall": await UninstallHostAsync(c.Server); break;
            case "mods": await OpenWorkshopAsync(c.Server); break;
            case "plugin": await InstallPluginAsync(c.Server); break;
            case "build" when Mods.FirstOrDefault(m => m.Id == c.Check.Target) is { } mod:
                SelectedMod = mod;
                SelectedServer = c.Server;
                Page = "build";
                break;
            case "open" when c.Check.Target != null: await _backend.OpenPathAsync(c.Check.Target); break;
        }
    }

    private async Task CreateKeyAsync()
    {
        var tag = new string((NewKeyTag ?? "").Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
        if (tag.Length == 0) return;
        await _backend.CreateKeyAsync(tag);
        NewKeyTag = "";
        await RefreshAsync();
        Toast("ok", Loc.F("T.Key", tag), $"{tag}.bikey · {tag}.biprivatekey");
    }

    private void Copy(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (TrySetClipboard(text)) Toast("ok", Loc.T("T.Copied"), text.Length > 80 ? text[..80] + "…" : text);
    }

    private static bool TrySetClipboard(string text)
    {
        for (var i = 0; i < 3; i++)
        {
            try { Clipboard.SetText(text); return true; }
            catch { Thread.Sleep(30); }
        }
        return false;
    }

    private void Browse(string? which)
    {
        if (which == null) return;
        var t = _settings.Tools;
        if (which == "KeyDir")
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = t.KeyDir };
            if (dlg.ShowDialog() == true) t.KeyDir = dlg.FolderName;
        }
        else if (which == "WorkshopDir")
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = Loc.T("Set.Workshop") };
            var cur = string.IsNullOrWhiteSpace(_settings.WorkshopDir) ? ServerMods.DefaultWorkshopDir() : _settings.WorkshopDir;
            if (System.IO.Directory.Exists(cur)) dlg.InitialDirectory = cur;
            if (dlg.ShowDialog() == true) _settings.WorkshopDir = dlg.FolderName;
        }
        else if (which is "ClientOut" or "ServerOut" or "GraftedOut")
        {
            var pk = _settings.Pack;
            var cur = which == "ClientOut" ? pk.ClientOut : which == "ServerOut" ? pk.ServerOut : pk.GraftedOut;
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = Loc.T("Set." + which) };
            if (System.IO.Directory.Exists(cur)) dlg.InitialDirectory = cur;
            if (dlg.ShowDialog() != true) return;
            if (which == "ClientOut") pk.ClientOut = dlg.FolderName;
            else if (which == "ServerOut") pk.ServerOut = dlg.FolderName;
            else pk.GraftedOut = dlg.FolderName;
            OptionsChanged();
        }
        else
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "exe / dll|*.exe;*.dll|*.*|*.*" };
            if (dlg.ShowDialog() != true) return;
            switch (which)
            {
                case "CMake": t.CMake = dlg.FileName; break;
                case "Graft": t.Graft = dlg.FileName; break;
                case "HostDll": t.HostDll = dlg.FileName; break;
            }
        }
        Raise(nameof(Settings));
    }

    public void Toast(string kind, string title, string sub)
    {
        var t = new ToastVM { Kind = kind, Title = title, Sub = sub };
        Toasts.Add(t);
        while (Toasts.Count > 4) Toasts.RemoveAt(0);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += (_, _) => { timer.Stop(); Toasts.Remove(t); };
        timer.Start();
    }
}
