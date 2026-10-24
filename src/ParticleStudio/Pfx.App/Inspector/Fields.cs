using System.Collections.ObjectModel;
using System.Globalization;
using Pfx.App.Core;
using Pfx.Core.Editing;
using Pfx.Core.Model;
using Pfx.Core.Schema;
using Pfx.Core.Text;
using Pfx.Sim;
using Suite.Ui.Core;

namespace Pfx.App.Inspector;

public enum FieldScope { Effect, Emitter, Material }

// One target of a field: the block it edits and the block of the same emitter / file as it was opened (null for a
// new or cloned emitter), for the "changed against the file" mark.
public sealed record Target(KeyedBlock Block, EnfBlock? Original);

// A row of the inspector. Values are read from the token tree on Refresh; every change becomes one IEdit per target,
// run as one undo step through the owning document.
public abstract class FieldVm : ObservableObject
{
    private string? _note;
    private bool _flash;

    protected FieldVm(InspectorVm owner, string key, SchemaField? schema, FieldScope scope)
    {
        Owner = owner;
        Key = key;
        Schema = schema;
        Scope = scope;
    }

    public InspectorVm Owner { get; }
    public string Key { get; }
    public SchemaField? Schema { get; }
    public FieldScope Scope { get; }
    public IReadOnlyList<Target> Targets => Owner.Targets;

    // Keys this row edits (a value + spread pair edits two).
    public virtual IEnumerable<string> Keys => new[] { Key };

    public virtual string Label => Loc.TryT(Prefix + "." + Key) ?? Key;
    public string Unit => Loc.TryT((Scope == FieldScope.Material ? "MU." : "U.") + Key) ?? "";
    public string Hint => Loc.TryT((Scope == FieldScope.Material ? "MH." : "H.") + Key) ?? "";
    private string Prefix => Scope == FieldScope.Material ? "M" : "F";

    public virtual string Tip
    {
        get
        {
            var lines = new List<string> { Label + (Unit.Length > 0 ? ", " + Unit : "") + (Hint.Length > 0 ? " — " + Hint : "") };
            lines.Add(Loc.F("Tip.Key", Key) + (Schema?.Description is { Length: > 0 } d ? Loc.F("Tip.Desc", d) : ""));
            var range = Schema?.Range;
            string def = Schema?.Default ?? Loc.T("Tip.NoDefault");
            lines.Add(range != null ? Loc.F("Tip.DefaultRange", def, Loc.Num(range.Min), Loc.Num(range.Max)) : Loc.F("Tip.Default", def));
            lines.Add(Loc.T("Tip.Menu"));
            return string.Join("\n", lines);
        }
    }

    public bool Present { get; private set; }
    public bool Modified { get; private set; }
    public bool Mixed { get; protected set; }
    public bool FieldLocked => Owner.IsFieldLocked(Key);
    public bool ReadOnly => Owner.AllLocked;

    // Mark column: amber dot = changed against the file, small square = written in the file, empty = engine default.
    public string Mark => FieldLocked ? "⊘" : Modified ? "●" : Present ? "▪" : "";
    public string MarkBrush => FieldLocked ? "B.Warn" : Modified ? "B.Key" : "B.TextOff";
    public string MarkTip => FieldLocked ? Loc.T("Mark.Locked") : Modified ? Loc.T("Mark.Modified") : Present ? Loc.T("Mark.Present") : Loc.T("Mark.Default");

    // A short message under the row (clamped value, limits of the game); Flash plays the red border once.
    public string? Note { get => _note; set { if (Set(ref _note, value)) Raise(nameof(HasNote)); } }
    public bool HasNote => !string.IsNullOrEmpty(_note);
    public bool Flash { get => _flash; set => Set(ref _flash, value); }
    public virtual string? Limit => null;

    public virtual void Refresh()
    {
        Present = Targets.Any(t => t.Block.Has(Key));
        Modified = Keys.Any(k => Targets.Any(t => Raw.Of(t.Block.Block, k) != (t.Original == null ? null : Raw.Of(t.Original, k))));
        RaiseAll();
    }

    // Values per target as text ("" when every target agrees on nothing).
    protected IReadOnlyList<string?> Texts(string key) => Targets.Select(t => t.Block.Text(key)).ToList();

    protected void Apply(Func<KeyedBlock, IEdit?> make, string label, string? merge = null) => Owner.Apply(this, make, label, merge);

    public string Summary
    {
        get
        {
            var texts = Texts(Key);
            return texts.Distinct().Count() > 1 ? Loc.T("Val.Mixed") : texts.FirstOrDefault() ?? "";
        }
    }

