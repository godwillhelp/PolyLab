using System.Globalization;
using Pfx.App.Model;
using Pfx.Core.Editing;
using Pfx.Core.Model;
using Pfx.Textures;
using PolyLab.Ui.Core;

namespace Pfx.App.Textures;

public sealed record AtlasFrame(string Name, RgbaImage Image);

// Worded when shown (Loc on the UI thread, in the language of the moment): a key, its arguments and a kind.
public sealed record AtlasNote(string Key, string Kind, params object[] Args)
{
    public string Text => Args.Length == 0 ? Loc.T(Key) : Loc.F(Key, Args);

    public string Brush => Kind switch { "warn" => "B.Warn", "err" => "B.Err", "ok" => "B.Ok", _ => "B.Info" };
}

// What one build of the atlas produced: the top level before brightness goes into alpha (what EddsFile.Build starts
// from), the mip chain as the EDDS will store it (flipbook-aware, brightness packed per level when asked), the grid,
// the settings it was built with and the notes about it.
public sealed class AtlasException(AtlasNote note) : Exception(note.Key)
{
    public AtlasNote Note { get; } = note;
}

public sealed record AtlasResult(RgbaImage Top, RgbaImage[] Levels, int TileU, int TileV, EddsFormat Format, bool Packed, int MaxSide,
    long GpuBytes, IReadOnlyList<AtlasNote> Notes, (int Semi, int AtMostAlpha) Premultiply);

// The atlas builder (spec 3.2, board 4): N pictures or a picture sequence → a power-of-two flipbook atlas with every
// frame (AtlasPacker), the texture steps of the anomaly work (colour ^ gamma, colour bled under alpha 0, brightness in
// alpha for additive materials, each frame's mean alpha and brightness matched to its source), optional CPU upscale,
// mip and alpha views, video memory, export to EDDS (EddsFile.Build: ENF1 always, no NPOT DXT, maximum side) and
// assignment to the emitter's material. Building runs on a worker, newest settings win; the UI thread only swaps in
// the result. State: frames, options and the last result live here; nothing reaches a file until Export.
public sealed class AtlasBuilder : ObservableObject, ISheetSource
{
    public static readonly int[] Sides = { 64, 128, 256, 512, 1024, 2048, 4096 };

    private readonly List<AtlasFrame> _frames = new();
    private List<RgbaImage>? _upscaled;
    private int _build;
    private bool _building;
    private bool _pending;
    private EddsFormat _format = EddsFormat.Dxt5;
    private int _maxSide = TextureRules.DefaultMaxSide;
    private int _width;
    private int _height;
    private double _gamma = 1;
    private bool _bleed = true;
    private bool _packIntensity;
    private bool _matchMean = true;
    private double _fps = 16;
    private SheetView _view;
    private bool _numbers = true;
    private int _selected;
    private double _clock;
    private CancellationTokenSource? _upscale;

    public IReadOnlyList<AtlasFrame> Frames => _frames;
    public AtlasResult? Result { get; private set; }
    public AtlasNote? Error { get; private set; }
    public bool Building => _building;
    public bool Upscaling => _upscale != null;
    public (int Done, int Total) UpscaleProgress { get; private set; }
    public bool HasUpscaled => _upscaled != null;
    public string? LastExport { get; private set; }

    public event Action? Changed;
    public event Action<string, string>? Toast;

    public EddsFormat Format { get => _format; set { if (Set(ref _format, value)) Rebuild(); } }
    public int MaxSide { get => _maxSide; set { if (Set(ref _maxSide, value)) Rebuild(); } }
    // 0 = the nearest power of two of the frame grid (AtlasPacker.AutoSize).
    public int Width { get => _width; set { if (Set(ref _width, value)) Rebuild(); } }
    public int Height { get => _height; set { if (Set(ref _height, value)) Rebuild(); } }
    public double Gamma { get => _gamma; set { if (Set(ref _gamma, Math.Clamp(value, 0.2, 5))) Rebuild(); } }
    public bool Bleed { get => _bleed; set { if (Set(ref _bleed, value)) Rebuild(); } }
    public bool PackIntensity { get => _packIntensity; set { if (Set(ref _packIntensity, value)) Rebuild(); } }
    public bool MatchMean { get => _matchMean; set { if (Set(ref _matchMean, value)) Rebuild(); } }
    // The AnimFPS written to the emitter on "apply", and the speed the preview plays at.
    public double Fps { get => _fps; set { if (Set(ref _fps, Math.Clamp(value, 0, 100000))) Notify(); } }

