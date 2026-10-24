using System.Diagnostics;

namespace Pfx.Textures;

// Real-ESRGAN x4 per frame on the CPU (never GPU compute: Vulkan ESRGAN resets the owner's display driver), the way
// hires.py upscale() does it: the network sees the frame with its colour bled under alpha 0; brightness (Pillow's
// YCbCr Y) comes from the network, colour (Cb, Cr) from a Lanczos enlargement of the same frame, because ESRGAN shifts
// hues (dim olive turned saturated yellow, 2026-10-11); alpha, when the frames have any, goes through the network as
// grey; the result is resized to the frame size with Lanczos and its mean alpha and brightness matched to the source
// frame. The network runs in the user's esrgan_cpu.py through pfx_upscale.py; everything here is a path from settings.
public sealed class CpuUpscaler
{
    public const int Scale = 4;

    private readonly string _exe;
    private readonly IReadOnlyList<string> _args;
    private readonly string _bridge, _executor, _param, _bin;

    public CpuUpscaler(string pythonCommand, string bridgeScript, string executor, string modelParam)
    {
        (_exe, _args) = SplitCommand(pythonCommand);
        _bridge = bridgeScript;
        _executor = executor;
        _param = modelParam;
        _bin = Path.ChangeExtension(modelParam, ".bin");
        foreach (var file in new[] { bridgeScript, executor, modelParam, _bin })
            if (!File.Exists(file))
                throw new FileNotFoundException("Upscaler file not found", file);
    }

