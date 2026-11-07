using System.Numerics;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules.Pandas;

/// <summary><c>pd.Series</c>: one column of values with an index.</summary>
internal sealed partial class PdSeries : PyObject, IPyOperatorOverloads
{
    public PdSeries(PdColumn data, PdIndex index, PyObject? name)
    {
        if (data.Length != index.Count)
        {
            throw Pd.ValueError($"Length of values ({data.Length}) does not match length of index ({index.Count})");
        }

        Data = data;
        Index = index;
        Name = name ?? PyNone.Instance;
    }

    public PdColumn Data { get; private set; }

    public PdIndex Index { get; set; }

    public PyObject Name { get; set; }

    public int Count => Data.Length;

    public override string TypeName => "Series";

    /// <summary>A Series of the same index and name over new values.</summary>
    public PdSeries With(PdColumn data) => new(data, Index, Name);

    /// <summary>Replaces the values, as an in-place operation does.</summary>
    public void Replace(PdColumn data) => Data = data;

    /// <summary>The values, copied first when another owner shares them.</summary>
    public PdColumn Own()
    {
        if (Data.Shared)
        {
            Data = Data.Clone();
        }

        return Data;
    }

    public PdSeries Take(IReadOnlyList<int> positions) => new(Data.Take(positions), Index.Take(positions), Name);

    public PdSeries Copy() => new(Data.Clone(), Index, Name);

    public override int? Length() => Count;

    public override IEnumerable<PyObject> Iterate() => Data.Values;

    public override bool IsTruthy() =>
        throw Pd.ValueError("The truth value of a Series is ambiguous. Use a.empty, a.bool(), a.item(), a.any() or a.all().");

    public override bool Contains(PyObject item) => Index.Find(item) >= 0;

    public override BigInteger PyHash() => throw Pd.TypeError("unhashable type: 'Series'");

    public override string Repr() => Pd.Guard(() => PdFormat.Series(this));

    // ---- subscripting -------------------------------------------------------------------

    public override PyObject GetItem(PyObject index) => PdRuntime.Current is { } runtime
        ? runtime.Run(() => Select(index))
        : Select(index);

    private PyObject Select(PyObject key)
    {
        if (key is PySlice slice)
        {
            var labelSlice = !(slice.Start is null or PyNone or PyInt && slice.Stop is null or PyNone or PyInt) || Index.Labels.Type == DType.Float64;
            return Take(PdSelect.Positions(slice, Count, Index, positional: !labelSlice));
        }

        if ((!Pd.IsListLike(key) && key is not PdSeries) || (key is PyTuple && (Index.IsMulti || Index.Find(key) >= 0)))
        {
            if (Index.FindAll(key) is { } found)
            {
                return found.Count == 1 ? Data.Values[found[0]] : Take(found);
            }

            if (PdSelect.Prefix(key, Index) is { Count: > 0 } prefixed)
            {
                var part = Take(prefixed);
                part.Index = PdSelect.DropLevels(part.Index, key is PyTuple t ? t.Items.Count : 1);
                return part;
            }

            // The positional fallback pandas keeps (deprecated) for a non-integer index.
            if (key is PyInt and not PyBool && !Index.IsIntegerLike)
            {
                return Data.Values[PdSelect.Position(key, Count)];
            }

            throw Pd.KeyError(key);
        }

        var items = key is PdSeries or PdIndex ? null : Pd.List(key);

        if (items is not null && items.Count > 0 && items.All(static i => i is PyInt and not PyBool) && !Index.IsIntegerLike)
        {
            return Take([.. items.Select(i => PdSelect.Position(i, Count))]);
        }

        return Take(PdSelect.Positions(key, Count, Index, positional: false));
    }

    public override void SetItem(PyObject index, PyObject value)
    {
        if (PdRuntime.Current is { } runtime)
        {
            runtime.Run(() =>
            {
                Assign(index, value, positional: null);
                return PyNone.Instance;
            });
        }
        else
        {
            Assign(index, value, positional: null);
        }
    }

