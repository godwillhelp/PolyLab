using System.Numerics;
using Pfx.Core.Model;

namespace Pfx.Sim;

public enum ShapeKind { Box = 1, Point, Cone, Sphere, Ellipse, OnSphere }

// Where the simulator is known to differ from the game, or rests on an unmeasured law (journal P3). The viewport's
// "known differences" panel and the CLI list them per emitter; the UI translates the names.
[Flags]
public enum KnownDifference
{
    None = 0,
    ShapeNotDrawnInGame = 1,
    BirthRateRndMean = 2,
    WindLaw = 4,
    RestitutionFreezes = 8,
    SpringFrameRate = 16,
    AngleOrder = 32,
    SpringLocalFlies = 64,
}

// One emitter's keys as numbers, read through EmitterView (a missing key = the engine default from schema.json).
public sealed record EmitterParams
{
    public string Name { get; init; } = "";
    public ShapeKind Shape { get; init; } = ShapeKind.Point;
    public Vector3 ShapeSize { get; init; }
    public Vector3 ConeAngle { get; init; }
    public int MaxNum { get; init; }
    public float BirthRate { get; init; }
    public float BirthRateRnd { get; init; }
    public float BirthRateVel { get; init; }
    public Vector3 Offset { get; init; }
    public Vector3 Angles { get; init; }
    public bool LocalTransform { get; init; }
    public string? Material { get; init; }
    public float SizeMultiplier { get; init; }
    public float SizeRnd { get; init; }
    public bool RandomRotDir { get; init; }
    public float RotMultiplier { get; init; }
    public float StretchMultiplier { get; init; }
    public bool VelAngle { get; init; }
    public bool RandomAngle { get; init; }
    public float Velocity { get; init; }
    public float VelocityRnd { get; init; }
    public float VelAffect { get; init; }
    public float AirResistance { get; init; }
    public float AirResistanceRnd { get; init; }
    public float GravityMultiply { get; init; }
    public float GravityMultiplyRnd { get; init; }
    public float Restitution { get; init; }
    public float Spring { get; init; }
    public bool Wind { get; init; }
    public bool AnimOnce { get; init; }
    public float AnimFps { get; init; }
    public bool LifetimeByAnim { get; init; }
    public bool RandomFrame { get; init; }
    public float LifeTime { get; init; }
    public float LifeTimeRnd { get; init; }
    public Graph Color { get; init; } = Graph.Constant(1, 1, 1);
    public Graph Alpha { get; init; } = Graph.Constant(1);
    public Graph RotationSpeed { get; init; } = Graph.Constant(1);
    public Graph Size { get; init; } = Graph.Constant(1);
    public float EffectTime { get; init; }
    public bool Repeat { get; init; }
    public Graph ColorMast { get; init; } = Graph.Constant(1, 1, 1);
    public Graph AlphaMast { get; init; } = Graph.Constant(1);
    public Graph RotationSpeedMast { get; init; } = Graph.Constant(1);
    public Graph SizeMast { get; init; } = Graph.Constant(1);
    public Graph BRateMast { get; init; } = Graph.Constant(1);
    public Graph AirResistanceMast { get; init; } = Graph.Constant(1);
    public Graph VelocityMast { get; init; } = Graph.Constant(1);

    // Flipbook grid from the material (TileU × TileV); 1 × 1 when the material is not found.
    public int TileU { get; init; } = 1;
    public int TileV { get; init; } = 1;
    public bool MaterialRead { get; init; }

    public int Frames => Math.Max(1, TileU) * Math.Max(1, TileV);

    // P0: LifetimeByAnim replaces LifeTime by tiles / fps (needs AnimFPS > 0).
    public float BaseLifeTime => LifetimeByAnim && AnimFps > 0 ? Frames / AnimFps : LifeTime;

    // The longest life a particle can draw: X + U[0,1)·RND stays below X + RND (P3); LifetimeByAnim replaces it.
    public float MaxLife => Math.Max(0, LifetimeByAnim && AnimFps > 0 ? BaseLifeTime : LifeTime + Math.Max(0, LifeTimeRnd));

    // EffectTime 0 (8 corpus files) acts as 1 s: with Repeat 0 births stopped after 1.0 s in game (P3).
    public float Cycle => EffectTime > 0 ? EffectTime : 1f;

    // Half extents of the emit volume in emitter space, before Angles (the simulator spawns inside it and the shape
    // gizmo draws it): ShapeSize is the full extent (prompt 2.3); Sphere is round with diameter ShapeSize.x (2 1 0.5
    // gave ~1.8 m on all axes, P3); Ellipse and OnSphere scale per axis; Point and Cone spawn at the emitter point.
    public Vector3 SpawnHalfExtent => Shape switch
    {
        ShapeKind.Box or ShapeKind.Ellipse or ShapeKind.OnSphere => ShapeSize * 0.5f,
        ShapeKind.Sphere => new Vector3(ShapeSize.X * 0.5f),
        _ => Vector3.Zero,
    };

