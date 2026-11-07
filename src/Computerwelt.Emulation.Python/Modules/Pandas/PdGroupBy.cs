using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules.Pandas;

/// <summary>
/// <c>df.groupby(...)</c> and <c>s.groupby(...)</c>: rows split by key, then reduced,
/// transformed, filtered or handed to a function group by group. Keys are sorted by default
/// and missing keys dropped, as pandas does. Several keys give a tuple-labelled index with one
/// name per level.
/// </summary>
internal sealed class PdGroupBy : PyObject
{
    private readonly PdFrame _frame;
    private readonly List<PdColumn> _keys;
    private readonly List<PyObject> _keyNames;
    private readonly bool _tupleKeys;
    private readonly bool _asIndex;
    private readonly bool _sort;
    private readonly bool _dropna;
    private readonly List<int> _values;
    private readonly bool _seriesSelection;
    private List<(PyObject Key, List<int> Rows)>? _groups;

    public PdGroupBy(PdFrame frame, PdArgs arguments)
    {
        _frame = frame;
        _asIndex = arguments.Bool("as_index", true);
        _sort = arguments.Bool("sort", true);
        _dropna = arguments.Bool("dropna", true);
        (_keys, _keyNames, _tupleKeys, var used) = ResolveKeys(frame, arguments.Given("by"), arguments.Given("level"));
        _values = [.. Enumerable.Range(0, frame.Columns.Count).Where(c => !used.Contains(c))];
    }

    private PdGroupBy(PdGroupBy source, List<int> values, bool seriesSelection)
    {
        _frame = source._frame;
        _keys = source._keys;
        _keyNames = source._keyNames;
        _tupleKeys = source._tupleKeys;
        _asIndex = source._asIndex;
        _sort = source._sort;
        _dropna = source._dropna;
        _groups = source._groups;
        _values = values;
        _seriesSelection = seriesSelection;
    }

    /// <summary><c>series.groupby(...)</c>: a one-column frame grouped, with the Series selected.</summary>
    public static PdGroupBy ForSeries(PdSeries series, PdArgs arguments)
    {
        var name = series.Name is PyNone ? new PyStr("__series__") : series.Name;
        var frame = new PdFrame(PdIndex.Of([name]), [series.Data], series.Index);
        var by = arguments.Given("by");

        // A key that names nothing in a one-column frame is not a column: a list or array
        // of keys, or a Series, lines up with the values instead.
        if (by is not null and not PdSeries && Pd.IsListLike(by) && !Pd.List(by).All(k => frame.Columns.Find(k) >= 0))
        {
            var keys = Pd.List(by);
            if (keys.Count == series.Count && !keys.All(static k => Pd.IsListLike(k)))
            {
                by = new PdSeries(PdColumn.Infer(keys), series.Index, null);
            }
        }

        var grouped = new PdGroupBy(frame, new PdArgs("groupby", [], KeywordsWith(arguments, by), ["by", "axis", "level", "as_index", "sort", "group_keys", "observed", "dropna"]));
        return new PdGroupBy(grouped, [0], seriesSelection: true);
    }

    private static PyDict KeywordsWith(PdArgs arguments, PyObject? by)
    {
        var keywords = new PyDict();
        foreach (var name in new[] { "level", "as_index", "sort", "dropna" })
        {
            if (arguments[name] is { } value)
            {
                keywords.Set(new PyStr(name), value);
            }
        }

        if (by is not null)
        {
            keywords.Set(new PyStr("by"), by);
        }

        return keywords;
    }

    public override string TypeName => _seriesSelection ? "SeriesGroupBy" : "DataFrameGroupBy";

    public override string Repr() => $"<pandas.core.groupby.generic.{TypeName} object>";

