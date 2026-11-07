using System.Globalization;
using System.Text;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules.Pandas;

/// <summary>The DataFrame's attributes and methods, by name.</summary>
internal sealed partial class PdFrame
{
    private static readonly string[] ReduceParameters = ["axis", "skipna", "numeric_only", "min_count", "ddof"];

    public override bool SetAttribute(string name, PyObject value)
    {
        switch (name)
        {
            case "columns":
            {
                var columns = PdBuild.IndexFrom(value);
                if (columns.Count != Columns.Count)
                {
                    throw Pd.ValueError($"Length mismatch: Expected axis has {Columns.Count} elements, new values have {columns.Count} elements");
                }

                Columns = columns;
                return true;
            }

            case "index":
            {
                var index = PdBuild.IndexFrom(value);
                if (index.Count != RowCount)
                {
                    throw Pd.ValueError($"Length mismatch: Expected axis has {RowCount} elements, new values have {index.Count} elements");
                }

                Index = index;
                return true;
            }

            default:
                // `df.col = values` sets an existing column, as pandas does; a new name is refused
                // rather than silently becoming an attribute nobody can see.
                if (Columns.Find(new PyStr(name)) is var at && at >= 0)
                {
                    Data[at] = ColumnFrom(value);
                    return true;
                }

                return false;
        }
    }

