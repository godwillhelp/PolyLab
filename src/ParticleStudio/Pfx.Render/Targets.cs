using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Pfx.Render;

// The size-dependent views of one frame: the colour target (swap-chain back buffer or an offscreen texture, 8-bit
// BGRA drawn through an sRGB view like the game's swap chain, R0), the depth buffer and the linear depth that soft
// particles read (the game's R32F depth pre-pass).
public sealed class Targets : IDisposable
{
    public Targets(Gpu gpu, ID3D11Texture2D colour)
    {
        var desc = colour.Description;
        Width = (int)desc.Width;
        Height = (int)desc.Height;
        Colour = colour;
        ColourView = gpu.Device.CreateRenderTargetView(colour,
            new RenderTargetViewDescription(RenderTargetViewDimension.Texture2D, Format.B8G8R8A8_UNorm_SRgb));
        using (var depth = gpu.Device.CreateTexture2D(Format.D32_Float, desc.Width, desc.Height, 1, 1, null, BindFlags.DepthStencil))
            DepthView = gpu.Device.CreateDepthStencilView(depth);
        LinearDepth = gpu.Device.CreateTexture2D(Format.R32_Float, desc.Width, desc.Height, 1, 1, null,
            BindFlags.RenderTarget | BindFlags.ShaderResource);
        LinearDepthTarget = gpu.Device.CreateRenderTargetView(LinearDepth);
        LinearDepthView = gpu.Device.CreateShaderResourceView(LinearDepth);
    }

    // An offscreen colour texture of the given size (screenshots, the game comparison, benchmarks). Typeless, because an
    // sRGB view of a plain UNORM texture is allowed only for flip-model back buffers.
    public static Targets Offscreen(Gpu gpu, int width, int height) =>
        new(gpu, gpu.Device.CreateTexture2D(Format.B8G8R8A8_Typeless, (uint)width, (uint)height, 1, 1, null,
            BindFlags.RenderTarget | BindFlags.ShaderResource));

    public int Width { get; }
    public int Height { get; }
    public ID3D11Texture2D Colour { get; }
    public ID3D11RenderTargetView ColourView { get; }
    public ID3D11DepthStencilView DepthView { get; }
    public ID3D11Texture2D LinearDepth { get; }
    public ID3D11RenderTargetView LinearDepthTarget { get; }
    public ID3D11ShaderResourceView LinearDepthView { get; }

    public void Dispose()
    {
        LinearDepthView.Dispose();
        LinearDepthTarget.Dispose();
        LinearDepth.Dispose();
        DepthView.Dispose();
        ColourView.Dispose();
        Colour.Dispose();
    }
}

// A dynamic GPU buffer refilled every frame (instances, lines, overlay quads); grows by doubling.
public sealed class DynamicBuffer<T> : IDisposable where T : unmanaged
{
    private readonly Gpu _gpu;
    private readonly BindFlags _bind;
    private int _capacity;

    public DynamicBuffer(Gpu gpu, BindFlags bind, int capacity)
    {
        _gpu = gpu;
        _bind = bind;
        Allocate(Math.Max(1, capacity));
    }

    public ID3D11Buffer Buffer { get; private set; } = null!;
    public static uint Stride => (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<T>();

    private void Allocate(int capacity)
    {
        Buffer?.Dispose();
        _capacity = capacity;
        Buffer = _gpu.Device.CreateBuffer((uint)(capacity * (int)Stride), _bind, ResourceUsage.Dynamic, CpuAccessFlags.Write);
    }

    public unsafe void Write(ReadOnlySpan<T> data)
    {
        if (data.Length == 0)
            return;
        if (data.Length > _capacity)
        {
            int c = _capacity;
            while (c < data.Length)
                c *= 2;
            Allocate(c);
        }
        var map = _gpu.Context.Map(Buffer, MapMode.WriteDiscard);
        data.CopyTo(new Span<T>((void*)map.DataPointer, data.Length));
        _gpu.Context.Unmap(Buffer);
    }

    public void Dispose() => Buffer.Dispose();
}
