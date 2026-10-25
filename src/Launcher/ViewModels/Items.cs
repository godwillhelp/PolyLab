using PolyLab.Launcher.Core;
using PolyLab.Ui.Core;

namespace PolyLab.Launcher.ViewModels;

public sealed class ToolCard(ToolInfo info) : ObservableObject
{
    public ToolInfo Info { get; } = info;
    public string Id => Info.Id;
    public string Name => Info.Name;
    public string Abbrev => Info.Abbrev;
    public string Image => "pack://application:,,,/PolyLab.Ui;component/Assets/Photos/" + Info.Photo;
    public string Desc => Loc.T("Tool." + Info.Id);

    private ToolManifest? _installed;
    public ToolManifest? Installed { get => _installed; set { if (Set(ref _installed, value)) RaiseState(); } }

    private Offer? _offer;
    public Offer? Offer { get => _offer; set { if (Set(ref _offer, value)) RaiseState(); } }

    // Why there is no offer: no release yet, no connection, GitHub's limit, or another GitHub failure (a Lang key).
    private string _missingKey = "Tool.NoRelease";
    public string MissingKey { get => _missingKey; set { if (Set(ref _missingKey, value)) RaiseState(); } }

    private bool _running;
    public bool Running { get => _running; set { if (Set(ref _running, value)) RaiseState(); } }

    private bool _busy;
    public bool Busy { get => _busy; set { if (Set(ref _busy, value)) RaiseState(); } }

    private double _progress;
    public double Progress { get => _progress; set => Set(ref _progress, value); }

    // False hides the card: the known release list has no public release of the tool (tools/visibility.ps1).
    private bool _listed = true;
    public bool Listed { get => _listed; set => Set(ref _listed, value); }

    public bool IsInstalled => Installed != null;
    public bool HasUpdate => Installed != null && Offer != null && SemVer.TryParse(Installed.Version, out var v) && Offer.Version.CompareTo(v) > 0;
    public bool Beta => Offer?.Beta == true;
    public bool ShowInstall => !Busy && Offer != null && (!IsInstalled || HasUpdate);
    public bool ShowLaunch => !Busy && IsInstalled && !HasUpdate;
    public bool CanLaunch => !Running;
    public bool ShowLaunchOld => !Busy && HasUpdate && !Running;
    // Not installed and nothing to install (offline, or no release yet): the button slot shows why.
    public bool ShowStatusOnly => !Busy && !IsInstalled && Offer == null;
    public bool ShowRemove => !Busy && IsInstalled && !Running;
    public string InstallText => Loc.T(HasUpdate ? "Tool.Update" : "Tool.Install");
    public string LaunchOldText => Loc.F("Tool.LaunchOld", Installed?.Version);
    public string VersionText => Installed != null ? "v" + Installed.Version : "";

    public string StatusKind => Busy || Running ? "run" : HasUpdate ? "warn" : IsInstalled ? "ok" : Offer == null && MissingKey != "Tool.NoRelease" ? "warn" : "idle";

    public string StatusText
    {
        get
        {
            if (Busy) return Loc.T("Tool.Installing");
            if (Running) return Loc.T("Tool.Running");
            if (HasUpdate) return Loc.F("Tool.UpdateTo", Offer!.Version);
            if (Installed != null) return Loc.F("Tool.Version", Installed.Version);
            if (Offer != null) return Loc.F("Tool.Available", Offer.Version);
            return Loc.T(MissingKey);
        }
    }

    private void RaiseState()
    {
        foreach (var n in new[] { nameof(IsInstalled), nameof(HasUpdate), nameof(Beta), nameof(ShowInstall), nameof(ShowLaunch),
                     nameof(CanLaunch), nameof(ShowLaunchOld), nameof(ShowStatusOnly), nameof(ShowRemove), nameof(InstallText), nameof(LaunchOldText), nameof(VersionText),
                     nameof(StatusKind), nameof(StatusText) })
            Raise(n);
    }

    public void Relocalize()
    {
        Raise(nameof(Desc));
        Raise(nameof(InstallText));
        Raise(nameof(LaunchOldText));
        Raise(nameof(StatusText));
    }
}

public sealed class NewsItem(GhRelease release, string? label, bool isNew) : ObservableObject
{
    public GhRelease Release { get; } = release;
    public string Title => Release.Name is { Length: > 0 } n ? n : Release.TagName;
    public string Body { get; } = Plain(release.Body);
    public bool HasBody => Body.Length > 0;
    public string Page => Release.HtmlUrl;
    public string Kind => label != null ? "ok" : "run";
    public string KindText => label ?? Loc.T("News.KindNews");

    public string TimeText
    {
        get
        {
            var t = (Release.PublishedAt ?? DateTimeOffset.MinValue).ToLocalTime();
            return t.Date == DateTime.Today ? Loc.F("News.Today", t.ToString("HH:mm"))
                : t.Date == DateTime.Today.AddDays(-1) ? Loc.F("News.Yesterday", t.ToString("HH:mm"))
                : t.ToString("dd.MM.yyyy HH:mm");
        }
    }

    private bool _isNew = isNew;
    public bool IsNew { get => _isNew; set => Set(ref _isNew, value); }

    public void Relocalize()
    {
        Raise(nameof(KindText));
        Raise(nameof(TimeText));
    }

    // Release notes are Markdown; the panel shows plain text: headings lose their #, list items get a bullet.
    private static string Plain(string? markdown)
    {
        var lines = (markdown ?? "").Replace("\r", "").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var l = lines[i];
            if (l.StartsWith('#')) lines[i] = l.TrimStart('#').TrimStart();
            else if (l.StartsWith("- ") || l.StartsWith("* ")) lines[i] = "• " + l[2..];
        }
        return string.Join("\n", lines).Trim();
    }
}