    public SheetView View { get => _view; set { if (Set(ref _view, value)) Notify(); } }
    public bool Numbers { get => _numbers; set { if (Set(ref _numbers, value)) Notify(); } }
    public int Selected { get => _selected; set { if (Set(ref _selected, value)) Notify(); } }
    public int Mip { get; set; }

    RgbaImage[]? ISheetSource.Levels => Result?.Levels;
    public int TileU => Result?.TileU ?? 1;
    public int TileV => Result?.TileV ?? 1;
    public int PlayingFrame => Result != null && _fps > 0 ? (int)(_clock * _fps) % (TileU * TileV) : -1;
    public int[] Live => Array.Empty<int>();

    public void Tick(double seconds)
    {
        if (Result != null)
            _clock = (_clock + seconds) % 3600;
    }

    // ---- frames ----

    // Files sorted by name with numbers compared as numbers (frame_2 before frame_10), read on a worker.
    public void AddFiles(IEnumerable<string> paths)
    {
        var list = paths.OrderBy(p => p, NaturalOrder.Instance).ToList();
        var ui = TaskScheduler.FromCurrentSynchronizationContext();
        Task.Run(() => list.Select(p =>
        {
            try
            {
                return (Frame: new AtlasFrame(Path.GetFileName(p), ImageFiles.Load(p)), Error: (string?)null);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException or ArgumentException)
            {
                return (Frame: (AtlasFrame?)null, Error: $"{Path.GetFileName(p)}: {e.Message}");
            }
        }).ToList()).ContinueWith(t =>
        {
            foreach (var (frame, error) in t.Result)
            {
                if (frame != null)
                    _frames.Add(frame);
                else
                    Toast?.Invoke(Loc.F("Toast.FrameUnreadable", error), "err");
            }
            _upscaled = null;
            Rebuild();
        }, ui);
    }

    // The frames of an existing flipbook (a texture split by its material's grid), to rework it.
    public bool AddSheet(string name, RgbaImage image, int tileU, int tileV)
    {
        if (image.Width % tileU != 0 || image.Height % tileV != 0)
        {
            Toast?.Invoke(Loc.F("Toast.SheetNotWhole", image.Width, image.Height, tileU, tileV), "warn");
            return false;
        }
        var frames = TextureTools.Split(image, tileU, tileV);
        for (int i = 0; i < frames.Count; i++)
            _frames.Add(new AtlasFrame($"{name} #{i}", frames[i]));
        _upscaled = null;
        Rebuild();
        return true;
    }

    public void Remove(int index)
    {
        if (index < 0 || index >= _frames.Count)
            return;
        _frames.RemoveAt(index);
        _upscaled = null;
        Rebuild();
    }

    public void Clear()
    {
        _frames.Clear();
        _upscaled = null;
        Rebuild();
    }

    // ---- building ----

    private void Rebuild()
    {
        if (_building)
        {
            _pending = true;
            Notify();
            return;
        }
        if (_frames.Count == 0)
        {
            Result = null;
            Error = null;
            Notify();
            return;
        }
        _building = true;
        int token = ++_build;
        var job = Snapshot();
        var ui = TaskScheduler.FromCurrentSynchronizationContext();
        Notify();
        Task.Run(() => Run(job)).ContinueWith(t =>
        {
            _building = false;
            if (token == _build)
            {
                if (t.Exception is { } ex)
                {
                    var inner = ex.InnerException ?? ex;
                    Error = inner is AtlasException a ? a.Note : new AtlasNote("Atlas.Error", "err", inner.Message);
                    Result = null;
                }
                else
                {
                    Error = null;
                    Result = t.Result;
                    _selected = Math.Min(_selected, Result.TileU * Result.TileV - 1);
                }
            }
            if (_pending)
            {
                _pending = false;
                Rebuild();
                return;
            }
            Notify();
        }, ui);
    }

    private sealed record Job(IReadOnlyList<RgbaImage> Sources, IReadOnlyList<RgbaImage>? Upscaled, EddsFormat Format, int MaxSide,
        int Width, int Height, double Gamma, bool Bleed, bool PackIntensity, bool MatchMean);

    private Job Snapshot() => new(_frames.Select(f => f.Image).ToList(), _upscaled, _format, _maxSide, _width, _height, _gamma, _bleed,
        _packIntensity, _matchMean);

