using System.Collections.ObjectModel;
using PolyLab.PBOGPacker.Core;

namespace PolyLab.PBOGPacker.ViewModels;

public sealed class ModVM : ObservableObject
{
    private readonly MainViewModel _root;
    private bool _busy;

    public ModVM(ModInfo info, MainViewModel root)
    {
        Info = info;
        _root = root;
    }

    public ModInfo Info { get; private set; }

    public void Update(ModInfo info)
    {
        Info = info;
        _canGraft = null;
        RaiseAll();
    }

    private bool? _canGraft;

    public string Id => Info.Id;
    public string Name => Info.Name;
    public string NameUpper => Info.Name.ToUpperInvariant();
    public string Version => "v" + Info.Version;
    public string Path => Info.Path;
    public string Modules => string.Join(" · ", Info.Modules);
    public string Key => Info.Key;
    public string Kind => Info.Kind;
    public bool IsGrafted => Info.Kind == ModKinds.Grafted;
    public bool IsClient => Info.Kind == ModKinds.Client;
    // A C++ project to build a plugin from; demo mods (no folder on disk) count as projects.
    public bool HasProject => _canGraft ??= PackLayout.HasProject(Info) || (IsGrafted && !System.IO.Directory.Exists(Info.Path));
    public bool CanCreateCpp => !PackLayout.HasProject(Info) && System.IO.Directory.Exists(Info.Path);
    public string? CppDir => PackLayout.HasProject(Info) ? PackLayout.ProjectDir(Info) : null;
    public bool BuildsPlugin => IsGrafted && HasProject;
    public string KindText => Loc.T("Kind." + Info.Kind);
    public string KindTag => Loc.T("KindTag." + Info.Kind);
    public string Dest => PackLayout.OutRoot(Info, _root.Settings.Pack);
    public string DestLeaf => System.IO.Path.GetFileName(Dest);
    public string? Logo => Info.Logo;
    public bool HasLogo => !string.IsNullOrEmpty(Info.Logo);

    public string Abbrev
    {
        get
        {
            var n = Info.Name.StartsWith("CG_", StringComparison.OrdinalIgnoreCase) ? Info.Name[3..] : Info.Name;
            var caps = new string(n.Where(char.IsUpper).Take(2).ToArray());
            return caps.Length >= 2 ? caps : n[..Math.Min(2, n.Length)].ToUpperInvariant();
        }
    }

    public bool IsBusy { get => _busy; set { if (Set(ref _busy, value)) Raise2(); } }

    // Ticked for packing several mods in one go.
    private bool _checked;
    public bool IsChecked { get => _checked; set => Set(ref _checked, value); }
    public bool IsQueued => _root.IsQueued(this);

    public void Raise2()
    {
        Raise(nameof(IsRunning));
        Raise(nameof(IsQueued));
        Raise(nameof(StatusKind));
        Raise(nameof(StatusText));
        Raise(nameof(CanDeploy));
        Raise(nameof(DeployTip));
    }
    public bool IsRunning => _root.RunningModId == Id;
    public bool CanDeploy => Info.Build.State == "ok" && !IsBusy && !_root.IsRunning;

    // Deploy re-runs only the deploy and host steps on the last build, so it needs one.
    public string DeployTip
    {
        get
        {
            if (IsBusy) return Loc.T("Tip.DeployBusy");
            if (_root.IsRunning) return Loc.T("Tip.DeployRunning");
            if (Info.Build.State != "ok") return Loc.T("Tip.DeployNoBuild");
            var srv = _root.SelectedServer;
            return srv == null ? Loc.T("Tip.DeployNoServer") : Loc.F("Tip.Deploy", Info.Name, srv.Name);
        }
    }

    public string StatusKind
    {
        get
        {
            if (IsRunning || IsBusy) return "run";
            if (IsQueued) return "queue";
            if (Info.Build.State == "fail") return "err";
            if (Info.Build.State == "none") return "idle";
            return Info.Deploy.State == "ok" ? "ok" : "warn";
        }
    }

    public string StatusText => StatusKind switch
    {
        "run" => Loc.T("Pill.Running"),
        "queue" => Loc.T("Pill.Queued"),
        "err" => Loc.T("Pill.Fail"),
        "idle" => Loc.T("Pill.New"),
        "ok" => Loc.T("Pill.Live"),
        _ => Loc.T("Pill.Pending"),
    };

