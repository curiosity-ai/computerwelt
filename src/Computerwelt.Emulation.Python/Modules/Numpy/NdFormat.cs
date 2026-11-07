using System.Globalization;
using System.Text;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules.Numpy;

/// <summary>
/// <c>repr</c> and <c>str</c> of an array, following <c>numpy/_core/arrayprint.py</c>.
/// </summary>
/// <remarks>
/// A notebook's output is mostly printed arrays, so this follows numpy's own algorithm
/// rather than approximating it: floats are printed with the fewest digits that identify
/// them, capped at eight decimals (<c>floatmode='maxprec'</c>), padded to a shared width
/// with the trailing point kept (<c>2.</c>), switched to scientific notation by numpy's
/// rule; rows wrap at 75 columns with the hanging indent; and arrays over 1000 elements are
/// summarized with <c>...</c>.
/// </remarks>
internal static class NdFormat
{
    private const int LineWidth = 75;
    private const int Threshold = 1000;
    private const int EdgeItems = 3;
    private const int Precision = 8;

    public static string Repr(NdArray array)
    {
        const string prefix = "array(";
        var skipDType = array.Kind is DKind.Float or DKind.Int or DKind.Bool && array.Size > 0;
        var suffix = skipDType ? ")" : ",";

        string body;

        if (array.Size > 0 || (array.NDim == 1 && array.Shape[0] == 0))
        {
            body = array.Size == 0 ? "[]"
                : array.NDim == 0
                ? Formatter(array, array.ToBoxed())(array.GetBoxed(array.Offset))
                : Format(array, LineWidth - suffix.Length, new string(' ', prefix.Length + 1), ", ");
        }
        else
        {
            body = "[], shape=" + ShapeRepr(array.Shape);
        }

        var text = prefix + body + suffix;

        if (skipDType)
        {
            return text;
        }

        var dtype = "dtype=" + array.Kind switch
        {
            DKind.Str => "'" + array.DType.Spelling + "'",
            DKind.Object => "object",
            _ => array.DType.Name,
        } + ")";

        var lastLine = text.Length - (text.LastIndexOf('\n') + 1);
        var spacer = lastLine + dtype.Length + 1 > LineWidth ? "\n" + new string(' ', prefix.Length) : " ";
        return text + spacer + dtype;
    }

    public static string Str(NdArray array)
    {
        if (array.NDim == 0)
        {
            return array.GetBoxed(array.Offset).Display();
        }

        return array.Size == 0 ? "[]" : Format(array, LineWidth, " ", " ");
    }

    private static string ShapeRepr(int[] shape) => NdArray.FormatShape(shape);

    private static string Format(NdArray array, int lineWidth, string nextLinePrefix, string separator)
    {
        var summarize = array.Size > Threshold;
        var formatter = Formatter(array, summarize ? Edges(array) : array.ToBoxed());
        return Recurse(array, formatter, nextLinePrefix, lineWidth, separator, summarize);
    }

    /// <summary>The elements a summarized print shows, which are what decide its widths.</summary>
    private static PyObject[] Edges(NdArray array)
    {
        var result = new List<PyObject>();
        Collect(array, result);
        return [.. result];

        static void Collect(NdArray a, List<PyObject> into)
        {
            if (a.NDim == 0)
            {
                into.Add(a.GetBoxed(a.Offset));
                return;
            }

            var n = a.Shape[0];
            var rows = n > 2 * EdgeItems
                ? Enumerable.Range(0, EdgeItems).Concat(Enumerable.Range(n - EdgeItems, EdgeItems))
                : Enumerable.Range(0, n);

            foreach (var i in rows)
            {
                Collect(a.View(a.Shape[1..], a.Strides[1..], a.Offset + (i * a.Strides[0])), into);
            }
        }
    }