    /// <summary>
    /// Writes values at a key. <paramref name="positional"/> is true for <c>iloc</c>, false for
    /// <c>loc</c>, and null for plain <c>s[key] = v</c>, which takes labels and falls back to
    /// positions on a non-integer index.
    /// </summary>
    public void Assign(PyObject key, PyObject value, bool? positional)
    {
        List<int> positions;

        if (positional is null && key is PySlice slice && slice.Start is null or PyNone or PyInt && slice.Stop is null or PyNone or PyInt)
        {
            positions = PdSelect.Positions(key, Count, Index, positional: true);
        }
        else if (positional is true)
        {
            positions = PdSelect.Positions(key, Count, Index, positional: true);
        }
        else if ((!Pd.IsListLike(key) && key is not (PdSeries or PySlice)) || (key is PyTuple && (Index.IsMulti || Index.Find(key) >= 0)))
        {
            if (Index.FindAll(key) is { } found)
            {
                positions = found;
            }
            else if (positional is null && key is PyInt and not PyBool && !Index.IsIntegerLike)
            {
                positions = [PdSelect.Position(key, Count)];
            }
            else
            {
                // Setting a label that is not there appends it, as pandas enlarges.
                Index = PdIndex.Of([.. Index.Labels.Values, key], Index.Name);
                Data = PdColumn.Concat([Own(), PdColumn.Infer([PdColumn.Unwrap(value)])]);
                return;
            }
        }
        else
        {
            positions = PdSelect.Positions(key, Count, Index, positional: false);
        }

        WriteAt(positions, value);
    }

    /// <summary>Writes a scalar, a list or an aligned Series into the given rows.</summary>
    public void WriteAt(List<int> positions, PyObject value)
    {
        var column = Own();

        if (value is PdSeries other)
        {
            var values = positions.Select(p =>
            {
                var at = other.Index.Find(Index[p]);
                return at >= 0 ? other.Data.Values[at] : PdColumn.MissingFor(other.Data.Type);
            }).ToList();

            Data = column.WithMany(positions, values);
            return;
        }

        if (Pd.IsListLike(value))
        {
            var values = Pd.List(value);

            if (values.Count != positions.Count)
            {
                throw Pd.ValueError($"cannot set using a list-like indexer with a different length than the value");
            }

            Data = column.WithMany(positions, values);
            return;
        }

        Data = column.With(positions, value);
    }

    public override void DeleteItem(PyObject index)
    {
        var drop = PdSelect.LabelPositions(index, Index).ToHashSet();
        var keep = Enumerable.Range(0, Count).Where(i => !drop.Contains(i)).ToList();
        Data = Data.Take(keep);
        Index = Index.Take(keep);
    }

    // ---- operators ----------------------------------------------------------------------

    public PyObject? BinaryOperator(string op, PyObject other, bool reflected) => Pd.Guard(() => BinaryCore(op, other, reflected));

    private PyObject? BinaryCore(string op, PyObject other, bool reflected)
    {
        if (other is PdFrame)
        {
            return null;
        }

        if (other is PdSeries series)
        {
            var (index, left, right) = PdOps.Align(Index, series.Index);
            var mine = new PdOps.Operand(Data.Take(left));
            var theirs = new PdOps.Operand(series.Data.Take(right));
            var name = Pd.Same(Name, series.Name) ? Name : PyNone.Instance;
            var result = reflected
                ? PdOps.Arithmetic(op, theirs, mine, index.Count)
                : PdOps.Arithmetic(op, mine, theirs, index.Count);
            return new PdSeries(result, index, name);
        }

        if (Pd.IsListLike(other))
        {
            var values = Pd.List(other);

            if (values.Count != Count)
            {
                throw Pd.ValueError($"Lengths must match to compare");
            }

            var column = new PdOps.Operand(PdColumn.Infer(values));
            return With(reflected
                ? PdOps.Arithmetic(op, column, new PdOps.Operand(Data), Count)
                : PdOps.Arithmetic(op, new PdOps.Operand(Data), column, Count));
        }

        var scalar = new PdOps.Operand(other);
        return With(reflected
            ? PdOps.Arithmetic(op, scalar, new PdOps.Operand(Data), Count)
            : PdOps.Arithmetic(op, new PdOps.Operand(Data), scalar, Count));
    }

