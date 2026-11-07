using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules.Pandas;

/// <summary>
/// Text and HTML rendering, following pandas' formatters (<c>pandas/io/formats/format.py</c>):
/// the per-dtype value formatters, the float trimming, the column justification, truncation
/// to the first and last five rows past sixty, and wrapping at eighty columns.
/// </summary>
internal static partial class PdFormat
{
    // The display options, read from the run's `pd.set_option` values; None means no limit.
    private static int? Option(string key, int fallback) => PdRuntime.Current is { } runtime ? runtime.IntOption(key, fallback) : fallback;

    private static int? MaxRows => Option("display.max_rows", 60);

    private static int MinRows => Option("display.min_rows", 10) ?? MaxRows ?? 10;

    private static int? MaxColumns => Option("display.max_columns", 20);

    private static int? LineWidth => Option("display.width", 80);

    private static int Precision => Math.Clamp(Option("display.precision", 6) ?? 6, 0, 30);

    /// <summary>
    /// The strings pandas shows for a column's values, each with the leading space numeric
    /// formatters reserve for a sign, not yet justified.
    /// </summary>
    public static string[] Values(PdColumn column, bool leadingSpace = true)
    {
        var values = column.Values;
        var lead = leadingSpace ? " " : string.Empty;

        switch (column.Type)
        {
            case DType.Int64:
                return [.. values.Select(v => ((PyInt)v).Value.Sign < 0 ? Pd.Text(v) : lead + Pd.Text(v))];

            case DType.Float64:
                return Floats(column.Doubles(), leadingSpace);

            case DType.Bool:
                return [.. values.Select(v => lead + v.Repr())];

            case DType.DateTime:
            {
                var dateOnly = values.All(static v => v is not PdTimestamp t || t.IsMidnight);
                return [.. values.Select(v => v is PdTimestamp t ? t.Text(dateOnly) : "NaT")];
            }

            case DType.TimeDelta:
            {
                var daysOnly = values.All(static v => v is not PyDelta d || (d.Seconds == 0 && d.Microseconds == 0));
                return [.. values.Select(v => v is PyDelta d ? PdTimestamp.DeltaText(d, daysOnly) : "NaT")];
            }

            default:
                return [.. values.Select(v => v switch
                {
                    PyFloat number when !double.IsNaN(number.Value) => TrimSingle(Pd.Fixed(number.Value, Precision), leadingSpace),
                    _ => lead + Pprint(v),
                })];
        }
    }

    /// <summary>One float in an object column: fixed point, trailing zeros trimmed.</summary>
    private static string TrimSingle(string text, bool leadingSpace)
    {
        if (!text.StartsWith('-') && leadingSpace)
        {
            text = " " + text;
        }

        text = text.TrimEnd('0');
        return text.EndsWith('.') ? text + "0" : text;
    }

    /// <summary>A value as <c>pprint_thing</c> shows it inside a table.</summary>
    public static string Pprint(PyObject value) => value switch
    {
        PyNone => "None",
        PyFloat number when double.IsNaN(number.Value) => "NaN",
        PdNA => "<NA>",
        PyStr text => Escape(text.Value),
        PdTimestamp moment => moment.Display(),
        _ => Escape(value.Display()),
    };

    private static string Escape(string text) =>
        text.Contains('\t') || text.Contains('\n') || text.Contains('\r')
            ? text.Replace("\t", "\\t", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal)
            : text;

