using System.Globalization;
using Pfx.App.Inspector;
using Pfx.App.Model;
using Pfx.Core.Model;
using Pfx.Core.Schema;
using Pfx.Render;
using Pfx.Sim;
using Pfx.Textures;
using PolyLab.Ui.Core;

namespace Pfx.App.Textures;

public enum SheetView { Checker, Black, Alpha }

// A particle the slicer plays to show the flipbook: its age on the slicer's own clock and its start tile.
public sealed record SampleParticle(float Age, int Start, int Frame, bool Held);

// The frame slicer (spec 3.2, board 12) for the first selected emitter's material texture: the TileU x TileV grid of
// the material over the image, the flipbook played by the game's rule (Pfx.Sim.Flipbook, the same function the
// simulator draws with), the live particles of the viewport counted per tile, and the sprite pivot and scale. Grid
// edits write the material, frame and pivot edits write the emitter, all into the effect's one undo history (drags
// merge into one step). State: the documents own every value; this object owns only the view (which emitter, the
// decoded image, the shown tile, the slicer clock and speed).
public sealed class SlicerModel : ObservableObject, ISheetSource
{
    // Grid fields accept 1..64 tiles per side (a UI limit: the largest vanilla grid is 8 x 5; the engine key has no range).
    public const int MaxTiles = 64;
    public const int Samples = 4;
    public static readonly double[] Speeds = { 1, 0.25, 0.05 };

    private readonly Func<string, string?> _resolve;
    private readonly TextureCache _cache;
    private int _load;
    private SheetView _view;
    private bool _numbers = true;
    private int _selected;
    private bool _playing = true;
    private double _speed = 0.25;
    private double _clock;
    private string? _merge;

    public SlicerModel(Func<string, string?> resolve, TextureCache cache)
    {
        _resolve = resolve;
        _cache = cache;
    }

    public EffectDoc? Doc { get; private set; }
    public EmitterView? Emitter { get; private set; }
    public int EmitterIndex { get; private set; } = -1;
    public int SelectedEmitters { get; private set; }
    public MaterialDoc? Material { get; private set; }
    public string? TextureGamePath { get; private set; }
    public string? TexturePath { get; private set; }
    public RgbaImage[]? Levels { get; private set; }
    public TextureSummary? Summary { get; private set; }
    public string? LoadError { get; private set; }
    public bool Loading { get; private set; }
    public EmitterParams? Params { get; private set; }
    public int[] Live { get; private set; } = Array.Empty<int>();
    public int LiveTotal { get; private set; }

    public event Action? Changed;
    // Every presented frame while the slicer is shown: the clock moved and the live counts were taken.
    public event Action? Ticked;
    public event Action<string, string>? Toast;

    public int TileU => Math.Clamp((int)(Material?.Emat.Number("TileU") ?? 1), 1, MaxTiles);
    public int TileV => Math.Clamp((int)(Material?.Emat.Number("TileV") ?? 1), 1, MaxTiles);
    public int Frames => TileU * TileV;
    public bool Editable => Emitter != null && Doc != null && !Doc.Locked.Contains(Emitter.Block);
    public bool MaterialEditable => Editable && Material != null;

    public SheetView View { get => _view; set { if (Set(ref _view, value)) Notify(); } }
    public bool Numbers { get => _numbers; set { if (Set(ref _numbers, value)) Notify(); } }
    public int Selected { get => Math.Min(_selected, Frames - 1); set { if (Set(ref _selected, Math.Clamp(value, 0, Frames - 1))) Notify(); } }
    public bool Playing { get => _playing; set { if (Set(ref _playing, value)) Notify(); } }
    public double Speed { get => _speed; set { if (Set(ref _speed, value)) Notify(); } }
    public double Clock => _clock;
    public int PlayingFrame => SampleParticles() is { Count: > 0 } s ? s[0].Frame : -1;

    public double CenterX => Emitter?.Number("CenterX") ?? 0;
    public double CenterY => Emitter?.Number("CenterY") ?? 0;
    public double ScaleX => Emitter?.Number("ScaleX") ?? 1;
    public double ScaleY => Emitter?.Number("ScaleY") ?? 1;
    public double SizeMultiplier => Emitter?.Number("SizeMultiplier") ?? 1;

    public static SchemaRange RangeOf(string key) => PfxSchema.Shared.Find(SchemaScope.Emitor, key)?.Range ?? new SchemaRange(-1, 1, 0.01);

    // A life the samples share: LifeTime (or tiles / AnimFPS with LifetimeByAnim) without its random part.
    public float SampleLife => Params is { } p && p.BaseLifeTime > 0 ? p.BaseLifeTime : 1;

