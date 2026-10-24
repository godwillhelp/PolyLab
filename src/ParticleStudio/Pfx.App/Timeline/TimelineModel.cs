using Pfx.App.Model;
using Pfx.Core.Model;
using Pfx.Core.Text;
using Pfx.Sim;

namespace Pfx.App.Timeline;

public enum TrackKind { State, Part, Emitter, Script }

// A stretch of time on a track (seconds of scene time), drawn as a bar.
public sealed record TimelineSpan(double From, double To, string Brush, double Opacity, string? Label, string Tip);

// A point in time on a track (a hard stop, the last particle, a loop seam, a state event).
public sealed record TimelineMark(double At, string Brush, string Tip);

public sealed record TimelineTrack(string Name, string Tip, TrackKind Kind, IReadOnlyList<TimelineSpan> Spans,
    IReadOnlyList<TimelineMark> Marks, EffectDoc? Doc = null, EnfBlock? Emitter = null);

// An anomaly state cycle from the K design (CG_Anomaly_K_DESIGN.md §4.1 machines, §4.3 per-type timings). Sample data
// until P10 brings the project's own states and their parts; the UI says so.
public sealed record StateSample(string Id, string Archetype, IReadOnlyDictionary<string, double> Timing, double OccupiedFor)
{
    public static readonly StateSample[] All =
    {
        new("electra", "burst", new Dictionary<string, double> { ["hitDelay"] = 0.3, ["active"] = 0, ["cooldown"] = 5 }, 0),
        new("zharka", "burn", new Dictionary<string, double> { ["tick"] = 0.5, ["linger"] = 10 }, 2),
        new("voronka", "pull", new Dictionary<string, double> { ["charge"] = 2, ["active"] = 0, ["cooldown"] = 5.5 }, 0),
    };
}

// The open project's own states on the preview clock (spec 3.8): its state spans, the runs of its items, the length of
// one cycle and which part a document is.
public sealed record ProjectTimeline(ProjectFile File, IReadOnlyList<StateSpan> States, IReadOnlyList<ItemRun> Runs, double Length,
    Func<EffectDoc, string?> PartOf);

// One state of the sample cycle: its name key, its time range in the cycle, and the timing keys that set it.
public sealed record StateRange(string State, double From, double To, string Keys);

public static class TimelineModel
{
    // The sample cycle: idle for one loop of the open scene, then the archetype's states (K design §4.1). Burn uses the
    // sample's scenario "one player inside for OccupiedFor seconds" (ticks while inside, then linger).
    public static List<StateRange> Cycle(StateSample s, double idle)
    {
        double T(string key) => s.Timing.TryGetValue(key, out var v) ? v : 0;
        var list = new List<StateRange> { new("idle", 0, idle, Loc.T("State.IdleKeys")) };
        double at = idle;
        void Add(string state, double length, string keys)
        {
            list.Add(new StateRange(state, at, at + length, keys));
            at += length;
        }
        switch (s.Archetype)
        {
            case "burst":
                Add("active", Math.Max(T("hitDelay"), T("active")), "hitDelay · active");
                Add("cooldown", T("cooldown"), "cooldown");
                break;
            case "burn":
                Add("active", s.OccupiedFor, "tick");
                Add("linger", T("linger"), "linger");
                break;
            case "pull":
                Add("charge", T("charge"), "charge");
                Add("active", T("active"), "active");
                Add("cooldown", T("cooldown"), "cooldown");
                break;
        }
        return list;
    }

    // The loop of the scene: the first repeating emitter's EffectTime, else the time the last particle is gone.
    public static double SceneLoop(IReadOnlyList<(EffectDoc Doc, IReadOnlyList<EmitterParams> Emitters)> parts)
    {
        var all = parts.SelectMany(p => p.Emitters).ToList();
        if (all.FirstOrDefault(e => e.Repeat) is { } r)
            return r.Cycle;
        return all.Count == 0 ? 1 : all.Max(e => e.Cycle + e.MaxLife);
    }

    // The time the view shows at first: every non-repeating part to its last particle, one loop plus the longest life
    // of repeating ones.
    public static double SceneLength(IReadOnlyList<(EffectDoc Doc, IReadOnlyList<EmitterParams> Emitters)> parts) =>
        SceneLength(parts.SelectMany(p => p.Emitters));

    public static double SceneLength(IEnumerable<EmitterParams> emitters)
    {
        var all = emitters.ToList();
        return all.Count == 0 ? 1 : Math.Max(1, all.Max(e => e.Cycle + e.MaxLife));
    }

