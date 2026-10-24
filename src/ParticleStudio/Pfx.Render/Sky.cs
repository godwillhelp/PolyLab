using System.Numerics;

namespace Pfx.Render;

// Scene light the particle shaders read (sun colour, scene ambient, light direction, fog), plus the viewport's
// background and ground colours. Colours are linear.
public sealed record Sky
{
    public required string Name { get; init; }
    public required Vector3 SunColor { get; init; }
    public required Vector3 Ambient { get; init; }
    // Direction the sunlight travels (points down in daylight).
    public required Vector3 LightVec { get; init; }
    public required Vector4 FogSun { get; init; }
    public required Vector4 FogSunBack { get; init; }
    public required Vector4 FogLerp { get; init; }
    public required Vector3 Background { get; init; }
    public required Vector3 Ground { get; init; }

    // DayZDiag R0 capture S1 (Chernarus, noon, 2026-10-13): cDiffuseL1 of a material with Color 1 = sun colour,
    // cAmbientEmissiveL2 of a material with Ambient 1 and Emissive 0 = scene ambient, vLightVec, CBufferFog. Background
    // and ground from the P3 noon game frames (sky near the horizon sRGB 117 156 206, ground 98 90 60 lit by this sun).
    public static readonly Sky Noon = new()
    {
        Name = "noon",
        SunColor = new(1.167f, 1.024f, 1.008f),
        Ambient = new(0.3732f, 0.5182f, 0.6676f),
        LightVec = Vector3.Normalize(new(-0.02067f, -0.6502f, 0.7595f)),
        FogSun = new(0.1617f, 0.1875f, 0.232f, 0),
        FogSunBack = new(0.1375f, 0.1594f, 0.1972f, 0.001694f),
        FogLerp = new(0.00106f, -0.6667f, 0.00432f, 50),
        Background = Linear(117, 156, 206),
        Ground = GroundAlbedo(Linear(98, 90, 60), new(1.167f, 1.024f, 1.008f), new(0.3732f, 0.5182f, 0.6676f),
            Vector3.Normalize(new(-0.02067f, -0.6502f, 0.7595f))),
    };

    // Not measured in the game: a low warm sun and a dim sky, for checking how glows read late in the day.
    public static readonly Sky Dusk = Noon with
    {
        Name = "dusk",
        SunColor = new(0.55f, 0.32f, 0.18f),
        Ambient = new(0.10f, 0.11f, 0.17f),
        LightVec = Vector3.Normalize(new(-0.9f, -0.1f, 0.42f)),
        FogSun = new(0.20f, 0.12f, 0.08f, 0),
        FogSunBack = new(0.06f, 0.06f, 0.09f, 0.001694f),
        Background = Linear(170, 120, 95),
    };

    // Not measured in the game: moonlight only.
    public static readonly Sky Night = Noon with
    {
        Name = "night",
        SunColor = new(0.03f, 0.04f, 0.06f),
        Ambient = new(0.015f, 0.02f, 0.035f),
        LightVec = Vector3.Normalize(new(0.3f, -0.8f, -0.5f)),
        FogSun = new(0.01f, 0.012f, 0.02f, 0),
        FogSunBack = new(0.008f, 0.01f, 0.016f, 0.001694f),
        Background = Linear(12, 16, 28),
    };

    public static IReadOnlyList<Sky> All { get; } = new[] { Noon, Dusk, Night };

    public static Vector3 Linear(int r, int g, int b) => new(ToLinear(r), ToLinear(g), ToLinear(b));

    private static float ToLinear(int c)
    {
        float s = c / 255f;
        return s <= 0.04045f ? s / 12.92f : MathF.Pow((s + 0.055f) / 1.055f, 2.4f);
    }

    // The albedo that gives the seen colour under this light on flat ground (the viewport's ground shader).
    private static Vector3 GroundAlbedo(Vector3 seen, Vector3 sun, Vector3 ambient, Vector3 lightVec) =>
        seen / (sun * MathF.Max(0, -lightVec.Y) + ambient);
}
