using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules;

/// <summary>
/// <c>__format__</c> for <c>Fraction</c> and <c>Decimal</c>, which format their exact values
/// rather than a float's — so <c>f'{Fraction(1, 3):.30f}'</c> has thirty correct digits.
/// </summary>
/// <remarks>
/// Each follows its own CPython source: <c>fractions.py</c>'s <c>_format_general</c> and
/// <c>_format_float_style</c>, and <c>_pydecimal</c>'s <c>__format__</c> with its specifier
/// parser, which differ in small ways (a Decimal's exponent is not padded to two digits).
/// </remarks>
internal static partial class NumericFormat
{
    [GeneratedRegex(@"\A(?:(?<fill>.)?(?<align>[<>=^]))?(?<sign>[-+ ]?)(?<alt>\#)?(?<width>0|[1-9][0-9]*)?(?<sep>[,_])?\z", RegexOptions.Singleline)]
    private static partial Regex FractionGeneral();

    [GeneratedRegex(@"\A(?:(?<fill>.)?(?<align>[<>=^]))?(?<sign>[-+ ]?)(?<z>z)?(?<alt>\#)?(?<zeropad>0(?=[0-9]))?(?<width>0|[1-9][0-9]*)?(?<sep>[,_])?(?:\.(?<prec>0|[1-9][0-9]*))?(?<type>[eEfFgG%])\z", RegexOptions.Singleline)]
    private static partial Regex FractionFloat();

    [GeneratedRegex(@"\A(?:(?<fill>.)?(?<align>[<>=^]))?(?<sign>[-+ ])?(?<z>z)?(?<alt>\#)?(?<zeropad>0)?(?<width>(?!0)[0-9]+)?(?<sep>[,_])?(?:\.(?<prec>0|(?!0)[0-9]+))?(?<type>[eEfFgGn%])?\z", RegexOptions.Singleline)]
    private static partial Regex DecimalSpec();

    /// <summary>Inserts a separator every three digits from the right.</summary>
    public static string Group(string digits, string separator)
    {
        if (separator.Length == 0 || digits.Length <= 3)
        {
            return digits;
        }

        var first = 1 + ((digits.Length - 1) % 3);
        var builder = new StringBuilder(digits[..first]);

        for (var i = first; i < digits.Length; i += 3)
        {
            builder.Append(separator).Append(digits, i, 3);
        }

        return builder.ToString();
    }

    private static string Pad(string sign, string body, string fill, string align, int width)
    {
        var count = Math.Max(0, width - sign.Length - body.Length);
        var padding = new StringBuilder().Insert(0, fill, count).ToString();

        switch (align)
        {
            case "<": return sign + body + padding;
            case "^":
                var half = count / 2;
                return padding[..(half * fill.Length)] + sign + body + padding[(half * fill.Length)..];
            case "=": return sign + padding + body;
            default: return padding + sign + body;
        }
    }

    // ---- Fraction ----

    public static string FormatFraction(PyFraction value, string spec)
    {
        if (FractionGeneral().Match(spec) is { Success: true } general)
        {
            var fill = general.Groups["fill"].Success ? general.Groups["fill"].Value : " ";
            var align = general.Groups["align"].Success ? general.Groups["align"].Value : ">";
            var positive = general.Groups["sign"].Value == "-" ? string.Empty : general.Groups["sign"].Value;
            var width = general.Groups["width"].Success ? int.Parse(general.Groups["width"].Value, CultureInfo.InvariantCulture) : 0;
            var body = value.Body(general.Groups["sep"].Value, general.Groups["alt"].Success);
            var sign = value.Numerator.Sign < 0 ? "-" : positive;
            return Pad(sign, body, fill, align, width);
        }

        if (FractionFloat().Match(spec) is { Success: true } match
            && (!match.Groups["align"].Success || !match.Groups["zeropad"].Success))
        {
            return FractionFloatStyle(value, match);
        }

        throw new PyRaise(PyErrors.ValueError(
            $"Invalid format specifier {PyStr.Quote(spec)} for object of type 'Fraction'"));
    }