    public static List<TimelineTrack> Build(IReadOnlyList<(EffectDoc Doc, IReadOnlyList<EmitterParams> Emitters)> parts,
        StateSample? sample, double viewEnd, ProjectTimeline? project = null)
    {
        var tracks = new List<TimelineTrack>();
        if (project != null)
        {
            var spans = project.States.Select(r => new TimelineSpan(r.From, r.To, StateBrush(r.Name), 0.55, Views.ComposeView.StateLabel(r.Name),
                Loc.F("Tip.ProjectStateSpan", Views.ComposeView.StateLabel(r.Name), Fmt(r.From), Fmt(r.To)))).ToList();
            tracks.Add(new TimelineTrack(Loc.T("Track.ProjectStates"), Loc.T("Tip.TrackProjectStates"), TrackKind.State, spans, Array.Empty<TimelineMark>()));
        }
        else if (sample != null)
        {
            var cycle = Cycle(sample, SceneLoop(parts));
            var spans = cycle.Where(r => r.To > r.From).Select(r => new TimelineSpan(r.From, r.To, StateBrush(r.State), 0.55,
                Loc.T("State." + r.State), Loc.F("Tip.StateSpan", Loc.T("State." + r.State), Fmt(r.To - r.From), r.Keys))).ToList();
            var marks = cycle.Where(r => r.To <= r.From).Select(r => new TimelineMark(r.From, StateBrush(r.State),
                Loc.F("Tip.StateEvent", Loc.T("State." + r.State), r.Keys))).ToList();
            if (sample.Archetype == "burn" && sample.Timing.TryGetValue("tick", out double tick) && tick > 0)
            {
                var active = cycle.First(r => r.State == "active");
                for (double t = active.From; t < active.To - 1e-9; t += tick)
                    marks.Add(new TimelineMark(t, "B.Err", Loc.F("Tip.StateTick", Fmt(tick))));
            }
            tracks.Add(new TimelineTrack(Loc.F("Track.States", Loc.T("Sample." + sample.Id)), Loc.T("Tip.TrackStates"), TrackKind.State, spans, marks));
        }
        foreach (var (doc, emitters) in parts)
        {
            string? partId = project?.PartOf(doc);
            var runs = project == null ? null : project.Runs.Where(r => r.Kind == ItemKind.Part && r.Ref == partId).ToList();
            double offset = runs is { Count: > 0 } ? runs[0].Start : 0;
            var partSpans = new List<TimelineSpan>();
            var partMarks = new List<TimelineMark>();
            if (runs != null)
            {
                foreach (var r in runs)
                {
                    partSpans.Add(new TimelineSpan(r.Start, r.HardStop ?? r.End, "B.TextDim", 0.45, null, Loc.F("Tip.PartRun", doc.Title, Fmt(r.Start), Fmt(r.HardStop ?? r.End))));
                    if (r.HardStop is double h)
                        partMarks.Add(new TimelineMark(h, "B.Err", Loc.F("Tip.PartHardStop", Fmt(h))));
                }
            }
            else
            {
                double end = emitters.Count == 0 ? 0 : emitters.Max(e => e.Repeat ? viewEnd : e.Cycle + e.MaxLife);
                partSpans.Add(new TimelineSpan(0, end, "B.TextDim", 0.35, null, Loc.F("Tip.PartSpan", doc.Title)));
            }
            tracks.Add(new TimelineTrack(doc.Title, Loc.F("Tip.TrackPart", doc.Title, emitters.Count), TrackKind.Part, partSpans, partMarks, doc));
            var blocks = doc.Ptc.Emitters;
            for (int i = 0; i < emitters.Count && i < blocks.Count; i++)
            {
                var track = EmitterTrack(doc, blocks[i].Block, emitters[i], viewEnd, offset);
                // In a project's states an emitter's births happen only inside its part's runs (each run restarts the
                // source; the first run's pattern stands for all of them here).
                if (runs != null)
                    track = track with
                    {
                        Spans = track.Spans.SelectMany(s => runs.Select(r => (s, r))).Where(x => x.s.To > x.r.Start && x.s.From < (x.r.HardStop ?? x.r.End))
                            .Select(x => x.s with { From = Math.Max(x.s.From, x.r.Start), To = Math.Min(x.s.To, x.r.HardStop ?? x.r.End) }).ToList(),
                        Marks = track.Marks.Where(m => runs.Any(r => m.At >= r.Start && m.At <= (r.HardStop ?? r.End))).ToList(),
                    };
                tracks.Add(track);
            }
        }
        foreach (var key in new[] { "Spin", "Light", "Sound", "Screen" })
        {
            if (project == null)
            {
                tracks.Add(new TimelineTrack(Loc.T("Track." + key), Loc.T("Tip.Track." + key), TrackKind.Script,
                    new[] { new TimelineSpan(0, viewEnd, "B.TextOff", 0.18, Loc.T("Track.NoProject"), Loc.T("Tip.Track." + key)) }, Array.Empty<TimelineMark>()));
                continue;
            }
            var spans = project.Runs.Where(r => key switch
            {
                "Spin" => r.Kind == ItemKind.Part && project.File.Parts.FirstOrDefault(x => x.Id == r.Ref) is { } x && (x.Spin != 0 || x.PathKeys != null),
                "Light" => r.Kind is ItemKind.Light or ItemKind.Haze,
                "Sound" => r.Kind == ItemKind.Sound,
                _ => r.Kind is ItemKind.Screen or ItemKind.Strike,
            }).Select(r => new TimelineSpan(r.Start, r.End, key == "Light" ? "B.Warn" : key == "Sound" ? "B.Info" : "B.Path", 0.5, r.Ref,
                Loc.F("Tip.ScriptRun", Views.ComposeView.KindLabel(r.Kind), r.Ref, Fmt(r.Start), Fmt(r.End)))).ToList();
            tracks.Add(new TimelineTrack(Loc.T("Track." + key), Loc.T("Tip.Track." + key), TrackKind.Script, spans, Array.Empty<TimelineMark>()));
        }
        return tracks;
    }

