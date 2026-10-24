using System.Globalization;
using Pfx.Core.Editing;
using Pfx.Core.Schema;
using Pfx.Core.Text;

namespace Pfx.Core.Model;

// A block of "Key values" lines read through the schema: a key that is not in the file has the engine default.
// Every setter returns an edit; nothing changes until the caller runs it (UndoStack.Do).
public class KeyedBlock
{
    protected KeyedBlock(EnfDocument doc, EnfBlock block, SchemaScope scope, PfxSchema schema)
    {
        Doc = doc;
        Block = block;
        Scope = scope;
        Schema = schema;
    }

    public EnfDocument Doc { get; }
    public EnfBlock Block { get; }
    public SchemaScope Scope { get; }
    public PfxSchema Schema { get; }

    public bool Has(string key) => Block.FindLine(key) != null || Block.FindBlock(key) != null;

    // Raw value texts as written (strings without quotes), or the schema default split into parts, or null.
    public IReadOnlyList<string>? Raw(string key)
    {
        var line = Block.FindLine(key);
        if (line != null)
            return line.Values.SelectMany(t => t.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToList();
        var graph = Block.FindBlock(key);
        if (graph != null)
            return graph.Items.OfType<EnfLine>().SelectMany(l => l.Tokens).Select(t => t.Value).ToList();
        string? def = Schema.Find(Scope, key)?.Default;
        return def?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    public double? Number(string key) => ParseNumber(Raw(key) is { Count: > 0 } r ? r[0] : null);

    public double[]? Numbers(string key)
    {
        var raw = Raw(key);
        if (raw == null)
            return null;
        var result = new double[raw.Count];
        for (int i = 0; i < raw.Count; i++)
        {
            if (ParseNumber(raw[i]) is not double d)
                return null;
            result[i] = d;
        }
        return result;
    }

    public string? Text(string key) => Raw(key) is { Count: > 0 } r ? string.Join(" ", r) : null;

    // Older materials write a vector as one quoted string ("0 0 0 1"); such a line keeps that form.
    public IEdit Set(string key, params double[] values)
    {
        var texts = values.Select(EnfEdits.Number).ToList();
        var line = Block.FindLine(key);
        bool quotedVector = line != null && line.Tokens.Count == 2 && line.Tokens[1].Kind == EnfTokenKind.String
                            && line.Tokens[1].Value.Contains(' ');
        if (quotedVector)
            texts = new List<string> { EnfEdits.Quote(string.Join(" ", texts)) };
        return EnfEdits.SetLine(Doc, Block, key, texts, OrderOf);
    }

    public IEdit SetWord(string key, string word) => EnfEdits.SetLine(Doc, Block, key, new[] { word }, OrderOf);

    // Values as written (an own recipe's parameter typed as text: numbers, a word or a quoted path).
    public IEdit SetWords(string key, IReadOnlyList<string> words) => EnfEdits.SetLine(Doc, Block, key, words, OrderOf);

    public IEdit SetString(string key, string value) =>
        EnfEdits.SetLine(Doc, Block, key, new[] { EnfEdits.Quote(value) }, OrderOf);

    public IEdit SetGraph(string key, IReadOnlyList<double> numbers) =>
        EnfEdits.SetGraph(Doc, Block, key, numbers, OrderOf);

    public IEdit SetGraph(string key, IEnumerable<GraphKey> keys) =>
        EnfEdits.SetGraph(Doc, Block, key, GraphKeys.Texts(keys), OrderOf);

    public IEdit? Reset(string key) => EnfEdits.RemoveKey(Block, key);

    protected int? OrderOf(string key) => Schema.Find(Scope, key)?.Order;

    private static double? ParseNumber(string? text) =>
        text != null && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
}
