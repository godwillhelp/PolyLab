using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Pfx.Sim;
using Pfx.Textures;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Pfx.Render;

// What the viewport draws besides the particles.
// What the particles show: their look, one flat colour per emitter in draw order (LayerColor), or how many sprites
// cover each pixel (OverdrawStep added per sprite pixel on a black screen).
public enum DrawView { Normal, Layers, Overdraw }

public sealed record ViewOptions
{
    public Sky Sky { get; init; } = Sky.Noon;
    public bool Ground { get; init; } = true;
    // The per-vertex fog of the particle shaders (the only atmosphere term the R0 captures show; the game's post
    // passes after the forward pass are not modelled).
    public bool Fog { get; init; } = true;
    public HelperModel Helper { get; init; }
    public bool Gizmos { get; init; }
    public DrawView Order { get; init; }
}

public sealed class FrameStats
{
    public double SimMs { get; set; }
    public double UploadMs { get; set; }
    // GPU time of the frame after the target clears, from timestamp queries (a few frames late; NaN until the first
    // result arrives).
    public double GpuMs { get; set; } = double.NaN;
    public int Instances { get; set; }
    public int Draws { get; set; }
}

// One emitter draw in back-to-front order.
public readonly record struct EmitterDraw(int Effect, int Copy, int Emitter, int Start, int Count, float Key, bool MaySwap);

[StructLayout(LayoutKind.Sequential)]
internal struct SceneConstants
{
    public Matrix4x4 ViewProj;
    public Vector3 EyePos;
    public float FarPlane;
    public Vector3 ViewRight;
    public float NearPlane;
    public Vector3 ViewUp;
    public float GroundY;
    public Vector3 ViewFwd;
    public float ParamsTexel;
    public Vector3 LightVec;
    public float FogOn;
    public Vector3 SunColor;
    public float Pad0;
    public Vector3 AmbientColor;
    public float Pad1;
    public Vector4 FogSun;
    public Vector4 FogSunBack;
    public Vector4 FogLerp;
    public Vector4 ScreenSize;
}

[StructLayout(LayoutKind.Sequential)]
internal struct EmitterConstants
{
    public Vector3 AxisX;
    public float Mode;
    public Vector3 AxisY;
    public float VelAngle;
    public Vector4 ScaleCenter;
    public Vector4 AnimTile;
    public Vector2 CamBlend;
    public float LightTerm;
    public float Stretch;
    public Vector4 L1;
    public Vector4 L2;
    public float Softness;
    public float DepthScale;
    public float Lit;
    public float Soft;
    public float ParamsRow;
    public float RowStep;
    public float FogBlack;
    public float View;
    public Vector4 ViewColor;
}

// Draws one frame of the previewed effects the way the R0 captures show the game drawing it: a linear depth pre-pass of
// the opaque scene, then one instanced draw per emitter, emitters back to front by the view depth of their origin,
// particles in birth order unless the emitter has Sort 1 (then back to front by view depth).
public sealed class Renderer : IDisposable
{
    // R0: emitters whose sort keys were 1-2 m apart swapped order between frames of a still scene; 5-8 m apart never.
    public const float MaySwapMetres = 2;
    private const int QuerySets = 4;
    // Linear colour one sprite pixel adds in the overdraw view: red fills at 8 layers, green at 22, blue at 67, so the
    // count reads as dark red -> orange -> yellow -> white.
    public static readonly Vector3 OverdrawStep = new(0.125f, 0.045f, 0.015f);