    public void ShowNote(string text)
    {
        Note = text;
        Flash = false;
        Flash = true;
    }
}

// Raw value text of a key in a block as written (line values or graph numbers), null when the key is absent.
public static class Raw
{
    public static string? Of(EnfBlock block, string key)
    {
        if (block.FindLine(key) is { } line)
            return string.Join(" ", line.Values.Select(v => v.Text));
        if (block.FindBlock(key) is { } graph)
            return string.Join(" ", graph.Items.OfType<EnfLine>().SelectMany(l => l.Tokens).Select(t => t.Text));
        return null;
    }
}

public sealed class NumberField : FieldVm
{
    private string _text = "";

    public NumberField(InspectorVm owner, string key, SchemaField schema, FieldScope scope) : base(owner, key, schema, scope)
    {
        IsInteger = schema.Type == "int";
        IsSlider = schema.Editor == "slider" && schema.Range != null;
        Step = schema.Range?.Step ?? (IsInteger ? 1 : 0.01);
    }

    public bool IsInteger { get; }
    public bool IsSlider { get; }
    public double Step { get; }
    public double? Min => Schema!.Range?.Min;
    public double? Max => Schema!.Range?.Max;
    public double Default => double.TryParse(Schema!.Default, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
    public double Value { get; private set; }
    public string Text { get => _text; set => Set(ref _text, value); }
    public string MixedList { get; private set; } = "";

    public override void Refresh()
    {
        var values = Targets.Select(t => t.Block.Number(Key) ?? Default).ToList();
        Mixed = values.Distinct().Count() > 1;
        Value = values.FirstOrDefault(Default);
        MixedList = string.Join(" · ", values.Distinct().Take(4).Select(Loc.Num));
        _text = Mixed ? "" : Targets.Count == 1 && Targets[0].Block.Raw(Key) is { Count: > 0 } r ? r[0] : Loc.Num(Value);
        base.Refresh();
    }

    // Typed text: a number, an expression or a relative change, applied to each target's own value.
    public void Commit(string text) => Change(v => Expr.TryApply(text, v, out var r) ? r : null, Loc.F("Undo.Set", Label), null, text);

    // Scrub label / slider drag: one undo step per drag (merge key), value from the drag start.
    public void SetFromDrag(Func<double, double> f, string merge) => Change(v => f(v), Loc.F("Undo.Set", Label), merge, null);

    private void Change(Func<double, double?> f, string label, string? merge, string? typed)
    {
        string? clampNote = null;
        Apply(b =>
        {
            double current = b.Number(Key) ?? Default;
            if (f(current) is not double v)
                return null;
            var (c, note) = Clamp(v);
            clampNote ??= note;
            return Math.Abs(c - current) < 1e-12 && b.Has(Key) ? null : b.Set(Key, c);
        }, label, merge);
        if (typed != null && Targets.All(t => !Expr.TryApply(typed, t.Block.Number(Key) ?? Default, out _)))
            ShowNote(Loc.F("Note.NotNumber", typed));
        else if (clampNote != null)
            ShowNote(clampNote);
        else
            Note = null;
    }

    public (double Value, string? Note) Clamp(double v)
    {
        if (IsInteger)
            v = Math.Round(v);
        if (Min is double lo && v < lo)
            return (lo, Loc.F("Note.Min", Loc.Num(lo), Unit));
        if (Max is double hi && v > hi)
            return (hi, Loc.F("Note.Max", Loc.Num(hi), Unit));
        return (v, null);
    }
}

// Value + spread: the game takes value .. value + spread (X + U[0,1) * RND, measured in P3), shown as one bar.
public sealed class PairField : FieldVm
{
    public PairField(InspectorVm owner, SchemaField baseField, SchemaField rnd) : base(owner, baseField.Key, baseField, FieldScope.Emitter)
    {
        Base = new NumberField(owner, baseField.Key, baseField, FieldScope.Emitter);
        Rnd = new NumberField(owner, rnd.Key, rnd, FieldScope.Emitter);
    }

    public NumberField Base { get; }
    public NumberField Rnd { get; }
    public override IEnumerable<string> Keys => new[] { Base.Key, Rnd.Key };

    public string RangeText => Base.Mixed || Rnd.Mixed ? Loc.T("Val.Mixed") : Loc.F("Val.Range", Loc.Num(Base.Value), Loc.Num(Base.Value + Rnd.Value), Base.Unit);
    public string RndTip => Rnd.Tip + "\n" + (Rnd.Key == "BirthRateRND" ? Loc.T("Tip.BirthRateRnd") : Loc.T("Tip.Rnd"));

