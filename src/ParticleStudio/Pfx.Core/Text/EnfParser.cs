namespace Pfx.Core.Text;

public static class EnfParser
{
    public static EnfDocument Parse(string text)
    {
        var (tokens, trailing) = EnfLexer.Tokenize(text);
        int pos = 0;
        var items = ParseItems(tokens, ref pos, topLevel: true);
        return new EnfDocument(items, trailing);
    }

    // Items run until the closing brace of the enclosing block (or the end of file at top level). A group of words
    // and strings that stays on one line is either a line item or, when a '{' follows, the header of a block.
    private static List<EnfItem> ParseItems(List<EnfToken> tokens, ref int pos, bool topLevel)
    {
        var items = new List<EnfItem>();
        while (pos < tokens.Count)
        {
            var t = tokens[pos];
            if (t.Kind == EnfTokenKind.RBrace)
            {
                if (topLevel)
                    throw new EnfSyntaxException("Unexpected '}'", pos);
                return items;
            }
            var group = new List<EnfToken>();
            while (pos < tokens.Count && tokens[pos].Kind is EnfTokenKind.Word or EnfTokenKind.String or EnfTokenKind.Colon
                   && (group.Count == 0 || !tokens[pos].StartsLine || group[^1].Kind == EnfTokenKind.Colon))
            {
                group.Add(tokens[pos]);
                pos++;
            }
            if (pos < tokens.Count && tokens[pos].Kind == EnfTokenKind.LBrace)
            {
                var open = tokens[pos++];
                var inner = ParseItems(tokens, ref pos, topLevel: false);
                if (pos >= tokens.Count)
                    throw new EnfSyntaxException("Missing '}'", pos);
                var close = tokens[pos++];
                items.Add(new EnfBlock(group, open, inner, close));
            }
            else if (group.Count > 0)
            {
                items.Add(new EnfLine(group));
            }
            else
            {
                throw new EnfSyntaxException($"Unexpected '{t.Text}'", pos);
            }
        }
        if (!topLevel)
            throw new EnfSyntaxException("Missing '}'", pos);
        return items;
    }
}
