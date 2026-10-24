namespace Pfx.Sim;

// SplitMix64: small, fast, and the same sequence on every machine and .NET version (System.Random makes no such
// promise). Each emitter gets its own stream, so adding an emitter does not change the others.
public sealed class SimRandom
{
    private ulong _state;

    public SimRandom(ulong seed, ulong stream = 0)
    {
        _state = seed ^ (stream * 0xD1B54A32D192ED03UL);
    }

    // The whole generator state (saved and restored with the simulation).
    public ulong State { get => _state; set => _state = value; }

    public ulong NextULong()
    {
        ulong z = _state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    // Uniform in [0, 1): the top 24 bits, so every value is exact in float.
    public float Next() => (NextULong() >> 40) * (1f / 16777216f);

    public float Signed() => Next() * 2f - 1f;
}