    private static (List<PdColumn>, List<PyObject>, bool, HashSet<int>) ResolveKeys(PdFrame frame, PyObject? by, PyObject? level)
    {
        var keys = new List<PdColumn>();
        var names = new List<PyObject>();
        var used = new HashSet<int>();

        if (by is null)
        {
            if (level is null)
            {
                throw Pd.TypeError("You have to supply one of 'by' and 'level'");
            }

            var levels = Pd.IsListLike(level) ? Pd.List(level) : [level];

            foreach (var item in levels)
            {
                var position = item is PyInt integer ? (int)integer.Value : frame.Index.LevelNames.ToList().FindIndex(n => Pd.Same(n, item));

                if (position < 0 || position >= frame.Index.LevelNames.Count)
                {
                    throw Pd.KeyError(item);
                }

                keys.Add(frame.Index.IsMulti
                    ? PdColumn.Infer([.. frame.Index.Labels.Values.Select(v => ((PyTuple)v).Items[position])])
                    : frame.Index.Labels);
                names.Add(frame.Index.LevelNames[position]);
            }

            return (keys, names, Pd.IsListLike(level), used);
        }

        var items = by is PyList or PyTuple ? Pd.List(by) : [by];

        foreach (var item in items)
        {
            switch (item)
            {
                case PdSeries series:
                    keys.Add(frame.ColumnFrom(series));
                    names.Add(series.Name);
                    break;
                case PdIndex index:
                    keys.Add(index.Labels);
                    names.Add(index.Name);
                    break;
                case PyDict mapping:
                    keys.Add(PdColumn.Infer([.. frame.Index.Labels.Values.Select(l => mapping.TryGetValue(l, out var v) ? v : Pd.NaN)]));
                    names.Add(PyNone.Instance);
                    break;
                case PyCallable callable when item is not PyType && frame.Columns.Find(item) < 0:
                    keys.Add(PdColumn.Infer([.. frame.Index.Labels.Values.Select(l => PdRuntime.Current!.Call(callable, l))]));
                    names.Add(PyNone.Instance);
                    break;
                default:
                    var at = frame.Columns.Find(item);

                    if (at >= 0)
                    {
                        keys.Add(frame.Data[at]);
                        names.Add(item);
                        used.Add(at);
                    }
                    else if (frame.Index.LevelNames.ToList().FindIndex(n => Pd.Same(n, item)) is var lv && lv >= 0)
                    {
                        keys.Add(frame.Index.IsMulti
                            ? PdColumn.Infer([.. frame.Index.Labels.Values.Select(v => ((PyTuple)v).Items[lv])])
                            : frame.Index.Labels);
                        names.Add(item);
                    }
                    else if (Pd.IsListLike(item) && Pd.List(item).Count == frame.RowCount)
                    {
                        keys.Add(PdColumn.Infer(Pd.List(item)));
                        names.Add(PyNone.Instance);
                    }
                    else
                    {
                        throw Pd.KeyError(item);
                    }

                    break;
            }
        }

        return (keys, names, by is PyList or PyTuple, used);
    }

    /// <summary>The groups, in key order (or first appearance with sort=False).</summary>
    private List<(PyObject Key, List<int> Rows)> Groups()
    {
        if (_groups is not null)
        {
            return _groups;
        }

        var groups = TypedGroups() ?? BoxedGroups();

        if (_sort && groups.Count > 0)
        {
            var firsts = groups.Select(static g => g.First).ToList();
            var order = PdSort.Order([.. _keys.Select(k => k.Take(firsts))], [.. _keys.Select(_ => true)], "last");
            groups = [.. order.Select(o => groups[o])];
        }

        return _groups = [.. groups.Select(static g => (g.Key, g.Rows))];
    }

    /// <summary>
    /// The groups of one int64 or float64 key, hashed on the unboxed values; null for any
    /// other key, or a float key holding NaN that is not being dropped.
    /// </summary>
    /// <remarks>
    /// The same groups as the boxed path: within one int64 or float64 column, Python
    /// equality is equality of the numbers, and <see cref="double"/> equality already puts
    /// 0.0 and -0.0 together. A NaN key row is skipped with dropna, as the boxed path does.
    /// </remarks>
    private List<(PyObject Key, List<int> Rows, int First)>? TypedGroups()
    {
        if (_keys.Count != 1 || _tupleKeys)
        {
            return null;
        }

        var column = _keys[0];

        if (column.Type == DType.Int64)
        {
            return Grouped(column.LongArray, static v => PyInt.From(v), static _ => false);
        }

        if (column.Type == DType.Float64)
        {
            var doubles = column.FloatArray;

            if (!_dropna && doubles.AsSpan().ContainsAnyExceptInRange(double.NegativeInfinity, double.PositiveInfinity))
            {
                return null;
            }

            return Grouped(doubles, static v => Pd.Float(v), double.IsNaN);
        }

        return null;
    }

