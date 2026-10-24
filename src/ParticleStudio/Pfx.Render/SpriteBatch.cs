using System.Numerics;

namespace Pfx.Render;

public struct SpriteVertex
{
    public Vector3 Position;
    public Vector4 Color;
    // u, v, and 1 when the draw ignores the pixel alpha (X-Ray particles/add).
    public Vector3 UvMode;
}

public enum SpriteBlend { Add, AlphaAdd, Blend }

// One run of sprites with one texture and one blend: Count vertices from Start (two triangles per sprite).
public readonly record struct SpriteDraw(string? Texture, SpriteBlend Blend, int Start, int Count);

// Camera-built quads the viewport draws after the particles (the X-Ray side of the A/B view). Filled each frame by
// ViewportHost.ComposeSprites; the renderer only uploads and draws it.
public sealed class SpriteBatch
{
    public List<SpriteVertex> Vertices { get; } = new();
    public List<SpriteDraw> Draws { get; } = new();

    public void Clear()
    {
        Vertices.Clear();
        Draws.Clear();
    }

    // Starts a run; quads added after it belong to it until the next Begin.
    public void Begin(string? texture, SpriteBlend blend)
    {
        Close();
        Draws.Add(new SpriteDraw(texture, blend, Vertices.Count, 0));
    }

    // Corners in the X-Ray order of FillSprite: d (left, bottom), a (left, top), c (right, bottom), b (right, top).
    public void Quad(Vector3 d, Vector3 a, Vector3 c, Vector3 b, Vector2 lt, Vector2 rb, Vector4 colour)
    {
        float mode = Draws.Count > 0 && Draws[^1].Blend == SpriteBlend.Add ? 1 : 0;
        SpriteVertex V(Vector3 p, float u, float v) => new() { Position = p, Color = colour, UvMode = new Vector3(u, v, mode) };
        Vertices.Add(V(d, lt.X, rb.Y));
        Vertices.Add(V(a, lt.X, lt.Y));
        Vertices.Add(V(c, rb.X, rb.Y));
        Vertices.Add(V(c, rb.X, rb.Y));
        Vertices.Add(V(a, lt.X, lt.Y));
        Vertices.Add(V(b, rb.X, lt.Y));
    }

    // A disc in the plane of `right` and `up` (their lengths are the radii), its colour going from `centre` to `rim`:
    // the soft glow marker of a light. Triangles of a fan; the texture is sampled at its centre.
    public void Fan(Vector3 at, Vector3 right, Vector3 up, Vector4 centre, Vector4 rim)
    {
        const int Segments = 32;
        float mode = Draws.Count > 0 && Draws[^1].Blend == SpriteBlend.Add ? 1 : 0;
        var uv = new Vector3(0.5f, 0.5f, mode);
        for (int i = 0; i < Segments; i++)
        {
            float a0 = i * MathF.Tau / Segments, a1 = (i + 1) * MathF.Tau / Segments;
            Vertices.Add(new SpriteVertex { Position = at, Color = centre, UvMode = uv });
            Vertices.Add(new SpriteVertex { Position = at + right * MathF.Cos(a0) + up * MathF.Sin(a0), Color = rim, UvMode = uv });
            Vertices.Add(new SpriteVertex { Position = at + right * MathF.Cos(a1) + up * MathF.Sin(a1), Color = rim, UvMode = uv });
        }
    }

    public void Close()
    {
        if (Draws.Count > 0 && Draws[^1] is var last && last.Count == 0)
            Draws[^1] = last with { Count = Vertices.Count - last.Start };
    }
}
