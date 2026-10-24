using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Pfx.App.Core;
using Pfx.App.XRay;
using Pfx.XRay;

namespace Pfx.App.Views;

public interface IImportHost
{
    AppSettings Settings { get; }
    XrayWorkspace Xray { get; }
    void Toast(string text, string kind);
    void ShowPlan(XrAnomalyPlan plan);
    void ImportPlan(XrAnomalyPlan plan);
    bool Imported(string tag, string ptype, string state);
    void SettingsChanged();
}

// The X-Ray folder import (board 7): point at any folder, the scan decides by content; the mods it found with their
// core and option folders, what it skipped and why, archives offered for unpacking (asked inline, never silently),
// the anomalies of an added source, and the saved sources in priority order.
public partial class ImportView : UserControl
{
    private IImportHost? _host;
    private XrScanResult? _scan;
    private bool _loading;
    private int _plansGeneration;
    // The index, source and language the anomaly list was made for (its rows are worded when made).
    private (XrIndex?, string?, string?) _plansFor;

    public ImportView()
    {
        InitializeComponent();
    }

    public void Init(IImportHost host)
    {
        _host = host;
        FolderBox.Text = host.Settings.LastScanFolder;
        host.Xray.Changed += Refresh;
        Refresh();
    }

    public XrMod? Mod => ModBox.SelectedItem is ComboBoxItem { Tag: XrMod m } ? m : null;
    public IReadOnlyList<RootRow> Roots => RootList.ItemsSource as IReadOnlyList<RootRow> ?? Array.Empty<RootRow>();
    public IReadOnlyList<AnomalyRow> Anomalies => AnomalyList.ItemsSource as IReadOnlyList<AnomalyRow> ?? Array.Empty<AnomalyRow>();
    public string TagText { get => TagBox.Text; set => TagBox.Text = value; }
    public string FolderText { get => FolderBox.Text; set => FolderBox.Text = value; }