    private static List<(PyObject Key, List<int> Rows, int First)> Grouped<T>(T[] keys, Func<T, PyObject> box, Func<T, bool> skip)
        where T : notnull
    {
        var lookup = new Dictionary<T, int>();
        var groups = new List<(PyObject Key, List<int> Rows, int First)>();

        for (var r = 0; r < keys.Length; r++)
        {
            var key = keys[r];

            if (skip(key))
            {
                continue;
            }

            if (!lookup.TryGetValue(key, out var at))
            {
                lookup[key] = at = groups.Count;
                groups.Add((box(key), [], r));
            }

            groups[at].Rows.Add(r);
        }

        return groups;
    }

    private List<(PyObject Key, List<int> Rows, int First)> BoxedGroups()
    {
        var lookup = new Dictionary<PyKey, int>();
        var groups = new List<(PyObject Key, List<int> Rows, int First)>();
        var rows = _frame.RowCount;

        // One buffer for the key parts of every row; a list is made only for a row that
        // starts a new group, since only that row's parts become a label.
        var width = _keys.Count;
        var parts = new PyObject[width];

        for (var r = 0; r < rows; r++)
        {
            var missing = false;

            for (var k = 0; k < width; k++)
            {
                parts[k] = _keys[k].Values[r];
                missing |= Pd.IsMissing(parts[k]);
            }

            if (_dropna && missing)
            {
                continue;
            }

            var key = width == 1
                ? new PyKey(PdOps.Normalize(parts[0]))
                : PdOps.RowKey(parts);

            if (!lookup.TryGetValue(key, out var at))
            {
                lookup[key] = at = groups.Count;
                var label = width == 1 && !_tupleKeys ? parts[0] : new PyTuple([.. parts]);
                groups.Add((label, [], r));
            }

            groups[at].Rows.Add(r);
        }

        return groups;
    }

    /// <summary>The result index: the group keys, one level per key.</summary>
    private PdIndex KeyIndex()
    {
        var groups = Groups();
        var firsts = groups.Select(static g => g.Rows[0]).ToList();

        if (_keys.Count == 1)
        {
            return new PdIndex(_keys[0].Take(firsts), _keyNames[0]);
        }

        return new PdIndex(new PdColumn(DType.Object, [.. groups.Select(static g => g.Key is PyTuple ? g.Key : new PyTuple([g.Key]))]), null, _keyNames);
    }

    /// <summary>Puts the keys back as columns when <c>as_index=False</c>.</summary>
    private PyObject Finish(PyObject result)
    {
        if (_asIndex)
        {
            return result;
        }

        var frame = result switch
        {
            PdFrame f => f,
            PdSeries s => s.ToFrame(null),
            _ => null,
        };

        if (frame is null)
        {
            return result;
        }

        var reset = frame.ResetIndex(drop: false);
        return reset;
    }

    public override IEnumerable<PyObject> Iterate()
    {
        foreach (var (key, rows) in Groups())
        {
            PyObject part = _seriesSelection ? SelectedSeries().Take(rows) : _frame.TakeRows(rows);
            yield return new PyTuple([key, part]);
        }
    }

    public override int? Length() => Groups().Count;

    private PdSeries SelectedSeries()
    {
        var series = _frame.Column(_values[0]);

        if (series.Name is PyStr { Value: "__series__" })
        {
            series.Name = PyNone.Instance;
        }

        return series;
    }

    public override PyObject GetItem(PyObject index) => Pd.Guard(() => Subscript(index));

    private PyObject Subscript(PyObject index)
    {
        if (Pd.IsListLike(index))
        {
            return new PdGroupBy(this, _frame.ColumnPositions(Pd.List(index)), seriesSelection: false);
        }

        return new PdGroupBy(this, [_frame.Columns.Locate(index)], seriesSelection: true);
    }

    public override PyObject? GetAttribute(string name) => Pd.Guard(() => AttributeCore(name));