    public PyObject? UnaryOperator(string op) => Pd.Guard(() => UnaryCore(op));

    private PyObject? UnaryCore(string op)
    {
        switch (op)
        {
            case "-":
                return With(Map(v => Operators.Unary("-", v), keepMissing: true));
            case "+":
                return Copy();
            case "abs":
                return With(Map(static v => v switch
                {
                    PyBool flag => flag,
                    PyInt integer => PyInt.From(BigInteger.Abs(integer.Value)),
                    PyFloat number => new PyFloat(Math.Abs(number.Value)),
                    _ => Operators.Unary("abs", v),
                }, keepMissing: true));
            case "~":
                return Data.Type == DType.Bool
                    ? With(PdColumn.Bools([.. Data.Values.Select(static v => !v.IsTruthy())]))
                    : With(Map(v => v is PyBool flag ? PyBool.Of(!flag.Value) : Operators.Unary("~", v), keepMissing: true));
            default:
                return null;
        }
    }

    public PyObject? CompareOperator(string op, PyObject other) => Pd.Guard(() => CompareCore(op, other));

    private PyObject? CompareCore(string op, PyObject other)
    {
        if (other is PdFrame)
        {
            return null;
        }

        if (other is PdSeries series)
        {
            if (!Index.SameAs(series.Index))
            {
                throw Pd.ValueError("Can only compare identically-labeled Series objects");
            }

            return With(PdOps.Compare(op, new PdOps.Operand(Data), new PdOps.Operand(series.Data), Count));
        }

        if (Pd.IsListLike(other) && other is not PyTuple)
        {
            var values = Pd.List(other);

            if (values.Count != Count)
            {
                throw Pd.ValueError("('Lengths must match to compare', (" + Count + ",), (" + values.Count + ",))");
            }

            return With(PdOps.Compare(op, new PdOps.Operand(Data), new PdOps.Operand(PdColumn.Infer(values)), Count));
        }

        return With(PdOps.Compare(op, new PdOps.Operand(Data), new PdOps.Operand(other), Count));
    }

    /// <summary>Applies a function to every value; missing values stay missing when asked.</summary>
    public PdColumn Map(Func<PyObject, PyObject> function, bool keepMissing)
    {
        var raw = new PyObject[Count];

        for (var i = 0; i < Count; i++)
        {
            var value = Data.Values[i];
            raw[i] = keepMissing && Data.IsMissing(i) ? value : function(value);
        }

        return PdColumn.Infer(raw);
    }

    // ---- shared computations ------------------------------------------------------------

    /// <summary>The distinct values in order of first appearance, NaN counted once.</summary>
    public List<PyObject> UniqueValues()
    {
        var seen = new HashSet<PyKey>();
        var result = new List<PyObject>();

        foreach (var value in Data.Values)
        {
            if (seen.Add(new PyKey(PdOps.Normalize(value))))
            {
                result.Add(value);
            }
        }

        return result;
    }

    public int CountUnique(bool dropna) =>
        UniqueValues().Count(v => !dropna || !Pd.IsMissing(v));

    /// <summary>Elementwise membership in <paramref name="values"/>.</summary>
    public PdColumn IsIn(PyObject values)
    {
        if (!Pd.IsListLike(values) && values is not PyDict)
        {
            throw Pd.TypeError($"only list-like objects are allowed to be passed to isin(), you passed a `{values.TypeName}`");
        }

        var set = new HashSet<PyKey>();

        foreach (var item in values is PyDict dict ? dict.Entries.Select(static e => e.Key) : Pd.List(values))
        {
            set.Add(new PyKey(PdOps.Normalize(PdTimestamp.From(item) ?? item)));
        }

        return PdColumn.Bools([.. Data.Values.Select(v => set.Contains(new PyKey(PdOps.Normalize(v))))]);
    }