    private readonly Gpu _gpu;
    private readonly TextureCache _textures;
    private readonly ID3D11Buffer _sceneCb;
    private readonly ID3D11Buffer _emitterCb;
    private readonly ID3D11Buffer _quadIndices;
    private readonly DynamicBuffer<ParticleInstance> _instances;
    private readonly DynamicBuffer<MeshVertex> _meshes;
    private readonly DynamicBuffer<LineVertex> _lines;
    private readonly DynamicBuffer<OverlayVertex> _overlay;
    private readonly DynamicBuffer<SpriteVertex> _sprites;
    private readonly ID3D11ShaderResourceView _white;
    private readonly ID3D11ShaderResourceView? _glyphs;
    private readonly (ID3D11Query Disjoint, ID3D11Query Begin, ID3D11Query End)[] _queries;
    private readonly List<LineVertex> _lineList = new();
    private readonly List<LineVertex> _gizmoList = new();
    private ParticleInstance[] _cpuInstances = new ParticleInstance[1024];
    private float[] _sortKeys = new float[1024];
    private EffectPreview[] _paramsFor = Array.Empty<EffectPreview>();
    private int[] _rowBase = Array.Empty<int>();
    private int _rows = 1;
    private ID3D11ShaderResourceView? _params;
    private int _frame;
    private int _pending;

    public Renderer(Gpu gpu, TextureCache textures, GlyphAtlas? atlas)
    {
        _gpu = gpu;
        _textures = textures;
        var device = gpu.Device;
        _sceneCb = device.CreateBuffer((uint)Marshal.SizeOf<SceneConstants>(), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write);
        _emitterCb = device.CreateBuffer((uint)Marshal.SizeOf<EmitterConstants>(), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write);
        _quadIndices = device.CreateBuffer(new ushort[] { 0, 1, 2, 2, 1, 3 }, BindFlags.IndexBuffer, ResourceUsage.Immutable);
        _instances = new DynamicBuffer<ParticleInstance>(gpu, BindFlags.VertexBuffer, 4096);
        _meshes = new DynamicBuffer<MeshVertex>(gpu, BindFlags.VertexBuffer, 512);
        _lines = new DynamicBuffer<LineVertex>(gpu, BindFlags.VertexBuffer, 4096);
        _overlay = new DynamicBuffer<OverlayVertex>(gpu, BindFlags.VertexBuffer, 4096);
        _sprites = new DynamicBuffer<SpriteVertex>(gpu, BindFlags.VertexBuffer, 4096);
        _white = Texture(device, new byte[] { 255, 255, 255, 255 }, 1, 1, Format.R8G8B8A8_UNorm);
        if (atlas != null)
            _glyphs = Texture(device, atlas.Alpha, atlas.Width, atlas.Height, Format.R8_UNorm);
        _queries = Enumerable.Range(0, QuerySets).Select(_ => (device.CreateQuery(QueryType.TimestampDisjoint),
            device.CreateQuery(QueryType.Timestamp), device.CreateQuery(QueryType.Timestamp))).ToArray();
    }

    public FrameStats Stats { get; } = new();
    public IReadOnlyList<EmitterDraw> Draws => _draws;
    private readonly List<EmitterDraw> _draws = new();
    // Layer number of each emitter (effect, emitter) in copy 0's back-to-front order: stress copies take its colour.
    private readonly Dictionary<(int, int), int> _layers = new();