    private static string Recurse(
        NdArray array,
        Func<PyObject, string> formatter,
        string hangingIndent,
        int currentWidth,
        string separator,
        bool summarize)
    {
        var axesLeft = array.NDim;

        if (axesLeft == 0)
        {
            return formatter(array.GetBoxed(array.Offset));
        }

        var nextHangingIndent = hangingIndent + " ";
        var nextWidth = currentWidth - 1;
        var length = array.Shape[0];
        var showSummary = summarize && 2 * EdgeItems < length;
        var leading = showSummary ? EdgeItems : 0;
        var trailing = showSummary ? EdgeItems : length;

        NdArray Sub(int i) => array.View(array.Shape[1..], array.Strides[1..], array.Offset + (i * array.Strides[0]));

        string Item(int i) => Recurse(Sub(i), formatter, nextHangingIndent, nextWidth, separator, summarize);

        var s = new StringBuilder();

        if (axesLeft == 1)
        {
            var elementWidth = currentWidth - Math.Max(separator.TrimEnd().Length, 1);
            var line = hangingIndent;

            for (var i = 0; i < leading; i++)
            {
                line = ExtendLine(s, line, Item(i), elementWidth, hangingIndent);
                line += separator;
            }

            if (showSummary)
            {
                line = ExtendLine(s, line, "...", elementWidth, hangingIndent);
                line += separator;
            }

            for (var i = trailing; i > 1; i--)
            {
                line = ExtendLine(s, line, Item(length - i), elementWidth, hangingIndent);
                line += separator;
            }

            line = ExtendLine(s, line, Item(length - 1), elementWidth, hangingIndent);
            s.Append(line);
        }
        else
        {
            var lineSeparator = separator.TrimEnd() + new string('\n', axesLeft - 1);

            for (var i = 0; i < leading; i++)
            {
                s.Append(hangingIndent).Append(Item(i)).Append(lineSeparator);
            }

            if (showSummary)
            {
                s.Append(hangingIndent).Append("...").Append(lineSeparator);
            }

            for (var i = trailing; i > 1; i--)
            {
                s.Append(hangingIndent).Append(Item(length - i)).Append(lineSeparator);
            }

            s.Append(hangingIndent).Append(Item(length - 1));
        }

        return "[" + s.ToString()[hangingIndent.Length..] + "]";
    }

    private static string ExtendLine(StringBuilder s, string line, string word, int lineWidth, string nextLinePrefix)
    {
        var needsWrap = line.Length + word.Length > lineWidth;

        // Wrapping a line that holds nothing but the indent would not help.
        if (line.Length <= nextLinePrefix.Length)
        {
            needsWrap = false;
        }

        if (needsWrap)
        {
            s.Append(line.TrimEnd()).Append('\n');
            line = nextLinePrefix;
        }

        return line + word;
    }

    /// <summary>Builds the element formatter for an array, sized over <paramref name="data"/>.</summary>
    private static Func<PyObject, string> Formatter(NdArray array, PyObject[] data)
    {
        switch (array.Kind)
        {
            case DKind.Bool:
            {
                var trueText = array.NDim == 0 ? "True" : " True";
                return value => value.IsTruthy() ? trueText : "False";
            }

            case DKind.Int:
            {
                var width = data.Length == 0 ? 0 : data.Max(static v => v.Repr().Length);
                return value => value.Repr().PadLeft(width);
            }

            case DKind.Float:
                return FloatFormatter([.. data.Select(static v => ((PyFloat)v).Value)]);

            default:
                return static value => value.Repr();
        }
    }

    /// <summary>numpy's <c>FloatingFormat</c> in its default <c>maxprec</c> mode.</summary>
    public static Func<PyObject, string> FloatFormatter(double[] data)
    {
        var finite = data.Where(double.IsFinite).ToArray();
        var nonZero = finite.Where(static v => v != 0).Select(Math.Abs).ToArray();
        var exponential = false;

        if (nonZero.Length > 0)
        {
            var max = nonZero.Max();
            var min = nonZero.Min();
            exponential = max >= 1e8 || min < 0.0001 || max / min > 1000;
        }

        int padLeft;
        int padRight;
        Func<double, string> format;

        if (finite.Length == 0)
        {
            padLeft = 0;
            padRight = 0;
            format = static _ => string.Empty;
        }
        else if (exponential)
        {
            var parts = finite.Select(v => Scientific(v, Precision, minDigits: 0)).ToArray();
            var mantissas = parts.Select(static p => p[..p.IndexOf('e', StringComparison.Ordinal)]).ToArray();
            var exponentDigits = parts.Max(static p => p.Length - p.IndexOf('e', StringComparison.Ordinal) - 2);
            var precision = mantissas.Max(static m => m.Length - m.IndexOf('.', StringComparison.Ordinal) - 1);
            padLeft = mantissas.Max(static m => m.IndexOf('.', StringComparison.Ordinal));
            padRight = exponentDigits + 2 + precision;

            var left = padLeft;
            format = v =>
            {
                var text = Scientific(v, precision, minDigits: precision, exponentDigits);
                var point = text.IndexOf('.', StringComparison.Ordinal);
                return new string(' ', Math.Max(0, left - point)) + text;
            };
        }
        else
        {
            var parts = finite.Select(static v => Positional(v, Precision)).ToArray();
            padLeft = parts.Max(static p => p.IndexOf('.', StringComparison.Ordinal));
            padRight = parts.Max(static p => p.Length - p.IndexOf('.', StringComparison.Ordinal) - 1);
            var (left, right) = (padLeft, padRight);

            format = v =>
            {
                var text = Positional(v, Precision);
                var point = text.IndexOf('.', StringComparison.Ordinal);
                var fraction = text.Length - point - 1;
                return new string(' ', Math.Max(0, left - point)) + text + new string(' ', Math.Max(0, right - fraction));
            };
        }

        // Non-finite values may need more room than the numbers do.
        if (data.Length != finite.Length)
        {
            var negativeInfinity = data.Any(static v => double.IsNegativeInfinity(v));
            var offset = padRight + 1;
            padLeft = Math.Max(padLeft, Math.Max(3 - offset, 3 + (negativeInfinity ? 1 : 0) - offset));
        }

        var (finalLeft, finalRight) = (padLeft, padRight);

        return value =>
        {
            var v = ((PyFloat)value).Value;

            if (!double.IsFinite(v))
            {
                var text = double.IsNaN(v) ? "nan" : v < 0 ? "-inf" : "inf";
                return new string(' ', Math.Max(0, finalLeft + finalRight + 1 - text.Length)) + text;
            }

            var formatted = format(v);
            var point = formatted.IndexOf('.', StringComparison.Ordinal);

            // A wider integer pad than the format computed (for nan/inf) shifts everything.
            var integerWidth = point;
            return integerWidth < finalLeft ? new string(' ', finalLeft - integerWidth) + formatted : formatted;
        };
    }