    public string BuildKind => Info.Build.State switch { "ok" => "ok", "fail" => "err", _ => "idle" };

    public string BuildTitle => Info.Build.State switch
    {
        "ok" => Loc.F("B.OkTitle", Loc.Ago(Info.Build.At)),
        "fail" => Loc.F("B.FailTitle", Loc.T("Step." + (Info.Build.Step ?? "build"))),
        _ => Loc.T("B.NoneTitle"),
    };

    public string BuildText => Info.Build.State switch
    {
        "ok" => BuildsPlugin ? Loc.F("B.OkText", Loc.Sec(Info.Build.Ms), _root.Preset) : Loc.F("B.OkTextPack", Loc.Sec(Info.Build.Ms)),
        "fail" => Loc.F("B.FailText", Loc.Ago(Info.Build.At)),
        _ => Loc.T(BuildsPlugin ? "B.NoneText" : "B.NoneTextPack"),
    };

    public string SignKind => Info.Sign.State == "ok" ? "ok" : "idle";
    public string SignTitle => Info.Sign.State == "ok" ? Loc.F("S.OkTitle", Info.Sign.Key) : Loc.T("S.NoneTitle");

    public string SignText
    {
        get
        {
            if (Info.Sign.State != "ok") return Loc.T("S.NoneText");
            var fp = _root.Keys.FirstOrDefault(k => k.Tag == Info.Sign.Key)?.Fingerprint ?? "—";
            return Loc.F("S.OkText", fp);
        }
    }

    private string ServerNames => string.Join(", ", Info.Deploy.Servers.Select(id => _root.Servers.FirstOrDefault(s => s.Id == id)?.Name).Where(n => n != null));

    public string DeployKind => Info.Deploy.State switch { "ok" => "ok", "stale" => "warn", _ => "idle" };

    public string DeployTitle => Info.Deploy.State switch
    {
        "ok" => Loc.T("D.OkTitle"),
        "stale" => Loc.T("D.StaleTitle"),
        _ => Loc.T("D.NoneTitle"),
    };

    public string DeployText => Info.Deploy.State switch
    {
        "ok" => Loc.F("D.OkText", ServerNames, Loc.Ago(Info.Deploy.At)),
        "stale" => Loc.F("D.StaleText", ServerNames, Loc.Ago(Info.Deploy.At)),
        _ => Loc.T("D.NoneText"),
    };
}

public sealed class LogLineVM
{
    public LogLineVM(double t, string step, string text, LogLevel level)
    {
        Time = Loc.LogTime(t);
        StepId = step;
        Step = Loc.T("Step." + step);
        Text = text;
        Level = level switch
        {
            LogLevel.Cmd => "cmd",
            LogLevel.Ok => "ok",
            LogLevel.Warn => "warn",
            LogLevel.Err => "err",
            _ => "info",
        };
    }

    public LogLineVM(LogEntry e) : this(0, "env", e.Line, e.Level) { }

    public string Time { get; }
    public string StepId { get; }
    public string Step { get; }
    public string Text { get; }
    public string Level { get; }
    public string Icon => Level switch { "ok" => "check", "warn" => "warn", "err" => "x", "cmd" => "chevRight", _ => "" };
}

public sealed class StepVM : ObservableObject
{
    private readonly MainViewModel _root;
    private StepStatus _status = StepStatus.Idle;
    private long _ms;
    private bool _open;

    public StepVM(string id, int index, MainViewModel root)
    {
        Id = id;
        Index = index;
        _root = root;
    }

    public string Id { get; }
    public int Index { get; }
    public ObservableCollection<LogLineVM> Lines { get; } = new();

    public StepStatus Status
    {
        get => _status;
        set { if (Set(ref _status, value)) { Raise(nameof(Kind)); Raise(nameof(StatusText)); } }
    }

    public long Ms { get => _ms; set { if (Set(ref _ms, value)) Raise(nameof(MsText)); } }
    public bool IsOpen { get => _open; set => Set(ref _open, value); }
    public bool AutoOpened { get; set; }