    /// <summary>pandas' <c>FloatArrayFormatter</c>: six decimals, then trailing zeros trimmed in step.</summary>
    public static string[] Floats(double[] values, bool leadingSpace)
    {
        string Fixed(double v) => double.IsNaN(v)
            ? "NaN"
            : (leadingSpace && !(v < 0 || (v == 0 && double.IsNegative(v))) ? " " : string.Empty) + FixedText(v);

        string Scientific(double v) => double.IsNaN(v)
            ? "NaN"
            : (leadingSpace && !(v < 0) ? " " : string.Empty) + ScientificText(v);

        var formatted = values.Select(Fixed).ToArray();
        var finite = values.Where(static v => !double.IsNaN(v)).ToArray();
        var tooLong = formatted.Length > 0 && formatted.Max(static f => f.Length) > Precision + 6;
        var hasLarge = finite.Any(static v => Math.Abs(v) > 1e6);
        var hasSmall = finite.Any(static v => Math.Abs(v) < 1e-6 && v != 0);

        if (hasSmall || (tooLong && hasLarge))
        {
            return [.. values.Select(Scientific)];
        }

        return TrimZeros(formatted);

        static string FixedText(double v) => double.IsPositiveInfinity(v) ? "inf" : double.IsNegativeInfinity(v) ? "-inf" : Pd.Fixed(v, Precision);

        static string ScientificText(double v) => double.IsPositiveInfinity(v) ? "inf" : double.IsNegativeInfinity(v) ? "-inf" : Pd.Scientific(v, Precision);
    }

    [GeneratedRegex(@"^\s*[\+-]?[0-9]+\.[0-9]*$")]
    private static partial Regex NumberWithDecimal();

    /// <summary>pandas' <c>_trim_zeros_float</c>: drop a trailing zero from every number while all have one.</summary>
    private static string[] TrimZeros(string[] values)
    {
        var trimmed = values.ToArray();

        bool ShouldTrim()
        {
            var numbers = trimmed.Where(static t => NumberWithDecimal().IsMatch(t)).ToList();
            return numbers.Count > 0 && numbers.All(static n => n.EndsWith('0'));
        }

        while (ShouldTrim())
        {
            for (var i = 0; i < trimmed.Length; i++)
            {
                if (NumberWithDecimal().IsMatch(trimmed[i]))
                {
                    trimmed[i] = trimmed[i][..^1];
                }
            }
        }

        for (var i = 0; i < trimmed.Length; i++)
        {
            if (NumberWithDecimal().IsMatch(trimmed[i]) && trimmed[i].EndsWith('.'))
            {
                trimmed[i] += "0";
            }
        }

        return trimmed;
    }

    /// <summary>The strings an index shows in a table, without the numeric leading space.</summary>
    public static string[] Labels(PdIndex index)
    {
        if (index.Labels.Type == DType.Float64)
        {
            return [.. Floats(index.Labels.Doubles(), leadingSpace: false)];
        }

        if (index.Labels.Type is DType.DateTime or DType.TimeDelta)
        {
            return Values(index.Labels, leadingSpace: false);
        }

        return [.. index.Labels.Values.Select(v => LabelText(v, index.Labels.Type))];
    }

    /// <summary>A float label as an Index repr lists it: <c>1.0</c>, <c>2.5</c>, <c>nan</c>.</summary>
    public static string FloatLabel(double value) => PyFloat.Format(value);

    /// <summary>One label as a table shows it.</summary>
    public static string LabelText(PyObject label, DType type) => label switch
    {
        PyFloat number when type == DType.Float64 => Floats([number.Value], false)[0],
        PyNone => "None",
        PyFloat number when double.IsNaN(number.Value) => "NaN",
        PyStr text => Escape(text.Value),
        PdTimestamp moment => moment.Text(moment.IsMidnight),
        _ => Escape(label.Display()),
    };

    /// <summary>The text <c>astype(str)</c> gives a value.</summary>
    public static string StrOf(PyObject value, DType type) => value switch
    {
        PdTimestamp moment => moment.Display(),
        PyFloat number => Pd.FloatText(number.Value),
        PdNaT => "NaT",
        _ => value.Display(),
    };

    private static string PadLeft(string text, int width) => text.Length >= width ? text : new string(' ', width - text.Length) + text;

    private static string PadRight(string text, int width) => text.Length >= width ? text : text + new string(' ', width - text.Length);

