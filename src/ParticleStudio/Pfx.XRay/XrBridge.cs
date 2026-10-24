using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pfx.XRay;

// One particle to convert, as xray.py takes it (manifest XRAY_PARTICLES rows): source tag, variant (root id), the
// group or effect name, and the type / state words that name the output files (<ptype>_<state>.ptc).
public sealed record XrImportPart(string Tag, string Variant, string Ref, string PType, string State);

public sealed class XrBridgeAction
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("replayed")] public bool Replayed { get; set; }
    // fitted, approximated or dropped (xray.py action_status).
    [JsonPropertyName("dayz")] public string DayZ { get; set; } = "";
}

public sealed class XrBridgeReplay
{
    [JsonPropertyName("particles")] public int Particles { get; set; }
    [JsonPropertyName("survive")] public double Survive { get; set; }
    [JsonPropertyName("life")] public double[] Life { get; set; } = Array.Empty<double>();
    [JsonPropertyName("life_mean")] public double LifeMean { get; set; }
    [JsonPropertyName("rise")] public double Rise { get; set; }
    [JsonPropertyName("turn")] public double? Turn { get; set; }
    [JsonPropertyName("x")] public double[] X { get; set; } = Array.Empty<double>();
    [JsonPropertyName("curves")] public Dictionary<string, double[]> Curves { get; set; } = new();
    [JsonPropertyName("birth")] public Dictionary<string, double[][]>? Birth { get; set; }
    [JsonPropertyName("lived")] public double[]? Lived { get; set; }
    // Steps x particles x 10 channels (null = dead): age, |size x|, |size y|, r, g, b, a, turn rate, speed, height.
    [JsonPropertyName("rec")] public double?[][][]? Rec { get; set; }
}

public sealed class XrBridgeFit
{
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("spin")] public double? Spin { get; set; }
    [JsonPropertyName("stop")] public double? Stop { get; set; }
    [JsonPropertyName("life")] public double? Life { get; set; }
    [JsonPropertyName("rate")] public double? Rate { get; set; }
}

public sealed class XrBridgeEmitter
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    // emitter, spawner (not drawn, keeps its children), haze (distortion -> heat haze data) or skipped.
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("child")] public bool Child { get; set; }
    [JsonPropertyName("window")] public double?[] Window { get; set; } = Array.Empty<double?>();
    [JsonPropertyName("notes")] public List<string> Notes { get; set; } = new();
    [JsonPropertyName("actions")] public List<XrBridgeAction> Actions { get; set; } = new();
    [JsonPropertyName("replay")] public XrBridgeReplay? Replay { get; set; }
    [JsonPropertyName("fit")] public XrBridgeFit? Fit { get; set; }
    [JsonPropertyName("effect")] public JsonElement Effect { get; set; }
}

public sealed class XrBridgeParticle
{
    [JsonPropertyName("tag")] public string Tag { get; set; } = "";
    [JsonPropertyName("variant")] public string Variant { get; set; } = "";
    [JsonPropertyName("ref")] public string Ref { get; set; } = "";
    [JsonPropertyName("ptype")] public string PType { get; set; } = "";
    [JsonPropertyName("state")] public string State { get; set; } = "";
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("group_path")] public string? GroupPath { get; set; }
    [JsonPropertyName("ptc")] public List<string> Ptc { get; set; } = new();
    [JsonPropertyName("files")] public List<string> Files { get; set; } = new();
    [JsonPropertyName("log")] public List<string> Log { get; set; } = new();
    [JsonPropertyName("emitters")] public List<XrBridgeEmitter> Emitters { get; set; } = new();
}

public sealed class XrBridgeFile
{
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("text")] public string? Text { get; set; }
}

public sealed class XrBridgeResult
{
    [JsonPropertyName("version")] public int Version { get; set; }
    [JsonPropertyName("particles")] public List<XrBridgeParticle> Particles { get; set; } = new();
    [JsonPropertyName("files")] public Dictionary<string, XrBridgeFile> Files { get; set; } = new();
    [JsonPropertyName("fx")] public JsonElement Fx { get; set; }
    [JsonPropertyName("sounds")] public JsonElement Sounds { get; set; }
    [JsonPropertyName("summary")] public Dictionary<string, int> Summary { get; set; } = new();
    [JsonPropertyName("log")] public List<string> Log { get; set; } = new();

