using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Pfx.Core.Model;
using Pfx.Core.Text;
using Pfx.Sim;

namespace PfxSim;

// Headless simulator runs for P3: live counts over time, reach box, height percentiles, steady state vs MaxNum,
// particle dumps for the game comparison, and a cost measurement.
public static class Program
{
    private const string Usage = """
        pfxsim run <file.ptc> [options]          counts over time, reach box, heights, steady state
        pfxsim dump <file.ptc> <t,t..> <out.csv> [options]   particles at the given times
        pfxsim bench <file.ptc> [options]        cost per 1000 particle steps
        pfxsim seek <file.ptc> [options]         seek to --seconds: full vs exact fast-forward, state save/load
        options: --seconds 10 --step 0.016667 --first-frame 0 (the frame after the spawn frame, s; 0 = a step)
                 --seed 1 --every 0.5 --data <root;root> (folders material paths
                 are relative to, for TileU/TileV) --motion still|turn:<deg/s>|line:<vx,vy,vz>|circle:<r,speed,X|Y|Z>
                 --yaw <deg> --gravity 9.81 --wind <x,y,z> --spring-rate 62
        """;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static int Main(string[] args)
    {
        try
        {
            switch (args.FirstOrDefault())
            {
                case "run" when args.Length >= 2:
                    Run(args[1], Options(args, 2));
                    return 0;
                case "dump" when args.Length >= 4:
                    Dump(args[1], args[2], args[3], Options(args, 4));
                    return 0;
                case "bench" when args.Length >= 2:
                    Bench(args[1], Options(args, 2));
                    return 0;
                case "seek" when args.Length >= 2:
                    Seek(args[1], Options(args, 2));
                    return 0;
                default:
                    Console.Error.WriteLine(Usage);
                    return 2;
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or EnfSyntaxException or ArgumentException
                                      or FormatException)
        {
            Console.Error.WriteLine($"error: {e.Message}");
            return 1;
        }
    }

    private static Dictionary<string, string> Options(string[] args, int from)
    {
        var map = new Dictionary<string, string>();
        for (int i = from; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--") || i + 1 >= args.Length)
                throw new ArgumentException($"Option without value: {args[i]}");
            map[args[i][2..]] = args[++i];
        }
        return map;
    }

    private static float Num(Dictionary<string, string> o, string key, float fallback) =>
        o.TryGetValue(key, out var s) ? float.Parse(s, Inv) : fallback;

    private static Vector3 Vec(string text)
    {
        var p = text.Split(',').Select(s => float.Parse(s, Inv)).ToArray();
        if (p.Length != 3)
            throw new ArgumentException($"Expected x,y,z: {text}");
        return new Vector3(p[0], p[1], p[2]);
    }

    private static EffectSim Build(string path, Dictionary<string, string> o)
    {
        var options = new SimOptions
        {
            Step = Num(o, "step", 1f / 60f),
            FirstFrame = Num(o, "first-frame", 0),
            Seed = o.TryGetValue("seed", out var seed) ? ulong.Parse(seed, Inv) : 1,
            Gravity = Num(o, "gravity", 9.81f),
            Wind = o.TryGetValue("wind", out var wind) ? Vec(wind) : Vector3.Zero,
            SpringRate = Num(o, "spring-rate", 62f),
        };
        if (options.Step <= 0)
            throw new ArgumentException("--step must be > 0");
        float yaw = Num(o, "yaw", 0);
        string motion = o.GetValueOrDefault("motion", "still");
        string arg = motion.Contains(':') ? motion[(motion.IndexOf(':') + 1)..] : "";
        SourceMotion m = motion.Split(':')[0] switch
        {
            "still" => SourceMotion.Still(yaw: yaw),
            "turn" => new SourceMotion.Turning(Vector3.Zero, yaw, float.Parse(arg, Inv)),
            "line" => new SourceMotion.Line(Vector3.Zero, Vec(arg), yaw),
            "circle" => Circle(arg),
            _ => throw new ArgumentException($"Unknown motion: {motion}"),
        };
        var roots = o.GetValueOrDefault("data", "").Split(';', StringSplitOptions.RemoveEmptyEntries);
        return EffectSim.From(PtcDocument.Load(path), options, m, MaterialTiles.Resolver(roots));
    }

    private static SourceMotion Circle(string arg)
    {
        var p = arg.Split(',');
        if (p.Length != 3)
            throw new ArgumentException("circle:<radius,speed,X|Y|Z>");
        return new SourceMotion.Circle(Vector3.Zero, float.Parse(p[0], Inv), float.Parse(p[1], Inv),
            Enum.Parse<CircleAxis>(p[2], true));
    }

    private static void Run(string path, Dictionary<string, string> o)
    {
        var sim = Build(path, o);
        float seconds = Num(o, "seconds", 10), every = Num(o, "every", 0.5f);
        var emitters = sim.Emitters;
        Console.WriteLine($"{path}: {emitters.Count} emitters, step {sim.Options.Step.ToString("0.######", Inv)} s, " +
                          $"seed {sim.Options.Seed}, motion {o.GetValueOrDefault("motion", "still")}");
        foreach (var e in emitters)
        {
            if (e.Params.Material != null && !e.Params.MaterialRead)
                Console.WriteLine($"  note: {e.Params.Name}: material not found under --data, flipbook taken as 1 x 1");
            if (e.Params.Differences != KnownDifference.None)
                Console.WriteLine($"  known differences: {e.Params.Name}: {e.Params.Differences}");
        }
        Console.WriteLine("t\ttotal\t" + string.Join("\t", emitters.Select(e => e.Params.Name)));
        var lo = new Vector3(float.MaxValue);
        var hi = new Vector3(float.MinValue);
        var heights = new List<float>();
        var sums = new double[emitters.Count];
        int samplesLate = 0;
        for (int k = 1; k * every <= seconds + 1e-4f; k++)
        {
            sim.RunTo(k * every);
            Console.WriteLine(sim.Time.ToString("0.00", Inv) + "\t" + sim.Count + "\t" +
                              string.Join("\t", emitters.Select(e => e.Count)));
            for (int i = 0; i < emitters.Count; i++)
            {
                foreach (var p in sim.Render(i))
                {
                    lo = Vector3.Min(lo, p.Position);
                    hi = Vector3.Max(hi, p.Position);
                    heights.Add(p.Position.Y);
                }
            }
            if (sim.Time >= seconds / 2)
            {
                samplesLate++;
                for (int i = 0; i < emitters.Count; i++)
                    sums[i] += emitters[i].Count;
            }
        }
        Console.WriteLine();
        Console.WriteLine("emitter\tMaxNum\tsteady (rate x life)\tsim mean (2nd half)\tborn");
        for (int i = 0; i < emitters.Count; i++)
        {
            var e = emitters[i];
            float steady = e.SteadyState;
            string capped = steady > e.Params.MaxNum ? "  capped by MaxNum" : "";
            double mean = samplesLate > 0 ? sums[i] / samplesLate : 0;
            Console.WriteLine($"{e.Params.Name}\t{e.Params.MaxNum}\t{steady.ToString("0.0", Inv)}\t" +
                              $"{mean.ToString("0.0", Inv)}\t{e.Born}{capped}");
        }
        if (heights.Count == 0)
        {
            Console.WriteLine("no particles");
            return;
        }
        heights.Sort();
        float Q(double q) => heights[(int)Math.Round(q * (heights.Count - 1))];
        Console.WriteLine($"reach box min {V(lo)} max {V(hi)} size {V(hi - lo)}");
        Console.WriteLine($"height p5 {Q(0.05).ToString("0.00", Inv)} p50 {Q(0.5).ToString("0.00", Inv)} " +
                          $"p95 {Q(0.95).ToString("0.00", Inv)} ({heights.Count} particle samples)");
    }

    private static string V(Vector3 v) =>
        $"{v.X.ToString("0.00", Inv)} {v.Y.ToString("0.00", Inv)} {v.Z.ToString("0.00", Inv)}";

    private static void Dump(string path, string times, string outPath, Dictionary<string, string> o)
    {
        var sim = Build(path, o);
        using var w = new StreamWriter(outPath);
        w.WriteLine("t,emitter,i,x,y,z,vx,vy,vz,size,r,g,b,a,angle,frame,age,stretch_m");
        foreach (float t in times.Split(',').Select(s => float.Parse(s, Inv)).Order())
        {
            sim.RunTo(t);
            for (int e = 0; e < sim.Emitters.Count; e++)
            {
                var list = sim.Render(e);
                for (int i = 0; i < list.Count; i++)
                {
                    var p = list[i];
                    w.WriteLine(string.Join(",", new object[]
                    {
                        sim.Time, sim.Emitters[e].Params.Name, i, p.Position.X, p.Position.Y, p.Position.Z,
                        p.Velocity.X, p.Velocity.Y, p.Velocity.Z, p.Size, p.Color.X, p.Color.Y, p.Color.Z, p.Color.W,
                        p.Angle, p.Frame, p.Age, p.StretchLength,
                    }.Select(x => Convert.ToString(x, Inv))));
                }
            }
        }
        Console.WriteLine($"wrote {outPath}");
    }

    private static void Bench(string path, Dictionary<string, string> o)
    {
        var sim = Build(path, o);
        float seconds = Num(o, "seconds", 10);
        long particleSteps = 0;
        var step = new Stopwatch();
        var render = new Stopwatch();
        while (sim.Time + sim.Options.Step * 0.5f <= seconds)
        {
            step.Start();
            sim.Step();
            step.Stop();
            render.Start();
            for (int e = 0; e < sim.Emitters.Count; e++)
                sim.Render(e);
            render.Stop();
            particleSteps += sim.Count;
        }
        double Per1000(Stopwatch w) => particleSteps > 0 ? w.Elapsed.TotalMilliseconds * 1000 / particleSteps * 1000 : 0;
        Console.WriteLine($"{path}: {particleSteps} particle steps, step {step.Elapsed.TotalMilliseconds:0.0} ms " +
                          $"({Per1000(step):0.0} us per 1000 particles), render {render.Elapsed.TotalMilliseconds:0.0} ms " +
                          $"({Per1000(render):0.0} us per 1000), final count {sim.Count}");
    }

    // A seek to --seconds from 0: full steps vs the exact fast-forward, plus the cost of saving and loading the state
    // there (what a checkpoint costs). Medians of five runs.
    private static void Seek(string path, Dictionary<string, string> o)
    {
        float seconds = Num(o, "seconds", 10);
        const int Runs = 5;
        double Median(Func<double> f) => Enumerable.Range(0, Runs).Select(_ => f()).OrderBy(x => x).ElementAt(Runs / 2);
        double Time(Action a)
        {
            var w = Stopwatch.StartNew();
            a();
            return w.Elapsed.TotalMilliseconds;
        }
        double full = Median(() => { var s = Build(path, o); return Time(() => s.RunTo(seconds)); });
        double fast = Median(() => { var s = Build(path, o); return Time(() => s.RunTo(seconds, exactAt: seconds)); });
        var sim = Build(path, o);
        sim.RunTo(seconds);
        EffectSimState? state = null;
        double save = Median(() => Time(() => state = sim.Save()));
        double load = Median(() => Time(() => sim.Load(state!)));
        Console.WriteLine($"{Path.GetFileName(path)} to {seconds.ToString(Inv)} s: {sim.Count} alive, full {full:0.0} ms, " +
                          $"fast-forward {fast:0.0} ms, save {save:0.00} ms, load {load:0.00} ms, state {state!.Bytes / 1024.0:0} KiB, " +
                          $"longest life {sim.Emitters.Max(e => e.MaxLife).ToString("0.##", Inv)} s");
    }
}
