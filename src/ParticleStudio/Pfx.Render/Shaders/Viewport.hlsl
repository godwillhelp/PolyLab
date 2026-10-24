// Particles: port of the DayZ 1.29 particle shaders captured with RenderDoc in R0 (vs_1422
// unlit, vs_1424 soft, vs_1428 lit; ps_1430 unlit, ps_1434 soft, ps_1442 lit + soft). Additions that the capture did
// not contain are marked "not in R0". Meshes, lines and the overlay are the viewport's own scene, not the game's.

cbuffer SceneData : register(b0)
{
    float4x4 ViewProj;
    float3 EyePos;      float FarPlane;
    float3 ViewRight;   float NearPlane;
    float3 ViewUp;      float GroundY;
    float3 ViewFwd;     float ParamsTexel;
    float3 LightVec;    float FogOn;
    float3 SunColor;    float ScenePad0;
    float3 AmbientColor; float ScenePad1;
    float4 FogSun;
    float4 FogSunBack;
    float4 FogLerp;
    float4 ScreenSize;
};

cbuffer EmitterData : register(b1)
{
    float3 AxisX;       float Mode;
    float3 AxisY;       float VelAngle;
    float4 ScaleCenter;
    float4 AnimTile;
    float2 CamBlend;    float LightTerm;  float Stretch;
    float4 L1;
    float4 L2;
    float Softness;     float DepthScale; float Lit;        float Soft;
    float ParamsRow;    float RowStep;    float FogBlack;   float ViewMode;
    float4 ViewColor;
};

Texture2D<float4> Params : register(t0);
Texture2D<float4> Albedo : register(t1);
Texture2D<float4> NormalMap : register(t2);
Texture2D<float> SceneDepth : register(t3);
SamplerState ParamSampler : register(s0);
SamplerState TexSampler : register(s1);

static const float4 Corners[4] =
{
    float4(-1, -1, 0, 0), float4(-1, 1, 0, 1), float4(1, -1, 1, 0), float4(1, 1, 1, 1)
};

struct VSIn
{
    float4 PosAge : POSITION;
    float4 VelRot : TEXCOORD0;
    float3 SizeBirthFrame : TEXCOORD1;
    uint Vid : SV_VertexID;
};

struct VSOut
{
    float4 Color : COLOR0;
    float4 Fog : COLOR1;
    float3 UvSun : TEXCOORD0;
    float2 DepthSize : TEXCOORD1;
    float3 LightT : TEXCOORD2;
    float3 Ambient : TEXCOORD3;
    float4 Pos : SV_Position;
};

float4 Row(float u, float row)
{
    return Params.SampleLevel(ParamSampler, float2(u + 0.5 * ParamsTexel, ParamsRow + row * RowStep), 0);
}

// vs_1422 lines 86-113: per-vertex fog with a sun phase term and a height falloff.
float4 FogAt(float3 world)
{
    if (FogOn == 0)
        return 0;
    float3 v = world - EyePos;
    float d = length(v);
    float3 dir = v / max(d, 1e-6);
    float x = 1.16 - 0.8 * dot(-LightVec, dir);
    float m = 0.40107 / (x * sqrt(x)) * 0.5 + 0.5;
    float4 o;
    o.rgb = FogBlack != 0 ? 0 : lerp(FogSunBack.rgb, FogSun.rgb, m);
    float height = 1;
    if (abs(v.y) > 0.1 && FogLerp.z != 0)
    {
        float h = v.y * FogLerp.z;
        height = (1 - exp(-max(h, -60))) / h;
    }
    float amount = 1 - min(exp(-(height * FogSunBack.w * d - FogSun.w)), 1);
    float far = saturate(FogLerp.x * d + FogLerp.y);
    o.a = min(far * far + amount, 1);
    return o;
}