    public string Title => Loc.T("Step." + Id);
    public string MsText => Ms > 0 ? Loc.Sec(Ms) : $"{Index + 1:00}/08";
    public string Tool => _root.StepTool(Id);
    public string Image => $"pack://application:,,,/PBOG_Packer;component/Assets/Photos/st_{Id}.jpg";

    public string Kind => EffectiveStatus switch
    {
        StepStatus.Running => "run",
        StepStatus.Done => "ok",
        StepStatus.Error => "err",
        StepStatus.Skipped => "skip",
        StepStatus.Off => "off",
        _ => "idle",
    };

    public StepStatus EffectiveStatus => Status == StepStatus.Idle && _root.IsStepOff(Id) ? StepStatus.Off : Status;
    public string StatusText => Loc.T("Ss." + EffectiveStatus);

    public void Touch()
    {
        Raise(nameof(Kind));
        Raise(nameof(StatusText));
        Raise(nameof(Tool));
        Raise(nameof(Title));
        Raise(nameof(MsText));
    }
}

public sealed class ServerVM : ObservableObject
{
    private readonly MainViewModel _root;
    private bool _busy;

    public ServerVM(ServerInfo info, MainViewModel root)
    {
        Info = info;
        _root = root;
    }

    public ServerInfo Info { get; private set; }

    public void Update(ServerInfo info)
    {
        Info = info;
        _scripts = null;
        RaiseAll();
    }

    private IReadOnlyList<string>? _scripts;
    private string? _bat;
    private bool _running;

    public IReadOnlyList<string> Scripts => _scripts ??= ServerLaunch.Scripts(Info.Path);
    public bool HasScripts => Scripts.Count > 0;
    public bool ManyScripts => Scripts.Count > 1;

    public string? SelectedBat
    {
        get => _bat != null && Scripts.Contains(_bat) ? _bat : ServerLaunch.Pick(Info);
        set { _bat = value; Raise(); }
    }

    public bool IsRunning
    {
        get => _running;
        set { if (Set(ref _running, value)) RaiseLaunch(); }
    }

    // From the click until DayZServer_x64.exe shows up (or the script window closes, or a timeout):
    // the button stays off so a double click or an impatient second click cannot start a second server.
    private bool _starting;
    public bool IsStarting
    {
        get => _starting;
        set { if (Set(ref _starting, value)) RaiseLaunch(); }
    }

    public int ScriptPid { get; set; }
    public DateTime? LaunchedAt { get; set; }

    public bool CanLaunch => HasScripts && !IsRunning && !IsStarting;
    public string StateKind => IsStopping ? "run" : IsRunning ? "ok" : IsStarting ? "run" : "idle";
    public string StateText => Loc.T(IsStopping ? "Srv.Stopping" : IsRunning ? "Srv.Running" : IsStarting ? "Srv.Starting" : "Srv.Stopped");

    // Stop takes two clicks: the first arms the button for a few seconds, the second stops the server.
    private bool _stopping, _armed;
    public bool IsStopping { get => _stopping; set { if (Set(ref _stopping, value)) RaiseLaunch(); } }
    public bool StopArmed { get => _armed; set { if (Set(ref _armed, value)) RaiseLaunch(); } }
    public bool ShowStop => IsRunning || IsStarting || IsStopping;
    public bool CanStop => ShowStop && !IsStopping;
    public string StopText => Loc.T(IsStopping ? "Act.StoppingServer" : StopArmed ? "Act.StopSure" : "Act.StopServer");
    public string StopTip => Loc.T(IsStopping ? "Srv.StoppingTip" : StopArmed ? "Srv.StopSureTip" : IsRunning ? "Srv.StopTip" : "Srv.StopLoopTip");
    public string LaunchTip => Loc.T(!HasScripts ? "Srv.NoBat" : IsRunning ? "Srv.AlreadyRunning" : IsStarting ? "Srv.Starting" : "Srv.LaunchTip");