    public override void Refresh()
    {
        Base.Refresh();
        Rnd.Refresh();
        base.Refresh();
    }
}

public sealed class VectorField : FieldVm
{
    public VectorField(InspectorVm owner, string key, SchemaField schema, FieldScope scope) : base(owner, key, schema, scope)
    {
    }

    public string[] Texts3 { get; } = new string[3];
    public double[] Values { get; } = new double[3];
    public bool[] MixedAxis { get; } = new bool[3];
    public string X => Texts3[0];
    public string Y => Texts3[1];
    public string Z => Texts3[2];

    private double[] Of(KeyedBlock b) => b.Numbers(Key) is { Length: >= 3 } n ? n : new double[3];

    public override void Refresh()
    {
        var all = Targets.Select(t => Of(t.Block)).ToList();
        for (int a = 0; a < 3; a++)
        {
            MixedAxis[a] = all.Select(v => v[a]).Distinct().Count() > 1;
            Values[a] = all.Count > 0 ? all[0][a] : 0;
            Texts3[a] = MixedAxis[a] ? "—" : Loc.Num(Values[a]);
        }
        Mixed = MixedAxis.Any(m => m);
        base.Refresh();
    }

    public void Commit(int axis, string text)
    {
        Apply(b =>
        {
            var v = (double[])Of(b).Clone();
            if (!Expr.TryApply(text, v[axis], out var r))
                return null;
            v[axis] = r;
            return b.Set(Key, v);
        }, Loc.F("Undo.SetAxis", Label, "XYZ"[axis]));
        if (Targets.All(t => !Expr.TryApply(text, Of(t.Block)[axis], out _)))
            ShowNote(Loc.F("Note.NotNumber", text));
    }

    public void SetFromDrag(int axis, Func<double, double> f, string merge) =>
        Apply(b =>
        {
            var v = (double[])Of(b).Clone();
            v[axis] = f(v[axis]);
            return b.Set(Key, v);
        }, Loc.F("Undo.SetAxis", Label, "XYZ"[axis]), merge);
}

public sealed class ToggleField : FieldVm
{
    public ToggleField(InspectorVm owner, string key, SchemaField schema, FieldScope scope) : base(owner, key, schema, scope)
    {
    }

    public bool? IsOn { get; private set; }
    public string StateText => IsOn switch { true => Loc.T("Val.On"), false => Loc.T("Val.Off"), _ => Loc.T("Val.Mixed") };

    private static bool On(string? t) => t is "1" or "true" or "True";

    public override void Refresh()
    {
        var on = Targets.Select(t => On(t.Block.Text(Key))).Distinct().ToList();
        IsOn = on.Count == 1 ? on[0] : null;
        Mixed = on.Count > 1;
        base.Refresh();
    }

    // Written as the file already spells it (0/1 or false/true); new keys as 0/1 like vanilla files.
    public void Toggle()
    {
        bool target = IsOn != true;
        Apply(b =>
        {
            string? now = Raw.Of(b.Block, Key);
            bool words = now is "true" or "false";
            return b.SetWord(Key, words ? (target ? "true" : "false") : (target ? "1" : "0"));
        }, Loc.F(target ? "Undo.On" : "Undo.Off", Label));
    }
}

public sealed class ChoiceField : FieldVm
{
    // ShapeType counts from 1 when written as a number (default "2" = Point, P3); the other enums from 0
    // (BlendMode 2 = Additive_AlphaModulate in the vanilla torch materials).
    private readonly int _base;

    public ChoiceField(InspectorVm owner, string key, SchemaField schema, FieldScope scope) : base(owner, key, schema, scope)
    {
        _base = key == "ShapeType" ? 1 : 0;
        Options = new ObservableCollection<ChoiceOption>(schema.Values!.Select(v => new ChoiceOption(this, v)));
    }

    public ObservableCollection<ChoiceOption> Options { get; }
    // Segments only when every option fits a third of the column; otherwise a drop-down list.
    public bool Few => Options.Count <= 3 && Options.All(o => o.Text.Length <= 7);
    public string? Current { get; private set; }
    public ChoiceOption? Selected { get => Options.FirstOrDefault(o => o.Value == Current); set { if (value != null && value.Value != Current) Choose(value.Value); } }
    public string? Unknown { get; private set; }