    private PyObject? Attribute(string name)
    {
        var runtime = PdRuntime.Current;

        switch (name)
        {
            case "columns": return Columns;
            case "index": return Index;
            case "shape": return new PyTuple([PyInt.From(RowCount), PyInt.From(Columns.Count)]);
            case "size": return PyInt.From(RowCount * Columns.Count);
            case "ndim": return PyInt.From(2);
            case "empty": return PyBool.Of(RowCount == 0 || Columns.Count == 0);
            case "axes": return new PyList([Index, Columns]);
            case "dtypes": return new PdSeries(new PdColumn(DType.Object, [.. Data.Select(static d => (PyObject)new PdDTypeObject(d.Type))]), Columns, null);
            case "loc": return new PdIndexer(this, positional: false, scalar: false);
            case "iloc": return new PdIndexer(this, positional: true, scalar: false);
            case "at": return new PdIndexer(this, positional: false, scalar: true);
            case "iat": return new PdIndexer(this, positional: true, scalar: true);
            case "T": return Transpose();
        }

        if (runtime is null)
        {
            return ColumnAttribute(name);
        }

        PyBuiltinFunction M(string[] parameters, Func<PdArgs, PyObject> body, bool extra = false) =>
            runtime.Method(name, parameters, body, extra);

        if (PdArith.Methods.TryGetValue(name, out var arith))
        {
            return M(["other", "axis", "level", "fill_value"], a => PdArith.Frame(this, arith.Op, arith.Reflected, a.Required("other"), a.Given("fill_value"), a["axis"]));
        }

        if (PdArith.Comparisons.TryGetValue(name, out var comparison))
        {
            return M(["other", "axis", "level"], a => Compare(comparison, a.Required("other"))!);
        }

        if (PdReduce.Names.Contains(name) && name is not ("first" or "last" or "size" or "nunique"))
        {
            return M(ReduceParameters, a => Reduce(name, a));
        }

        return name switch
        {
            "values" => Values(runtime),
            "to_numpy" => M(["dtype", "copy", "na_value"], _ => Values(runtime)),
            "head" => M(["n"], a => Head(a.Int("n", 5))),
            "tail" => M(["n"], a => Tail(a.Int("n", 5))),
            "copy" => M(["deep"], a => a.Bool("deep", true) ? Copy() : ShallowCopy()),
            "keys" => M([], _ => Columns),
            "items" => M([], _ => new PyIterator(Enumerable.Range(0, Columns.Count).Select(c => (PyObject)new PyTuple([Columns[c], Column(c)])))),
            "iterrows" => M([], _ => new PyIterator(Enumerable.Range(0, RowCount).Select(r => (PyObject)new PyTuple([Index[r], Row(r)])))),
            "itertuples" => M(["index", "name"], a => IterTuples(a.Bool("index", true), a["name"] is PyNone ? null : a.Str("name", "Pandas"))),
            "info" => M(["verbose", "buf", "max_cols", "memory_usage", "show_counts"], _ => Info(runtime)),
            "describe" => M(["percentiles", "include", "exclude"], Describe),
            "transpose" => M(["copy"], _ => Transpose()),
            "to_dict" => M(["orient", "into", "index"], a => ToDict(a.Str("orient", "dict"))),
            "to_string" => M(["buf", "columns", "col_space", "header", "index", "na_rep", "formatters", "float_format", "max_rows"], a =>
                new PyStr(PdFormat.Frame(this, truncate: a.Given("max_rows") is not null, showIndex: a.Bool("index", true), naRep: a.Str("na_rep")))),
            "to_html" => M(["buf", "columns", "col_space", "header", "index", "na_rep", "max_rows"], a =>
                new PyStr(PdFormat.Html(this, showIndex: a.Bool("index", true), naRep: a.Str("na_rep")))),
            "_repr_html_" => M([], _ => new PyStr(PdFormat.NotebookHtml(this))),
            "to_markdown" => M(["buf", "mode", "index", "tablefmt"], a => new PyStr(PdFormat.Markdown(this, a.Bool("index", true)))),
            "to_csv" => M(PdIO.ToCsvParameters, a => PdIO.ToCsv(this, a, series: false)),
            "to_json" => M(PdIO.ToJsonParameters, a => PdIO.ToJson(this, a)),
            "astype" => M(["dtype", "copy", "errors"], a => AsType(a.Required("dtype"))),
            "assign" => M([], Assign, extra: true),
            "insert" => M(["loc", "column", "value", "allow_duplicates"], a => Insert(a)),
            "pop" => M(["item"], a =>
            {
                var column = Column(a.Required("item"));
                DeleteItem(a.Required("item"));
                return column;
            }),
            "get" => M(["key", "default"], a => Columns.Find(a.Required("key")) >= 0 || Pd.IsListLike(a.Required("key")) ? Select(a.Required("key")) : a["default"] ?? PyNone.Instance),
            "drop" => M(["labels", "axis", "index", "columns", "level", "inplace", "errors"], a => Inplace(a, Drop(a))),
            "rename" => M(["mapper", "index", "columns", "axis", "copy", "inplace", "level", "errors"], a => Inplace(a, Rename(a))),
            "rename_axis" => M(["mapper", "index", "columns", "axis", "copy", "inplace"], a =>
            {
                var result = ShallowCopy();
                if (Axis(a["axis"]) == 1 || a.Given("columns") is not null)
                {
                    result.Columns = Columns.WithName(a.Given("columns") ?? a.Given("mapper") ?? PyNone.Instance);
                }
                else
                {
                    result.Index = Index.WithName(a.Given("index") ?? a.Given("mapper") ?? PyNone.Instance);
                }

                return Inplace(a, result);
            }),
            "set_axis" => M(["labels", "axis", "copy"], a =>
            {
                var result = ShallowCopy();
                result.SetAttribute(Axis(a["axis"]) == 1 ? "columns" : "index", a.Required("labels"));
                return result;
            }),
            "set_index" => M(["keys", "drop", "append", "inplace", "verify_integrity"], a => Inplace(a, SetIndex(a.Required("keys"), a.Bool("drop", true), a.Bool("append", false)))),
            "reset_index" => M(["level", "drop", "inplace", "col_level", "col_fill", "allow_duplicates", "names"], a => Inplace(a, ResetIndex(a.Bool("drop", false)))),
            "sort_values" => M(["by", "axis", "ascending", "inplace", "kind", "na_position", "ignore_index", "key"], a => Inplace(a, SortValues(a), a.Bool("ignore_index", false))),
            "sort_index" => M(["axis", "level", "ascending", "inplace", "kind", "na_position", "sort_remaining", "ignore_index", "key"], a => Inplace(a, SortIndex(a), a.Bool("ignore_index", false))),
            "groupby" => M(["by", "axis", "level", "as_index", "sort", "group_keys", "observed", "dropna"], a => new PdGroupBy(this, a)),
            "merge" => M(PdReshape.MergeParameters.Skip(1).ToArray(), a => PdReshape.Merge(this, a)),
            "join" => M(["other", "on", "how", "lsuffix", "rsuffix", "sort", "validate"], a => PdReshape.Join(this, a)),
            "pivot_table" => M(PdReshape.PivotTableParameters.Skip(1).ToArray(), a => PdReshape.PivotTable(this, a)),
            "pivot" => M(["columns", "index", "values"], a => PdReshape.Pivot(this, a)),
            "melt" => M(PdReshape.MeltParameters.Skip(1).ToArray(), a => PdReshape.Melt(this, a)),
            "fillna" => M(["value", "method", "axis", "inplace", "limit"], a => Inplace(a, FillNa(a.Given("value"), a.Str("method"), a.Given("limit")))),
            "ffill" or "pad" => M(["axis", "inplace", "limit"], a => Inplace(a, FillNa(null, "ffill", a.Given("limit")))),
            "bfill" or "backfill" => M(["axis", "inplace", "limit"], a => Inplace(a, FillNa(null, "bfill", a.Given("limit")))),
            "interpolate" => M(["method", "axis", "limit", "inplace", "limit_direction"], a => Inplace(a, MapColumns(static c => PdWindow.Interpolate(c)))),
            "dropna" => M(["axis", "how", "thresh", "subset", "inplace", "ignore_index"], a => Inplace(a, DropNa(a), a.Bool("ignore_index", false))),
            "isna" or "isnull" => M([], _ => MapColumns(static c => PdSeries.MissingMask(c, true))),
            "notna" or "notnull" => M([], _ => MapColumns(static c => PdSeries.MissingMask(c, false))),
            "apply" => M(["func", "axis", "raw", "result_type", "args", "by_row", "engine"], a => Apply(a), extra: true),
            "map" or "applymap" => M(["func", "na_action"], a =>
            {
                var function = a.Required("func");
                var ignore = a.Str("na_action") == "ignore";
                return MapColumns(c => new PdSeries(c, Index, null).Map(function, ignore).Data);
            }, extra: true),
            "duplicated" => M(["subset", "keep"], a => new PdSeries(PdColumn.Bools([.. Duplicated(a.Given("subset"), a["keep"])]), Index, null)),
            "drop_duplicates" => M(["subset", "keep", "inplace", "ignore_index"], a =>
            {
                var duplicated = Duplicated(a.Given("subset"), a["keep"]);
                return Inplace(a, TakeRows([.. Enumerable.Range(0, RowCount).Where(i => !duplicated[i])]), a.Bool("ignore_index", false));
            }),
            "value_counts" => M(["subset", "normalize", "sort", "ascending", "dropna"], ValueCounts),
            "nlargest" => M(["n", "columns", "keep"], a => Largest(a, largest: true)),
            "nsmallest" => M(["n", "columns", "keep"], a => Largest(a, largest: false)),
            "corr" => M(["method", "min_periods", "numeric_only"], a => Pairwise(a, (x, y) => Pearson(x, y))),
            "cov" => M(["min_periods", "ddof", "numeric_only"], a => Pairwise(a, (x, y) => Covariance(x, y, a.Int("ddof", 1)))),
            "sample" => M(["n", "frac", "replace", "weights", "random_state", "axis", "ignore_index"], a =>
            {
                var sampled = TakeRows(PdSample.Positions(RowCount, a));
                return a.Bool("ignore_index", false) ? sampled.ResetIndex(drop: true) : sampled;
            }),
            "replace" => M(["to_replace", "value", "inplace", "limit", "regex"], a => Inplace(a, Replace(a))),
            "query" => M(["expr", "inplace"], a => Inplace(a, TakeRows(PdSelect.Mask(PdQuery.Evaluate(this, a.Str("expr") ?? throw Pd.TypeError("query() missing 1 required positional argument: 'expr'")), Index)))),
            "eval" => M(["expr", "inplace"], a => PdQuery.EvaluateValue(this, a.Str("expr") ?? throw Pd.TypeError("eval() missing 1 required positional argument: 'expr'"))),
            "abs" => M([], _ => Unary("abs")!),
            "round" => M(["decimals"], a => Round(a.Given("decimals"))),
            "clip" => M(["lower", "upper", "axis", "inplace"], a => Inplace(a, PerColumn(c => (PyObject)Column(c).GetAttribute("clip")!, a))),
            "where" => M(["cond", "other", "inplace", "axis", "level"], a => Inplace(a, Where(a.Required("cond"), a.Given("other"), keep: true))),
            "mask" => M(["cond", "other", "inplace", "axis", "level"], a => Inplace(a, Where(a.Required("cond"), a.Given("other"), keep: false))),
            "isin" => M(["values"], a => IsIn(a.Required("values"))),
            "idxmax" => M(["axis", "skipna", "numeric_only"], a => ArgExtreme(a, max: true)),
            "idxmin" => M(["axis", "skipna", "numeric_only"], a => ArgExtreme(a, max: false)),
            "cumsum" or "cumprod" or "cummax" or "cummin" => M(["axis", "skipna"], a => MapColumns(c => PdWindow.Cumulative(c, name, a.Bool("skipna", true)))),
            "shift" => M(["periods", "freq", "axis", "fill_value"], a => MapColumns(c => PdWindow.Shift(c, a.Int("periods", 1), a.Given("fill_value")))),
            "diff" => M(["periods", "axis"], a => MapColumns(c => PdWindow.Diff(c, a.Int("periods", 1)))),
            "pct_change" => M(["periods", "fill_method", "limit", "freq"], a => MapColumns(c => PdWindow.PctChange(c, a.Int("periods", 1)))),
            "rank" => M(["axis", "method", "numeric_only", "na_option", "ascending", "pct"], a => MapColumns(c => PdWindow.Rank(c, a.Str("method", "average"), a.Bool("ascending", true), a.Bool("pct", false), a.Str("na_option", "keep")))),
            "rolling" => M(["window", "min_periods", "center", "win_type", "on", "axis", "closed"], a => new PdWindow(null, this, a.Int("window", 1), a.Given("min_periods") is { } mp ? Pd.ToInt(mp, "min_periods") : null, a.Bool("center", false), expanding: false)),
            "expanding" => M(["min_periods", "axis", "method"], a => new PdWindow(null, this, 0, a.Int("min_periods", 1), false, expanding: true)),
            "agg" or "aggregate" => M(["func", "axis"], a => Aggregate(a), extra: true),
            "transform" => M(["func", "axis"], a => PerColumn(c => Column(c).GetAttribute("transform")!, a, forward: ["func"]), extra: true),
            "pipe" => M(["func"], a => runtime.Machine.Call(a.Required("func"), [this], PdSeries.Keywords(a)), extra: true),
            "equals" => M(["other"], a => PyBool.Of(a.Required("other") is PdFrame other && Columns.SameAs(other.Columns) && Index.SameAs(other.Index)
                && Enumerable.Range(0, Columns.Count).All(c => Enumerable.Range(0, RowCount).All(r => Pd.Same(Data[c].Values[r], other.Data[c].Values[r]))))),
            "select_dtypes" => M(["include", "exclude"], a => SelectDTypes(a.Given("include"), a.Given("exclude"))),
            "nunique" => M(["axis", "dropna"], a => new PdSeries(PdColumn.Ints([.. Enumerable.Range(0, Columns.Count).Select(c => (long)Column(c).CountUnique(a.Bool("dropna", true)))]), Columns, null)),
            "explode" => M(["column", "ignore_index"], a => Explode(a.Required("column"), a.Bool("ignore_index", false))),
            "quantile" => M(["q", "axis", "numeric_only", "interpolation"], a => Quantile(a.Given("q") ?? new PyFloat(0.5), a.Bool("numeric_only", false))),
            "mode" => M(["axis", "numeric_only", "dropna"], _ => PdReshape.ConcatColumns([.. Enumerable.Range(0, Columns.Count).Select(c => (PdSeries)runtime.Call(Column(c).GetAttribute("mode")!))])),
            "reindex" => M(["labels", "index", "columns", "axis", "fill_value", "method"], Reindex),
            "filter" => M(["items", "like", "regex", "axis"], Filter),
            "memory_usage" => M(["index", "deep"], _ => new PdSeries(PdColumn.Ints([.. Data.Select(c => (long)c.Length * 8)]), Columns, null)),
            "squeeze" => M(["axis"], _ => Columns.Count == 1 ? RowCount == 1 ? Data[0].Values[0] : Column(0) : this),
            "first_valid_index" => M([], _ => Enumerable.Range(0, RowCount).FirstOrDefault(r => Data.Any(d => !d.IsMissing(r)), -1) is var at && at >= 0 ? Index[at] : PyNone.Instance),
            _ => ColumnAttribute(name),
        };
    }

