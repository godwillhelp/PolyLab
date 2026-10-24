namespace Pfx.Sim.XRay;

// Doubles in [0, 1) and normal draws from SplitMix64 (SimRandom), the same on every machine. The X-Ray engine uses its
// own generators; the A/B view matches X-Ray in distribution, not draw for draw (xray.py uses numpy's).
public sealed class XrRandom
{
    private readonly SimRandom _r;
    private double? _spare;

    public XrRandom(ulong seed) => _r = new SimRandom(seed);

    public double Next() => (_r.NextULong() >> 11) * (1.0 / (1UL << 53));

    // Box-Muller; the second value of a pair is kept for the next call.
    public double Normal(double sd)
    {
        if (_spare is double s)
        {
            _spare = null;
            return s * sd;
        }
        double u = 1.0 - Next(), v = Next();
        double r = Math.Sqrt(-2.0 * Math.Log(u));
        _spare = r * Math.Sin(2 * Math.PI * v);
        return r * Math.Cos(2 * Math.PI * v) * sd;
    }
}

// An X-Ray domain (engine particle_core.cpp, as xray.py Domain): 0 point, 1 line, 2 triangle, 3 plane, 4 box,
// 5 sphere, 6 cylinder, 7 cone, 8 blob, 9 disc, 10 rectangle; nine stored values (v0, v1, v2).
public sealed class XrDomain
{
    // xray.py NRAND_SD: the engine's NRand scales a unit draw by 1 / 0.7979.
    public const double NRandSd = 1.0 / 0.7979;

    private readonly D3 _p1, _p2, _u, _v;
    private readonly double _r1, _r2, _d, _inv;

    public XrDomain(int type, double[] a)
    {
        Type = type;
        Values = a;
        D3 P(int i) => new(a[i], a[i + 1], a[i + 2]);
        _p1 = P(0);
        switch (type)
        {
            case 1: _p2 = P(3) - _p1; break;
            case 2: _u = P(3) - _p1; _v = P(6) - _p1; break;
            case 3: _p2 = P(3).Unit(); _d = -_p1.Dot(_p2); break;
            case 4: _p1 = D3.Min(P(0), P(3)); _p2 = D3.Max(P(0), P(3)); break;
            case 5: _r1 = Math.Max(a[3], a[4]); _r2 = Math.Min(a[3], a[4]); break;
            case 6:
            case 7:
                _p2 = P(3) - _p1;
                _r1 = Math.Max(a[6], a[7]);
                _r2 = Math.Min(a[6], a[7]);
                double l2 = _p2.Length2;
                _inv = l2 != 0 ? 1.0 / l2 : 0.0;
                (_u, _v) = Basis(_p2.Unit());
                break;
            case 8: _r1 = a[3]; break;
            case 9:
                _p2 = P(3).Unit();
                _r1 = Math.Max(a[6], a[7]);
                _r2 = Math.Min(a[6], a[7]);
                (_u, _v) = Basis(_p2);
                break;
            case 10: _u = P(3); _v = P(6); break;
        }
    }

    public int Type { get; }
    public double[] Values { get; }
    public D3 P1 => _p1;
    public D3 P2 => _p2;
    public D3 U => _u;
    public D3 V => _v;
    public double R1 => _r1;
    public double R2 => _r2;

    private static (D3, D3) Basis(D3 n)
    {
        var b = new D3(1, 0, 0);
        if (Math.Abs(b.Dot(n)) > 0.999)
            b = new D3(0, 1, 0);
        var u = (b - n * b.Dot(n)).Unit();
        return (u, n.Cross(u));
    }

    public D3 Generate(XrRandom rng)
    {
        switch (Type)
        {
            case 1:
                return _p1 + _p2 * rng.Next();
            case 2:
            {
                double r1 = rng.Next(), r2 = rng.Next();
                if (r1 + r2 >= 1.0)
                {
                    r1 = 1.0 - r1;
                    r2 = 1.0 - r2;
                }
                return _p1 + _u * r1 + _v * r2;
            }
            case 4:
                return _p1 + (_p2 - _p1) * new D3(rng.Next(), rng.Next(), rng.Next());
            case 5:
            {
                var d = new D3(rng.Next() - 0.5, rng.Next() - 0.5, rng.Next() - 0.5);
                d /= Math.Max(d.Length, 1e-9);
                return _r1 == _r2 ? _p1 + d * _r1 : _p1 + d * (_r2 + rng.Next() * (_r1 - _r2));
            }
            case 6:
            case 7:
            {
                double dist = rng.Next(), theta = rng.Next() * 2.0 * Math.PI;
                double rad = _r2 + rng.Next() * (_r1 - _r2);
                double x = rad * Math.Cos(theta), y = rad * Math.Sin(theta);
                if (Type == 7)
                {
                    x *= dist;
                    y *= dist;
                }
                return _p1 + _p2 * dist + _u * x + _v * y;
            }
            case 8:
                return _p1 + new D3(rng.Normal(NRandSd * _r1), rng.Normal(NRandSd * _r1), rng.Normal(NRandSd * _r1));
            case 9:
            {
                double theta = rng.Next() * 2.0 * Math.PI;
                double rad = _r2 + rng.Next() * (_r1 - _r2);
                return _p1 + _u * (rad * Math.Cos(theta)) + _v * (rad * Math.Sin(theta));
            }
            case 10:
                return _p1 + _u * rng.Next() + _v * rng.Next();
            default:
                return _p1;
        }
    }

    public bool Within(D3 pos, XrRandom rng)
    {
        switch (Type)
        {
            case 4:
                return pos.X >= _p1.X && pos.X <= _p2.X && pos.Y >= _p1.Y && pos.Y <= _p2.Y && pos.Z >= _p1.Z && pos.Z <= _p2.Z;
            case 3:
                return pos.Dot(_p2) >= -_d;
            case 5:
            {
                double r2 = (pos - _p1).Length2;
                return r2 <= _r1 * _r1 && r2 >= _r2 * _r2;
            }
            case 6:
            case 7:
            {
                var x = pos - _p1;
                double dist = x.Dot(_p2) * _inv;
                double rs = (x - _p2 * dist).Length2;
                bool ok = Type == 7 ? rs <= (dist * _r1) * (dist * _r1) && rs >= (dist * _r2) * (dist * _r2) : rs <= _r1 * _r1 && rs >= _r2 * _r2;
                return ok && dist >= 0.0 && dist <= 1.0;
            }
            case 8:
            {
                double r2 = (pos - _p1).Length2;
                double g = Math.Exp(-0.5 * r2 / (_r1 * _r1)) / (Math.Sqrt(2 * Math.PI) * _r1);
                return rng.Next() < g;
            }
            default:
                return false;
        }
    }
}
