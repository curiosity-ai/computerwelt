using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules.Pandas;

/// <summary>The Series' attributes and methods, by name.</summary>
internal sealed partial class PdSeries
{
    private static readonly string[] ReduceParameters = ["axis", "skipna", "numeric_only", "min_count", "ddof"];

    public override bool SetAttribute(string name, PyObject value)
    {
        switch (name)
        {
            case "name":
                Name = value;
                return true;
            case "index":
                var index = PdBuild.IndexFrom(value);
                if (index.Count != Count)
                {
                    throw Pd.ValueError($"Length mismatch: Expected axis has {Count} elements, new values have {index.Count} elements");
                }

                Index = index;
                return true;
            default:
                return false;
        }
    }

    public override PyObject? GetAttribute(string name) => Pd.Guard(() => AttributeCore(name));

    private PyObject? AttributeCore(string name)
    {
        var runtime = PdRuntime.Current;

        switch (name)
        {
            case "name": return Name;
            case "index": return Index;
            case "dtype" or "dtypes": return new PdDTypeObject(Data.Type);
            case "size": return PyInt.From(Count);
            case "shape": return new PyTuple([PyInt.From(Count)]);
            case "ndim": return PyInt.From(1);
            case "empty": return PyBool.Of(Count == 0);
            case "T": return this;
            case "axes": return new PyList([Index]);
            case "is_unique": return PyBool.Of(CountUnique(false) == Count);
            case "hasnans": return PyBool.Of(Enumerable.Range(0, Count).Any(Data.IsMissing));
            case "is_monotonic_increasing": return PyBool.Of(Monotonic(1));
            case "is_monotonic_decreasing": return PyBool.Of(Monotonic(-1));
            case "loc": return new PdIndexer(this, positional: false, scalar: false);
            case "iloc": return new PdIndexer(this, positional: true, scalar: false);
            case "at": return new PdIndexer(this, positional: false, scalar: true);
            case "iat": return new PdIndexer(this, positional: true, scalar: true);
            case "str":
                return Data.Type == DType.Object
                    ? new PdStringAccessor(this, asIndex: false)
                    : throw new PyRaise(new PyException(PyExceptionType.AttributeError, "Can only use .str accessor with string values!"));
            case "dt": return new PdDateAccessor(this, asIndex: false);
        }

        if (runtime is null)
        {
            return null;
        }

        PyBuiltinFunction M(string[] parameters, Func<PdArgs, PyObject> body, bool extra = false) =>
            runtime.Method(name, parameters, body, extra);

        if (PdArith.Methods.TryGetValue(name, out var arith))
        {
            return M(["other", "level", "fill_value", "axis"], a => PdArith.Series(this, arith.Op, arith.Reflected, a.Required("other"), a.Given("fill_value")));
        }

        if (PdArith.Comparisons.TryGetValue(name, out var comparison))
        {
            return M(["other", "level", "fill_value", "axis"], a => Compare(comparison, a.Required("other"))!);
        }

        if (PdReduce.Names.Contains(name) && name is not ("first" or "last" or "size" or "nunique"))
        {
            return M(ReduceParameters, a => Reduce(name, a.Bool("skipna", true), a.Bool("numeric_only", false), a.Int("ddof", 1), a.Int("min_count", 0)));
        }

        return name switch
        {
            "values" => runtime.Array(new PyList([.. Data.Values])),
            "array" => runtime.Array(new PyList([.. Data.Values])),
            "to_numpy" => M(["dtype", "copy", "na_value"], _ => runtime.Array(new PyList([.. Data.Values]))),
            "tolist" or "to_list" => M([], _ => new PyList([.. Data.Values])),
            "head" => M(["n"], a => Head(a.Int("n", 5))),
            "tail" => M(["n"], a => Tail(a.Int("n", 5))),
            "copy" => M(["deep"], _ => Copy()),
            "keys" => M([], _ => Index),
            "items" => M([], _ => new PyIterator(Enumerable.Range(0, Count).Select(i => (PyObject)new PyTuple([Index[i], Data.Values[i]])))),
            "item" => M([], _ => Count == 1 ? Data.Values[0] : throw Pd.ValueError("can only convert an array of size 1 to a Python scalar")),
            "get" => M(["key", "default"], a => Index.Find(a.Required("key")) is var at && at >= 0 ? Data.Values[at] : a["default"] ?? PyNone.Instance),
            "equals" => M(["other"], a => PyBool.Of(a.Required("other") is PdSeries other && Index.SameAs(other.Index) && Enumerable.Range(0, Count).All(i => Pd.Same(Data.Values[i], other.Data.Values[i])))),
            "nunique" => M(["dropna"], a => PyInt.From(CountUnique(a.Bool("dropna", true)))),
            "unique" => M([], _ => runtime.Array(new PyList(UniqueValues()))),
            "value_counts" => M(["normalize", "sort", "ascending", "bins", "dropna"], ValueCounts),
            "describe" => M(["percentiles", "include", "exclude"], a => Describe(a.Given("percentiles") is { } p ? Pd.List(p).Select(Pd.ToDouble).ToList() : null)),
            "idxmax" => M(["axis", "skipna"], _ => Index[ArgExtreme(true)]),
            "idxmin" => M(["axis", "skipna"], _ => Index[ArgExtreme(false)]),
            "argmax" => M(["axis", "skipna"], _ => PyInt.From(ArgExtreme(true))),
            "argmin" => M(["axis", "skipna"], _ => PyInt.From(ArgExtreme(false))),
            "cumsum" or "cumprod" or "cummax" or "cummin" => M(["axis", "skipna"], a => With(PdWindow.Cumulative(Data, name, a.Bool("skipna", true)))),
            "abs" => M([], _ => Unary("abs")!),
            "round" => M(["decimals"], a => With(RoundColumn(Data, a.Int("decimals", 0)))),
            "astype" => M(["dtype", "copy", "errors"], a => AsType(a.Required("dtype"))),
            "isna" or "isnull" => M([], _ => With(MissingMask(Data, true))),
            "notna" or "notnull" => M([], _ => With(MissingMask(Data, false))),
            "fillna" => M(["value", "method", "axis", "inplace", "limit"], a => Inplace(a, FillNa(a.Given("value"), a.Str("method"), a.Given("limit")))),
            "ffill" or "pad" => M(["axis", "inplace", "limit"], a => Inplace(a, FillNa(null, "ffill", a.Given("limit")))),
            "bfill" or "backfill" => M(["axis", "inplace", "limit"], a => Inplace(a, FillNa(null, "bfill", a.Given("limit")))),
            "dropna" => M(["axis", "inplace", "how", "ignore_index"], a => Inplace(a, Take(PdReduce.Valid(Data)), a.Bool("ignore_index", false))),
            "interpolate" => M(["method", "axis", "limit", "inplace", "limit_direction"], a => Inplace(a, With(PdWindow.Interpolate(Data)))),
            "apply" => M(["func", "convert_dtype", "args", "by_row"], a => Apply(a.Required("func"), a.Given("args") is { } extra ? Pd.List(extra) : [], Keywords(a)), extra: true),
            "map" => M(["arg", "na_action"], a => Map(a.Required("arg"), a.Str("na_action") == "ignore")),
            "replace" => M(["to_replace", "value", "inplace", "limit", "regex"], a => Inplace(a, With(PdBuild.ReplaceValues(Data, a["to_replace"], a["value"], a.Bool("regex", false))))),
            "sort_values" => M(["axis", "ascending", "inplace", "kind", "na_position", "ignore_index", "key"], a => Inplace(a, SortValues(a), a.Bool("ignore_index", false))),
            "sort_index" => M(["axis", "level", "ascending", "inplace", "kind", "na_position", "sort_remaining", "ignore_index", "key"], a =>
                Inplace(a, Take(PdSort.Order([Index.Labels], [a.Bool("ascending", true)], a.Str("na_position", "last"))), a.Bool("ignore_index", false))),
            "rank" => M(["axis", "method", "numeric_only", "na_option", "ascending", "pct"], a => With(PdWindow.Rank(Data, a.Str("method", "average"), a.Bool("ascending", true), a.Bool("pct", false), a.Str("na_option", "keep")))),
            "to_dict" => M(["into"], _ => ToDict()),
            "to_frame" => M(["name"], a => ToFrame(a.Given("name"))),
            "between" => M(["left", "right", "inclusive"], a => Between(a.Required("left"), a.Required("right"), a.Str("inclusive", "both"))),
            "isin" => M(["values"], a => With(IsIn(a.Required("values")))),
            "clip" => M(["lower", "upper", "axis", "inplace"], a => Inplace(a, Clip(a.Given("lower"), a.Given("upper")))),
            "shift" => M(["periods", "freq", "axis", "fill_value"], a => With(PdWindow.Shift(Data, a.Int("periods", 1), a.Given("fill_value")))),
            "diff" => M(["periods"], a => With(PdWindow.Diff(Data, a.Int("periods", 1)))),
            "pct_change" => M(["periods", "fill_method", "limit", "freq"], a => With(PdWindow.PctChange(Data, a.Int("periods", 1)))),
            "quantile" => M(["q", "interpolation"], a => QuantileOf(a.Given("q") ?? new PyFloat(0.5))),
            "mode" => M(["dropna"], a => Mode(a.Bool("dropna", true))),
            "nlargest" => M(["n", "keep"], a => Largest(a.Int("n", 5), true)),
            "nsmallest" => M(["n", "keep"], a => Largest(a.Int("n", 5), false)),
            "where" => M(["cond", "other", "inplace", "axis", "level"], a => Inplace(a, Where(a.Required("cond"), a.Given("other"), keepWhereTrue: true))),
            "mask" => M(["cond", "other", "inplace", "axis", "level"], a => Inplace(a, Where(a.Required("cond"), a.Given("other"), keepWhereTrue: false))),
            "rolling" => M(["window", "min_periods", "center", "win_type", "on", "axis", "closed"], a => new PdWindow(this, null, a.Int("window", 1), a.Given("min_periods") is { } mp ? Pd.ToInt(mp, "min_periods") : null, a.Bool("center", false), expanding: false)),
            "expanding" => M(["min_periods", "axis", "method"], a => new PdWindow(this, null, 0, a.Int("min_periods", 1), false, expanding: true)),
            "reset_index" => M(["level", "drop", "name", "inplace", "allow_duplicates"], ResetIndex),
            "rename" => M(["index", "axis", "copy", "inplace", "level", "errors"], Rename),
            "rename_axis" => M(["mapper", "index", "axis", "copy", "inplace"], a => new PdSeries(Data, Index.WithName(a.Given("mapper") ?? a.Given("index") ?? PyNone.Instance), Name)),
            "set_axis" => M(["labels", "axis", "copy"], a => new PdSeries(Data, PdBuild.IndexFrom(a.Required("labels")), Name)),
            "duplicated" => M(["keep"], a => With(PdColumn.Bools([.. PdOps.Duplicated([.. Data.Values.Select(static v => (IReadOnlyList<PyObject>)[v])], a["keep"] ?? new PyStr("first"))]))),
            "drop_duplicates" => M(["keep", "inplace", "ignore_index"], a =>
            {
                var duplicated = PdOps.Duplicated([.. Data.Values.Select(static v => (IReadOnlyList<PyObject>)[v])], a["keep"] ?? new PyStr("first"));
                return Inplace(a, Take([.. Enumerable.Range(0, Count).Where(i => !duplicated[i])]), a.Bool("ignore_index", false));
            }),
            "drop" => M(["labels", "axis", "index", "columns", "level", "inplace", "errors"], a =>
            {
                var key = a.Given("labels") ?? a.Given("index") ?? throw Pd.TypeError("Need to specify at least one of 'labels', 'index' or 'columns'");
                var labels = Pd.IsListLike(key) ? Pd.List(key) : [key];
                var drop = new HashSet<int>();
                foreach (var label in labels)
                {
                    if (Index.FindAll(label) is { } found)
                    {
                        drop.UnionWith(found);
                    }
                    else if (a.Str("errors", "raise") == "raise")
                    {
                        throw Pd.KeyError(new PyStr($"[{label.Repr()}] not found in axis"));
                    }
                }

                return Inplace(a, Take([.. Enumerable.Range(0, Count).Where(i => !drop.Contains(i))]));
            }),
            "agg" or "aggregate" => M(["func", "axis"], a => Aggregate(a.Required("func")), extra: true),
            "transform" => M(["func", "axis"], a => Transform(a.Required("func")), extra: true),
            "pipe" => M(["func"], a => runtime.Machine.Call(a.Required("func"), [this], Keywords(a)), extra: true),
            "corr" => M(["other", "method", "min_periods"], a => Pd.Float(PdFrame.Pearson(this, (PdSeries)a.Required("other")))),
            "cov" => M(["other", "min_periods", "ddof"], a => Pd.Float(PdFrame.Covariance(this, (PdSeries)a.Required("other"), a.Int("ddof", 1)))),
            "explode" => M(["ignore_index"], a => Explode(a.Bool("ignore_index", false))),
            "sample" => M(["n", "frac", "replace", "weights", "random_state", "axis", "ignore_index"], a =>
            {
                var positions = PdSample.Positions(Count, a);
                var sampled = Take(positions);
                return a.Bool("ignore_index", false) ? new PdSeries(sampled.Data, PdIndex.Default(sampled.Count), Name) : sampled;
            }),
            "reindex" => M(["index", "fill_value", "method"], a =>
            {
                var target = PdBuild.IndexFrom(a.Required("index"));
                var positions = Enumerable.Range(0, target.Count).Select(i => Index.Find(target[i])).ToList();
                var column = Data.Take(positions);
                if (a.Given("fill_value") is { } fill)
                {
                    column = column.With([.. positions.Select((p, i) => (p, i)).Where(static x => x.p < 0).Select(static x => x.i)], fill);
                }

                return new PdSeries(column, target, Name);
            }),
            "groupby" => M(["by", "axis", "level", "as_index", "sort", "group_keys", "observed", "dropna"], a => PdGroupBy.ForSeries(this, a)),
            "to_string" => M(["buf", "na_rep", "float_format", "header", "index", "length", "dtype", "name", "max_rows"], _ => new PyStr(PdFormat.Series(this, truncate: false))),
            "to_markdown" => M(["buf", "mode", "index"], a => new PyStr(PdFormat.Markdown(ToFrame(null), a.Bool("index", true)))),
            "to_csv" => M(PdIO.ToCsvParameters, a => PdIO.ToCsv(ToFrame(null), a, series: true)),
            "to_json" => M(PdIO.ToJsonParameters, a => PdIO.ToJson(this, a)),
            "memory_usage" => M(["index", "deep"], _ => PyInt.From((Count * 8L) + (Index.Count * 8L))),
            _ => null,
        };
    }