VSOut VS(VSIn i)
{
    VSOut o = (VSOut)0;
    float4 c = Corners[i.Vid];
    float age = i.PosAge.w;
    float birth = i.SizeBirthFrame.y;
    float4 color = Row(age, 0) * Row(birth, 1);
    float sizeCurve = Row(age, 2).z * Row(birth, 2).w;
    float size = sizeCurve * i.SizeBirthFrame.x;
    float s, co;
    sincos(i.VelRot.w, s, co);

    float3 a = -normalize(AxisY);
    float3 right, up;
    float orient;
    if (Mode == 2)
    {
        // None: the sheet spans the emitter X and Y axes; the particle angle turns X about the emitter Y axis
        // (vs_1422 lines 39-51) and alpha follows |N.view|.
        float3 xr = AxisX * co + cross(AxisX, a) * s;
        orient = abs(dot(ViewFwd, cross(xr, a)));
        right = xr * size;
        up = a * size;
    }
    else
    {
        float3 r0;
        float3 u0;
        float3 side = cross(ViewFwd, a);
        if (Mode == 1 && dot(side, side) > 1e-10)
        {
            // LockedAxis: turns about the emitter Y axis towards the view (vs_1422 lines 20-28).
            r0 = normalize(side);
            u0 = a;
            orient = abs(dot(ViewFwd, cross(r0, a)));
        }
        else
        {
            // Full: camera-plane aligned.
            r0 = ViewRight;
            u0 = -ViewUp;
            orient = 1;
        }
        r0 *= size;
        u0 *= size;
        right = r0 * co + u0 * s;
        up = u0 * co - r0 * s;
    }

    float3 center = i.PosAge.xyz;
    float3 stretch = 0;
    if (VelAngle != 0 && Mode == 0)
    {
        // Not in R0: VelAngle turns the sprite to the screen-projected velocity, texture top ahead (not verified);
        // StretchMultiplier S moves the front edge (S - 1) |v| 1 s times the Size graph ahead along the velocity,
        // the sprite's own half size stays behind (P0/P3 in game).
        float3 v = i.VelRot.xyz;
        float3 onScreen = v - dot(v, ViewFwd) * ViewFwd;
        float len = length(onScreen);
        if (len > 1e-5)
        {
            float3 ahead = onScreen / len;
            right = normalize(cross(ahead, ViewFwd)) * size;
            up = -ahead * size;
            stretch = v * (Stretch * sizeCurve);
        }
    }

    float2 k = c.xy + ScaleCenter.zw;
    float3 offset = right * (k.x * ScaleCenter.x) + up * (k.y * ScaleCenter.y);
    if (c.y < 0)
        offset += stretch;
    float3 world = center + offset;

    float viewDepth = dot(ViewFwd, world - EyePos);
    float fade = saturate(abs(viewDepth) * CamBlend.x + CamBlend.y);
    color.a *= fade * orient;
    if (color.a <= 1.0 / 255)
    {
        o.Pos = 0;
        return o;
    }

    float frames = AnimTile.w;
    float frame = i.SizeBirthFrame.z;
    frame -= frames * floor(frame / frames);
    // Half a tile in: frame * (1 / U) in float lands just below the row for U = 41, 47, 55, 61 (P7 check).
    float row = floor((frame + 0.5) * AnimTile.x);
    float col = frame - row * AnimTile.z;
    o.UvSun.xy = (float2(col, row) + c.zw) * AnimTile.xy;

    float3 toEye = normalize(EyePos - world);
    o.UvSun.z = 1 - (dot(LightVec, toEye) * 0.5 + 0.5) * LightTerm;
    o.Color = color;
    o.Fog = FogAt(world);
    o.DepthSize = float2(viewDepth, size);
    o.Pos = mul(float4(world, 1), ViewProj);

    if (Lit != 0)
    {
        // vs_1428 lines 141-163: light in a frame around the eye-to-particle direction, turned by the angle.
        float3 d = normalize(center - EyePos);
        float kx = rsqrt(max(d.x * d.x + d.z * d.z, 1e-8));
        float3 b = kx * float3(-d.x * d.y, d.x * d.x + d.z * d.z, -d.y * d.z);
        float3 t = cross(b, d);
        float3 t2 = t * co - b * s;
        float3 b2 = b * co + t * s;
        if (Mode == 2)
        {
            t2 = AxisX * co + cross(AxisX, a) * s;
            b2 = a;
        }
        o.LightT = float3(dot(-t2, LightVec), dot(-b2, LightVec), dot(d, LightVec));
        // vs_1428 lines 164-171: hemisphere ambient 0.25..1 by how high the vertex sits in the sprite and how far
        // it is above the terrain shadow height (3 m ramp); the viewport has open flat ground, so that height is
        // the ground plane.
        float upness = saturate(offset.y / max(size, 1e-6));
        float above = saturate((world.y - GroundY) / 3);
        o.Ambient = L2.rgb * (0.25 + 0.75 * max(upness, above));
    }
    return o;
}

float4 PS(VSOut i) : SV_Target
{
    float4 tex = Albedo.Sample(TexSampler, i.UvSun.xy);
    if (tex.a < 1.0 / 255)
        discard;
    // Not in R0: the tool's draw-order views (layers: the emitter's flat colour with the sprite's alpha; overdraw: a
    // fixed step per drawn pixel, summed by the blend).
    if (ViewMode == 2)
        return float4(ViewColor.rgb, 1);
    if (ViewMode == 1)
        return float4(ViewColor.rgb, tex.a * i.Color.a);
    float4 c = tex * i.Color;
    float alpha = c.a;
    if (Soft != 0)
    {
        // ps_1434 lines 5-17: soft fade over Softness x particle size behind the scene depth; DepthScale pushes the
        // particle back by alpha x size x DepthScale; switched off over the last 10 % of the depth range.
        float range = saturate((i.DepthSize.x - NearPlane) / FarPlane);
        if (range < 0.9999)
        {
            float scene = SceneDepth.Load(int3(i.Pos.xy, 0));
            float particle = i.DepthSize.x + alpha * i.DepthSize.y * DepthScale;
            float soft = saturate((scene - particle) / max(Softness * i.DepthSize.y, 1e-6)) * alpha;
            float keep = saturate(range * 10 - 9);
            alpha = keep * (tex.a * i.Color.a - soft) + soft;
        }
    }
    float3 light;
    if (Lit != 0)
    {
        float3 n = NormalMap.Sample(TexSampler, i.UvSun.xy).xyz * 2 - 1;
        float term = saturate((dot(n, i.LightT) * L1.w + L2.w) * i.UvSun.z);
        light = term * L1.rgb + i.Ambient;
    }
    else
    {
        light = i.UvSun.z * L1.rgb + L2.rgb;
    }
    float3 rgb = c.rgb * light;
    rgb = lerp(rgb, i.Fog.rgb, i.Fog.a);
    return float4(rgb, alpha);
}

