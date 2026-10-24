using System.Diagnostics;
using System.Numerics;
using Pfx.Core.Model;
using Pfx.Sim;

namespace Pfx.Render;

public enum MotionKind { Still, Circle, Turn }

// What the preview shows besides the file: seed, step, low frame rate, source motion and stress-test copies.
public sealed record PreviewSettings
{
    public ulong Seed { get; init; } = 1;
    public float Step { get; init; } = 1f / 60f;
    // Simulated low frame rate in frames per second (0 = off): the simulation steps once per shown frame and the
    // viewport shows only those frames (the vanilla editor fixes 16 fps, W0).
    public float LowFps { get; init; }
    // Length of the frame after the spawn frame in seconds (0 = a normal step): the first, cold spawn (SimOptions).
    public float FirstFrame { get; init; }
    public MotionKind Motion { get; init; }
    // Source position and starting yaw (degrees, + = clockwise seen from above).
    public Vector3 Origin { get; init; }
    public float Yaw { get; init; }
    // Turning source (script spin), degrees per second.
    public float TurnRate { get; init; }
    // The vanilla movement simulation defaults: radius 2 m, speed 4 m/s, axis Y (W0).
    public float CircleRadius { get; init; } = 2;
    public float CircleSpeed { get; init; } = 4;
    public CircleAxis CircleAxis { get; init; } = CircleAxis.Y;
    // Stress test: copies of the effect in a square grid, Spacing metres apart (1 = only the effect).
    public int Copies { get; init; } = 1;
    public float Spacing { get; init; } = 5;
    // Script motion of this part (a drawn path or spin): replaces Motion when set.
    public SourceMotion? Script { get; init; }
    // The runs of this part on the scene clock when a composition plays its states (null = one run from 0 on).
    public WindowSet? Windows { get; init; }

    public float SimStep => LowFps > 0 ? 1f / LowFps : Step;
}

// Windows compared by value, so settings rebuilt with the same runs still let a rebuild reuse the old checkpoints.
public sealed record WindowSet(IReadOnlyList<SimWindow> Items)
{
    public bool Equals(WindowSet? other) => other != null && Items.SequenceEqual(other.Items);

    public override int GetHashCode() => Items.Aggregate(Items.Count, (h, w) => HashCode.Combine(h, w));
}

// A source motion moved by a fixed offset (stress-test copies).
public sealed record ShiftedMotion(SourceMotion Inner, Vector3 By) : SourceMotion
{
    public override (Vector3 Position, float Yaw, Vector3 Velocity) At(float t)
    {
        var (p, yaw, v) = Inner.At(t);
        return (p + By, yaw, v);
    }
}

// Every stress-test copy of a preview at one simulation step: a checkpoint, or the result of a seek job. Read-only.
public sealed class PreviewState
{
    internal PreviewState(long steps, float time, IReadOnlyList<EffectSimState> sims)
    {
        Steps = steps;
        Time = time;
        Sims = sims;
    }

    public long Steps { get; }
    public float Time { get; }
    internal IReadOnlyList<EffectSimState> Sims { get; }
    public long Bytes => Sims.Sum(s => s.Bytes);
}

public sealed record ReuseSource(IReadOnlyList<PreviewState> Checkpoints, IReadOnlyList<string> Keys, PreviewSettings Settings, long FilledSteps);

// A seek prepared on the UI thread (PrepareSeek) and run on a worker: it copies the nearest earlier checkpoint into
// its own simulations and steps them to the target, exact there (EffectSim fast-forward). The preview takes the
// result with Apply, so the UI thread only copies a state. Inputs are read-only, so the preview plays on meanwhile.
public sealed class SeekJob
{
    private readonly IReadOnlyList<EmitterParams> _params;
    private readonly IReadOnlyList<(SimOptions Options, SourceMotion Motion)> _sims;
    private readonly PreviewState _from;