    public string? NameOf(string? raw)
    {
        if (raw == null)
            return null;
        if (int.TryParse(raw, out int n))
        {
            int i = n - _base;
            return i >= 0 && i < Schema!.Values!.Count ? Schema.Values[i] : null;
        }
        return Schema!.Values!.FirstOrDefault(v => v.Equals(raw, StringComparison.OrdinalIgnoreCase));
    }

    public override void Refresh()
    {
        var names = Targets.Select(t => t.Block.Text(Key)).Select(r => (Raw: r, Name: NameOf(r))).ToList();
        Mixed = names.Select(n => n.Name ?? n.Raw).Distinct().Count() > 1;
        Current = Mixed ? null : names.FirstOrDefault().Name;
        var bad = names.FirstOrDefault(n => n.Raw != null && n.Name == null);
        Unknown = bad.Raw != null ? Loc.F("Note.NotInList", bad.Raw) : null;
        Note = Unknown;
        foreach (var o in Options)
            o.Raise();
        base.Refresh();
    }

    public void Choose(string value) => Apply(b => b.SetWord(Key, value), Loc.F("Undo.Choose", Label, Loc.TryT("E." + Key + "." + value) ?? value));
}

public sealed class ChoiceOption : ObservableObject
{
    private readonly ChoiceField _field;

    public ChoiceOption(ChoiceField field, string value)
    {
        _field = field;
        Value = value;
    }

    public string Value { get; }
    public string Text => Loc.TryT("E." + _field.Key + "." + Value) ?? Value;
    public string Tip => Text + " (" + Value + ")";
    public bool IsOn { get => _field.Current == Value; set { if (value) _field.Choose(Value); } }

    public void Raise() => RaiseAll();

    public override string ToString() => Text;
}

// Graph keys "t v .." (or "t r g b .." for Color): a thumbnail of the engine curve; a click opens it in the curve editor.
public sealed class GraphField : FieldVm
{
    public GraphField(InspectorVm owner, string key, SchemaField schema, FieldScope scope) : base(owner, key, schema, scope)
    {
        Channels = schema.Type == "graph4" ? 3 : 1;
    }

    public int Channels { get; }
    public bool IsColor => Channels == 3;
    public double[] Numbers { get; private set; } = Array.Empty<double>();
    public string KeyCount => Loc.F("Val.Keys", Numbers.Length / (Channels + 1));
    public bool ClipsOutside { get; private set; }

    public Graph Curve => Graph.FromNumbers(Numbers, Channels);

    public override void Refresh()
    {
        var all = Targets.Select(t => t.Block.Numbers(Key) ?? Array.Empty<double>()).ToList();
        Mixed = all.Select(a => string.Join(" ", a)).Distinct().Count() > 1;
        Numbers = all.FirstOrDefault() ?? Array.Empty<double>();
        var g = Curve;
        ClipsOutside = false;
        for (int s = 0; s <= 64 && !ClipsOutside; s++)
            for (int c = 0; c < Channels; c++)
            {
                float u = g.Unclamped(s / 64f, c);
                if (u < -0.0005f || u > 1.0005f)
                    ClipsOutside = true;
            }
        Note = ClipsOutside ? Loc.T("Note.Clips") : null;
        base.Refresh();
    }

    public void Open() => Owner.RequestCurve(this);
}

// Paths: the emitter's Material, the material's texture maps. Texture lines may carry flags after the path
// ("AlbedoMap "<path>" alpha"): only the path is replaced.
public sealed class PathField : FieldVm
{
    private string _text = "";

    public PathField(InspectorVm owner, string key, SchemaField schema, FieldScope scope) : base(owner, key, schema, scope)
    {
    }

    public string Text { get => _text; set => Set(ref _text, value); }

    public override void Refresh()
    {
        var paths = Targets.Select(t => t.Block.Block.FindLine(Key)?.Values.FirstOrDefault()?.Value).ToList();
        Mixed = paths.Distinct().Count() > 1;
        _text = Mixed ? "" : paths.FirstOrDefault() ?? "";
        Note = !Mixed && _text.Length > 0 && Owner.Resolve(_text) == null ? Loc.T("Note.FileMissing") : null;
        base.Refresh();
    }

    public void Commit(string text)
    {
        text = text.Trim();
        Apply(b =>
        {
            var line = b.Block.FindLine(Key);
            if (text.Length == 0)
                return b.Reset(Key);
            var values = new List<string> { EnfEdits.Quote(text) };
            if (line != null)
                values.AddRange(line.Values.Skip(1).Select(v => v.Text));
            return Raw.Of(b.Block, Key) == string.Join(" ", values) ? null : EnfEdits.SetLine(b.Doc, b.Block, Key, values, k => b.Schema.Find(b.Scope, k)?.Order);
        }, Loc.F("Undo.Set", Label));
    }
}

// Colours of a material: four numbers (r g b a) and a swatch.
public sealed class ColorField : FieldVm
{
    public ColorField(InspectorVm owner, string key, SchemaField schema, FieldScope scope) : base(owner, key, schema, scope)
    {
    }

