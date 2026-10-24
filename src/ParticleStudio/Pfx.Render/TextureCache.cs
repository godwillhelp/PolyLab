using Pfx.Textures;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Pfx.Render;

// Decoded textures by file path. The decoded mips stay in memory for the session so a lost device rebuilds its
// textures without reading or decoding the files again; GPU views live per device (Views, cleared on device loss).
// Decoding is thread-safe: preview builds decode on their worker, so the first frame that draws an emitter only
// uploads (P6: decoding in that frame took ~140 ms on the UI thread for the tornado).
public sealed class TextureCache
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<RgbaImage[]?>> _decoded = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, ID3D11ShaderResourceView> Views { get; } = new(StringComparer.OrdinalIgnoreCase);

    // Every mip level the file stores, largest first; null when the file cannot be read.
    public RgbaImage[]? Decoded(string path) => _decoded.GetOrAdd(path, p => new Lazy<RgbaImage[]?>(() => TryDecode(p))).Value;

    private static RgbaImage[]? TryDecode(string path)
    {
        try
        {
            return Decode(path);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    private static RgbaImage[] Decode(string path)
    {
        var data = File.ReadAllBytes(path);
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".paa")
        {
            var paa = PaaFile.Read(data);
            return Enumerable.Range(0, paa.Levels.Count).Select(paa.Decode).ToArray();
        }
        if (ext is ".edds" or ".dds")
        {
            if (EddsFile.IsEdds(data))
            {
                var edds = EddsFile.Read(data);
                return Enumerable.Range(0, edds.Levels.Count).Select(edds.Decode).ToArray();
            }
            var dds = DdsFile.Read(data);
            return Enumerable.Range(0, dds.Levels.Count).Select(dds.Decode).ToArray();
        }
        return new[] { ImageFiles.Load(path) };
    }

    // Albedo maps are sampled as sRGB and normal maps as plain UNORM (R0: BC1_SRGB / BC3_SRGB / B8G8R8A8_SRGB albedo,
    // B8G8R8A8_UNORM normal map). Levels that do not halve exactly (a broken chain) end the mip chain there.
    public ID3D11ShaderResourceView? View(ID3D11Device device, string path, bool srgb)
    {
        string key = (srgb ? "s:" : "u:") + path;
        if (Views.TryGetValue(key, out var view))
            return view;
        var levels = Decoded(path);
        if (levels == null || levels.Length == 0)
            return null;
        int count = 1;
        while (count < levels.Length && levels[count].Width == Math.Max(1, levels[0].Width >> count)
               && levels[count].Height == Math.Max(1, levels[0].Height >> count))
            count++;
        var format = srgb ? Format.R8G8B8A8_UNorm_SRgb : Format.R8G8B8A8_UNorm;
        var pins = new System.Runtime.InteropServices.GCHandle[count];
        var init = new SubresourceData[count];
        try
        {
            for (int i = 0; i < count; i++)
            {
                pins[i] = System.Runtime.InteropServices.GCHandle.Alloc(levels[i].Pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
                init[i] = new SubresourceData(pins[i].AddrOfPinnedObject(), (uint)(levels[i].Width * 4), 0);
            }
            using var texture = device.CreateTexture2D(format, (uint)levels[0].Width, (uint)levels[0].Height, 1, (uint)count,
                init, BindFlags.ShaderResource, ResourceOptionFlags.None, ResourceUsage.Immutable);
            view = device.CreateShaderResourceView(texture);
        }
        finally
        {
            foreach (var pin in pins)
                if (pin.IsAllocated)
                    pin.Free();
        }
        Views[key] = view;
        return view;
    }

    // A file the tool rewrote: the next preview build decodes it again and the next frame uploads it again. UI thread
    // (the renderer reads Views there).
    public void Forget(string path)
    {
        _decoded.TryRemove(path, out _);
        foreach (string key in new[] { "s:" + path, "u:" + path })
            if (Views.Remove(key, out var view))
                view.Dispose();
    }

    public void ReleaseViews()
    {
        foreach (var v in Views.Values)
            v.Dispose();
        Views.Clear();
    }
}