struct MeshIn
{
    float3 Pos : POSITION;
    float3 Normal : NORMAL;
    float4 Color : COLOR0;
};

struct MeshOut
{
    float4 Color : COLOR0;
    float3 Normal : NORMAL;
    float Depth : TEXCOORD0;
    float4 Pos : SV_Position;
};

struct MeshTargets
{
    float4 Color : SV_Target0;
    float Depth : SV_Target1;
};

MeshOut MeshVS(MeshIn i)
{
    MeshOut o;
    o.Color = i.Color;
    o.Normal = i.Normal;
    o.Depth = dot(ViewFwd, i.Pos - EyePos);
    o.Pos = mul(float4(i.Pos, 1), ViewProj);
    return o;
}

// Opaque helper geometry writes the linear depth that soft particles compare against (the game's depth pre-pass).
MeshTargets MeshPS(MeshOut i)
{
    MeshTargets o;
    float sun = saturate(dot(normalize(i.Normal), -LightVec));
    o.Color = float4(i.Color.rgb * (SunColor * sun + AmbientColor), 1);
    o.Depth = i.Depth;
    return o;
}

struct LineIn
{
    float3 Pos : POSITION;
    float4 Color : COLOR0;
};

struct LineOut
{
    float4 Color : COLOR0;
    float4 Pos : SV_Position;
};

LineOut LineVS(LineIn i)
{
    LineOut o;
    o.Color = i.Color;
    o.Pos = mul(float4(i.Pos, 1), ViewProj);
    return o;
}

float4 LinePS(LineOut i) : SV_Target
{
    return i.Color;
}

// X-Ray sprites of the A/B view (not the game's pipeline): corners built on the CPU the way the X-Ray engine's
// FillSprite does, coloured as Anomaly's particle.ps (texture x vertex colour, soft against the scene depth).
// UvMode.z = 1: the shader's alpha is not used (X-Ray particles/add blends One/One).
struct SpriteIn
{
    float3 Pos : POSITION;
    float4 Color : COLOR0;
    float3 UvMode : TEXCOORD0;
};

struct SpriteOut
{
    float4 Color : COLOR0;
    float3 UvMode : TEXCOORD0;
    float Depth : TEXCOORD1;
    float4 Pos : SV_Position;
};

SpriteOut SpriteVS(SpriteIn i)
{
    SpriteOut o;
    o.Color = i.Color;
    o.UvMode = i.UvMode;
    o.Depth = dot(ViewFwd, i.Pos - EyePos);
    o.Pos = mul(float4(i.Pos, 1), ViewProj);
    return o;
}

// Anomaly shaders/r3/common_functions.h.
float Contrast(float x, float power)
{
    bool above = x > 0.5;
    float raised = 0.5 * pow(saturate(2 * (above ? 1 - x : x)), power);
    return above ? 1 - raised : raised;
}

float4 SpritePS(SpriteOut i) : SV_Target
{
    float4 c = i.Color * Albedo.Sample(TexSampler, i.UvMode.xy);
    // particle.ps: depth difference minus DEPTH_EPSILON 0.1 m; nothing behind (the cleared far plane) counts as far away.
    float scene = SceneDepth.Load(int3(i.Pos.xy, 0));
    float space = scene >= FarPlane ? 100000 : scene - i.Depth - 0.1;
    float soft = Contrast(saturate(space * 1.3), 2);
    c *= soft;
    if (c.a < 0.01 / 255 && i.UvMode.z == 0)
        discard;
    return i.UvMode.z != 0 ? float4(c.rgb, 1) : c;
}

Texture2D<float> Glyphs : register(t0);

struct OverlayIn
{
    float2 Pixel : POSITION;
    float2 Uv : TEXCOORD0;
    float4 Color : COLOR0;
};

struct OverlayOut
{
    float2 Uv : TEXCOORD0;
    float4 Color : COLOR0;
    float4 Pos : SV_Position;
};

OverlayOut OverlayVS(OverlayIn i)
{
    OverlayOut o;
    o.Uv = i.Uv;
    o.Color = i.Color;
    o.Pos = float4(i.Pixel.x * ScreenSize.z * 2 - 1, 1 - i.Pixel.y * ScreenSize.w * 2, 0, 1);
    return o;
}

float4 OverlayPS(OverlayOut i) : SV_Target
{
    return float4(i.Color.rgb, i.Color.a * Glyphs.Sample(ParamSampler, i.Uv));
}