    private PdSeries? ColumnAttribute(string name) =>
        !name.StartsWith("__", StringComparison.Ordinal) && Columns.Labels.Type == DType.Object && Columns.Find(new PyStr(name)) is var at && at >= 0 ? Column(at) : null;

    private PyObject Values(PdRuntime runtime) =>
        runtime.Array(new PyList([.. Enumerable.Range(0, RowCount).Select(r => (PyObject)new PyList([.. Row(r).Data.Values]))]));

    private static int Axis(PyObject? axis) => axis switch
    {
        null or PyNone => 0,
        PyStr { Value: "index" or "rows" } => 0,
        PyStr { Value: "columns" } => 1,
        PyInt { Value.IsZero: true } => 0,
        PyInt { Value.IsOne: true } => 1,
        _ => throw Pd.ValueError($"No axis named {axis.Repr()} for object type DataFrame"),
    };

    private PyObject Inplace(PdArgs arguments, PdFrame result, bool ignoreIndex = false)
    {
        if (ignoreIndex)
        {
            result = result.ResetIndex(drop: true);
        }

        if (!arguments.Bool("inplace", false))
        {
            return result;
        }

        Become(result);
        return PyNone.Instance;
    }

    public PdFrame MapColumns(Func<PdColumn, PdColumn> map) => new(Columns, [.. Data.Select(map)], Index);

    public PdFrame Head(int n)
    {
        var count = n >= 0 ? Math.Min(n, RowCount) : Math.Max(0, RowCount + n);
        return new PdFrame(Columns, [.. Data.Select(d => d.Range(0, count))], Index.Slice(0, count));
    }

    public PdFrame Tail(int n)
    {
        var count = n >= 0 ? Math.Min(n, RowCount) : Math.Max(0, RowCount + n);
        return new PdFrame(Columns, [.. Data.Select(d => d.Range(RowCount - count, count))], Index.Slice(RowCount - count, count));
    }

    public PdFrame Transpose()
    {
        var data = Enumerable.Range(0, RowCount).Select(r => Row(r).Data).ToList();
        return new PdFrame(Index, data, Columns);
    }

    /// <summary>A reduction per column (axis 0) or per row (axis 1).</summary>
    private PyObject Reduce(string how, PdArgs arguments)
    {
        var skipna = arguments.Bool("skipna", true);
        var numericOnly = arguments.Bool("numeric_only", false);
        var ddof = arguments.Int("ddof", 1);
        var minCount = arguments.Int("min_count", 0);
        var positions = Enumerable.Range(0, Columns.Count)
            .Where(c => !numericOnly || Data[c].IsNumeric)
            .ToList();

        if (how is "count" or "nunique" or "any" or "all")
        {
            numericOnly = false;
        }

        if (!numericOnly && PdReduce.NeedsNumbers(how))
        {
            foreach (var c in positions)
            {
                if (!Data[c].IsNumeric && Data[c].Type != DType.DateTime && !Data[c].Values.All(static v => Pd.IsNumber(v) || Pd.IsMissing(v)))
                {
                    throw Pd.TypeError($"could not convert column {Column(c).Name.Repr()} of dtype {Pd.Name(Data[c].Type)} to numeric; pass numeric_only=True");
                }
            }
        }

        if (Axis(arguments["axis"]) == 1)
        {
            var subset = TakeColumns(positions);
            var results = Enumerable.Range(0, RowCount).Select(r => PdReduce.Reduce(subset.Row(r).Data, how, skipna, ddof, minCount)).ToList();
            return new PdSeries(how == "count" ? PdColumn.Infer(results) : PdColumn.Infer(results), Index, null);
        }

        var values = positions.Select(c => PdReduce.Reduce(Data[c], how, skipna, ddof, minCount)).ToList();
        var column = PdColumn.Infer(values);

        if (how is "mean" or "median" or "std" or "var" or "sem" or "skew" && column.Type == DType.Int64)
        {
            column = PdColumn.OfType(DType.Float64, column.Values);
        }

        return new PdSeries(column, Columns.Take(positions), null);
    }