    /// <summary>pandas' <c>adjoin</c>: columns side by side, each padded, with spaces between.</summary>
    public static string Adjoin(int space, IReadOnlyList<IReadOnlyList<string>> lists)
    {
        if (lists.Count == 0)
        {
            return string.Empty;
        }

        var lengths = lists.Select((list, i) => (list.Count == 0 ? 0 : list.Max(static x => x.Length)) + (i < lists.Count - 1 ? space : 0)).ToList();
        var rows = lists.Max(static l => l.Count);
        var builder = new StringBuilder();

        for (var r = 0; r < rows; r++)
        {
            if (r > 0)
            {
                builder.Append('\n');
            }

            for (var c = 0; c < lists.Count; c++)
            {
                var list = lists[c];
                var offset = rows - list.Count;
                var cell = r < offset ? string.Empty : list[r - offset];
                builder.Append(PadRight(cell, lengths[c]));
            }
        }

        return builder.ToString();
    }

    /// <summary>The index as one or more left-justified columns of text, sparsified per level.</summary>
    private static List<List<string>> IndexLevels(PdIndex index, bool includeNames)
    {
        if (index.IsMulti)
        {
            var levels = index.Names!.Count;
            var result = new List<List<string>>();

            for (var level = 0; level < levels; level++)
            {
                var labels = index.Labels.Values.Select(v => v is PyTuple t && level < t.Items.Count ? t.Items[level] : PyNone.Instance).ToList();
                var column = PdColumn.Infer(labels);
                var text = column.Type == DType.Float64 ? Floats(column.Doubles(), false).ToList() : labels.Select(l => LabelText(l, column.Type)).ToList();
                var cells = new List<string>();

                if (includeNames)
                {
                    cells.Add(index.Names[level] is PyNone ? string.Empty : Pd.Text(index.Names[level]));
                }

                cells.AddRange(text);
                result.Add(cells);
            }

            Sparsify(result, includeNames ? 1 : 0);
            return result;
        }

        var flat = new List<string>();

        if (includeNames)
        {
            flat.Add(index.Name is PyNone ? string.Empty : Pd.Text(index.Name));
        }

        flat.AddRange(Labels(index));
        return [flat];
    }

    /// <summary>pandas' <c>sparsify_labels</c>: blank an upper level repeated from the row above.</summary>
    private static void Sparsify(List<List<string>> levels, int start)
    {
        var k = levels.Count;
        var rows = levels[0].Count;
        var original = levels.Select(static l => l.ToList()).ToList();

        for (var r = start + 1; r < rows; r++)
        {
            for (var i = 0; i < k - 1; i++)
            {
                if (original[i][r - 1] == original[i][r])
                {
                    levels[i][r] = string.Empty;
                }
                else
                {
                    break;
                }
            }
        }
    }

    private static bool HasNames(PdIndex index) => index.LevelNames.Any(static n => n is not PyNone);

    private static List<string> Justified(IEnumerable<string> cells, bool left)
    {
        var list = cells.ToList();
        var width = list.Count == 0 ? 0 : list.Max(static c => c.Length);
        return [.. list.Select(c => left ? PadRight(c, width) : PadLeft(c, width))];
    }

    /// <summary>The rows shown: all of them, or the first and last five past sixty.</summary>
    private static (List<int> Rows, int Cut) VisibleRows(int count, bool truncate)
    {
        if (!truncate || MaxRows is not { } maxRows || count <= maxRows)
        {
            return ([.. Enumerable.Range(0, count)], -1);
        }

        var half = Math.Max(1, Math.Min(MinRows, maxRows) / 2);
        return ([.. Enumerable.Range(0, half), .. Enumerable.Range(count - half, half)], half);
    }

    /// <summary><c>repr(series)</c>.</summary>
    public static string Series(PdSeries series, bool truncate = true)
    {
        var footer = SeriesFooter(series, truncate && series.Count > (MaxRows ?? int.MaxValue));

        if (series.Count == 0)
        {
            return $"Series([], {footer})";
        }

        var (rows, cut) = VisibleRows(series.Count, truncate);
        var index = series.Index.Take(rows);
        var data = series.Data.Take(rows);
        var haveHeader = HasNames(series.Index);

        List<string> fmtIndex;

        if (index.IsMulti)
        {
            fmtIndex = [.. Adjoin(2, IndexLevels(index, includeNames: true)).Split('\n')];
        }
        else
        {
            fmtIndex = IndexLevels(index, includeNames: true)[0];
        }

        var values = Justified(Values(data), left: false);

        if (cut >= 0)
        {
            var width = values[cut - 1].Length;
            values.Insert(cut, Pd.Center(width > 3 ? "..." : "..", width));
            fmtIndex.Insert(cut + 1, string.Empty);
        }

        var result = Adjoin(3, [fmtIndex.Skip(1).ToList(), values]);

        if (haveHeader)
        {
            result = fmtIndex[0] + "\n" + result;
        }

        return footer.Length > 0 ? result + "\n" + footer : result;
    }

