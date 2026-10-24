using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Pfx.App.Core;
using Pfx.App.Model;
using Pfx.Core.Editing;
using Pfx.Core.Model;
using Pfx.Core.Schema;
using Suite.Ui.Core;

namespace Pfx.App.Inspector;

public sealed class SectionVm : ObservableObject
{
    // Open/closed per section name for the whole session, so switching emitters keeps the layout.
    private static readonly Dictionary<string, bool> OpenState = new();
    private bool _visible = true;

    public SectionVm(string id, string nameKey, IEnumerable<FieldVm> fields, bool openByDefault)
    {
        Id = id;
        NameKey = nameKey;
        Fields = new ObservableCollection<FieldVm>(fields);
        if (!OpenState.ContainsKey(id))
            OpenState[id] = openByDefault;
    }

    public string Id { get; }
    public string NameKey { get; }
    public string Name => Loc.TryT(NameKey) ?? NameKey;
    public ObservableCollection<FieldVm> Fields { get; }
    public ObservableCollection<FieldVm> Shown { get; } = new();
    public bool Forced { get; set; }

    public bool IsOpen
    {
        get => Forced || OpenState[Id];
        set
        {
            OpenState[Id] = value;
            Raise();
            Raise(nameof(Summary));
        }
    }

    public bool Visible { get => _visible; set => Set(ref _visible, value); }

    // Collapsed sections show the first values written in the file ("Physics: 2 · 0.2 · 1").
    public string Summary => IsOpen ? "" : string.Join(" · ", Fields.Where(f => f.Present && !f.Mixed).Take(3).Select(f => f.Summary).Where(s => s.Length > 0));

    public void RaiseOpen()
    {
        Raise(nameof(IsOpen));
        Raise(nameof(Summary));
    }
}

// The inspector for one scope (effect, emitters, material): rows generated from the schema descriptors (FieldDescriptor)
// in engine registration order, grouped like the Workbench property grid, plus raw rows for keys the schema does not
// know. Edits from here run through the document's undo history.
public sealed class InspectorVm : ObservableObject
{
    private static readonly HashSet<string> FieldLocks = new();
    private static readonly Dictionary<string, string> Pairs = new()
    {
        ["BirthRate"] = "BirthRateRND", ["SizeMultiplier"] = "SizeRND", ["Velocity"] = "VelocityRND",
        ["AirResistance"] = "AirResistanceRND", ["GravityMultiply"] = "GravityMultiplyRND", ["LifeTime"] = "LifeTimeRND",
    };

    private string _query = "";
    private string _title = "";
    private string _subtitle = "";
    private string _empty = "";

    public InspectorVm(FieldScope scope)
    {
        Scope = scope;
    }

    public FieldScope Scope { get; }
    public EffectDoc? Doc { get; private set; }
    public IReadOnlyList<Target> Targets { get; private set; } = Array.Empty<Target>();
    public ObservableCollection<SectionVm> Sections { get; } = new();
    public IEnumerable<FieldVm> AllFields => Sections.SelectMany(s => s.Fields);
    public Func<string, string?> Resolve { get; set; } = _ => null;

    public string Title { get => _title; private set => Set(ref _title, value); }
    public string Subtitle { get => _subtitle; private set => Set(ref _subtitle, value); }
    public string Empty { get => _empty; private set { if (Set(ref _empty, value)) Raise(nameof(HasEmpty)); } }
    public bool HasEmpty => _empty.Length > 0;
    public bool AllLocked => Doc != null && Targets.Count > 0 && Targets.All(t => Doc.Locked.Contains(t.Block.Block));
    public int MatchCount { get; private set; }

    public event Action<string, string>? Toast;
    public event Action<FieldVm>? ShowInText;
    public event Action<FieldVm>? OpenCurve;

    public string Query
    {
        get => _query;
        set
        {
            if (Set(ref _query, value))
                Filter();
        }
    }

    public bool IsFieldLocked(string key) => FieldLocks.Contains(Scope + ":" + key);

    public void ToggleFieldLock(string key)
    {
        if (!FieldLocks.Remove(Scope + ":" + key))
            FieldLocks.Add(Scope + ":" + key);
        Refresh();
    }

    public void Clear(string emptyText)
    {
        Doc = null;
        Targets = Array.Empty<Target>();
        Sections.Clear();
        Title = "";
        Subtitle = "";
        Empty = emptyText;
        RaiseAll();
    }

    public void Show(EffectDoc doc, IReadOnlyList<Target> targets, string title, string subtitle)
    {
        bool sameShape = Doc == doc && Targets.Count == targets.Count && Targets.Zip(targets).All(p => p.First.Block.Block == p.Second.Block.Block);
        Doc = doc;
        Targets = targets;
        Title = title;
        Subtitle = subtitle;
        Empty = "";
        var unknown = UnknownKeys();
        bool rebuild = !sameShape || !AllFields.Where(f => f.Schema == null).Select(f => f.Key).SequenceEqual(unknown);
        if (rebuild)
            Build(unknown);
        Refresh();
    }

