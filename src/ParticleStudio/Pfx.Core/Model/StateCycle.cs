namespace Pfx.Core.Model;

// One state on the preview clock.
public sealed record StateSpan(int Index, string Name, double From, double To);

// One run of a project item on the preview clock: it starts at Start (state start + delay) and its state ends at End.
// HardStop is the part's own hard stop (Start + PartEntry.Stop) when it falls before End; lights and hazes with a
// lifetime end after life + fade-out. The game soft-stops parts at End (births stop, live particles finish their lives).
public sealed record ItemRun(string Ref, ItemKind Kind, int State, double Start, double End, double? HardStop);

// The project's states played once in order, as the preview and the timeline show them. A state with Duration 0 lasts
// until an event in the game; here it lasts `openLength` (one loop of the scene). A state with OverIdle keeps the first
// state's items running through it (their runs end with the over-idle stretch).
public static class StateCycle
{
    public static List<StateSpan> Spans(ProjectFile p, double openLength)
    {
        var spans = new List<StateSpan>();
        double at = 0;
        for (int i = 0; i < p.States.Count; i++)
        {
            var s = p.States[i];
            double length = s.Duration > 0 ? s.Duration : openLength;
            spans.Add(new StateSpan(i, s.Name, at, at + length));
            at += length;
        }
        return spans;
    }

    public static double Length(ProjectFile p, double openLength) => Spans(p, openLength).LastOrDefault()?.To ?? 0;

    public static List<ItemRun> Runs(ProjectFile p, double openLength)
    {
        var spans = Spans(p, openLength);
        var runs = new List<ItemRun>();
        for (int i = 0; i < spans.Count; i++)
        {
            var span = spans[i];
            double stateEnd = span.To;
            // The first state continues under the over-idle states that directly follow it.
            if (i == 0)
                for (int j = 1; j < spans.Count && p.States[j].OverIdle; j++)
                    stateEnd = spans[j].To;
            foreach (var item in p.States[i].Items)
            {
                if (p.Find(item.Ref) is not { } found)
                    continue;
                double start = span.From + Math.Max(0, item.Delay);
                double end = stateEnd;
                if (start >= end)
                    continue;
                double? hard = null;
                if (found is PartEntry part && part.Stop is float stop && start + stop < end)
                    hard = start + stop;
                if (found is LightEntry light && light.Life > 0)
                    end = Math.Min(end, start + light.Life + light.FadeOut);
                if (found is HazeEntry haze && haze.Life > 0)
                    end = Math.Min(end, start + haze.Life + haze.Fade);
                runs.Add(new ItemRun(item.Ref, found.Kind, i, start, end, hard));
            }
        }
        return runs;
    }

    // The timing block of the anomaly type (config.json, K design §2) from the state lengths and the extra keys: the
    // archetype's machine states are idle, charge, active, cooldown and linger.
    public static Dictionary<string, float> Timing(ProjectFile p)
    {
        float Len(string name) => p.States.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))?.Duration ?? 0;
        var t = new Dictionary<string, float>
        {
            ["hitDelay"] = p.Anomaly.HitDelay,
            ["active"] = Len("active"),
            ["cooldown"] = Len("cooldown"),
            ["tick"] = p.Anomaly.Tick,
            ["linger"] = Len("linger"),
            ["charge"] = Len("charge"),
        };
        return t;
    }
}
