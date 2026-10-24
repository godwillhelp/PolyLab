using Pfx.App.Model;
using Pfx.App.XRay;
using Pfx.Render;
using Pfx.XRay;

namespace Pfx.App.Views;

// X-Ray import in the shell (spec 3.9, P9): the import tab, the X-Ray panel, the A/B panel and the substitute table
// share one XrayWorkspace; imported parts open as ordinary parts and the A/B view follows the active one.
public partial class MainWindow : IImportHost, IXrayHost
{
    private XrayWorkspace _xray = null!;
    private readonly AbCompare _ab = new();

    public XrayWorkspace Xray => _xray;
    public AbCompare Ab => _ab;
    public ImportView ImportPanel => ImportTab;
    public XrayView XrayPanel => XrayIndexPanel;
    public AbView AbPanel => AbComparePanel;
    public SubstView SubstPanel => SubstitutesPanel;
    public AvalonDock.Layout.LayoutDocument ViewportDocument => ViewportDoc;
    public void CloseDocument(EffectDoc doc) => CloseDoc(doc);

    private void InitXray()
    {
        _xray = new XrayWorkspace { AutoSave = !_launch.Automated || _launch.XrayRun != null };
        _xray.Toast += Toast;
        _xray.Changed += OnXrayChanged;
        ImportTab.Init(this);
        XrayIndexPanel.Init(this);
        AbComparePanel.Init(_ab, () => { });
        SubstitutesPanel.Init(_xray, Toast);
        _viewport.ComposeSprites = (batch, dt) =>
        {
            ComposeAb(batch, dt);
            ComposeProjectSprites(batch);
        };
        if (_xray.Sources.Sources.Count > 0)
            _xray.RebuildIndex();
    }

    private object? _abIndex;

    private void OnXrayChanged()
    {
        if (_xray.Index != null && _xray.Index != _abIndex && _active != null)
            ShowAbFor(_active);
    }

    // The A/B subject follows the active part: its import record, resolved in the current index.
    private void ShowAbFor(EffectDoc doc)
    {
        var record = ImportRecord.ForPart(doc.Path);
        if (record == null)
        {
            if (_ab.Record != null)
            {
                _ab.Clear();
                AbComparePanel.Show();
            }
            return;
        }
        if (_xray.Index == null)
            return;
        if (_ab.Record?.Key == record.Key && _abIndex == _xray.Index)
            return;
        _abIndex = _xray.Index;
        _ab.Show(record, _xray.Index);
        AbComparePanel.Show();
    }

    private void ComposeAb(SpriteBatch batch, float dt)
    {
        if (_ab.Record == null)
            return;
        var (right, up, fwd) = _viewport.Camera.Basis();
        _ab.Compose(batch, _pipeline.Time, right, up, fwd);
        AbComparePanel.ShowLive(_viewport.Previews.Sum(p => p.AliveTotal));
    }

    void IImportHost.Toast(string text, string kind) => Toast(text, kind);
    void IXrayHost.Toast(string text, string kind) => Toast(text, kind);

    public void ShowPlan(XrAnomalyPlan plan)
    {
        XrayIndexPanel.ShowPlan(plan);
        ShowPane("xray");
    }

    public void ImportPlan(XrAnomalyPlan plan)
    {
        XrayIndexPanel.ShowPlan(plan);
        XrayIndexPanel.ImportNow();
    }

    public bool Imported(string tag, string ptype, string state) =>
        Settings.ImportFolder.Length > 0 && File.Exists(ImportRecord.FileOf(Path.Combine(Settings.ImportFolder, ImportRecord.Prefix), tag, ptype, state));

    public void SettingsChanged()
    {
        if (!_launch.Automated)
            Settings.Save();
    }

    public void ShowSubstitutes(IEnumerable<string> missing)
    {
        SubstitutesPanel.AddMissing(missing);
        ShowPane("xsubst");
    }

    private string? XrayScriptPath => _launch.XrayScript ?? (Settings.XrayScript.Length > 0 ? Settings.XrayScript : null);

    // Converts through the user's xray.py into the import folder, which becomes a data root, then opens the parts.
    public bool ImportParts(IReadOnlyList<XrImportPart> parts, XrAnomalyPlan? plan) => ImportParts(parts, plan, null);

    public bool ImportParts(IReadOnlyList<XrImportPart> parts, XrAnomalyPlan? plan, Action<IReadOnlyList<string>>? after)
    {
        if (XrayScriptPath is not { } script || !File.Exists(script))
        {
            Toast(Loc.T("Xr.Toast.NoScript"), "warn");
            ShowPane("settings");
            return false;
        }
        if (Settings.ImportFolder.Length == 0)
        {
            Toast(Loc.T("Xr.Toast.NoImportFolder"), "warn");
            ShowPane("settings");
            return false;
        }
        if (plan != null)
            SubstitutesPanel.AddMissing(plan.Particles.SelectMany(p => p.Effects).Where(e => e.Texture is { File: null }).Select(e => e.Texture!.Name));
        return _xray.Import(parts, Settings.ImportFolder, Settings.Python, script, plan, files =>
        {
            if (!Settings.Roots.Contains(Settings.ImportFolder, StringComparer.OrdinalIgnoreCase))
            {
                Settings.Roots.Add(Settings.ImportFolder);
                RootsChanged();
                SettingsPanel.Show(Settings);
                Toast(Loc.F("Xr.Toast.RootAdded", Settings.ImportFolder), "info");
            }
            var existing = files.Where(File.Exists).ToList();
            Toast(Loc.F("Xr.Toast.Imported", parts.Count, existing.Count), "ok");
            foreach (var d in _docs.Where(d => existing.Contains(d.Path, StringComparer.OrdinalIgnoreCase)).ToList())
            {
                if (d.Dirty)
                    Toast(Loc.F("Xr.Toast.OpenEdited", d.Title), "warn");
                else
                    CloseDoc(d);
            }
            if (existing.Count > 0)
            {
                ShowPane("xab");
                ViewportDoc.IsSelected = true;
                Open(existing);
            }
            if (plan != null && existing.Count > 0 && ImportRecord.ForPart(existing[0]) is { Zone: not null } record)
                OpenZoneProject(record, Path.Combine(Settings.ImportFolder, ImportRecord.Prefix));
            after?.Invoke(existing);
        });
    }
}
