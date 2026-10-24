using System.Globalization;
using Pfx.Core.Editing;

namespace Pfx.Core.Model;

// One key of a graph: its time and channel values, each with the text the file wrote it with (null = a new or changed
// number, written by EnfEdits.Number).
public sealed record GraphKey(double[] Values, string?[] Texts)
{
    public double Time => Values[0];

    public static GraphKey New(params double[] values) => new(values, new string?[values.Length]);

    // A copy with number i set; the text of an unchanged number is kept.
    public GraphKey With(int i, double value)
    {
        if (Values[i].Equals(value))
            return this;
        var v = (double[])Values.Clone();
        var t = (string?[])Texts.Clone();
        v[i] = value;
        t[i] = null;
        return new GraphKey(v, t);
    }
}

// The keys of a graph block ("t v .." or "t r g b .."), read with their texts so an edit keeps the spelling of every
// number it does not touch: a drag of one key never rewrites the others (W0: Workbench rounds every graph to 3
// decimals on save; we keep what the file says).
public static class GraphKeys
{
    public static List<GraphKey> Read(KeyedBlock block, string key, int channels)
    {
        var raw = block.Raw(key) ?? Array.Empty<string>();
        int stride = channels + 1;
        var keys = new List<GraphKey>();
        for (int k = 0; k + stride <= raw.Count; k += stride)
        {
            var values = new double[stride];
            var texts = new string?[stride];
            bool ok = true;
            for (int c = 0; c < stride; c++)
            {
                ok &= double.TryParse(raw[k + c], NumberStyles.Float, CultureInfo.InvariantCulture, out values[c]);
                texts[c] = raw[k + c];
            }
            if (ok)
                keys.Add(new GraphKey(values, texts));
        }
        return keys;
    }

    public static List<string> Texts(IEnumerable<GraphKey> keys) =>
        keys.SelectMany(k => k.Values.Select((v, i) => k.Texts[i] ?? EnfEdits.Number(v))).ToList();

    public static double[] Numbers(IEnumerable<GraphKey> keys) => keys.SelectMany(k => k.Values).ToArray();
}