    private static string FractionFloatStyle(PyFraction value, Match match)
    {
        var fill = match.Groups["fill"].Success ? match.Groups["fill"].Value : " ";
        var align = match.Groups["align"].Success ? match.Groups["align"].Value : ">";
        var positive = match.Groups["sign"].Value == "-" ? string.Empty : match.Groups["sign"].Value;
        var noNegativeZero = match.Groups["z"].Success;
        var alternate = match.Groups["alt"].Success;
        var zeropad = match.Groups["zeropad"].Success;
        var width = match.Groups["width"].Success ? int.Parse(match.Groups["width"].Value, CultureInfo.InvariantCulture) : 0;
        var separator = match.Groups["sep"].Value;
        var precision = match.Groups["prec"].Success ? int.Parse(match.Groups["prec"].Value, CultureInfo.InvariantCulture) : 6;
        var type = match.Groups["type"].Value[0];
        var trimZeros = type is 'g' or 'G' && !alternate;
        var trimPoint = !alternate;
        var indicator = type is 'E' or 'F' or 'G' ? "E" : "e";

        if (align == "=" && fill == "0")
        {
            zeropad = true;
        }

        bool negative;
        BigInteger significand;
        int pointPosition;
        string suffix;

        if (type is 'f' or 'F' or '%')
        {
            var exponent = -precision - (type == '%' ? 2 : 0);
            (negative, significand) = PyFraction.RoundToExponent(value.Numerator, value.Denominator, exponent, noNegativeZero);
            pointPosition = precision;
            suffix = type == '%' ? "%" : string.Empty;
        }
        else
        {
            var figures = type is 'g' or 'G' ? Math.Max(precision, 1) : precision + 1;
            (negative, significand, var exponent) = PyFraction.RoundToFigures(value.Numerator, value.Denominator, figures);
            var scientific = type is 'e' or 'E' || exponent > 0 || exponent + figures <= -4;
            pointPosition = scientific ? figures - 1 : -exponent;
            var shown = exponent + pointPosition;
            suffix = scientific
                ? indicator + (shown < 0 ? "-" : "+") + Math.Abs(shown).ToString("00", CultureInfo.InvariantCulture)
                : string.Empty;
        }

        var digits = significand.ToString(CultureInfo.InvariantCulture).PadLeft(pointPosition + 1, '0');
        var sign = negative ? "-" : positive;
        var leading = digits[..(digits.Length - pointPosition)];
        var fraction = digits[(digits.Length - pointPosition)..];

        if (trimZeros)
        {
            fraction = fraction.TrimEnd('0');
        }

        var trailing = (trimPoint && fraction.Length == 0 ? string.Empty : ".") + fraction + suffix;

        if (zeropad)
        {
            var minimum = width - sign.Length - trailing.Length;
            var target = separator.Length > 0 ? (3 * minimum / 4) + 1 : minimum;
            leading = leading.PadLeft(Math.Max(target, leading.Length), '0');
        }

        leading = Group(leading, separator);
        return Pad(sign, leading + trailing, fill, align, width);
    }

    // ---- Decimal ----

