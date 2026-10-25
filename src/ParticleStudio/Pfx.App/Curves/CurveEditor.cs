using System.Globalization;
using Pfx.App.Core;
using Pfx.App.Model;
using Pfx.Core.Model;
using Pfx.Sim;
using PolyLab.Ui.Core;

namespace Pfx.App.Curves;

public enum CurveKind { Life, Master }

// One curve of the editor: a graph key and, for Color / ColorMast, one of its three channels (they share key times).
public sealed record CurveLayer(string Key, int Channel, string Name, string Brush)
{
    public int Channels => Key is "Color" or "ColorMast" ? 3 : 1;
    public bool IsColor => Channels == 3;
}

// The curve editor of the bottom dock (spec 3.6, board 11): every graph of one emitter, drawn as the engine evaluates
// it (Pfx.Sim.Graph), with the Workbench gestures (double-click adds a key, drag with an x/y readout, Delete removes,
// snap to the grid) plus typed keys, box select, copy/paste, presets and drawing, both baked to engine keys within the
// set error. Every change is one step of the document's undo history (drags merge into one). State: the document owns
// the keys (the file text); this object owns only the view (which emitter, layer, selection, clipboard, settings).
public sealed class CurveEditor : ObservableObject
{
    public static readonly CurveLayer[] LifeLayers =
    {
        new("Color", 0, "Curve.R", "B.CurveR"), new("Color", 1, "Curve.G", "B.CurveG"), new("Color", 2, "Curve.B", "B.CurveB"),
        new("Alpha", 0, "Curve.Alpha", "B.CurveA"), new("Size", 0, "Curve.Size", "B.CurveSize"),
        new("RotationSpeed", 0, "Curve.Rotation", "B.CurveRot"),
    };

    public static readonly CurveLayer[] MasterLayers =
    {
        new("ColorMast", 0, "Curve.R", "B.CurveR"), new("ColorMast", 1, "Curve.G", "B.CurveG"), new("ColorMast", 2, "Curve.B", "B.CurveB"),
        new("AlphaMast", 0, "Curve.Alpha", "B.CurveA"), new("SizeMast", 0, "Curve.Size", "B.CurveSize"),
        new("RotationSpeedMast", 0, "Curve.Rotation", "B.CurveRot"), new("BRateMast", 0, "Curve.Births", "B.Accent"),
        new("VelocityMast", 0, "Curve.Velocity", "B.Ok"), new("AirResistanceMast", 0, "Curve.Drag", "B.TextDim"),
    };

    // The snap grid is the editor's minor grid: twenty steps across time and across the value range 0..1.
    public const double SnapStep = 0.05;
    // Dragged values stay inside the drawn value range (the game reads 0..1; the band above 1 shows the clamp).
    public const double ViewTop = 1.25;
    // Two keys closer than this in time count as one place (a double-click on a key, a pasted key on a key).
    private const double SameTime = 0.002;
    // Generated key numbers carry four decimals (GraphBake writes the same).
    private const int Decimals = 4;

    private readonly HashSet<int> _selection = new();
    private readonly bool[] _lifeShown = { true, true, true, true, true, false };
    private readonly bool[] _masterShown = { false, false, false, true, true, false, true, false, false };
    private List<GraphKey>? _dragStart;
    private string? _dragMerge;
    private int _dragCount;
    private (List<GraphKey> Keys, int Channels, int Channel)? _clipboard;
    private CurveKind _kind;
    private int _active = 3;
    private int _masterActive = 6;
    private bool _snap;
    private bool _seconds;
    private bool _drawing;
    // The smallest round limit all four presets meet within GraphBake.MaxKeys (P6: flash reaches 0.0146 at best).
    private double _maxError = 0.02;
    private string _bakeText = "";
    private bool _bakeMissed;

    public EffectDoc? Doc { get; private set; }
    public EmitterView? Emitter { get; private set; }
    public int SelectedEmitters { get; private set; }

    public event Action? Changed;
    public event Action<string, string>? Toast;