    private PyObject IterTuples(bool index, string? name)
    {
        var fields = new List<string>();

        if (index)
        {
            fields.Add("Index");
        }

        for (var c = 0; c < Columns.Count; c++)
        {
            var label = Columns[c] is PyStr text && IsIdentifier(text.Value) && !fields.Contains(text.Value) ? text.Value : $"_{c + (index ? 1 : 0)}";
            fields.Add(label);
        }

        var type = name is null ? null : new PyNamedTupleType(name, fields);

        return new PyIterator(Enumerable.Range(0, RowCount).Select(r =>
        {
            var values = new List<PyObject>();

            if (index)
            {
                values.Add(Index[r]);
            }

            values.AddRange(Data.Select(d => d.Values[r]));
            return type is null ? new PyTuple(values) : type.Instantiate([.. values], null);
        }));

        static bool IsIdentifier(string text) =>
            text.Length > 0 && (char.IsLetter(text[0]) || text[0] == '_') && text.All(static ch => char.IsLetterOrDigit(ch) || ch == '_')
            && text is not ("class" or "def" or "return" or "if" or "else" or "for" or "while" or "in" or "is" or "not" or "and" or "or" or "import" or "from" or "as" or "with" or "lambda" or "None" or "True" or "False" or "pass" or "break" or "continue" or "try" or "except" or "finally" or "raise" or "global" or "nonlocal" or "del" or "yield" or "assert" or "elif" or "async" or "await");
    }

    private PyObject Info(PdRuntime runtime)
    {
        var builder = new StringBuilder();
        builder.Append("<class 'pandas.core.frame.DataFrame'>\n");

        var indexKind = Index.IsRange ? "RangeIndex" : Index.Labels.Type == DType.DateTime ? "DatetimeIndex" : "Index";
        builder.Append(CultureInfo.InvariantCulture, $"{indexKind}: {RowCount} entries");

        if (RowCount > 0)
        {
            builder.Append(CultureInfo.InvariantCulture, $", {PdFormat.LabelText(Index[0], Index.Labels.Type)} to {PdFormat.LabelText(Index[RowCount - 1], Index.Labels.Type)}");
        }

        builder.Append('\n');

        if (Columns.Count == 0)
        {
            builder.Append("Empty DataFrame\n");
        }
        else
        {
            builder.Append(CultureInfo.InvariantCulture, $"Data columns (total {Columns.Count} columns):\n");
            var rows = Enumerable.Range(0, Columns.Count).Select(c => new[]
            {
                " " + c.ToString(CultureInfo.InvariantCulture),
                Pd.Text(Columns[c]),
                $"{PdReduce.Valid(Data[c]).Count} non-null",
                Pd.Name(Data[c].Type),
            }).ToList();

            string[] headers = [" # ", "Column", "Non-Null Count", "Dtype"];
            var widths = headers.Select((h, i) => Math.Max(h.Length, rows.Max(r => r[i].Length))).ToArray();

            void Line(IReadOnlyList<string> cells) =>
                builder.Append(string.Join("  ", cells.Select((cell, i) => cell.PadRight(widths[i])))).Append('\n');

            Line(headers);
            Line([.. headers.Select(static h => new string('-', h.Length))]);
            rows.ForEach(Line);
        }

        var counts = Data.GroupBy(static d => Pd.Name(d.Type)).OrderBy(static g => g.Key, StringComparer.Ordinal)
            .Select(static g => $"{g.Key}({g.Count()})");
        builder.Append("dtypes: ").Append(string.Join(", ", counts)).Append('\n');

        double bytes = (Index.IsRange ? 132 : RowCount * 8) + Data.Sum(d => d.Length * 8.0);
        var plus = Data.Any(static d => d.Type == DType.Object) || Index.Labels.Type == DType.Object ? "+" : string.Empty;
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        var unit = 0;

        while (bytes >= 1024 && unit < units.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }

        builder.Append(CultureInfo.InvariantCulture, $"memory usage: {bytes:0.0}{plus} {units[unit]}\n");
        runtime.Machine.Write(builder.ToString());
        return PyNone.Instance;
    }

    private PyObject Describe(PdArgs arguments)
    {
        var include = arguments.Given("include");
        var percentiles = arguments.Given("percentiles") is { } p ? Pd.List(p).Select(Pd.ToDouble).ToList() : null;
        var all = include is PyStr { Value: "all" };
        var wantObject = all || (include is not null && Pd.Text(include) is "object" or "O" or "str") || (include is PyList list && list.Items.Any(static i => Pd.Text(i) is "object" or "O"));
        var numeric = Enumerable.Range(0, Columns.Count).Where(c => Data[c].IsNumeric && Data[c].Type != DType.Bool).ToList();
        var chosen = all
            ? Enumerable.Range(0, Columns.Count).ToList()
            : wantObject ? Enumerable.Range(0, Columns.Count).Where(c => !Data[c].IsNumeric).ToList()
            : numeric.Count > 0 ? numeric
            : Enumerable.Range(0, Columns.Count).Where(c => Data[c].Type is DType.DateTime or DType.Object or DType.Bool).ToList();

        var described = chosen.Select(c => Column(c).Describe(percentiles)).ToList();

        if (described.Count == 0)
        {
            throw Pd.ValueError("Cannot describe a DataFrame without columns");
        }

        return PdReshape.ConcatColumns(described, sortLabels: false, preferredOrder: ["count", "unique", "top", "freq", "mean", "std", "min"]);
    }

    private PyObject ToDict(string orient)
    {
        switch (orient)
        {
            case "dict":
            {
                var result = new PyDict();
                for (var c = 0; c < Columns.Count; c++)
                {
                    var inner = new PyDict();
                    for (var r = 0; r < RowCount; r++)
                    {
                        inner.Set(Index[r], Data[c].Values[r]);
                    }

                    result.Set(Columns[c], inner);
                }

                return result;
            }

            case "list":
            {
                var result = new PyDict();
                for (var c = 0; c < Columns.Count; c++)
                {
                    result.Set(Columns[c], new PyList([.. Data[c].Values]));
                }

                return result;
            }

            case "series":
            {
                var result = new PyDict();
                for (var c = 0; c < Columns.Count; c++)
                {
                    result.Set(Columns[c], Column(c));
                }

                return result;
            }

            case "records":
                return new PyList([.. Enumerable.Range(0, RowCount).Select(r =>
                {
                    var row = new PyDict();
                    for (var c = 0; c < Columns.Count; c++)
                    {
                        row.Set(Columns[c], Data[c].Values[r]);
                    }

                    return (PyObject)row;
                })]);

            case "index":
            {
                var result = new PyDict();
                for (var r = 0; r < RowCount; r++)
                {
                    var row = new PyDict();
                    for (var c = 0; c < Columns.Count; c++)
                    {
                        row.Set(Columns[c], Data[c].Values[r]);
                    }

                    result.Set(Index[r], row);
                }

                return result;
            }

            case "split" or "tight":
            {
                var result = new PyDict();
                result.Set(new PyStr("index"), Index.ToList());
                result.Set(new PyStr("columns"), Columns.ToList());
                result.Set(new PyStr("data"), new PyList([.. Enumerable.Range(0, RowCount).Select(r => (PyObject)new PyList([.. Data.Select(d => d.Values[r])]))]));
                return result;
            }

            default:
                throw Pd.ValueError($"orient '{orient}' not understood");
        }
    }