    public void RaiseLaunch()
    {
        Raise(nameof(CanLaunch));
        Raise(nameof(StateKind));
        Raise(nameof(StateText));
        Raise(nameof(LaunchTip));
        Raise(nameof(ShowStop));
        Raise(nameof(CanStop));
        Raise(nameof(StopText));
        Raise(nameof(StopTip));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    public void Rescan()
    {
        _scripts = null;
        Raise(nameof(Scripts));
        Raise(nameof(HasScripts));
        Raise(nameof(ManyScripts));
        Raise(nameof(SelectedBat));
        RaiseLaunch();
    }

    public string Id => Info.Id;
    public string Name => Info.Name;
    public string Path => Info.Path;
    public string Build => Info.Build;
    public bool HostInstalled => Info.Host.Installed;
    private string HostState => Info.Host.State ?? (Info.Host.Installed ? "ok" : "none");
    public bool CanInstallHost => HostState is "none" or "other";
    public string InstallHostText => Loc.T(HostState == "other" ? "Act.UpdateHost" : "Act.InstallHost");
    public string InstallHostTip => Loc.T(HostState == "other" ? "Tip.UpdateHost" : "Tip.InstallHost");
    public bool IsBusy { get => _busy; set => Set(ref _busy, value); }
    public string VerifyKind => Info.VerifySignatures == 2 ? "ok" : "warn";
    public string VerifyText => Loc.F(Info.VerifySignatures == 2 ? "Srv.V2" : "Srv.V0", Info.VerifySignatures);
    public string HostKind => HostState switch { "ok" => "ok", "other" => "warn", _ => "err" };
    public string HostText => HostState switch
    {
        "ok" => Loc.F("Srv.HostOk", Info.Host.Version ?? GraftDoctor.Release),
        "other" => Loc.T("Srv.HostOther"),
        "foreign" => Loc.T("Srv.HostForeign"),
        _ => Loc.T("Srv.HostNo"),
    };

    // The last "Check GRAFT" of this server, shown under its card until closed.
    private bool _checking;
    private DateTime _reportAt;
    public ObservableCollection<GraftCheckVM> Report { get; } = new();
    public bool IsChecking { get => _checking; set { if (Set(ref _checking, value)) System.Windows.Input.CommandManager.InvalidateRequerySuggested(); } }
    public bool HasReport => Report.Count > 0;
    public string ReportKind => Report.Any(r => r.Kind == "err") ? "err" : Report.Any(r => r.Kind == "warn") ? "warn" : "ok";
    public string ReportTitle => Loc.F("Gc.Title", _reportAt.ToString("HH:mm:ss"));
    public string ReportSummary => Loc.F("Gc.Summary." + ReportKind,
        Report.Count(r => r.Kind == "err"), Report.Count(r => r.Kind == "warn"));

    public void SetReport(GraftReport r)
    {
        Update(r.Server);
        _reportAt = r.At;
        Report.Clear();
        foreach (var c in r.Items.OrderBy(c => c.State switch { "err" => 0, "warn" => 1, "ok" => 2, _ => 3 }))
            Report.Add(new GraftCheckVM(c, this));
        RaiseReport();
    }

    public void ClearReport()
    {
        Report.Clear();
        RaiseReport();
    }

    private void RaiseReport()
    {
        Raise(nameof(HasReport));
        Raise(nameof(ReportKind));
        Raise(nameof(ReportTitle));
        Raise(nameof(ReportSummary));
    }

    public IEnumerable<string> Mods => _root.Mods.Where(m => m.Info.Deploy.Servers.Contains(Id))
        .Select(m => "@" + m.Name + (m.Info.Deploy.State == "stale" ? " ⚠" : ""));

    public bool HasMods => Mods.Any();
}

public sealed class GraftCheckVM
{
    public GraftCheckVM(GraftCheck c, ServerVM server)
    {
        Check = c;
        Server = server;
    }

    public GraftCheck Check { get; }
    public ServerVM Server { get; }
    public string Kind => Check.State is "ok" or "warn" or "err" ? Check.State : "idle";
    public string Title => Loc.F("Gc." + Check.Key + ".T", Check.Args.Cast<object?>().ToArray());
    public string Detail => Loc.F("Gc." + Check.Key + ".D", Check.Args.Cast<object?>().ToArray());
    public bool HasAction => Check.Action != null;
    public string ActionText => Check.Action == null ? "" : Loc.T("Gc.Act." + Check.Action);
}

public sealed class KeyVM : ObservableObject
{
    private readonly MainViewModel _root;