    public CurveKind Kind => _kind;
    public IReadOnlyList<CurveLayer> Layers => _kind == CurveKind.Life ? LifeLayers : MasterLayers;
    public int ActiveIndex => _kind == CurveKind.Life ? _active : _masterActive;
    public CurveLayer Active => Layers[ActiveIndex];
    public IReadOnlySet<int> Selection => _selection;
    public bool HasSelection => _selection.Count > 0;
    public bool HasClipboard => _clipboard != null;
    public bool IsMaster => _kind == CurveKind.Master;
    public bool Snap { get => _snap; set { if (Set(ref _snap, value)) Notify(); } }
    public bool Seconds { get => _seconds; set { if (Set(ref _seconds, value)) Notify(); } }
    public bool Drawing { get => _drawing; set { if (Set(ref _drawing, value)) Notify(); } }
    public string BakeText { get => _bakeText; private set => Set(ref _bakeText, value); }
    public bool BakeMissed { get => _bakeMissed; private set => Set(ref _bakeMissed, value); }
    public bool Editable => Emitter != null && Doc != null && !Doc.Locked.Contains(Emitter.Block);

    public double MaxError
    {
        get => _maxError;
        set => Set(ref _maxError, Math.Clamp(value, 0.0005, 0.25));
    }

    // Seconds per unit of x on master graphs (EffectTime; 0 plays as 1 s in game, P3); life graphs stay 0..1.
    public double TimeScale => IsMaster && _seconds ? EffectTime : 1;

    public double EffectTime
    {
        get
        {
            double t = Emitter?.Number("EffectTime") ?? 0;
            return t > 0 ? t : 1;
        }
    }

    public bool IsShown(int layer) => layer == ActiveIndex || (_kind == CurveKind.Life ? _lifeShown : _masterShown)[layer];

    public void ToggleShown(int layer)
    {
        var shown = _kind == CurveKind.Life ? _lifeShown : _masterShown;
        shown[layer] = !shown[layer];
        Notify();
    }

    public void Show(EffectDoc? doc, EmitterView? emitter, int selected)
    {
        bool same = doc == Doc && emitter?.Block == Emitter?.Block;
        Doc = doc;
        Emitter = emitter;
        SelectedEmitters = selected;
        if (!same)
        {
            _selection.Clear();
            BakeText = "";
        }
        Notify();
    }

    // The document changed (an edit here, in the inspector, an undo): drop selected keys that no longer exist.
    public void Refresh()
    {
        int count = Keys(Active).Count;
        _selection.RemoveWhere(i => i >= count);
        Notify();
    }

    public void SetKind(CurveKind kind)
    {
        if (_kind == kind)
            return;
        _kind = kind;
        _selection.Clear();
        Notify();
    }

    public void SetActive(int layer)
    {
        if (layer == ActiveIndex)
            return;
        bool sameGraph = Layers[layer].Key == Active.Key;
        if (_kind == CurveKind.Life)
            _active = layer;
        else
            _masterActive = layer;
        if (!sameGraph)
            _selection.Clear();
        Notify();
    }

    // Opens a graph key (the inspector's thumbnail): its kind, its first channel.
    public void Focus(string key)
    {
        var kind = LifeLayers.Any(l => l.Key == key) ? CurveKind.Life : CurveKind.Master;
        SetKind(kind);
        int at = Array.FindIndex(kind == CurveKind.Life ? LifeLayers : MasterLayers, l => l.Key == key);
        if (at >= 0)
            SetActive(at);
    }

    // ---- reading ----

    public List<GraphKey> Keys(CurveLayer layer) =>
        Emitter == null ? new List<GraphKey>() : GraphKeys.Read(Emitter, layer.Key, layer.Channels);

    public Graph Curve(CurveLayer layer) => Graph.FromNumbers(GraphKeys.Numbers(Keys(layer)), layer.Channels);

    public bool Present(CurveLayer layer) => Emitter?.Has(layer.Key) == true;

    public bool Modified(CurveLayer layer)
    {
        if (Doc == null || Emitter == null)
            return false;
        var original = Doc.OriginalOf(Emitter.Block);
        string? now = Inspector.Raw.Of(Emitter.Block, layer.Key);
        return original == null ? now != null : now != Inspector.Raw.Of(original, layer.Key);
    }