    private PyObject? AttributeCore(string name)
    {
        if (PdRuntime.Current is not { } runtime)
        {
            return null;
        }

        PyBuiltinFunction M(string[] parameters, Func<PdArgs, PyObject> body, bool extra = false) => runtime.Method(name, parameters, body, extra);

        switch (name)
        {
            case "ngroups":
                return PyInt.From(Groups().Count);
            case "groups":
            {
                var dict = new PyDict();
                foreach (var (key, rows) in Groups())
                {
                    dict.Set(key, _frame.Index.Take(rows));
                }

                return dict;
            }

            case "indices":
            {
                var dict = new PyDict();
                foreach (var (key, rows) in Groups())
                {
                    dict.Set(key, new PyList([.. rows.Select(static r => (PyObject)PyInt.From(r))]));
                }

                return dict;
            }
        }

        if (PdReduce.Names.Contains(name) && name != "size")
        {
            return M(["numeric_only", "min_count", "ddof", "skipna"], a => Finish(Reduce(name, a.Bool("numeric_only", false), a.Int("ddof", 1), a.Int("min_count", 0))));
        }

        var result = name switch
        {
            "size" => M([], _ => Size()),
            "agg" or "aggregate" => M(["func"], a => Finish(Aggregate(a)), extra: true),
            "apply" => M(["func", "include_groups"], a => Apply(a.Required("func"), PdSeries.Keywords(a), a.Bool("include_groups", true)), extra: true),
            "transform" => M(["func"], a => Transform(a.Required("func"), PdSeries.Keywords(a)), extra: true),
            "filter" => M(["func", "dropna"], a => Filter(a.Required("func"))),
            "head" => M(["n"], a => Positional(a.Int("n", 5), head: true)),
            "tail" => M(["n"], a => Positional(a.Int("n", 5), head: false)),
            "nth" => M(["n"], a => Nth(Pd.ToInt(a.Required("n"), "n"))),
            "get_group" => M(["name", "obj"], a => GetGroup(a.Required("name"))),
            "cumsum" or "cumprod" or "cummax" or "cummin" => M(["axis", "skipna", "numeric_only"], a => PerGroup(c => PdWindow.Cumulative(c, name, a.Bool("skipna", true)), numericOnly: true)),
            "cumcount" => M(["ascending"], a => CumCount(a.Bool("ascending", true))),
            "shift" => M(["periods", "freq", "axis", "fill_value"], a => PerGroup(c => PdWindow.Shift(c, a.Int("periods", 1), a.Given("fill_value")), numericOnly: false)),
            "diff" => M(["periods", "axis"], a => PerGroup(c => PdWindow.Diff(c, a.Int("periods", 1)), numericOnly: true)),
            "pct_change" => M(["periods", "fill_method", "limit", "freq"], a => PerGroup(c => PdWindow.PctChange(c, a.Int("periods", 1)), numericOnly: true)),
            "rank" => M(["method", "ascending", "na_option", "pct", "axis"], a => PerGroup(c => PdWindow.Rank(c, a.Str("method", "average"), a.Bool("ascending", true), a.Bool("pct", false), a.Str("na_option", "keep")), numericOnly: false)),
            "ffill" => M(["limit"], a => PerGroup(c => PdWindow.Fill(c, true, a.Int("limit", int.MaxValue)), numericOnly: false)),
            "bfill" => M(["limit"], a => PerGroup(c => PdWindow.Fill(c, false, a.Int("limit", int.MaxValue)), numericOnly: false)),
            "fillna" => M(["value", "method", "axis", "inplace", "limit"], a => a.Str("method") is { } method
                ? PerGroup(c => PdWindow.Fill(c, method is "ffill" or "pad", int.MaxValue), numericOnly: false)
                : PerGroup(c => new PdSeries(c, PdIndex.Default(c.Length), null).FillNa(a.Given("value"), null, null).Data, numericOnly: false)),
            "value_counts" => M(["normalize", "sort", "ascending", "bins", "dropna"], ValueCounts),
            "describe" => M(["percentiles", "include", "exclude"], _ => Describe()),
            "unique" => M([], _ => Finish(new PdSeries(new PdColumn(DType.Object, [.. Groups().Select(g => (PyObject)new PyList(new PdSeries(SelectedSeries().Data.Take(g.Rows), PdIndex.Default(g.Rows.Count), null).UniqueValues()))]), KeyIndex(), SelectedSeries().Name))),
            "pipe" => M(["func"], a => runtime.Machine.Call(a.Required("func"), [this], PdSeries.Keywords(a)), extra: true),
            _ => null,
        };

        if (result is not null)
        {
            return result;
        }

        // `gb.column` selects a column, as `gb['column']` does.
        return !_seriesSelection && _frame.Columns.Find(new PyStr(name)) is var at && at >= 0
            ? new PdGroupBy(this, [at], seriesSelection: true)
            : null;
    }

