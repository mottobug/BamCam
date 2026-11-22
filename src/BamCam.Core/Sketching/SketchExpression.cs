using System.Globalization;

namespace BamCam.Core.Sketching;

/// <summary>
/// Formulas of sketch dimensions and variables: numbers (decimal point; a plain number may have a decimal comma),
/// variable names, + − * / ^,
/// parentheses and the functions sqrt, abs, min, max, round, sin, cos, tan, asin, acos, atan (degrees) and the
/// constant pi.
/// </summary>
public static class SketchExpression
{
    /// <summary>
    /// Evaluates <paramref name="text"/>. <paramref name="variable"/> gives the value of a name (null when unknown).
    /// Throws <see cref="FormatException"/> with a readable message for a formula that cannot be evaluated.
    /// </summary>
    public static double Evaluate(string text, Func<string, double?> variable)
    {
        // A plain number may have a decimal comma ("12,5"); in formulas the comma separates function arguments.
        var trimmed = text.Trim();
        if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^[+-]?\d+,\d+$")) text = trimmed.Replace(',', '.');
        var p = new Parser(text, variable);
        var v = p.ParseSum();
        p.SkipSpaces();
        if (!p.AtEnd) throw new FormatException(Localization.Tr.F("Unexpected '{0}' in '{1}'.", p.Rest, text));
        if (double.IsNaN(v) || double.IsInfinity(v)) throw new FormatException(Localization.Tr.F("'{0}' gives no number.", text));
        return v;
    }

    /// <summary>The variable names a formula uses.</summary>
    public static IEnumerable<string> Names(string text)
    {
        var names = new List<string>();
        try
        {
            Evaluate(text, n => { names.Add(n); return 1; });
        }
        catch (FormatException)
        {
            // Names found up to the error are still returned.
        }
        return names.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Whether <paramref name="name"/> can be a variable name (a letter or _, then letters, digits or _).</summary>
    public static bool IsValidName(string name) =>
        name.Length > 0 && (char.IsLetter(name[0]) || name[0] == '_') && name.All(c => char.IsLetterOrDigit(c) || c == '_')
        && !Functions.Contains(name, StringComparer.OrdinalIgnoreCase) && !name.Equals("pi", StringComparison.OrdinalIgnoreCase);

    private static readonly string[] Functions = ["sqrt", "abs", "min", "max", "round", "sin", "cos", "tan", "asin", "acos", "atan"];

    /// <summary>
    /// The values of all variables, each evaluated with the others; a variable that refers to itself (directly or
    /// through others) or has an error is listed in <paramref name="errors"/> and left out.
    /// </summary>
    public static Dictionary<string, double> EvaluateVariables(IEnumerable<SketchVariable> variables, out Dictionary<string, string> errors)
    {
        var defs = variables.Where(v => !string.IsNullOrWhiteSpace(v.Name))
            .GroupBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Expression, StringComparer.OrdinalIgnoreCase);
        var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var errs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var busy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        double? Get(string name)
        {
            if (values.TryGetValue(name, out var known)) return known;
            if (!defs.TryGetValue(name, out var expr) || errs.ContainsKey(name)) return null;
            if (!busy.Add(name)) throw new FormatException(Localization.Tr.F("'{0}' refers to itself.", name));
            try
            {
                var v = Evaluate(expr, Get);
                values[name] = v;
                return v;
            }
            finally
            {
                busy.Remove(name);
            }
        }

        foreach (var name in defs.Keys)
        {
            try
            {
                if (Get(name) == null && !errs.ContainsKey(name)) errs[name] = Localization.Tr.F("'{0}' cannot be evaluated.", name);
            }
            catch (FormatException ex)
            {
                errs[name] = ex.Message;
            }
        }
        errors = errs;
        return values;
    }

    private sealed class Parser(string text, Func<string, double?> variable)
    {
        private int _i;

        public bool AtEnd => _i >= text.Length;
        public string Rest => text[_i..];

        public void SkipSpaces()
        {
            while (_i < text.Length && char.IsWhiteSpace(text[_i])) _i++;
        }