    public string Format(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    // ---- edits ----

    private void Write(List<GraphKey> keys, string label, string? merge = null) => Write(Active.Key, keys, label, merge);

    private void Write(string key, List<GraphKey> keys, string label, string? merge)
    {
        if (Doc == null || Emitter == null)
            return;
        if (!Editable)
        {
            if (merge == null)
                Toast?.Invoke(Loc.F("Toast.Skipped", 1), "warn");
            return;
        }
        Doc.Do(Emitter.SetGraph(key, keys), label, merge);
    }

    private static double Round(double v) => Math.Round(v, Decimals);

    private double SnapTime(double t) => _snap ? Math.Round(t / SnapStep) * SnapStep : Round(t);

    private double SnapValue(double v) => _snap ? Math.Round(v / SnapStep) * SnapStep : Round(v);

    // A key at t whose active channel is v and whose other channels follow the curve as the engine draws it there.
    private GraphKey KeyAt(List<GraphKey> keys, double t, double v)
    {
        var layer = Active;
        var g = Graph.FromNumbers(GraphKeys.Numbers(keys), layer.Channels);
        var values = new double[layer.Channels + 1];
        values[0] = t;
        for (int c = 0; c < layer.Channels; c++)
            values[c + 1] = c == layer.Channel ? v : Round(g.Unclamped((float)t, c));
        return GraphKey.New(values);
    }

    // Double-click: a key at the cursor (Workbench gesture), on the snap grid when it is on.
    public void AddKeyAt(double t, double v)
    {
        var keys = Keys(Active);
        t = Math.Clamp(SnapTime(t), 0, 1);
        v = Math.Clamp(SnapValue(v), 0, ViewTop);
        if (keys.Any(k => Math.Abs(k.Time - t) < SameTime))
            return;
        var key = KeyAt(keys, t, v);
        int at = keys.FindIndex(k => k.Time > t);
        at = at < 0 ? keys.Count : at;
        keys.Insert(at, key);
        Write(keys, Loc.F("Undo.CurveAdd", Loc.T(Active.Name), Format(t * TimeScale)));
        _selection.Clear();
        _selection.Add(at);
        Notify();
    }

    // The "+ key" button: halfway between the selected key and the next (or the last two keys).
    public void AddKey()
    {
        var keys = Keys(Active);
        if (keys.Count == 0)
        {
            AddKeyAt(0, 1);
            return;
        }
        int i = _selection.Count > 0 ? Math.Min(_selection.Max(), keys.Count - 2) : keys.Count - 2;
        double t = i >= 0 ? (keys[i].Time + keys[i + 1].Time) / 2 : Math.Min(1, keys[0].Time + 0.5);
        var g = Graph.FromNumbers(GraphKeys.Numbers(keys), Active.Channels);
        AddKeyAt(t, g.Unclamped((float)Round(t), Active.Channel));
    }

    // Delete: the selected keys; a graph keeps at least one key.
    public void DeleteSelected()
    {
        var keys = Keys(Active);
        var gone = _selection.Where(i => i < keys.Count).OrderByDescending(i => i).ToList();
        if (gone.Count >= keys.Count)
            gone.RemoveAt(gone.Count - 1);
        if (gone.Count == 0)
            return;
        foreach (int i in gone)
            keys.RemoveAt(i);
        Write(keys, Loc.F("Undo.CurveDelete", Loc.T(Active.Name), gone.Count));
        _selection.Clear();
        Notify();
    }

    public void Select(int key, bool add)
    {
        if (!add)
            _selection.Clear();
        if (!_selection.Remove(key) || !add)
            _selection.Add(key);
        Notify();
    }

    public void ClearSelection()
    {
        _selection.Clear();
        Notify();
    }

    public void SelectAll()
    {
        _selection.Clear();
        for (int i = 0; i < Keys(Active).Count; i++)
            _selection.Add(i);
        Notify();
    }

    // Box select in curve coordinates (t and value).
    public void SelectBox(double t0, double v0, double t1, double v1, bool add)
    {
        if (!add)
            _selection.Clear();
        var keys = Keys(Active);
        for (int i = 0; i < keys.Count; i++)
        {
            double v = Math.Clamp(keys[i].Values[Active.Channel + 1], 0, ViewTop);
            if (keys[i].Time >= Math.Min(t0, t1) && keys[i].Time <= Math.Max(t0, t1) && v >= Math.Min(v0, v1) && v <= Math.Max(v0, v1))
                _selection.Add(i);
        }
        Notify();
    }

    public void BeginDrag()
    {
        _dragStart = Keys(Active);
        _dragMerge = "curve-drag:" + Active.Key + ":" + (++_dragCount).ToString(CultureInfo.InvariantCulture);
    }

    // Moves the selected keys by dt / dv from where the drag began. Keys keep their order (a key stops 0.001 before
    // an unselected neighbour), time stays in 0..1, values in the drawn range; one undo step for the whole drag.
    public (double T, double V)? DragTo(double dt, double dv)
    {
        if (_dragStart is not { } start || _selection.Count == 0)
            return null;
        var sel = _selection.Where(i => i < start.Count).OrderBy(i => i).ToList();
        if (sel.Count == 0)
            return null;
        const double Gap = 0.001;
        double lo = -1, hi = 1;
        foreach (int i in sel)
        {
            double t0 = start[i].Time;
            lo = Math.Max(lo, i == 0 ? -t0 : sel.Contains(i - 1) ? lo : start[i - 1].Time + Gap - t0);
            hi = Math.Min(hi, i == start.Count - 1 ? 1 - t0 : sel.Contains(i + 1) ? hi : start[i + 1].Time - Gap - t0);
        }
        dt = Math.Clamp(dt, lo, Math.Max(lo, hi));
        var keys = start.ToList();
        int ch = Active.Channel + 1;
        foreach (int i in sel)
        {
            double t = Round(start[i].Time + dt);
            if (_snap && sel.Count == 1)
                t = Math.Clamp(SnapTime(t), start[i].Time + lo, start[i].Time + hi);
            double v = Math.Clamp(SnapValue(start[i].Values[ch] + dv), 0, ViewTop);
            keys[i] = start[i].With(0, Round(t)).With(ch, v);
        }
        Write(keys, Loc.F("Undo.CurveDrag", Loc.T(Active.Name), sel.Count), _dragMerge);
        return (keys[sel[0]].Time, keys[sel[0]].Values[ch]);
    }

    public void EndDrag()
    {
        if (_dragStart != null)
            Doc?.Seal();
        _dragStart = null;
        _dragMerge = null;
        Notify();
    }

    // Typed time (in the shown unit) of key i: kept between its neighbours.
    public void SetTime(int i, string text)
    {
        var keys = Keys(Active);
        if (i >= keys.Count || !Expr.TryApply(text, keys[i].Time * TimeScale, out double typed))
        {
            Notify();
            return;
        }
        double lo = i > 0 ? keys[i - 1].Time + 0.001 : 0, hi = i + 1 < keys.Count ? keys[i + 1].Time - 0.001 : 1;
        double t = Math.Clamp(typed / TimeScale, lo, Math.Max(lo, hi));
        if (Math.Abs(t - typed / TimeScale) > 1e-9)
            Toast?.Invoke(Loc.F("Toast.CurveTimeClamped", Format(t * TimeScale)), "warn");
        keys[i] = keys[i].With(0, t);
        Write(keys, Loc.F("Undo.CurveTime", Loc.T(Active.Name), i + 1));
        Notify();
    }

    // Typed value of key i in the active channel: any number is written (values above 1 act as 1 in game).
    public void SetValue(int i, string text)
    {
        var keys = Keys(Active);
        int ch = Active.Channel + 1;
        if (i >= keys.Count || !Expr.TryApply(text, keys[i].Values[ch], out double v) || !double.IsFinite(v))
        {
            Notify();
            return;
        }
        keys[i] = keys[i].With(ch, v);
        Write(keys, Loc.F("Undo.CurveValue", Loc.T(Active.Name), i + 1));
        Notify();
    }

    public void Copy()
    {
        var keys = Keys(Active);
        var picked = _selection.Where(i => i < keys.Count).OrderBy(i => i).Select(i => keys[i]).ToList();
        if (picked.Count == 0)
            return;
        _clipboard = (picked, Active.Channels, Active.Channel);
        Toast?.Invoke(Loc.F("Toast.CurveCopied", picked.Count), "info");
        Notify();
    }

    // Pasted keys land at their times: a key already there is replaced. Colour to colour keeps all three channels;
    // otherwise the copied channel goes into the active one and the other channels follow the curve.
    public void Paste()
    {
        if (_clipboard is not { } clip)
            return;
        var keys = Keys(Active);
        foreach (var src in clip.Keys)
        {
            double t = src.Time;
            GraphKey key = Active.Channels == clip.Channels
                ? src
                : KeyAt(keys, t, src.Values[clip.Channels > 1 ? clip.Channel + 1 : 1]);
            int same = keys.FindIndex(k => Math.Abs(k.Time - t) < SameTime / 4);
            if (same >= 0)
                keys[same] = key;
            else
                keys.Add(key);
        }
        keys.Sort((a, b) => a.Time.CompareTo(b.Time));
        Write(keys, Loc.F("Undo.CurvePaste", Loc.T(Active.Name), clip.Keys.Count));
        _selection.Clear();
        Notify();
    }

    // ---- the colour strip: stops are the keys of the colour graph and of its alpha graph ----

    public CurveLayer ColorLayer => Layers[0];
    public CurveLayer AlphaLayer => Layers[3];
    public bool ShowsStrip => Active.Key == ColorLayer.Key || Active.Key == AlphaLayer.Key;

    public void PickStop(bool alpha, int key)
    {
        SetActive(alpha ? 3 : Active.Key == ColorLayer.Key ? ActiveIndex : 0);
        Select(key, false);
    }

    public void AddStop(bool alpha, double t)
    {
        SetActive(alpha ? 3 : 0);
        var g = Curve(Active);
        AddKeyAt(t, g.Evaluate((float)Round(t), Active.Channel));
    }

    // Every channel of one stop at once (the colour popup): one undo step per popup session (merge).
    public void SetStop(bool alpha, int key, IReadOnlyList<double> values, string merge)
    {
        var layer = alpha ? AlphaLayer : ColorLayer;
        var keys = Keys(layer);
        if (key >= keys.Count)
            return;
        for (int c = 0; c < values.Count && c < layer.Channels; c++)
            keys[key] = keys[key].With(c + 1, Round(values[c]));
        Write(layer.Key, keys, Loc.F("Undo.CurveStop", Loc.T(layer.IsColor ? "Curve.Colour" : layer.Name), key + 1), merge);
        Notify();
    }

    // A stop dragged along the strip: its time, kept between its neighbours (one undo step per drag).
    public void MoveStop(bool alpha, int key, double t, string merge)
    {
        var layer = alpha ? AlphaLayer : ColorLayer;
        var keys = Keys(layer);
        if (key >= keys.Count)
            return;
        double lo = key > 0 ? keys[key - 1].Time + 0.001 : 0, hi = key + 1 < keys.Count ? keys[key + 1].Time - 0.001 : 1;
        keys[key] = keys[key].With(0, Math.Clamp(SnapTime(t), lo, Math.Max(lo, hi)));
        Write(layer.Key, keys, Loc.F("Undo.CurveStop", Loc.T(layer.IsColor ? "Curve.Colour" : layer.Name), key + 1), merge);
        Notify();
    }

    public void Seal()
    {
        Doc?.Seal();
        Notify();
    }

    // ---- authored curves: presets and drawing, baked to engine keys ----

    public void ApplyPreset(string name)
    {
        var (curve, corners) = CurvePresets.Get(name);
        var layer = Active;
        // Colour graphs take the preset in all three channels (a fade of brightness).
        Bake((t, _) => curve(t), corners, layer.IsColor ? -1 : layer.Channel, Loc.F("Undo.CurvePreset", Loc.T(layer.Name), Loc.T("Preset." + name)));
    }

    // A drawn stroke (t, value) replaces the active channel; the other channels of a colour graph keep their curve
    // as the engine draws it now, and are baked together with the stroke.
    public void ApplyStroke(IReadOnlyList<(double T, double V)> points)
    {
        if (points.Count < 2)
            return;
        var stroke = GraphBake.Polyline(points);
        double from = points.Min(p => p.T), to = points.Max(p => p.T);
        var old = Curve(Active);
        int channel = Active.Channel;
        // Outside the stroke's time span the curve stays as it was.
        Func<double, int, double> f = (t, c) => c == channel && t >= from && t <= to ? stroke(t) : old.Evaluate((float)t, c);
        var corners = points.Select(p => p.T).Append(from).Append(to).ToArray();
        Bake(f, corners, -2, Loc.F("Undo.CurveDraw", Loc.T(Active.Name)));
    }

    // channel -1: the same curve in every channel; -2: f gives each channel; otherwise f fills that channel and the
    // other channels keep their engine curve.
    private void Bake(Func<double, int, double> f, IReadOnlyList<double> corners, int channel, string label)
    {
        var layer = Active;
        var old = Curve(layer);
        Func<double, int, double> all = channel switch
        {
            -1 => (t, _) => f(t, 0),
            -2 => f,
            _ => (t, c) => c == channel ? f(t, c) : old.Evaluate((float)t, c),
        };
        var result = GraphBake.Fit(all, layer.Channels, _maxError, corners);
        var keys = new List<GraphKey>();
        int stride = layer.Channels + 1;
        for (int k = 0; k < result.Keys; k++)
            keys.Add(GraphKey.New(result.Numbers.Skip(k * stride).Take(stride).ToArray()));
        Write(keys, label);
        BakeMissed = !result.WithinLimit;
        BakeText = Loc.F(result.WithinLimit ? "Curve.Baked" : "Curve.BakedMissed", result.Keys, Format(result.Error), Format(_maxError));
        _selection.Clear();
        Notify();
    }

    private void Notify()
    {
        RaiseAll();
        Changed?.Invoke();
    }
}
