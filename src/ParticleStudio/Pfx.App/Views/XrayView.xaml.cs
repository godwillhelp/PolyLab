using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Pfx.App.XRay;
using Pfx.XRay;

namespace Pfx.App.Views;

public interface IXrayHost
{
    XrayWorkspace Xray { get; }
    void Toast(string text, string kind);
    bool ImportParts(IReadOnlyList<XrImportPart> parts, XrAnomalyPlan? plan);
    void ShowSubstitutes(IEnumerable<string> missing);
}

// The X-Ray panel: the index of every source (spec 3.9) searched by name, anomaly type or texture, with which source
// wins (first source, an option before its core, a loose file before the library); the details of the picked entry or
// the whole plan of a zone ("everything for this anomaly"), and the import of it.
public partial class XrayView : UserControl
{
    // A list of every texture and sound of base Anomaly runs to tens of thousands of rows; the list shows this many
    // and the count says how many matched.
    public const int ShownMax = 2000;
    private static readonly XrEntryKind[] Kinds = { XrEntryKind.Zone, XrEntryKind.Group, XrEntryKind.Effect, XrEntryKind.Texture, XrEntryKind.Sound, XrEntryKind.Lanim, XrEntryKind.Item };

    private IXrayHost? _host;
    private readonly HashSet<XrEntryKind> _kinds = new() { XrEntryKind.Zone, XrEntryKind.Group, XrEntryKind.Effect };
    private bool _loading;
    private XrAnomalyPlan? _plan;
    private XrEntry? _entry;

    public XrayView()
    {
        InitializeComponent();
    }

    public XrAnomalyPlan? Plan => _plan;
    public XrEntry? Entry => _entry;
    public IReadOnlyList<EntryRow> Results => ResultList.ItemsSource as IReadOnlyList<EntryRow> ?? Array.Empty<EntryRow>();
    public IReadOnlyList<DetailRow> Details => DetailList.ItemsSource as IReadOnlyList<DetailRow> ?? Array.Empty<DetailRow>();
    public string PType { get => PTypeBox.Text; set => PTypeBox.Text = value; }
    public string Query { get => SearchBox.Text; set => SearchBox.Text = value; }

    public void Init(IXrayHost host)
    {
        _host = host;
        host.Xray.Changed += () => Refresh();
        Rebuild();
    }

