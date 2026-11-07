using System.Text;

namespace PolyLab.CgUi;

public static class EnforceOut
{
    public const int MaxIntsPerLiteral = 200_000;

    public static string JsonInts(IEnumerable<int> ints) => "[" + string.Join(",", ints) + "]";

    public static string JsonStrings(IEnumerable<string> strs) => "[" + string.Join(",", strs.Select(JsonString)) + "]";

    public static string JsonString(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char ch in s)
        {
            if (ch == '"')
                sb.Append("\\\"");
            else if (ch == '\\')
                sb.Append("\\\\");
            else if (ch < 0x20 || ch > 0x7E)
                sb.Append("\\u").Append(((int)ch).ToString("x4"));
            else
                sb.Append(ch);
        }
        return sb.Append('"').ToString();
    }

    public static string Literal(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    public static string ClassSource(string className, Encoded e)
    {
        if (e.Ints.Length > MaxIntsPerLiteral)
            throw new InvalidOperationException(
                $"{className}: {e.Ints.Length} ints exceed the tested literal size of {MaxIntsPerLiteral}");
        return $"class {className}\n{{\n    static const string INTS = {Literal(JsonInts(e.Ints))};\n" +
               $"    static const string STRS = {Literal(JsonStrings(e.Strs))};\n}}\n";
    }
}