    /// <summary>One reduction applied to every selected column within every group.</summary>
    private PyObject Reduce(string how, bool numericOnly, int ddof = 1, int minCount = 0)
    {
        var groups = Groups();
        var index = KeyIndex();
        var labels = new List<PyObject>();
        var data = new List<PdColumn>();

        foreach (var c in _values)
        {
            var column = _frame.Data[c];
            var numeric = column.IsNumeric || column.Type == DType.DateTime || column.Values.All(static v => Pd.IsNumber(v) || Pd.IsMissing(v));

            if (PdReduce.NeedsNumbers(how) && !numeric)
            {
                if (numericOnly && !_seriesSelection)
                {
                    continue;
                }

                throw Pd.TypeError($"agg function failed [how->{how},dtype->{Pd.Name(column.Type)}]");
            }

            if (numericOnly && !column.IsNumeric && !_seriesSelection)
            {
                continue;
            }

            var results = groups.Select(g => PdReduce.Reduce(column.Take(g.Rows), how, true, ddof, minCount)).ToList();
            var reduced = results.Count == 0 ? new PdColumn(column.Type, []) : PdColumn.Infer(results);

            if (how is "mean" or "median" or "std" or "var" or "sem" or "skew" && reduced.Type == DType.Int64)
            {
                reduced = PdColumn.OfType(DType.Float64, reduced.Values);
            }

            labels.Add(_frame.Columns[c]);
            data.Add(reduced);
        }

        if (_seriesSelection)
        {
            return new PdSeries(data[0], index, SelectedSeries().Name);
        }

        return new PdFrame(PdIndex.Of(labels, _frame.Columns.Name), data, index);
    }

    private PyObject Size()
    {
        var sizes = PdColumn.Ints([.. Groups().Select(static g => (long)g.Rows.Count)]);

        if (!_asIndex)
        {
            var frame = new PdSeries(sizes, KeyIndex(), null).ToFrame(new PyStr("size"));
            return frame.ResetIndex(drop: false);
        }

        return new PdSeries(sizes, KeyIndex(), _seriesSelection ? SelectedSeries().Name : null);
    }

    /// <summary>A named function as <c>agg</c> spells it, applied to a Series.</summary>
    public static PyObject ReduceNamed(PdSeries series, string name)
    {
        if (PdReduce.Names.Contains(name))
        {
            return PdReduce.Reduce(series.Data, name);
        }

        if (series.GetAttribute(name) is { } method && PdRuntime.Current is { } runtime)
        {
            return runtime.Call(method);
        }

        throw new PyRaise(new PyException(PyExceptionType.AttributeError, $"'SeriesGroupBy' object has no attribute '{name}'"));
    }

    /// <summary>The label a function gets as a result column: its name, <c>&lt;lambda&gt;</c> for a lambda.</summary>
    public static PyObject FunctionName(PyObject function) => function switch
    {
        PyStr text => text,
        PyCallable callable => new PyStr(ShortName(callable.Name)),
        _ => new PyStr(function.Display()),
    };

    private static string ShortName(string name) => name.Contains('.', StringComparison.Ordinal) ? name[(name.LastIndexOf('.') + 1)..] : name;

    /// <summary>A function as a reduction name when it is a known one (<c>np.sum</c>, <c>sum</c>, <c>len</c>).</summary>
    private static string? Known(PyObject function) => function switch
    {
        PyStr text => text.Value,
        PyCallable callable when ShortName(callable.Name) is var name && (PdReduce.Names.Contains(name) || name is "len" or "amax" or "amin" or "nansum" or "nanmean") =>
            name switch { "len" => "size", "amax" => "max", "amin" => "min", "nansum" => "sum", "nanmean" => "mean", _ => name },
        _ => null,
    };