    // "py -3.12" → ("py", ["-3.12"]); a quoted first part may hold spaces.
    public static (string Exe, IReadOnlyList<string> Args) SplitCommand(string command)
    {
        command = command.Trim();
        if (command.Length == 0)
            throw new ArgumentException("No Python command");
        string exe, rest;
        if (command[0] == '"')
        {
            int end = command.IndexOf('"', 1);
            if (end < 0)
                throw new ArgumentException("Unclosed quote in the Python command");
            exe = command[1..end];
            rest = command[(end + 1)..];
        }
        else
        {
            int space = command.IndexOf(' ');
            exe = space < 0 ? command : command[..space];
            rest = space < 0 ? "" : command[space..];
        }
        return (exe, rest.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    public IReadOnlyList<RgbaImage> Upscale(IReadOnlyList<RgbaImage> frames, int width, int height, IProgress<(int Done, int Total)>? progress,
        CancellationToken cancel)
    {
        bool alpha = frames.Any(HasAlpha);
        string work = Path.Combine(Path.GetTempPath(), "pfx_upscale_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var items = new List<string[]>();
            var bled = new List<RgbaImage>();
            for (int i = 0; i < frames.Count; i++)
            {
                var rgb = Opaque(TextureTools.Bleed(frames[i]));
                bled.Add(rgb);
                items.Add(Item(work, $"c{i:D3}", rgb));
                if (alpha)
                    items.Add(Item(work, $"a{i:D3}", AlphaAsGrey(frames[i])));
            }
            string job = Path.Combine(work, "job.json");
            File.WriteAllText(job, System.Text.Json.JsonSerializer.Serialize(new { executor = _executor, param = _param, bin = _bin, items }));
            Run(job, work, items.Count, progress, cancel);
            var result = new List<RgbaImage>(frames.Count);
            for (int i = 0; i < frames.Count; i++)
            {
                var up = Load(work, $"c{i:D3}", frames[i]);
                var upAlpha = alpha ? Load(work, $"a{i:D3}", frames[i]) : null;
                result.Add(Combine(frames[i], bled[i], up, upAlpha, width, height));
            }
            return result;
        }
        finally
        {
            Directory.Delete(work, true);
        }
    }

    private static string[] Item(string work, string name, RgbaImage image)
    {
        string input = Path.Combine(work, name + ".png");
        File.WriteAllBytes(input, ImageFiles.WritePng(image));
        return new[] { input, Path.Combine(work, name + "_x4.png") };
    }

    private static RgbaImage Load(string work, string name, RgbaImage source)
    {
        var image = ImageFiles.ReadPng(File.ReadAllBytes(Path.Combine(work, name + "_x4.png")));
        if (image.Width != source.Width * Scale || image.Height != source.Height * Scale)
            throw new InvalidDataException($"The upscaler returned {image.Width}x{image.Height} for {source.Width}x{source.Height}");
        return image;
    }

    private void Run(string job, string work, int total, IProgress<(int Done, int Total)>? progress, CancellationToken cancel)
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
        start.ArgumentList.Add(_bridge);
        start.ArgumentList.Add(job);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Python did not start");
        var stderr = process.StandardError.ReadToEndAsync();
        string? error = null;
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
                if (line.StartsWith("ok ", StringComparison.Ordinal) && int.TryParse(line.AsSpan(3), out int done))
                    progress?.Report((done, total));
                else if (line.StartsWith("error ", StringComparison.Ordinal))
                    error = line[6..];
            }
            process.WaitForExit();
        }
        cancel.ThrowIfCancellationRequested();
        if (process.ExitCode != 0 || error != null)
            throw new InvalidOperationException(error ?? $"Python exit code {process.ExitCode}: {stderr.Result.Trim()}");
    }

    // One frame: brightness from the network, colour from Lanczos of the bled frame, then down to the frame size and
    // matched to the source's mean alpha and brightness (hires.py upscale + match_mean).
    public static RgbaImage Combine(RgbaImage source, RgbaImage bled, RgbaImage up, RgbaImage? upAlpha, int width, int height)
    {
        var colour = Resample.Lanczos(bled, up.Width, up.Height);
        var merged = new RgbaImage(up.Width, up.Height);
        var m = merged.Pixels;
        for (int i = 0; i < m.Length; i += 4)
        {
            double y = Y(up.Pixels, i);
            var (cb, cr) = CbCr(colour.Pixels, i);
            m[i] = ToByte(y + 1.402 * (cr - 128));
            m[i + 1] = ToByte(y - 0.344136 * (cb - 128) - 0.714136 * (cr - 128));
            m[i + 2] = ToByte(y + 1.772 * (cb - 128));
            m[i + 3] = 255;
        }
        var output = Resample.Lanczos(merged, width, height);
        if (upAlpha != null)
        {
            var a = Resample.Lanczos(upAlpha, width, height);
            for (int i = 3; i < output.Pixels.Length; i += 4)
                output.Pixels[i] = ToByte(Y(a.Pixels, i - 3));
        }
        return TextureTools.MatchMean(output, source);
    }

    private static bool HasAlpha(RgbaImage image)
    {
        for (int i = 3; i < image.Pixels.Length; i += 4)
            if (image.Pixels[i] < 255)
                return true;
        return false;
    }

    private static RgbaImage Opaque(RgbaImage image)
    {
        for (int i = 3; i < image.Pixels.Length; i += 4)
            image.Pixels[i] = 255;
        return image;
    }

    private static RgbaImage AlphaAsGrey(RgbaImage image)
    {
        var grey = new RgbaImage(image.Width, image.Height);
        for (int i = 0; i < grey.Pixels.Length; i += 4)
        {
            byte a = image.Pixels[i + 3];
            grey.Pixels[i] = grey.Pixels[i + 1] = grey.Pixels[i + 2] = a;
            grey.Pixels[i + 3] = 255;
        }
        return grey;
    }

    // ITU-R BT.601 full range, the YCbCr of Pillow (JPEG).
    private static double Y(byte[] p, int i) => 0.299 * p[i] + 0.587 * p[i + 1] + 0.114 * p[i + 2];

    private static (double Cb, double Cr) CbCr(byte[] p, int i) =>
        (128 - 0.168736 * p[i] - 0.331264 * p[i + 1] + 0.5 * p[i + 2], 128 + 0.5 * p[i] - 0.418688 * p[i + 1] - 0.081312 * p[i + 2]);

    private static byte ToByte(double v) => (byte)Math.Clamp(Math.Round(v, MidpointRounding.ToEven), 0, 255);
}
