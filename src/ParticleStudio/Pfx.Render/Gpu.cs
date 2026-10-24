using System.Reflection;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Pfx.Render;

// The D3D11 device, its immediate context and every device-bound object that does not depend on the scene: shaders,
// input layouts, states. A lost device is disposed whole and created again; only the shader bytecode survives
// (compiled once per process). The GPU only draws here: no compute shader, UAV or dispatch exists in this project.
public sealed class Gpu : IDisposable
{
    private static readonly Lazy<Dictionary<string, byte[]>> Bytecode = new(CompileAll);

    private static readonly (string Entry, string Profile)[] Entries =
    {
        ("VS", "vs_5_0"), ("PS", "ps_5_0"), ("MeshVS", "vs_5_0"), ("MeshPS", "ps_5_0"),
        ("LineVS", "vs_5_0"), ("LinePS", "ps_5_0"), ("OverlayVS", "vs_5_0"), ("OverlayPS", "ps_5_0"),
        ("SpriteVS", "vs_5_0"), ("SpritePS", "ps_5_0"),
    };

    public Gpu()
    {
        D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_0 }, out var device, out var context).CheckError();
        Device = device!;
        Context = context!;
        var code = Bytecode.Value;

        ParticleVS = Device.CreateVertexShader(code["VS"]);
        ParticlePS = Device.CreatePixelShader(code["PS"]);
        // One instance = ParticleInstance (44 bytes): position + age fraction, velocity + angle, half size + birth
        // fraction + frame.
        ParticleLayout = Device.CreateInputLayout(new[]
        {
            new InputElementDescription("POSITION", 0, Format.R32G32B32A32_Float, 0, 0, InputClassification.PerInstanceData, 1),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32B32A32_Float, 16, 0, InputClassification.PerInstanceData, 1),
            new InputElementDescription("TEXCOORD", 1, Format.R32G32B32_Float, 32, 0, InputClassification.PerInstanceData, 1),
        }, code["VS"]);

        MeshVS = Device.CreateVertexShader(code["MeshVS"]);
        MeshPS = Device.CreatePixelShader(code["MeshPS"]);
        MeshLayout = Device.CreateInputLayout(new[]
        {
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
            new InputElementDescription("COLOR", 0, Format.R32G32B32A32_Float, 24, 0),
        }, code["MeshVS"]);

        LineVS = Device.CreateVertexShader(code["LineVS"]);
        LinePS = Device.CreatePixelShader(code["LinePS"]);
        LineLayout = Device.CreateInputLayout(new[]
        {
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("COLOR", 0, Format.R32G32B32A32_Float, 12, 0),
        }, code["LineVS"]);

        OverlayVS = Device.CreateVertexShader(code["OverlayVS"]);
        OverlayPS = Device.CreatePixelShader(code["OverlayPS"]);
        OverlayLayout = Device.CreateInputLayout(new[]
        {
            new InputElementDescription("POSITION", 0, Format.R32G32_Float, 0, 0),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 8, 0),
            new InputElementDescription("COLOR", 0, Format.R32G32B32A32_Float, 16, 0),
        }, code["OverlayVS"]);

        SpriteVS = Device.CreateVertexShader(code["SpriteVS"]);
        SpritePS = Device.CreatePixelShader(code["SpritePS"]);
        SpriteLayout = Device.CreateInputLayout(new[]
        {
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("COLOR", 0, Format.R32G32B32A32_Float, 12, 0),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32B32_Float, 28, 0),
        }, code["SpriteVS"]);

        // R0: Additive_AlphaModulate = SrcAlpha/One, everything else SrcAlpha/InvSrcAlpha, colour and alpha alike.
        BlendAdditive = Device.CreateBlendState(new BlendDescription(Blend.SourceAlpha, Blend.One, Blend.SourceAlpha, Blend.One));
        BlendAlpha = Device.CreateBlendState(new BlendDescription(Blend.SourceAlpha, Blend.InverseSourceAlpha,
            Blend.SourceAlpha, Blend.InverseSourceAlpha));
        BlendOpaque = Device.CreateBlendState(BlendDescription.Opaque);
        // The overdraw view: every sprite pixel adds its colour, whatever its alpha (the cost is the pixel, not its look).
        BlendSum = Device.CreateBlendState(new BlendDescription(Blend.One, Blend.One, Blend.One, Blend.One));
        // Reverse Z as in the game (R0: GreaterEqual): opaque geometry tests and writes, particles test only, and
        // ZEnable 0 turns the test off.
        DepthWrite = Device.CreateDepthStencilState(DepthStencilDescription.DepthReverseZ);
        DepthTest = Device.CreateDepthStencilState(DepthStencilDescription.DepthReadReverseZ);
        DepthOff = Device.CreateDepthStencilState(DepthStencilDescription.None);
        NoCull = Device.CreateRasterizerState(RasterizerDescription.CullNone);
        LinearClamp = Device.CreateSamplerState(SamplerDescription.LinearClamp);
        LinearWrap = Device.CreateSamplerState(SamplerDescription.LinearWrap);
        PointClamp = Device.CreateSamplerState(SamplerDescription.PointClamp);
    }

    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public ID3D11VertexShader ParticleVS { get; }
    public ID3D11PixelShader ParticlePS { get; }
    public ID3D11InputLayout ParticleLayout { get; }
    public ID3D11VertexShader MeshVS { get; }
    public ID3D11PixelShader MeshPS { get; }
    public ID3D11InputLayout MeshLayout { get; }
    public ID3D11VertexShader LineVS { get; }
    public ID3D11PixelShader LinePS { get; }
    public ID3D11InputLayout LineLayout { get; }
    public ID3D11VertexShader OverlayVS { get; }
    public ID3D11PixelShader OverlayPS { get; }
    public ID3D11InputLayout OverlayLayout { get; }
    public ID3D11VertexShader SpriteVS { get; }
    public ID3D11PixelShader SpritePS { get; }
    public ID3D11InputLayout SpriteLayout { get; }
    public ID3D11BlendState BlendAdditive { get; }
    public ID3D11BlendState BlendAlpha { get; }
    public ID3D11BlendState BlendOpaque { get; }
    public ID3D11BlendState BlendSum { get; }
    public ID3D11DepthStencilState DepthWrite { get; }
    public ID3D11DepthStencilState DepthTest { get; }
    public ID3D11DepthStencilState DepthOff { get; }
    public ID3D11RasterizerState NoCull { get; }
    public ID3D11SamplerState LinearClamp { get; }
    public ID3D11SamplerState LinearWrap { get; }
    // The graph texture's sampler: the game's sssParamsTexture magnifies with Point (CG8 capture).
    public ID3D11SamplerState PointClamp { get; }

    // Compiled per entry point (VS, PS, MeshVS ...); compiling needs no device.
    public static IReadOnlyDictionary<string, byte[]> ShaderBytecode => Bytecode.Value;

    public IDXGIFactory2 Factory()
    {
        using var dxgi = Device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgi.GetAdapter();
        return adapter.GetParent<IDXGIFactory2>();
    }

    private static Dictionary<string, byte[]> CompileAll()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Viewport.hlsl")
                           ?? throw new InvalidOperationException("Viewport.hlsl is not embedded");
        string source = new StreamReader(stream).ReadToEnd();
        var result = new Dictionary<string, byte[]>();
        foreach (var (entry, profile) in Entries)
        {
            var hr = Compiler.Compile(source, entry, "Viewport.hlsl", profile, out var blob, out var errors);
            if (hr.Failure || blob == null)
            {
                string message = errors?.AsString() ?? hr.ToString();
                errors?.Dispose();
                throw new InvalidOperationException($"Shader {entry}: {message}");
            }
            result[entry] = blob.AsBytes();
            blob.Dispose();
            errors?.Dispose();
        }
        return result;
    }

    public void Dispose()
    {
        Context.ClearState();
        Context.Flush();
        foreach (var o in new IDisposable[]
                 {
                     ParticleVS, ParticlePS, ParticleLayout, MeshVS, MeshPS, MeshLayout, LineVS, LinePS, LineLayout,
                     OverlayVS, OverlayPS, OverlayLayout, SpriteVS, SpritePS, SpriteLayout, BlendAdditive, BlendAlpha, BlendOpaque, BlendSum, DepthWrite, DepthTest,
                     DepthOff, NoCull, LinearClamp, LinearWrap, PointClamp, Context, Device,
                 })
            o.Dispose();
    }
}