    /// <summary>Applies one function to one column's rows in every group.</summary>
    private PdColumn AggregateColumn(int column, PyObject function)
    {
        var groups = Groups();
        var data = _frame.Data[column];

        if (Known(function) is { } name)
        {
            if (name == "size")
            {
                return PdColumn.Ints([.. groups.Select(static g => (long)g.Rows.Count)]);
            }

            var reduced = new PdGroupBy(this, [column], seriesSelection: true).Reduce(name, numericOnly: false);
            return ((PdSeries)reduced).Data;
        }

        var runtime = PdRuntime.Current!;
        var results = groups.Select(g => runtime.Call(function, new PdSeries(data.Take(g.Rows), _frame.Index.Take(g.Rows), _frame.Columns[column]))).ToList();
        return PdColumn.Infer(results);
    }

    private PyObject Aggregate(PdArgs arguments)
    {
        var function = arguments["func"];
        var index = KeyIndex();

        // Named aggregation: total=('col', 'sum') on a frame, total='sum' on a Series.
        if (function is null or PyNone && arguments.Extra.Count > 0)
        {
            var labels = new List<PyObject>();
            var data = new List<PdColumn>();

            foreach (var (name, spec) in arguments.Extra)
            {
                labels.Add(new PyStr(name));

                if (_seriesSelection)
                {
                    data.Add(AggregateColumn(_values[0], spec));
                    continue;
                }

                var parts = spec is PyTuple tuple ? tuple.Items : Pd.List(spec);

                if (parts.Count != 2)
                {
                    throw Pd.TypeError("Must provide 'func' or tuples of '(column, aggfunc).");
                }

                data.Add(AggregateColumn(_frame.Columns.Locate(parts[0]), parts[1]));
            }

            return new PdFrame(PdIndex.Of(labels), data, index);
        }

        switch (function)
        {
            case null or PyNone:
                throw Pd.TypeError("Must provide 'func' or tuples of '(column, aggfunc).");

            case PyDict dict:
            {
                var anyList = dict.Entries.Any(static e => e.Value is PyList or PyTuple);
                var labels = new List<PyObject>();
                var data = new List<PdColumn>();

                foreach (var (column, spec) in dict.Entries)
                {
                    var at = _frame.Columns.Locate(column);
                    var functions = spec is PyList or PyTuple ? Pd.List(spec) : [spec];

                    foreach (var f in functions)
                    {
                        labels.Add(anyList ? new PyTuple([column, FunctionName(f)]) : column);
                        data.Add(AggregateColumn(at, f));
                    }
                }

                var columns = anyList ? new PdIndex(new PdColumn(DType.Object, [.. labels]), null, [PyNone.Instance, PyNone.Instance]) : PdIndex.Of(labels);
                return new PdFrame(columns, data, index);
            }

            case PyList or PyTuple:
            {
                var functions = Pd.List(function);
                var labels = new List<PyObject>();
                var data = new List<PdColumn>();

                foreach (var c in _values)
                {
                    foreach (var f in functions)
                    {
                        labels.Add(_seriesSelection ? FunctionName(f) : new PyTuple([_frame.Columns[c], FunctionName(f)]));
                        data.Add(AggregateColumn(c, f));
                    }
                }

                var columns = _seriesSelection
                    ? PdIndex.Of(labels)
                    : new PdIndex(new PdColumn(DType.Object, [.. labels]), null, [PyNone.Instance, PyNone.Instance]);
                return new PdFrame(columns, data, index);
            }

            default:
            {
                if (Known(function) is { } name && PdReduce.Names.Contains(name) && name != "size")
                {
                    return Reduce(name, numericOnly: false);
                }

                var data = _values.Select(c => AggregateColumn(c, function)).ToList();
                return _seriesSelection
                    ? new PdSeries(data[0], index, SelectedSeries().Name)
                    : new PdFrame(_frame.Columns.Take(_values), data, index);
            }
        }
    }

