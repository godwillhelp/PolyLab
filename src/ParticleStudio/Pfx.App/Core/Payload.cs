namespace Pfx.App.Core;

// Files the tool needs as files beside itself: the live link's mod (the game loads it with -mod) and the upscale
// bridge script (Python runs it). They are embedded in the tool's assembly, so a single-file build carries them too,
// and written next to the tool when first needed or when the tool's copy differs.
public static class Payload
{
    public const string LinkPbo = "PolyLab_PfxLink.pbo";
    public const string Upscale = "pfx_upscale.py";

    public static string LinkMod => Path.Combine(AppContext.BaseDirectory, "link", "@PolyLab_PfxLink");

    private static readonly Dictionary<string, string> Places = new()
    {
        [LinkPbo] = Path.Combine("link", "@PolyLab_PfxLink", "addons", LinkPbo),
        [Upscale] = Path.Combine("Tools", Upscale),
    };

    // The file on disk; null when this build has no such resource and the file is not there either.
    public static string? Ensure(string name)
    {
        string target = Path.Combine(AppContext.BaseDirectory, Places[name]);
        using var stream = typeof(Payload).Assembly.GetManifestResourceStream(name);
        if (stream == null)
            return File.Exists(target) ? target : null;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        byte[] bytes = memory.ToArray();
        try
        {
            if (!File.Exists(target) || !File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                AtomicFile.Write(target, bytes);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return File.Exists(target) ? target : null;
        }
        return target;
    }
}
