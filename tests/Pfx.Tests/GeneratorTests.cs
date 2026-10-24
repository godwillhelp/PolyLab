using System.Globalization;
using System.Text.Json;
using Pfx.Core.Generators;
using Pfx.Core.Model;
using Pfx.Core.Text;

namespace Pfx.Tests;

// P12: the tornado generator against own_tornado.py. PFX_TORNADO_GOLDEN = the folder tools/ParticleStudio/tornado/golden.py wrote
// (the script's output with its own constants and with the variant constants listed in golden.json).
public sealed class GeneratorTests
{
    [Fact]
    public void Number_format_is_pythons_percent_3f_without_zeros()
    {
        Assert.Equal("0", TornadoGenerator.Num(0));
        Assert.Equal("0", TornadoGenerator.Num(-0.0001));
        Assert.Equal("1.5", TornadoGenerator.Num(1.5));
        Assert.Equal("10.46", TornadoGenerator.Num(10.4597));
        Assert.Equal("-261.799", TornadoGenerator.Num(-261.79938779914943));
        Assert.Equal("2", TornadoGenerator.Num(1.9999));
    }

    [Fact]
    public void Generated_parts_parse_and_carry_the_script_data()
    {
        var result = TornadoGenerator.Generate(new TornadoSettings());
        Assert.Contains("Gen.NoBoltDonor", result.Notes);
        foreach (var part in result.Parts)
            Assert.NotEmpty(PtcDocument.From(EnfDocument.Parse(part.Bytes)).Emitters);
        Assert.Equal(-150f, result.Parts.Single(p => p.Suffix == "_t1").Spin);
        Assert.Equal(150f, result.Parts.Single(p => p.Suffix == "_t9").Near);
        Assert.Equal(new[] { -261.8, 10.0 }, result.Swirl);
    }

    [Theory]
    [InlineData("default")]
    [InlineData("variant")]
    public void Output_equals_own_tornado_py_byte_for_byte(string run)
    {
        string? dir = Environment.GetEnvironmentVariable("PFX_TORNADO_GOLDEN");
        if (string.IsNullOrWhiteSpace(dir))
            return;
        using var golden = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, "golden.json")));
        var root = golden.RootElement;
        var data = root.GetProperty(run);
        var settings = Apply(new TornadoSettings { BoltDonor = root.GetProperty("donor").GetString() }, data.GetProperty("overrides"));
        var result = TornadoGenerator.Generate(settings);
        Assert.Empty(result.Notes);
        var expected = Directory.GetFiles(Path.Combine(dir, run), "*.ptc").Select(f => Path.GetFileName(f)).Order().ToList();
        Assert.Equal(expected, result.Parts.Select(p => settings.Name + p.Suffix + ".ptc").Order().ToList());
        foreach (var part in result.Parts)
        {
            byte[] want = File.ReadAllBytes(Path.Combine(dir, run, settings.Name + part.Suffix + ".ptc"));
            Assert.True(want.AsSpan().SequenceEqual(part.Bytes), $"{run}: {settings.Name}{part.Suffix}.ptc differs from own_tornado.py");
        }
        foreach (var spin in data.GetProperty("spins").EnumerateArray())
        {
            string suffix = "_" + spin[0].GetString()!["cg_".Length..];
            Assert.Equal((float)spin[1].GetDouble(), result.Parts.Single(p => p.Suffix == suffix).Spin);
        }
        foreach (var near in data.GetProperty("near").EnumerateObject())
            Assert.Equal((float)near.Value.GetDouble(), result.Parts.Single(p => p.Suffix == "_" + near.Name["cg_".Length..]).Near);
        Assert.Equal(data.GetProperty("swirl").EnumerateArray().Select(v => v.GetDouble()), result.Swirl);
        var strike = data.GetProperty("strike");
        Assert.Equal(strike.GetProperty("shape").EnumerateArray().Select(v => v.GetDouble()), new[] { settings.FunnelHeight, settings.FunnelBase, settings.FunnelTop });
        Assert.Equal(strike.GetProperty("time").EnumerateArray().Select(v => v.GetDouble()), new[] { settings.StrikeGap[0], settings.StrikeGap[1], settings.StrikeBurst });
    }

    // The python constants golden.py replaced, read into the settings.
    private static TornadoSettings Apply(TornadoSettings s, JsonElement overrides)
    {
        foreach (var o in overrides.EnumerateObject())
        {
            string v = o.Value.GetString()!;
            double D() => double.Parse(v, CultureInfo.InvariantCulture);
            double[] Tuple(string t) => t.Trim('(', ')', ' ').Split(',').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            s = o.Name switch
            {
                "FUNNEL_HEIGHT" => s with { FunnelHeight = D() },
                "FUNNEL_TOP" => s with { FunnelTop = D() },
                "FUNNEL_SPIN" => s with { FunnelSpin = D() },
                "STRANDS" => s with { Strands = (int)D() },
                "STRAND_SPEED" => s with { StrandSpeed = D() },
                "CAP_SPIN" => s with { CapSpin = D() },
                "AROUND_LIFE" => s with { AroundLife = D() },
                "STRIKE_BURST" => s with { StrikeBurst = D() },
                "DARK" => s with { Dark = Tuple(v) },
                "RINGS" => s with
                {
                    Rings = v.Trim('[', ']').Split("),", StringSplitOptions.RemoveEmptyEntries).Select(Tuple)
                        .Select(t => new TornadoRing(t[0], t[1], (int)t[2], t[3])).ToArray(),
                },
                _ => throw new InvalidDataException("unknown constant " + o.Name),
            };
        }
        return s;
    }
}