    /// <summary>A named reduction, honouring skipna.</summary>
    public PyObject Reduce(string how, bool skipna, bool numericOnly, int ddof = 1, int minCount = 0)
    {
        if (numericOnly && !Data.IsNumeric)
        {
            throw Pd.TypeError($"Series.{how} does not allow numeric_only=True with non-numeric dtypes.");
        }

        return PdReduce.Reduce(Data, how, skipna, ddof, minCount);
    }

    /// <summary>The position of the largest (or smallest) value.</summary>
    public int ArgExtreme(bool max)
    {
        var best = -1;

        for (var i = 0; i < Count; i++)
        {
            if (Data.IsMissing(i))
            {
                continue;
            }

            if (best < 0 || (max ? Pd.Order(Data.Values[i], Data.Values[best]) > 0 : Pd.Order(Data.Values[i], Data.Values[best]) < 0))
            {
                best = i;
            }
        }

        return best >= 0 ? best : throw Pd.ValueError($"attempt to get {(max ? "argmax" : "argmin")} of an empty sequence");
    }

    /// <summary><c>value_counts</c>: counts per value, most frequent first, ties in order of appearance.</summary>
    public PdSeries ValueCounts(PdArgs args)
    {
        var dropna = args.Bool("dropna", true);
        var normalize = args.Bool("normalize", false);
        var sort = args.Bool("sort", true);
        var ascending = args.Bool("ascending", false);
        var counts = new Dictionary<PyKey, int>();
        var order = new List<PyObject>();

        foreach (var value in Data.Values)
        {
            if (dropna && Pd.IsMissing(value))
            {
                continue;
            }

            var key = new PyKey(PdOps.Normalize(value));

            if (!counts.TryGetValue(key, out var count))
            {
                order.Add(value);
            }

            counts[key] = count + 1;
        }

        var labels = order;
        var values = labels.Select(l => counts[new PyKey(PdOps.Normalize(l))]).ToList();

        if (sort)
        {
            var positions = Enumerable.Range(0, labels.Count).ToArray();
            Array.Sort(positions, (a, b) =>
            {
                var compare = ascending ? values[a].CompareTo(values[b]) : values[b].CompareTo(values[a]);
                return compare != 0 ? compare : a.CompareTo(b);
            });
            labels = [.. positions.Select(p => labels[p])];
            values = [.. positions.Select(p => values[p])];
        }

        var total = values.Sum();
        var data = normalize
            ? PdColumn.Floats([.. values.Select(v => (double)v / total)])
            : PdColumn.Ints([.. values.Select(static v => (long)v)]);
        var index = new PdIndex(new PdColumn(Data.Type, [.. labels]), Name);
        return new PdSeries(data, index, new PyStr(normalize ? "proportion" : "count"));
    }

    /// <summary><c>quantile</c> of the non-missing values, linear interpolation.</summary>
    public double Quantile(double q)
    {
        if (q is < 0 or > 1)
        {
            throw Pd.ValueError("percentiles should all be in the interval [0, 1]");
        }

        var values = PdReduce.Valid(Data).Select(i => Pd.ToDouble(Data.Values[i])).Order().ToList();
        return PdReduce.Quantile(values, q);
    }

