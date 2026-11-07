using System.Globalization;
using System.Text;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules.Pandas;

/// <summary>
/// CSV and JSON in and out. Every path goes through the sandbox's virtual filesystem; with
/// none, naming a path raises <c>OSError</c>, while a file-like object (<c>io.StringIO</c>)
/// or returning the text still works.
/// </summary>
internal static class PdIO
{
    public static readonly string[] ReadCsvParameters =
    [
        "filepath_or_buffer", "sep", "delimiter", "header", "names", "index_col", "usecols", "dtype", "engine", "converters",
        "true_values", "false_values", "skipinitialspace", "skiprows", "skipfooter", "nrows", "na_values", "keep_default_na",
        "na_filter", "skip_blank_lines", "parse_dates", "date_format", "dayfirst", "thousands", "decimal", "lineterminator",
        "quotechar", "quoting", "doublequote", "escapechar", "comment", "encoding", "encoding_errors", "on_bad_lines",
        "low_memory", "memory_map", "float_precision", "dtype_backend", "delim_whitespace", "squeeze", "iterator", "chunksize",
        "compression", "storage_options",
    ];

    public static readonly string[] ToCsvParameters =
    [
        "path_or_buf", "sep", "na_rep", "float_format", "columns", "header", "index", "index_label", "mode", "encoding",
        "compression", "quoting", "quotechar", "lineterminator", "chunksize", "date_format", "doublequote", "escapechar",
        "decimal", "errors", "storage_options",
    ];

    public static readonly string[] ReadJsonParameters =
    [
        "path_or_buf", "orient", "typ", "dtype", "convert_axes", "convert_dates", "keep_default_dates", "precise_float",
        "date_unit", "encoding", "encoding_errors", "lines", "chunksize", "compression", "nrows", "storage_options", "dtype_backend", "engine",
    ];

    public static readonly string[] ToJsonParameters =
    [
        "path_or_buf", "orient", "date_format", "double_precision", "force_ascii", "date_unit", "default_handler", "lines",
        "compression", "index", "indent", "storage_options", "mode",
    ];

    private static readonly HashSet<string> DefaultNa = new(StringComparer.Ordinal)
    {
        string.Empty, "#N/A", "#N/A N/A", "#NA", "-1.#IND", "-1.#QNAN", "-NaN", "-nan", "1.#IND", "1.#QNAN", "<NA>", "N/A",
        "NA", "NULL", "NaN", "None", "n/a", "nan", "null",
    };

    // ---- reading the source ---------------------------------------------------------------

    /// <summary>The text behind a path, a path-like or a file-like object.</summary>
    private static string ReadSource(PyObject source, string? encoding)
    {
        var runtime = PdRuntime.Current!;

        if (Attributes.TryGet(runtime.Machine, source, "read") is { } read && source is not PyStr)
        {
            return runtime.Call(read) switch
            {
                PyStr text => text.Value,
                PyBytes bytes => Decode(bytes.Value, encoding),
                var other => throw Pd.TypeError($"read() returned {other.TypeName}, expected str"),
            };
        }

        var path = PathOf(source);
        var fileSystem = runtime.FileSystem ?? throw NoFileSystem(path);
        return Decode(fileSystem.Read(path), encoding);
    }

    private static PyRaise NoFileSystem(string path) => new(new PyException(
        PyExceptionType.OSError,
        $"[Errno 2] No such file or directory: '{path}' (this sandbox has no filesystem)"));

    private static string PathOf(PyObject source)
    {
        if (source is PyStr text)
        {
            if (text.Value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.Value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || text.Value.StartsWith("s3://", StringComparison.OrdinalIgnoreCase) || text.Value.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase))
            {
                throw new PyRaise(new PyException(PyExceptionType.PermissionError, "pandas: reading from a URL is not allowed in this sandbox"));
            }

            return text.Value;
        }

        var runtime = PdRuntime.Current!;

        if (Attributes.TryGet(runtime.Machine, source, "__fspath__") is { } fspath)
        {
            return runtime.Call(fspath).Display();
        }

        // pathlib.Path renders as its path.
        if (source.TypeName.Contains("Path", StringComparison.Ordinal))
        {
            return source.Display();
        }

