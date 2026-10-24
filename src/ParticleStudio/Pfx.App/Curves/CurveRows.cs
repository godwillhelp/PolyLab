namespace Pfx.App.Curves;

// Rows of the curve editor's lists, made again from the editor after each change (plain values, nothing to sync).
public sealed record CurveLayerRow(int Index, string Name, string Brush, string Count, string Mark, string MarkBrush,
    bool Shown, bool Active, string Tip)
{
    public string NameBrush => Active ? "B.TextHi" : "B.Text";
}

public sealed record CurveKeyRow(int Index, string Time, string Value, bool Selected, string ValueBrush, string TimeTip, string ValueTip)
{
    public string Number => (Index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
}