    private static string SeriesFooter(PdSeries series, bool truncated)
    {
        var parts = new List<string>();

        if (series.Index.Freq is { } freq)
        {
            parts.Add($"Freq: {freq}");
        }

        if (series.Name is not PyNone)
        {
            parts.Add($"Name: {Pprint(series.Name)}");
        }

        if (truncated)
        {
            parts.Add($"Length: {series.Count}");
        }

        parts.Add($"dtype: {Pd.Name(series.Data.Type)}");
        return string.Join(", ", parts);
    }

    /// <summary><c>repr(frame)</c>, or <c>to_string()</c> when not truncating.</summary>
    public static string Frame(PdFrame frame, bool truncate = true, bool showIndex = true, string? naRep = null)
    {
        if (frame.Columns.Count == 0 || frame.Index.Count == 0)
        {
            return $"Empty DataFrame\nColumns: [{string.Join(", ", frame.Columns.Labels.Values.Select(Pprint))}]\nIndex: [{string.Join(", ", frame.Index.Labels.Values.Select(Pprint))}]";
        }

        var (rows, cut) = VisibleRows(frame.Index.Count, truncate);
        var columnCount = frame.Columns.Count;
        var columnCut = -1;
        var columns = Enumerable.Range(0, columnCount).ToList();

        if (truncate && MaxColumns is { } maxColumns && maxColumns > 0 && columnCount > maxColumns)
        {
            var half = Math.Max(1, maxColumns / 2);
            columns = [.. Enumerable.Range(0, half), .. Enumerable.Range(columnCount - half, half)];
            columnCut = half;
        }

        var index = frame.Index.Take(rows);
        var showIndexNames = HasNames(frame.Index) && showIndex;
        var multiColumns = frame.Columns.IsMulti;
        var headerRows = multiColumns ? frame.Columns.Names!.Count : 1;

        var headers = ColumnHeaders(frame, columns, multiColumns, headerRows);
        var strcols = new List<List<string>>();

        for (var c = 0; c < columns.Count; c++)
        {
            var column = frame.Data[columns[c]].Take(rows);
            var header = headers[c];

            if (showIndexNames)
            {
                header.Add(string.Empty);
            }

            var headerWidth = header.Max(static h => h.Length);
            var values = Values(column, leadingSpace: showIndex);

            if (naRep is not null)
            {
                for (var r = 0; r < values.Length; r++)
                {
                    if (column.IsMissing(r))
                    {
                        values[r] = naRep;
                    }
                }
            }

            var width = Math.Max(headerWidth, values.Length == 0 ? 0 : values.Max(static v => v.Length));
            strcols.Add([.. header.Select(h => PadLeft(h, width)), .. values.Select(v => PadLeft(v, width))]);
        }

        var headerCount = headerRows + (showIndexNames ? 1 : 0);

        if (showIndex)
        {
            var levels = IndexLevels(index, includeNames: showIndexNames).Select(l => Justified(l, left: true)).ToList();
            var adjoined = Adjoin(1, levels.Select(static l => (IReadOnlyList<string>)l).ToList()).Split('\n').ToList();
            var columnHeader = Enumerable.Repeat(string.Empty, headerRows).ToList();

            if (frame.Columns.Name is not PyNone && !multiColumns)
            {
                columnHeader = [Pd.Text(frame.Columns.Name)];
            }

            strcols.Insert(0, [.. columnHeader, .. adjoined]);
        }

        if (columnCut >= 0)
        {
            var length = strcols[0].Count;
            strcols.Insert(columnCut + (showIndex ? 1 : 0), [.. Enumerable.Repeat(" ...", length)]);
        }

        if (cut >= 0)
        {
            var row = cut + headerCount;

            for (var c = 0; c < strcols.Count; c++)
            {
                var col = strcols[c];
                var width = col[Math.Min(row, col.Count - 1)].Length;
                var isDotColumn = columnCut >= 0 && c == columnCut + (showIndex ? 1 : 0);
                var dots = width > 3 || isDotColumn ? "..." : "..";
                var dotted = c == 0 && showIndex ? PadRight(dots, width) : PadLeft(dots, isDotColumn ? 4 : width);
                col.Insert(row, dotted);
            }
        }

        var text = Wrap(strcols, showIndex);

        if (cut >= 0 || columnCut >= 0)
        {
            text += $"\n\n[{frame.Index.Count} rows x {columnCount} columns]";
        }

        return text;
    }

