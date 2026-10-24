namespace Pfx.Sim.XRay;

public struct XrParticle
{
    public D3 Pos, PosB, Vel, Size;
    public double Rot;
    public double R, G, B, A;
    public double Age;
    // The engine keeps the flipbook frame as frame * 255 in a u16; Frame holds that number.
    public int Frame;
    public bool Ccw;
    // Birth order within the effect, for tests that follow one particle (removal reorders the array as the engine does).
    public int Id;
}

// One X-Ray effect playing: the engine's CParticleEffect::OnFrame step loop over ParticleManager::Update (actions in list
// order), CPEDef::ExecuteAnimate and the birth / death callbacks. Actions follow xray.py simulate (the reference the
// golden tests compare with) plus what its one-batch replay leaves out: sources emitting over time (rate per second
// with the fraction dithered, PASource::Execute), the effect time limit and deferred stops. Not simulated, as in
// xray.py: turbulence, gravitate, follow, matchvelocity, jet, avoid, bounce, restore, copyvertexb, and collisions.
// The effect sits at Offset (a translation, like every parent matrix the A/B view and group children use).
public sealed class XrEffectSim
{
    // PATargetColor works in steps of the engine default (STEP_DEFAULT, 33 ms).
    public const double StepDefault = 0.033;

    private readonly XrRandom _rng;
    private readonly Dictionary<XrAction, double> _explosionAge = new();
    private XrParticle[] _p = new XrParticle[64];
    private int _nextId;
    private int _memMs;
    private double _elapsedLimit;

    public XrEffectSim(XrEffect def, XrRandom rng)
    {
        Def = def;
        _rng = rng;
        StepMs = Math.Max(1, (int)Math.Round(def.Step * 1000));
        _elapsedLimit = def.TimeLimitValue ?? 0;
    }

    public XrEffect Def { get; }
    public int StepMs { get; }
    public int Count { get; private set; }
    public ReadOnlySpan<XrParticle> Particles => _p.AsSpan(0, Count);
    public bool Playing { get; private set; }
    public bool DeferredStop { get; private set; }
    public bool Silent { get; private set; } = true;
    public D3 Offset { get; set; }
    public D3 ParentVelocity { get; set; }
    // xray.py's replay runs the actions only (no time limit), so the golden comparison switches it off.
    public bool IgnoreTimeLimit { get; init; }
    public Action<XrEffectSim, int>? Born;
    public Action<XrEffectSim, int, XrParticle>? Died;

    public void Play()
    {
        DeferredStop = false;
        Playing = true;
        Silent = false;
        _explosionAge.Clear();
    }

    // CParticleEffect::Stop: the sources fall silent; not deferred, every particle goes at once (no death callbacks,
    // as p_count = 0 in the engine).
    public void Stop(bool deferred)
    {
        Silent = true;
        if (deferred)
        {
            DeferredStop = true;
        }
        else
        {
            Playing = false;
            Count = 0;
        }
    }

    // Steps of this effect inside one frame of frameMs (the engine accumulates frame time in ms and runs at most three
    // steps per frame).
    public void Frame(int frameMs)
    {
        if (!Playing)
            return;
        _memMs += frameMs;
        int steps = 0;
        if (_memMs >= StepMs)
        {
            steps = Math.Min(_memMs / StepMs, 3);
            _memMs %= StepMs;
        }
        for (; steps > 0 && Playing; steps--)
            Step();
    }

    public void Step()
    {
        double dt = StepMs / 1000.0;
        if (Def.Has(XrEffect.TimeLimit) && !DeferredStop && !IgnoreTimeLimit)
        {
            _elapsedLimit -= dt;
            if (_elapsedLimit < 0)
            {
                _elapsedLimit = Def.TimeLimitValue ?? 0;
                Stop(true);
                return;
            }
        }
        double tmMax = 1.0;
        foreach (var a in Def.Actions)
            Execute(a, dt, ref tmMax);
        if (Def.Has(XrEffect.Framed | XrEffect.Animated) && Def.Frame is { } f)
            Animate(f, dt);
        if (DeferredStop && Count == 0)
        {
            Playing = false;
            DeferredStop = false;
        }
    }

    // Particles given from outside (the golden replay): the effect then runs only its other actions.
    public void Load(IEnumerable<XrParticle> particles)
    {
        Count = 0;
        foreach (var p in particles)
            Add(p, callBirth: false);
        Playing = true;
        Silent = true;
    }