    public void Show(EffectDoc? doc, EmitterView? emitter, int selected)
    {
        bool same = doc == Doc && emitter?.Block == Emitter?.Block;
        Doc = doc;
        Emitter = emitter;
        SelectedEmitters = selected;
        EmitterIndex = doc != null && emitter != null ? doc.Ptc.Emitters.ToList().FindIndex(e => e.Block == emitter.Block) : -1;
        string? material = emitter?.Text("Material");
        Material = material != null && doc != null && doc.Materials.TryGetValue(material, out var m) ? m : null;
        string? map = Material?.Emat.Block.FindLine("AlbedoMap")?.Values.FirstOrDefault()?.Value;
        if (!same)
            _merge = null;
        if (map != TextureGamePath || !same)
            Load(map);
        Refresh();
    }

    // The emitter or material changed (an edit, undo, the inspector): re-read values; a new texture path loads again.
    public void Refresh()
    {
        if (Emitter == null || Doc == null)
        {
            Params = null;
            Notify();
            return;
        }
        string? map = Material?.Emat.Block.FindLine("AlbedoMap")?.Values.FirstOrDefault()?.Value;
        if (map != TextureGamePath)
            Load(map);
        Params = EmitterParams.From(Emitter, _ => (TileU, TileV));
        if (Live.Length != Frames)
            Live = new int[Frames];
        Notify();
    }

    // The file behind the texture was rewritten by the tool (atlas export): decode it again.
    public void Reload()
    {
        if (TexturePath != null)
            _cache.Forget(TexturePath);
        Load(TextureGamePath);
    }

    private void Load(string? gamePath)
    {
        TextureGamePath = gamePath;
        TexturePath = gamePath != null ? _resolve(gamePath) : null;
        Levels = null;
        Summary = null;
        LoadError = gamePath != null && TexturePath == null ? Loc.F("Slicer.TextureMissing", gamePath) : null;
        Loading = TexturePath != null;
        int token = ++_load;
        if (TexturePath is not { } path)
            return;
        var ui = TaskScheduler.FromCurrentSynchronizationContext();
        Task.Run(() => (Levels: _cache.Decoded(path), Summary: TextureSummary.Read(path))).ContinueWith(t =>
        {
            if (token != _load)
                return;
            Loading = false;
            if (t.Exception is { } ex)
                LoadError = Loc.F("Slicer.TextureBroken", Path.GetFileName(path), (ex.InnerException ?? ex).Message);
            else if (t.Result.Levels == null)
                LoadError = Loc.F("Slicer.TextureBroken", Path.GetFileName(path), "");
            else
                (Levels, Summary) = t.Result;
            Notify();
        }, ui);
    }

    // ---- the flipbook as the game plays it ----

    public int FrameAt(int start, float age) => Params != null ? Flipbook.FrameAt(Params, start, age) : 0;

    // Sample particles spread evenly over one life on the slicer clock. With RandomFrame their start tiles are fixed
    // draws spread over the sheet (the simulator draws its own from the emitter's stream; the rule is the same).
    public IReadOnlyList<SampleParticle> SampleParticles()
    {
        var list = new List<SampleParticle>(Samples);
        if (Params is not { } p)
            return list;
        float life = SampleLife;
        for (int k = 0; k < Samples; k++)
        {
            float age = (float)((_clock + k * life / Samples) % life);
            int start = p.RandomFrame ? Flipbook.RandomStart(p, (float)((0.1 + k * 0.618034) % 1.0)) : 0;
            int frame = Flipbook.FrameAt(p, start, age);
            bool held = p.AnimOnce && start + (int)(age * Flipbook.Rate(p)) >= p.Frames - 1;
            list.Add(new SampleParticle(age, start, frame, held));
        }
        return list;
    }

    // Called after every presented frame while the slicer is shown: the slicer clock and the live counts per tile.
    public void Tick(double seconds, EffectPreview? preview)
    {
        if (_playing && Params != null)
            _clock = (_clock + seconds * _speed) % Math.Max(1e-3, SampleLife);
        LiveTotal = 0;
        if (preview != null && EmitterIndex >= 0 && preview.Sims.Count > 0 && EmitterIndex < preview.Sims[0].Emitters.Count)
        {
            var sim = preview.Sims[0].Emitters[EmitterIndex];
            if (Live.Length != sim.Params.Frames)
                Live = new int[sim.Params.Frames];
            sim.CountFrames(Live);
            LiveTotal = sim.Count;
        }
        else
        {
            Array.Clear(Live);
        }
        Ticked?.Invoke();
    }

    // ---- edits ----

    public void SetTiles(int u, int v)
    {
        if (!MaterialEditable || Material == null)
            return;
        u = Math.Clamp(u, 1, MaxTiles);
        v = Math.Clamp(v, 1, MaxTiles);
        var emat = Material.Emat;
        Steps(Loc.F("Undo.SetTiles", u, v), null,
            () => emat.Number("TileU") == u && emat.Has("TileU") ? null : emat.Set("TileU", u),
            () => emat.Number("TileV") == v && emat.Has("TileV") ? null : emat.Set("TileV", v));
    }