    private static ID3D11ShaderResourceView Texture(ID3D11Device device, byte[] pixels, int width, int height, Format format)
    {
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            int bpp = format == Format.R8_UNorm ? 1 : 4;
            var init = new[] { new SubresourceData(handle.AddrOfPinnedObject(), (uint)(width * bpp), 0) };
            using var texture = device.CreateTexture2D(format, (uint)width, (uint)height, 1, 1, init, BindFlags.ShaderResource,
                ResourceOptionFlags.None, ResourceUsage.Immutable);
            return device.CreateShaderResourceView(texture);
        }
        finally
        {
            handle.Free();
        }
    }

    private unsafe void Upload<T>(ID3D11Buffer buffer, in T value) where T : unmanaged
    {
        var map = _gpu.Context.Map(buffer, MapMode.WriteDiscard);
        *(T*)map.DataPointer = value;
        _gpu.Context.Unmap(buffer);
    }

    // toolLines: world-space lines of the active viewport tool (drawn over everything, like the gizmos); sprites: the
    // X-Ray side of the A/B view, drawn after the particles.
    public void Render(Targets t, Camera camera, IReadOnlyList<EffectPreview> previews, ViewOptions view, OverlayBuilder? overlay,
        IReadOnlyList<LineVertex>? toolLines = null, SpriteBatch? sprites = null)
    {
        var ctx = _gpu.Context;
        ReadTimings();
        // All sets still in flight: this frame reuses the oldest, so its result is given up.
        if (_pending == QuerySets)
            _pending--;
        var q = _queries[_frame % QuerySets];
        ctx.Begin(q.Disjoint);

        var (right, up, fwd) = camera.Basis();
        var eye = camera.Eye;
        var viewProj = camera.View() * camera.Projection(t.Width / (float)Math.Max(1, t.Height));
        var sky = view.Sky;
        var scene = new SceneConstants
        {
            ViewProj = Matrix4x4.Transpose(viewProj),
            EyePos = eye,
            FarPlane = Camera.FarPlane,
            ViewRight = right,
            NearPlane = Camera.NearPlane,
            ViewUp = up,
            GroundY = 0,
            ViewFwd = fwd,
            ParamsTexel = 1f / EffectPreview.ParamsWidth,
            LightVec = sky.LightVec,
            FogOn = view.Fog ? 1 : 0,
            SunColor = sky.SunColor,
            AmbientColor = sky.Ambient,
            FogSun = sky.FogSun,
            FogSunBack = sky.FogSunBack,
            FogLerp = sky.FogLerp,
            ScreenSize = new Vector4(t.Width, t.Height, 1f / Math.Max(1, t.Width), 1f / Math.Max(1, t.Height)),
        };
        Upload(_sceneCb, scene);

        ctx.ClearState();
        ctx.RSSetViewport(0, 0, t.Width, t.Height);
        ctx.RSSetState(_gpu.NoCull);
        ctx.ClearRenderTargetView(t.ColourView, new Vortice.Mathematics.Color4(sky.Background.X, sky.Background.Y, sky.Background.Z, 1));
        ctx.ClearRenderTargetView(t.LinearDepthTarget, new Vortice.Mathematics.Color4(Camera.FarPlane, 0, 0, 0));
        ctx.ClearDepthStencilView(t.DepthView, DepthStencilClearFlags.Depth, 0, 0);
        // The first write to the back buffer waits until the display has released it (vsync); the GPU time starts
        // after the clears so that wait is not counted.
        ctx.End(q.Begin);
        ctx.VSSetConstantBuffer(0, _sceneCb);
        ctx.PSSetConstantBuffer(0, _sceneCb);
        ctx.VSSetSampler(0, _gpu.PointClamp);
        ctx.PSSetSampler(0, _gpu.LinearClamp);
        ctx.PSSetSampler(1, _gpu.LinearWrap);

        DrawOpaque(t, previews, view);
        if (view.Order == DrawView.Overdraw)
            ctx.ClearRenderTargetView(t.ColourView, new Vortice.Mathematics.Color4(0, 0, 0, 1));
        else
            DrawGrid(t, camera, view);
        DrawParticles(t, eye, fwd, previews, view);
        if (sprites != null)
            DrawSprites(t, sprites);
        DrawGizmos(t, previews, view, toolLines);
        if (overlay != null)
            DrawOverlay(t, overlay);

        ctx.End(q.End);
        ctx.End(q.Disjoint);
        _frame++;
        _pending++;
    }

    private void ReadTimings()
    {
        var ctx = _gpu.Context;
        // The oldest set still pending; results stay unread (not waited for) until the GPU has them.
        while (_pending > 0)
        {
            var q = _queries[(_frame - _pending + QuerySets) % QuerySets];
            if (!ctx.IsDataAvailable(q.Disjoint, AsyncGetDataFlags.DoNotFlush))
                return;
            var disjoint = ctx.GetData<QueryDataTimestampDisjoint>(q.Disjoint, AsyncGetDataFlags.DoNotFlush);
            ulong begin = ctx.GetData<ulong>(q.Begin, AsyncGetDataFlags.DoNotFlush);
            ulong end = ctx.GetData<ulong>(q.End, AsyncGetDataFlags.DoNotFlush);
            if (!disjoint.Disjoint && disjoint.Frequency > 0 && end >= begin)
                Stats.GpuMs = (end - begin) * 1000.0 / disjoint.Frequency;
            _pending--;
        }
    }

    private void DrawOpaque(Targets t, IReadOnlyList<EffectPreview> previews, ViewOptions view)
    {
        var verts = new List<MeshVertex>();
        if (view.Ground)
            verts.AddRange(SceneGeometry.Ground(view.Sky.Ground));
        if (view.Helper != HelperModel.None)
        {
            // On the ground 2 m east of the source.
            var at = previews.Count > 0 ? previews[0].Settings.Origin : Vector3.Zero;
            verts.AddRange(SceneGeometry.Helper(view.Helper, new Vector3(at.X + 2, 0, at.Z)));
        }
        var ctx = _gpu.Context;
        ctx.OMSetRenderTargets(new[] { t.ColourView, t.LinearDepthTarget }, t.DepthView);
        if (verts.Count == 0)
            return;
        _meshes.Write(CollectionsMarshal.AsSpan(verts));
        ctx.IASetInputLayout(_gpu.MeshLayout);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.IASetVertexBuffer(0, _meshes.Buffer, DynamicBuffer<MeshVertex>.Stride);
        ctx.VSSetShader(_gpu.MeshVS);
        ctx.PSSetShader(_gpu.MeshPS);
        ctx.OMSetBlendState(_gpu.BlendOpaque);
        ctx.OMSetDepthStencilState(_gpu.DepthWrite);
        ctx.Draw((uint)verts.Count, 0);
    }

    private void DrawLines(Targets t, List<LineVertex> lines, ID3D11DepthStencilState depth)
    {
        if (lines.Count == 0)
            return;
        var ctx = _gpu.Context;
        _lines.Write(CollectionsMarshal.AsSpan(lines));
        ctx.OMSetRenderTargets(t.ColourView, t.DepthView);
        ctx.IASetInputLayout(_gpu.LineLayout);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.LineList);
        ctx.IASetVertexBuffer(0, _lines.Buffer, DynamicBuffer<LineVertex>.Stride);
        ctx.VSSetShader(_gpu.LineVS);
        ctx.PSSetShader(_gpu.LinePS);
        ctx.OMSetBlendState(_gpu.BlendAlpha);
        ctx.OMSetDepthStencilState(depth);
        ctx.Draw((uint)lines.Count, 0);
    }

    private void DrawGrid(Targets t, Camera camera, ViewOptions view)
    {
        _lineList.Clear();
        if (!view.Ground)
            return;
        // 1 m lines out to twice the view distance (at least 10 m), 10 m lines ten times further, within the ground.
        float minor = Math.Clamp(camera.Distance * 2, 10, 60);
        float major = Math.Min(minor * 10, SceneGeometry.GroundHalf);
        SceneGeometry.Grid(_lineList, new Vector3(camera.Target.X, 0, camera.Target.Z), minor, major);
        DrawLines(t, _lineList, _gpu.DepthTest);
    }

    private void DrawGizmos(Targets t, IReadOnlyList<EffectPreview> previews, ViewOptions view, IReadOnlyList<LineVertex>? toolLines)
    {
        _gizmoList.Clear();
        foreach (var preview in previews)
            AddGizmos(preview, view);
        if (toolLines != null)
            _gizmoList.AddRange(toolLines);
        DrawLines(t, _gizmoList, _gpu.DepthOff);
    }

    private void AddGizmos(EffectPreview preview, ViewOptions view)
    {
        if (preview.Settings.Motion == MotionKind.Circle)
        {
            var s = preview.Settings;
            SceneGeometry.Circle(_gizmoList, new SourceMotion.Circle(s.Origin, s.CircleRadius, s.CircleSpeed, s.CircleAxis), Palette.Path);
        }
        if (view.Gizmos)
        {
            for (int e = 0; e < preview.Params.Count; e++)
            {
                var p = preview.Params[e];
                var origin = preview.EmitterOrigin(0, e);
                var axes = preview.EmitterAxes(0, e);
                SceneGeometry.Cone(_gizmoList, origin, axes, p.ConeAngle, 1, Palette.Accent);
                SceneGeometry.Shape(_gizmoList, origin, axes, p, Palette.CurveA);
            }
        }
    }

    private void DrawParticles(Targets t, Vector3 eye, Vector3 fwd, IReadOnlyList<EffectPreview> previews, ViewOptions view)
    {
        _draws.Clear();
        Stats.Instances = 0;
        Stats.Draws = 0;
        if (previews.Count == 0)
            return;
        var clock = Stopwatch.StartNew();
        EnsureParams(previews);

        int total = previews.Sum(p => p.AliveTotal);
        if (_cpuInstances.Length < total)
        {
            int c = _cpuInstances.Length;
            while (c < total)
                c *= 2;
            _cpuInstances = new ParticleInstance[c];
            _sortKeys = new float[c];
        }
        int at = 0;
        for (int effect = 0; effect < previews.Count; effect++)
        {
            var preview = previews[effect];
            for (int copy = 0; copy < preview.Sims.Count; copy++)
            {
                var sim = preview.Sims[copy];
                for (int e = 0; e < sim.Emitters.Count; e++)
                {
                    int count = sim.Emitters[e].Count;
                    if (count == 0 || preview.Materials[e].DrawsNothing)
                        continue;
                    int n = sim.WriteInstances(e, _cpuInstances.AsSpan(at, count));
                    if (preview.SortParticles[e])
                    {
                        // Sort 1: back to front by view depth (R0, hotspring_watervapor); the key is negated because
                        // Array.Sort orders ascending.
                        for (int i = 0; i < n; i++)
                            _sortKeys[at + i] = -Vector3.Dot(_cpuInstances[at + i].Position - eye, fwd);
                        Array.Sort(_sortKeys, _cpuInstances, at, n);
                    }
                    float key = Vector3.Dot(preview.EmitterOrigin(copy, e) - eye, fwd);
                    _draws.Add(new EmitterDraw(effect, copy, e, at, n, key, false));
                    at += n;
                }
            }
        }
        _draws.Sort((a, b) => b.Key.CompareTo(a.Key));
        for (int i = 0; i < _draws.Count; i++)
        {
            bool close = (i > 0 && _draws[i - 1].Key - _draws[i].Key < MaySwapMetres)
                         || (i + 1 < _draws.Count && _draws[i].Key - _draws[i + 1].Key < MaySwapMetres);
            _draws[i] = _draws[i] with { MaySwap = close };
        }
        _instances.Write(_cpuInstances.AsSpan(0, at));
        Stats.UploadMs = clock.Elapsed.TotalMilliseconds;
        Stats.Instances = at;
        Stats.Draws = _draws.Count;
        if (at == 0)
            return;

        var ctx = _gpu.Context;
        ctx.OMSetRenderTargets(t.ColourView, t.DepthView);
        ctx.IASetInputLayout(_gpu.ParticleLayout);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.IASetVertexBuffer(0, _instances.Buffer, DynamicBuffer<ParticleInstance>.Stride);
        ctx.IASetIndexBuffer(_quadIndices, Format.R16_UInt, 0);
        ctx.VSSetShader(_gpu.ParticleVS);
        ctx.PSSetShader(_gpu.ParticlePS);
        ctx.VSSetShaderResource(0, _params);
        ctx.VSSetConstantBuffer(1, _emitterCb);
        ctx.PSSetConstantBuffer(1, _emitterCb);
        ctx.PSSetShaderResource(3, t.LinearDepthView);
        var device = _gpu.Device;
        _layers.Clear();
        foreach (var d in _draws)
            if (d.Copy == 0)
                _layers[(d.Effect, d.Emitter)] = _layers.Count;
        foreach (var d in _draws)
        {
            var preview = previews[d.Effect];
            var m = preview.Materials[d.Emitter];
            var p = preview.Params[d.Emitter];
            var albedo = preview.AlbedoPaths[d.Emitter] is { } ap ? _textures.View(device, ap, true) : null;
            var normal = preview.NormalPaths[d.Emitter] is { } np ? _textures.View(device, np, false) : null;
            var c = Constants(preview, d, m, p, normal != null, view.Sky, _rowBase[d.Effect], _rows);
            c.View = (float)view.Order;
            c.ViewColor = view.Order switch
            {
                DrawView.Layers => new Vector4(LayerLinear(_layers.GetValueOrDefault((d.Effect, d.Emitter))), 1),
                DrawView.Overdraw => new Vector4(OverdrawStep, 1),
                _ => Vector4.Zero,
            };
            Upload(_emitterCb, c);
            ctx.PSSetShaderResource(1, albedo ?? _white);
            ctx.PSSetShaderResource(2, normal ?? _white);
            ctx.OMSetBlendState(view.Order switch
            {
                DrawView.Overdraw => _gpu.BlendSum,
                DrawView.Layers => _gpu.BlendAlpha,
                _ => m.Additive ? _gpu.BlendAdditive : _gpu.BlendAlpha,
            });
            ctx.OMSetDepthStencilState(m.ZEnable ? _gpu.DepthTest : _gpu.DepthOff);
            ctx.DrawIndexedInstanced(6, (uint)d.Count, 0, 0, (uint)d.Start);
        }
        ctx.PSSetShaderResource(3, null!);
    }

    private void DrawSprites(Targets t, SpriteBatch batch)
    {
        batch.Close();
        if (batch.Vertices.Count == 0)
            return;
        var ctx = _gpu.Context;
        _sprites.Write(CollectionsMarshal.AsSpan(batch.Vertices));
        ctx.OMSetRenderTargets(t.ColourView, t.DepthView);
        ctx.IASetInputLayout(_gpu.SpriteLayout);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.IASetVertexBuffer(0, _sprites.Buffer, DynamicBuffer<SpriteVertex>.Stride);
        ctx.VSSetShader(_gpu.SpriteVS);
        ctx.PSSetShader(_gpu.SpritePS);
        ctx.PSSetShaderResource(3, t.LinearDepthView);
        ctx.OMSetDepthStencilState(_gpu.DepthTest);
        var device = _gpu.Device;
        foreach (var d in batch.Draws)
        {
            if (d.Count == 0)
                continue;
            var albedo = d.Texture != null ? _textures.View(device, d.Texture, true) : null;
            ctx.PSSetShaderResource(1, albedo ?? _white);
            ctx.OMSetBlendState(d.Blend == SpriteBlend.Blend ? _gpu.BlendAlpha : _gpu.BlendAdditive);
            ctx.Draw((uint)d.Count, (uint)d.Start);
        }
        ctx.PSSetShaderResource(3, null!);
    }

    // The colour of the n-th emitter layer (copy 0, back to front) in the layers view, 8-bit sRGB: hues a golden angle
    // apart, so neighbours in the order never look alike; the draw-order list shows the same swatch.
    public static (int R, int G, int B) LayerColor(int n)
    {
        double hue = n * 0.381966 % 1.0 * 6;
        int sector = (int)hue;
        double f = hue - sector;
        const double v = 0.95, s = 0.65;
        double p = v * (1 - s), q = v * (1 - s * f), r = v * (1 - s * (1 - f));
        var (x, y, z) = sector switch
        {
            0 => (v, r, p),
            1 => (q, v, p),
            2 => (p, v, r),
            3 => (p, q, v),
            4 => (r, p, v),
            _ => (v, p, q),
        };
        return ((int)Math.Round(x * 255), (int)Math.Round(y * 255), (int)Math.Round(z * 255));
    }

    private static Vector3 LayerLinear(int n)
    {
        var (r, g, b) = LayerColor(n);
        return Sky.Linear(r, g, b);
    }

    // What the shaders receive per emitter of copy 0 (the --report table of PfxView).
    public static string Describe(EffectPreview preview, int emitter, Sky sky)
    {
        var m = preview.Materials[emitter];
        var p = preview.Params[emitter];
        var c = Constants(preview, new EmitterDraw(0, 0, emitter, 0, 0, 0, false), m, p, preview.NormalPaths[emitter] != null, sky, 0, preview.ParamsRows);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string V(Vector4 v) => string.Format(inv, "{0:0.###} {1:0.###} {2:0.###} | {3:0.###}", v.X, v.Y, v.Z, v.W);
        return string.Format(inv, "{0}\tmaterial {1} ({2}, found {3})\talbedo {4}\tnormal {5}\tL1 {6}\tL2 {7}\tmode {8} soft {9} additive {10} zenable {11} tiles {12}x{13} camblend {14:0.###} {15:0.###}",
            p.Name, p.Material, m.ClassName, m.Found, preview.AlbedoPaths[emitter] ?? "-", preview.NormalPaths[emitter] ?? "-",
            V(c.L1), V(c.L2), c.Mode, c.Soft, m.Additive, m.ZEnable, m.TileU, m.TileV, c.CamBlend.X, c.CamBlend.Y);
    }

    private static EmitterConstants Constants(EffectPreview preview, EmitterDraw d, MaterialInfo m, EmitterParams p, bool lit, Sky sky,
        int rowBase, int rows)
    {
        var axes = preview.EmitterAxes(d.Copy, d.Emitter);
        int u = m.Found ? m.TileU : p.TileU, v = m.Found ? m.TileV : p.TileV;
        // R0 vDistBlend: x = 1 / CameraBlendFar, y = -(near plane + CameraBlendNear) x (Far 1.5 -> 0.6667, -0.04667;
        // Near 0.001 Far 0.001 -> 1000, -71). The engine range of CameraBlendFar starts at 0.001.
        float far = Math.Max(m.CameraBlendFar, 0.001f);
        float b = m.BidirLightScale;
        float h = 1f / rows;
        return new EmitterConstants
        {
            AxisX = new Vector3(axes.M11, axes.M12, axes.M13),
            Mode = preview.BillboardModes[d.Emitter],
            AxisY = new Vector3(axes.M21, axes.M22, axes.M23),
            VelAngle = p.VelAngle ? 1 : 0,
            ScaleCenter = preview.Scales[d.Emitter],
            AnimTile = new Vector4(1f / u, 1f / v, u, u * v),
            CamBlend = new Vector2(1f / far, -(Camera.NearPlane + m.CameraBlendNear) / far),
            LightTerm = 1 - m.LightScale,
            Stretch = p.StretchMultiplier > 1 ? p.StretchMultiplier - 1 : 0,
            // R0: cDiffuseL1 = sun colour x emat Color (w = BidirLightScale), cAmbientEmissiveL2 = scene ambient x
            // Ambient + Emissive (w = 1 - BidirLightScale); Diffuse does not appear in any captured constant.
            L1 = new Vector4(sky.SunColor * m.Color, b),
            L2 = new Vector4(sky.Ambient * m.Ambient + m.Emissive, 1 - b),
            Softness = m.Softness,
            DepthScale = m.DepthScale,
            Lit = lit ? 1 : 0,
            Soft = m.Softness > 0 ? 1 : 0,
            ParamsRow = (rowBase + d.Emitter * EffectPreview.RowsPerEmitter + 0.5f) * h,
            RowStep = h,
            // R0: additive materials get a zero fog colour (they fade out instead of turning grey).
            FogBlack = m.Additive ? 1 : 0,
        };
    }

    // One graph texture for all previewed effects, their rows one after another.
    private void EnsureParams(IReadOnlyList<EffectPreview> previews)
    {
        if (_params != null && _paramsFor.SequenceEqual(previews))
            return;
        _params?.Dispose();
        _paramsFor = previews.ToArray();
        _rowBase = new int[previews.Count];
        _rows = 0;
        for (int i = 0; i < previews.Count; i++)
        {
            _rowBase[i] = _rows;
            _rows += previews[i].ParamsRows;
        }
        var data = new byte[EffectPreview.ParamsWidth * _rows * 4];
        for (int i = 0; i < previews.Count; i++)
            previews[i].ParamsTexture.CopyTo(data, _rowBase[i] * EffectPreview.ParamsWidth * 4);
        _params = Texture(_gpu.Device, data, EffectPreview.ParamsWidth, _rows, Format.R8G8B8A8_UNorm);
    }

    private void DrawOverlay(Targets t, OverlayBuilder overlay)
    {
        if (_glyphs == null || overlay.Vertices.Count == 0)
            return;
        var ctx = _gpu.Context;
        _overlay.Write(CollectionsMarshal.AsSpan(overlay.Vertices));
        ctx.OMSetRenderTargets(t.ColourView);
        ctx.IASetInputLayout(_gpu.OverlayLayout);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.IASetVertexBuffer(0, _overlay.Buffer, DynamicBuffer<OverlayVertex>.Stride);
        ctx.VSSetShader(_gpu.OverlayVS);
        ctx.PSSetShader(_gpu.OverlayPS);
        ctx.PSSetShaderResource(0, _glyphs);
        ctx.OMSetBlendState(_gpu.BlendAlpha);
        ctx.OMSetDepthStencilState(_gpu.DepthOff);
        ctx.Draw((uint)overlay.Vertices.Count, 0);
    }

    // Wall time per frame of `frames` renders of the same scene back to back, waited for with an event query at the end
    // (benchmarks only: it blocks). Each render also writes and uploads the instances, so this is the larger of the
    // CPU submit time and the GPU time per frame: an upper bound of the GPU time, where the timestamps (which this
    // driver reports as 0.000 ms for light frames) give a lower one.
    public double BackToBackMs(Targets t, Camera camera, IReadOnlyList<EffectPreview> previews, ViewOptions view, int frames)
    {
        var ctx = _gpu.Context;
        using var done = _gpu.Device.CreateQuery(QueryType.Event);
        void Wait()
        {
            ctx.End(done);
            while (!ctx.IsDataAvailable(done))
                Thread.SpinWait(64);
        }
        Render(t, camera, previews, view, null);
        Wait();
        var clock = Stopwatch.StartNew();
        for (int i = 0; i < frames; i++)
            Render(t, camera, previews, view, null);
        Wait();
        return clock.Elapsed.TotalMilliseconds / frames;
    }

    // The finished frame as RGBA pixels (screenshots, the device-loss check, the game comparison).
    public RgbaImage Capture(Targets t)
    {
        var device = _gpu.Device;
        using var staging = device.CreateTexture2D(Format.B8G8R8A8_UNorm, (uint)t.Width, (uint)t.Height, 1, 1, null,
            BindFlags.None, ResourceOptionFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read);
        _gpu.Context.CopyResource(staging, t.Colour);
        var map = _gpu.Context.Map(staging, 0, MapMode.Read);
        var image = new RgbaImage(t.Width, t.Height);
        try
        {
            for (int y = 0; y < t.Height; y++)
            {
                Marshal.Copy(map.DataPointer + y * (int)map.RowPitch, image.Pixels, y * t.Width * 4, t.Width * 4);
            }
        }
        finally
        {
            _gpu.Context.Unmap(staging, 0);
        }
        // BGRA to RGBA. Alpha is set opaque: blending leaves the target's alpha below 1 under particles, and the swap
        // chain ignores it (AlphaMode.Ignore), so the screen shows these colours as opaque.
        var p = image.Pixels;
        for (int i = 0; i < p.Length; i += 4)
        {
            (p[i], p[i + 2]) = (p[i + 2], p[i]);
            p[i + 3] = 255;
        }
        return image;
    }

    public void Dispose()
    {
        foreach (var q in _queries)
        {
            q.Disjoint.Dispose();
            q.Begin.Dispose();
            q.End.Dispose();
        }
        _params?.Dispose();
        _glyphs?.Dispose();
        _white.Dispose();
        _overlay.Dispose();
        _sprites.Dispose();
        _lines.Dispose();
        _meshes.Dispose();
        _instances.Dispose();
        _quadIndices.Dispose();
        _emitterCb.Dispose();
        _sceneCb.Dispose();
        _textures.ReleaseViews();
    }
}