    // The order of hires.py build(): colour ^ gamma, frames (upscaled ones matched to their source), the atlas, colour
    // bled under alpha 0 on the top level, the flipbook-aware mip chain, brightness into alpha on every level.
    private static AtlasResult Run(Job j)
    {
        var notes = new List<AtlasNote>();
        var first = j.Sources[0];
        var odd = j.Sources.Select((f, i) => (f, i)).Where(x => x.f.Width != first.Width || x.f.Height != first.Height).ToList();
        if (odd.Count > 0)
            throw new AtlasException(new AtlasNote("Atlas.SizeMismatch", "err", odd[0].i + 1, odd[0].f.Width, odd[0].f.Height, first.Width, first.Height));
        var frames = new List<RgbaImage>(j.Sources.Count);
        for (int i = 0; i < j.Sources.Count; i++)
        {
            var f = j.Upscaled?[i] ?? j.Sources[i];
            if (j.Gamma != 1)
                f = TextureTools.Contrast(f, j.Gamma);
            if (j.MatchMean && (j.Gamma != 1 || j.Upscaled != null))
                f = TextureTools.MatchMean(f, j.Sources[i]);
            frames.Add(f);
        }
        var (cols, rows) = AtlasPacker.Grid(frames.Count, frames[0].Width, frames[0].Height);
        var auto = AtlasPacker.AutoSize(frames[0].Width * cols, frames[0].Height * rows, j.MaxSide);
        var atlas = AtlasPacker.Pack(frames, j.MaxSide, (j.Width > 0 ? j.Width : auto.W, j.Height > 0 ? j.Height : auto.H));
        var top = atlas.Image;
        if (j.Bleed)
            top = TextureTools.Bleed(top);
        IReadOnlyList<RgbaImage> levels = TextureTools.MipChain(top, atlas.TileU, atlas.TileV);
        bool pack = j.PackIntensity && j.Format == EddsFormat.Dxt5;
        if (pack)
            levels = levels.Select(TextureTools.PackIntensity).ToList();
        var (w, h) = (top.Width, top.Height);
        double fw = (double)w / atlas.TileU, fh = (double)h / atlas.TileV;
        notes.Add(new AtlasNote("Atlas.NoteGrid", "info", frames.Count, atlas.TileU, atlas.TileV, w, h, Loc.Num(Math.Round(fw, 2)), Loc.Num(Math.Round(fh, 2))));
        if (w % atlas.TileU != 0 || h % atlas.TileV != 0)
            notes.Add(new AtlasNote("Atlas.NoteFraction", "warn"));
        double scale = Math.Max(fw / first.Width, fh / first.Height) * (j.Upscaled != null ? 1.0 / CpuUpscaler.Scale : 1);
        if (fw > first.Width * 8 || fh > first.Height * 8)
            notes.Add(new AtlasNote("Atlas.NoteOver8", "warn"));
        else if (scale > 1.01 && j.Upscaled == null)
            notes.Add(new AtlasNote("Atlas.NoteEnlarged", "info", Loc.Num(Math.Round(scale, 2))));
        if (j.Format == EddsFormat.Dxt1 && j.Sources.Any(HasAlpha))
            notes.Add(new AtlasNote("Atlas.NoteDxt1Alpha", "warn"));
        if (j.PackIntensity && j.Format != EddsFormat.Dxt5)
            notes.Add(new AtlasNote("Atlas.NotePackNeedsDxt5", "warn"));
        var pm = TextureTools.PremultiplyCheck(levels[0]);
        if (pm.Semi >= 64 && pm.AtMostAlpha == pm.Semi && !pack)
            notes.Add(new AtlasNote("Atlas.NotePremultiplied", "warn", pm.Semi));
        return new AtlasResult(top, levels.ToArray(), atlas.TileU, atlas.TileV, j.Format, pack, j.MaxSide, TextureMemory.Edds(j.Format, w, h), notes, pm);
    }

    private static bool HasAlpha(RgbaImage image)
    {
        for (int i = 3; i < image.Pixels.Length; i += 4)
            if (image.Pixels[i] < 255)
                return true;
        return false;
    }

    // ---- upscale (CPU only) ----

    public void StartUpscale(CpuUpscaler upscaler)
    {
        if (_upscale != null || _frames.Count == 0)
            return;
        var cancel = new CancellationTokenSource();
        _upscale = cancel;
        var sources = _frames.Select(f => f.Image).ToList();
        var ui = TaskScheduler.FromCurrentSynchronizationContext();
        var progress = new Progress<(int Done, int Total)>(p =>
        {
            UpscaleProgress = p;
            Notify();
        });
        UpscaleProgress = (0, sources.Count * (sources.Any(HasAlpha) ? 2 : 1));
        Notify();
        Task.Run(() => upscaler.Upscale(sources, sources[0].Width * CpuUpscaler.Scale, sources[0].Height * CpuUpscaler.Scale, progress, cancel.Token))
            .ContinueWith(t =>
            {
                _upscale = null;
                cancel.Dispose();
                if (t.IsCanceled || t.Exception?.InnerException is OperationCanceledException)
                    Toast?.Invoke(Loc.T("Toast.UpscaleCancelled"), "info");
                else if (t.Exception is { } ex)
                    Toast?.Invoke(Loc.F("Toast.UpscaleFailed", (ex.InnerException ?? ex).Message), "err");
                else if (sources.SequenceEqual(_frames.Select(f => f.Image)))
                {
                    _upscaled = t.Result.ToList();
                    Toast?.Invoke(Loc.F("Toast.UpscaleDone", sources.Count), "ok");
                    Rebuild();
                    return;
                }
                Notify();
            }, ui);
    }