    public static XrBridgeResult Load(string path)
    {
        using var f = File.OpenRead(path);
        return JsonSerializer.Deserialize<XrBridgeResult>(f) ?? throw new InvalidDataException("empty xray.py result");
    }
}

// Runs the user's xray.py (CG_AnomalyTools; its path comes from settings) with the --json mode added in P9: the tool
// writes the request (sources = the saved roots, particles, substitutes, the stage folder), xray.py converts exactly as
// its own pipeline does and writes the model; progress comes from its "particle i of n" lines; cancelling kills it.
public sealed class XrBridge
{
    private readonly string _exe;
    private readonly IReadOnlyList<string> _args;
    private readonly string _script;

    public XrBridge(string exe, IReadOnlyList<string> args, string xrayPy)
    {
        if (!File.Exists(xrayPy))
            throw new FileNotFoundException("xray.py not found", xrayPy);
        _exe = exe;
        _args = args;
        _script = xrayPy;
    }

    public XrBridgeResult Convert(XrSourceSet sources, IReadOnlyList<XrImportPart> parts, IReadOnlyDictionary<string, string> substitutes, string stage,
        IReadOnlyList<string>? replay, IProgress<(int Done, int Total)>? progress, CancellationToken cancel)
    {
        string work = Path.Combine(Path.GetTempPath(), "pfx_xray_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            string req = Path.Combine(work, "request.json"), output = Path.Combine(work, "model.json");
            var request = new Dictionary<string, object>
            {
                ["sources"] = sources.Request(),
                ["particles"] = parts.Select(p => new[] { p.Tag, p.Variant, p.Ref, p.PType, p.State }).ToList(),
                ["sounds"] = Array.Empty<string[]>(),
                ["subst"] = substitutes.ToDictionary(k => k.Key, k => k.Value.Replace('\\', '/')),
                ["replay"] = replay ?? Array.Empty<string>(),
                ["stage"] = stage.Replace('\\', '/'),
            };
            File.WriteAllText(req, JsonSerializer.Serialize(request));
            Run(req, output, work, parts.Count, progress, cancel);
            return XrBridgeResult.Load(output);
        }
        finally
        {
            Directory.Delete(work, true);
        }
    }

    private void Run(string req, string output, string work, int total, IProgress<(int, int)>? progress, CancellationToken cancel)
    {
        var start = new ProcessStartInfo(_exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = work,
        };
        foreach (var a in _args)
            start.ArgumentList.Add(a);
        // -I: isolated mode, so nothing from the working folder or user site-packages is imported (xray.py adds its own
        // folder for manifest.py itself).
        start.ArgumentList.Add("-I");
        start.ArgumentList.Add(_script);
        start.ArgumentList.Add("--json");
        start.ArgumentList.Add(output);
        start.ArgumentList.Add("--request");
        start.ArgumentList.Add(req);
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Python did not start");
        var stderr = process.StandardError.ReadToEndAsync();
        using (cancel.Register(() =>
               {
                   try
                   {
                       process.Kill(true);
                   }
                   catch (InvalidOperationException)
                   {
                   }
               }))
        {
            while (process.StandardOutput.ReadLine() is { } line)
            {
                var words = line.Split(' ');
                if (words.Length >= 4 && words[0] == "particle" && int.TryParse(words[1], out int done))
                    progress?.Report((done, total));
            }
            process.WaitForExit();
        }
        cancel.ThrowIfCancellationRequested();
        if (process.ExitCode != 0 || !File.Exists(output))
            throw new InvalidOperationException($"xray.py exit code {process.ExitCode}: {LastLines(stderr.Result, 6)}");
    }

    private static string LastLines(string text, int n) => string.Join(" | ", text.Trim().Split('\n').TakeLast(n).Select(l => l.Trim()));
}
