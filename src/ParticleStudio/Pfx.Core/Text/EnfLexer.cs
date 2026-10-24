using System.Text;

namespace Pfx.Core.Text;

public sealed class EnfSyntaxException : Exception
{
    public EnfSyntaxException(string message, int position) : base($"{message} at offset {position}")
    {
        Position = position;
    }

    public int Position { get; }
}

public static class EnfLexer
{
    public static (List<EnfToken> Tokens, string Trailing) Tokenize(string text)
    {
        var tokens = new List<EnfToken>();
        int i = 0;
        while (true)
        {
            int start = i;
            while (i < text.Length && char.IsWhiteSpace(text[i]))
                i++;
            string leading = text[start..i];
            if (i == text.Length)
                return (tokens, leading);
            char c = text[i];
            switch (c)
            {
                case '{':
                    tokens.Add(new EnfToken(EnfTokenKind.LBrace, leading, "{"));
                    i++;
                    break;
                case '}':
                    tokens.Add(new EnfToken(EnfTokenKind.RBrace, leading, "}"));
                    i++;
                    break;
                case ':':
                    tokens.Add(new EnfToken(EnfTokenKind.Colon, leading, ":"));
                    i++;
                    break;
                case '"':
                    int close = text.IndexOf('"', i + 1);
                    if (close < 0)
                        throw new EnfSyntaxException("Unterminated string", i);
                    tokens.Add(new EnfToken(EnfTokenKind.String, leading, text[i..(close + 1)]));
                    i = close + 1;
                    break;
                default:
                    int wordStart = i;
                    while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('{' or '}' or ':' or '"'))
                        i++;
                    tokens.Add(new EnfToken(EnfTokenKind.Word, leading, text[wordStart..i]));
                    break;
            }
        }
    }

    // Latin-1 maps every byte to one char and back, so any file survives decode + encode unchanged.
    public static readonly Encoding FileEncoding = Encoding.Latin1;
}