    private PyObject Apply(PyObject function, PyDict? keywords, bool includeGroups)
    {
        var runtime = PdRuntime.Current!;
        var groups = Groups();
        var results = new List<PyObject>();

        foreach (var (_, rows) in groups)
        {
            PyObject part = _seriesSelection
                ? SelectedSeries().Take(rows)
                : includeGroups ? _frame.TakeRows(rows) : _frame.TakeColumns(_values).TakeRows(rows);
            results.Add(runtime.Machine.Call(function, [part], keywords));
        }

        var index = KeyIndex();

        if (results.All(static r => r is not (PdSeries or PdFrame)))
        {
            return Finish(new PdSeries(PdColumn.Infer(results), index, null));
        }

        // Series that all share one index become the columns of a frame keyed by group.
        if (results.All(static r => r is PdSeries) && !_seriesSelection)
        {
            var series = results.Cast<PdSeries>().ToList();

            if (series.All(s => s.Index.SameAs(series[0].Index)))
            {
                var frame = PdReshape.ConcatColumns(series, sortLabels: false).Transpose();
                frame.Index = index;
                return Finish(frame);
            }
        }

        // Otherwise each result is stacked under its group's key.
        var parts = new List<PdFrame>();
        var labels = new List<PyObject>();

        for (var g = 0; g < results.Count; g++)
        {
            var frame = results[g] switch
            {
                PdFrame f => f,
                PdSeries s => s.ToFrame(null),
                var other => new PdSeries(PdColumn.Infer([other]), PdIndex.Default(1), null).ToFrame(null),
            };

            parts.Add(frame);
            labels.AddRange(frame.Index.Labels.Values.Select(l => (PyObject)new PyTuple([.. KeyParts(groups[g].Key), .. LabelParts(l)])));
        }

        var stacked = PdReshape.ConcatRows(parts, ignoreIndex: true);
        var names = _keyNames.Concat(parts.Count > 0 ? parts[0].Index.LevelNames : [PyNone.Instance]).ToList();
        stacked.Index = new PdIndex(new PdColumn(DType.Object, [.. labels]), null, names);

        if (results.All(static r => r is PdSeries) && _seriesSelection)
        {
            return new PdSeries(stacked.Data[0], stacked.Index, SelectedSeries().Name);
        }

        return stacked;

        IEnumerable<PyObject> KeyParts(PyObject key) => key is PyTuple tuple && (_keys.Count > 1 || _tupleKeys) ? tuple.Items : [key];

        static IEnumerable<PyObject> LabelParts(PyObject label) => label is PyTuple tuple ? tuple.Items : [label];
    }

    /// <summary>A result aligned with the original rows, computed group by group.</summary>
    private PyObject PerGroup(Func<PdColumn, PdColumn> compute, bool numericOnly)
    {
        var columns = _values.Where(c => !numericOnly || _frame.Data[c].IsNumeric || _seriesSelection).ToList();
        var data = new List<PdColumn>();

        foreach (var c in columns)
        {
            var raw = new PyObject[_frame.RowCount];
            Array.Fill(raw, Pd.NaN);
            var types = new List<DType>();

            foreach (var (_, rows) in Groups())
            {
                var result = compute(_frame.Data[c].Take(rows));
                types.Add(result.Type);

                for (var i = 0; i < rows.Count; i++)
                {
                    raw[rows[i]] = result.Values[i];
                }
            }

            var column = PdColumn.Infer(raw);
            data.Add(types.Count > 0 && types.All(t => t == DType.Float64) && column.Type == DType.Int64 ? PdColumn.OfType(DType.Float64, column.Values) : column);
        }

        if (_seriesSelection)
        {
            return new PdSeries(data[0], _frame.Index, SelectedSeries().Name);
        }

        return new PdFrame(_frame.Columns.Take(columns), data, _frame.Index);
    }

    private PyObject Transform(PyObject function, PyDict? keywords)
    {
        var runtime = PdRuntime.Current!;

        if (Known(function) is { } name)
        {
            var reduced = new PdGroupBy(this, _values, _seriesSelection) { }.Reduce(name == "size" ? "count" : name, numericOnly: false);
            var groups = Groups();
            var reducedColumns = reduced is PdFrame frame ? frame.Data : [((PdSeries)reduced).Data];
            var data = new List<PdColumn>();

            foreach (var reducedColumn in reducedColumns)
            {
                var positions = new int[_frame.RowCount];
                Array.Fill(positions, -1);

                for (var g = 0; g < groups.Count; g++)
                {
                    foreach (var row in groups[g].Rows)
                    {
                        positions[row] = g;
                    }
                }

                data.Add(reducedColumn.Take(positions));
            }

            return _seriesSelection
                ? new PdSeries(data[0], _frame.Index, SelectedSeries().Name)
                : new PdFrame(reduced is PdFrame f2 ? f2.Columns : PdIndex.Of([_frame.Columns[_values[0]]]), data, _frame.Index);
        }

        return PerGroup(column =>
        {
            var result = runtime.Machine.Call(function, [new PdSeries(column, PdIndex.Default(column.Length), null)], keywords);
            return result switch
            {
                PdSeries series when series.Count == column.Length => series.Data,
                PdSeries => throw Pd.ValueError("transform must return a scalar value for each group or a Series of the same length"),
                _ => PdColumn.Repeat(result, column.Length),
            };
        }, numericOnly: false);
    }