    // ---- scan ----

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = Loc.T("Xr.Folder") };
        if (Directory.Exists(FolderBox.Text))
            dialog.InitialDirectory = FolderBox.Text;
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            FolderBox.Text = dialog.FolderName;
            ScanNow();
        }
    }

    private void OnFolderKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            ScanNow();
    }

    private void OnScan(object sender, RoutedEventArgs e) => ScanNow();

    private void OnCancel(object sender, RoutedEventArgs e) => _host?.Xray.Cancel();

    public bool ScanNow(Action<XrScanResult>? after = null)
    {
        if (_host == null)
            return false;
        string folder = FolderBox.Text.Trim().Trim('"');
        _host.Settings.LastScanFolder = folder;
        _host.SettingsChanged();
        return _host.Xray.Scan(folder, r =>
        {
            ShowScan(r);
            after?.Invoke(r);
        });
    }

    public void Refresh()
    {
        var x = _host?.Xray;
        if (x == null)
            return;
        ScanButton.IsEnabled = !x.Busy;
        CancelButton.IsEnabled = x.Busy;
        AddSourceButton.IsEnabled = !x.Busy && Mod != null && TagProblem.Visibility != Visibility.Visible;
        ImportButton.IsEnabled = !x.Busy && AnomalyList.SelectedItem is AnomalyRow;
        Bar.Value = x.Busy ? x.Progress : _scan != null ? 1 : 0;
        StatusText.Text = x.Busy ? $"{Loc.T(x.Job!)} · {x.ProgressText}"
            : _scan is { } s ? Loc.F("Xr.Status.Done", s.Files, s.Took.TotalSeconds, s.Reused) : x.IndexBuilding ? Loc.T("Xr.Status.Index") : "";
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource(x.Busy ? "B.Text" : "B.Ok");
        ShowSources();
        ShowAnomalies();
    }

    public void ShowScan(XrScanResult r)
    {
        _scan = r;
        int Kind(XrKind k) => r.Kinds.GetValueOrDefault(k);
        var rows = new List<KindRow>
        {
            Row("Xr.Kind.Particles", "Xr.How.Particles", $"{Kind(XrKind.Effect)} · {Kind(XrKind.Group)}", Kind(XrKind.Effect) + Kind(XrKind.Group)),
            Row("Xr.Kind.Library", "Xr.How.Library", Kind(XrKind.Library).ToString(), Kind(XrKind.Library)),
            Row("Xr.Kind.Lanims", "Xr.How.Lanims", Kind(XrKind.Lanims).ToString(), Kind(XrKind.Lanims)),
            Row("Xr.Kind.Configs", "Xr.How.Configs", Kind(XrKind.Config).ToString(), Kind(XrKind.Config)),
            Row("Xr.Kind.Sounds", "Xr.How.Sounds", Loc.F("Xr.OfAll", r.SoundsWithParams, Kind(XrKind.Sound)), Kind(XrKind.Sound)),
            Row("Xr.Kind.Textures", "Xr.How.Textures", Kind(XrKind.Texture).ToString(), Kind(XrKind.Texture)),
            Row("Xr.Kind.Archives", "Xr.How.Archives", r.Archives.Count.ToString(), r.Archives.Count),
        };
        KindList.ItemsSource = rows;
        SkipList.ItemsSource = r.Skipped.OrderByDescending(s => s.Value.Count).Select(s => new SkipRow
        {
            Reason = Loc.T("Xr.Skip." + s.Key),
            Count = s.Value.Count.ToString("N0"),
            Tip = Loc.T("Tip.Xr.Skip." + s.Key) + "\n" + string.Join("\n", s.Value.Examples),
        }).ToList();
        ArchiveList.ItemsSource = r.Archives.Select(a => new ArchiveRow
        {
            File = a, Name = Path.GetFileName(a), Target = Loc.F("Xr.UnpackTo", UnpackTarget(a)), Tip = a,
        }).ToList();
        NothingText.Visibility = r.Nothing ? Visibility.Visible : Visibility.Collapsed;
        string? keep = Mod?.Core.Path;
        _loading = true;
        ModBox.Items.Clear();
        foreach (var m in r.Mods.OrderByDescending(m => m.Roots.Sum(x => x.Effects)).ThenBy(m => m.Name))
            ModBox.Items.Add(new ComboBoxItem
            {
                Content = $"{m.Name}{(m.Guess.Tag.Length > 0 ? " · " + m.Guess.Tag : "")}", Tag = m,
                ToolTip = Loc.F("Tip.Xr.ModItem", m.Folder, m.Roots.Count),
            });
        _loading = false;
        int again = ModBox.Items.Cast<ComboBoxItem>().ToList().FindIndex(i => string.Equals(((XrMod)i.Tag).Core.Path, keep, StringComparison.OrdinalIgnoreCase));
        ModBox.SelectedIndex = again >= 0 ? again : ModBox.Items.Count > 0 ? 0 : -1;
        if (ModBox.Items.Count == 0)
            ShowMod(null);
        Refresh();
    }

    private static KindRow Row(string title, string how, string count, int n) => new()
    {
        Title = Loc.T(title), How = Loc.T(how), Count = count, Brush = n > 0 ? "B.TextHi" : "B.TextOff", Tip = Loc.T(how) + "\n" + Loc.T("Tip." + title),
    };

    private void OnMod(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading)
            ShowMod(Mod);
    }

    public void SelectMod(XrMod mod)
    {
        foreach (ComboBoxItem item in ModBox.Items)
            if (item.Tag == mod)
                ModBox.SelectedItem = item;
    }

    private XrSource? SavedSource(XrMod? mod) => mod == null ? null :
        _host?.Xray.Sources.Sources.FirstOrDefault(s => s.CoreRoot is { } c && string.Equals(c.Path, mod.Core.Path, StringComparison.OrdinalIgnoreCase));

    private void ShowMod(XrMod? mod)
    {
        if (_host == null)
            return;
        var saved = SavedSource(mod);
        RootList.ItemsSource = mod?.Roots.Select(r => new RootRow
        {
            Root = r,
            Name = r.Core ? Loc.F("Xr.Core", r.Name) : r.Name,
            Path = r.Path,
            Effects = r.Effects.ToString(),
            Overrides = r.Core ? "—" : r.OverridesCore > 0 ? Loc.F("Xr.OverFiles", r.OverridesCore) : Loc.T("Xr.OverNone"),
            OverridesBrush = r.Core || r.OverridesCore == 0 ? "B.TextOff" : "B.Warn",
            Tip = RootTip(r),
            Selected = r.Core || (saved?.Roots.FirstOrDefault(s => string.Equals(s.Path, r.Path, StringComparison.OrdinalIgnoreCase))?.Selected ?? false),
        }).ToList();
        RootCount.Text = mod == null ? "" : Loc.F("Xr.RootCount", mod.Roots.Count);
        GuessChip.Visibility = mod == null ? Visibility.Collapsed : Visibility.Visible;
        if (mod != null)
        {
            GuessText.Text = mod.Guess.Known != null ? Loc.F("Xr.Guess." + mod.Guess.Known, mod.Guess.Marker ?? "") : Loc.T("Xr.Guess.unknown");
            GuessChip.ToolTip = mod.Guess.Known != null ? Loc.F("Tip.Xr.Guess", mod.Guess.Marker ?? "") : Loc.T("Tip.Xr.GuessUnknown");
        }
        TagBox.Text = saved?.Tag ?? (mod != null ? _host.Xray.Sources.Propose(mod) : "");
        AddSourceButton.Content = Loc.T(saved != null ? "Xr.UpdateSource" : "Xr.AddSource");
        ShowSources();
        ShowAnomalies();
        Refresh();
    }

    private static string RootTip(XrFoundRoot r)
    {
        var lines = new List<string> { r.Path, Loc.F("Tip.Xr.RootCounts", r.Files, r.Effects) };
        if (!r.Core)
            lines.Add(r.OverridesCore > 0 ? Loc.F("Tip.Xr.RootOver", r.OverridesCore) : Loc.T("Tip.Xr.RootOverNone"));
        foreach (var (other, n) in r.Overlaps.OrderByDescending(o => o.Value).Take(6))
            lines.Add(Loc.F("Tip.Xr.RootOverlap", other, n));
        return string.Join("\n", lines);
    }

    private void OnRootToggle(object sender, RoutedEventArgs e)
    {
        if (_host == null || SavedSource(Mod) is not { } saved)
            return;
        foreach (var row in Roots)
            if (saved.Roots.FirstOrDefault(r => string.Equals(r.Path, row.Root.Path, StringComparison.OrdinalIgnoreCase)) is { } r)
                r.Selected = row.Selected || r.Core;
        _host.Xray.SourcesEdited();
    }

    // ---- source ----

    private void OnTag(object sender, TextChangedEventArgs e)
    {
        string? why = null;
        string tag = TagBox.Text.Trim();
        if (!XrSourceSet.ValidTag(tag))
            why = Loc.T("Xr.TagBad");
        else if (_host?.Xray.Sources.Source(tag) is { } other && other != SavedSource(Mod))
            why = Loc.F("Xr.TagTaken", other.Name);
        TagProblem.Text = why ?? "";
        TagProblem.Visibility = why != null ? Visibility.Visible : Visibility.Collapsed;
        Refresh();
    }

    private void OnAddSource(object sender, RoutedEventArgs e) => AddSource();

    public XrSource? AddSource(Action<XrIndex>? indexed = null)
    {
        if (_host == null || Mod is not { } mod || TagProblem.Visibility == Visibility.Visible)
            return null;
        var s = _host.Xray.Sources.Add(mod, TagBox.Text.Trim());
        foreach (var row in Roots)
            if (s.Roots.FirstOrDefault(r => string.Equals(r.Path, row.Root.Path, StringComparison.OrdinalIgnoreCase)) is { } r)
                r.Selected = row.Selected || r.Core;
        _host.Xray.SaveSources();
        _host.Xray.RebuildIndex(indexed);
        _host.Toast(Loc.F("Xr.Toast.SourceAdded", s.Tag, s.Name), "ok");
        ShowMod(mod);
        return s;
    }

    private void ShowSources()
    {
        var x = _host?.Xray;
        if (x == null)
            return;
        var list = x.Sources.Sources;
        SourceList.ItemsSource = list.Select(s => new SourceRow
        {
            Source = s, Title = s.Tag,
            Info = Loc.F("Xr.SourceInfo", s.Name, s.Roots.Count(r => r.Core || r.Selected), s.Roots.Count),
            Tip = string.Join("\n", s.Roots.Where(r => r.Core || r.Selected).Select(r => r.Path)),
        }).ToList();
        var saved = SavedSource(Mod);
        OrderText.Text = saved != null ? Loc.F("Xr.Order", list.IndexOf(saved) + 1, list.Count) : Loc.F("Xr.OrderNew", list.Count + 1);
    }

    private XrSource? RowSource(object sender) => ((FrameworkElement)sender).DataContext is SourceRow r ? r.Source : null;

    private void OnSourceUp(object sender, RoutedEventArgs e) => MoveSource(RowSource(sender), -1);
    private void OnSourceDown(object sender, RoutedEventArgs e) => MoveSource(RowSource(sender), 1);

    public void MoveSource(XrSource? s, int by)
    {
        if (_host == null || s == null)
            return;
        var list = _host.Xray.Sources.Sources;
        int i = list.IndexOf(s), j = Math.Clamp(i + by, 0, list.Count - 1);
        if (i < 0 || i == j)
            return;
        list.RemoveAt(i);
        list.Insert(j, s);
        _host.Xray.SourcesEdited();
    }

    private void OnSourceRemove(object sender, RoutedEventArgs e)
    {
        if (_host == null || RowSource(sender) is not { } s)
            return;
        _host.Xray.Sources.Sources.Remove(s);
        _host.Xray.SourcesEdited();
        _host.Toast(Loc.F("Xr.Toast.SourceRemoved", s.Tag), "info");
        ShowMod(Mod);
    }

    // ---- anomalies of the selected mod (once it is a source and the index holds it) ----

    private void ShowAnomalies()
    {
        var x = _host?.Xray;
        var saved = SavedSource(Mod);
        if (x == null)
            return;
        if (saved == null)
        {
            AnomalyHint.Text = Mod == null ? Loc.T("Xr.AnomalyNoMod") : Loc.T("Xr.AnomalyAddFirst");
            AnomalyList.ItemsSource = null;
            return;
        }
        if (x.Index == null || x.IndexBuilding || x.IndexSource(saved.Tag) is not { } src)
        {
            AnomalyHint.Text = Loc.T("Xr.Status.Index");
            return;
        }
        if (_plansFor == (x.Index, src.Tag, Loc.Lang))
            return;
        _plansFor = (x.Index, src.Tag, Loc.Lang);
        AnomalyList.ItemsSource = null;
        var index = x.Index;
        var subst = new Dictionary<string, string>(x.Substitutes);
        int generation = ++_plansGeneration;
        AnomalyHint.Text = Loc.T("Xr.AnomalyReading");
        var zones = index.Entries.Where(en => en.Kind == XrEntryKind.Zone && en.Providers.Any(p => p.Source == src)).Select(en => en.Key).OrderBy(k => k).ToList();
        var ui = TaskScheduler.FromCurrentSynchronizationContext();
        Task.Run(() => zones.Select(z => XrAnomalyPlanner.Plan(index, src, null, z, subst)).ToList()).ContinueWith(t =>
        {
            if (generation != _plansGeneration || t.Exception != null)
                return;
            var rows = t.Result.Select(AnomalyRowOf).ToList();
            AnomalyList.ItemsSource = rows;
            AnomalyHint.Text = rows.Count == 0 ? Loc.T("Xr.AnomalyNone") : Loc.F("Xr.AnomalyCount", rows.Count);
        }, ui);
    }

    private AnomalyRow AnomalyRowOf(XrAnomalyPlan p)
    {
        int groups = p.Particles.Count, missingTex = p.TexturesTotal - p.TexturesFound - p.Particles.SelectMany(x => x.Effects).Count(e => e.Texture is { File: null, Proposal: not null });
        var (status, brush) = groups == 0 ? (Loc.T("Xr.St.NoParticles"), "B.Info")
            : p.Particles.Any(x => x.Missing.Count > 0) || missingTex > 0 ? (Loc.F("Xr.St.Missing", Math.Max(missingTex, p.Particles.Sum(x => x.Missing.Count))), "B.Err")
            : ImportedAll(p) ? (Loc.T("Xr.St.Imported"), "B.TextDim") : (Loc.T("Xr.St.New"), "B.Ok");
        return new AnomalyRow
        {
            Plan = p, Name = p.Section,
            Info = Loc.F("Xr.AnomalyInfo", groups, p.TexturesFound, p.TexturesTotal, p.Sounds.Count(s => s.File != null)),
            Status = status, Brush = brush,
            Tip = string.Join("\n", p.Particles.Select(x => $"{Loc.T("Xr.Role." + x.Role)}: {x.Ref}").Concat(p.Sounds.Select(s => $"{Loc.T("Xr.Role." + s.Role)}: {s.Ref}"))),
        };
    }

    private bool ImportedAll(XrAnomalyPlan p) => _host != null && p.Particles.All(x => _host.Imported(p.Source.Tag, XrayNames.PType(p.Section), XrayNames.State(p.Source.Tag, x.Role)));

    private void OnAnomaly(object sender, SelectionChangedEventArgs e)
    {
        if (AnomalyList.SelectedItem is AnomalyRow r)
            _host?.ShowPlan(r.Plan);
        Refresh();
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        if (AnomalyList.SelectedItem is AnomalyRow r)
            _host?.ImportPlan(r.Plan);
    }

    // ---- archives ----

    private static string UnpackTarget(string archive) => Path.Combine(Path.GetDirectoryName(archive)!, "_unpacked");

    private void OnUnpackNo(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is ArchiveRow row)
            row.Asking = false;
    }

    // The first click names the target (inline, no dialog); the second one unpacks with the game's converter.exe.
    private void OnUnpack(object sender, RoutedEventArgs e)
    {
        if (_host == null || ((FrameworkElement)sender).DataContext is not ArchiveRow row)
            return;
        if (!row.Asking)
        {
            row.Asking = true;
            return;
        }
        row.Asking = false;
        string? converter = _host.Settings.XrConverter is { Length: > 0 } c && File.Exists(c) ? c : FindConverter(row.File);
        if (converter == null)
        {
            _host.Toast(Loc.T("Xr.Toast.NoConverter"), "warn");
            return;
        }
        string target = UnpackTarget(row.File);
        _host.Xray.Unpack(row.File, converter, target, done =>
        {
            _host.Toast(Loc.F("Xr.Toast.Unpacked", Path.GetFileName(row.File), done), "ok");
            FolderBox.Text = done;
            ScanNow();
        });
    }

    // The game ships converter.exe in tools\ next to its db\ folder (Anomaly 1.5.3).
    private static string? FindConverter(string archive)
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(archive)!); dir != null; dir = dir.Parent)
        {
            string c = Path.Combine(dir.FullName, "tools", "converter.exe");
            if (File.Exists(c))
                return c;
        }
        return null;
    }
}

// Default names of imported parts, as CG's manifest writes them: <type>_<tag>_<state>.ptc, the type from the zone
// section ("zone_mine_electric" -> "electric"), the state from the role ("blowout_particles" -> "<tag>_blowout").
public static class XrayNames
{
    public static string PType(string section)
    {
        string s = section.ToLowerInvariant();
        foreach (var p in new[] { "zone_", "mine_", "field_" })
            if (s.StartsWith(p, StringComparison.Ordinal))
                s = s[p.Length..];
        return XrSourceSet.Slug(s);
    }

    public static string State(string tag, string role) => $"{tag}_{role.Replace("_particles", "")}";
}