    internal SeekJob(EffectPreview preview, int generation, float time, long steps, PreviewState from)
    {
        Preview = preview;
        Generation = generation;
        Time = time;
        TargetSteps = steps;
        _params = preview.Params;
        _sims = preview.Sims.Select(s => (s.Options, s.Motion)).ToList();
        _from = from;
    }

    public EffectPreview Preview { get; }
    internal int Generation { get; }
    public float Time { get; }
    public long TargetSteps { get; }
    public float FromTime => _from.Time;
    public PreviewState? Result { get; private set; }
    public double Ms { get; private set; }

    public void Run()
    {
        var clock = Stopwatch.StartNew();
        var states = new List<EffectSimState>();
        foreach (var (options, motion) in _sims)
        {
            var sim = new EffectSim(_params, options, motion);
            int copy = states.Count;
            sim.Load(_from.Sims[copy]);
            float t = TargetSteps * options.Step;
            sim.RunTo(t, exactAt: t);
            states.Add(sim.Save());
        }
        Result = new PreviewState(TargetSteps, TargetSteps * _sims[0].Options.Step, states);
        Ms = clock.Elapsed.TotalMilliseconds;
    }
}

// Simulates, on a worker, a stretch the preview jumped over (a rebuild at the playback time, a seek far ahead) with full
// steps from the last state before it, and takes a checkpoint after every CheckpointWorkMs of measured work, so later
// seeks into the stretch are short. Stops when cancelled (the preview was replaced).
public sealed class FillJob
{
    private readonly IReadOnlyList<EmitterParams> _params;
    private readonly IReadOnlyList<(SimOptions Options, SourceMotion Motion)> _sims;
    private readonly PreviewState _from;
    private readonly List<PreviewState> _taken = new();

    internal FillJob(EffectPreview preview, int generation, PreviewState from, long to)
    {
        Preview = preview;
        Generation = generation;
        _params = preview.Params;
        _sims = preview.Sims.Select(s => (s.Options, s.Motion)).ToList();
        _from = from;
        To = to;
    }

    public EffectPreview Preview { get; }
    internal int Generation { get; }
    public long To { get; }
    public bool Done { get; private set; }
    public IReadOnlyList<PreviewState> Taken => _taken;

    public void Run(CancellationToken cancel)
    {
        var clock = Stopwatch.StartNew();
        var sims = _sims.Select((s, i) =>
        {
            var sim = new EffectSim(_params, s.Options, s.Motion);
            sim.Load(_from.Sims[i]);
            return sim;
        }).ToList();
        double work = 0, last = 0;
        while (sims[0].Steps < To)
        {
            if (cancel.IsCancellationRequested)
                return;
            foreach (var sim in sims)
                sim.Step();
            double now = clock.Elapsed.TotalMilliseconds;
            work += now - last;
            last = now;
            if (work >= EffectPreview.CheckpointWorkMs && sims[0].Steps < To)
            {
                _taken.Add(new PreviewState(sims[0].Steps, sims[0].Time, sims.Select(s => s.Save()).ToList()));
                work = 0;
            }
        }
        Done = true;
    }
}

// The effect being previewed: its emitters, materials, simulations and playback clock. Everything here is CPU state
// that survives a lost device.
public sealed class EffectPreview
{
    // A checkpoint is taken after every this much measured simulation work: a seek restores the nearest earlier
    // checkpoint and simulates at most this long, inside the 0.1 s a response still feels immediate (Card, Moran and
    // Newell's perceptual processor; Nielsen's response-time limits).
    public const double CheckpointWorkMs = 100;
    // Checkpoints beyond this many bytes are thinned (every second one dropped, which doubles the spacing). A UI choice:
    // 50k particles hold 4 MB per checkpoint and take one about every 2 s of effect time (Release, P6), so this keeps
    // about four minutes of such a scene at full density.
    public const long CheckpointBudgetBytes = 512L << 20;

    private double _workSinceCheckpoint;
    private int _generation;
    // The engine bakes the graphs into a 256-wide texture row per emitter (R0: texsParamsTexture 256 x 256,
    // vPartTexture.w = 1/256); three rows per emitter here: life colour + alpha, master colour + alpha, sizes.
    public const int ParamsWidth = 256;
    public const int RowsPerEmitter = 3;