    private void Add(XrParticle p, bool callBirth)
    {
        if (Count == _p.Length)
            Array.Resize(ref _p, _p.Length * 2);
        p.Id = _nextId++;
        _p[Count++] = p;
        if (callBirth)
            Born?.Invoke(this, Count - 1);
    }

    // ParticleEffect::Remove: the death callback sees the particle, then the last one takes its place.
    private void Remove(int i)
    {
        var dead = _p[i];
        Died?.Invoke(this, i, dead);
        _p[i] = _p[--Count];
    }

    private static double Quantize(double c) => Math.Clamp(Math.Floor(c * 255.0), 0, 255) / 255.0;

    private void Animate(XrFrame f, double dt)
    {
        double speed = f.Speed * dt;
        for (int i = 0; i < Count; i++)
        {
            ref var m = ref _p[i];
            double fr = m.Frame / 255.0 + (m.Ccw ? -1 : 1) * speed;
            if (fr > f.Count)
                fr -= f.Count;
            if (fr < 0)
                fr += f.Count;
            m.Frame = (int)Math.Floor(fr * 255.0);
        }
    }

    private void Execute(XrAction a, double dt, ref double tmMax)
    {
        var o = Offset;
        switch (a.Type)
        {
            case "source":
                Emit(a, dt);
                break;
            case "killold":
            {
                double limit = a.F("Age Limit");
                bool less = a.B("Kill Less Than");
                tmMax = limit;
                for (int i = Count - 1; i >= 0; i--)
                    if (!((_p[i].Age < limit) ^ less))
                        Remove(i);
                break;
            }
            case "move":
                for (int i = 0; i < Count; i++)
                {
                    ref var m = ref _p[i];
                    m.Age += dt;
                    m.PosB = m.Pos;
                    m.Pos += m.Vel * dt;
                }
                break;
            case "gravity":
            {
                var g = a.V("Direction") * dt;
                for (int i = 0; i < Count; i++)
                    _p[i].Vel += g;
                break;
            }
            case "damping":
            {
                var d = a.V("Damping");
                var sc = new D3(1 - (1 - d.X) * dt, 1 - (1 - d.Y) * dt, 1 - (1 - d.Z) * dt);
                double lo = a.F("V Low"), hi = a.F("V High");
                for (int i = 0; i < Count; i++)
                {
                    double v2 = _p[i].Vel.Length2;
                    if (v2 >= lo * lo && v2 <= hi * hi)
                        _p[i].Vel *= sc;
                }
                break;
            }
            case "targetcolor":
            {
                double coeff = StepDefault / dt, from = a.F("TimeFrom") * tmMax, to = a.F("TimeTo") * tmMax, k = a.F("Scale") * StepDefault;
                var c = a.V("Color");
                double alpha = a.F("Alpha");
                for (int i = 0; i < Count; i++)
                {
                    ref var m = ref _p[i];
                    if (m.Age < from || m.Age > to)
                        continue;
                    m.R -= (m.R - Quantize(m.R + (c.X - m.R) * k)) / coeff;
                    m.G -= (m.G - Quantize(m.G + (c.Y - m.G) * k)) / coeff;
                    m.B -= (m.B - Quantize(m.B + (c.Z - m.B) * k)) / coeff;
                    m.A -= (m.A - Quantize(m.A + (alpha - m.A) * k)) / coeff;
                }
                break;
            }
            case "targetsize":
            {
                var s = a.V("Size");
                var k = a.V("Scale") * dt;
                for (int i = 0; i < Count; i++)
                    _p[i].Size += (s - _p[i].Size) * k;
                break;
            }
            case "targetrotate":
            {
                double sf = a.F("Scale") * dt, target = Math.Abs(a.V("Rotation").X);
                for (int i = 0; i < Count; i++)
                {
                    ref var m = ref _p[i];
                    m.Rot += (target - Math.Abs(m.Rot)) * (m.Rot >= 0 ? sf : -sf);
                }
                break;
            }
            case "targetvelocity":
            {
                var v = a.V("Velocity");
                double k = a.F("Scale") * dt;
                for (int i = 0; i < Count; i++)
                    _p[i].Vel += (v - _p[i].Vel) * k;
                break;
            }
            case "randomaccel":
                for (int i = 0; i < Count; i++)
                    _p[i].Vel += a.D("Accelerate").Generate(_rng) * dt;
                break;
            case "randomvelocity":
                for (int i = 0; i < Count; i++)
                    _p[i].Vel = a.D("Velocity").Generate(_rng);
                break;
            case "randomdisplace":
                for (int i = 0; i < Count; i++)
                    _p[i].Pos += a.D("Displace").Generate(_rng) * dt;
                break;
            case "speedlimit":
            {
                double min = a.F("Min Speed"), max = a.F("Max Speed");
                for (int i = 0; i < Count; i++)
                {
                    double sp = _p[i].Vel.Length;
                    if (sp < min && sp > 0)
                        _p[i].Vel *= min / sp;
                    else if (sp > max)
                        _p[i].Vel *= max / sp;
                }
                break;
            }
            case "sink":
            case "sinkvelocity":
            {
                var dom = a.D("Domain");
                bool killInside = a.B("Kill Inside");
                for (int i = Count - 1; i >= 0; i--)
                {
                    bool inside = dom.Within(a.Type == "sink" ? _p[i].Pos - o : _p[i].Vel, _rng);
                    if (!(inside ^ killInside))
                        Remove(i);
                }
                break;
            }
            case "vortex":
            {
                var axis = a.V("Axis").Unit();
                var centre = a.V("Center") + o;
                double max2 = a.F("Max Radius") * a.F("Max Radius"), mag = a.F("Magnitude"), eps = a.F("Epsilon");
                for (int i = 0; i < Count; i++)
                {
                    ref var m = ref _p[i];
                    var off = m.Pos - centre;
                    double r2 = off.Length2;
                    if (r2 > max2)
                        continue;
                    double r = Math.Sqrt(Math.Max(r2, 1e-12));
                    var n = off / r;
                    var w = axis * n.Dot(axis);
                    var u = n - w;
                    var v = axis.Cross(u);
                    double th = mag * dt / (r2 + eps);
                    m.Pos = (u * Math.Cos(th) + v * Math.Sin(th) + w) * r + centre;
                }
                break;
            }
            case "orbitpoint":
            {
                var centre = a.V("Center") + o;
                double max2 = a.F("Max Radius") * a.F("Max Radius"), magdt = a.F("Magnitude") * dt, eps = a.F("Epsilon");
                for (int i = 0; i < Count; i++)
                {
                    var d = centre - _p[i].Pos;
                    double r2 = d.Length2;
                    if (r2 < max2)
                        _p[i].Vel += d * (magdt / (Math.Sqrt(r2) + (r2 + eps)));
                }
                break;
            }
            case "orbitline":
            {
                var axis = a.V("Axis").Unit();
                var at = a.V("Position") + o;
                double max2 = a.F("Max Radius") * a.F("Max Radius"), magdt = a.F("Magnitude") * dt, eps = a.F("Epsilon");
                for (int i = 0; i < Count; i++)
                {
                    var f = _p[i].Pos - at;
                    var into = axis * f.Dot(axis) - f;
                    double r2 = into.Length2;
                    if (r2 < max2)
                        _p[i].Vel += into * (magdt / (Math.Sqrt(r2) + (r2 + eps)));
                }
                break;
            }
            case "scatter":
            {
                var centre = a.V("Center") + o;
                double max2 = a.F("Max Radius") * a.F("Max Radius"), magdt = a.F("Magnitude") * dt, eps = a.F("Epsilon");
                for (int i = 0; i < Count; i++)
                {
                    var d = _p[i].Pos - centre;
                    double r2 = d.Length2;
                    if (r2 < max2)
                        _p[i].Vel += d / Math.Sqrt(Math.Max(r2, 1e-12)) * (magdt / (r2 + eps));
                }
                break;
            }
            case "explosion":
            {
                double age = _explosionAge.TryGetValue(a, out var e) ? e : a.F("Age");
                double radius = a.F("Velocity") * age, sd = a.F("Standart Dev"), mag = a.F("Magnitude"), eps = a.F("Epsilon");
                var centre = a.V("Center") + o;
                for (int i = 0; i < Count; i++)
                {
                    var d = _p[i].Pos - centre;
                    double d2 = d.Length2, dist = Math.Sqrt(d2);
                    double gd = Math.Exp((radius - dist) * (radius - dist) * (-0.5 / (sd * sd))) / (Math.Sqrt(2 * Math.PI) * sd);
                    _p[i].Vel += d * (gd * mag * dt / ((dist + 1e-4) * (d2 + eps)));
                }
                _explosionAge[a] = age + dt;
                break;
            }
        }
    }