    private PdFrame AsType(PyObject dtype)
    {
        if (dtype is PyDict mapping)
        {
            var data = Data.ToList();

            foreach (var (label, type) in mapping.Entries)
            {
                var at = Columns.Find(label);

                if (at < 0)
                {
                    throw Pd.KeyError(new PyStr("Only a column name can be used for the key in a dtype mappings argument. "
                        + $"'{Pd.Text(label)}' not found in columns."));
                }

                var (target, spelling) = PdDTypeObject.Parse(type);
                data[at] = Data[at].Cast(target, spelling);
            }

            return new PdFrame(Columns, data, Index);
        }

        var (parsed, text) = PdDTypeObject.Parse(dtype);
        return MapColumns(c => c.Cast(parsed, text));
    }

    private PyObject Assign(PdArgs arguments)
    {
        var result = ShallowCopy();

        foreach (var (name, value) in arguments.Extra)
        {
            var computed = value is PyCallable and not PyType ? PdRuntime.Current!.Call(value, result) : value;
            result.SetColumn(new PyStr(name), result.ColumnFrom(computed));
        }

        return result;
    }

    private PyObject Insert(PdArgs arguments)
    {
        var position = Pd.ToInt(arguments.Required("loc"), "loc");
        var label = arguments.Required("column");

        if (Columns.Find(label) >= 0 && !arguments.Bool("allow_duplicates", false))
        {
            throw Pd.ValueError($"cannot insert {Pd.Text(label)}, already exists");
        }

        if (position < 0 || position > Columns.Count)
        {
            throw Pd.IndexError($"index {position} is out of bounds for axis 0 with size {Columns.Count}");
        }

        var column = ColumnFrom(arguments.Required("value"));
        var labels = Columns.Labels.Values.ToList();
        labels.Insert(position, label);
        Data.Insert(position, column);
        Columns = PdIndex.Of(labels, Columns.Name);
        return PyNone.Instance;
    }

    private PdFrame Drop(PdArgs arguments)
    {
        PyObject? rows = arguments.Given("index");
        PyObject? columns = arguments.Given("columns");

        if (arguments.Given("labels") is { } labels)
        {
            if (Axis(arguments["axis"]) == 1)
            {
                columns = labels;
            }
            else
            {
                rows = labels;
            }
        }

        if (rows is null && columns is null)
        {
            throw Pd.ValueError("Need to specify at least one of 'labels', 'index' or 'columns'");
        }

        var raise = arguments.Str("errors", "raise") == "raise";
        var result = this;

        if (columns is not null)
        {
            var drop = Positions(columns, Columns, raise);
            result = result.TakeColumns([.. Enumerable.Range(0, Columns.Count).Where(c => !drop.Contains(c))]);
        }

        if (rows is not null)
        {
            var drop = Positions(rows, Index, raise);
            result = result.TakeRows([.. Enumerable.Range(0, RowCount).Where(r => !drop.Contains(r))]);
        }

        return result == this ? ShallowCopy() : result;

        static HashSet<int> Positions(PyObject key, PdIndex axis, bool raise)
        {
            var labels = Pd.IsListLike(key) ? Pd.List(key) : [key];
            var found = new HashSet<int>();
            var missing = new List<PyObject>();

            foreach (var label in labels)
            {
                if (axis.FindAll(label) is { } positions)
                {
                    found.UnionWith(positions);
                }
                else if (PdSelect.Prefix(label, axis) is { Count: > 0 } prefixed)
                {
                    found.UnionWith(prefixed);
                }
                else
                {
                    missing.Add(label);
                }
            }

            if (missing.Count > 0 && raise)
            {
                throw Pd.KeyError(new PyStr($"[{string.Join(", ", missing.Select(static m => m.Repr()))}] not found in axis"));
            }

            return found;
        }
    }

    private PdFrame Rename(PdArgs arguments)
    {
        var columns = arguments.Given("columns");
        var index = arguments.Given("index");

        if (arguments.Given("mapper") is { } mapper)
        {
            if (Axis(arguments["axis"]) == 1)
            {
                columns = mapper;
            }
            else
            {
                index = mapper;
            }
        }

        var raise = arguments.Str("errors", "ignore") == "raise";
        var result = ShallowCopy();

        if (columns is not null)
        {
            result.Columns = Relabel(Columns, columns, raise);
        }

        if (index is not null)
        {
            result.Index = Relabel(Index, index, raise);
        }

        return result;

        static PdIndex Relabel(PdIndex axis, PyObject mapper, bool raise)
        {
            if (raise && mapper is PyDict dict)
            {
                foreach (var (key, _) in dict.Entries)
                {
                    if (axis.Find(key) < 0)
                    {
                        throw Pd.KeyError(new PyStr($"[{key.Repr()}] not found in axis"));
                    }
                }
            }

            var labels = axis.Labels.Values.Select(label => mapper switch
            {
                PyDict map => map.TryGetValue(label, out var renamed) ? renamed : label,
                PdSeries series => series.Index.Find(label) is var at && at >= 0 ? series.Data.Values[at] : label,
                _ => PdRuntime.Current!.Call(mapper, label),
            }).ToList();

            return PdIndex.Of(labels, axis.Name);
        }
    }

    public PdFrame SetIndex(PyObject keys, bool drop, bool append)
    {
        var items = keys is PyList list ? [.. list.Items] : new List<PyObject> { keys };
        var levels = new List<PdColumn>();
        var names = new List<PyObject>();
        var dropped = new HashSet<int>();

        if (append)
        {
            levels.Add(Index.Labels);
            names.Add(Index.Name);
        }

        foreach (var key in items)
        {
            switch (key)
            {
                case PdSeries series:
                    levels.Add(ColumnFrom(series));
                    names.Add(series.Name);
                    break;
                case PdIndex index:
                    levels.Add(index.Labels);
                    names.Add(index.Name);
                    break;
                default:
                    if (Pd.IsListLike(key) && key is not PyTuple)
                    {
                        levels.Add(ColumnFrom(key));
                        names.Add(PyNone.Instance);
                        break;
                    }

                    var at = Columns.Find(key);

                    if (at < 0)
                    {
                        throw Pd.KeyError(new PyStr($"None of [{key.Repr()}] are in the columns"));
                    }

                    levels.Add(Data[at]);
                    names.Add(key);
                    dropped.Add(at);
                    break;
            }
        }

        var newIndex = levels.Count == 1
            ? new PdIndex(levels[0].Clone(), names[0])
            : new PdIndex(PdColumn.Infer([.. Enumerable.Range(0, RowCount).Select(r => (PyObject)new PyTuple([.. levels.Select(l => l.Values[r])]))]), null, names);

        var kept = Enumerable.Range(0, Columns.Count).Where(c => !drop || !dropped.Contains(c)).ToList();
        var result = TakeColumns(kept);
        result.Index = newIndex;
        return result;
    }