    // Births where BirthRate × BRateMast is above zero (masters as the engine evaluates them, x = effect time), per loop
    // when the emitter repeats; a non-repeating emitter stops births at EffectTime (hard stop) and its particles live
    // on for at most the longest life.
    private static TimelineTrack EmitterTrack(EffectDoc doc, EnfBlock block, EmitterParams p, double viewEnd, double offset)
    {
        var spans = new List<TimelineSpan>();
        var marks = new List<TimelineMark>();
        double cycle = p.Cycle, life = p.MaxLife;
        int loops = p.Repeat ? (int)Math.Ceiling(viewEnd / cycle) : 1;
        const int Samples = 64;
        bool movingOnly = p.BirthRate + p.BirthRateRnd <= 0 && p.BirthRateVel > 0;
        for (int k = 0; k < loops; k++)
        {
            double start = offset + k * cycle;
            double? open = null;
            for (int s = 0; s <= Samples; s++)
            {
                double x = s / (double)Samples;
                bool births = movingOnly || ((p.BirthRate + p.BirthRateRnd) * p.BRateMast.EvaluateCpu((float)x) > 0);
                if (births && open == null)
                    open = x;
                if ((!births || s == Samples) && open is double from)
                {
                    double to = births ? 1 : x;
                    spans.Add(new TimelineSpan(start + from * cycle, start + to * cycle, "B.Accent", movingOnly ? 0.3 : 0.6,
                        movingOnly ? Loc.T("Track.MovingOnly") : null, Loc.F(movingOnly ? "Tip.BirthsMoving" : "Tip.Births", Fmt(start + from * cycle), Fmt(start + to * cycle))));
                    open = null;
                }
            }
            if (p.Repeat && k > 0)
                marks.Add(new TimelineMark(start, "B.LineStrong", Loc.F("Tip.LoopSeam", Fmt(start), Fmt(cycle))));
        }
        if (!p.Repeat)
        {
            cycle += offset;
            spans.Add(new TimelineSpan(cycle, cycle + life, "B.Accent", 0.2, null, Loc.F("Tip.Tail", Fmt(cycle), Fmt(cycle + life))));
            marks.Add(new TimelineMark(cycle, "B.Err", Loc.F("Tip.HardStop", Fmt(cycle))));
            marks.Add(new TimelineMark(cycle + life, "B.TextDim", Loc.F("Tip.LastParticle", Fmt(cycle + life))));
        }
        string tip = Loc.F("Tip.TrackEmitter", p.Name, Fmt(p.Cycle), Loc.T(p.Repeat ? "Val.Yes" : "Val.No"), Fmt(life));
        return new TimelineTrack(p.Name, tip, TrackKind.Emitter, spans, marks, doc, block);
    }

    private static string StateBrush(string state) => state switch
    {
        "idle" => "B.Accent",
        "charge" => "B.Warn",
        "active" => "B.Err",
        "linger" => "B.Key",
        "cooldown" => "B.Info",
        _ => "B.TextDim",
    };

    public static string Fmt(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}