    private void Emit(XrAction a, double dt)
    {
        if (Silent)
            return;
        double want = a.F("Rate") * dt;
        int rate = (int)Math.Floor(want);
        if (_rng.Next() < want - rate)
            rate++;
        rate = Math.Min(rate, Def.Max - Count);
        double sigma = a.F("Age Sigma"), alpha = a.F("Color\\Alpha"), age0 = a.F("Starting Age");
        var parent = ParentVelocity * a.F("Parent Motion");
        for (int i = 0; i < rate; i++)
        {
            var pos = a.D("Domain").Generate(_rng) + Offset;
            var size = a.D("Size").Generate(_rng);
            if (a.B("Single Size"))
                size = new D3(size.X, size.X, size.X);
            var rot = a.D("Rotation").Generate(_rng);
            var vel = a.D("Velocity").Generate(_rng) + parent;
            var col = a.D("Color").Generate(_rng);
            var p = new XrParticle
            {
                Pos = pos, PosB = pos, Vel = vel, Size = size, Rot = rot.X,
                R = Quantize(col.X), G = Quantize(col.Y), B = Quantize(col.Z), A = Quantize(alpha),
                Age = age0 + (sigma != 0 ? _rng.Normal(XrDomain.NRandSd * sigma) : 0),
            };
            if (Def.Has(XrEffect.RandomFrame) && Def.Frame is { Count: > 0 } f)
                p.Frame = (int)Math.Floor(Math.Floor(_rng.Next() * f.Count) * 255.0);
            if (Def.Has(XrEffect.Animated) && Def.Has(1 << 13) && _rng.Next() < 0.5)
                p.Ccw = true;
            Add(p, callBirth: true);
        }
    }
}