    private PdFrame SortValues(PdArgs arguments)
    {
        var by = arguments.Required("by");
        var labels = Pd.IsListLike(by) ? Pd.List(by) : [by];
        var ascending = arguments.Given("ascending") is { } given && Pd.IsListLike(given)
            ? Pd.List(given).Select(static g => g.IsTruthy()).ToList()
            : [arguments.Bool("ascending", true)];

        if (ascending.Count != 1 && ascending.Count != labels.Count)
        {
            throw Pd.ValueError($"Length of ascending ({ascending.Count}) != length of by ({labels.Count})");
        }

        var keys = labels.Select(label =>
        {
            var at = Columns.Find(label);
            if (at < 0)
            {
                throw Pd.KeyError(label);
            }

            var column = Data[at];

            if (arguments.Given("key") is { } key)
            {
                column = ((PdSeries)PdRuntime.Current!.Call(key, Column(at))).Data;
            }

            return column;
        }).ToList();

        return TakeRows(PdSort.Order(keys, ascending, arguments.Str("na_position", "last")));
    }

    private PdFrame SortIndex(PdArgs arguments)
    {
        var ascending = arguments.Bool("ascending", true);

        if (Axis(arguments["axis"]) == 1)
        {
            return TakeColumns(PdSort.Order([Columns.Labels], [ascending], "last"));
        }

        return TakeRows(PdSort.Order([Index.Labels], [ascending], arguments.Str("na_position", "last")));
    }

    private PdFrame FillNa(PyObject? value, string? method, PyObject? limit)
    {
        var data = new List<PdColumn>();

        for (var c = 0; c < Columns.Count; c++)
        {
            var fill = value switch
            {
                PyDict dict => dict.TryGetValue(Columns[c], out var v) ? v : null,
                PdSeries series => series.Index.Find(Columns[c]) is var at && at >= 0 ? series.Data.Values[at] : null,
                _ => value,
            };

            data.Add(method is null && fill is null ? Data[c] : new PdSeries(Data[c], Index, null).FillNa(fill, method, limit).Data);
        }

        return new PdFrame(Columns, data, Index);
    }

    private PdFrame DropNa(PdArgs arguments)
    {
        var how = arguments.Str("how", "any");
        var subset = arguments.Given("subset") is { } s ? ColumnPositions(Pd.IsListLike(s) ? Pd.List(s) : [s]) : [.. Enumerable.Range(0, Columns.Count)];
        var thresh = arguments.Given("thresh") is { } t ? Pd.ToInt(t, "thresh") : (int?)null;

        if (Axis(arguments["axis"]) == 1)
        {
            var keep = Enumerable.Range(0, Columns.Count).Where(c =>
            {
                var missing = Enumerable.Range(0, RowCount).Count(Data[c].IsMissing);
                return thresh is { } th ? RowCount - missing >= th : how == "all" ? missing < RowCount || RowCount == 0 : missing == 0;
            }).ToList();

            return TakeColumns(keep);
        }

        var rows = Enumerable.Range(0, RowCount).Where(r =>
        {
            var missing = subset.Count(c => Data[c].IsMissing(r));
            return thresh is { } th ? subset.Count - missing >= th : how == "all" ? missing < subset.Count : missing == 0;
        }).ToList();

        return TakeRows(rows);
    }

    private PyObject Apply(PdArgs arguments)
    {
        var function = arguments.Required("func");
        var runtime = PdRuntime.Current!;
        var extra = arguments.Given("args") is { } args ? Pd.List(args) : [];
        var keywords = PdSeries.Keywords(arguments);
        var byRow = Axis(arguments["axis"]) == 1;
        var count = byRow ? RowCount : Columns.Count;
        var results = new List<PyObject>(count);

        if (function is PyStr text)
        {
            return byRow
                ? Reduce(text.Value, new PdArgs(text.Value, [], null, ReduceParameters).WithAxis(PyInt.From(1)))
                : runtime.Call(GetAttribute(text.Value) ?? throw Pd.ValueError($"{text.Value} is not a valid function"));
        }

        for (var i = 0; i < count; i++)
        {
            var slice = byRow ? Row(i) : Column(i);
            results.Add(runtime.Machine.Call(function, [slice, .. extra], keywords));
        }

        var labels = byRow ? Index : Columns;

        if (results.Count > 0 && results.All(static r => r is PdSeries))
        {
            var series = results.Cast<PdSeries>().ToList();
            var frame = PdReshape.ConcatColumns(series, sortLabels: false);
            frame.Columns = labels;
            return byRow ? frame.Transpose() : frame;
        }

        return new PdSeries(PdColumn.Infer(results), labels, null);
    }

    private List<bool> Duplicated(PyObject? subset, PyObject? keep)
    {
        var columns = subset is null ? [.. Enumerable.Range(0, Columns.Count)] : ColumnPositions(Pd.IsListLike(subset) ? Pd.List(subset) : [subset]);
        var rows = Enumerable.Range(0, RowCount).Select(r => (IReadOnlyList<PyObject>)columns.Select(c => Data[c].Values[r]).ToList()).ToList();
        return PdOps.Duplicated(rows, keep ?? new PyStr("first"));
    }

    private PyObject ValueCounts(PdArgs arguments)
    {
        var subset = arguments.Given("subset") is { } s ? ColumnPositions(Pd.IsListLike(s) ? Pd.List(s) : [s]) : [.. Enumerable.Range(0, Columns.Count)];
        var keys = Enumerable.Range(0, RowCount)
            .Where(r => !arguments.Bool("dropna", true) || subset.All(c => !Data[c].IsMissing(r)))
            .Select(r => subset.Count == 1 ? Data[subset[0]].Values[r] : new PyTuple([.. subset.Select(c => Data[c].Values[r])]))
            .ToList();
        var series = new PdSeries(PdColumn.Infer(keys), PdIndex.Default(keys.Count), null);
        var counts = series.ValueCounts(new PdArgs("value_counts", [], null, ["normalize", "sort", "ascending", "dropna"]) );
        var normalize = arguments.Bool("normalize", false);
        var ascending = arguments.Bool("ascending", false);

        if (ascending)
        {
            counts = counts.Take(PdSort.Order([counts.Data], [true], "last"));
        }

        var names = subset.Select(c => Columns[c]).ToList();
        var index = subset.Count == 1 ? counts.Index.WithName(names[0]) : new PdIndex(counts.Index.Labels, null, names);

        if (normalize)
        {
            var total = counts.Data.Values.Sum(static v => (double)((PyInt)v).Value);
            return new PdSeries(PdColumn.Floats([.. counts.Data.Values.Select(v => (double)((PyInt)v).Value / total)]), index, new PyStr("proportion"));
        }

        return new PdSeries(counts.Data, index, new PyStr("count"));
    }