        private bool Take(char c)
        {
            SkipSpaces();
            if (_i < text.Length && text[_i] == c)
            {
                _i++;
                return true;
            }
            return false;
        }

        public double ParseSum()
        {
            var v = ParseProduct();
            while (true)
            {
                if (Take('+')) v += ParseProduct();
                else if (Take('-')) v -= ParseProduct();
                else return v;
            }
        }

        private double ParseProduct()
        {
            var v = ParseUnary();
            while (true)
            {
                if (Take('*')) v *= ParseUnary();
                else if (Take('/'))
                {
                    var d = ParseUnary();
                    if (d == 0) throw new FormatException(Localization.Tr.T("Division by zero."));
                    v /= d;
                }
                else return v;
            }
        }

        private double ParseUnary()
        {
            if (Take('-')) return -ParseUnary();
            if (Take('+')) return ParseUnary();
            return ParsePower();
        }

        private double ParsePower()
        {
            var b = ParseAtom();
            if (Take('^')) return Math.Pow(b, ParseUnary());
            return b;
        }

        private double ParseAtom()
        {
            SkipSpaces();
            if (AtEnd) throw new FormatException(Localization.Tr.F("'{0}' ends too early.", text));
            if (Take('('))
            {
                var v = ParseSum();
                if (!Take(')')) throw new FormatException(Localization.Tr.F("')' missing in '{0}'.", text));
                return v;
            }
            var c = text[_i];
            if (char.IsDigit(c) || c == '.')
            {
                var start = _i;
                while (_i < text.Length && (char.IsDigit(text[_i]) || text[_i] == '.'))
                    _i++;
                // An exponent: 1e-3.
                if (_i < text.Length && text[_i] is 'e' or 'E' && _i + 1 < text.Length && (char.IsDigit(text[_i + 1]) || text[_i + 1] is '-' or '+'))
                {
                    _i += 2;
                    while (_i < text.Length && char.IsDigit(text[_i])) _i++;
                }
                var s = text[start.._i];
                if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                    throw new FormatException(Localization.Tr.F("'{0}' is not a number.", s));
                return n;
            }
            if (char.IsLetter(c) || c == '_')
            {
                var start = _i;
                while (_i < text.Length && (char.IsLetterOrDigit(text[_i]) || text[_i] == '_')) _i++;
                var name = text[start.._i];
                if (Take('('))
                {
                    var args = new List<double> { ParseSum() };
                    while (Take(',') || Take(';')) args.Add(ParseSum());
                    if (!Take(')')) throw new FormatException(Localization.Tr.F("')' missing in '{0}'.", text));
                    return Call(name, args);
                }
                if (name.Equals("pi", StringComparison.OrdinalIgnoreCase)) return Math.PI;
                return variable(name) ?? throw new FormatException(Localization.Tr.F("Unknown variable '{0}'.", name));
            }
            throw new FormatException(Localization.Tr.F("Unexpected '{0}' in '{1}'.", Rest, text));
        }

        private static double Call(string name, List<double> a)
        {
            const double deg = Math.PI / 180;
            double One() => a.Count == 1 ? a[0] : throw new FormatException(Localization.Tr.F("{0} takes one value.", name));
            return name.ToLowerInvariant() switch
            {
                "sqrt" => Math.Sqrt(One()),
                "abs" => Math.Abs(One()),
                "round" => a.Count == 2 ? Math.Round(a[0], (int)a[1], MidpointRounding.AwayFromZero) : Math.Round(One(), MidpointRounding.AwayFromZero),
                "min" => a.Min(),
                "max" => a.Max(),
                "sin" => Math.Sin(One() * deg),
                "cos" => Math.Cos(One() * deg),
                "tan" => Math.Tan(One() * deg),
                "asin" => Math.Asin(One()) / deg,
                "acos" => Math.Acos(One()) / deg,
                "atan" => a.Count == 2 ? Math.Atan2(a[0], a[1]) / deg : Math.Atan(One()) / deg,
                _ => throw new FormatException(Localization.Tr.F("Unknown function '{0}'.", name)),
            };
        }
    }
}
