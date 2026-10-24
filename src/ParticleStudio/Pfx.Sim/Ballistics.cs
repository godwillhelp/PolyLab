using System.Numerics;
using System.Runtime.CompilerServices;

namespace Pfx.Sim;

// One particle's flight as EmitterSim steps it: the step's gravity is added, the velocity decays by exp(-k dt) (linear
// drag, P3), then the position moves by the new velocity plus the wind drift. EmitterSim and the path fits (PathFit)
// share it, so a fitted path is the path the viewport draws.
public static class Ballistics
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Step(ref Vector3 position, ref Vector3 velocity, Vector3 gained, float decay, Vector3 drift, float dt)
    {
        velocity = (velocity + gained) * decay;
        position += (velocity + drift) * dt;
    }

    // Steps a particle survives: it is removed in the step its age reaches its life, before moving (EmitterSim.Step),
    // so it is drawn at ages 0, dt, .. up to the last whole step below the life.
    public static int Steps(float life, float dt) => Math.Max(1, (int)MathF.Ceiling(life / dt - 1e-4f));

    // Positions at ages 0, dt, 2 dt, .. for a particle born at start with the given launch velocity.
    public static Vector3[] Path(Vector3 start, Vector3 velocity, float gravityMultiply, float drag, float life, SimOptions o)
    {
        float dt = o.Step;
        var gained = new Vector3(0, -o.Gravity, 0) * (gravityMultiply * dt);
        float decay = MathF.Exp(-Math.Max(drag, 0) * dt);
        var path = new Vector3[Steps(life, dt)];
        Vector3 p = start, v = velocity;
        path[0] = p;
        for (int i = 1; i < path.Length; i++)
        {
            Step(ref p, ref v, gained, decay, Vector3.Zero, dt);
            path[i] = p;
        }
        return path;
    }
}
