using System.Globalization;
using Pfx.Core.Text;

namespace Pfx.Core.Editing;

// Builds edits on the token tree. New tokens copy the indentation and line break the file already uses, and new
// keys are placed in the engine's registration order, the way Workbench writes them.
public static class EnfEdits
{
    public static string Number(double value) => value.ToString("0.##########", CultureInfo.InvariantCulture);

    public static string Quote(string value) => "\"" + value + "\"";

    public static IEdit SetLine(EnfDocument doc, EnfBlock block, string key, IReadOnlyList<string> values,
        Func<string, int?>? orderOf = null)
    {
        var line = block.FindLine(key);
        if (line != null)
        {
            var tokens = new List<EnfToken>();
            for (int i = 0; i < values.Count; i++)
            {
                string leading = i + 1 < line.Tokens.Count ? line.Tokens[i + 1].Leading : " ";
                tokens.Add(new EnfToken(KindOf(values[i]), leading, values[i]));
            }
            return new SpliceEdit<EnfToken>(line.Tokens, 1, line.Tokens.Count - 1, tokens);
        }
        var newTokens = new List<EnfToken> { new(EnfTokenKind.Word, doc.NewLine + ChildIndent(block), key) };
        newTokens.AddRange(values.Select(v => new EnfToken(KindOf(v), " ", v)));
        return new SpliceEdit<EnfItem>(block.Items, InsertIndex(block, key, orderOf), 0, new[] { new EnfLine(newTokens) });
    }

    public static IEdit? RemoveKey(EnfBlock block, string key)
    {
        int i = block.Items.FindIndex(it => KeyOf(it).Equals(key, StringComparison.OrdinalIgnoreCase));
        return i < 0 ? null : new SpliceEdit<EnfItem>(block.Items, i, 1, Array.Empty<EnfItem>());
    }

    // Graph blocks ("Color { 0 1 1 1 1 1 1 1 }") get all their numbers on one line, like Workbench writes them.
    public static IEdit SetGraph(EnfDocument doc, EnfBlock block, string key, IReadOnlyList<double> numbers,
        Func<string, int?>? orderOf = null) => SetGraph(doc, block, key, numbers.Select(Number).ToList(), orderOf);

    // The numbers as texts: a caller keeps the spelling of numbers it did not change (Model.GraphKeys).
    public static IEdit SetGraph(EnfDocument doc, EnfBlock block, string key, IReadOnlyList<string> numbers,
        Func<string, int?>? orderOf = null)
    {
        var graph = block.FindBlock(key);
        string inner = ChildIndent(block) + " ";
        if (graph != null)
        {
            string first = graph.Items.Count > 0 ? graph.Items[0].First.Leading : doc.NewLine + inner;
            return new SpliceEdit<EnfItem>(graph.Items, 0, graph.Items.Count, new[] { NumberLine(first, numbers) });
        }
        string indent = ChildIndent(block);
        var created = new EnfBlock(
            new List<EnfToken> { new(EnfTokenKind.Word, doc.NewLine + indent, key) },
            new EnfToken(EnfTokenKind.LBrace, " ", "{"),
            new List<EnfItem> { NumberLine(doc.NewLine + inner, numbers) },
            new EnfToken(EnfTokenKind.RBrace, doc.NewLine + indent, "}"));
        return new SpliceEdit<EnfItem>(block.Items, InsertIndex(block, key, orderOf), 0, new[] { created });
    }

    public static IEdit AddChildBlock(EnfDocument doc, EnfBlock parent, string key, string? name, int index)
    {
        string indent = ChildIndent(parent);
        var header = new List<EnfToken> { new(EnfTokenKind.Word, doc.NewLine + indent, key) };
        if (name != null)
            header.Add(new EnfToken(EnfTokenKind.Word, " ", name));
        var block = new EnfBlock(header, new EnfToken(EnfTokenKind.LBrace, " ", "{"), new List<EnfItem>(),
            new EnfToken(EnfTokenKind.RBrace, doc.NewLine + indent, "}"));
        return new SpliceEdit<EnfItem>(parent.Items, index, 0, new[] { block });
    }

    public static IEdit RemoveItem(EnfBlock parent, EnfItem item) =>
        new SpliceEdit<EnfItem>(parent.Items, parent.Items.IndexOf(item), 1, Array.Empty<EnfItem>());

    public static IEdit MoveItem(EnfBlock parent, EnfItem item, int newIndex)
    {
        int from = parent.Items.IndexOf(item);
        var remove = new SpliceEdit<EnfItem>(parent.Items, from, 1, Array.Empty<EnfItem>());
        var insert = new SpliceEdit<EnfItem>(parent.Items, newIndex, 0, new[] { item });
        return new CompositeEdit(new IEdit[] { remove, insert });
    }

    public static IEdit Rename(EnfBlock block, string name)
    {
        var old = block.Header[^1];
        return new SpliceEdit<EnfToken>(block.Header, block.Header.Count - 1, 1,
            new[] { new EnfToken(EnfTokenKind.Word, old.Leading, name) });
    }

    public static string KeyOf(EnfItem item) => item switch
    {
        EnfLine l => l.Key,
        EnfBlock b => b.Key,
        _ => "",
    };

    private static EnfLine NumberLine(string leading, IReadOnlyList<string> numbers)
    {
        var tokens = new List<EnfToken>();
        for (int i = 0; i < numbers.Count; i++)
            tokens.Add(new EnfToken(EnfTokenKind.Word, i == 0 ? leading : " ", numbers[i]));
        return new EnfLine(tokens);
    }

    private static EnfTokenKind KindOf(string text) =>
        text.Length >= 2 && text[0] == '"' && text[^1] == '"' ? EnfTokenKind.String : EnfTokenKind.Word;

    private static string IndentOf(EnfToken t)
    {
        int nl = t.Leading.LastIndexOf('\n');
        return nl < 0 ? "" : t.Leading[(nl + 1)..];
    }

    private static string ChildIndent(EnfBlock block) =>
        block.Items.Count > 0 ? IndentOf(block.Items[0].First) : IndentOf(block.First) + " ";

    private static int InsertIndex(EnfBlock block, string key, Func<string, int?>? orderOf)
    {
        if (orderOf == null || orderOf(key) is not int order)
            return block.Items.Count;
        int index = -1;
        for (int i = 0; i < block.Items.Count; i++)
        {
            if (orderOf(KeyOf(block.Items[i])) is int o && o < order)
                index = i;
        }
        if (index >= 0)
            return index + 1;
        int firstLater = block.Items.FindIndex(it => orderOf(KeyOf(it)) is int o && o > order);
        return firstLater >= 0 ? firstLater : block.Items.Count;
    }
}