    private PreviewSettings _settings;

    public EffectPreview(PtcDocument doc, string path, DataRoots roots, PreviewSettings? settings = null)
    {
        Doc = doc;
        FilePath = path;
        Roots = roots;
        foreach (var e in doc.Emitters)
            if (e.Text("Material") is { } m)
                roots.AddRootFor(path, m);
        Materials = doc.Emitters.Select(e => roots.Material(e.Text("Material"))).ToList();
        foreach (var m in Materials)
            foreach (var map in new[] { m.AlbedoMap, m.NormalMap })
                if (map != null)
                    roots.AddRootFor(m.Path ?? path, map);
        AlbedoPaths = Materials.Select(m => m.AlbedoMap != null ? roots.Resolve(m.AlbedoMap) : null).ToList();
        NormalPaths = Materials.Select(m => m.NormalMap != null ? roots.Resolve(m.NormalMap) : null).ToList();
        Params = doc.Emitters.Select(e => EmitterParams.From(e, roots.Tiles())).ToList();
        SortParticles = doc.Emitters.Select(e => (e.Number("Sort") ?? 0) != 0).ToList();
        BillboardModes = doc.Emitters.Select(e => e.Text("BillboardingType") switch
        {
            "LockedAxis" => 1,
            "None" => 2,
            _ => 0,
        }).ToList();
        Scales = doc.Emitters.Select(e => new Vector4((float)(e.Number("ScaleX") ?? 1), (float)(e.Number("ScaleY") ?? 1),
            (float)(e.Number("CenterX") ?? 0), (float)(e.Number("CenterY") ?? 0))).ToList();
        ParamsTexture = Bake(Params);
        EmitterKeys = doc.Emitters.Select((e, i) =>
        {
            var sb = new System.Text.StringBuilder();
            e.Block.Write(sb);
            return sb.Append('|').Append(Params[i].TileU).Append('x').Append(Params[i].TileV).ToString();
        }).ToList();
        _settings = settings ?? new PreviewSettings();
        Sims = new List<EffectSim>();
        Restart();
    }

    public static EffectPreview Load(string path, DataRoots roots, PreviewSettings? settings = null) =>
        new(PtcDocument.Load(path), path, roots, settings);

    public PtcDocument Doc { get; }
    public string FilePath { get; }
    public DataRoots Roots { get; }
    public IReadOnlyList<EmitterParams> Params { get; }
    public IReadOnlyList<MaterialInfo> Materials { get; }
    // Texture files on disk per emitter (null = no map in the material, or not found under the roots).
    public IReadOnlyList<string?> AlbedoPaths { get; }
    public IReadOnlyList<string?> NormalPaths { get; }
    public IReadOnlyList<bool> SortParticles { get; }
    public IReadOnlyList<int> BillboardModes { get; }
    public IReadOnlyList<Vector4> Scales { get; }
    public byte[] ParamsTexture { get; }
    public int ParamsRows => Math.Max(1, Params.Count * RowsPerEmitter);
    public List<EffectSim> Sims { get; }
    public List<Vector3> CopyOffsets { get; } = new();
    // What makes an emitter simulate the same as in another preview with equal settings: its text and its flipbook
    // grid, at the same index (the index picks the random stream).
    public IReadOnlyList<string> EmitterKeys { get; }
    // Checkpoints in step order; the list object is replaced, never changed, so a worker may hold it.
    public IReadOnlyList<PreviewState> Checkpoints { get; private set; } = Array.Empty<PreviewState>();
    public long CheckpointBytes => Checkpoints.Sum(c => c.Bytes);
    public long ShownSteps => Sims.Count > 0 ? Sims[0].Steps : 0;
    // Up to this step the checkpoints lie at most CheckpointWorkMs of work apart (beyond it: a stretch to fill).
    public long FilledSteps { get; private set; }
    // Playback holds at this time (a loop end waiting for its seek back), null = no hold.
    public float? HoldAt { get; set; }

