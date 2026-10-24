using System.Text;

namespace Pfx.Core.Text;

public abstract class EnfItem
{
    public abstract EnfToken First { get; }
    public abstract void Write(StringBuilder sb);
}

// One line of tokens: "Key value value ..." or, inside a graph block, a row of bare numbers.
public sealed class EnfLine : EnfItem
{
    public EnfLine(List<EnfToken> tokens)
    {
        Tokens = tokens;
    }

    public List<EnfToken> Tokens { get; }

    public override EnfToken First => Tokens[0];

    public string Key => Tokens[0].Text;

    public IEnumerable<EnfToken> Values => Tokens.Skip(1);

    public override void Write(StringBuilder sb)
    {
        foreach (var t in Tokens)
            sb.Append(t.Leading).Append(t.Text);
    }
}

// "Header tokens { items }": EffectDef, Emitors, EmitorDef name, graph blocks like Color, material roots.
public sealed class EnfBlock : EnfItem
{
    public EnfBlock(List<EnfToken> header, EnfToken open, List<EnfItem> items, EnfToken close)
    {
        Header = header;
        Open = open;
        Items = items;
        Close = close;
    }

    public List<EnfToken> Header { get; }
    public EnfToken Open { get; }
    public List<EnfItem> Items { get; }
    public EnfToken Close { get; }

    public override EnfToken First => Header.Count > 0 ? Header[0] : Open;

    public string Key => Header.Count > 0 ? Header[0].Text : "";

    // "EmitorDef sparks" -> "sparks"; "material "path": Particle" -> "Particle" (the class after the colon).
    public string? Name => Header.Count > 1 && Header[^2].Kind != EnfTokenKind.Colon ? Header[^1].Value : null;

    public string ClassName
    {
        get
        {
            int colon = Header.FindIndex(t => t.Kind == EnfTokenKind.Colon);
            return colon >= 0 && colon + 1 < Header.Count ? Header[colon + 1].Text : Key;
        }
    }

    public EnfLine? FindLine(string key) =>
        Items.OfType<EnfLine>().FirstOrDefault(l => l.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    public EnfBlock? FindBlock(string key) =>
        Items.OfType<EnfBlock>().FirstOrDefault(b => b.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    public override void Write(StringBuilder sb)
    {
        foreach (var t in Header)
            sb.Append(t.Leading).Append(t.Text);
        sb.Append(Open.Leading).Append(Open.Text);
        foreach (var item in Items)
            item.Write(sb);
        sb.Append(Close.Leading).Append(Close.Text);
    }
}

public sealed class EnfDocument
{
    public EnfDocument(List<EnfItem> items, string trailing)
    {
        Items = items;
        Trailing = trailing;
    }

    public List<EnfItem> Items { get; }
    public string Trailing { get; set; }

    public EnfBlock? Root => Items.OfType<EnfBlock>().FirstOrDefault();

    // The line break the file already uses, so inserted lines match it.
    public string NewLine
    {
        get
        {
            var sb = new StringBuilder();
            foreach (var item in Items)
                item.Write(sb);
            string text = sb.ToString();
            int lf = text.IndexOf('\n');
            return lf > 0 && text[lf - 1] == '\r' ? "\r\n" : "\n";
        }
    }

    public string Write()
    {
        var sb = new StringBuilder();
        foreach (var item in Items)
            item.Write(sb);
        sb.Append(Trailing);
        return sb.ToString();
    }

    public byte[] ToBytes() => EnfLexer.FileEncoding.GetBytes(Write());

    public static EnfDocument Parse(string text) => EnfParser.Parse(text);

    public static EnfDocument Parse(byte[] bytes) => EnfParser.Parse(EnfLexer.FileEncoding.GetString(bytes));

    public static EnfDocument Load(string path) => Parse(File.ReadAllBytes(path));
}