    public void CancelUpscale() => _upscale?.Cancel();

    public void DropUpscale()
    {
        _upscaled = null;
        Rebuild();
    }

    // ---- export and assignment ----

    // Writes the EDDS of the shown result on a worker. The caller picked the path in the OS file dialog (which asks
    // before replacing a file); DXT needs texconv. Calls back on the UI thread with the written path.
    public void Export(string path, Texconv? texconv, Action<string> written)
    {
        if (Result is not { } r)
            return;
        var ui = TaskScheduler.FromCurrentSynchronizationContext();
        Task.Run(() =>
        {
            var edds = EddsFile.Build(r.Top, r.Format, r.TileU, r.TileV, r.MaxSide, texconv, r.Packed ? TextureTools.PackIntensity : null);
            Core.AtomicFile.Write(path, edds.ToBytes());
            return edds;
        }).ContinueWith(t =>
        {
            if (t.Exception is { } ex)
            {
                Toast?.Invoke(Loc.F("Toast.ExportFailed", Path.GetFileName(path), (ex.InnerException ?? ex).Message), "err");
                return;
            }
            LastExport = path;
            Toast?.Invoke(Loc.F("Toast.Exported", Path.GetFileName(path), t.Result.Levels.Count, Loc.Num(Math.Round(r.GpuBytes / 1048576.0, 2))), "ok");
            written(path);
            Notify();
        }, ui);
    }

    // Material AlbedoMap = the exported file's game path, TileU / TileV = the grid, emitter AnimFPS = the builder's frame
    // rate: one undo step. The material file may be shared (the same edit reaches its other emitters; the toast says how many).
    public bool Apply(EffectDoc doc, EmitterView emitter, MaterialDoc material, string gamePath)
    {
        if (Result is not { } r)
            return false;
        var emat = material.Emat;
        var line = emat.Block.FindLine("AlbedoMap");
        var values = new List<string> { EnfEdits.Quote(gamePath) };
        if (line != null)
            values.AddRange(line.Values.Skip(1).Select(v => v.Text));
        string merge = "atlas-apply:" + Guid.NewGuid().ToString("N");
        var label = Loc.Later("Undo.ApplyAtlas", Path.GetFileName(gamePath));
        var makers = new Func<IEdit?>[]
        {
            () => EnfEdits.SetLine(emat.Doc, emat.Block, "AlbedoMap", values, k => emat.Schema.Find(emat.Scope, k)?.Order),
            () => emat.Number("TileU") == r.TileU && emat.Has("TileU") ? null : emat.Set("TileU", r.TileU),
            () => emat.Number("TileV") == r.TileV && emat.Has("TileV") ? null : emat.Set("TileV", r.TileV),
            () => emitter.Number("AnimFPS") == _fps && emitter.Has("AnimFPS") ? null : emitter.Set("AnimFPS", _fps),
        };
        foreach (var make in makers)
            if (make() is { } edit)
                doc.Do(edit, label, merge);
        doc.Seal();
        int users = doc.Ptc.Emitters.Count(e => e.Text("Material") == emitter.Text("Material"));
        Toast?.Invoke(users > 1 ? Loc.F("Toast.AppliedShared", users) : Loc.F("Toast.Applied", emitter.Name), "ok");
        return true;
    }

    private void Notify() => Changed?.Invoke();
}

// "frame_2" before "frame_10": digit runs compare as numbers, the rest ordinally (case ignored).
public sealed class NaturalOrder : IComparer<string>
{
    public static readonly NaturalOrder Instance = new();

    public int Compare(string? a, string? b)
    {
        if (a == null || b == null)
            return string.CompareOrdinal(a, b);
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                var na = a[si..i].TrimStart('0');
                var nb = b[sj..j].TrimStart('0');
                int c = na.Length != nb.Length ? na.Length.CompareTo(nb.Length) : string.CompareOrdinal(na, nb);
                if (c != 0)
                    return c;
                continue;
            }
            int d = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
            if (d != 0)
                return d;
            i++;
            j++;
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }
}