    public PreviewSettings Settings
    {
        get => _settings;
        set
        {
            _settings = value;
            float t = Time;
            Restart();
            Seek(t);
        }
    }

    // Playback time in seconds; the simulations stand at ShownTime (equal unless the low frame rate holds a frame).
    // Setting it moves only the clock: the next Advance steps the simulations forward to it (a later time), or the
    // shown frame waits for it (an earlier time; going back is a Seek).
    public float Time { get; set; }
    public float ShownTime => Sims.Count > 0 ? Sims[0].Time : 0;
    public bool Playing { get; set; } = true;
    public double LastSimMs { get; private set; }

    public void Restart()
    {
        Sims.Clear();
        CopyOffsets.Clear();
        var s = _settings;
        SourceMotion motion = s.Script ?? s.Motion switch
        {
            MotionKind.Circle => new SourceMotion.Circle(s.Origin, s.CircleRadius, s.CircleSpeed, s.CircleAxis),
            MotionKind.Turn => new SourceMotion.Turning(s.Origin, s.Yaw, s.TurnRate),
            _ => SourceMotion.Still(s.Origin, s.Yaw),
        };
        int copies = Math.Max(1, s.Copies);
        var offsets = GridOffsets(copies, s.Spacing);
        // A part that runs in no state still gets one simulation (callers read Sims[0]); it never starts.
        IReadOnlyList<SimWindow?> windows = s.Windows is not { } set ? new SimWindow?[] { null }
            : set.Items.Count > 0 ? set.Items.Cast<SimWindow?>().ToList() : new SimWindow?[] { new SimWindow(float.MaxValue, null, null) };
        for (int r = 0; r < windows.Count; r++)
            for (int i = 0; i < copies; i++)
            {
                var offset = offsets[i];
                CopyOffsets.Add(offset);
                var options = new SimOptions { Step = s.SimStep, FirstFrame = s.FirstFrame, Seed = s.Seed + (ulong)(r * copies + i), Window = windows[r] };
                Sims.Add(new EffectSim(Params, options, i == 0 ? motion : new ShiftedMotion(motion, offset)));
            }
        Time = 0;
        LastSimMs = 0;
        _generation++;
        _workSinceCheckpoint = 0;
        Checkpoints = new[] { Capture() };
        FilledSteps = 0;
    }

    public PreviewState Capture() => new(ShownSteps, ShownTime, Sims.Select(s => s.Save()).ToList());

    // Copies a state of this preview into its simulations (a checkpoint or a finished seek job).
    public void Restore(PreviewState s)
    {
        for (int i = 0; i < Sims.Count; i++)
            Sims[i].Load(s.Sims[i]);
        _workSinceCheckpoint = 0;
    }

    public long StepsOf(float t) => (long)MathF.Floor(t / _settings.SimStep + 1e-4f);

