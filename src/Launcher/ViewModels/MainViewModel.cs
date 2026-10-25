using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using PolyLab.Launcher.Core;
using PolyLab.Ui.Core;

namespace PolyLab.Launcher.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    // Two requests an hour against GitHub's anonymous 60; unchanged lists come back as free 304s anyway.
    private static readonly TimeSpan PollEvery = TimeSpan.FromMinutes(30);

    private readonly ReleasesClient _feed;
    private readonly Installer _installer;
    private readonly DispatcherTimer _poll = new() { Interval = PollEvery };
    private FeedResult? _last;
    private bool _refreshing;
    private bool _graftBusy;
    private string? _selfDismissed;
    private bool _demo;

    public MainViewModel()
    {
        var url = Environment.GetEnvironmentVariable(Catalog.ReleasesUrlEnv) is { Length: > 0 } u ? u : Catalog.ReleasesUrl;
        _feed = new ReleasesClient(Net.Http, url, Path.Combine(Paths.Data, "feed.json"));
        _installer = new Installer(Net.Http, Paths.Root);
        foreach (var t in Catalog.Tools) Tools.Add(new ToolCard(t));
        _poll.Tick += async (_, _) => await RefreshAsync(false);
        Loc.Changed += () =>
        {
            foreach (var t in Tools) t.Relocalize();
            foreach (var n in News) n.Relocalize();
            RaiseAll();
        };

        LaunchCmd = new RelayCommand(p => Launch((ToolCard)p!));
        InstallCmd = new RelayCommand(p => InstallAsync((ToolCard)p!));
        RemoveCmd = new RelayCommand(p => Remove((ToolCard)p!));
        CheckCmd = new RelayCommand(_ => RefreshAsync(true), _ => !Checking);
        GraftRetryCmd = new RelayCommand(_ => RetryGraftAsync(), _ => !_graftBusy);
        OpenCmd = new RelayCommand(p => Open((string)p!));
        SelfDismissCmd = new RelayCommand(_ => { _selfDismissed = SelfUpdate?.Version.ToString(); SelfUpdate = null; });
        DismissCmd = new RelayCommand(_ => BannerKind = "");
        NewsCmd = new RelayCommand(_ => NewsOpen = !NewsOpen);
        NewsCloseCmd = new RelayCommand(_ => NewsOpen = false);
        LangCmd = new RelayCommand(_ => SetLang(UiLanguage.Next(Loc.Lang)));
    }

    public ICommand LaunchCmd { get; }
    public ICommand InstallCmd { get; }
    public ICommand RemoveCmd { get; }
    public ICommand CheckCmd { get; }
    public ICommand GraftRetryCmd { get; }
    public ICommand OpenCmd { get; }
    public ICommand SelfDismissCmd { get; }
    public ICommand DismissCmd { get; }
    public ICommand NewsCmd { get; }
    public ICommand NewsCloseCmd { get; }
    public ICommand LangCmd { get; }

    public Settings Settings { get; } = Settings.Load();
    public ObservableCollection<ToolCard> Tools { get; } = new();
    public ObservableCollection<NewsItem> News { get; } = new();

    public static string AppVersion => typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    public string AppVersionText => "v" + AppVersion;

    private bool _checking;
    public bool Checking { get => _checking; set { if (Set(ref _checking, value)) CommandManager.InvalidateRequerySuggested(); } }

    public async Task InitAsync()
    {
        SetLang(Settings.Lang);
        RefreshLocal();
        await RefreshAsync(false);
        _poll.Start();
        NetworkChange.NetworkAvailabilityChanged += (_, e) =>
        {
            if (e.IsAvailable) Application.Current?.Dispatcher.BeginInvoke(() => _ = RefreshAsync(false));
        };
    }

    private void SetLang(string lang)
    {
        Loc.Set(lang);
        if (Settings.Lang == Loc.Lang || _demo) return;
        Settings.Lang = Loc.Lang;
        Settings.Save();
    }

    // What is on disk: installed manifests, and whether a tool's exe is running (also when started outside the launcher).
    private void RefreshLocal()
    {
        foreach (var c in Tools)
        {
            c.Installed = _installer.Installed(c.Id);
            if (!c.Busy) c.Running = c.IsInstalled && Installer.IsRunning(_installer.ExePath(c.Info));
        }
    }

    private async Task RefreshAsync(bool manual)
    {
        if (_refreshing) return;
        _refreshing = true;
        Checking = true;
        try
        {
            var r = await _feed.FetchAsync();
            ApplyFeed(r);
            RefreshLocal();
            // Rate limited: GitHub says when the window resets; asking earlier would only be refused again.
            _poll.Interval = r.State == FeedState.RateLimited && r.Reset is { } reset && reset > DateTimeOffset.UtcNow
                ? reset - DateTimeOffset.UtcNow : PollEvery;
            if (manual) ReportCheck(r);
            await EnsureGraftAsync();
        }
        finally
        {
            _refreshing = false;
            Checking = false;
        }
    }

    private void ApplyFeed(FeedResult r)
    {
        _last = r;
        var latest = Feed.Latest(r.Releases);
        var missing = r switch
        {
            { Live: true } or { Code: 404 } => "Tool.NoRelease",
            { State: FeedState.Offline } => "Tool.NoNet",
            { State: FeedState.RateLimited } => "Tool.Limit",
            _ => "Tool.NoData",
        };
        foreach (var c in Tools)
        {
            c.Offer = latest.GetValueOrDefault(c.Id);
            c.MissingKey = missing;
        }
        SelfUpdate = latest.GetValueOrDefault(Catalog.LauncherId) is { } l && SemVer.TryParse(AppVersion, out var mine)
            && l.Version.CompareTo(mine) > 0 && l.Version.ToString() != _selfDismissed ? l : null;
        RebuildNews(r.Releases);
        RaiseFoot();
    }

    private void ReportCheck(FeedResult r)
    {
        var updates = Tools.Where(t => t.HasUpdate).Select(t => $"{t.Name} {t.Offer!.Version}").ToList();
        if (SelfUpdate != null) updates.Insert(0, "Poly Lab " + SelfUpdate.Version);
        if (!r.Live) Banner("err", Loc.T("Upd.Failed"), FootText);
        else if (updates.Count == 0) Banner("ok", Loc.T("Upd.None"), Loc.F("Upd.NoneText", DateTime.Now.ToString("HH:mm")));
        else Banner("ok", Loc.F("Upd.Some", string.Join(", ", updates)), Loc.T("Upd.SomeText"));
    }

    // ── tools ───────────────────────────────────────────────────────────────────

    public Task InstallAsync(string id) =>
        Tools.FirstOrDefault(t => t.Id == id) is { ShowInstall: true } c ? InstallAsync(c) : Task.CompletedTask;

    private async Task InstallAsync(ToolCard c)
    {
        if (c.Busy || c.Offer is not { } offer) return;
        c.Busy = true;
        c.Progress = 0;
        try
        {
            var r = await _installer.InstallToolAsync(c.Info, offer, new Progress<double>(v => c.Progress = v));
            if (r.Ok) Banner("ok", Loc.F("Install.Done", c.Name, offer.Version));
            else Banner(r.Error == InstallError.Running ? "warn" : "err", Loc.F("Install.FailedTitle", c.Name), Describe(r, c.Name));
        }
        finally
        {
            c.Busy = false;
            RefreshLocal();
        }
        await EnsureGraftAsync();
    }

    private void Remove(ToolCard c)
    {
        if (c.Installed is not { } m || App.Shot
            || MessageBox.Show(Loc.F("Remove.Confirm", c.Name, m.Version), "Poly Lab", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        var r = _installer.Uninstall(c.Info);
        if (r.Ok) Banner("ok", Loc.F("Remove.Done", c.Name));
        else Banner(r.Error == InstallError.Running ? "warn" : "err", Loc.F("Remove.FailedTitle", c.Name), Describe(r, c.Name));
        RefreshLocal();
    }

    public void Launch(string id, IReadOnlyList<string> args)
    {
        if (Tools.FirstOrDefault(t => t.Id == id) is { } c) Launch(c, args);
    }

    // Tools are independent processes: closing the launcher leaves them running.
    private void Launch(ToolCard c, IReadOnlyList<string>? args = null)
    {
        if (c.Running || !c.IsInstalled) return;
        var exe = _installer.ExePath(c.Info);
        var psi = new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe)!, UseShellExecute = false };
        psi.Environment[Catalog.GraftDirEnv] = _installer.GraftDir;
        foreach (var a in args ?? []) psi.ArgumentList.Add(a);
        try
        {
            var p = Process.Start(psi) ?? throw new InvalidOperationException(exe);
            c.Running = true;
            p.EnableRaisingEvents = true;
            p.Exited += (_, _) => Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                c.Running = false;
                p.Dispose();
            });
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            Banner("err", Loc.F("Msg.LaunchFail", c.Name), e.Message);
        }
    }

    private static string Describe(InstallResult r, string name) => r.Error switch
    {
        InstallError.Running => Loc.F("Install.Running", name),
        InstallError.NoPackage => Loc.F("Install.NoPackage", r.Detail),
        InstallError.NoDigest => Loc.F("Install.NoDigest", r.Detail),
        InstallError.Digest => Loc.F("Install.Digest", r.Detail),
        InstallError.BadPackage => Loc.F("Install.BadPackage", r.Detail),
        InstallError.NotFound => Loc.F("Install.NotFound", r.Detail),
        InstallError.Download => Loc.F("Install.Download", r.Detail),
        _ => Loc.F("Install.Io", r.Detail),
    };

    private static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
        catch (Win32Exception) { }
    }

    // ── GRAFT ───────────────────────────────────────────────────────────────────
    // The version comes from the installed tools' tool.json (PBOG Packer pins the GRAFT it is built for), so a GRAFT
    // release with a new ABI never reaches users before a tool that supports it.

    private string? _graftPin;
    public string? GraftPin { get => _graftPin; set { if (Set(ref _graftPin, value)) RaiseGraft(); } }

    private string _graftState = "";
    public string GraftState { get => _graftState; set { if (Set(ref _graftState, value)) RaiseGraft(); } }

    private string _graftError = "";
    public string GraftError { get => _graftError; set { if (Set(ref _graftError, value)) RaiseGraft(); } }

    public bool HasGraft => GraftPin != null;
    public bool GraftFailed => GraftState == "err";
    public string GraftText => GraftPin == null ? "" : "GRAFT " + GraftPin + " · " + Loc.T(GraftState switch
    {
        "ok" => "Graft.Ok",
        "run" => "Graft.Run",
        _ => "Graft.Err",
    });
    public string GraftTip => GraftFailed ? Loc.T("Graft.Tip") + "\n\n" + GraftError : Loc.T("Graft.Tip");

    private void RaiseGraft()
    {
        foreach (var n in new[] { nameof(HasGraft), nameof(GraftFailed), nameof(GraftText), nameof(GraftTip) }) Raise(n);
    }

    private async Task EnsureGraftAsync()
    {
        string? pin = null;
        SemVer best = default;
        foreach (var c in Tools)
            if (c.Installed?.Graft is { } g && SemVer.TryParse(g, out var v) && (pin == null || v.CompareTo(best) > 0))
                (pin, best) = (g, v);
        GraftPin = pin;
        if (pin == null || _graftBusy) return;
        if (_installer.InstalledGraft() == pin)
        {
            GraftState = "ok";
            return;
        }
        var failedBefore = GraftFailed;
        _graftBusy = true;
        GraftState = "run";
        try
        {
            var r = await _installer.InstallGraftAsync(Catalog.GraftApi, pin);
            GraftError = r.Ok ? "" : Describe(r, "GRAFT");
            GraftState = r.Ok ? "ok" : "err";
            if (r.Ok) Banner("ok", Loc.F("Graft.Ready", pin));
            else if (!failedBefore) Banner("err", Loc.F("Graft.Failed", pin), GraftError);
        }
        finally
        {
            _graftBusy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    // A failure stays on the pill without a new banner on every poll; the retry button asks out loud again.
    private Task RetryGraftAsync()
    {
        GraftState = "";
        return EnsureGraftAsync();
    }

    // ── launcher update ─────────────────────────────────────────────────────────

    private Offer? _selfUpdate;
    public Offer? SelfUpdate
    {
        get => _selfUpdate;
        set
        {
            if (!Set(ref _selfUpdate, value)) return;
            Raise(nameof(HasSelfUpdate));
            Raise(nameof(SelfTitle));
        }
    }

    public bool HasSelfUpdate => SelfUpdate != null;
    public string SelfTitle => SelfUpdate == null ? "" : Loc.F("Self.Title", SelfUpdate.Version);

    // ── banner and footer ───────────────────────────────────────────────────────

    private string _bannerKind = "";
    public string BannerKind { get => _bannerKind; set { if (Set(ref _bannerKind, value)) Raise(nameof(HasBanner)); } }
    public bool HasBanner => BannerKind.Length > 0;

    private string _bannerTitle = "";
    public string BannerTitle { get => _bannerTitle; set => Set(ref _bannerTitle, value); }

    private string _bannerText = "";
    public string BannerText { get => _bannerText; set => Set(ref _bannerText, value); }

    private void Banner(string kind, string title, string text = "")
    {
        BannerKind = kind;
        BannerTitle = title;
        BannerText = text;
    }

    public void ShowError(Exception e) => Banner("err", Loc.T("Msg.Error"), e.Message);

    public string FootKind => _last?.State switch
    {
        FeedState.Fresh or FeedState.NotModified => "ok",
        FeedState.Offline or FeedState.RateLimited => "warn",
        FeedState.Failed => "err",
        _ => "idle",
    };

    public string FootText => _last switch
    {
        null => Loc.T("Foot.Never"),
        { Live: true, Fetched: { } t } => Loc.F("Foot.Ok", Time(t)),
        { State: FeedState.Offline, Fetched: { } t } => Loc.F("Foot.OfflineCache", Time(t)),
        { State: FeedState.Offline } => Loc.T("Foot.Offline"),
        { State: FeedState.RateLimited, Reset: { } t } => Loc.F("Foot.Limit", Time(t)),
        { Code: 404 } => Loc.T("Foot.NotFound"),
        _ => Loc.F("Foot.Failed", _last.Error),
    };

    public string FootTip => _last?.Remaining is { } n ? Loc.F("Foot.Rate", n) : Loc.T("Foot.RateUnknown");

    private void RaiseFoot()
    {
        Raise(nameof(FootKind));
        Raise(nameof(FootText));
        Raise(nameof(FootTip));
    }

    private static string Time(DateTimeOffset t)
    {
        var local = t.ToLocalTime();
        return local.Date == DateTime.Today ? local.ToString("HH:mm") : local.ToString("dd.MM HH:mm");
    }

    // ── news: release notes of the Poly Lab repo, newest at the bottom ─────────

    public event Action? NewsShown;

    private bool _newsOpen;
    public bool NewsOpen
    {
        get => _newsOpen;
        set
        {
            if (!Set(ref _newsOpen, value)) return;
            if (value)
            {
                MarkNewsRead();
                NewsShown?.Invoke();
            }
            else foreach (var n in News) n.IsNew = false;
        }
    }

    public bool NewsEmpty => News.Count == 0;
    public bool NewsStale => _last is { Live: false } && News.Count > 0;
    public int Unread => News.Count(n => IsUnread(n.Release));
    public bool HasUnread => Unread > 0;
    public string UnreadText => Unread > 9 ? "9+" : Unread.ToString();

    private bool IsUnread(GhRelease r) => Settings.NewsReadAt is not { } read || r.PublishedAt > read;

    private void RaiseNews()
    {
        foreach (var n in new[] { nameof(NewsEmpty), nameof(NewsStale), nameof(Unread), nameof(HasUnread), nameof(UnreadText) }) Raise(n);
    }

    private void RebuildNews(IReadOnlyList<GhRelease> releases)
    {
        var list = releases.Where(r => !r.Draft && r.PublishedAt != null).OrderBy(r => r.PublishedAt).ToList();
        var same = list.Count == News.Count && list.Zip(News, (r, n) => r.Id == n.Release.Id && r.Body == n.Release.Body && r.Name == n.Release.Name).All(x => x);
        if (!same)
        {
            var highlighted = News.Where(n => n.IsNew).Select(n => n.Release.Id).ToHashSet();
            News.Clear();
            foreach (var r in list) News.Add(new NewsItem(r, Label(r), IsUnread(r) || highlighted.Contains(r.Id)));
            if (NewsOpen)
            {
                MarkNewsRead();
                NewsShown?.Invoke();
            }
        }
        RaiseNews();
    }

    private static string? Label(GhRelease r)
    {
        if (!Feed.TryParseTag(r.TagName, out var id, out var v)) return null;
        if (id == Catalog.LauncherId) return "Poly Lab " + v;
        return Catalog.Find(id) is { } t ? t.Name + " " + v : null;
    }

    private void MarkNewsRead()
    {
        var max = News.Select(n => n.Release.PublishedAt).Max();
        if (max != null && (Settings.NewsReadAt == null || max > Settings.NewsReadAt))
        {
            Settings.NewsReadAt = max;
            if (!_demo) Settings.Save();
        }
        RaiseNews();
    }

    // ── --shot demo states ──────────────────────────────────────────────────────

    public void Demo(string state, string? news)
    {
        _demo = true;
        var now = DateTimeOffset.UtcNow;
        GhRelease Rel(long id, string tag, string name, double daysAgo, string body, bool pre = false) =>
            new(id, tag, name, body, false, pre, now.AddDays(-daysAgo), $"https://github.com/{Catalog.Repo}/releases/tag/{tag}", []);
        var releases = new List<GhRelease>
        {
            Rel(1, "launcher-v1.0.0", "Poly Lab 1.0.0", 12, "The launcher installs and updates the tools from GitHub Releases."),
            Rel(2, "pfx-v0.1.0", "Particle Studio 0.1.0", 9, "First public build.\n\n## Features\n- Emitter editor with curves and textures\n- Live preview in the game"),
            Rel(3, "pbog-v0.2.0", "PBOG Packer 0.2.0", 2, "## Changes\n- Batch packing of several mods\n- Stop the server from its card\n- Signing fixes"),
        };
        ToolManifest? pbog = null, pfx = null;
        var feed = new FeedResult(FeedState.Fresh, releases, now.AddMinutes(-3), 57, now.AddMinutes(41), 200);
        switch (state)
        {
            case "installed":
                pbog = new("pbog", "0.2.0", "PBOG_Packer.exe", "v0.4.0");
                pfx = new("pfx", "0.1.0", "Particle_Studio.exe");
                break;
            case "update":
                releases.Add(Rel(4, "launcher-v1.1.0", "Poly Lab 1.1.0", 0.1, "Remembers the window on every monitor."));
                pbog = new("pbog", "0.1.2", "PBOG_Packer.exe", "v0.4.0");
                pfx = new("pfx", "0.1.0", "Particle_Studio.exe");
                break;
            case "beta":
                releases.Add(Rel(4, "pfx-v0.2.0-beta.1", "Particle Studio 0.2.0 beta 1", 0.2, "Anomaly presets.\n\nPre-release: expect rough edges.", true));
                pfx = new("pfx", "0.1.0", "Particle_Studio.exe");
                break;
            case "offline":
                pbog = new("pbog", "0.2.0", "PBOG_Packer.exe", "v0.4.0");
                feed = new FeedResult(FeedState.Offline, [], null, Error: "No such host is known.");
                break;
        }
        Settings.NewsReadAt = now.AddDays(-5);
        ApplyFeed(feed);
        Tools[0].Installed = pbog;
        Tools[1].Installed = pfx;
        Tools[0].Running = state == "installed";
        GraftPin = pbog?.Graft;
        GraftState = pbog != null ? "ok" : "";
        if (news == "open") NewsOpen = true;
    }
}