    public double[] Values { get; private set; } = new double[4];
    public string[] Texts4 { get; } = new string[4];
    public string R => Texts4[0];
    public string G => Texts4[1];
    public string B => Texts4[2];
    public string A => Texts4[3];
    public System.Windows.Media.Color Swatch => System.Windows.Media.Color.FromRgb(Byte(Values[0]), Byte(Values[1]), Byte(Values[2]));

    private static byte Byte(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);

    private double[] Of(KeyedBlock b) => b.Numbers(Key) is { Length: >= 4 } n ? n : new double[] { 1, 1, 1, 1 };

    public override void Refresh()
    {
        var all = Targets.Select(t => Of(t.Block)).ToList();
        Values = all.FirstOrDefault() ?? new double[] { 1, 1, 1, 1 };
        for (int i = 0; i < 4; i++)
            Texts4[i] = all.Select(v => v[i]).Distinct().Count() > 1 ? "—" : Loc.Num(Values[i]);
        base.Refresh();
    }

    public void Commit(int channel, string text) => Apply(b =>
    {
        var v = (double[])Of(b).Clone();
        if (!Expr.TryApply(text, v[channel], out var r))
            return null;
        v[channel] = r;
        return b.Set(Key, v);
    }, Loc.F("Undo.SetAxis", Label, "RGBA"[channel]));
}

// Free text: strings (PhysMaterial), matrices (AlbedoMapTexMat) and keys the schema does not know. Written back as
// the tokens typed; a quoted value stays quoted.
public sealed class TextField : FieldVm
{
    private string _text = "";

    public TextField(InspectorVm owner, string key, SchemaField? schema, FieldScope scope) : base(owner, key, schema, scope)
    {
    }

    public bool IsUnknown => Schema == null;
    public string Text { get => _text; set => Set(ref _text, value); }
    public override string Label => IsUnknown ? Key : base.Label;
    public override string Tip => IsUnknown ? Loc.F("Tip.Unknown", Key) : base.Tip;

    public override void Refresh()
    {
        var texts = Targets.Select(t => Raw.Of(t.Block.Block, Key) ?? t.Block.Text(Key)).ToList();
        Mixed = texts.Distinct().Count() > 1;
        _text = Mixed ? "" : texts.FirstOrDefault() ?? "";
        base.Refresh();
    }

    public void Commit(string text)
    {
        var tokens = Tokenize(text.Trim());
        if (tokens.Count == 0)
            return;
        Apply(b => Raw.Of(b.Block, Key) == string.Join(" ", tokens) ? null
            : EnfEdits.SetLine(b.Doc, b.Block, Key, tokens, k => b.Schema.Find(b.Scope, k)?.Order), Loc.F("Undo.Set", Label));
    }

    private static List<string> Tokenize(string text)
    {
        var list = new List<string>();
        int i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i])) { i++; continue; }
            int start = i;
            if (text[i] == '"')
            {
                int end = text.IndexOf('"', i + 1);
                i = end < 0 ? text.Length : end + 1;
            }
            else
            {
                while (i < text.Length && !char.IsWhiteSpace(text[i]))
                    i++;
            }
            list.Add(text[start..i]);
        }
        return list;
    }
}

// Child blocks (Emitors of the effect, TCModFuncs of a material): shown with their count; Emitors are edited in the
// layers panel, texture-coordinate functions in the texture workspace (P7).
public sealed class ChildrenField : FieldVm
{
    public ChildrenField(InspectorVm owner, string key, SchemaField schema, FieldScope scope) : base(owner, key, schema, scope)
    {
    }

    public string Text { get; private set; } = "";

    public override void Refresh()
    {
        var block = Targets.FirstOrDefault()?.Block.Block.FindBlock(Key);
        int n = block?.Items.OfType<EnfBlock>().Count() ?? 0;
        Text = Key == "Emitors" ? Loc.F("Val.Emitters", n) : block == null ? Loc.T("Val.None") : Loc.F("Val.Children", n);
        Note = Key == "Emitors" ? Loc.T("Note.EmitorsInLayers") : Loc.T("Note.TcMod");
        base.Refresh();
    }
}
