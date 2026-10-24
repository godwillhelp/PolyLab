using System.Numerics;
using Pfx.Sim.XRay;

namespace Pfx.Render;

// The quads the X-Ray engine draws for a playing effect (ParticleEffect.cpp ParticleRenderStream + FillSprite): half
// sizes size / 2 plus |v| x velocity scale, the angle rot.x, the flipbook tile of the frame (CalculateTC), and the
// sprite axes: the camera's up and right; along the velocity when aligned to the path; across it when face-aligned;
// the default rotation (setXYZ) when world-aligned and still. Blend from the shader name (xray.py SHADERS).
public static class XrSprites
{
    // Below this speed an aligned sprite counts as still (the engine's EPS_S).
    public const double StillSpeed = 1e-6;

    public static SpriteBlend? BlendOf(string shader) => shader switch
    {
        "particles/add" => SpriteBlend.Add,
        "particles/alpha_add" or "particles/xadd" => SpriteBlend.AlphaAdd,
        "particles/blend" or "particles/xblend" or "particles/set" => SpriteBlend.Blend,
        _ => null,
    };

    public static int Add(SpriteBatch batch, XrEffectSim sim, string? texture, Vector3 shift, Vector3 camRight, Vector3 camUp, Vector3 camFwd)
    {
        var def = sim.Def;
        if (!def.Has(XrEffect.Sprite) || BlendOf(def.Shader) is not { } blend || sim.Count == 0)
            return 0;
        batch.Begin(texture, blend);
        foreach (ref readonly var m in sim.Particles)
        {
            var lt = Vector2.Zero;
            var rb = Vector2.One;
            if (def.Has(XrEffect.Framed) && def.Frame is { } f && f.DimX > 0)
            {
                int frame = m.Frame / 255;
                lt = new Vector2((float)(frame % f.DimX * f.TexU), (float)(frame / f.DimX * f.TexV));
                rb = lt + new Vector2((float)f.TexU, (float)f.TexV);
            }
            double rx = m.Size.X * 0.5, ry = m.Size.Y * 0.5, speed = m.Vel.Length;
            if (def.Has(XrEffect.VelocityScale) && def.VelScale is { } vs)
            {
                rx += speed * vs.X;
                ry += speed * vs.Y;
            }
            var pos = ToV(m.Pos) + shift;
            Vector3 t = camUp, r = camRight;
            if (def.Has(XrEffect.AlignToPath))
            {
                var dflt = def.Align ?? D3.Zero;
                if (speed < StillSpeed && def.Has(XrEffect.WorldAlign))
                {
                    var (i, _, k) = SetHpb(dflt.Y, dflt.X, dflt.Z);
                    (t, r) = (k, i);
                }
                else if (speed >= StillSpeed && def.Has(XrEffect.FaceAlign))
                {
                    var kk = ToV(m.Vel / speed);
                    var j = Vector3.UnitY;
                    if (MathF.Abs(Vector3.Dot(j, kk)) > 0.99f)
                        j = Vector3.UnitZ;
                    var i = Vector3.Normalize(Vector3.Cross(j, kk));
                    j = Vector3.Normalize(Vector3.Cross(kk, i));
                    (t, r) = (j, i);
                }
                else
                {
                    t = speed >= StillSpeed ? ToV(m.Vel / speed) : SetDirHp(-dflt.Y, -dflt.X);
                    var c = Vector3.Cross(t, camFwd);
                    r = c.LengthSquared() > 1e-12f ? Vector3.Normalize(c) : camRight;
                }
            }
            float sa = MathF.Sin((float)m.Rot), ca = MathF.Cos((float)m.Rot);
            var vr = (t * sa + r * ca) * (float)rx;
            var vt = (t * ca - r * sa) * (float)ry;
            var a = vt - vr;
            var b = vt + vr;
            batch.Quad(pos - b, pos + a, pos - a, pos + b, lt, rb, new Vector4((float)m.R, (float)m.G, (float)m.B, (float)m.A));
        }
        batch.Close();
        return sim.Count;
    }

    private static Vector3 ToV(D3 d) => new((float)d.X, (float)d.Y, (float)d.Z);

    // xrCore _matrix.h setHPB (i, j, k rows); setXYZ(x, y, z) = setHPB(y, x, z).
    private static (Vector3 I, Vector3 J, Vector3 K) SetHpb(double h, double p, double b)
    {
        float sh = (float)Math.Sin(h), ch = (float)Math.Cos(h), sp = (float)Math.Sin(p), cp = (float)Math.Cos(p), sb = (float)Math.Sin(b), cb = (float)Math.Cos(b);
        float cc = ch * cb, cs = ch * sb, sc = sh * cb, ss = sh * sb;
        return (new Vector3(cc - sp * ss, -cp * sb, sp * cs + sc), new Vector3(sp * sc + cs, cp * cb, ss - sp * cc), new Vector3(-cp * sh, sp, cp * ch));
    }

    // xrCore _vector3d.h setHP: the direction of heading h and pitch p.
    private static Vector3 SetDirHp(double h, double p)
    {
        float sh = (float)Math.Sin(h), ch = (float)Math.Cos(h), sp = (float)Math.Sin(p), cp = (float)Math.Cos(p);
        return new Vector3(-cp * sh, sp, cp * ch);
    }
}
