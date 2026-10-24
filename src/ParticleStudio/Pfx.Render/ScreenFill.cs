using System.Numerics;
using Pfx.Sim;

namespace Pfx.Render;

// Sprite overdraw of the shown frame, counted on the CPU: every live sprite's square on screen (its drawn width times
// ScaleX / ScaleY, cut to the screen) summed and divided by the screen's pixels. 1.0 = the particles cost one full
// screen of pixels. Stretched sprites count their unstretched square (stretch adds length, not measured here).
public static class ScreenFill
{
    public sealed record Result(double Total, IReadOnlyDictionary<(int Effect, int Emitter), double> PerEmitter);

    public static Result Estimate(Camera camera, int width, int height, IReadOnlyList<EffectPreview> previews)
    {
        var per = new Dictionary<(int, int), double>();
        double total = 0, screen = Math.Max(1.0, (double)width * height);
        float focal = camera.FocalPixels(height);
        var list = new List<RenderParticle>();
        for (int fx = 0; fx < previews.Count; fx++)
        {
            var preview = previews[fx];
            foreach (var sim in preview.Sims)
            {
                var (source, yaw, _) = sim.Motion.At(sim.Time);
                for (int e = 0; e < sim.Emitters.Count; e++)
                {
                    if (preview.Materials[e].DrawsNothing)
                        continue;
                    list.Clear();
                    sim.Emitters[e].Render(list, source, yaw);
                    var scale = preview.Scales[e];
                    double sum = 0;
                    foreach (var p in list)
                    {
                        if (camera.Project(p.Position, width, height) is not { } at)
                            continue;
                        double half = p.Size * 0.5 * focal / at.Z;
                        double hx = half * scale.X, hy = half * scale.Y;
                        double w = Math.Min(width, at.X + hx) - Math.Max(0, at.X - hx);
                        double h = Math.Min(height, at.Y + hy) - Math.Max(0, at.Y - hy);
                        if (w > 0 && h > 0)
                            sum += w * h;
                    }
                    sum /= screen;
                    per[(fx, e)] = per.GetValueOrDefault((fx, e)) + sum;
                    total += sum;
                }
            }
        }
        return new Result(total, per);
    }
}
