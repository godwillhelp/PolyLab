using System.Globalization;
using System.Numerics;
using Pfx.App.Core;
using Pfx.App.Textures;
using Pfx.Core.Model;
using Pfx.Core.Text;
using Pfx.Render;
using Pfx.Sim;
using Pfx.Textures;

namespace Pfx.App.Lint;

// The checks of spec 3.11 with the rows 2.11, 2.12 and 2.13 add. Pure: the same input gives the same issues, so it runs
// on a worker after every change (the panel keeps the newest run) and one test per rule feeds it a crafted bad file.
public static class Linter
{
    // Sort keys closer than this swap order between frames (R0: 1-2 m apart swapped, 5-8 m never; the renderer's mark).
    private const float SwapMetres = Renderer.MaySwapMetres;
    // 2.3: an offset cut at 15-22 m was reported on a turning source and never reproduced on a still one; 13 m stays below.
    private const float FarOffsetMetres = 13;
    // 2.10 / R0: SizeMultiplier from 5 m counted as a huge camera-plane card (118 vanilla emitters, 340 CG).
    private const float BigCardMetres = 5;
    // 2.11: the shimmer of tornado debris was seen at 300 m on 1080 lines; the game camera had the 45° vertical FOV the
    // viewport uses (P11 shot: GetCurrentFOV 0.7854). Below ~3 px a moving sprite blinks.
    private const float FarRingMetres = 300;
    private const float ScreenLines = 1080;
    private const float FovDegrees = 45;
    private const float ShimmerPixels = 3;
    // 2.12: births with BirthRateRND came to BirthRate + 0.73 · RND (three pairs, law not identified).
    private const double BirthRndShare = 0.73;
    // A master under 1 % of the rate counts as births off; "most of the effect" = more than half of it (2.11: X-Ray CV
    // effects kept births off for 40-98 % of EffectTime).
    private const float GateOff = 0.01f;
    private const double GateShare = 0.5;
    // CG7: births the drawn BRateMast promises but the game's 32-sample read never gives; more than 5 % lost is reported
    // (the threshold of the CG7 scan, a probe choice).
    private const double TailLoss = 0.05;
    // CG8: the same share for the other graphs the game reads from 32 samples (Graph.ReadOnCpu), counted both ways
    // (a step the read turns into a ramp adds as much as it takes away elsewhere).
    private const double SampledLoss = 0.05;
    // CG9: births lag a frame and the frame that reaches EffectTime gives none, so a one-shot emitter loses its last one
    // or two frames. Counted at a steady 60 frames per second (a common display rate, a probe choice) against 1 ms
    // frames, the same 5 % share; and with the 137 ms frame that followed the first, cold spawn of damage_cv_tear_00
    // under a path not registered in ParticleList (CG10: registered particles are loaded at game start, warm at once).
    private const float SteadyFrame = 1f / 60;
    private const float FineFrame = 0.001f;
    private const float ColdFrame = 0.137f;
    private const double FrameLossShare = 0.05;
    // A cap refilled by random lives moves by about one birth from one draw to the next; 5 % of a 15-particle burst is
    // 0.75 births, and the mean of 16 draws moves by a quarter birth.
    private const int CountSeeds = 16;
    // W0: Workbench rounds every graph to 3 decimals when it saves.
    private const int WorkbenchDecimals = 3;
    // One 8-bit step of the baked graph texture: a dip below the lowest key smaller than this cannot be seen. Both sides
    // are clamped to 0-1 first, as the game draws them (a dip below a key of 0 changes nothing).
    private const float VisibleStep = 1f / 255;
    // Points at which a drawn graph is compared with the game's read of it.
    private const int BakeSamples = 256;