    // tiles(material path) -> (TileU, TileV) or null when the material cannot be read.
    public static EmitterParams From(EmitterView e, Func<string, (int U, int V)?>? tiles = null)
    {
        float F(string key) => (float)(e.Number(key) ?? 0);
        bool B(string key) => F(key) != 0;
        Vector3 V(string key) => e.Numbers(key) is { Length: >= 3 } n ? new((float)n[0], (float)n[1], (float)n[2]) : Vector3.Zero;
        Graph G(string key, int channels) => e.Numbers(key) is { } n ? Graph.FromNumbers(n, channels)
            : Graph.Constant(Enumerable.Repeat(1f, channels).ToArray());
        string? material = e.Text("Material");
        var grid = material != null ? tiles?.Invoke(material) : null;
        return new EmitterParams
        {
            Name = e.Name,
            Shape = ParseShape(e.Text("ShapeType")),
            ShapeSize = V("ShapeSize"),
            ConeAngle = V("ConeAngle"),
            MaxNum = (int)F("MaxNum"),
            BirthRate = F("BirthRate"),
            BirthRateRnd = F("BirthRateRND"),
            BirthRateVel = F("BirthRateVel"),
            Offset = V("Offset"),
            Angles = V("Angles"),
            LocalTransform = B("LocalTransform"),
            Material = material,
            SizeMultiplier = F("SizeMultiplier"),
            SizeRnd = F("SizeRND"),
            RandomRotDir = B("RandomRotDir"),
            RotMultiplier = F("RotMultiplier"),
            StretchMultiplier = F("StretchMultiplier"),
            VelAngle = B("VelAngle"),
            RandomAngle = B("RandomAngle"),
            Velocity = F("Velocity"),
            VelocityRnd = F("VelocityRND"),
            VelAffect = F("VelAffect"),
            AirResistance = F("AirResistance"),
            AirResistanceRnd = F("AirResistanceRND"),
            GravityMultiply = F("GravityMultiply"),
            GravityMultiplyRnd = F("GravityMultiplyRND"),
            Restitution = F("Restitution"),
            Spring = F("Spring"),
            Wind = B("Wind"),
            AnimOnce = B("AnimOnce"),
            AnimFps = F("AnimFPS"),
            LifetimeByAnim = B("LifetimeByAnim"),
            RandomFrame = B("RandomFrame"),
            LifeTime = F("LifeTime"),
            LifeTimeRnd = F("LifeTimeRND"),
            Color = G("Color", 3),
            Alpha = G("Alpha", 1),
            RotationSpeed = G("RotationSpeed", 1),
            Size = G("Size", 1),
            EffectTime = F("EffectTime"),
            Repeat = B("Repeat"),
            ColorMast = G("ColorMast", 3),
            AlphaMast = G("AlphaMast", 1),
            RotationSpeedMast = G("RotationSpeedMast", 1),
            SizeMast = G("SizeMast", 1),
            BRateMast = G("BRateMast", 1),
            AirResistanceMast = G("AirResistanceMast", 1),
            VelocityMast = G("VelocityMast", 1),
            TileU = grid?.U ?? 1,
            TileV = grid?.V ?? 1,
            MaterialRead = grid != null,
        };
    }

    public KnownDifference Differences
    {
        get
        {
            var d = KnownDifference.None;
            if (Shape is ShapeKind.Cone or ShapeKind.OnSphere && ShapeSize != Vector3.Zero)
                d |= KnownDifference.ShapeNotDrawnInGame;
            if (BirthRateRnd > 0)
                d |= KnownDifference.BirthRateRndMean;
            if (Wind)
                d |= KnownDifference.WindLaw;
            if (Restitution > 0)
                d |= KnownDifference.RestitutionFreezes;
            if (Spring > 0)
                d |= KnownDifference.SpringFrameRate;
            // P12d in game: with LocalTransform 1 the spring pulls the particles' local positions to the source's world
            // position, so they fly off along the line from the map origin through the source (Spring 0.05 at x 6100 m:
            // nothing left after 6 s; Spring 0.000001: the cloud drifted away from the origin). The simulator does not know
            // the world position and keeps the pull to the source.
            if (Spring > 0 && LocalTransform)
                d |= KnownDifference.SpringLocalFlies;
            // P8 in game: yaw is applied after pitch and roll; pitch + roll together leaned to roll first (10.5 vs 11.8 px
            // rms), too close to call.
            if (Angles.Y != 0 && Angles.Z != 0)
                d |= KnownDifference.AngleOrder;
            return d;
        }
    }

    // Files write the name (Box, Point ..); the schema default is the number 2 = Point (enum counted from 1, range
    // "1 7"; Workbench shows a missing ShapeType as Point, W0).
    public static ShapeKind ParseShape(string? text)
    {
        if (text == null)
            return ShapeKind.Point;
        if (Enum.TryParse<ShapeKind>(text, true, out var kind) && Enum.IsDefined(kind))
            return kind;
        return ShapeKind.Point;
    }
}
