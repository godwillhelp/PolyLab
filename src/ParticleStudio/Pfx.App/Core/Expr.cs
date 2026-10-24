using System.Globalization;

namespace Pfx.App.Core;

// What a number field accepts (spec 5.2): a number, an expression (0.5*2, (1+2)/4), a relative change of the current
// value (+=0.1, -=1, *=2, /=4, *1.5) or a percentage (+10%, -25%). A comma counts as the decimal point.
public static class Expr
{
    public static bool TryApply(string text, double current, out double result)
    {
        result = current;
        string t = text.Trim().Replace(',', '.');
        if (t.Length == 0)
            return false;
        try
        {
            if (t.Length > 2 && t[1] == '=' && "+-*/".Contains(t[0]))
            {
                double w = Eval(t[2..]);
                result = t[0] switch { '+' => current + w, '-' => current - w, '*' => current * w, _ => current / w };
            }
            else if (t.EndsWith('%') && (t[0] == '+' || t[0] == '-'))
            {
                double p = double.Parse(t[1..^1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture) / 100;
                result = current * (t[0] == '+' ? 1 + p : 1 - p);
            }
            else if (t[0] == '*')
            {
                result = current * Eval(t[1..]);
            }
            else
            {
                result = Eval(t);
            }
        }
        catch (FormatException)
        {
            return false;
        }
        return double.IsFinite(result);
    }

    public static double Eval(string text)
    {
        var p = new Parser(text.Replace(" ", ""));
        double v = p.Sum();
        if (p.Pos != p.Text.Length)
            throw new FormatException(text);
        return v;
    }

    private sealed class Parser
    {
        public readonly string Text;
        public int Pos;

        public Parser(string text) => Text = text;

        private char Peek => Pos < Text.Length ? Text[Pos] : '\0';

        public double Sum()
        {
            double v = Product();
            while (Peek is '+' or '-')
            {
                char op = Text[Pos++];
                double w = Product();
                v = op == '+' ? v + w : v - w;
            }
            return v;
        }

        private double Product()
        {
            double v = Factor();
            while (Peek is '*' or '/')
            {
                char op = Text[Pos++];
                double w = Factor();
                v = op == '*' ? v * w : v / w;
            }
            return v;
        }

        private double Factor()
        {
            if (Peek == '-') { Pos++; return -Factor(); }
            if (Peek == '+') { Pos++; return Factor(); }
            if (Peek == '(')
            {
                Pos++;
                double v = Sum();
                if (Peek != ')')
                    throw new FormatException(Text);
                Pos++;
                return v;
            }
            int start = Pos;
            while (Pos < Text.Length && (char.IsDigit(Text[Pos]) || Text[Pos] == '.' || Text[Pos] is 'e' or 'E'
                   || ((Text[Pos] is '+' or '-') && Pos > start && Text[Pos - 1] is 'e' or 'E')))
                Pos++;
            if (Pos == start)
                throw new FormatException(Text);
            return double.Parse(Text[start..Pos], NumberStyles.Float, CultureInfo.InvariantCulture);
        }
    }
}