    // Controls whose words come from the language (rebuilt on a language change).
    public void Rebuild()
    {
        _loading = true;
        KindBar.Children.Clear();
        foreach (var k in Kinds)
        {
            var b = new ToggleButton { Style = (Style)FindResource("Seg"), Content = Loc.T("Xr.EK." + k), ToolTip = Loc.T("Tip.Xr.EK." + k), Tag = k, IsChecked = _kinds.Contains(k) };
            b.Click += OnKind;
            KindBar.Children.Add(b);
        }
        object? type = (TypeBox.SelectedItem as ComboBoxItem)?.Tag;
        TypeBox.Items.Clear();
        TypeBox.Items.Add(new ComboBoxItem { Content = Loc.T("Xr.Type.any"), Tag = null, ToolTip = Loc.T("Tip.Xr.TypeBox") });
        foreach (var t in XrTypes.Words.Keys)
            TypeBox.Items.Add(new ComboBoxItem { Content = Loc.T("Xr.Type." + t), Tag = t, ToolTip = string.Join(", ", XrTypes.Words[t]) });
        TypeBox.SelectedItem = TypeBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, type)) ?? TypeBox.Items[0];
        _loading = false;
        Refresh();
        if (_plan != null)
            ShowPlan(_plan);
        else if (_entry != null)
            ShowEntry(_entry);
    }

    private void OnKind(object sender, RoutedEventArgs e)
    {
        var b = (ToggleButton)sender;
        var k = (XrEntryKind)b.Tag;
        if (b.IsChecked == true)
            _kinds.Add(k);
        else
            _kinds.Remove(k);
        Refresh();
    }

    public void SetKinds(params XrEntryKind[] kinds)
    {
        _kinds.Clear();
        _kinds.UnionWith(kinds);
        foreach (ToggleButton b in KindBar.Children)
            b.IsChecked = _kinds.Contains((XrEntryKind)b.Tag);
        Refresh();
    }

    private void OnFilter(object sender, EventArgs e)
    {
        if (!_loading)
            Refresh();
    }

    private XrIndex? _shownIndex;
    private string _shownKey = "";

    public void Refresh()
    {
        var x = _host?.Xray;
        if (x == null)
            return;
        ImportButton.IsEnabled = !x.Busy && (_plan != null || _entry?.Kind is XrEntryKind.Effect or XrEntryKind.Group);
        SubstButton.IsEnabled = MissingTextures().Any();
        if (x.Index is not { } index)
        {
            ResultList.ItemsSource = null;
            CountText.Text = Loc.T(x.IndexBuilding ? "Xr.Status.Index" : "Xr.NoIndex");
            if (_plan == null && _entry == null)
                EmptyText.Text = Loc.T("Xr.NoIndex");
            return;
        }
        string type = (TypeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        string key = $"{Loc.Lang}|{SearchBox.Text}|{type}|{string.Join(",", _kinds.OrderBy(k => k))}";
        if (index == _shownIndex && key == _shownKey)
            return;
        _shownIndex = index;
        _shownKey = key;
        var found = index.Search(SearchBox.Text, _kinds, type.Length > 0 ? type : null).ToList();
        ResultList.ItemsSource = found.Take(ShownMax).Select(e => new EntryRow
        {
            Entry = e, Name = e.Name, Kind = Loc.T("Xr.EK1." + e.Kind), Where = string.Join(" ", e.Providers.Select(p => p.Source.Tag).Distinct()),
            Tip = string.Join("\n", e.Providers.Select((p, i) => (i == 0 ? "▶ " : "   ") + Provider(p))),
        }).ToList();
        CountText.Text = found.Count > ShownMax ? Loc.F("Xr.FoundMany", found.Count, ShownMax) : Loc.F("Xr.FoundN", found.Count);
        if (_plan == null && _entry == null)
            EmptyText.Text = Loc.T("Xr.PickHint");
    }

    private static string Provider(XrProvider p) => $"{p.Source.Tag}:{(p.Root.Core ? "core" : p.Root.Name)} {(p.Library ? Loc.T("Xr.FromLibrary") : Loc.T("Xr.Loose"))} — {p.File}";

    private void OnPick(object sender, SelectionChangedEventArgs e)
    {
        if (ResultList.SelectedItem is EntryRow r)
            Pick(r.Entry);
    }

    public void Pick(XrEntry entry)
    {
        if (_host?.Xray.Index is not { } index)
            return;
        if (entry.Kind == XrEntryKind.Zone)
        {
            var src = entry.Winner.Source;
            ShowPlan(XrAnomalyPlanner.Plan(index, src, null, entry.Key, _host.Xray.Substitutes));
            return;
        }
        ShowEntry(entry);
    }

    private void FillVariants(XrSource src, string? variant)
    {
        _loading = true;
        VariantBox.Items.Clear();
        foreach (var r in src.Roots.Where(r => r.Core || r.Selected))
            VariantBox.Items.Add(new ComboBoxItem { Content = r.Core ? Loc.T("Xr.CoreOnly") : r.Name, Tag = r.Id, ToolTip = r.Path });
        VariantBox.SelectedItem = VariantBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == (variant ?? "core")) ?? (VariantBox.Items.Count > 0 ? VariantBox.Items[0] : null);
        _loading = false;
    }

    public string Variant => (VariantBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "core";

    public void SetVariant(string id)
    {
        foreach (ComboBoxItem i in VariantBox.Items)
            if ((string)i.Tag == id)
                VariantBox.SelectedItem = i;
    }

    private void OnVariant(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _host?.Xray.Index is not { } index)
            return;
        if (_plan != null)
            ShowPlan(XrAnomalyPlanner.Plan(index, _plan.Source, Variant == "core" ? null : Variant, _plan.Section, _host.Xray.Substitutes), keepNames: true);
    }

    private static DetailRow H(string label) => new() { Label = label, Value = "", Header = true, Tip = label };
    private static DetailRow P(string label, string value, string brush = "B.Text", string tip = "") => new() { Label = label, Value = value, Brush = brush, Tip = tip.Length > 0 ? tip : value };

    public void ShowPlan(XrAnomalyPlan plan, bool keepNames = false)
    {
        _plan = plan;
        _entry = null;
        EmptyText.Text = "";
        if (!keepNames)
        {
            PTypeBox.Text = XrayNames.PType(plan.Section);
            FillVariants(plan.Source, plan.Variant);
        }
        var rows = new List<DetailRow> { H(Loc.F("Xr.Plan.Title", plan.Section, plan.Source.Tag)) };
        rows.Add(P(Loc.T("Xr.Plan.Type"), plan.Type is { } t ? Loc.T("Xr.Type." + t) : "—"));
        if (plan.MissingParents.Count > 0)
            rows.Add(P(Loc.T("Xr.Plan.MissingParents"), string.Join(", ", plan.MissingParents), "B.Warn", Loc.T("Tip.Xr.MissingParents")));
        foreach (var p in plan.Particles)
        {
            rows.Add(H($"{Loc.T("Xr.Role." + p.Role)} → {XrayNames.State(plan.Source.Tag, p.Role)}"));
            rows.Add(P(p.Group ? Loc.T("Xr.Plan.Group") : Loc.T("Xr.Plan.Effect"), p.Ref, p.File != null ? "B.TextHi" : "B.Err", p.File ?? Loc.T("Xr.NotFound")));
            foreach (var e in p.Effects)
            {
                string label = e.ChildOf != null ? "  ↳ " + Loc.T("Xr.Child." + e.ChildKind) : "  " + Loc.T("Xr.Plan.Item");
                string tex = e.Texture == null ? Loc.T("Xr.NoTexture") : e.Texture.File != null ? e.Texture.Name
                    : e.Texture.Proposal != null ? Loc.F(e.Texture.FromTable ? "Xr.TexSubst" : "Xr.TexProposal", e.Texture.Name, Path.GetFileName(e.Texture.Proposal)) : Loc.F("Xr.TexMissing", e.Texture.Name);
                string brush = e.Texture == null || e.Texture.File != null ? "B.Text" : e.Texture.Proposal != null ? "B.Warn" : "B.Err";
                rows.Add(P(label, $"{e.Name}\n{e.Shader} · {tex}", brush, $"{e.File}\n{e.Texture?.File ?? e.Texture?.Proposal ?? ""}"));
            }
            foreach (var m in p.Missing)
                rows.Add(P("  " + Loc.T("Xr.Plan.Item"), Loc.F("Xr.EffectMissing", m), "B.Err"));
        }
        if (plan.Sounds.Count > 0)
        {
            rows.Add(H(Loc.T("Xr.Plan.Sounds")));
            foreach (var s in plan.Sounds)
                rows.Add(P(Loc.T("Xr.Role." + s.Role), s.Params is { } sp ? Loc.F("Xr.SoundParams", s.Ref, sp.Min, sp.Range, sp.Volume) : s.File != null ? Loc.F("Xr.SoundNoParams", s.Ref) : Loc.F("Xr.SoundMissing", s.Ref),
                    s.File == null ? "B.Err" : "B.Text", s.File ?? ""));
        }
        foreach (var l in plan.Lights)
        {
            rows.Add(H(Loc.T("Xr.Role." + l.Role)));
            foreach (var (k, v) in l.Keys)
                rows.Add(P(k, v));
            if (l.Anim is { } a)
            {
                var m = a.Mean();
                rows.Add(P(Loc.T("Xr.Plan.Lanim"), Loc.F("Xr.LanimInfo", a.Name, a.Frames, a.Fps, m.R.ToString("0.###", CultureInfo.InvariantCulture),
                    m.G.ToString("0.###", CultureInfo.InvariantCulture), m.B.ToString("0.###", CultureInfo.InvariantCulture)), "B.Text", Loc.T("Tip.Xr.Lanim")));
            }
        }
        if (plan.Timings.Count > 0)
        {
            rows.Add(H(Loc.T("Xr.Plan.Timings")));
            foreach (var (k, v) in plan.Timings)
                rows.Add(P(k, v));
        }
        if (plan.Postprocess != null || plan.Hit.Count > 0)
        {
            rows.Add(H(Loc.T("Xr.Plan.Other")));
            if (plan.Postprocess != null)
                rows.Add(P("postprocess", plan.Postprocess, "B.Text", Loc.T("Tip.Xr.Postprocess")));
            foreach (var (k, v) in plan.Hit)
                rows.Add(P(k, v));
        }
        rows.Add(P(Loc.T("Xr.Plan.P10"), Loc.T("Xr.Plan.P10Text"), "B.TextDim", Loc.T("Tip.Xr.P10")));
        DetailList.ItemsSource = rows;
        Refresh();
    }

    public void ShowEntry(XrEntry e)
    {
        _entry = e;
        _plan = null;
        EmptyText.Text = "";
        var src = e.Winner.Source;
        if (e.Kind is XrEntryKind.Effect or XrEntryKind.Group)
        {
            PTypeBox.Text = "xray";
            FillVariants(src, e.Winner.Root.Core ? null : e.Winner.Root.Id);
        }
        var rows = new List<DetailRow> { H($"{Loc.T("Xr.EK1." + e.Kind)}: {e.Name}") };
        for (int i = 0; i < e.Providers.Count; i++)
        {
            var p = e.Providers[i];
            rows.Add(P(i == 0 ? Loc.T("Xr.Wins") : Loc.T("Xr.AlsoIn"), $"{p.Source.Tag} · {(p.Root.Core ? Loc.T("Xr.CoreOnly") : p.Root.Name)} · {(p.Library ? Loc.T("Xr.FromLibrary") : Loc.T("Xr.Loose"))}",
                i == 0 ? "B.Ok" : "B.TextDim", p.File));
        }
        if (e.Shader != null)
            rows.Add(P(Loc.T("Xr.Shader"), e.Shader));
        if (e.Texture != null && _host?.Xray.Index is { } index)
        {
            var t = XrAnomalyPlanner.Texture(index, src, null, e.Texture, _host.Xray.Substitutes);
            rows.Add(P(Loc.T("Xr.Texture"), t.File != null ? e.Texture : t.Proposal != null ? Loc.F("Xr.TexProposal", e.Texture, Path.GetFileName(t.Proposal)) : Loc.F("Xr.TexMissing", e.Texture),
                t.File != null ? "B.Text" : t.Proposal != null ? "B.Warn" : "B.Err", t.File ?? t.Proposal ?? ""));
        }
        if (e.Types.Count > 0)
            rows.Add(P(Loc.T("Xr.Types"), string.Join(", ", e.Types.Select(t => Loc.T("Xr.Type." + t)))));
        if (e.Kind == XrEntryKind.Group && _host?.Xray.Index?.Group(src, null, e.Name) is { } g)
            foreach (var item in g.Effects)
                rows.Add(P(Loc.F("Xr.GroupItem", item.T0, item.T1), item.Name + string.Concat(item.Children.Select(c => $"\n  ↳ {Loc.T("Xr.Child." + c.Kind)} {c.Name}")),
                    item.Has(Pfx.Sim.XRay.XrGroupItem.Enabled) ? "B.Text" : "B.TextOff"));
        DetailList.ItemsSource = rows;
        Refresh();
    }

    private IEnumerable<string> MissingTextures() => _plan?.Particles.SelectMany(p => p.Effects).Where(e => e.Texture is { File: null }).Select(e => e.Texture!.Name).Distinct()
                                                     ?? Enumerable.Empty<string>();

    private void OnToSubst(object sender, RoutedEventArgs e) => _host?.ShowSubstitutes(MissingTextures());

    private void OnImport(object sender, RoutedEventArgs e) => ImportNow();

    // The parts of the current plan (one per particle the zone names) or of the picked effect / group.
    public IReadOnlyList<XrImportPart> Parts()
    {
        string ptype = XrSourceSet.Slug(PTypeBox.Text.Trim());
        if (_plan is { } plan)
            return plan.Particles.Where(p => p.File != null).Select(p => new XrImportPart(plan.Source.Tag, Variant, p.Ref, ptype, XrayNames.State(plan.Source.Tag, p.Role))).ToList();
        if (_entry is { Kind: XrEntryKind.Effect or XrEntryKind.Group } en)
        {
            string tag = en.Winner.Source.Tag;
            return new[] { new XrImportPart(tag, Variant, en.Name, ptype, $"{tag}_{XrSourceSet.Slug(en.Name[(en.Name.LastIndexOf('/') + 1)..])}") };
        }
        return Array.Empty<XrImportPart>();
    }

    public void ImportNow()
    {
        var parts = Parts();
        if (parts.Count > 0)
            _host?.ImportParts(parts, _plan);
    }
}
