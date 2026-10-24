using System.Diagnostics;

namespace Pfx.Textures;

// DXT encoding through Microsoft's texconv.exe (DirectXTex), one call for all levels, each level encoded as given
// (-m 1), uniform colour weighting (-bc u), as hires.py encode(). -nogpu keeps it on the CPU: no GPU compute on the
// owner's machine. The exe path comes from the user's settings.
public sealed class Texconv
{
    public Texconv(string exePath)
    {
        if (!File.Exists(exePath))
            throw new FileNotFoundException("texconv.exe not found", exePath);
        ExePath = exePath;
    }

    public string ExePath { get; }

    public IReadOnlyList<byte[]> Encode(IReadOnlyList<RgbaImage> levels, EddsFormat format)
    {
        var (dxgi, block) = format switch
        {
            EddsFormat.Dxt1 => ("BC1_UNORM", BlockFormat.Dxt1),
            EddsFormat.Dxt5 => ("BC3_UNORM", BlockFormat.Dxt5),
            _ => throw new ArgumentException($"texconv is used for DXT only, not {format}"),
        };
        string work = Path.Combine(Path.GetTempPath(), "pfx_texconv_" + Guid.NewGuid().ToString("N"));
        string input = Path.Combine(work, "in"), output = Path.Combine(work, "out");
        Directory.CreateDirectory(input);
        Directory.CreateDirectory(output);
        try
        {
            var start = new ProcessStartInfo(ExePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var arg in new[] { "-f", dxgi, "-m", "1", "-bc", "u", "-nogpu", "-y", "-nologo", "-o", output })
                start.ArgumentList.Add(arg);
            for (int i = 0; i < levels.Count; i++)
            {
                var level = format == EddsFormat.Dxt1 ? Opaque(levels[i]) : levels[i];
                string path = Path.Combine(input, $"{i:D2}.dds");
                File.WriteAllBytes(path, DdsFile.WritePlainBgra(level));
                start.ArgumentList.Add(path);
            }
            using var process = Process.Start(start) ?? throw new InvalidOperationException("texconv did not start");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"texconv exit code {process.ExitCode}: {stdout.Result}{stderr.Result}");
            var result = new List<byte[]>(levels.Count);
            for (int i = 0; i < levels.Count; i++)
            {
                var dds = DdsFile.Read(File.ReadAllBytes(Path.Combine(output, $"{i:D2}.dds")));
                if (dds.Header.Block != block || dds.Header.Width != levels[i].Width || dds.Header.Height != levels[i].Height)
                    throw new InvalidDataException($"texconv wrote {dds.Header.FormatName} {dds.Header.Width}x{dds.Header.Height} for level {i}, " +
                                                   $"wanted {block} {levels[i].Width}x{levels[i].Height}");
                result.Add(dds.Levels[0]);
            }
            return result;
        }
        finally
        {
            Directory.Delete(work, true);
        }
    }

    private static RgbaImage Opaque(RgbaImage image)
    {
        var output = image.Clone();
        for (int i = 3; i < output.Pixels.Length; i += 4)
            output.Pixels[i] = 255;
        return output;
    }
}
