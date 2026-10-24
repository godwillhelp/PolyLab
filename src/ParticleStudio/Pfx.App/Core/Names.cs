using System.Text.RegularExpressions;

namespace Pfx.App.Core;

// Emitter names: Workbench rejects special characters and spaces, and an Enforce keyword (ref, class, ...) loads as an
// unnamed emitter there and is saved as "EmitorDef {" (W0, spec 2.13).
public static partial class Names
{
    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "ref", "class", "auto", "autoptr", "proto", "native", "static", "const", "out", "inout", "notnull", "private",
        "protected", "override", "event", "sealed", "modded", "typedef", "enum", "extends", "void", "int", "float",
        "bool", "string", "vector", "array", "set", "map", "null", "this", "super", "new", "delete", "return", "if",
        "else", "for", "foreach", "while", "break", "continue", "switch", "case", "default", "local", "owned",
        "volatile", "thread", "external", "func", "typename",
    };

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex Plain();

    // Null when the name is fine, else the language key of the reason.
    public static string? Problem(string name, IEnumerable<string> others)
    {
        if (!Plain().IsMatch(name))
            return "Name.Chars";
        if (Keywords.Contains(name))
            return "Name.Keyword";
        if (others.Contains(name))
            return "Name.Taken";
        return null;
    }

    // emitor1 -> emitor2 -> emitor3, sparks_cloned -> sparks_cloned2: the trailing number counts up.
    public static string Unique(string wanted, IEnumerable<string> others)
    {
        var taken = others.ToHashSet();
        if (!taken.Contains(wanted))
            return wanted;
        var m = Trailing().Match(wanted);
        string stem = m.Groups[1].Value;
        int n = m.Groups[2].Length > 0 ? int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) + 1 : 2;
        while (taken.Contains(stem + n))
            n++;
        return stem + n;
    }

    [GeneratedRegex("^(.*?)([0-9]*)$")]
    private static partial Regex Trailing();
}