    public static readonly LintRule MaterialMissing = new("MaterialMissing", LintLevel.Block, "3.11");
    public static readonly LintRule TextureMissing = new("TextureMissing", LintLevel.Block, "3.11");
    public static readonly LintRule NpotDxt = new("NpotDxt", LintLevel.Block, "2.5");
    public static readonly LintRule NoEnf1 = new("NoEnf1", LintLevel.Block, "2.5");
    public static readonly LintRule TextureBig = new("TextureBig", LintLevel.Warn, "2.11");
    public static readonly LintRule MaxNumLow = new("MaxNumLow", LintLevel.Warn, "3.11");
    public static readonly LintRule NeverEmits = new("NeverEmits", LintLevel.Warn, "3.11");
    public static readonly LintRule UnusedMaterial = new("UnusedMaterial", LintLevel.Info, "3.11");
    public static readonly LintRule FarOffset = new("FarOffset", LintLevel.Info, "2.3");
    public static readonly LintRule GraphAboveOne = new("GraphAboveOne", LintLevel.Warn, "P0");
    public static readonly LintRule GraphUndershoot = new("GraphUndershoot", LintLevel.Warn, "P0");
    public static readonly LintRule Streak = new("Streak", LintLevel.Warn, "P0");
    public static readonly LintRule LitMoving = new("LitMoving", LintLevel.Warn, "2.11");
    public static readonly LintRule SortOnSpin = new("SortOnSpin", LintLevel.Warn, "2.11");
    public static readonly LintRule ZEnableHard = new("ZEnableHard", LintLevel.Warn, "R0");
    public static readonly LintRule CloseAlpha = new("CloseAlpha", LintLevel.Warn, "R0");
    public static readonly LintRule DenseNoSort = new("DenseNoSort", LintLevel.Warn, "R0");
    public static readonly LintRule BigCard = new("BigCard", LintLevel.Warn, "R0");
    public static readonly LintRule EmissiveSmoke = new("EmissiveSmoke", LintLevel.Warn, "R0");
    public static readonly LintRule MixedStops = new("MixedStops", LintLevel.Info, "V11");
    public static readonly LintRule Restitution = new("Restitution", LintLevel.Warn, "P3");
    public static readonly LintRule SizedShape = new("SizedShape", LintLevel.Warn, "P3");
    public static readonly LintRule BirthRateRnd = new("BirthRateRnd", LintLevel.Info, "P3");
    public static readonly LintRule Wind = new("Wind", LintLevel.Info, "P3");
    public static readonly LintRule Spring = new("Spring", LintLevel.Info, "P11");
    public static readonly LintRule SpringLocal = new("SpringLocal", LintLevel.Warn, "P12d");
    public static readonly LintRule NameKeyword = new("NameKeyword", LintLevel.Warn, "W0");
    public static readonly LintRule NameChars = new("NameChars", LintLevel.Warn, "W0");
    public static readonly LintRule NameTaken = new("NameTaken", LintLevel.Warn, "P11");
    public static readonly LintRule Nameless = new("Nameless", LintLevel.Warn, "W0");
    public static readonly LintRule ManyDecimals = new("ManyDecimals", LintLevel.Info, "W0");
    public static readonly LintRule FarShimmer = new("FarShimmer", LintLevel.Warn, "2.11");
    public static readonly LintRule StackedAxis = new("StackedAxis", LintLevel.Warn, "2.11");
    public static readonly LintRule TurnRates = new("TurnRates", LintLevel.Info, "2.11");
    public static readonly LintRule GlowAtNoon = new("GlowAtNoon", LintLevel.Info, "2.11");
    public static readonly LintRule BirthGate = new("BirthGate", LintLevel.Warn, "2.11");
    public static readonly LintRule MasterTail = new("MasterTail", LintLevel.Warn, "CG7");
    public static readonly LintRule SampledGraph = new("SampledGraph", LintLevel.Warn, "CG8");
    public static readonly LintRule FrameLoss = new("FrameLoss", LintLevel.Warn, "CG9");
    public static readonly LintRule ColdCut = new("ColdCut", LintLevel.Info, "CG9");
    public static readonly LintRule DonorTag = new("DonorTag", LintLevel.Info, "2.11");
    public static readonly LintRule DonorPath = new("DonorPath", LintLevel.Info, "3.11");

    public static IReadOnlyList<LintRule> Rules { get; } = new[]
    {
        MaterialMissing, TextureMissing, NpotDxt, NoEnf1, TextureBig, MaxNumLow, NeverEmits, UnusedMaterial, FarOffset,
        GraphAboveOne, GraphUndershoot, Streak, LitMoving, SortOnSpin, ZEnableHard, CloseAlpha, DenseNoSort, BigCard,
        EmissiveSmoke, MixedStops, Restitution, SizedShape, BirthRateRnd, Wind, Spring, SpringLocal, NameKeyword, NameChars, NameTaken,
        Nameless, ManyDecimals, FarShimmer, StackedAxis, TurnRates, GlowAtNoon, BirthGate, MasterTail, SampledGraph, FrameLoss, ColdCut,
        DonorTag, DonorPath,
    };