    /// <summary>The <c>describe</c> statistics, numeric or categorical.</summary>
    public PdSeries Describe(IReadOnlyList<double>? percentiles = null)
    {
        var quantiles = percentiles ?? [0.25, 0.5, 0.75];

        if (Data.IsNumeric && Data.Type != DType.Bool)
        {
            var labels = new List<PyObject> { new PyStr("count"), new PyStr("mean"), new PyStr("std"), new PyStr("min") };
            labels.AddRange(quantiles.Select(static q => (PyObject)new PyStr(PercentLabel(q))));
            labels.Add(new PyStr("max"));

            var values = new List<double>
            {
                PdReduce.Valid(Data).Count,
                Pd.ToDouble(PdReduce.Reduce(Data, "mean")),
                Pd.ToDouble(PdReduce.Reduce(Data, "std")),
                Pd.ToDouble(PdReduce.Reduce(Data, "min")),
            };

            values.AddRange(quantiles.Select(Quantile));
            values.Add(Pd.ToDouble(PdReduce.Reduce(Data, "max")));
            return new PdSeries(PdColumn.Floats([.. values]), PdIndex.Of(labels), Name);
        }

        if (Data.Type == DType.DateTime)
        {
            var valid = PdReduce.Valid(Data);
            var labels = new List<PyObject> { new PyStr("count"), new PyStr("mean"), new PyStr("min") };
            labels.AddRange(quantiles.Select(static q => (PyObject)new PyStr(PercentLabel(q))));
            labels.Add(new PyStr("max"));
            var sorted = valid.Select(i => ((PdTimestamp)Data.Values[i]).Micro).Order().ToList();
            var values = new List<PyObject>
            {
                PyInt.From(valid.Count),
                PdReduce.Reduce(Data, "mean"),
                PdReduce.Reduce(Data, "min"),
            };
            values.AddRange(quantiles.Select(q => sorted.Count == 0
                ? PdNaT.Instance
                : (PyObject)new PdTimestamp((long)PdReduce.Quantile([.. sorted.Select(static s => (double)s)], q))));
            values.Add(PdReduce.Reduce(Data, "max"));
            return new PdSeries(new PdColumn(DType.Object, [.. values]), PdIndex.Of(labels), Name);
        }

        var counts = ValueCounts(new PdArgs("describe", [], null, ["normalize", "sort", "ascending", "dropna"]));
        var described = new List<PyObject>
        {
            PyInt.From(PdReduce.Valid(Data).Count),
            PyInt.From(CountUnique(true)),
            counts.Count > 0 ? counts.Index[0] : Pd.NaN,
            counts.Count > 0 ? counts.Data.Values[0] : Pd.NaN,
        };

        return new PdSeries(
            new PdColumn(DType.Object, [.. described]),
            PdIndex.Of([new PyStr("count"), new PyStr("unique"), new PyStr("top"), new PyStr("freq")]),
            Name);
    }

    /// <summary>A percentile's label as <c>describe</c> writes it: <c>25%</c>, <c>2.5%</c>.</summary>
    public static string PercentLabel(double q)
    {
        var percent = Math.Round(q * 100, 10);
        return Pd.FloatText(percent).EndsWith(".0", StringComparison.Ordinal)
            ? Pd.FloatText(percent)[..^2] + "%"
            : Pd.FloatText(percent) + "%";
    }

    /// <summary>Calls a Python function on each value.</summary>
    public PdSeries Apply(PyObject function, IReadOnlyList<PyObject> extra, PyDict? keywords)
    {
        var runtime = PdRuntime.Current!;
        var raw = new PyObject[Count];

        for (var i = 0; i < Count; i++)
        {
            raw[i] = runtime.Machine.Call(function, [Data.Values[i], .. extra], keywords);
        }

        return With(PdColumn.Infer(raw));
    }

    /// <summary><c>map</c> with a dict, a Series or a function.</summary>
    public PdSeries Map(PyObject mapper, bool ignoreNa)
    {
        var runtime = PdRuntime.Current!;
        var raw = new PyObject[Count];

        for (var i = 0; i < Count; i++)
        {
            var value = Data.Values[i];

            if (ignoreNa && Data.IsMissing(i))
            {
                raw[i] = value;
                continue;
            }

            raw[i] = mapper switch
            {
                PyDict dict => dict.TryGetValue(value, out var mapped) ? mapped : Pd.NaN,
                PdSeries series => series.Index.Find(value) is var at && at >= 0 ? series.Data.Values[at] : Pd.NaN,
                _ => runtime.Machine.Call(mapper, [value]),
            };
        }

        return With(PdColumn.Infer(raw));
    }
}
