namespace Pfx.Sim;

// The texture sheet rule of the game (P0, 2.12): tiles play row by row from the top-left, one every 1 / rate seconds of
// particle age from the particle's start tile, every tile plays; AnimOnce holds the last tile; RandomFrame draws the
// start tile at birth; LifetimeByAnim replaces LifeTime by tiles / AnimFPS (EmitterParams.BaseLifeTime). The simulator
// and the frame slicer both use this, so the slicer highlights the tile the viewport draws.
public static class Flipbook
{
    // P7 in game (DayZDiag, 2026-10-15): AnimFPS 0 is not a still frame — such an emitter steps one tile per second, on
    // its own as well as next to an AnimFPS 2 emitter of the same material. Negative values were not tried.
    public const float ZeroRate = 1;

    public static float Rate(EmitterParams p) => p.AnimFps > 0 ? p.AnimFps : ZeroRate;

    public static int FrameAt(EmitterParams p, int startFrame, float age)
    {
        int frames = p.Frames;
        int frame = startFrame + (int)(age * Rate(p));
        return p.AnimOnce ? Math.Min(frame, frames - 1) : frame % frames;
    }

    // The start tile of a RandomFrame particle for its draw u in [0, 1) (EmitterSim draws u from the emitter's stream
    // at birth); without RandomFrame every particle starts at tile 0 (the engine has no start-tile key).
    public static int RandomStart(EmitterParams p, float u) => Math.Min((int)(u * p.Frames), p.Frames - 1);

    // Seconds one pass over every tile takes.
    public static float PassSeconds(EmitterParams p) => p.Frames / Rate(p);
}
