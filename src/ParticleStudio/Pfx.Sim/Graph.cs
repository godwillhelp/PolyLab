namespace Pfx.Sim;

// A ptc graph: keys "t v .." (Alpha, Size, masters) or "t r g b .." (Color). Between keys the engine draws a uniform
// Catmull-Rom Hermite per segment: tangent (p[i+1] - p[i-1]) / 2 in segment units (W0 fit of the Workbench curve,
// prompt 2.13). At the ends the tangent is the one-sided difference p[1] - p[0] (a mirrored phantom key), so two
// keys give a straight line: BRateMast 0 0 1 1 gave the births of a linear ramp in game (P3). Results are clamped to
// 0..1 (P0: keys above 1 act as 1; below 0 not tested).
public sealed class Graph
{
    private readonly float[] _times;
    private readonly float[] _values;

    private Graph(float[] times, float[] values, int channels)
    {
        _times = times;
        _values = values;
        Channels = channels;
        _cpu = Enumerable.Range(0, CpuSamples * channels)
            .Select(i => Evaluate(i / channels / (float)CpuSamples, i % channels)).ToArray();
        _texels = Enumerable.Range(0, Texels * channels)
            .Select(i => (byte)(Evaluate(i / channels / (float)(Texels - 1), i % channels) * 255f + FloatNoise)).ToArray();
    }

    public int Channels { get; }
    public int Count => _times.Length;

    public static Graph Constant(params float[] values) => new(new[] { 0f }, values, values.Length);

    // numbers = t v0 v1 .. per key; an incomplete last key is ignored.
    public static Graph FromNumbers(IReadOnlyList<double> numbers, int channels)
    {
        int stride = channels + 1;
        int n = numbers.Count / stride;
        if (n == 0)
            return Constant(Enumerable.Repeat(1f, channels).ToArray());
        var times = new float[n];
        var values = new float[n * channels];
        for (int k = 0; k < n; k++)
        {
            times[k] = (float)numbers[k * stride];
            for (int c = 0; c < channels; c++)
                values[k * channels + c] = (float)numbers[k * stride + 1 + c];
        }
        return new Graph(times, values, channels);
    }

    // Every key holds the same values: the graph is a flat line.
    public bool IsConstant
    {
        get
        {
            for (int i = Channels; i < _values.Length; i++)
                if (_values[i] != _values[i % Channels])
                    return false;
            return true;
        }
    }

    public float Evaluate(float t, int channel = 0) => Math.Clamp(Unclamped(t, channel), 0f, 1f);

    // How the game's CPU side reads BRateMast, VelocityMast, AirResistanceMast, RotationSpeedMast (u = effect fraction
    // at birth) and RotationSpeed (u = life fraction): 32 samples of the spline at k / 32, k = 0..31, read at index
    // u * 31 with linear blending. So the graph after 31/32 is never read and a step becomes a ramp 1/31 wide. CG7:
    // births of 40 emitters, EffectTime 2 / 12 / 30 s; CG8 (DayZ 1.30): the other four by height, braking and turn
    // rate of particles born on such a ramp, each pair of emitters giving one read time, and RotationSpeed within 0.6°
    // of this read over a 10 s life where the spline is 14° off.
    public const int CpuSamples = 32;

    // The graph keys of that read; the other graphs are baked for the GPU (EvaluateGpu).
    public static bool ReadOnCpu(string key) =>
        key is "BRateMast" or "VelocityMast" or "AirResistanceMast" or "RotationSpeedMast" or "RotationSpeed";

    private readonly float[] _cpu;

    public float EvaluateCpu(float u, int channel = 0)
    {
        float x = Math.Clamp(u, 0f, 1f) * (CpuSamples - 1);
        int k = Math.Min((int)x, CpuSamples - 2);
        float f = x - k;
        return _cpu[k * Channels + channel] * (1 - f) + _cpu[(k + 1) * Channels + channel] * f;
    }

    // How the GPU reads Color, Alpha, Size (u = life fraction) and ColorMast, AlphaMast, SizeMast (u = effect fraction
    // at birth), CG8 RenderDoc dump of texsParamsTexture (DayZ 1.30), byte for byte: a 256-texel row of
    // floor(255 · spline(i / 255)), sampled at u + 0.5 / 256 with a point filter (vs_1422 lines 3-11, magnify Point),
    // so the nearest texel round(256 · u), clamped to the last.
    public const int Texels = 256;

    // The float spline lands a few ulp under an exact step (0 0 1 1 gave 5.9999 at texel 6 where the game wrote 6);
    // a thousandth of a step lifts that noise and is far below any value the floor must cut.
    private const float FloatNoise = 1e-3f;

    private readonly byte[] _texels;

    public byte Texel(int i, int channel = 0) => _texels[i * Channels + channel];

    public float EvaluateGpu(float u, int channel = 0) =>
        Texel(Math.Min((int)(Math.Clamp(u, 0f, 1f) * Texels + 0.5f), Texels - 1), channel) / 255f;

    public float Unclamped(float t, int channel = 0)
    {
        int n = _times.Length;
        if (n == 1 || t <= _times[0])
            return P(0, channel);
        if (t >= _times[n - 1])
            return P(n - 1, channel);
        int i = 0;
        while (t >= _times[i + 1])
            i++;
        float span = _times[i + 1] - _times[i];
        float s = span > 0 ? (t - _times[i]) / span : 1f;
        float p0 = P(i, channel), p1 = P(i + 1, channel);
        float m0 = i > 0 ? (p1 - P(i - 1, channel)) * 0.5f : p1 - p0;
        float m1 = i + 2 < n ? (P(i + 2, channel) - p0) * 0.5f : p1 - p0;
        float s2 = s * s, s3 = s2 * s;
        return (2 * s3 - 3 * s2 + 1) * p0 + (s3 - 2 * s2 + s) * m0 + (-2 * s3 + 3 * s2) * p1 + (s3 - s2) * m1;
    }

    private float P(int key, int channel) => _values[key * Channels + channel];
}