// A group playing (CParticleGroup::OnFrame): items start when the group clock crosses their t0 and stop at t1 (deferred
// when the item says so); the group time limit stops everything deferred. Children as SItem does it: an on-play child
// effect follows each particle of its item and stops (deferred) when the particle dies (played again when "rewind"),
// on-birth / on-death children start where the particle is born / dies and play on their own. Child effects come from
// the resolver (the index); a child that cannot be found, or a free child without a time limit (the engine refuses those
// with a fatal error), is not started and named in Problems.
public sealed class XrGroupSim
{
    private sealed class Item
    {
        public required XrGroupItem Def;
        public XrEffectSim? Effect;
        public readonly List<XrEffectSim?> Related = new();
        public readonly List<XrEffectSim> Free = new();
    }

    private readonly List<Item> _items = new();
    private readonly Func<string, XrEffect?> _resolve;
    private readonly XrRandom _rng;
    private readonly HashSet<string> _problems = new();
    private double _time;

    public XrGroupSim(XrGroup def, Func<string, XrEffect?> resolve, XrRandom rng, D3 offset)
    {
        Def = def;
        _resolve = resolve;
        _rng = rng;
        Offset = offset;
        for (int i = 0; i < def.Effects.Count; i++)
        {
            var d = def.Effects[i];
            var item = new Item { Def = d };
            if (resolve(d.Name) is { } e)
            {
                item.Effect = new XrEffectSim(e, rng) { Offset = offset };
                int index = i;
                item.Effect.Born = (sim, at) => OnBirth(_items[index], sim, at);
                item.Effect.Died = (sim, at, p) => OnDeath(_items[index], p, at);
            }
            else
            {
                _problems.Add(d.Name);
            }
            _items.Add(item);
        }
    }

    public XrGroup Def { get; }
    public D3 Offset { get; }
    public bool Playing { get; private set; }
    public bool DeferredStop { get; private set; }
    public double Time => _time;
    public IReadOnlyCollection<string> Problems => _problems;

    // Every effect playing now (items, then their children), for drawing and counting.
    public IEnumerable<XrEffectSim> Effects()
    {
        foreach (var item in _items)
        {
            if (item.Effect != null)
                yield return item.Effect;
            foreach (var c in item.Related)
                if (c != null)
                    yield return c;
            foreach (var c in item.Free)
                yield return c;
        }
    }

    public void Play()
    {
        _time = 0;
        DeferredStop = false;
        Playing = true;
    }

    public void Stop(bool deferred)
    {
        if (deferred)
            DeferredStop = true;
        else
            Playing = false;
        foreach (var item in _items)
            StopItem(item, deferred);
    }