    // The newest checkpoint at or before the given step (the first one, taken at step 0, always qualifies).
    public PreviewState CheckpointAtOrBefore(long steps)
    {
        var list = Checkpoints;
        int lo = 0, hi = list.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (list[mid].Steps <= steps)
                lo = mid;
            else
                hi = mid - 1;
        }
        return list[lo];
    }

    // Keeps the list in step order, one entry per step, within CheckpointBudgetBytes.
    public void AddCheckpoint(PreviewState s)
    {
        var list = Checkpoints.Where(c => c.Steps != s.Steps).Append(s).OrderBy(c => c.Steps).ToList();
        while (list.Count > 2 && list.Sum(c => c.Bytes) > CheckpointBudgetBytes)
            list = list.Where((c, i) => i == 0 || i % 2 == 1 || i == list.Count - 1).ToList();
        Checkpoints = list;
    }

    // A seek to t that a worker runs (SeekJob.Run); null when the shown frame already stands there.
    public SeekJob? PrepareSeek(float t)
    {
        long steps = StepsOf(t);
        if (steps == ShownSteps)
            return null;
        var from = CheckpointAtOrBefore(steps);
        if (steps > ShownSteps && from.Steps <= ShownSteps)
            from = Capture();
        return new SeekJob(this, _generation, t, steps, from);
    }

    // What a rebuilt preview may take over from this one (read on the UI thread, used on a worker).
    public ReuseSource ForReuse() => new(Checkpoints, EmitterKeys, _settings, FilledSteps);

    // Brings a new preview (not yet shown) to time t, exact there. Emitters whose key is unchanged since `old` (same
    // settings) are copied from old's newest checkpoint at or before t; the others are simulated from 0 with the
    // fast-forward; then all step on to t. Returns how many emitters were taken over.
    public int StartAt(float t, ReuseSource? old)
    {
        long steps = StepsOf(t);
        float exact = steps * _settings.SimStep;
        int reused = 0;
        if (old != null && old.Settings == _settings && old.Keys.Count > 0)
        {
            var same = Enumerable.Range(0, Params.Count).Where(i => i < old.Keys.Count && old.Keys[i] == EmitterKeys[i]).ToList();
            var from = old.Checkpoints.LastOrDefault(c => c.Steps <= steps);
            if (same.Count > 0 && from != null && from.Sims.Count == Sims.Count && old.Keys.Count == Params.Count)
            {
                var changed = Enumerable.Range(0, Params.Count).Except(same).ToList();
                for (int i = 0; i < Sims.Count; i++)
                {
                    Sims[i].RunTo(from.Time, exactAt: from.Time, only: changed);
                    Sims[i].Load(from.Sims[i], only: same);
                }
                reused = same.Count;
                // Nothing that moves particles changed (a material edit): every old checkpoint holds for this preview.
                if (changed.Count == 0)
                {
                    Checkpoints = old.Checkpoints;
                    FilledSteps = old.FilledSteps;
                }
            }
        }
        foreach (var sim in Sims)
            sim.RunTo(exact, exactAt: exact);
        Time = t;
        if (steps > 0)
            AddCheckpoint(Capture());
        return reused;
    }

    // The stretch after the filled part up to the newest checkpoint, null when there is none.
    public FillJob? PrepareFill()
    {
        var newest = Checkpoints[^1];
        return newest.Steps <= FilledSteps ? null : new FillJob(this, _generation, CheckpointAtOrBefore(FilledSteps), newest.Steps);
    }

    public bool Apply(FillJob job)
    {
        if (job.Generation != _generation || !job.Done)
            return false;
        foreach (var c in job.Taken)
            AddCheckpoint(c);
        FilledSteps = Math.Max(FilledSteps, job.To);
        return true;
    }

    // Takes a finished seek job; false when the preview was restarted since the job was prepared.
    public bool Apply(SeekJob job)
    {
        if (job.Generation != _generation || job.Result is not { } state)
            return false;
        Restore(state);
        Time = job.Time;
        AddCheckpoint(state);
        return true;
    }

    // The cells of the smallest square grid that holds every copy, nearest to the grid centre first, shifted so
    // copy 0 (the effect itself) stays at the origin.
    private static List<Vector3> GridOffsets(int copies, float spacing)
    {
        int side = (int)MathF.Ceiling(MathF.Sqrt(copies));
        float half = (side - 1) * 0.5f;
        var cells = new List<Vector3>();
        for (int row = 0; row < side; row++)
            for (int col = 0; col < side; col++)
                cells.Add(new Vector3(col - half, 0, row - half) * spacing);
        cells.Sort((a, b) => a.LengthSquared().CompareTo(b.LengthSquared()));
        var first = cells[0];
        return cells.Take(copies).Select(c => c - first).ToList();
    }

    // Moves the playback clock by dt seconds of real time (when playing) and runs the simulations to the frame it
    // shows. Returns true when the shown frame changed.
    public bool Advance(float dt)
    {
        LastSimMs = 0;
        if (Playing)
            Time = HoldAt is float hold ? Math.Min(Time + dt, Math.Max(hold, Time)) : Time + dt;
        return RunToClock();
    }

    // One simulation step forward (the Step button; meant for the paused state).
    public void StepOnce()
    {
        Time = ShownTime + _settings.SimStep;
        RunToClock();
    }

    // Goes to time t here and now: from the nearest earlier checkpoint, or on from the shown frame when that is
    // nearer. The shell seeks through SeekJob instead; this is for tests and tools.
    public void Seek(float t)
    {
        long steps = StepsOf(t);
        var from = CheckpointAtOrBefore(steps);
        if (steps < ShownSteps || from.Steps > ShownSteps)
            Restore(from);
        Time = t;
        RunToClock();
    }

    // Steps every copy to the clock; while stepping beyond the newest checkpoint, takes one after every
    // CheckpointWorkMs of measured work.
    private bool RunToClock()
    {
        long target = StepsOf(Time);
        if (Sims.Count == 0 || ShownSteps >= target)
            return false;
        var clock = Stopwatch.StartNew();
        double last = 0;
        bool filled = ShownSteps <= FilledSteps;
        while (ShownSteps < target)
        {
            foreach (var sim in Sims)
                sim.Step();
            double now = clock.Elapsed.TotalMilliseconds;
            if (ShownSteps > Checkpoints[^1].Steps)
            {
                _workSinceCheckpoint += now - last;
                if (_workSinceCheckpoint >= CheckpointWorkMs)
                {
                    AddCheckpoint(Capture());
                    _workSinceCheckpoint = 0;
                }
            }
            last = now;
        }
        if (filled)
            FilledSteps = Math.Max(FilledSteps, ShownSteps);
        LastSimMs = clock.Elapsed.TotalMilliseconds;
        return true;
    }

    public int AliveTotal => Sims.Sum(s => s.Count);

    // Emitter origin in world space at the shown time: source position plus the Offset turned by the source yaw.
    public Vector3 EmitterOrigin(int copy, int emitter)
    {
        var sim = Sims[copy];
        var (pos, yaw, _) = sim.Motion.At(sim.Time);
        return Vector3.Transform(Params[emitter].Offset, Orientation.Yaw(yaw * MathF.PI / 180f)) + pos;
    }

    // Emitter axes in world space: the source yaw and the emitter Angles (the engine's per-draw mMatrix, R0).
    public Matrix4x4 EmitterAxes(int copy, int emitter)
    {
        var sim = Sims[copy];
        var (_, yaw, _) = sim.Motion.At(sim.Time);
        return Orientation.YawPitchRoll(Params[emitter].Angles) * Orientation.Yaw(yaw * MathF.PI / 180f);
    }

    // RGBA8 rows of the engine's texels (Graph.Texel, CG8: byte for byte as the game bakes them); the shaders sample
    // at x + half a texel with a point filter, so the nearest texel as in game.
    private static byte[] Bake(IReadOnlyList<EmitterParams> emitters)
    {
        int rows = Math.Max(1, emitters.Count * RowsPerEmitter);
        var data = new byte[ParamsWidth * rows * 4];
        for (int e = 0; e < emitters.Count; e++)
        {
            var p = emitters[e];
            for (int i = 0; i < ParamsWidth; i++)
            {
                int r0 = ((e * RowsPerEmitter) * ParamsWidth + i) * 4;
                int r1 = r0 + ParamsWidth * 4;
                int r2 = r1 + ParamsWidth * 4;
                data[r0] = p.Color.Texel(i, 0);
                data[r0 + 1] = p.Color.Texel(i, 1);
                data[r0 + 2] = p.Color.Texel(i, 2);
                data[r0 + 3] = p.Alpha.Texel(i);
                data[r1] = p.ColorMast.Texel(i, 0);
                data[r1 + 1] = p.ColorMast.Texel(i, 1);
                data[r1 + 2] = p.ColorMast.Texel(i, 2);
                data[r1 + 3] = p.AlphaMast.Texel(i);
                data[r2 + 2] = p.Size.Texel(i);
                data[r2 + 3] = p.SizeMast.Texel(i);
            }
        }
        return data;
    }
}