    public void SetFps(double fps)
    {
        if (!Editable || Emitter == null || Doc == null)
            return;
        var range = RangeOf("AnimFPS");
        double clamped = Math.Clamp(fps, range.Min, range.Max);
        if (clamped != fps)
            Toast?.Invoke(Loc.F("Toast.Clamped", Loc.T("Slicer.Fps"), Loc.Num(clamped)), "warn");
        if (Emitter.Number("AnimFPS") == clamped && Emitter.Has("AnimFPS"))
            return;
        Doc.Do(Emitter.Set("AnimFPS", clamped), Loc.F("Undo.Set", "AnimFPS"));
    }

    // Written as the file already spells it (0/1 or false/true), new keys as 0/1, like the inspector's switches.
    public void Toggle(string key)
    {
        if (!Editable || Emitter == null || Doc == null)
            return;
        string? now = Emitter.Text(key);
        bool on = now is "1" or "true" or "True";
        bool words = now is "true" or "false";
        Doc.Do(Emitter.SetWord(key, words ? (on ? "false" : "true") : (on ? "0" : "1")), Loc.F(on ? "Undo.Off" : "Undo.On", key));
    }

    // Pivot and scale share one undo step per drag: call with drag = true while dragging, then EndDrag.
    public void SetPivot(double cx, double cy, bool drag) => SetPair("CenterX", cx, "CenterY", cy, drag, "Undo.Pivot");

    public void SetScale(double sx, double sy, bool drag) => SetPair("ScaleX", sx, "ScaleY", sy, drag, "Undo.SpriteScale");

    public void EndDrag()
    {
        if (_merge == null)
            return;
        _merge = null;
        Doc?.Seal();
    }

    private void SetPair(string keyX, double x, string keyY, double y, bool drag, string label)
    {
        if (!Editable || Emitter == null)
            return;
        var e = Emitter;
        foreach (var (key, v) in new[] { (keyX, x), (keyY, y) })
            if (!drag && (v < RangeOf(key).Min || v > RangeOf(key).Max))
                Toast?.Invoke(Loc.F("Toast.Clamped", key, Loc.Num(Snap(key, v))), "warn");
        x = Snap(keyX, x);
        y = Snap(keyY, y);
        string? merge = drag ? _merge ??= label + ":" + Guid.NewGuid().ToString("N") : null;
        Steps(Loc.T(label), merge,
            () => e.Number(keyX) == x && e.Has(keyX) ? null : e.Set(keyX, x),
            () => e.Number(keyY) == y && e.Has(keyY) ? null : e.Set(keyY, y));
    }

    // Edits splice token lists at the indices they were made with, so each is made after the one before it has run;
    // one merge key makes them one undo step. Without a drag key the step is sealed at once.
    private void Steps(string label, string? merge, params Func<Pfx.Core.Editing.IEdit?>[] makers)
    {
        if (Doc == null)
            return;
        string key = merge ?? "slicer:" + Guid.NewGuid().ToString("N");
        foreach (var make in makers)
            if (make() is { } edit)
                Doc.Do(edit, label, key);
        if (merge == null)
            Doc.Seal();
    }

    // Values stay inside the schema range on its step (CenterX/Y -1..1, ScaleX/Y 0.1..1, step 0.01).
    private static double Snap(string key, double v)
    {
        var r = RangeOf(key);
        double step = r.Step ?? 0.01;
        return Math.Round(Math.Clamp(Math.Round(v / step) * step, r.Min, r.Max), 6);
    }

    public void ResetPivotToFile()
    {
        if (!Editable || Emitter == null || Doc?.OriginalOf(Emitter.Block) is not { } original)
            return;
        var e = Emitter;
        Func<Pfx.Core.Editing.IEdit?> Back(string key) => () =>
        {
            string? was = Raw.Of(original, key);
            if (was == Raw.Of(e.Block, key))
                return null;
            return was == null ? e.Reset(key)
                : e.Set(key, double.Parse(was, NumberStyles.Float, CultureInfo.InvariantCulture));
        };
        Steps(Loc.T("Undo.PivotFile"), null, Back("CenterX"), Back("CenterY"), Back("ScaleX"), Back("ScaleY"));
    }

    public void DefaultPivot()
    {
        if (!Editable || Emitter == null)
            return;
        var e = Emitter;
        Func<Pfx.Core.Editing.IEdit?> To(string key, double v) => () => e.Number(key) == v ? null : e.Set(key, v);
        Steps(Loc.T("Undo.PivotDefault"), null, To("CenterX", 0), To("CenterY", 0), To("ScaleX", 1), To("ScaleY", 1));
    }

    private void Notify() => Changed?.Invoke();
}