    /// <summary>
    /// The shortest digits that round-trip, as (digits, exponent of the first digit).
    /// </summary>
    private static (string Digits, int Exponent) Shortest(double value)
    {
        var text = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);
        var exponent = 0;
        var e = text.IndexOf('E', StringComparison.Ordinal);

        if (e >= 0)
        {
            exponent = int.Parse(text[(e + 1)..], CultureInfo.InvariantCulture);
            text = text[..e];
        }

        var point = text.IndexOf('.', StringComparison.Ordinal);
        var digits = point < 0 ? text : text.Remove(point, 1);
        var integerLength = point < 0 ? text.Length : point;
        var leadingZeros = digits.Length - digits.TrimStart('0').Length;
        digits = digits.TrimStart('0').TrimEnd('0');

        if (digits.Length == 0)
        {
            return ("0", 0);
        }

        return (digits, exponent + integerLength - 1 - leadingZeros);
    }

    /// <summary>
    /// <c>dragon4_positional(x, precision, unique=True, trim='.')</c>: the shortest digits,
    /// cut to <paramref name="precision"/> decimals, trailing zeros dropped but the point kept.
    /// </summary>
    public static string Positional(double value, int precision)
    {
        var sign = double.IsNegative(value) ? "-" : string.Empty;
        var (digits, exponent) = Shortest(value);
        string body;

        if (digits == "0")
        {
            body = "0.";
        }
        else if (digits.Length - 1 - exponent > precision)
        {
            body = Math.Abs(value).ToString("F" + precision.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)
                .TrimEnd('0');

            if (!body.Contains('.', StringComparison.Ordinal))
            {
                body += ".";
            }
        }
        else if (exponent < 0)
        {
            body = "0." + new string('0', -exponent - 1) + digits;
        }
        else
        {
            var whole = digits.PadRight(exponent + 1, '0');
            body = whole[..(exponent + 1)] + "." + whole[(exponent + 1)..];
        }

        // Rounding to the precision can leave nothing but zeros: `-0.` keeps its sign in numpy.
        return sign + body;
    }

    /// <summary>
    /// <c>dragon4_scientific</c>: <c>d.ddde±XX</c>, with at most <paramref name="precision"/>
    /// mantissa decimals, padded to <paramref name="minDigits"/>.
    /// </summary>
    public static string Scientific(double value, int precision, int minDigits, int exponentDigits = 2)
    {
        var sign = double.IsNegative(value) ? "-" : string.Empty;
        var (digits, exponent) = Shortest(value);

        if (digits.Length - 1 > precision)
        {
            var text = Math.Abs(value).ToString("E" + precision.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            var e = text.IndexOf('E', StringComparison.Ordinal);
            exponent = int.Parse(text[(e + 1)..], CultureInfo.InvariantCulture);
            digits = text[..e].Replace(".", string.Empty, StringComparison.Ordinal).TrimEnd('0');

            if (digits.Length == 0)
            {
                digits = "0";
            }
        }

        var fraction = digits[1..];

        if (fraction.Length < minDigits)
        {
            fraction = fraction.PadRight(minDigits, '0');
        }

        var exponentText = Math.Abs(exponent).ToString(CultureInfo.InvariantCulture).PadLeft(exponentDigits, '0');
        return $"{sign}{digits[0]}.{fraction}e{(exponent < 0 ? "-" : "+")}{exponentText}";
    }
}
