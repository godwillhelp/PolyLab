using System.Globalization;

namespace Pfx.Link;

public enum ParamKind { Float, Bool, Vector, Int }

// How the engine stores a key's file value (P11, GetParticleParmOriginal against files with known values): the cone as
// (spread, polar / 2, azimuth / 2) for a file "spread azimuth polar", the size as half, the stretch as S - 1.
public enum ParamUnit { Same, Half, MinusOne, Cone }

// One EmitorParam the engine lets a script set on a live source (1_core/proto/envisual.c), the .ptc key it stands for,
// and how its value is written. Its number is the running game's (LinkClient.Params).
public sealed record GameParam(string Name, ParamKind Kind, string? Key, bool Writable = true, ParamUnit Unit = ParamUnit.Same);

// The live-edit channel's table (spec 2.6): an edit of a key in this table goes to the running game with
// SetParticleParm; every other edit reloads the part. The parameters are matched by their EmitorParam names: the
// running game reports its own enum (P11: DayZ 1.29's enum has three entries more than the P: drive's envisual.c, so
// a written-down order sent VELOCITY into EMITOFFSET and crashed the client). Each key's pairing was checked in game
// by reading the engine's original value back from a file with known values (journal P11).
public static class GameParams
{
    public static readonly IReadOnlyList<GameParam> All = new GameParam[]
    {
        new("CONEANGLE", ParamKind.Vector, "ConeAngle", Unit: ParamUnit.Cone),
        new("EMITOFFSET", ParamKind.Vector, "Offset"),
        new("VELOCITY", ParamKind.Float, "Velocity"),
        new("VELOCITY_RND", ParamKind.Float, "VelocityRND"),
        new("AVELOCITY", ParamKind.Float, "RotMultiplier"),
        new("SIZE", ParamKind.Float, "SizeMultiplier", Unit: ParamUnit.Half),
        new("STRETCH", ParamKind.Float, "StretchMultiplier", Unit: ParamUnit.MinusOne),
        new("RANDOM_ANGLE", ParamKind.Bool, "RandomAngle"),
        new("RANDOM_ROT", ParamKind.Bool, "RandomRotDir"),
        new("AIR_RESISTANCE", ParamKind.Float, "AirResistance"),
        new("AIR_RESISTANCE_RND", ParamKind.Float, "AirResistanceRND"),
        new("GRAVITY_SCALE", ParamKind.Float, "GravityMultiply"),
        new("GRAVITY_SCALE_RND", ParamKind.Float, "GravityMultiplyRND"),
        new("BIRTH_RATE", ParamKind.Float, "BirthRate"),
        new("BIRTH_RATE_RND", ParamKind.Float, "BirthRateRND"),
        new("LIFETIME", ParamKind.Float, "LifeTime"),
        new("LIFETIME_RND", ParamKind.Float, "LifeTimeRND"),
        new("LIFETIME_BY_ANIM", ParamKind.Bool, "LifetimeByAnim"),
        new("ANIM_ONCE", ParamKind.Bool, "AnimOnce"),
        new("RAND_FRAME", ParamKind.Bool, "RandomFrame"),
        new("EFFECT_TIME", ParamKind.Float, "EffectTime"),
        new("REPEAT", ParamKind.Bool, "Repeat"),
        new("CURRENT_TIME", ParamKind.Float, null),
        new("ACTIVE_PARTICLES", ParamKind.Int, null, false),
        new("SORT", ParamKind.Bool, "Sort"),
        new("WIND", ParamKind.Bool, "Wind"),
        new("SPRING", ParamKind.Float, "Spring"),
    };

    private static readonly Dictionary<string, GameParam> ByName = All.ToDictionary(p => p.Name, StringComparer.Ordinal);

    public static GameParam? OfName(string name) => ByName.TryGetValue(name, out var p) ? p : null;

    private static readonly Dictionary<string, GameParam> ByKey =
        All.Where(p => p.Key != null).ToDictionary(p => p.Key!, StringComparer.OrdinalIgnoreCase);

    public static GameParam? OfKey(string key) => ByKey.TryGetValue(key, out var p) ? p : null;

    public static int Values(ParamKind kind) => kind == ParamKind.Vector ? 3 : 1;

    // The "set" arguments for a key's values as the file writes them, in engine units: "f 2.5", "b 1", "v 0 90 0". Null
    // when the values do not fit the kind (the edit then reloads the part).
    public static string? Arguments(GameParam p, IReadOnlyList<string> values)
    {
        int n = Values(p.Kind);
        if (values.Count < n)
            return null;
        var nums = new double[n];
        for (int i = 0; i < n; i++)
        {
            if (!TryNumber(values[i], out nums[i]))
                return null;
        }
        switch (p.Unit)
        {
            case ParamUnit.Half:
                nums[0] /= 2;
                break;
            case ParamUnit.MinusOne:
                nums[0] -= 1;
                break;
            case ParamUnit.Cone:
                (nums[1], nums[2]) = (nums[2] / 2, nums[1] / 2);
                break;
        }
        return p.Kind switch
        {
            ParamKind.Vector => "v " + string.Join(" ", nums.Select(Num)),
            ParamKind.Bool => nums[0] != 0 ? "b 1" : "b 0",
            _ => "f " + Num(nums[0]),
        };
    }

    public static bool TryNumber(string text, out double value)
    {
        if (text.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            value = 1;
            return true;
        }
        if (text.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            value = 0;
            return true;
        }
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    public static string Num(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
}
