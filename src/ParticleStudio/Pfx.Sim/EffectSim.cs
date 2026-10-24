using System.Numerics;
using Pfx.Core.Model;

namespace Pfx.Sim;

// A whole simulation at one step (EffectSim.Save); read-only, so a worker may restore from it while the UI plays on.
public sealed class EffectSimState
{
    internal EffectSimState(long steps, EmitterState[] emitters)
    {
        Steps = steps;
        Emitters = emitters;
    }

    public long Steps { get; }
    public IReadOnlyList<EmitterState> Emitters { get; }
    public long Bytes => Emitters.Sum(e => e.Bytes);
}

// One .ptc on one moving source, stepped with the fixed step of SimOptions. Same file, options and motion give the
// same particles every run.
public sealed class EffectSim
{
    private long _steps;

    // The game clamps a frame's time at 0.3 s (CG9: a 1026 ms frame gave the script a 300 ms timeslice and a 1000
    // births/s emitter 300 births).
    public const float MaxFrame = 0.3f;

    public EffectSim(IReadOnlyList<EmitterParams> emitters, SimOptions options, SourceMotion? motion = null)
    {
        Options = options;
        Motion = motion ?? SourceMotion.Still();
        Emitters = emitters.Select((e, i) => new EmitterSim(e, options, i)).ToList();
        if (options.Window is { } w && w.Start > 0)
        {
            // Floor first, then the same float test Step makes (a window that never starts sits at float.MaxValue).
            double steps = Math.Floor(w.Start / (double)options.Step);
            _spawnStep = steps < long.MaxValue / 2 ? Math.Max(0, (long)steps - 1) : long.MaxValue / 2;
            while (_spawnStep < long.MaxValue / 2 && (float)(_spawnStep * (double)options.Step) + options.Step <= w.Start)
                _spawnStep++;
        }
    }

    // The step in which the source is created (the first step that ends after the window's start, else 0); the step
    // after it lasts FirstFrame for the emitters. Their own clocks then run FirstFrame - Step ahead of the scene clock,
    // which stays a fixed step so seeking and saved states stay exact.
    private readonly long _spawnStep;

    public static EffectSim From(PtcDocument doc, SimOptions options, SourceMotion? motion = null,
        Func<string, (int U, int V)?>? tiles = null) =>
        new(doc.Emitters.Select(e => EmitterParams.From(e, tiles)).ToList(), options, motion);

    public SimOptions Options { get; }
    public SourceMotion Motion { get; }
    public IReadOnlyList<EmitterSim> Emitters { get; }
    public float Time => (float)(_steps * (double)Options.Step);
    public int Count => Emitters.Sum(e => e.Count);

    public long Steps => _steps;

    public void Step() => Step(null, float.NegativeInfinity);

    // Half an ulp relative to the value: the largest rounding error of one float addition.
    private const float AddError = 1f / 16777216f;

    // only = the emitters to step (null = all). An emitter skips motion in a step that ends more than its longest life
    // before exactAt, so every particle alive in that step is dead at exactAt and the state there equals a full run bit
    // for bit (test Fast_forward_is_exact_at_the_target). The margin covers the birth step, the age comparison and the
    // rounding of an age summed from L / dt additions (at most L · AddError each).
    private void Step(IReadOnlyCollection<int>? only, float exactAt)
    {
        float dt = Options.Step;
        float end = Time + dt;
        bool births = true;
        if (Options.Window is { } w)
        {
            if (end <= w.Start)
            {
                _steps++;
                return;
            }
            if (w.HardStop is float hard && end > hard)
            {
                for (int i = 0; i < Emitters.Count; i++)
                    if (only == null || only.Contains(i))
                        Emitters[i].Clear();
                _steps++;
                return;
            }
            births = w.SoftStop is not float soft || end <= soft;
        }
        var (pos, yaw, vel) = Motion.At(end);
        float own = _steps == _spawnStep + 1 && Options.FirstFrame > 0 ? Math.Min(Options.FirstFrame, MaxFrame) : dt;
        for (int i = 0; i < Emitters.Count; i++)
        {
            if (only != null && !only.Contains(i))
                continue;
            var e = Emitters[i];
            float life = e.MaxLife;
            float margin = 2 * Math.Max(dt, own) + life * life / dt * AddError;
            e.Step(own, pos, yaw, vel, end >= exactAt - life - margin, births);
        }
        _steps++;
    }

    // Steps until Time reaches t (never backwards: Load a saved state or build a new EffectSim to go back). The state
    // is exact from exactAt on; earlier steps skip the motion of particles that die before it. With `only`, the other
    // emitters stay where they are and must be loaded from a state of the same step afterwards (partial rebuild).
    public void RunTo(float t, float exactAt = float.NegativeInfinity, IReadOnlyCollection<int>? only = null)
    {
        while (Time + Options.Step * 0.5f <= t)
            Step(only, exactAt);
    }

    public EffectSimState Save() => new(_steps, Emitters.Select(e => e.Save()).ToArray());

    // Loads every emitter, or only those in `only` (their state must come from the step this simulation is at).
    public void Load(EffectSimState s, IReadOnlyCollection<int>? only = null)
    {
        if (s.Emitters.Count != Emitters.Count)
            throw new ArgumentException("The state belongs to an effect with another number of emitters.");
        if (only != null && s.Steps != _steps)
            throw new ArgumentException("A partial load needs a state of the same step.");
        for (int i = 0; i < Emitters.Count; i++)
            if (only == null || only.Contains(i))
                Emitters[i].Load(s.Emitters[i]);
        _steps = s.Steps;
    }

    public int WriteInstances(int emitter, Span<ParticleInstance> into)
    {
        var (pos, yaw, _) = Motion.At(Time);
        return Emitters[emitter].WriteInstances(into, pos, yaw);
    }

    public List<RenderParticle> Render(int emitter)
    {
        var list = new List<RenderParticle>(Emitters[emitter].Count);
        var (pos, yaw, _) = Motion.At(Time);
        Emitters[emitter].Render(list, pos, yaw);
        return list;
    }
}
