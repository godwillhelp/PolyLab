using System.Text;

namespace Pfx.XRay;

// Section tables of X-Ray text files, read as xray.py read_ini does: cp1251, ';' starts a comment, "[name]" opens a
// section (names and keys lower-case), "key = value" lines, a later key wins.
public static class XrText
{
    public static readonly Encoding Cp1251;

    static XrText()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Cp1251 = Encoding.GetEncoding(1251);
    }

    public static Dictionary<string, Dictionary<string, string>> ReadIni(byte[] bytes) => ReadIni(Cp1251.GetString(bytes));

    public static Dictionary<string, Dictionary<string, string>> ReadIni(string text)
    {
        var secs = new Dictionary<string, Dictionary<string, string>>();
        Dictionary<string, string>? cur = null;
        foreach (var raw in Lines(text))
        {
            int semi = raw.IndexOf(';');
            string line = (semi >= 0 ? raw[..semi] : raw).Trim();
            if (line.Length == 0)
                continue;
            if (line.StartsWith('[') && line.Contains(']'))
            {
                string name = line[1..line.IndexOf(']')].Trim().ToLowerInvariant();
                if (!secs.TryGetValue(name, out cur))
                    secs[name] = cur = new Dictionary<string, string>();
            }
            else if (cur != null && line.Contains('='))
            {
                int eq = line.IndexOf('=');
                cur[line[..eq].Trim().ToLowerInvariant()] = line[(eq + 1)..].Trim();
            }
        }
        return secs;
    }

    // Python str.splitlines() boundaries (the ones a cp1251 decode can produce).
    public static IEnumerable<string> Lines(string text)
    {
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '\n' or '\r' or '\v' or '\f' or '\x1c' or '\x1d' or '\x1e' or '\u2028' or '\u2029')
            {
                yield return text[start..i];
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                start = i + 1;
            }
        }
        if (start < text.Length)
            yield return text[start..];
    }
}
