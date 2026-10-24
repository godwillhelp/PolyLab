using System.Numerics;
using System.Runtime.CompilerServices;

namespace Pfx.Sim;

// What the renderer needs per particle, in world space.
public struct RenderParticle
{
    public Vector3 Position;
    public Vector3 Velocity;
    public float Size;
    public Vector4 Color;
    public float Angle;
    public int Frame;
    public float Age;
    public float AgeFraction;

    // Extra sprite length in metres ahead of the particle along Velocity (VelAngle with StretchMultiplier S > 1):
    // (S - 1) · |v| · 1 s times the Size graph and master (P3: 1.00 ± 0.02 s over five S/speed pairs; the Size
    // graph factor rests on P0's graph-sized sprites). The sprite's own half size stays behind the particle.
    public float StretchLength;
}

// What the GPU needs per particle when the graphs are baked into a texture (the engine's instance, R0: position,
// age fraction, rotation, size, frame and effect-time fraction): the graphs are sampled by AgeFraction and the
// masters by BirthFraction, the effect-time fraction at the particle's birth (P3: masters are read at birth).
public struct ParticleInstance
{
    public Vector3 Position;
    public float AgeFraction;
    public Vector3 Velocity;
    public float Angle;
    // The engine's instance size is half the drawn width before the Size graph (R0: SizeMultiplier 3 gave 1.5).
    public float HalfSize;
    public float BirthFraction;
    public float Frame;
}

// An emitter's whole simulation state after one step (EmitterSim.Save): the particles, the birth accumulator, the
// clock and the random stream. Never changed after it is made, so other threads may read it.
public sealed class EmitterState
{
    internal EmitterState(EmitterSim.State[] particles, long born, float births, float lastStep, double time, ulong random)
    {
        Particles = particles;
        Born = born;
        Births = births;
        LastStep = lastStep;
        Time = time;
        Random = random;
    }

    internal EmitterSim.State[] Particles { get; }
    internal long Born { get; }
    internal float Births { get; }
    internal float LastStep { get; }
    internal double Time { get; }
    internal ulong Random { get; }
    public int Count => Particles.Length;
    public long Bytes => (long)Count * Unsafe.SizeOf<EmitterSim.State>();
}

