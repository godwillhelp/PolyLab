using Pfx.App.Textures;
using Pfx.App.Timeline;
using Pfx.Core.Model;
using Pfx.Sim;

namespace Pfx.App.Lint;

public sealed record BudgetPart(string Title, int Peak, double PeakAt, int MaxNum, long TextureBytes, int Textures);

// The budget panel's numbers (spec 3.11) that do not depend on the camera: particles alive at the peak (simulated over
// the whole cycle, every part in its runs), texture video memory, and the lights, hazes and sounds alive at once.
// Overdraw depends on the camera and is read from the viewport's frame instead (ScreenFill).
public sealed record BudgetReport(IReadOnlyList<BudgetPart> Parts, int Peak, double PeakAt, double Length, long TextureBytes, int Textures,
    int Lights, int LightsTotal, int Hazes, int HazesTotal, int Sounds, int SoundsTotal, bool Project)
{
    // The engine's dynamic light limit per view (the anomaly work's "2 of 32", board 5).
    public const int LightLimit = 32;

    public static readonly BudgetReport Empty = new(Array.Empty<BudgetPart>(), 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, false);

    public static BudgetReport Run(LintInput input, float step, CancellationToken cancel)
    {
        var parts = Linter.Read(input);
        var project = input.Project;
        double open = TimelineModel.SceneLength(parts.SelectMany(p => p.Emitters).Select(e => e.P));
        var runs = project != null ? StateCycle.Runs(project, open) : new List<ItemRun>();
        double length = project != null ? Math.Max(StateCycle.Length(project, open), step) : open;

        var sims = new List<(int Part, EffectSim Sim)>();
        foreach (var part in parts)
        {
            var ps = part.Emitters.Select(e => e.P).ToList();
            // A strike plays its part in bursts at random gaps: one burst at the start of each strike run is counted.
            var windows = part.Source.Entry is { } entry && project != null
                ? runs.Where(r => r.Kind == ItemKind.Part && r.Ref == entry.Id)
                    .Select(r => (SimWindow?)new SimWindow((float)r.Start, (float)r.End, r.HardStop is double h ? (float)h : null))
                    .Concat(runs.Where(r => r.Kind == ItemKind.Strike && project.Find(r.Ref) is StrikeEntry k && k.Part == entry.Id)
                        .Select(r => (SimWindow?)new SimWindow((float)r.Start, (float)r.Start + ((StrikeEntry)project.Find(r.Ref)!).Duration, null))).ToList()
                : new List<SimWindow?> { null };
            foreach (var w in windows)
                sims.Add((part.Index, new EffectSim(ps, new SimOptions { Step = step, Window = w })));
        }

        var peak = new int[parts.Count];
        var peakAt = new double[parts.Count];
        int total = 0;
        double totalAt = 0;
        var now = new int[parts.Count];
        for (long n = 1; n * (double)step <= length + step / 2; n++)
        {
            cancel.ThrowIfCancellationRequested();
            Array.Clear(now);
            foreach (var (part, sim) in sims)
            {
                sim.Step();
                now[part] += sim.Count;
            }
            int sum = 0;
            for (int i = 0; i < now.Length; i++)
            {
                sum += now[i];
                if (now[i] > peak[i])
                    (peak[i], peakAt[i]) = (now[i], n * (double)step);
            }
            if (sum > total)
                (total, totalAt) = (sum, n * (double)step);
        }

        var textures = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<BudgetPart>();
        foreach (var part in parts)
        {
            var mine = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in part.Emitters.Where(e => e.M.Found))
                foreach (var map in new[] { e.M.AlbedoMap, e.M.NormalMap })
                    if (map != null && input.Files.Resolve(map) is { } disk && TextureFacts.SummaryOf(disk) is { } t)
                        mine[disk] = textures[disk] = t.GpuBytes;
            rows.Add(new BudgetPart(part.Source.Title, peak[part.Index], peakAt[part.Index], part.Emitters.Sum(e => Math.Max(0, e.P.MaxNum)), mine.Values.Sum(), mine.Count));
        }

        int AtOnce(Func<ItemRun, bool> which) => runs.Where(which).Select(r => runs.Count(o => which(o) && o.Start <= r.Start && r.Start < o.End)).DefaultIfEmpty(0).Max();
        bool IsLight(ItemRun r) => r.Kind is ItemKind.Light or ItemKind.Strike;
        bool IsHaze(ItemRun r) => r.Kind == ItemKind.Haze || (r.Kind == ItemKind.Light && project!.Find(r.Ref) is LightEntry { HazeRadius: > 0 });
        int lightsTotal = project != null ? project.Lights.Count + project.Strikes.Count : 0;
        int hazesTotal = project != null ? project.Hazes.Count + project.Lights.Count(l => l.HazeRadius > 0) : 0;
        return new BudgetReport(rows, total, totalAt, length, textures.Values.Sum(), textures.Count,
            AtOnce(IsLight), lightsTotal, project != null ? AtOnce(IsHaze) : 0, hazesTotal,
            AtOnce(r => r.Kind == ItemKind.Sound), project?.Sounds.Count ?? 0, project != null);
    }
}