    public KeyVM(KeyInfo info, MainViewModel root)
    {
        Info = info;
        _root = root;
    }

    public KeyInfo Info { get; }
    public string Tag => Info.Tag;
    public string Fingerprint => Info.Fingerprint;
    public string Created => Info.Created;
    public string Path => Info.Path;
    public IEnumerable<string> Signed => _root.Mods.Where(m => m.Info.Sign.State == "ok" && m.Info.Sign.Key == Tag).Select(m => "@" + m.Name);
    public bool HasSigned => Signed.Any();
}

public sealed class DoctorVM : ObservableObject
{
    public DoctorVM(DoctorItem item) => Item = item;

    public DoctorItem Item { get; }
    public bool Checking { get; set; }
    public string Name => Item.NameKey != null ? Loc.T(Item.NameKey) : Item.Name ?? Item.Id;
    public string Path => Item.Path;
    public string Version => Checking ? "" : Item.Version;
    public string Kind => Checking ? "run" : Item.State switch { CheckState.Ok => "ok", CheckState.Warn => "warn", _ => "err" };
    public string StateText => Loc.T("Env.St." + Kind switch { "ok" => "Ok", "warn" => "Warn", "err" => "Err", _ => "Run" });
    public bool HasFix => !Checking && Item.State != CheckState.Ok && Item.FixKey != null;
    public string FixText => Item.FixKey != null ? Loc.T(Item.FixKey) : "";
    public string? FixCmd => Item.FixCmd;
    public bool FixIsLink => Item.FixIsLink;
}

public sealed class ChoiceVM
{
    private readonly Action<string> _pick;

    public ChoiceVM(string tag, bool on, Action<string> pick)
    {
        Tag = tag;
        _on = on;
        _pick = pick;
    }

    private bool _on;
    public string Tag { get; }

    public bool IsOn
    {
        get => _on;
        set
        {
            _on = value;
            if (value) _pick(Tag);
        }
    }
}

public sealed class ToastVM
{
    public string Kind { get; init; } = "ok";
    public string Title { get; init; } = "";
    public string Sub { get; init; } = "";
}

public sealed class ResultVM
{
    public bool Ok { get; init; }
    public string ModId { get; init; } = "";
    public string ModName { get; init; } = "";
    public string Title { get; init; } = "";
    public string Text { get; init; } = "";
    public string ModLine { get; init; } = "";
    public bool HasModLine => ModLine.Length > 0;
    public string Warning { get; init; } = "";
    public bool HasWarning => Warning.Length > 0;
    public string CmdLine { get; init; } = "";
    public string Stats { get; init; } = "";
    public string ModDir { get; init; } = "";
    public string ServerDir { get; init; } = "";
    public bool Deployed { get; init; }
    public string Step { get; init; } = "";
    public string StepAt { get; init; } = "";
    public string Message { get; init; } = "";
    public string Where { get; init; } = "";
    public IReadOnlyList<LogLineVM> Tail { get; init; } = Array.Empty<LogLineVM>();
    public string Hint { get; init; } = "";
    public string? FixCmd { get; init; }
    public string RetryText { get; init; } = "";

    // A run of several mods: one row per mod instead of the single-mod panels.
    public bool IsBatch { get; init; }
    public IReadOnlyList<BatchRowVM> Rows { get; init; } = Array.Empty<BatchRowVM>();
    public bool ShowOk => Ok && !IsBatch;
    public bool ShowErr => !Ok && !IsBatch;
    public bool ShowDone => Ok || IsBatch;
    // The error of one mod opened from the batch summary leads back to it.
    public ResultVM? Back { get; set; }
    public bool HasBack => Back != null;
}

// One mod of a batch run. Kind: ok, err, cancel (stopped while it ran), skip (not started).
public sealed class BatchRowVM
{
    public string ModId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Kind { get; init; } = "ok";
    public string Text { get; init; } = "";
    public string Dir { get; init; } = "";
    public string ModLine { get; init; } = "";
    public ResultVM? Error { get; init; }
    public bool HasDir => Dir.Length > 0;
    public bool HasError => Error != null;
}

public sealed record PhotoCredit(string title, string author, string license, string licenseUrl, string page)
{
    public string Line => $"{title} · {author} · {license}";
}
