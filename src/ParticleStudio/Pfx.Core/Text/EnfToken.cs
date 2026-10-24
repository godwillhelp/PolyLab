namespace Pfx.Core.Text;

public enum EnfTokenKind { Word, String, LBrace, RBrace, Colon }

// Leading holds every whitespace character before the token, so writing Leading + Text for all tokens in order
// gives back the original file byte for byte.
public sealed class EnfToken
{
    public EnfToken(EnfTokenKind kind, string leading, string text)
    {
        Kind = kind;
        Leading = leading;
        Text = text;
    }

    public EnfTokenKind Kind { get; }
    public string Leading { get; set; }
    public string Text { get; set; }

    public bool StartsLine => Leading.Contains('\n');

    // A string token without its quotes; words are returned as they are.
    public string Value => Kind == EnfTokenKind.String ? Text[1..^1] : Text;
}