    private static List<List<string>> ColumnHeaders(PdFrame frame, List<int> columns, bool multi, int levels)
    {
        if (!multi)
        {
            return [.. columns.Select(c => new List<string>
            {
                (frame.Data[c].IsNumeric ? " " : string.Empty) + LabelText(frame.Columns[c], frame.Columns.Labels.Type),
            })];
        }

        var rows = new List<List<string>>();

        for (var level = 0; level < levels; level++)
        {
            rows.Add([.. columns.Select(c =>
            {
                var label = frame.Columns[c] is PyTuple t && level < t.Items.Count ? t.Items[level] : PyNone.Instance;
                return (frame.Data[c].IsNumeric ? " " : string.Empty) + Pd.Text(label);
            })]);
        }

        // Sparsify across columns: an upper level label repeated from the column to its left is blank.
        for (var c = columns.Count - 1; c > 0; c--)
        {
            for (var level = 0; level < levels - 1; level++)
            {
                var same = true;
                for (var upper = 0; upper <= level; upper++)
                {
                    same &= Level(frame, columns[c], upper) == Level(frame, columns[c - 1], upper);
                }

                if (same)
                {
                    rows[level][c] = string.Empty;
                }
            }
        }

        return [.. columns.Select((_, c) => rows.Select(r => r[c]).ToList())];

        static string Level(PdFrame frame, int column, int level) =>
            frame.Columns[column] is PyTuple t && level < t.Items.Count ? Pd.Text(t.Items[level]) : string.Empty;
    }

    /// <summary>pandas' <c>_join_multiline</c>: columns in blocks no wider than eighty, joined by <c>\</c>.</summary>
    private static string Wrap(List<List<string>> strcols, bool showIndex)
    {
        if (LineWidth is not { } lineWidth)
        {
            return Adjoin(1, strcols.Select(static c => (IReadOnlyList<string>)c).ToList());
        }

        var width = lineWidth;
        List<string>? index = null;
        var columns = strcols.ToList();

        if (showIndex)
        {
            index = columns[0];
            columns.RemoveAt(0);
            width -= index.Max(static x => x.Length) + 1;
        }

        var widths = columns.Select(static c => c.Count == 0 ? 0 : c.Max(static x => x.Length)).ToList();
        var bins = new List<int>();
        var current = 0;

        for (var i = 0; i < widths.Count; i++)
        {
            var adjoined = widths[i] + 1;
            current += adjoined;
            var wrap = i == widths.Count - 1 ? current + 1 > width && i > 0 : current + 2 > width && i > 0;

            if (wrap)
            {
                bins.Add(i);
                current = adjoined;
            }
        }

        bins.Add(columns.Count);
        var blocks = new List<string>();
        var start = 0;

        for (var b = 0; b < bins.Count; b++)
        {
            var end = bins[b];
            var row = columns.Skip(start).Take(end - start).Select(static c => (IReadOnlyList<string>)c).ToList();

            if (index is not null)
            {
                row.Insert(0, index);
            }

            if (bins.Count > 1)
            {
                var rowsCount = row[^1].Count;
                row.Add(b < bins.Count - 1
                    ? [" \\", .. Enumerable.Repeat("  ", rowsCount - 1)]
                    : [.. Enumerable.Repeat(" ", rowsCount)]);
            }

            blocks.Add(Adjoin(1, row));
            start = end;
        }

        return string.Join("\n\n", blocks);
    }