    public static string FormatDecimal(PyDecimal value, string spec, DecimalContext context)
    {
        var match = DecimalSpec().Match(spec);

        if (!match.Success)
        {
            throw new PyRaise(PyErrors.ValueError($"Invalid format specifier: {spec}"));
        }

        var zeropad = match.Groups["zeropad"].Success;

        if (zeropad && match.Groups["fill"].Success)
        {
            throw new PyRaise(PyErrors.ValueError($"Fill character conflicts with '0' in format specifier: {spec}"));
        }

        if (zeropad && match.Groups["align"].Success)
        {
            throw new PyRaise(PyErrors.ValueError($"Alignment conflicts with '0' in format specifier: {spec}"));
        }

        var fill = zeropad ? "0" : match.Groups["fill"].Success ? match.Groups["fill"].Value : " ";
        var align = zeropad ? "=" : match.Groups["align"].Success ? match.Groups["align"].Value : ">";
        var signOption = match.Groups["sign"].Success ? match.Groups["sign"].Value : "-";
        var width = match.Groups["width"].Success ? int.Parse(match.Groups["width"].Value, CultureInfo.InvariantCulture) : 0;
        var separator = match.Groups["sep"].Value;
        int? precision = match.Groups["prec"].Success ? int.Parse(match.Groups["prec"].Value, CultureInfo.InvariantCulture) : null;
        var type = match.Groups["type"].Success ? match.Groups["type"].Value[0] : '\0';
        var alternate = match.Groups["alt"].Success;

        if (precision == 0 && type is '\0' or 'g' or 'G' or 'n')
        {
            precision = 1;
        }

        if (value.IsSpecial)
        {
            var text = value.IsInfinite ? "Infinity" : value.Display().TrimStart('-');
            text = value.IsInfinite ? "Infinity" : text;
            var specialSign = value.Negative ? "-" : signOption is "+" or " " ? signOption : string.Empty;

            if (type == '%')
            {
                text += "%";
            }

            return Pad(specialSign, text, fill, align, width);
        }

        if (type == '\0')
        {
            type = context.Capitals ? 'G' : 'g';
        }

        if (type == 'n')
        {
            type = 'g';
        }

        var working = value;

        if (type == '%')
        {
            working = working.WithExponent(working.Exponent + 2);
        }

        var rounding = context.Rounding;

        if (precision is { } p)
        {
            if (type is 'e' or 'E')
            {
                working = working.RoundToDigits(p + 1, rounding);
            }
            else if (type is 'f' or 'F' or '%')
            {
                working = working.Rescale(-p, rounding);
            }
            else if (type is 'g' or 'G' && PyDecimal.DigitCount(working.Coefficient) > p)
            {
                working = working.RoundToDigits(p, rounding);
            }
        }

        if (working.Coefficient.IsZero && working.Exponent > 0 && type is 'f' or 'F' or '%')
        {
            working = working.Rescale(0, rounding);
        }

        var negative = working.Negative && !(working.Coefficient.IsZero && match.Groups["z"].Success);
        var coefficient = working.Coefficient.ToString(CultureInfo.InvariantCulture);
        var leftDigits = working.Exponent + coefficient.Length;
        long dotPlace;

        if (type is 'e' or 'E')
        {
            dotPlace = working.Coefficient.IsZero && precision is { } q ? 1 - q : 1;
        }
        else if (type is 'f' or 'F' or '%')
        {
            dotPlace = leftDigits;
        }
        else
        {
            dotPlace = working.Exponent <= 0 && leftDigits > -6 ? leftDigits : 1;
        }

        string integerPart;
        string fractionPart;

        if (dotPlace < 0)
        {
            integerPart = "0";
            fractionPart = new string('0', (int)-dotPlace) + coefficient;
        }
        else if (dotPlace > coefficient.Length)
        {
            integerPart = coefficient + new string('0', (int)(dotPlace - coefficient.Length));
            fractionPart = string.Empty;
        }
        else
        {
            integerPart = dotPlace == 0 ? "0" : coefficient[..(int)dotPlace];
            fractionPart = coefficient[(int)dotPlace..];
        }

        var exponent = leftDigits - dotPlace;
        var sign = negative ? "-" : signOption is "+" or " " ? signOption : string.Empty;

        if (fractionPart.Length > 0 || alternate)
        {
            fractionPart = "." + fractionPart;
        }

        if (exponent != 0 || type is 'e' or 'E')
        {
            var indicator = type is 'E' or 'G' ? "E" : "e";
            fractionPart += indicator + (exponent < 0 ? "-" : "+") + Math.Abs(exponent).ToString(CultureInfo.InvariantCulture);
        }

        if (type == '%')
        {
            fractionPart += "%";
        }

        var minimumWidth = zeropad ? width - fractionPart.Length - sign.Length : 0;
        integerPart = InsertThousands(integerPart, separator, minimumWidth);
        return Pad(sign, integerPart + fractionPart, fill, align, width);
    }

    /// <summary><c>_pydecimal._insert_thousands_sep</c>: groups of three, zero-filled to a width.</summary>
    private static string InsertThousands(string digits, string separator, int minimumWidth)
    {
        var groups = new List<string>();

        while (true)
        {
            var length = Math.Min(Math.Max(Math.Max(digits.Length, minimumWidth), 1), 3);
            groups.Add(new string('0', Math.Max(0, length - digits.Length)) + digits[Math.Max(0, digits.Length - length)..]);
            digits = digits[..Math.Max(0, digits.Length - length)];
            minimumWidth -= length;

            if (digits.Length == 0 && minimumWidth <= 0)
            {
                break;
            }

            minimumWidth -= separator.Length;
        }

        groups.Reverse();
        return string.Join(separator, groups);
    }
}