    private PyObject Filter(PyObject function)
    {
        var runtime = PdRuntime.Current!;
        var keep = new List<int>();

        foreach (var (_, rows) in Groups())
        {
            PyObject part = _seriesSelection ? SelectedSeries().Take(rows) : _frame.TakeRows(rows);

            if (runtime.Call(function, part).IsTruthy())
            {
                keep.AddRange(rows);
            }
        }

        keep.Sort();
        return _seriesSelection ? SelectedSeries().Take(keep) : _frame.TakeRows(keep);
    }

    private PyObject Positional(int n, bool head)
    {
        var keep = new List<int>();

        foreach (var (_, rows) in Groups())
        {
            keep.AddRange(head ? rows.Take(n) : rows.Skip(Math.Max(0, rows.Count - n)));
        }

        keep.Sort();
        return _seriesSelection ? SelectedSeries().Take(keep) : _frame.TakeRows(keep);
    }

    private PyObject Nth(int n)
    {
        var keep = new List<int>();

        foreach (var (_, rows) in Groups())
        {
            var at = n < 0 ? rows.Count + n : n;
            if (at >= 0 && at < rows.Count)
            {
                keep.Add(rows[at]);
            }
        }

        keep.Sort();
        return _seriesSelection ? SelectedSeries().Take(keep) : _frame.TakeRows(keep);
    }

    private PyObject GetGroup(PyObject name)
    {
        foreach (var (key, rows) in Groups())
        {
            if (Pd.Same(key, name) || (key is PyTuple { Items.Count: 1 } single && Pd.Same(single.Items[0], name)))
            {
                return _seriesSelection ? SelectedSeries().Take(rows) : _frame.TakeRows(rows);
            }
        }

        throw Pd.KeyError(name);
    }

    private PyObject CumCount(bool ascending)
    {
        var values = new long[_frame.RowCount];

        foreach (var (_, rows) in Groups())
        {
            for (var i = 0; i < rows.Count; i++)
            {
                values[rows[i]] = ascending ? i : rows.Count - 1 - i;
            }
        }

        return new PdSeries(PdColumn.Ints(values), _frame.Index, null);
    }

    private PyObject ValueCounts(PdArgs arguments)
    {
        var series = SelectedSeries();
        var labels = new List<PyObject>();
        var counts = new List<PyObject>();
        var normalize = arguments.Bool("normalize", false);

        foreach (var (key, rows) in Groups())
        {
            var part = new PdSeries(series.Data.Take(rows), PdIndex.Default(rows.Count), series.Name);
            var counted = part.ValueCounts(arguments);

            for (var i = 0; i < counted.Count; i++)
            {
                labels.Add(new PyTuple([.. key is PyTuple t && _keys.Count > 1 ? t.Items : [key], counted.Index[i]]));
                counts.Add(counted.Data.Values[i]);
            }
        }

        var index = new PdIndex(new PdColumn(DType.Object, [.. labels]), null, [.. _keyNames, series.Name]);
        return Finish(new PdSeries(PdColumn.Infer(counts), index, new PyStr(normalize ? "proportion" : "count")));
    }

    private PyObject Describe()
    {
        var index = KeyIndex();
        var stats = Groups().Select(g => SelectedSeries().Take(g.Rows).Describe()).ToList();

        if (stats.Count == 0)
        {
            return new PdFrame(PdIndex.Of([]), [], index);
        }

        var labels = stats[0].Index;
        var data = Enumerable.Range(0, labels.Count).Select(k => PdColumn.Infer([.. stats.Select(s => s.Data.Values[k])])).ToList();
        return new PdFrame(labels, data, index);
    }
}
