using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Pfx.Core.Model;
using Pfx.Core.Text;
using Pfx.Render;

namespace Pfx.App.Library;

// One effect file of the library: where it is, the source it was found under (its label) and the folder inside it.
public sealed record LibraryEntry(string Path, string Name, string Source, string Folder)
{
    public string Tag => Folder.Length > 0 ? Folder : Source;
}

// A folder the library reads, with the label shown on its chip.
public sealed record LibrarySource(string Label, string Folder);

// The effects under the library's folders (spec 3.10 library: vanilla, CG data, donors, own projects), found on a
// worker; nothing is read but file names until a row is opened or its thumbnail drawn.
public static class LibraryIndex
{
    public static List<LibraryEntry> Scan(IEnumerable<LibrarySource> sources, CancellationToken cancel)
    {
        var list = new List<LibraryEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sources)
        {
            if (!Directory.Exists(s.Folder))
                continue;
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(s.Folder, "*.ptc", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true });
                foreach (var f in files)
                {
                    cancel.ThrowIfCancellationRequested();
                    if (!seen.Add(System.IO.Path.GetFullPath(f)))
                        continue;
                    string rel = System.IO.Path.GetRelativePath(s.Folder, System.IO.Path.GetDirectoryName(f)!);
                    list.Add(new LibraryEntry(f, System.IO.Path.GetFileNameWithoutExtension(f), s.Label, rel == "." ? "" : rel.Replace('\\', '/')));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        return list;
    }

    // The emitters of an effect file as (name, text); a file that does not parse has none.
    public static List<(string Name, string Text)> Emitters(string path)
    {
        try
        {
            var doc = PtcDocument.Load(path);
            return doc.Emitters.Select(e => (e.Name, Pfx.App.Recipes.RecipeScene.Text(e.Block))).ToList();
        }
        catch (Exception e) when (e is IOException or EnfSyntaxException or InvalidDataException or UnauthorizedAccessException)
        {
            return new List<(string, string)>();
        }
    }

    // The thumbnail cache key: the file's path, size and write time (a changed file draws again).
    public static string ThumbKey(string path)
    {
        var info = new FileInfo(path);
        string key = System.IO.Path.GetFullPath(path).ToLowerInvariant() + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)), 0, 12).ToLowerInvariant();
    }
}

// A thumbnail's scene, prepared on a worker: the effect simulated to its fullest moment (most particles alive, sampled
// every quarter second over its whole look: every emitter's effect time plus its longest life, at most 30 s) and a
// camera that frames those particles.
public sealed class ThumbScene
{
    // A quarter second: finer than the eye picks a "fullest" frame from, coarse enough for 120 samples over 30 s.
    private const float SampleStep = 0.25f;
    private const float MaxLook = 30;

    public ThumbScene(EffectPreview preview, Camera camera)
    {
        Preview = preview;
        Camera = camera;
    }

    public EffectPreview Preview { get; }
    public Camera Camera { get; }

    public static ThumbScene? Build(string path, IReadOnlyList<string> roots, float aspect)
    {
        var preview = EffectPreview.Load(path, new DataRoots(roots));
        if (preview.Params.Count == 0)
            return null;
        float look = Math.Min(preview.Params.Max(p => (p.EffectTime > 0 ? p.EffectTime : 0) + p.LifeTime + p.LifeTimeRnd), MaxLook);
        float best = 0;
        int most = -1;
        for (float t = SampleStep; t <= Math.Max(look, SampleStep); t += SampleStep)
        {
            preview.Seek(t);
            int alive = preview.AliveTotal;
            if (alive > most)
            {
                most = alive;
                best = t;
            }
        }
        preview.Seek(best);
        preview.Playing = false;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var sim in preview.Sims)
            for (int i = 0; i < sim.Emitters.Count; i++)
                foreach (var r in sim.Render(i))
                {
                    var h = new Vector3(r.Size / 2);
                    min = Vector3.Min(min, r.Position - h);
                    max = Vector3.Max(max, r.Position + h);
                }
        if (most <= 0)
        {
            min = new Vector3(-1);
            max = new Vector3(1);
        }
        var centre = (min + max) / 2;
        float radius = Math.Max((max - min).Length() / 2, 0.2f);
        var camera = new Camera { Target = centre, Yaw = 30, Pitch = -15 };
        // Fits the sphere of the particles in the narrower of the two view angles.
        float vertical = camera.Fov * MathF.PI / 360f;
        float half = MathF.Min(vertical, MathF.Atan(MathF.Tan(vertical) * aspect));
        camera.Distance = radius / MathF.Sin(half);
        return new ThumbScene(preview, camera);
    }
}