    // Graph keys with their channel counts (life graphs, then the masters over effect time).
    public static readonly (string Key, int Channels)[] Graphs =
    {
        ("Color", 3), ("Alpha", 1), ("RotationSpeed", 1), ("Size", 1), ("ColorMast", 3), ("AlphaMast", 1),
        ("RotationSpeedMast", 1), ("SizeMast", 1), ("BRateMast", 1), ("AirResistanceMast", 1), ("VelocityMast", 1),
    };

    // Donor tags of CG's showcase variants (memory feedback-cg-names-no-donor-tags) and the X-Ray source tags of
    // manifest.py: release names keep only the CG tag.
    // refcheck.py's DONOR_MARKS: folders of donor data (other mods, X-Ray conversions) a release must not point into.
    private static readonly string[] DonorFolders = { "alpine/", "stalkeranomaly/", "stalkerdetectors/", "uarp_anomalies/", "gravemaw", "cg_xray/" };

    private static readonly HashSet<string> DonorTags = new(StringComparer.OrdinalIgnoreCase) { "al", "ib", "ua", "kz", "id", "ar", "cv" };

    // Material and texture names of smoke-like layers (2.10: emissive smoke has no light direction at all).
    private static readonly string[] SmokeWords = { "smoke", "dust", "cloud", "fog", "steam", "mist" };

    public sealed record Emitter(int Index, EmitterView View, EmitterParams P, MaterialInfo M, string? MaterialDisk)
    {
        public Vector3 Origin => P.Offset;
        public bool Alpha => M.Found && !M.Additive && !M.DrawsNothing;
        public float Size => P.SizeMultiplier * Peak(P.Size) * Peak(P.SizeMast);
    }

    // Burst: the longest strike burst when the project plays this part only as strikes (no state starts it), else null.
    public sealed record Part(int Index, LintPart Source, IReadOnlyList<Emitter> Emitters, double? Burst = null)
    {
        public float Spin => Source.Entry?.Spin ?? 0;
        public bool OnPath => Source.Entry?.PathKeys is { Points.Length: > 0 };
        // Around dust plays under the camera, so it moves with the player and is never seen from afar.
        public bool Around => (Source.Entry?.Around ?? 0) > 0;
        public bool SourceMoves => Spin != 0 || OnPath || Around;
    }

    // The largest value a GPU graph reaches where the game bakes it (texel i holds the spline at i / 255, CG8), before
    // the 8-bit floor, so a key of 0.5 stays 0.5 in the colours and sizes recipes derive from it.
    public static float Peak(Graph g, int channel = 0)
    {
        float best = 0;
        for (int i = 0; i < Graph.Texels; i++)
            best = Math.Max(best, g.Evaluate(i / (float)(Graph.Texels - 1), channel));
        return best;
    }

    // The largest value a CPU-read graph gives (Graph.EvaluateCpu: 32 samples, linear in between, so a sample).
    public static float MasterPeak(Graph g)
    {
        float best = 0;
        for (int k = 0; k < Graph.CpuSamples; k++)
            best = Math.Max(best, g.EvaluateCpu(k / (float)(Graph.CpuSamples - 1)));
        return best;
    }

    // Mean live count once births and deaths balance: births BirthRate + 0.73 · RND (2.12) at the master's peak,
    // life X + RND / 2 (X + U[0,1) · RND), or tiles / fps with LifetimeByAnim.
    public static double Steady(EmitterParams p)
    {
        double life = p.LifetimeByAnim && p.AnimFps > 0 ? p.BaseLifeTime : p.LifeTime + 0.5 * Math.Max(0, p.LifeTimeRnd);
        return Rate(p) * Math.Max(0, life);
    }

    public static double Rate(EmitterParams p) => (p.BirthRate + BirthRndShare * Math.Max(0, p.BirthRateRnd)) * MasterPeak(p.BRateMast);

    // What one emitter needs alive: the steady count, or for a strike part the births of one burst plus the particle
    // the game emits at spawn (2.12, P4), whichever is smaller.
    public static double Needed(Part part, EmitterParams p) =>
        part.Burst is double b ? Math.Min(Steady(p), Rate(p) * b + 1) : Steady(p);