    private IReadOnlyList<string> UnknownKeys()
    {
        var schema = PfxSchema.Shared;
        var scope = Scope switch { FieldScope.Effect => SchemaScope.Effect, FieldScope.Emitter => SchemaScope.Emitor, _ => SchemaScope.Material };
        return Targets.SelectMany(t => t.Block.Block.Items.Select(EnfEdits.KeyOf))
            .Where(k => k.Length > 0 && schema.Find(scope, k) == null && !(Scope == FieldScope.Effect && k == "Emitors"))
            .Distinct().ToList();
    }

    private void Build(IReadOnlyList<string> unknown)
    {
        Sections.Clear();
        var schema = PfxSchema.Shared;
        var fields = Scope switch { FieldScope.Effect => schema.Effect, FieldScope.Emitter => schema.Emitor, _ => schema.Material };
        var rnd = Pairs.Values.ToHashSet();
        foreach (var group in fields.GroupBy(f => f.Group ?? "General"))
        {
            var rows = new List<FieldVm>();
            foreach (var f in group)
            {
                if (Scope == FieldScope.Emitter && rnd.Contains(f.Key))
                    continue;
                if (Scope == FieldScope.Emitter && Pairs.TryGetValue(f.Key, out var r))
                    rows.Add(new PairField(this, f, schema.Find(SchemaScope.Emitor, r)!));
                else
                    rows.Add(Make(f));
            }
            string id = Scope + "." + Regex.Replace(group.Key, "[^A-Za-z]", "");
            Sections.Add(new SectionVm(id, "Grp." + Regex.Replace(group.Key, "[^A-Za-z]", ""), rows,
                group.Key is "General" or "Physics" or "Particle-lifetime animation" or "Albedo map" or "Color modificators" or "Particle"));
        }
        if (unknown.Count > 0)
            Sections.Add(new SectionVm(Scope + ".Unknown", "Grp.Unknown", unknown.Select(k => (FieldVm)new TextField(this, k, null, Scope)), true));
        Filter();
    }

    private FieldVm Make(SchemaField f) => FieldDescriptor.For(f).Editor switch
    {
        EditorKind.Number or EditorKind.Integer => new NumberField(this, f.Key, f, Scope),
        EditorKind.Toggle => new ToggleField(this, f.Key, f, Scope),
        EditorKind.Vector3 => new VectorField(this, f.Key, f, Scope),
        EditorKind.Choice => new ChoiceField(this, f.Key, f, Scope),
        EditorKind.Resource or EditorKind.Texture => new PathField(this, f.Key, f, Scope),
        EditorKind.Graph => new GraphField(this, f.Key, f, Scope),
        EditorKind.Color => new ColorField(this, f.Key, f, Scope),
        EditorKind.Children => new ChildrenField(this, f.Key, f, Scope),
        _ => new TextField(this, f.Key, f, Scope),
    };

    public void Refresh()
    {
        foreach (var f in AllFields)
            f.Refresh();
        foreach (var s in Sections)
            s.RaiseAll();
        Raise(nameof(AllLocked));
    }

    private void Filter()
    {
        string q = _query.Trim();
        MatchCount = 0;
        foreach (var s in Sections)
        {
            s.Shown.Clear();
            foreach (var f in s.Fields)
            {
                if (q.Length == 0 || Matches(f, q))
                {
                    s.Shown.Add(f);
                    MatchCount++;
                }
            }
            s.Forced = q.Length > 0;
            s.Visible = s.Shown.Count > 0;
            s.RaiseOpen();
        }
        Raise(nameof(MatchCount));
    }

    private static bool Matches(FieldVm f, string q)
    {
        bool Hit(FieldVm x) =>
            x.Label.Contains(q, StringComparison.CurrentCultureIgnoreCase) || x.Key.Contains(q, StringComparison.OrdinalIgnoreCase)
            || x.Hint.Contains(q, StringComparison.CurrentCultureIgnoreCase) || (x.Schema?.Description ?? "").Contains(q, StringComparison.OrdinalIgnoreCase);
        return Hit(f) || (f is PairField p && Hit(p.Rnd));
    }

    // One undo step for every target. Locked emitters, and other targets of a batch when the field is locked against
    // batch edits, are left out and named in a toast.
    public void Apply(FieldVm field, Func<KeyedBlock, IEdit?> make, string label, string? merge)
    {
        if (Doc == null)
            return;
        var edits = new List<IEdit>();
        int skipped = 0;
        bool batchLocked = Targets.Count > 1 && field.Keys.Any(IsFieldLocked);
        foreach (var t in Targets)
        {
            if (Doc.Locked.Contains(t.Block.Block) || batchLocked)
            {
                skipped++;
                continue;
            }
            if (make(t.Block) is { } e)
                edits.Add(e);
        }
        if (skipped > 0 && merge == null)
            Toast?.Invoke(batchLocked ? Loc.F("Toast.FieldLocked", field.Label) : Loc.F("Toast.Skipped", skipped), "warn");
        if (edits.Count == 0)
        {
            field.Refresh();
            return;
        }
        Doc.Do(edits.Count == 1 ? edits[0] : new CompositeEdit(edits), label, merge);
    }

    public void RequestShowInText(FieldVm f) => ShowInText?.Invoke(f);

    public void RequestCurve(FieldVm f) => OpenCurve?.Invoke(f);

    public void Say(string text, string kind = "info") => Toast?.Invoke(text, kind);
}