// One EmitorDef. Particles are kept in birth order (the engine draws an emitter in birth order unless Sort 1, R0).
public sealed class EmitterSim
{
    internal struct State
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public float Age;
        public float Life;
        public float BaseSize;
        public float Gravity;
        public float Drag;
        public float Angle;
        public float RotDir;
        public int StartFrame;
        public Vector4 MastColor;
        public float MastSize;
        public float MastRot;
        public float MastDrag;
        public float BirthFraction;
    }

    private readonly SimOptions _o;
    private readonly SimRandom _rnd;
    private readonly Matrix4x4 _shape;
    private State[] _p = new State[16];
    // The birth accumulator starts full (CG8, DayZ 1.30, per-frame counts at BirthRate 0.003 to 16): the first particle
    // is born in the first step whose rate is above 0 (the spawn frame, or where BRateMast first rises above 0), then
    // one every 1 / rate. Over Repeat it carries on (Repeat 1, EffectTime 2, rate 0.2: births 5 s apart).
    private float _births = 1;
    // The previous step's length: a step's births are the rate times this (CG9), 0 before the first step.
    private float _lastStep;
    private double _time;

    public EmitterSim(EmitterParams p, SimOptions options, int index)
    {
        Params = p;
        _o = options;
        _rnd = new SimRandom(options.Seed, (ulong)index + 1);
        _shape = Orientation.YawPitchRoll(p.Angles);
    }

    public EmitterParams Params { get; }
    public int Count { get; private set; }
    public long Born { get; private set; }

    // Emitter clock in seconds since spawn.
    public float Time => (float)_time;

    public float EffectTime => Params.Cycle;

    public bool Emitting => Params.Repeat || Time < EffectTime;

    // Position on the master graphs (x = effect time 0..1).
    public float EffectFraction => Params.Repeat ? Time % EffectTime / EffectTime : Math.Min(Time / EffectTime, 1f);

    // Mean live count when births and deaths balance, before the MaxNum cap (still source, masters ignored).
    public float SteadyState
    {
        get
        {
            var p = Params;
            float rate = p.BirthRate + Mean(p.BirthRateRnd);
            float life = p.LifetimeByAnim && p.AnimFps > 0 ? p.BaseLifeTime : p.LifeTime + Mean(p.LifeTimeRnd);
            return rate * Math.Max(life, 0);
        }
    }

    private static float Mean(float rnd) => rnd * 0.5f;

    public float MaxLife => Params.MaxLife;

    public EmitterState Save() => new(_p.AsSpan(0, Count).ToArray(), Born, _births, _lastStep, _time, _rnd.State);

    public void Load(EmitterState s)
    {
        if (_p.Length < s.Count)
            _p = new State[s.Count];
        s.Particles.CopyTo(_p, 0);
        Count = s.Count;
        Born = s.Born;
        _births = s.Births;
        _lastStep = s.LastStep;
        _time = s.Time;
        _rnd.State = s.Random;
    }

    // X + U[0,1)·RND: LifeTime 2 + LifeTimeRND 2 kept 30 alive at 10 births/s in game (P3), the mean of this rule.
    private float Spread(float value, float rnd) => rnd == 0 ? value : value + _rnd.Next() * rnd;

    // physics = false skips motion and rotation; ages, deaths, births and every random draw are those of a full step,
    // so the live count, the MaxNum cap and the random stream stay exact and only the particles alive during the
    // skipped steps are misplaced (EffectSim skips only steps whose particles are dead by the time it must be exact).
    public void Step(float dt, Vector3 srcPos, float srcYaw, Vector3 srcVel, bool physics = true, bool births = true)
    {
        var p = Params;
        float frac = EffectFraction;
        var srcRot = Orientation.Yaw(srcYaw * (MathF.PI / 180f));
        Matrix4x4.Invert(srcRot, out var toLocal);
        Vector3 gravity = new(0, -_o.Gravity, 0);
        Vector3 wind = p.Wind ? _o.Wind : Vector3.Zero;
        Vector3 origin = p.Offset;
        if (p.LocalTransform)
        {
            gravity = Vector3.TransformNormal(gravity, toLocal);
            wind = Vector3.TransformNormal(wind, toLocal);
        }
        else
        {
            origin = Vector3.Transform(p.Offset, srcRot) + srcPos;
        }
        float springKeep = MathF.Exp(-_o.SpringRate * p.Spring * dt);
        // Restitution > 0: particles stayed at their birth point in game, with and without gravity, 2 m above the
        // ground (P3, cause unknown; vanilla uses only -1 and 0, and -1 falls through the ground like 0).
        bool frozen = p.Restitution > 0;

        int w = 0;
        for (int i = 0; i < Count; i++)
        {
            ref var s = ref _p[i];
            s.Age += dt;
            if (s.Age >= s.Life)
                continue;
            if (physics && !frozen)
            {
                Ballistics.Step(ref s.Position, ref s.Velocity, gravity * (s.Gravity * dt), MathF.Exp(-s.Drag * s.MastDrag * dt), wind, dt);
                if (p.Spring > 0)
                    s.Position = origin + (s.Position - origin) * springKeep;
            }
            if (physics)
                s.Angle += p.RotMultiplier * (MathF.PI / 180f) * p.RotationSpeed.EvaluateCpu(s.Age / s.Life) * s.MastRot * s.RotDir * dt;
            if (w != i)
                _p[w] = s;
            w++;
        }
        Count = w;

        // Births lag one step (CG9, DayZ 1.30, per-frame counts around forced long frames): a step adds the rate times
        // the previous step's length, with the rate read at the end of this step, and emits only when it ends before
        // EffectTime. So a step that reaches EffectTime emits nothing, and a long frame that crosses it ends the
        // emitter (a 110 ms spawn frame left 0.05 and 0.1 s emitters empty; a 137 ms first frame of a cold part left 1).
        // Fits all 15 probe cases within 2 births per frame and the CG8 counts within 3.
        float end = Time + dt;
        if (births && (p.Repeat || end < EffectTime))
        {
            float u = p.Repeat ? end % EffectTime / EffectTime : end / EffectTime;
            float rate = Spread(p.BirthRate, p.BirthRateRnd) * p.BRateMast.EvaluateCpu(u) + p.BirthRateVel * srcVel.Length();
            if (rate > 0)
            {
                // The step that gives the full accumulator's birth adds nothing to it (CG8); in the spawn step there
                // is no previous step anyway.
                if (_births < 1)
                    _births += rate * _lastStep;
                int n = (int)_births;
                _births -= n;
                for (int k = 0; k < n && Count < p.MaxNum; k++)
                    Emit(frac, srcRot, srcPos, srcVel);
            }
        }
        _lastStep = dt;
        _time += dt;
    }

    // The hard stop: every particle removed at once.
    public void Clear() => Count = 0;

    private void Emit(float frac, Matrix4x4 srcRot, Vector3 srcPos, Vector3 srcVel)
    {
        var p = Params;
        if (Count == _p.Length)
            Array.Resize(ref _p, _p.Length * 2);
        const float rad = MathF.PI / 180f;
        float azimuth = _rnd.Next() * p.ConeAngle.X * rad;
        float polar = (p.ConeAngle.Y + (p.ConeAngle.Z - p.ConeAngle.Y) * _rnd.Next()) * 0.5f * rad;
        var dir = Vector3.TransformNormal(Orientation.ConeDirection(polar, azimuth), _shape);
        var pos = Vector3.Transform(ShapePoint(), _shape) + p.Offset;
        float speed = Spread(p.Velocity, p.VelocityRnd) * p.VelocityMast.EvaluateCpu(frac);
        var vel = dir * speed;
        // LocalTransform 1 particles already move with the source; VelAffect on them is not measured and not added.
        if (!p.LocalTransform)
        {
            pos = Vector3.Transform(pos, srcRot) + srcPos;
            vel = Vector3.TransformNormal(vel, srcRot) + srcVel * p.VelAffect;
        }
        _p[Count++] = new State
        {
            Position = pos,
            Velocity = vel,
            Life = Math.Max(p.LifetimeByAnim && p.AnimFps > 0 ? p.BaseLifeTime : Spread(p.LifeTime, p.LifeTimeRnd), 0),
            BaseSize = Math.Max(Spread(p.SizeMultiplier, p.SizeRnd), 0),
            Gravity = Spread(p.GravityMultiply, p.GravityMultiplyRnd),
            Drag = Math.Max(Spread(p.AirResistance, p.AirResistanceRnd), 0),
            Angle = p.RandomAngle ? _rnd.Next() * MathF.Tau : 0,
            RotDir = p.RandomRotDir && _rnd.Next() < 0.5f ? -1 : 1,
            StartFrame = p.RandomFrame ? Flipbook.RandomStart(p, _rnd.Next()) : 0,
            MastColor = new Vector4(p.ColorMast.EvaluateGpu(frac, 0), p.ColorMast.EvaluateGpu(frac, 1), p.ColorMast.EvaluateGpu(frac, 2),
                p.AlphaMast.EvaluateGpu(frac)),
            MastSize = p.SizeMast.EvaluateGpu(frac),
            MastRot = p.RotationSpeedMast.EvaluateCpu(frac),
            // Read once at birth like the other masters (CG8: a particle born at 0.25 kept braking at 0.25 after the
            // master stepped to 1).
            MastDrag = p.AirResistanceMast.EvaluateCpu(frac),
            BirthFraction = frac,
        };
        Born++;
    }

    // Emitter-space point before Angles and Offset, inside EmitterParams.SpawnHalfExtent. Sphere and Ellipse take a
    // uniform direction times a uniform radius, so points crowd the centre as seen in game. Cone and OnSphere with a
    // non-zero size showed nothing in game (P3, KnownDifferences); Cone is placed at the emitter point here.
    private Vector3 ShapePoint()
    {
        var half = Params.SpawnHalfExtent;
        switch (Params.Shape)
        {
            case ShapeKind.Box:
                return new Vector3(_rnd.Signed(), _rnd.Signed(), _rnd.Signed()) * half;
            case ShapeKind.Sphere:
            case ShapeKind.Ellipse:
                return UnitDirection() * _rnd.Next() * half;
            case ShapeKind.OnSphere:
                return UnitDirection() * half;
            default:
                return Vector3.Zero;
        }
    }

    private Vector3 UnitDirection()
    {
        float y = _rnd.Signed();
        float a = _rnd.Next() * MathF.Tau;
        float r = MathF.Sqrt(Math.Max(0, 1 - y * y));
        return new Vector3(r * MathF.Cos(a), y, r * MathF.Sin(a));
    }

    private int FrameOf(in State s) => Flipbook.FrameAt(Params, s.StartFrame, s.Age);

    // How many live particles show each tile now (the frame slicer marks them on the sheet).
    public void CountFrames(Span<int> perFrame)
    {
        perFrame.Clear();
        for (int i = 0; i < Count; i++)
        {
            int frame = FrameOf(_p[i]);
            if ((uint)frame < (uint)perFrame.Length)
                perFrame[frame]++;
        }
    }

    // Writes Count instances in birth order; returns how many were written.
    public int WriteInstances(Span<ParticleInstance> into, Vector3 srcPos, float srcYaw)
    {
        var p = Params;
        var srcRot = Orientation.Yaw(srcYaw * (MathF.PI / 180f));
        int n = Math.Min(Count, into.Length);
        for (int i = 0; i < n; i++)
        {
            ref var s = ref _p[i];
            ref var o = ref into[i];
            o.Position = p.LocalTransform ? Vector3.Transform(s.Position, srcRot) + srcPos : s.Position;
            o.Velocity = p.LocalTransform ? Vector3.TransformNormal(s.Velocity, srcRot) : s.Velocity;
            o.AgeFraction = s.Life > 0 ? s.Age / s.Life : 0;
            o.Angle = s.Angle;
            o.HalfSize = s.BaseSize * 0.5f;
            o.BirthFraction = s.BirthFraction;
            o.Frame = FrameOf(s);
        }
        return n;
    }

    public void Render(List<RenderParticle> into, Vector3 srcPos, float srcYaw)
    {
        var p = Params;
        var srcRot = Orientation.Yaw(srcYaw * (MathF.PI / 180f));
        for (int i = 0; i < Count; i++)
        {
            ref var s = ref _p[i];
            float age = s.Life > 0 ? s.Age / s.Life : 0;
            int frame = FrameOf(s);
            var pos = p.LocalTransform ? Vector3.Transform(s.Position, srcRot) + srcPos : s.Position;
            var vel = p.LocalTransform ? Vector3.TransformNormal(s.Velocity, srcRot) : s.Velocity;
            float sizeCurve = p.Size.EvaluateGpu(age) * s.MastSize;
            into.Add(new RenderParticle
            {
                Position = pos,
                Velocity = vel,
                Size = s.BaseSize * sizeCurve,
                Color = new Vector4(p.Color.EvaluateGpu(age, 0), p.Color.EvaluateGpu(age, 1), p.Color.EvaluateGpu(age, 2),
                    p.Alpha.EvaluateGpu(age)) * s.MastColor,
                Angle = s.Angle,
                Frame = frame,
                Age = s.Age,
                AgeFraction = age,
                StretchLength = p.VelAngle && p.StretchMultiplier > 1 ? (p.StretchMultiplier - 1) * vel.Length() * sizeCurve : 0,
            });
        }
    }
}