    public static List<Part> Read(LintInput input)
    {
        var materials = new Dictionary<string, (MaterialInfo Info, string? Disk)>(StringComparer.OrdinalIgnoreCase);
        (MaterialInfo, string?) Material(string? game)
        {
            if (game == null)
                return (MaterialInfo.Missing, null);
            if (materials.TryGetValue(game, out var hit))
                return hit;
            var disk = input.Files.Resolve(game);
            var info = MaterialInfo.Missing;
            if (disk != null)
            {
                try
                {
                    info = MaterialInfo.From(EmatDocument.From(EnfDocument.Parse(input.Files.Read(disk))), disk);
                }
                catch (Exception e) when (e is EnfSyntaxException or InvalidDataException or IOException or UnauthorizedAccessException)
                {
                }
            }
            return materials[game] = (info, disk);
        }

        var parts = new List<Part>();
        for (int i = 0; i < input.Parts.Count; i++)
        {
            var source = input.Parts[i];
            PtcDocument doc;
            try
            {
                doc = PtcDocument.From(EnfDocument.Parse(source.Bytes));
            }
            catch (Exception e) when (e is EnfSyntaxException or InvalidDataException)
            {
                parts.Add(new Part(i, source, Array.Empty<Emitter>()));
                continue;
            }
            var emitters = new List<Emitter>();
            var views = doc.Emitters;
            for (int k = 0; k < views.Count; k++)
            {
                var (m, disk) = Material(views[k].Text("Material"));
                var p = EmitterParams.From(views[k], game => Material(game) is ({ Found: true } info, _) ? (info.TileU, info.TileV) : null);
                emitters.Add(new Emitter(k, views[k], p, m, disk));
            }
            parts.Add(new Part(i, source, emitters, BurstOf(input.Project, source.Entry)));
        }
        return parts;
    }

    public static LintReport Run(LintInput input)
    {
        var parts = Read(input);
        var issues = new List<LintIssue>();
        void Add(LintRule rule, int part, int emitter, string? key, params object[] args) => issues.Add(new LintIssue(rule, part, emitter, key, args));

        foreach (var part in parts)
        {
            CheckFiles(input, part, Add);
            foreach (var e in part.Emitters)
                CheckEmitter(part, e, Add);
            CheckPart(input, part, Add);
        }
        CheckAcrossParts(input, parts, Add);
        CheckProject(input, parts, Add);
        return new LintReport(issues, input.Parts.Select(p => p.Title).ToList(),
            parts.Select(p => (IReadOnlyList<string>)p.Emitters.Select(e => e.View.Block.Name ?? "").ToList()).ToList());
    }

    private delegate void Sink(LintRule rule, int part, int emitter, string? key, params object[] args);