    /// <summary><c>to_html()</c>: the table pandas writes, with its classes and header layout.</summary>
    public static string Html(PdFrame frame, bool notebook = false, bool showIndex = true, bool truncate = false, string? naRep = null)
    {
        var (rows, cut) = VisibleRows(frame.Index.Count, truncate);
        var builder = new StringBuilder();
        builder.Append(notebook ? "<table border=\"1\" class=\"dataframe\">\n" : "<table border=\"1\" class=\"dataframe\">\n");
        builder.Append("  <thead>\n    <tr style=\"text-align: right;\">\n");

        if (showIndex)
        {
            builder.Append(CultureInfo.InvariantCulture, $"      <th>{(frame.Columns.Name is PyNone ? string.Empty : Html(Pd.Text(frame.Columns.Name)))}</th>\n");
        }

        for (var c = 0; c < frame.Columns.Count; c++)
        {
            builder.Append(CultureInfo.InvariantCulture, $"      <th>{Html(LabelText(frame.Columns[c], frame.Columns.Labels.Type))}</th>\n");
        }

        builder.Append("    </tr>\n");

        if (showIndex && HasNames(frame.Index))
        {
            builder.Append("    <tr>\n");
            foreach (var name in frame.Index.LevelNames)
            {
                builder.Append(CultureInfo.InvariantCulture, $"      <th>{(name is PyNone ? string.Empty : Html(Pd.Text(name)))}</th>\n");
            }

            for (var c = 0; c < frame.Columns.Count; c++)
            {
                builder.Append("      <th></th>\n");
            }

            builder.Append("    </tr>\n");
        }

        builder.Append("  </thead>\n  <tbody>\n");
        var formatted = frame.Data.Select(column => Values(column, leadingSpace: false)).ToList();
        var labels = Labels(frame.Index);

        for (var i = 0; i < rows.Count; i++)
        {
            if (i == cut)
            {
                builder.Append("    <tr>\n");
                if (showIndex)
                {
                    builder.Append("      <th>...</th>\n");
                }

                for (var c = 0; c < frame.Columns.Count; c++)
                {
                    builder.Append("      <td>...</td>\n");
                }

                builder.Append("    </tr>\n");
            }

            var r = rows[i];
            builder.Append("    <tr>\n");

            if (showIndex)
            {
                if (frame.Index.IsMulti && frame.Index[r] is PyTuple tuple)
                {
                    foreach (var part in tuple.Items)
                    {
                        builder.Append(CultureInfo.InvariantCulture, $"      <th>{Html(LabelText(part, DType.Object))}</th>\n");
                    }
                }
                else
                {
                    builder.Append(CultureInfo.InvariantCulture, $"      <th>{Html(labels[r])}</th>\n");
                }
            }

            for (var c = 0; c < frame.Columns.Count; c++)
            {
                var cell = naRep is not null && frame.Data[c].IsMissing(r) ? naRep : formatted[c][r].Trim();
                builder.Append(CultureInfo.InvariantCulture, $"      <td>{Html(cell)}</td>\n");
            }

            builder.Append("    </tr>\n");
        }

        builder.Append("  </tbody>\n</table>");

        if (cut >= 0)
        {
            builder.Append(CultureInfo.InvariantCulture, $"\n<p>{frame.Index.Count} rows × {frame.Columns.Count} columns</p>");
        }

        return builder.ToString();
    }

    private static string Html(string text) => WebUtility.HtmlEncode(text);

    /// <summary>The notebook's <c>_repr_html_</c>: the table inside pandas' styled <c>div</c>.</summary>
    public static string NotebookHtml(PdFrame frame) =>
        "<div>\n<style scoped>\n    .dataframe tbody tr th:only-of-type {\n        vertical-align: middle;\n    }\n\n"
        + "    .dataframe tbody tr th {\n        vertical-align: top;\n    }\n\n"
        + "    .dataframe thead th {\n        text-align: right;\n    }\n</style>\n"
        + Html(frame, notebook: true, truncate: true)
        + "\n</div>";

