using Pfx.Core.Schema;

namespace Pfx.Tests;

public class SchemaTests
{
    private static readonly PfxSchema Schema = PfxSchema.Shared;

    // The emitter keys of prompt section 2.2 (from the engine strings and the vanilla files).
    [Theory]
    [InlineData("ShapeType", "ShapeSize", "ConeAngle", "LOD", "MaxNum", "BirthRate", "BirthRateRND", "BirthRateVel",
        "Offset", "Angles", "LocalTransform")]
    [InlineData("Material", "CenterX", "CenterY", "ScaleX", "ScaleY", "SizeMultiplier", "SizeRND", "RandomRotDir",
        "RotMultiplier", "StretchMultiplier", "StreakFullUV", "VelAngle", "RandomAngle", "BillboardingType", "Sort")]
    [InlineData("Velocity", "VelocityRND", "VelAffect", "AirResistance", "AirResistanceRND", "GravityMultiply",
        "GravityMultiplyRND", "Restitution", "Spring", "Wind")]
    [InlineData("AnimOnce", "AnimFPS", "LifetimeByAnim", "RandomFrame", "LifeTime", "LifeTimeRND", "LifetimeShortening")]
    [InlineData("Color", "Alpha", "Size", "RotationSpeed", "EffectTime", "Repeat", "ColorMast", "AlphaMast", "SizeMast",
        "RotationSpeedMast", "BRateMast", "VelocityMast", "AirResistanceMast")]
    public void Emitter_keys_are_in_the_schema(params string[] keys)
    {
        foreach (var k in keys)
            Assert.True(Schema.Find(SchemaScope.Emitor, k) != null, $"{k} missing");
    }

    [Theory]
    [InlineData("AlbedoMap", "NormalMap", "Color", "Diffuse", "Ambient", "Emissive", "BlendMode", "Sort", "Softness",
        "DepthScale", "CameraBlendNear", "CameraBlendFar", "TileU", "TileV", "LightScale", "BidirLightScale",
        "CastShadow", "ReceiveShadow", "ZBias", "AlphaTest", "Cull", "ZEnable", "SortBias")]
    public void Material_keys_are_in_the_schema(params string[] keys)
    {
        foreach (var k in keys)
            Assert.True(Schema.Find(SchemaScope.Material, k) != null, $"{k} missing");
    }

    [Fact]
    public void Every_schema_key_has_an_editor()
    {
        var all = Schema.Effect.Concat(Schema.Emitor).Concat(Schema.Material);
        var missing = all.Where(f => FieldDescriptor.For(f).Editor == EditorKind.Unknown)
            .Select(f => $"{f.Scope}.{f.Key} ({f.Type})").ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void Engine_defaults_and_ranges_are_read()
    {
        var velocity = Schema.Find(SchemaScope.Emitor, "Velocity")!;
        Assert.Equal("2", velocity.Default);
        Assert.Equal(new SchemaRange(-10000, 10000, null), velocity.Range);
        Assert.Equal("360 0 20", Schema.Find(SchemaScope.Emitor, "ConeAngle")!.Default);
        Assert.Equal(new SchemaRange(0.1, 1, 0.01), Schema.Find(SchemaScope.Emitor, "ScaleX")!.Range);
        Assert.Equal("0.9", Schema.Find(SchemaScope.Emitor, "LifetimeShortening")!.Default);
        Assert.Equal(new SchemaRange(0, 2, 0.01), Schema.Find(SchemaScope.Material, "Softness")!.Range);
    }

    [Fact]
    public void Emitter_keys_keep_registration_order()
    {
        int Order(string k) => Schema.Find(SchemaScope.Emitor, k)!.Order;
        Assert.True(Order("ShapeType") < Order("MaxNum"));
        Assert.True(Order("Material") < Order("Velocity"));
        Assert.True(Order("LifeTime") < Order("Color"));
        Assert.True(Order("EffectTime") < Order("ColorMast"));
    }
}