        throw Pd.ValueError($"Invalid file path or buffer object type: <class '{source.TypeName}'>");
    }

    private static string Decode(byte[] bytes, string? encoding)
    {
        var name = encoding?.ToLowerInvariant().Replace("_", "-", StringComparison.Ordinal);
        var text = name switch
        {
            "latin-1" or "latin1" or "iso-8859-1" or "cp1252" or "windows-1252" => Encoding.Latin1.GetString(bytes),
            "ascii" => Encoding.ASCII.GetString(bytes),
            _ => Encoding.UTF8.GetString(bytes),
        };

        return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
    }

    /// <summary>Writes text to a path, a file-like object, or returns it when there is no target.</summary>
    private static PyObject WriteTarget(PyObject? target, string text, string mode)
    {
        if (target is null or PyNone)
        {
            return new PyStr(text);
        }

        var runtime = PdRuntime.Current!;

        if (target is not PyStr && Attributes.TryGet(runtime.Machine, target, "write") is { } write)
        {
            runtime.Call(write, new PyStr(text));
            return PyNone.Instance;
        }

        var path = PathOf(target);
        var fileSystem = runtime.FileSystem ?? throw NoFileSystem(path);
        var bytes = Encoding.UTF8.GetBytes(text);

        if (mode.StartsWith('a'))
        {
            fileSystem.Append(path, bytes);
        }
        else
        {
            fileSystem.Write(path, bytes);
        }

        return PyNone.Instance;
    }

    // ---- read_csv -----------------------------------------------------------------------

    public static PdFrame ReadCsv(PdArgs arguments)
    {
        var text = ReadSource(arguments.Required("filepath_or_buffer"), arguments.Str("encoding"));
        var separator = arguments.Str("sep") ?? arguments.Str("delimiter") ?? (arguments.Bool("delim_whitespace", false) ? @"\s+" : ",");
        var quote = arguments.Str("quotechar", "\"");
        var comment = arguments.Str("comment");
        var skipInitialSpace = arguments.Bool("skipinitialspace", false);
        var rows = Tokenize(text, separator, quote.Length > 0 ? quote[0] : '\0', comment, skipInitialSpace, arguments.Bool("skip_blank_lines", true));

        // skiprows: a count of leading lines, or the line numbers to drop.
        if (arguments.Given("skiprows") is { } skip)
        {
            if (Pd.IsListLike(skip))
            {
                var drop = Pd.List(skip).Select(s => Pd.ToInt(s, "skiprows")).ToHashSet();
                rows = [.. rows.Where((_, i) => !drop.Contains(i))];
            }
            else if (skip is PyCallable and not PyType)
            {
                rows = [.. rows.Where((_, i) => !PdRuntime.Current!.Call(skip, PyInt.From(i)).IsTruthy())];
            }
            else
            {
                rows = [.. rows.Skip(Pd.ToInt(skip, "skiprows"))];
            }
        }

        var names = arguments.Given("names") is { } n ? Pd.List(n) : null;
        var headerArgument = arguments["header"];
        int? header = headerArgument switch
        {
            null or PyStr { Value: "infer" } => names is null ? 0 : null,
            PyNone => null,
            var value => Pd.ToInt(value, "header"),
        };

        List<PyObject> columns;

        if (header is { } h)
        {
            if (h >= rows.Count)
            {
                throw new PyRaise(new PyException(PdModule.EmptyDataError, "No columns to parse from file"));
            }

            columns = names ?? Mangle(rows[h]);
            rows = [.. rows.Skip(h + 1)];
        }
        else
        {
            var width = rows.Count == 0 ? 0 : rows.Max(static r => r.Count);
            columns = names ?? [.. Enumerable.Range(0, width).Select(static i => (PyObject)PyInt.From(i))];
        }

        if (rows.Count == 0 && columns.Count == 0)
        {
            throw new PyRaise(new PyException(PdModule.EmptyDataError, "No columns to parse from file"));
        }

        if (arguments.Int("skipfooter", 0) is var footer && footer > 0)
        {
            rows = [.. rows.Take(Math.Max(0, rows.Count - footer))];
        }

        if (arguments.Given("nrows") is { } nrows)
        {
            rows = [.. rows.Take(Pd.ToInt(nrows, "nrows"))];
        }

        Pd.CheckSize((long)rows.Count * Math.Max(1, columns.Count));

        // One field more than the header on every row: the first is the index, as pandas infers.
        var implicitIndex = header is not null && names is null && rows.Count > 0 && rows.All(r => r.Count == columns.Count + 1);

        for (var r = 0; r < rows.Count; r++)
        {
            var expected = columns.Count + (implicitIndex ? 1 : 0);

            if (rows[r].Count > expected && !(names is not null && arguments.Given("index_col") is null && rows[r].Count == expected + 1))
            {
                throw new PyRaise(new PyException(PdModule.ParserError,
                    $"Error tokenizing data. C error: Expected {expected} fields in line {r + 2 + (header ?? -1)}, saw {rows[r].Count}"));
            }
        }

        var naSet = arguments.Bool("keep_default_na", true) ? new HashSet<string>(DefaultNa, StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
        var perColumnNa = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        switch (arguments.Given("na_values"))
        {
            case PyDict dict:
                foreach (var (key, value) in dict.Entries)
                {
                    perColumnNa[Pd.Text(key)] = [.. (Pd.IsListLike(value) ? Pd.List(value) : [value]).Select(Pd.Text)];
                }

                break;
            case { } values:
                naSet.UnionWith((Pd.IsListLike(values) ? Pd.List(values) : [values]).Select(Pd.Text));
                break;
        }

        var naFilter = arguments.Bool("na_filter", true);
        var trueValues = arguments.Given("true_values") is { } tv ? Pd.List(tv).Select(Pd.Text).ToHashSet(StringComparer.Ordinal) : [];
        var falseValues = arguments.Given("false_values") is { } fv ? Pd.List(fv).Select(Pd.Text).ToHashSet(StringComparer.Ordinal) : [];
        var thousands = arguments.Str("thousands");
        var decimalMark = arguments.Str("decimal", ".");
        var converters = arguments.Given("converters") as PyDict;
        var dtypes = arguments.Given("dtype");

        var allLabels = implicitIndex ? [PyNone.Instance, .. columns] : columns;
        var data = new List<PdColumn>();

        for (var c = 0; c < allLabels.Count; c++)
        {
            var label = allLabels[c];
            var fields = rows.Select(r => c < r.Count ? r[c] : null).ToList();
            var na = perColumnNa.TryGetValue(Pd.Text(label), out var specific) ? [.. naSet, .. specific] : naSet;

            if (converters is not null && (converters.TryGetValue(label, out var converter) || converters.TryGetValue(PyInt.From(c), out converter)))
            {
                data.Add(PdColumn.Infer([.. fields.Select(f => PdRuntime.Current!.Call(converter, new PyStr(f ?? string.Empty)))]));
                continue;
            }

            var dtype = dtypes is PyDict perColumn ? perColumn.TryGetValue(label, out var d) ? d : null : dtypes;

            if (dtype is not null && PdDTypeObject.Parse(dtype) is { Type: DType.Object })
            {
                data.Add(new PdColumn(DType.Object, [.. fields.Select(f => f is null || (naFilter && na.Contains(f)) ? Pd.NaN : (PyObject)new PyStr(f))]));
                continue;
            }

            var column = InferField(fields, naFilter ? na : [], trueValues, falseValues, thousands, decimalMark);

            if (dtype is not null)
            {
                var (type, spelling) = PdDTypeObject.Parse(dtype);
                column = column.Cast(type, spelling);
            }

            data.Add(column);
        }

        var frame = new PdFrame(PdIndex.Of(allLabels), data, PdIndex.Default(rows.Count));

        if (implicitIndex)
        {
            var index = new PdIndex(frame.Data[0], null);
            frame = frame.TakeColumns([.. Enumerable.Range(1, columns.Count)]);
            frame.Index = index;
        }

        // usecols: by name, by position, or a predicate on the name.
        if (arguments.Given("usecols") is { } usecols)
        {
            var keep = usecols is PyCallable and not PyType
                ? Enumerable.Range(0, frame.Columns.Count).Where(i => PdRuntime.Current!.Call(usecols, frame.Columns[i]).IsTruthy()).ToList()
                : Pd.List(usecols).Select(u => u is PyInt i and not PyBool && frame.Columns.Find(u) < 0 ? (int)i.Value : frame.Columns.Locate(u)).Order().ToList();
            frame = frame.TakeColumns(keep);
        }

        if (arguments.Given("parse_dates") is { } parseDates)
        {
            var dayFirst = arguments.Bool("dayfirst", false);
            var format = arguments.Str("date_format");
            var targets = parseDates is PyBool { Value: true } ? [] : Pd.List(parseDates);

            foreach (var target in targets)
            {
                var at = target is PyInt i and not PyBool && frame.Columns.Find(target) < 0 ? (int)i.Value : frame.Columns.Locate(target);
                frame.Data[at] = PdModule.ToDatetimeColumn(frame.Data[at], format, dayFirst, coerce: false);
            }
        }

        if (arguments.Given("index_col") is { } indexColumn and not PyBool)
        {
            var keys = Pd.IsListLike(indexColumn) ? Pd.List(indexColumn) : [indexColumn];
            var resolved = keys.Select(k => k is PyInt i and not PyBool && frame.Columns.Find(k) < 0 ? frame.Columns[(int)i.Value] : k).ToList();
            frame = frame.SetIndex(resolved.Count == 1 ? resolved[0] : new PyList(resolved), drop: true, append: false);

            if (arguments.Given("parse_dates") is PyBool { Value: true } && !frame.Index.IsMulti)
            {
                frame.Index = new PdIndex(PdModule.ToDatetimeColumn(frame.Index.Labels, null, false, coerce: false), frame.Index.Name);
            }
        }

        return frame;
    }

    /// <summary>pandas' duplicate-header mangling: <c>a</c>, <c>a.1</c>; an empty name is <c>Unnamed: i</c>.</summary>
    private static List<PyObject> Mangle(List<string> header)
    {
        var result = new List<PyObject>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var i = 0; i < header.Count; i++)
        {
            var name = header[i].Length == 0 ? $"Unnamed: {i}" : header[i];

            if (counts.TryGetValue(name, out var seen))
            {
                var candidate = $"{name}.{seen}";
                while (counts.ContainsKey(candidate))
                {
                    seen++;
                    candidate = $"{name}.{seen}";
                }

                counts[name] = seen + 1;
                counts[candidate] = 1;
                name = candidate;
            }
            else
            {
                counts[name] = 1;
            }

            result.Add(new PyStr(name));
        }

        return result;
    }

    /// <summary>The C parser's inference: int, then float, then bool, else strings.</summary>
    private static PdColumn InferField(List<string?> fields, HashSet<string> na, HashSet<string> trueValues, HashSet<string> falseValues, string? thousands, string decimalMark)
    {
        var count = fields.Count;
        var missing = fields.Select(f => f is null || na.Contains(f)).ToArray();
        var valid = Enumerable.Range(0, count).Where(i => !missing[i]).ToList();

        string Clean(string field)
        {
            var text = field.Trim();
            if (thousands is { Length: > 0 })
            {
                text = text.Replace(thousands, string.Empty, StringComparison.Ordinal);
            }

            return decimalMark != "." ? text.Replace(decimalMark, ".", StringComparison.Ordinal) : text;
        }

        if (valid.Count == 0)
        {
            return PdColumn.Floats([.. Enumerable.Repeat(double.NaN, count)]);
        }

        if (valid.All(i => long.TryParse(Clean(fields[i]!), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)))
        {
            if (valid.Count == count)
            {
                return PdColumn.Ints([.. fields.Select(f => long.Parse(Clean(f!), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture))]);
            }

            return PdColumn.Floats([.. fields.Select((f, i) => missing[i] ? double.NaN : double.Parse(Clean(f!), CultureInfo.InvariantCulture))]);
        }

        if (valid.All(i => TryFloat(Clean(fields[i]!), out _)))
        {
            return PdColumn.Floats([.. fields.Select((f, i) => missing[i] ? double.NaN : TryFloat(Clean(f!), out var v) ? v : double.NaN)]);
        }

        bool? Boolean(string field) =>
            trueValues.Contains(field) || field is "True" or "TRUE" or "true" ? true
            : falseValues.Contains(field) || field is "False" or "FALSE" or "false" ? false
            : null;

        if (valid.All(i => Boolean(fields[i]!) is not null))
        {
            if (valid.Count == count)
            {
                return PdColumn.Bools([.. fields.Select(f => Boolean(f!)!.Value)]);
            }

            return new PdColumn(DType.Object, [.. fields.Select((f, i) => missing[i] ? Pd.NaN : (PyObject)PyBool.Of(Boolean(f!)!.Value))]);
        }

        return new PdColumn(DType.Object, [.. fields.Select((f, i) => missing[i] ? Pd.NaN : (PyObject)new PyStr(f!))]);

        static bool TryFloat(string text, out double value)
        {
            if (text.Length > 0 && (char.IsAsciiDigit(text[^1]) || text[^1] == '.') && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                return true;
            }

            switch (text.ToLowerInvariant())
            {
                case "inf" or "+inf" or "infinity" or "+infinity":
                    value = double.PositiveInfinity;
                    return true;
                case "-inf" or "-infinity":
                    value = double.NegativeInfinity;
                    return true;
                default:
                    value = 0;
                    return false;
            }
        }
    }

    /// <summary>Splits CSV text into rows of fields: quotes, doubled quotes, newlines inside quotes.</summary>
    private static List<List<string>> Tokenize(string text, string separator, char quote, string? comment, bool skipInitialSpace, bool skipBlank)
    {
        var rows = new List<List<string>>();

        if (separator is @"\s+" or " " && separator != ",")
        {
            foreach (var line in text.Split('\n'))
            {
                var content = line.TrimEnd('\r');

                if (comment is { Length: > 0 } && content.IndexOf(comment[0], StringComparison.Ordinal) is var at && at >= 0)
                {
                    content = content[..at];
                }

                if (separator == @"\s+")
                {
                    var parts = content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 0 || !skipBlank)
                    {
                        rows.Add([.. parts]);
                    }

                    continue;
                }

                if (content.Length > 0 || !skipBlank)
                {
                    rows.Add([.. content.Split(' ')]);
                }
            }

            return rows;
        }

        if (separator.Length != 1)
        {
            throw Pd.ValueError($"pandas: only a single-character separator (or '\\s+') is supported, got {separator.Length} characters");
        }

        var sep = separator[0];
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var fieldStarted = false;
        var lineHasContent = false;
        var i = 0;

        void EndField()
        {
            row.Add(field.ToString());
            field.Clear();
            fieldStarted = false;
        }

        void EndRow()
        {
            EndField();

            if (lineHasContent || !skipBlank)
            {
                rows.Add(row);
            }

            row = [];
            lineHasContent = false;
        }

        while (i < text.Length)
        {
            var ch = text[i];

            if (inQuotes)
            {
                if (ch == quote)
                {
                    if (i + 1 < text.Length && text[i + 1] == quote)
                    {
                        field.Append(quote);
                        i += 2;
                        continue;
                    }

                    inQuotes = false;
                    i++;
                    continue;
                }

                field.Append(ch);
                i++;
                continue;
            }

            if (comment is { Length: > 0 } && ch == comment[0])
            {
                while (i < text.Length && text[i] != '\n' && text[i] != '\r')
                {
                    i++;
                }

                continue;
            }

            if (ch == quote && !fieldStarted)
            {
                inQuotes = true;
                fieldStarted = true;
                lineHasContent = true;
                i++;
                continue;
            }

            if (ch == sep)
            {
                lineHasContent = true;
                EndField();
                i++;

                if (skipInitialSpace)
                {
                    while (i < text.Length && text[i] == ' ')
                    {
                        i++;
                    }
                }

                continue;
            }

            if (ch is '\n' or '\r')
            {
                EndRow();
                i += ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1;
                continue;
            }

            field.Append(ch);
            fieldStarted = true;
            lineHasContent = true;
            i++;
        }

        if (field.Length > 0 || row.Count > 0 || fieldStarted)
        {
            EndRow();
        }

        return rows;
    }

    // ---- to_csv -------------------------------------------------------------------------

    public static PyObject ToCsv(PdFrame frame, PdArgs arguments, bool series)
    {
        var separator = arguments.Str("sep", ",");
        var naRep = arguments.Str("na_rep", string.Empty);
        var floatFormat = arguments.Given("float_format");
        var lineTerminator = arguments.Str("lineterminator", "\n");
        var quote = arguments.Str("quotechar", "\"");
        var quoting = arguments.Int("quoting", 0);
        var dateFormat = arguments.Str("date_format");
        var decimalMark = arguments.Str("decimal", ".");
        var showIndex = arguments.Bool("index", true);
        var header = arguments["header"] ?? PyBool.True;

        if (arguments.Given("columns") is { } selected)
        {
            frame = frame.TakeColumns(frame.ColumnPositions(Pd.IsListLike(selected) ? Pd.List(selected) : [selected]));
        }

        var builder = new StringBuilder();

        void Line(IReadOnlyList<string> fields, IReadOnlyList<bool> numeric)
        {
            for (var i = 0; i < fields.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(separator);
                }

                var value = fields[i];
                var needsQuotes = quoting == 1
                    || (quoting == 2 && !numeric[i])
                    || (quoting != 3 && (value.Contains(separator, StringComparison.Ordinal) || value.Contains(quote, StringComparison.Ordinal) || value.Contains('\n') || value.Contains('\r')))
                    || (fields.Count == 1 && value.Length == 0 && quoting != 3);

                builder.Append(needsQuotes ? quote + value.Replace(quote, quote + quote, StringComparison.Ordinal) + quote : value);
            }

            builder.Append(lineTerminator);
        }

        if (header is not PyBool { Value: false })
        {
            var labels = header is PyBool ? frame.Columns.Labels.Values.Select(Pd.Text).ToList() : Pd.List(header).Select(Pd.Text).ToList();
            var fields = new List<string>();

            if (showIndex)
            {
                var indexLabel = arguments.Given("index_label");
                fields.AddRange(indexLabel is PyBool { Value: false } ? []
                    : indexLabel is not null ? (Pd.IsListLike(indexLabel) ? Pd.List(indexLabel) : [indexLabel]).Select(Pd.Text)
                    : frame.Index.LevelNames.Select(static n => n is PyNone ? string.Empty : Pd.Text(n)));
            }

            fields.AddRange(series && header is PyBool && frame.Columns[0] is PyInt { Value.IsZero: true } ? ["0"] : labels);
            Line(fields, [.. fields.Select(static _ => false)]);
        }

        var formatted = frame.Data.Select(column => CsvValues(column, naRep, floatFormat, dateFormat, decimalMark)).ToList();
        var indexText = showIndex ? IndexValues(frame.Index, naRep) : null;
        var numericColumns = frame.Data.Select(static c => c.IsNumeric).ToList();

        for (var r = 0; r < frame.RowCount; r++)
        {
            var fields = new List<string>();
            var numeric = new List<bool>();

            if (indexText is not null)
            {
                fields.AddRange(indexText[r]);
                numeric.AddRange(indexText[r].Select(_ => frame.Index.Labels.IsNumeric));
            }

            for (var c = 0; c < formatted.Count; c++)
            {
                fields.Add(formatted[c][r]);
                numeric.Add(numericColumns[c]);
            }

            Line(fields, numeric);
        }

        return WriteTarget(arguments.Given("path_or_buf"), builder.ToString(), arguments.Str("mode", "w"));
    }

    private static List<string[]> IndexValues(PdIndex index, string naRep)
    {
        var dateOnly = index.Labels.Type == DType.DateTime && index.Labels.Values.All(static v => v is not PdTimestamp t || t.IsMidnight);
        return [.. index.Labels.Values.Select(v => v is PyTuple tuple
            ? tuple.Items.Select(i => Scalar(i, naRep, null, null, false, ".")).ToArray()
            : [Scalar(v, naRep, null, null, dateOnly, ".")])];
    }

    private static string[] CsvValues(PdColumn column, string naRep, PyObject? floatFormat, string? dateFormat, string decimalMark)
    {
        var dateOnly = column.Type == DType.DateTime && column.Values.All(static v => v is not PdTimestamp t || t.IsMidnight);
        return [.. column.Values.Select(v => Scalar(v, naRep, floatFormat, dateFormat, dateOnly, decimalMark))];
    }

    private static string Scalar(PyObject value, string naRep, PyObject? floatFormat, string? dateFormat, bool dateOnly, string decimalMark)
    {
        if (Pd.IsMissing(value))
        {
            return naRep;
        }

        switch (value)
        {
            case PyFloat number:
            {
                var text = floatFormat switch
                {
                    null => Pd.FloatText(number.Value),
                    PyStr pattern => Operators.Binary("%", pattern, number).Display(),
                    _ => PdRuntime.Current!.Call(floatFormat, number).Display(),
                };

                return decimalMark == "." ? text : text.Replace(".", decimalMark, StringComparison.Ordinal);
            }

            case PdTimestamp moment:
                return dateFormat is not null ? DatetimeModule.Expand(moment.Parts, dateFormat) : moment.Text(dateOnly);
            case PyDelta delta:
                return PdTimestamp.DeltaText(delta, false);
            default:
                return value.Display();
        }
    }

    // ---- JSON ---------------------------------------------------------------------------

    public static PyObject ToJson(PyObject source, PdArgs arguments)
    {
        var isFrame = source is PdFrame;
        var orient = arguments.Str("orient") ?? (isFrame ? "columns" : "index");
        var lines = arguments.Bool("lines", false);
        var iso = arguments.Str("date_format", "epoch") == "iso";
        var precision = arguments.Int("double_precision", 10);
        var ascii = arguments.Bool("force_ascii", true);
        var indent = arguments.Int("indent", 0);
        var frame = source as PdFrame ?? ((PdSeries)source).ToFrame(null);
        var writer = new JsonText(iso, precision, ascii);

        if (lines && orient != "records")
        {
            throw Pd.ValueError("'lines' keyword only valid when 'orient' is records");
        }

        string text;

        if (!isFrame)
        {
            var series = (PdSeries)source;
            text = orient switch
            {
                "index" => writer.Object(Enumerable.Range(0, series.Count).Select(i => (Pd.Text(series.Index[i]), series.Data.Values[i]))),
                "records" or "values" => writer.Array(series.Data.Values.Select(writer.Value)),
                "split" => writer.ObjectRaw([
                    ("name", writer.Value(series.Name)),
                    ("index", writer.Array(series.Index.Labels.Values.Select(writer.Value))),
                    ("data", writer.Array(series.Data.Values.Select(writer.Value))),
                ]),
                "columns" => writer.Object(Enumerable.Range(0, series.Count).Select(i => (Pd.Text(series.Index[i]), series.Data.Values[i]))),
                _ => throw Pd.ValueError($"Invalid value '{orient}' for option 'orient'"),
            };
        }
        else
        {
            string Row(int r) => writer.Object(Enumerable.Range(0, frame.Columns.Count).Select(c => (Pd.Text(frame.Columns[c]), frame.Data[c].Values[r])));

            text = orient switch
            {
                "columns" => writer.ObjectRaw(Enumerable.Range(0, frame.Columns.Count).Select(c =>
                    (Pd.Text(frame.Columns[c]), writer.Object(Enumerable.Range(0, frame.RowCount).Select(r => (Pd.Text(frame.Index[r]), frame.Data[c].Values[r])))))),
                "index" => writer.ObjectRaw(Enumerable.Range(0, frame.RowCount).Select(r => (Pd.Text(frame.Index[r]), Row(r)))),
                "records" when lines => string.Join("\n", Enumerable.Range(0, frame.RowCount).Select(Row)) + (frame.RowCount > 0 ? "\n" : string.Empty),
                "records" => writer.Array(Enumerable.Range(0, frame.RowCount).Select(Row)),
                "values" => writer.Array(Enumerable.Range(0, frame.RowCount).Select(r => writer.Array(frame.Data.Select(d => writer.Value(d.Values[r]))))),
                "split" => writer.ObjectRaw([
                    ("columns", writer.Array(frame.Columns.Labels.Values.Select(writer.Value))),
                    ("index", writer.Array(frame.Index.Labels.Values.Select(writer.Value))),
                    ("data", writer.Array(Enumerable.Range(0, frame.RowCount).Select(r => writer.Array(frame.Data.Select(d => writer.Value(d.Values[r])))))),
                ]),
                _ => throw Pd.ValueError($"Invalid value '{orient}' for option 'orient'"),
            };
        }

        if (indent > 0 && PdRuntime.Current!.Machine.Import("json") is { } json)
        {
            var runtime = PdRuntime.Current;
            var parsed = runtime.Call(json.GetAttribute("loads")!, new PyStr(text));
            var keywords = new PyDict();
            keywords.Set(new PyStr("indent"), PyInt.From(indent));
            text = runtime.Machine.Call(json.GetAttribute("dumps")!, [parsed], keywords).Display();
        }

        return WriteTarget(arguments.Given("path_or_buf"), text, arguments.Str("mode", "w"));
    }

    /// <summary>pandas' ujson flavour: <c>\/</c>, ASCII escapes, ten significant decimals, epoch-ms dates.</summary>
    private sealed class JsonText(bool iso, int precision, bool ascii)
    {
        public string Object(IEnumerable<(string Key, PyObject Value)> entries) =>
            ObjectRaw(entries.Select(e => (e.Key, Value(e.Value))));

        public string ObjectRaw(IEnumerable<(string Key, string Json)> entries) =>
            "{" + string.Join(",", entries.Select(e => String(e.Key) + ":" + e.Json)) + "}";

        public string Array(IEnumerable<string> items) => "[" + string.Join(",", items) + "]";

        public string Value(PyObject value) => value switch
        {
            PyNone or PdNaT or PdNA => "null",
            PyFloat number when !double.IsFinite(number.Value) => "null",
            PyBool flag => flag.Value ? "true" : "false",
            PyInt integer => PyInt.Decimal(integer.Value),
            PyFloat number => Number(number.Value),
            PyStr text => String(text.Value),
            PdTimestamp moment => iso ? String(moment.Text(false).Replace(' ', 'T') + (moment.Parts.Microsecond == 0 ? ".000" : string.Empty)) : ((moment.Nanoseconds / 1_000_000L).ToString(CultureInfo.InvariantCulture)),
            PyList list => Array(list.Items.Select(Value)),
            PyTuple tuple => Array(tuple.Items.Select(Value)),
            PyDict dict => ObjectRaw(dict.Entries.Select(e => (Pd.Text(e.Key), Value(e.Value)))),
            _ => String(value.Display()),
        };

        private string Number(double value)
        {
            if (value == Math.Floor(value) && Math.Abs(value) < 1e16)
            {
                return value.ToString("F1", CultureInfo.InvariantCulture);
            }

            if (Math.Abs(value) >= 1e16 || Math.Abs(value) < 1e-10)
            {
                return PyFloat.Format(value);
            }

            var rounded = Math.Round(value, precision, MidpointRounding.AwayFromZero);
            var text = rounded.ToString("0." + new string('#', precision), CultureInfo.InvariantCulture);
            return text.Contains('.', StringComparison.Ordinal) ? text : text + ".0";
        }

        private string String(string text)
        {
            var builder = new StringBuilder("\"");

            foreach (var ch in text)
            {
                switch (ch)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '/': builder.Append("\\/"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\f': builder.Append("\\f"); break;
                    default:
                        if (ch < 0x20 || (ascii && ch > 0x7e))
                        {
                            builder.Append(CultureInfo.InvariantCulture, $"\\u{(int)ch:x4}");
                        }
                        else
                        {
                            builder.Append(ch);
                        }

                        break;
                }
            }

            return builder.Append('"').ToString();
        }
    }

    public static PyObject ReadJson(PdArgs arguments)
    {
        var runtime = PdRuntime.Current!;
        var source = arguments.Required("path_or_buf");
        string text;

        if (source is PyStr literal && literal.Value.TrimStart() is { Length: > 0 } trimmed && trimmed[0] is '{' or '[')
        {
            text = literal.Value;
        }
        else
        {
            text = ReadSource(source, arguments.Str("encoding"));
        }

        var loads = runtime.Machine.Import("json").GetAttribute("loads")!;
        var typ = arguments.Str("typ", "frame");
        var orient = arguments.Str("orient");
        var convertAxes = arguments.Bool("convert_axes", true);
        PyObject parsed;

        if (arguments.Bool("lines", false))
        {
            parsed = new PyList([.. text.Split('\n').Where(static l => l.Trim().Length > 0).Select(l => runtime.Call(loads, new PyStr(l)))]);
        }
        else
        {
            parsed = runtime.Call(loads, new PyStr(text));
        }

        if (typ == "series")
        {
            if (parsed is PyDict dict)
            {
                return new PdSeries(PdColumn.Infer([.. dict.Entries.Select(static e => e.Value)]), Axis([.. dict.Entries.Select(static e => e.Key)], convertAxes), null);
            }

            return new PdSeries(PdColumn.Infer(Pd.List(parsed)), PdIndex.Default(Pd.List(parsed).Count), null);
        }

        switch (parsed)
        {
            case PyList list when orient is null or "records" && list.Items.All(static i => i is PyDict):
            {
                var keywords = new PyDict();
                return PdBuild.Frame(new PdArgs("read_json", [list], keywords, ["data", "index", "columns", "dtype", "copy"]));
            }

            case PyList list:
            {
                var frame = PdBuild.Frame(new PdArgs("read_json", [list], null, ["data", "index", "columns", "dtype", "copy"]));
                return frame;
            }

            case PyDict dict when orient == "split" || (orient is null && dict.TryGetValue(new PyStr("data"), out _) && dict.TryGetValue(new PyStr("columns"), out _)):
            {
                var data = dict.TryGetValue(new PyStr("data"), out var d) ? d : new PyList();
                var keywords = new PyDict();
                if (dict.TryGetValue(new PyStr("columns"), out var columns))
                {
                    keywords.Set(new PyStr("columns"), columns);
                }

                if (dict.TryGetValue(new PyStr("index"), out var index))
                {
                    keywords.Set(new PyStr("index"), index);
                }

                return PdBuild.Frame(new PdArgs("read_json", [data], keywords, ["data", "index", "columns", "dtype", "copy"]));
            }

            case PyDict dict when orient == "index":
            {
                var rows = dict.Entries.Select(static e => e.Value).Cast<PyDict>().ToList();
                var frame = PdBuild.Frame(new PdArgs("read_json", [new PyList([.. rows])], null, ["data", "index", "columns", "dtype", "copy"]));
                frame.Index = Axis([.. dict.Entries.Select(static e => e.Key)], convertAxes);
                return frame;
            }

            case PyDict dict:
            {
                // orient='columns': {column: {index: value}}.
                var frame = PdBuild.Frame(new PdArgs("read_json", [dict], null, ["data", "index", "columns", "dtype", "copy"]));

                if (convertAxes)
                {
                    frame.Index = Axis(frame.Index.Labels.Values, true);
                    frame.Columns = Axis(frame.Columns.Labels.Values, true);
                }

                return frame;
            }

            default:
                throw Pd.ValueError("Unexpected JSON for read_json");
        }

        static PdIndex Axis(IReadOnlyList<PyObject> labels, bool convert)
        {
            if (convert && labels.Count > 0 && labels.All(static l => l is PyStr s && long.TryParse(s.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)))
            {
                return PdIndex.Of([.. labels.Select(static l => (PyObject)PyInt.From(long.Parse(((PyStr)l).Value, CultureInfo.InvariantCulture)))]);
            }

            return PdIndex.Of(labels);
        }
    }
}