    /// <summary>Python's <c>format(value, 'g')</c>: six significant digits, exponent past them.</summary>
    private static string General(double value)
    {
        if (!double.IsFinite(value))
        {
            return double.IsNaN(value) ? "nan" : value > 0 ? "inf" : "-inf";
        }

        if (value == 0)
        {
            return double.IsNegative(value) ? "-0" : "0";
        }

        var exponent = (int)Math.Floor(Math.Log10(Math.Abs(double.Parse(value.ToString("E5", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture))));

        if (exponent < -4 || exponent >= 6)
        {
            var mantissa = (value / Math.Pow(10, exponent)).ToString("0.#####", CultureInfo.InvariantCulture);
            return $"{mantissa}e{(exponent < 0 ? '-' : '+')}{Math.Abs(exponent):00}";
        }

        return Math.Round(value, Math.Max(0, 5 - exponent), MidpointRounding.ToEven).ToString("0." + new string('#', Math.Max(0, 5 - exponent)), CultureInfo.InvariantCulture);
    }

    /// <summary><c>to_markdown()</c>: a pipe table in tabulate's layout (numbers right, text left).</summary>
    public static string Markdown(PdFrame frame, bool showIndex = true)
    {
        var headers = new List<string>();
        var columns = new List<List<string>>();
        var numeric = new List<bool>();

        if (showIndex)
        {
            headers.Add(frame.Index.Name is PyNone ? string.Empty : Pd.Text(frame.Index.Name));
            columns.Add([.. frame.Index.Labels.Values.Select(v => v is PyFloat f ? Pd.FloatText(f.Value) : Pd.Text(v))]);
            numeric.Add(frame.Index.Labels.IsNumeric);
        }

        for (var c = 0; c < frame.Columns.Count; c++)
        {
            headers.Add(Pd.Text(frame.Columns[c]));
            var column = frame.Data[c];
            var cells = column.Values.Select(v => v switch
            {
                PyFloat f when double.IsNaN(f.Value) => "nan",
                PyFloat f => General(f.Value),
                _ => Pprint(v),
            }).ToList();

            // tabulate's default numalign is "decimal": the points line up, then right-aligned.
            if (column.Type == DType.Float64)
            {
                var fraction = cells.Max(static t => t.Contains('.', StringComparison.Ordinal) ? t.Length - t.IndexOf('.', StringComparison.Ordinal) : 0);
                cells = [.. cells.Select(t => t + new string(' ', fraction - (t.Contains('.', StringComparison.Ordinal) ? t.Length - t.IndexOf('.', StringComparison.Ordinal) : 0)))];
            }

            columns.Add(cells);
            numeric.Add(column.IsNumeric && column.Type != DType.Bool);
        }

        // tabulate pads every header by two, so a column is never narrower than that.
        var widths = headers.Select((h, i) => Math.Max(h.Length + 2, columns[i].Count == 0 ? 0 : columns[i].Max(static v => v.Length))).ToList();
        var builder = new StringBuilder();

        builder.Append('|');
        for (var i = 0; i < headers.Count; i++)
        {
            builder.Append(' ').Append(numeric[i] ? PadLeft(headers[i], widths[i]) : PadRight(headers[i], widths[i])).Append(" |");
        }

        builder.Append("\n|");
        for (var i = 0; i < headers.Count; i++)
        {
            builder.Append(numeric[i] ? new string('-', widths[i] + 1) + ":|" : ":" + new string('-', widths[i] + 1) + "|");
        }

        for (var r = 0; r < frame.Index.Count; r++)
        {
            builder.Append("\n|");
            for (var i = 0; i < headers.Count; i++)
            {
                builder.Append(' ').Append(numeric[i] ? PadLeft(columns[i][r], widths[i]) : PadRight(columns[i][r], widths[i])).Append(" |");
            }
        }

        return builder.ToString();
    }
}