    private static void CheckFiles(LintInput input, Part part, Sink add)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in part.Emitters)
        {
            if (e.View.Text("Material") is not { } game)
                continue;
            foreach (var path in new[] { game, e.M.AlbedoMap, e.M.NormalMap })
                if (path != null && DonorFolders.FirstOrDefault(Norm(path).StartsWith) is { } folder && seen.Add("d|" + path))
                    add(DonorPath, part.Index, e.Index, "Material", path, folder.TrimEnd('/'));
            if (!e.M.Found)
            {
                if (seen.Add("m|" + game))
                    add(MaterialMissing, part.Index, e.Index, "Material", game);
                continue;
            }
            foreach (var map in new[] { e.M.AlbedoMap, e.M.NormalMap })
            {
                if (map == null || !seen.Add("t|" + map))
                    continue;
                if (input.Files.Resolve(map) is not { } disk)
                {
                    add(TextureMissing, part.Index, e.Index, "Material", map, Path.GetFileName(e.MaterialDisk ?? game));
                    continue;
                }
                if (TextureFacts.SummaryOf(disk) is not { } t)
                    continue;
                if (t.Compressed && !t.PowerOfTwo)
                    add(NpotDxt, part.Index, e.Index, "Material", Path.GetFileName(disk), t.Width, t.Height);
                if (t.Kind == "DDS" && Path.GetExtension(disk).Equals(".edds", StringComparison.OrdinalIgnoreCase))
                    add(NoEnf1, part.Index, e.Index, "Material", Path.GetFileName(disk));
                if (Math.Max(t.Width, t.Height) > TextureRules.DefaultMaxSide)
                    add(TextureBig, part.Index, e.Index, "Material", Path.GetFileName(disk), Math.Max(t.Width, t.Height), TextureRules.DefaultMaxSide);
            }
        }
    }

    private static void CheckEmitter(Part part, Emitter e, Sink add)
    {
        var p = e.P;
        int pi = part.Index, ei = e.Index;
        string name = e.View.Block.Name ?? "";

        if (name.Length == 0)
            add(Nameless, pi, ei, null);
        else
        {
            var others = part.Emitters.Where(o => o != e).Select(o => o.View.Block.Name ?? "");
            switch (Names.Problem(name, others))
            {
                case "Name.Chars": add(NameChars, pi, ei, null, name); break;
                case "Name.Keyword": add(NameKeyword, pi, ei, null, name); break;
                case "Name.Taken": add(NameTaken, pi, ei, null, name); break;
            }
        }

        string? never = p.MaxNum <= 0 ? "MaxNum"
            : p.BirthRate + Math.Max(0, p.BirthRateRnd) <= 0 && p.BirthRateVel <= 0 ? "BirthRate"
            : MasterPeak(p.BRateMast) <= 0 ? "BRateMast"
            : Peak(p.Alpha) <= 0 ? "Alpha"
            : Peak(p.AlphaMast) <= 0 ? "AlphaMast"
            : p.SizeMultiplier + Math.Max(0, p.SizeRnd) <= 0 ? "SizeMultiplier"
            : null;
        if (never != null)
            add(NeverEmits, pi, ei, never, never);
        else if (p.MaxNum < Math.Ceiling(Needed(part, p)))
            add(MaxNumLow, pi, ei, "MaxNum", (int)Math.Ceiling(Needed(part, p)), p.MaxNum);

        if (e.M.DrawsNothing)
            add(Streak, pi, ei, "Material", Path.GetFileName(e.MaterialDisk ?? ""));
        if (e.M.Found && !e.M.ZEnable && e.M.Softness <= 0)
            add(ZEnableHard, pi, ei, "ZEnable", Path.GetFileName(e.MaterialDisk ?? ""));
        if (p.Differences.HasFlag(KnownDifference.ShapeNotDrawnInGame))
            add(SizedShape, pi, ei, "ShapeSize", p.Shape.ToString());
        if (p.Restitution > 0)
            add(Restitution, pi, ei, "Restitution", Loc.Num(p.Restitution));
        if (p.BirthRateRnd > 0)
            add(BirthRateRnd, pi, ei, "BirthRateRND", Loc.Num(Math.Round(p.BirthRate + BirthRndShare * p.BirthRateRnd, 2)), Loc.Num(p.BirthRate + 0.5 * p.BirthRateRnd));
        if (p.Wind)
            add(Wind, pi, ei, "Wind");
        if (p.Differences.HasFlag(KnownDifference.SpringLocalFlies))
            add(SpringLocal, pi, ei, "LocalTransform", Loc.Num(p.Spring));
        else if (p.Spring > 0)
            add(Spring, pi, ei, "Spring", Loc.Num(p.Spring));

        bool full = e.View.Text("BillboardingType") is not ("LockedAxis" or "None");
        if (full && p.SizeMultiplier >= BigCardMetres)
            add(BigCard, pi, ei, "SizeMultiplier", Loc.Num(p.SizeMultiplier));
        if (e.M.Found && e.M.Additive && e.M.NormalMap == null && SmokeLike(e))
            add(EmissiveSmoke, pi, ei, "Material", Path.GetFileName(e.MaterialDisk ?? ""));

        bool sort = (e.View.Number("Sort") ?? 0) != 0;
        // Not on a moving source: sorting a spinning cloud made the blinking worse (2.11), so sorting is not suggested there.
        if (e.Alpha && !sort && !part.SourceMoves && Overlap(e) is var overlap and > 1)
            add(DenseNoSort, pi, ei, "Sort", Loc.Num(Math.Round(overlap, 1)));
        if (sort && part.Spin != 0)
            add(SortOnSpin, pi, ei, "Sort", Loc.Num(part.Spin));
        bool moves = part.OnPath || (part.Spin != 0 && e.Origin != Vector3.Zero);
        if (e.M.NormalMap != null && moves)
            add(LitMoving, pi, ei, "Material", Path.GetFileName(e.MaterialDisk ?? ""));
        if (!p.LocalTransform && part.Spin != 0 && e.Origin.Length() > FarOffsetMetres)
            add(FarOffset, pi, ei, "Offset", Loc.Num(Math.Round(e.Origin.Length(), 1)));
        if (part.SourceMoves && !part.Around && (part.Source.Entry?.Near ?? 0) <= 0 && Pixels(e.Size) is var px and > 0 && px < ShimmerPixels)
            add(FarShimmer, pi, ei, "SizeMultiplier", Loc.Num(Math.Round(e.Size, 2)), Loc.Num(Math.Round(px, 1)), FarRingMetres);

        var manyDecimals = new List<string>();
        foreach (var (key, channels) in Graphs)
        {
            if (e.View.Block.FindBlock(key) == null)
                continue;
            var keys = GraphKeys.Read(e.View, key, channels);
            if (keys.Count == 0)
                continue;
            double top = keys.SelectMany(k => k.Values.Skip(1)).Max();
            if (top > 1)
                add(GraphAboveOne, pi, ei, key, key, Loc.Num(top));
            var g = Graph.FromNumbers(GraphKeys.Numbers(keys), channels);
            bool cpu = Graph.ReadOnCpu(key);
            for (int c = 0; c < channels; c++)
            {
                float low = Math.Clamp(keys.Min(k => (float)k.Values[1 + c]), 0, 1);
                // A GPU graph's lowest key is floored to its texel like the dip (CG8), so the floor alone is no dip.
                if (!cpu)
                    low = Graph.Constant(low).Texel(0) / 255f;
                float dip = float.MaxValue;
                for (int i = 0; i < BakeSamples; i++)
                    dip = Math.Min(dip, cpu ? g.EvaluateCpu((i + 0.5f) / BakeSamples, c) : g.Texel(i, c) / 255f);
                if (dip < low - VisibleStep)
                {
                    add(GraphUndershoot, pi, ei, key, key, Loc.Num(Math.Round(dip, 3)), Loc.Num(Math.Round(low, 3)));
                    break;
                }
            }
            if (keys.SelectMany(k => k.Texts).Any(t => t != null && Decimals(t) > WorkbenchDecimals))
                manyDecimals.Add(key);
            if (key == "BRateMast" && p.Cycle > 0)
            {
                int off = 0;
                double drawn = 0, read = 0;
                for (int i = 0; i < BakeSamples; i++)
                {
                    float u = (i + 0.5f) / BakeSamples;
                    float v = g.EvaluateCpu(u);
                    off += v < GateOff ? 1 : 0;
                    drawn += g.Evaluate(u);
                    read += v;
                }
                double share = off / (double)BakeSamples;
                if (share > GateShare)
                    add(BirthGate, pi, ei, key, (int)Math.Round(share * 100), Loc.Num(Math.Round(share * p.Cycle, 1)), Loc.Num(p.Cycle));
                if (drawn > 0 && read < (1 - TailLoss) * drawn)
                    add(MasterTail, pi, ei, key, (int)Math.Round(100 * (1 - read / drawn)));
            }
            else if (cpu && Drives(key, p))
            {
                double drawn = 0, moved = 0;
                for (int i = 0; i < BakeSamples; i++)
                {
                    float u = (i + 0.5f) / BakeSamples;
                    drawn += g.Evaluate(u);
                    moved += Math.Abs(g.EvaluateCpu(u) - g.Evaluate(u));
                }
                if (drawn > 0 && moved > SampledLoss * drawn)
                    add(SampledGraph, pi, ei, key, key, (int)Math.Round(100 * moved / drawn));
            }
        }
        if (manyDecimals.Count > 0)
            add(ManyDecimals, pi, ei, manyDecimals[0], string.Join(", ", manyDecimals));

        // At most the births of two steady frames go: only an emitter whose drawn births are within reach of 5 % can
        // lose that share (a MaxNum cap only lowers the drawn count, so a capped burst is checked too).
        if (!p.Repeat && never == null)
        {
            double mean = 0;
            for (int i = 0; i < BakeSamples; i++)
                mean += p.BRateMast.EvaluateCpu((i + 0.5f) / BakeSamples) / BakeSamples;
            double drawn = Math.Min(Rate(p) * p.Cycle * mean, p.MaxNum);
            if (Rate(p) * 2 * SteadyFrame >= FrameLossShare * drawn)
            {
                double fine = Births(p, FineFrame, 0), steady = Births(p, SteadyFrame, 0), cold = Births(p, SteadyFrame, ColdFrame);
                if (fine > 0 && steady < (1 - FrameLossShare) * fine)
                    add(FrameLoss, pi, ei, "EffectTime", (int)Math.Round(100 * (1 - steady / fine)), Math.Round(fine, 1), Math.Round(steady, 1));
                if (fine > 1 && cold <= 1)
                    add(ColdCut, pi, ei, "EffectTime", Math.Round(fine, 1), Math.Round(cold, 1), (int)Math.Round(ColdFrame * 1000));
            }
        }
    }

    // Particles one emitter gives over its effect time at a steady frame, the frame after the spawn frame lasting
    // `first` (0 = steady), as the mean over CountSeeds draws. Births and deaths only (the simulator's skipped motion
    // keeps counts exact). One draw is not enough (CG10: 6 of CG9's 11 FrameLoss rows lost nothing on average over 400
    // seeds): the rate is drawn every step, so 1 ms and 1/60 s steps were two different random runs; it sits at its mean
    // (births follow the rate linearly). Lives stay random, drawn per particle in the same order at every frame size,
    // because a cap refilled as random lives end gives fewer births with one shared life (every death in the same
    // frame; voronka gravity_leaves_00 7 % instead of 3 %).
    private static double Births(EmitterParams p, float frame, float first)
    {
        var drawn = p with { BirthRate = p.BirthRate + p.BirthRateRnd / 2, BirthRateRnd = 0 };
        int seeds = p.LifeTimeRnd == 0 ? 1 : CountSeeds;
        long born = 0;
        for (int s = 1; s <= seeds; s++)
        {
            var sim = new EffectSim(new[] { drawn }, new SimOptions { Step = frame, FirstFrame = first, Seed = (ulong)s });
            sim.RunTo(p.Cycle + EffectSim.MaxFrame + 2 * frame, float.PositiveInfinity);
            born += sim.Emitters[0].Born;
        }
        return born / (double)seeds;
    }

    // A CPU-read graph other than BRateMast changes nothing while the value it scales is 0.
    private static bool Drives(string key, EmitterParams p) => key switch
    {
        "VelocityMast" => p.Velocity + Math.Max(0, p.VelocityRnd) != 0,
        "AirResistanceMast" => p.AirResistance + Math.Max(0, p.AirResistanceRnd) != 0,
        _ => p.RotMultiplier != 0,
    };

    private static void CheckPart(LintInput input, Part part, Sink add)
    {
        if (part.SourceMoves)
        {
            var alpha = part.Emitters.Where(e => e.Alpha).ToList();
            var stacked = alpha.Where(a => alpha.Any(b => b != a && Horizontal(a.Origin - b.Origin) < SwapMetres
                                                                 && Math.Abs(a.Origin.Y - b.Origin.Y) >= SwapMetres)).ToList();
            if (stacked.Count > 0)
                add(StackedAxis, part.Index, stacked[0].Index, "Offset", stacked.Count, string.Join(", ", stacked.Select(s => s.View.Name)));
        }
        if (part.Source.Entry?.Stop is float stop)
        {
            var ends = part.Emitters.Select(e => e.P.Repeat ? double.PositiveInfinity : e.P.Cycle).Distinct().OrderBy(t => t).ToList();
            if (ends.Count > 1)
                add(MixedStops, part.Index, -1, null, Loc.Num(stop), string.Join(" / ", ends.Select(t => double.IsInfinity(t) ? "∞" : Loc.Num(Math.Round(t, 2)))));
        }
        if (input.Project?.Export.Profile == "cg")
        {
            string stem = Path.GetFileNameWithoutExtension(part.Source.Path);
            var tags = stem.Split('_').Where(DonorTags.Contains).ToList();
            if (tags.Count > 0)
                add(DonorTag, part.Index, -1, null, stem, string.Join(", ", tags));
        }
    }

    private static void CheckAcrossParts(LintInput input, List<Part> parts, Sink add)
    {
        foreach (var part in parts)
        {
            foreach (var e in part.Emitters.Where(e => e.Alpha))
            {
                var close = parts.Where(o => o != part).SelectMany(o => o.Emitters.Where(x => x.Alpha && Vector3.Distance(x.Origin, e.Origin) < SwapMetres)
                    .Select(x => (o, x))).ToList();
                if (close.Count > 0)
                    add(CloseAlpha, part.Index, e.Index, "Offset", close.Count, close[0].o.Source.Title + " · " + close[0].x.View.Name);
            }
        }
        if (input.Sky != Sky.Noon.Name)
            return;
        bool dust = parts.SelectMany(p => p.Emitters).Any(e => e.Alpha && SmokeLike(e));
        if (!dust)
            return;
        foreach (var part in parts)
            if (part.Emitters.FirstOrDefault(e => e.M.Found && e.M.Additive && !e.M.DrawsNothing) is { } glow)
                add(GlowAtNoon, part.Index, glow.Index, "Material", part.Emitters.Count(e => e.M.Found && e.M.Additive));
    }

    private static void CheckProject(LintInput input, List<Part> parts, Sink add)
    {
        if (input.Project is not { } project)
            return;
        foreach (var state in project.States)
        {
            var rates = state.Items.Select(i => project.Find(i.Ref)).OfType<PartEntry>().Where(p => p.Spin != 0).Select(p => p.Spin).Distinct().ToList();
            if (rates.Count > 1)
                add(TurnRates, -1, -1, null, state.Name, rates.Count, string.Join(", ", rates.Select(r => Loc.Num(r))));
        }
        if (input.ProjectFolder is not { } folder)
            return;
        var used = parts.SelectMany(p => p.Emitters).Select(e => e.MaterialDisk).OfType<string>()
            .Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var m in project.Materials)
        {
            string full = Path.GetFullPath(Path.Combine(folder, m.Replace('/', Path.DirectorySeparatorChar)));
            if (!used.Contains(full))
                add(UnusedMaterial, -1, -1, null, m);
        }
    }

    private static double? BurstOf(ProjectFile? project, PartEntry? entry)
    {
        if (project == null || entry == null || project.States.Any(s => s.Items.Any(i => i.Ref == entry.Id)))
            return null;
        var strikes = project.Strikes.Where(k => k.Part == entry.Id).ToList();
        return strikes.Count > 0 ? strikes.Max(k => (double)k.Duration) : null;
    }

    // refcheck.py norm(): forward slashes, lower case, no Workbench GUID, no leading slash.
    private static string Norm(string path) =>
        MaterialTiles.StripGuid(path).Replace('\\', '/').ToLowerInvariant().TrimStart('/');

    private static bool SmokeLike(Emitter e)
    {
        string names = ((e.MaterialDisk ?? "") + "|" + (e.M.AlbedoMap ?? "")).ToLowerInvariant();
        return SmokeWords.Any(names.Contains);
    }

    // How many of the emitter's sprites cover one point of its cloud on average: live count × sprite area over the
    // area the cloud spans (the emit volume's widest side plus one sprite and one flight of the particles). Above 1
    // the sprites overlap, and their order inside the emitter shows.
    public static double Overlap(Emitter e)
    {
        var p = e.P;
        double size = e.Size * Math.Sqrt(Math.Max(0, (e.View.Number("ScaleX") ?? 1) * (e.View.Number("ScaleY") ?? 1)));
        var extent = p.SpawnHalfExtent * 2;
        double life = p.BaseLifeTime + 0.5 * Math.Max(0, p.LifeTimeRnd);
        double span = Math.Max(extent.X, Math.Max(extent.Y, extent.Z)) + size + Math.Max(0, p.Velocity) * life;
        if (span <= 0 || size <= 0)
            return 0;
        double count = Math.Min(Math.Max(0, p.MaxNum), Steady(p));
        return count * size * size / (span * span);
    }

    // Height in pixels of a sprite of this size at the far ring (2.11's measuring setup).
    public static double Pixels(double size) => size * ScreenLines / (2 * FarRingMetres * Math.Tan(FovDegrees * Math.PI / 360));

    private static float Horizontal(Vector3 v) => MathF.Sqrt(v.X * v.X + v.Z * v.Z);

    private static int Decimals(string number)
    {
        int dot = number.IndexOf('.');
        if (dot < 0)
            return 0;
        int end = number.IndexOfAny(new[] { 'e', 'E' }, dot);
        return (end < 0 ? number.Length : end) - dot - 1;
    }
}
