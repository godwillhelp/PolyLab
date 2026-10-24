using System.Numerics;

namespace Pfx.Sim;

// Everything the simulator needs that the .ptc does not say. Values marked "not verified" are not measured in game
// (journal P3); they stay parameters so a measurement can replace them without code changes.
public sealed record SimOptions
{
    // Fixed simulation step in seconds. The engine steps once per rendered frame with that frame's time, at most
    // EffectSim.MaxFrame (CG9).
    public float Step { get; init; } = 1f / 60f;

    // Length of the frame after the spawn frame, in seconds (0 = Step): the first spawn of a part on a client loads it
    // inside the create call, so that frame is long (CG9: damage_cv_tear_00 128 ms in the call, a 137 ms frame) and
    // short one-shot emitters lose their births. Only a part not registered in ParticleList: the game loads the
    // registered ones when it starts (CG10: first create 0.2-0.5 ms). Clamped at EffectSim.MaxFrame like any frame.
    public float FirstFrame { get; init; }

    public ulong Seed { get; init; } = 1;

    // m/s² multiplied by GravityMultiply: a GravityMultiply 0.25 trail fitted 9.89 (P3, ±1 % camera calibration).
    public float Gravity { get; init; } = 9.81f;

    // Drift velocity of emitters with Wind 1 (not verified: a still particle drifted ~0.2 × the weather wind in P3
    // while the wind changed; velocity or force not separated).
    public Vector3 Wind { get; init; } = Vector3.Zero;

    // Spring pulls a particle to its emitter at SpringRate · Spring per second (first order): trails of Spring 0.001
    // .. 0.15 gave 60–65 (P3; the client frame rate was not recorded and frame-rate dependence is not tested).
    public float SpringRate { get; init; } = 62f;

    // When the source runs on the scene clock (a state of a composition): null = from 0, forever.
    public SimWindow? Window { get; init; }
}

// A particle source started at Start seconds of the scene clock. At SoftStop births end and the live particles finish
// their lives (StopParticle()); at HardStop every particle is removed at once (StopParticle(IMMEDIATE)). The emitters'
// own clocks start at Start, as a source created then.
public sealed record SimWindow(float Start, float? SoftStop, float? HardStop);