    private static void StopItem(Item item, bool deferred)
    {
        item.Effect?.Stop(deferred);
        foreach (var c in item.Related)
            c?.Stop(deferred);
        foreach (var c in item.Free)
            c.Stop(deferred);
        if (!deferred)
        {
            item.Related.Clear();
            item.Free.Clear();
        }
    }

    public void Frame(int frameMs)
    {
        if (!Playing)
            return;
        double ct = _time, dt = frameMs / 1000.0;
        foreach (var item in _items)
        {
            var d = item.Def;
            if (!d.Has(XrGroupItem.Enabled) || item.Effect == null)
                continue;
            if (item.Effect.Playing)
            {
                if (ct <= d.T1 && ct + dt >= d.T1)
                    StopItem(item, d.Has(XrGroupItem.Deferred));
            }
            else if (!DeferredStop && ct <= d.T0 && ct + dt >= d.T0)
            {
                item.Effect.Play();
            }
        }
        _time += dt;
        if (_time > Def.TimeLimit && Def.TimeLimit > 0 && !DeferredStop)
            Stop(true);
        bool playing = false;
        foreach (var item in _items)
            playing |= FrameItem(item, frameMs);
        if (DeferredStop && !playing)
        {
            Playing = false;
            DeferredStop = false;
        }
    }

    private bool FrameItem(Item item, int frameMs)
    {
        bool playing = false;
        var e = item.Effect;
        if (e != null)
        {
            e.Frame(frameMs);
            if (e.Playing)
            {
                playing = true;
                if (item.Def.Has(XrGroupItem.OnPlayChild))
                {
                    var ps = e.Particles;
                    for (int i = 0; i < ps.Length && i < item.Related.Count; i++)
                        if (item.Related[i] is { } c)
                            Follow(c, ps[i]);
                }
            }
        }
        foreach (var c in item.Related)
        {
            if (c == null)
                continue;
            c.Frame(frameMs);
            if (c.Playing)
                playing = true;
            else if (item.Def.Has(XrGroupItem.OnPlayRewind))
                c.Play();
        }
        for (int i = item.Free.Count - 1; i >= 0; i--)
        {
            item.Free[i].Frame(frameMs);
            if (item.Free[i].Playing)
                playing = true;
            else
                item.Free.RemoveAt(i);
        }
        return playing;
    }

    // UpdateParent of a child: its sources move to the particle and inherit its velocity (pos - posB over the child's step).
    private static void Follow(XrEffectSim child, in XrParticle p)
    {
        child.Offset = p.Pos;
        child.ParentVelocity = (p.Pos - p.PosB) / (child.StepMs / 1000.0);
    }

    private XrEffectSim? Child(string name)
    {
        if (_resolve(name) is not { } def)
        {
            _problems.Add(name);
            return null;
        }
        return new XrEffectSim(def, _rng);
    }

    private void OnBirth(Item item, XrEffectSim parent, int at)
    {
        var p = parent.Particles[at];
        foreach (var (kind, name) in item.Def.Children)
        {
            if (kind == "on_birth_child" && item.Def.Has(XrGroupItem.OnBirthChild))
                StartFree(item, name, p);
            else if (kind == "on_play_child" && item.Def.Has(XrGroupItem.OnPlayChild))
            {
                var c = Child(name);
                if (c != null)
                {
                    Follow(c, p);
                    c.Play();
                }
                item.Related.Add(c);
            }
        }
        if (item.Def.Has(XrGroupItem.OnPlayChild) && !item.Def.Children.Any(c => c.Kind == "on_play_child"))
            item.Related.Add(null);
    }

    private void OnDeath(Item item, XrParticle p, int at)
    {
        if (item.Def.Has(XrGroupItem.OnPlayChild) && at < item.Related.Count)
        {
            item.Related[at]?.Stop(true);
            if (item.Related[at] is { } c)
                item.Free.Add(c);
            item.Related[at] = item.Related[^1];
            item.Related.RemoveAt(item.Related.Count - 1);
        }
        foreach (var (kind, name) in item.Def.Children)
            if (kind == "on_death_child" && item.Def.Has(XrGroupItem.OnDeadChild))
                StartFree(item, name, p);
    }

    private void StartFree(Item item, string name, in XrParticle p)
    {
        if (Child(name) is not { } c)
            return;
        if (!c.Def.Has(XrEffect.TimeLimit))
        {
            _problems.Add(name);
            return;
        }
        Follow(c, p);
        c.Play();
        item.Free.Add(c);
    }
}
