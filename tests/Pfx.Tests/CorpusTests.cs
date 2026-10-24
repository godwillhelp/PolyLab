using Pfx.Core.Model;
using Pfx.Core.Schema;
using Pfx.Core.Text;
using Xunit.Abstractions;

namespace Pfx.Tests;

// PFX_CORPUS = folders with .ptc/.emat files, separated by ';' (e.g. the game's graphics\particles and a mod's data).
public sealed class CorpusFactAttribute : FactAttribute
{
    public CorpusFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PFX_CORPUS")))
            Skip = "PFX_CORPUS is not set";
    }
}

public class CorpusTests
{
    private readonly ITestOutputHelper _out;

    public CorpusTests(ITestOutputHelper output)
    {
        _out = output;
    }

    private static IEnumerable<string> Roots() =>
        Environment.GetEnvironmentVariable("PFX_CORPUS")!.Split(';', StringSplitOptions.RemoveEmptyEntries);

    private static IEnumerable<string> Files(string root, string ext) =>
        Directory.EnumerateFiles(root, "*" + ext, SearchOption.AllDirectories);

    [CorpusFact]
    public void Every_file_round_trips_byte_for_byte()
    {
        var failures = new List<string>();
        foreach (var root in Roots())
        {
            int ptc = 0, emat = 0;
            foreach (var path in Files(root, ".ptc").Concat(Files(root, ".emat")))
            {
                var bytes = File.ReadAllBytes(path);
                try
                {
                    var back = EnfDocument.Parse(bytes).ToBytes();
                    if (!back.AsSpan().SequenceEqual(bytes))
                        failures.Add("differs: " + path);
                }
                catch (EnfSyntaxException e)
                {
                    failures.Add($"syntax: {path}: {e.Message}");
                }
                if (path.EndsWith(".ptc", StringComparison.OrdinalIgnoreCase))
                    ptc++;
                else
                    emat++;
            }
            _out.WriteLine($"{root}: {ptc} ptc, {emat} emat");
        }
        foreach (var f in failures.Take(20))
            _out.WriteLine(f);
        Assert.Empty(failures);
    }

    [CorpusFact]
    public void Every_ptc_reads_as_an_effect_and_every_key_is_known_or_reported()
    {
        var unknown = new Dictionary<string, int>();
        int effects = 0, emitters = 0;
        var errors = new List<string>();
        foreach (var path in Roots().SelectMany(r => Files(r, ".ptc")))
        {
            PtcDocument doc;
            try
            {
                doc = PtcDocument.Load(path);
            }
            catch (InvalidDataException e)
            {
                errors.Add($"{path}: {e.Message}");
                continue;
            }
            effects++;
            foreach (var e in doc.Emitters)
            {
                emitters++;
                foreach (var item in e.Block.Items)
                {
                    string key = Pfx.Core.Editing.EnfEdits.KeyOf(item);
                    if (doc.Schema.Find(SchemaScope.Emitor, key) == null)
                        unknown[key] = unknown.GetValueOrDefault(key) + 1;
                }
            }
        }
        _out.WriteLine($"{effects} effects, {emitters} emitters");
        foreach (var (key, n) in unknown.OrderByDescending(p => p.Value))
            _out.WriteLine($"key not in schema (kept as written): {key} x{n}");
        foreach (var e in errors.Take(20))
            _out.WriteLine(e);
        Assert.Empty(errors);
    }
}