    private PyObject Largest(PdArgs arguments, bool largest)
    {
        var n = arguments.Int("n", 5);
        var columns = arguments.Required("columns");
        var labels = Pd.IsListLike(columns) ? Pd.List(columns) : [columns];
        var keys = ColumnPositions(labels).Select(c => Data[c]).ToList();
        var order = PdSort.Order(keys, [.. keys.Select(_ => !largest)], "last");
        return TakeRows([.. order.Where(r => !keys[0].IsMissing(r)).Take(n)]);
    }

    private PdFrame Pairwise(PdArgs arguments, Func<PdSeries, PdSeries, double> measure)
    {
        var numericOnly = arguments.Bool("numeric_only", false);
        var positions = Enumerable.Range(0, Columns.Count).Where(c => Data[c].IsNumeric).ToList();

        if (!numericOnly && positions.Count != Columns.Count)
        {
            var bad = Enumerable.Range(0, Columns.Count).First(c => !Data[c].IsNumeric);
            var sample = Data[bad].Values.FirstOrDefault(static v => !Pd.IsMissing(v)) ?? PyNone.Instance;
            throw Pd.ValueError($"could not convert string to float: {sample.Repr()}");
        }

        var labels = Columns.Take(positions);
        var data = positions.Select(x => PdColumn.Floats([.. positions.Select(y => measure(Column(x), Column(y)))])).ToList();
        return new PdFrame(labels, data, labels);
    }

    private PdFrame Replace(PdArgs arguments)
    {
        var toReplace = arguments["to_replace"];
        var value = arguments["value"];
        var regex = arguments.Bool("regex", false);

        // {column: {old: new}} and {column: old} with a value are per column.
        if (toReplace is PyDict nested && (nested.Entries.Any(static e => e.Value is PyDict) || (value is not null and not PyNone && nested.Entries.All(e => Columns.Find(e.Key) >= 0))))
        {
            var data = Data.ToList();

            foreach (var (label, rule) in nested.Entries)
            {
                var at = Columns.Find(label);

                if (at >= 0)
                {
                    data[at] = rule is PyDict ? PdBuild.ReplaceValues(data[at], rule, null, regex) : PdBuild.ReplaceValues(data[at], rule, value, regex);
                }
            }

            return new PdFrame(Columns, data, Index);
        }

        if (value is PyDict perColumn)
        {
            var data = Data.ToList();

            foreach (var (label, replacement) in perColumn.Entries)
            {
                var at = Columns.Find(label);

                if (at >= 0)
                {
                    data[at] = PdBuild.ReplaceValues(data[at], toReplace, replacement, regex);
                }
            }

            return new PdFrame(Columns, data, Index);
        }

        return MapColumns(c => PdBuild.ReplaceValues(c, toReplace, value, regex));
    }

    private PdFrame Round(PyObject? decimals)
    {
        if (decimals is PyDict perColumn)
        {
            var data = Data.ToList();

            foreach (var (label, places) in perColumn.Entries)
            {
                var at = Columns.Find(label);
                if (at >= 0)
                {
                    data[at] = PdSeries.RoundColumn(data[at], Pd.ToInt(places, "decimals"));
                }
            }

            return new PdFrame(Columns, data, Index);
        }

        var digits = decimals is null ? 0 : Pd.ToInt(decimals, "decimals");
        return MapColumns(c => PdSeries.RoundColumn(c, digits));
    }

    /// <summary>Calls a Series method on every column, forwarding the arguments.</summary>
    private PdFrame PerColumn(Func<int, PyObject> method, PdArgs arguments, IReadOnlyList<string>? forward = null)
    {
        var runtime = PdRuntime.Current!;
        var keywords = new PyDict();

        foreach (var name in forward ?? ["lower", "upper"])
        {
            if (arguments[name] is { } value)
            {
                keywords.Set(new PyStr(name), value);
            }
        }

        foreach (var (name, value) in arguments.Extra)
        {
            keywords.Set(new PyStr(name), value);
        }

        var data = Enumerable.Range(0, Columns.Count)
            .Select(c => ((PdSeries)runtime.Machine.Call(method(c), [], keywords)).Data)
            .ToList();
        return new PdFrame(Columns, data, Index);
    }

    private PdFrame Where(PyObject condition, PyObject? other, bool keep)
    {
        if (condition is PyCallable and not PyType)
        {
            condition = PdRuntime.Current!.Call(condition, this);
        }

        if (condition is PdSeries series)
        {
            var mask = new PdFrame(Columns, [.. Columns.Labels.Values.Select(_ => new PdSeries(series.Data, series.Index, null).Data)], series.Index);
            return WhereFrame(mask, other, keep);
        }

        return WhereFrame((PdFrame)condition, other, keep);
    }

    private PdFrame IsIn(PyObject values)
    {
        if (values is PyDict perColumn)
        {
            return new PdFrame(Columns, [.. Enumerable.Range(0, Columns.Count).Select(c =>
                perColumn.TryGetValue(Columns[c], out var allowed)
                    ? Column(c).IsIn(allowed)
                    : PdColumn.Bools(new bool[RowCount]))], Index);
        }

        return MapColumns(c => new PdSeries(c, Index, null).IsIn(values));
    }

    private PyObject ArgExtreme(PdArgs arguments, bool max)
    {
        if (Axis(arguments["axis"]) == 1)
        {
            return new PdSeries(
                PdColumn.Infer([.. Enumerable.Range(0, RowCount).Select(r => Columns[Row(r).ArgExtreme(max)])]),
                Index,
                null);
        }

        var positions = Enumerable.Range(0, Columns.Count).Where(c => !arguments.Bool("numeric_only", false) || Data[c].IsNumeric).ToList();
        return new PdSeries(PdColumn.Infer([.. positions.Select(c => Index[Column(c).ArgExtreme(max)])]), Columns.Take(positions), null);
    }