    /// <summary>Unrecognised keyword arguments, for forwarding to a callback.</summary>
    internal static PyDict? Keywords(PdArgs arguments)
    {
        if (arguments.Extra.Count == 0)
        {
            return null;
        }

        var dict = new PyDict();
        foreach (var (key, value) in arguments.Extra)
        {
            dict.Set(new PyStr(key), value);
        }

        return dict;
    }

    private bool Monotonic(int direction)
    {
        for (var i = 1; i < Count; i++)
        {
            if (Data.IsMissing(i) || Data.IsMissing(i - 1) || Pd.Order(Data.Values[i - 1], Data.Values[i]) * direction > 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Returns <paramref name="result"/>, or writes it into this Series when <c>inplace=True</c>.</summary>
    private PyObject Inplace(PdArgs arguments, PdSeries result, bool ignoreIndex = false)
    {
        if (ignoreIndex)
        {
            result = new PdSeries(result.Data, PdIndex.Default(result.Count), result.Name);
        }

        if (!arguments.Bool("inplace", false))
        {
            return result;
        }

        Data = result.Data;
        Index = result.Index;
        return PyNone.Instance;
    }

    public PdSeries Head(int n)
    {
        var count = n >= 0 ? Math.Min(n, Count) : Math.Max(0, Count + n);
        return new PdSeries(Data.Range(0, count), Index.Slice(0, count), Name);
    }

    public PdSeries Tail(int n)
    {
        var count = n >= 0 ? Math.Min(n, Count) : Math.Max(0, Count + n);
        return new PdSeries(Data.Range(Count - count, count), Index.Slice(Count - count, count), Name);
    }

    internal static PdColumn RoundColumn(PdColumn column, int decimals) => column.Type switch
    {
        DType.Float64 => PdColumn.Floats([.. column.Doubles().Select(v => Pd.Round(v, decimals))]),
        DType.Object => new PdColumn(DType.Object, [.. column.Values.Select(v => v is PyFloat f ? new PyFloat(Pd.Round(f.Value, decimals)) : v)]),
        _ => column.Clone(),
    };

    internal static PdColumn MissingMask(PdColumn column, bool missing) =>
        PdColumn.Bools([.. Enumerable.Range(0, column.Length).Select(i => column.IsMissing(i) == missing)]);

    private PdSeries AsType(PyObject dtype)
    {
        var (type, spelling) = PdDTypeObject.Parse(dtype);
        return With(Data.Cast(type, spelling));
    }

    /// <summary><c>fillna</c> with a value, a mapping by label, or a fill direction.</summary>
    internal PdSeries FillNa(PyObject? value, string? method, PyObject? limit)
    {
        var maxFill = limit is null ? int.MaxValue : Pd.ToInt(limit, "limit");

        if (method is not null)
        {
            return With(PdWindow.Fill(Data, method is "ffill" or "pad", maxFill));
        }

        if (value is null)
        {
            throw Pd.ValueError("Must specify a fill 'value' or 'method'.");
        }

        var missing = Enumerable.Range(0, Count).Where(Data.IsMissing).ToList();
        var column = Data.Clone();

        if (value is PyDict or PdSeries)
        {
            foreach (var position in missing)
            {
                var replacement = value is PyDict dict
                    ? dict.TryGetValue(Index[position], out var found) ? found : null
                    : ((PdSeries)value).Index.Find(Index[position]) is var at && at >= 0 ? ((PdSeries)value).Data.Values[at] : null;

                if (replacement is not null)
                {
                    column = column.With(position, replacement);
                }
            }

            return With(column);
        }

        return With(missing.Count == 0 ? column : column.With(missing, value));
    }

    private PdSeries SortValues(PdArgs arguments)
    {
        var keyed = Data;

        if (arguments.Given("key") is { } key)
        {
            keyed = ((PdSeries)PdRuntime.Current!.Call(key, this)).Data;
        }

        var ascending = arguments.Given("ascending") is { } given && Pd.IsListLike(given) ? Pd.List(given)[0].IsTruthy() : arguments.Bool("ascending", true);
        return Take(PdSort.Order([keyed], [ascending], arguments.Str("na_position", "last")));
    }

    private PyDict ToDict()
    {
        var dict = new PyDict();
        for (var i = 0; i < Count; i++)
        {
            dict.Set(Index[i], Data.Values[i]);
        }

        return dict;
    }

    public PdFrame ToFrame(PyObject? name)
    {
        var label = name ?? (Name is PyNone ? PyInt.From(0) : Name);
        return new PdFrame(PdIndex.Of([label]), [Data], Index);
    }

    private PdSeries Between(PyObject left, PyObject right, string inclusive)
    {
        var lower = PdOps.Compare(inclusive is "both" or "left" ? ">=" : ">", new PdOps.Operand(Data), new PdOps.Operand(left), Count);
        var upper = PdOps.Compare(inclusive is "both" or "right" ? "<=" : "<", new PdOps.Operand(Data), new PdOps.Operand(right), Count);
        return With(PdColumn.Bools([.. Enumerable.Range(0, Count).Select(i => lower.Values[i].IsTruthy() && upper.Values[i].IsTruthy())]));
    }

    private PdSeries Clip(PyObject? lower, PyObject? upper)
    {
        var raw = new PyObject[Count];

        for (var i = 0; i < Count; i++)
        {
            var value = Data.Values[i];

            if (!Data.IsMissing(i))
            {
                if (lower is not null && Pd.Order(value, Bound(lower, i)) < 0)
                {
                    value = Bound(lower, i);
                }

                if (upper is not null && Pd.Order(value, Bound(upper, i)) > 0)
                {
                    value = Bound(upper, i);
                }
            }

            raw[i] = value;
        }

        var result = PdColumn.Infer(raw);
        return With(Data.Type == DType.Float64 ? PdColumn.OfType(DType.Float64, result.Values) : result);

        PyObject Bound(PyObject bound, int i) => bound is PdSeries series ? series.Data.Values[i] : bound;
    }

    private PyObject QuantileOf(PyObject q)
    {
        if (Pd.IsListLike(q))
        {
            var levels = Pd.List(q);
            return new PdSeries(PdColumn.Floats([.. levels.Select(l => Quantile(Pd.ToDouble(l)))]), PdIndex.Of([.. levels.Select(l => (PyObject)Pd.Float(Pd.ToDouble(l)))]), Name);
        }

        return Pd.Float(Quantile(Pd.ToDouble(q)));
    }

    private PdSeries Mode(bool dropna)
    {
        var keywords = new PyDict();
        keywords.Set(new PyStr("dropna"), PyBool.Of(dropna));
        var counts = ValueCounts(new PdArgs("mode", [], keywords, ["normalize", "sort", "ascending", "dropna"]));

        if (counts.Count == 0)
        {
            return new PdSeries(new PdColumn(Data.Type, []), PdIndex.Default(0), Name);
        }

        var top = counts.Data.Values[0];
        var modes = Enumerable.Range(0, counts.Count).Where(i => counts.Data.Values[i].PyEquals(top)).Select(i => counts.Index[i]).ToList();
        var column = new PdColumn(Data.Type, [.. modes]);
        var order = PdSort.Order([column], [true], "last");
        return new PdSeries(column.Take(order), PdIndex.Default(modes.Count), Name);
    }

    private PdSeries Largest(int n, bool largest)
    {
        var valid = PdReduce.Valid(Data);
        var order = PdSort.Order([Data.Take(valid)], [!largest], "last");
        return Take([.. order.Take(n).Select(o => valid[o])]);
    }

    private PdSeries Where(PyObject condition, PyObject? other, bool keepWhereTrue)
    {
        var mask = condition switch
        {
            PdSeries series => series.Data.Values.Select(static v => v.IsTruthy()).ToList(),
            _ when Pd.IsListLike(condition) => Pd.List(condition).Select(static v => v.IsTruthy()).ToList(),
            _ => Pd.List(PdRuntime.Current!.Call(condition, this)).Select(static v => v.IsTruthy()).ToList(),
        };

        var raw = new PyObject[Count];

        for (var i = 0; i < Count; i++)
        {
            raw[i] = mask[i] == keepWhereTrue
                ? Data.Values[i]
                : other switch
                {
                    null => PdColumn.MissingFor(Data.Type),
                    PdSeries series => series.Index.Find(Index[i]) is var at && at >= 0 ? series.Data.Values[at] : Pd.NaN,
                    _ => other,
                };
        }

        return With(PdColumn.Infer(raw));
    }

    private PyObject ResetIndex(PdArgs arguments)
    {
        if (arguments.Bool("drop", false))
        {
            var reset = new PdSeries(Data, PdIndex.Default(Count), Name);

            if (arguments.Bool("inplace", false))
            {
                Index = reset.Index;
                return PyNone.Instance;
            }

            return reset;
        }

        var name = arguments.Given("name") ?? (Name is PyNone ? PyInt.From(0) : Name);
        var frame = new PdFrame(PdIndex.Of([name]), [Data], Index);
        return frame.ResetIndex(drop: false);
    }

    private PyObject Rename(PdArgs arguments)
    {
        var mapper = arguments["index"];

        if (mapper is null)
        {
            return Copy();
        }

        if (mapper is PyDict or PdSeries || (mapper is PyCallable && mapper is not PyType))
        {
            var labels = Index.Labels.Values.Select(label => mapper switch
            {
                PyDict dict => dict.TryGetValue(label, out var renamed) ? renamed : label,
                PdSeries series => series.Index.Find(label) is var at && at >= 0 ? series.Data.Values[at] : label,
                _ => PdRuntime.Current!.Call(mapper, label),
            }).ToList();

            var renamed = new PdSeries(Data, PdIndex.Of(labels, Index.Name), Name);
            return InplaceRename(arguments, renamed);
        }

        return InplaceRename(arguments, new PdSeries(Data, Index, mapper));
    }

    private PyObject InplaceRename(PdArgs arguments, PdSeries renamed)
    {
        if (!arguments.Bool("inplace", false))
        {
            return renamed;
        }

        Index = renamed.Index;
        Name = renamed.Name;
        return PyNone.Instance;
    }

    private PyObject Aggregate(PyObject function)
    {
        switch (function)
        {
            case PyStr text:
                return PdGroupBy.ReduceNamed(this, text.Value);
            case PyList or PyTuple:
            {
                var functions = Pd.List(function);
                var results = functions.Select(f => f is PyStr t ? PdGroupBy.ReduceNamed(this, t.Value) : PdRuntime.Current!.Call(f, this)).ToList();
                return new PdSeries(PdColumn.Infer(results), PdIndex.Of([.. functions.Select(PdGroupBy.FunctionName)]), Name);
            }

            case PyDict dict:
                return new PdSeries(
                    PdColumn.Infer([.. dict.Entries.Select(e => Aggregate(e.Value))]),
                    PdIndex.Of([.. dict.Entries.Select(static e => e.Key)]),
                    Name);
            default:
                return PdRuntime.Current!.Call(function, this);
        }
    }

    private PyObject Transform(PyObject function)
    {
        if (function is PyStr text)
        {
            return GetAttribute(text.Value) is { } method ? PdRuntime.Current!.Call(method) : throw Pd.ValueError($"{text.Value} is not a valid function for 'Series' object");
        }

        var result = PdRuntime.Current!.Call(function, this);
        return result is PdSeries ? result : Apply(function, [], null);
    }

    private PdSeries Explode(bool ignoreIndex)
    {
        var values = new List<PyObject>();
        var labels = new List<PyObject>();

        for (var i = 0; i < Count; i++)
        {
            var value = Data.Values[i];

            if (value is PyList or PyTuple or PySet && Pd.List(value) is { } items)
            {
                if (items.Count == 0)
                {
                    values.Add(Pd.NaN);
                    labels.Add(Index[i]);
                }

                foreach (var item in items)
                {
                    values.Add(item);
                    labels.Add(Index[i]);
                }
            }
            else
            {
                values.Add(value);
                labels.Add(Index[i]);
            }
        }

        var column = PdColumn.Infer(values);
        return new PdSeries(new PdColumn(DType.Object, column.Values), ignoreIndex ? PdIndex.Default(values.Count) : PdIndex.Of(labels, Index.Name), Name);
    }
}