    private PyObject Aggregate(PdArgs arguments)
    {
        var function = arguments.Required("func");
        var runtime = PdRuntime.Current!;

        switch (function)
        {
            case PyStr text:
                return Reduce(text.Value, new PdArgs(text.Value, [], null, ReduceParameters).WithAxis(arguments["axis"]));

            case PyList or PyTuple:
            {
                var functions = Pd.List(function);
                var rows = functions.Select(f => (PdSeries)new PdSeries(PdColumn.Infer([.. Enumerable.Range(0, Columns.Count).Select(c => AggregateColumn(c, f))]), Columns, PdGroupBy.FunctionName(f))).ToList();
                return PdReshape.ConcatColumns(rows, sortLabels: false).Transpose();
            }

            case PyDict dict:
            {
                var anyList = dict.Entries.Any(static e => e.Value is PyList or PyTuple);

                if (!anyList)
                {
                    return new PdSeries(
                        PdColumn.Infer([.. dict.Entries.Select(e => AggregateColumn(Columns.Locate(e.Key), e.Value))]),
                        PdIndex.Of([.. dict.Entries.Select(static e => e.Key)]),
                        null);
                }

                var parts = dict.Entries.Select(e =>
                {
                    var functions = Pd.IsListLike(e.Value) ? Pd.List(e.Value) : [e.Value];
                    var at = Columns.Locate(e.Key);
                    return new PdSeries(PdColumn.Infer([.. functions.Select(f => AggregateColumn(at, f))]), PdIndex.Of([.. functions.Select(PdGroupBy.FunctionName)]), e.Key);
                }).ToList();

                return PdReshape.ConcatColumns(parts, sortLabels: false);
            }

            default:
                return new PdSeries(PdColumn.Infer([.. Enumerable.Range(0, Columns.Count).Select(c => runtime.Call(function, Column(c)))]), Columns, null);
        }
    }

    private PyObject AggregateColumn(int column, PyObject function) =>
        function is PyStr text ? PdGroupBy.ReduceNamed(Column(column), text.Value) : PdRuntime.Current!.Call(function, Column(column));

    private PdFrame SelectDTypes(PyObject? include, PyObject? exclude)
    {
        static HashSet<string> Kinds(PyObject? spec)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);

            if (spec is null)
            {
                return result;
            }

            foreach (var item in Pd.IsListLike(spec) ? Pd.List(spec) : [spec])
            {
                var text = item is PyType type ? type.Name : Pd.Text(item);
                switch (text)
                {
                    case "number" or "numeric":
                        result.UnionWith(["int64", "float64"]);
                        break;
                    case "int" or "int64" or "integer" or "int32":
                        result.Add("int64");
                        break;
                    case "float" or "float64" or "floating" or "float32":
                        result.Add("float64");
                        break;
                    case "object" or "O" or "str" or "string":
                        result.Add("object");
                        break;
                    case "bool" or "boolean":
                        result.Add("bool");
                        break;
                    case "datetime" or "datetime64" or "datetime64[ns]":
                        result.Add("datetime64[ns]");
                        break;
                    case "category":
                        break;
                    default:
                        result.Add(text);
                        break;
                }
            }

            return result;
        }

        var included = Kinds(include);
        var excluded = Kinds(exclude);
        var positions = Enumerable.Range(0, Columns.Count)
            .Where(c => (included.Count == 0 || included.Contains(Pd.Name(Data[c].Type))) && !excluded.Contains(Pd.Name(Data[c].Type)))
            .ToList();
        return TakeColumns(positions);
    }

    private PdFrame Explode(PyObject column, bool ignoreIndex)
    {
        var at = Columns.Locate(column);
        var rows = new List<int>();
        var values = new List<PyObject>();

        for (var r = 0; r < RowCount; r++)
        {
            var value = Data[at].Values[r];

            if (value is PyList or PyTuple or PySet)
            {
                var items = Pd.List(value);

                if (items.Count == 0)
                {
                    rows.Add(r);
                    values.Add(Pd.NaN);
                }

                foreach (var item in items)
                {
                    rows.Add(r);
                    values.Add(item);
                }
            }
            else
            {
                rows.Add(r);
                values.Add(value);
            }
        }

        var result = TakeRows(rows);
        result.Data[at] = new PdColumn(DType.Object, [.. values]);
        return ignoreIndex ? result.ResetIndex(drop: true) : result;
    }

    private PyObject Quantile(PyObject q, bool numericOnly)
    {
        var positions = Enumerable.Range(0, Columns.Count).Where(c => Data[c].IsNumeric || !numericOnly).ToList();

        if (Pd.IsListLike(q))
        {
            var levels = Pd.List(q);
            var data = positions.Select(c => PdColumn.Floats([.. levels.Select(l => Column(c).Quantile(Pd.ToDouble(l)))])).ToList();
            return new PdFrame(Columns.Take(positions), data, PdIndex.Of([.. levels.Select(l => (PyObject)Pd.Float(Pd.ToDouble(l)))]));
        }

        var level = Pd.ToDouble(q);
        return new PdSeries(PdColumn.Floats([.. positions.Select(c => Column(c).Quantile(level))]), Columns.Take(positions), Pd.Float(level));
    }

    private PyObject Reindex(PdArgs arguments)
    {
        var result = this;
        var fill = arguments.Given("fill_value");
        var rows = arguments.Given("index") ?? (Axis(arguments["axis"]) == 0 ? arguments.Given("labels") : null);
        var columns = arguments.Given("columns") ?? (Axis(arguments["axis"]) == 1 ? arguments.Given("labels") : null);

        if (rows is not null)
        {
            var target = PdBuild.IndexFrom(rows);
            var positions = Enumerable.Range(0, target.Count).Select(i => Index.Find(target[i])).ToList();
            var missing = positions.Select((p, i) => (p, i)).Where(static x => x.p < 0).Select(static x => x.i).ToList();
            result = new PdFrame(Columns, [.. Data.Select(d => fill is null || missing.Count == 0 ? d.Take(positions) : d.Take(positions).With(missing, fill))], target);
        }

        if (columns is not null)
        {
            var target = PdBuild.IndexFrom(columns);
            var data = Enumerable.Range(0, target.Count).Select(i =>
            {
                var at = result.Columns.Find(target[i]);
                return at >= 0 ? result.Data[at] : PdColumn.Repeat(fill ?? Pd.NaN, result.RowCount);
            }).ToList();
            result = new PdFrame(target, data, result.Index);
        }

        return result;
    }

    private PyObject Filter(PdArgs arguments)
    {
        var byRows = Axis(arguments["axis"] ?? PyInt.From(1)) == 0 && arguments["axis"] is not null;
        var axis = byRows ? Index : Columns;
        IEnumerable<int> positions;

        if (arguments.Given("items") is { } items)
        {
            positions = Pd.List(items).Select(axis.Find).Where(static p => p >= 0);
        }
        else if (arguments.Str("like") is { } like)
        {
            positions = Enumerable.Range(0, axis.Count).Where(i => Pd.Text(axis[i]).Contains(like, StringComparison.Ordinal));
        }
        else if (arguments.Str("regex") is { } pattern)
        {
            var runtime = PdRuntime.Current!;
            var search = runtime.Machine.Import("re").GetAttribute("search")!;
            positions = Enumerable.Range(0, axis.Count).Where(i => runtime.Call(search, new PyStr(pattern), new PyStr(Pd.Text(axis[i]))) is not PyNone);
        }
        else
        {
            throw Pd.TypeError("Must pass either `items`, `like`, or `regex`");
        }

        return byRows ? TakeRows([.. positions]) : TakeColumns([.. positions]);
    }
}
