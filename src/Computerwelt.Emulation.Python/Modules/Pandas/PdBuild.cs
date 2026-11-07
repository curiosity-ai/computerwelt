using System.Numerics;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules.Pandas;

/// <summary>The constructors: <c>Series(...)</c>, <c>DataFrame(...)</c>, <c>Index(...)</c>.</summary>
internal static class PdBuild
{
    /// <summary>An index from a value: an Index, a Series' values, or any list-like.</summary>
    public static PdIndex IndexFrom(PyObject value) => value switch
    {
        PdIndex index => index,
        PdSeries series => new PdIndex(series.Data.Clone(), series.Name),
        _ when Pd.IsListLike(value) => PdIndex.Of(Pd.List(value)),
        _ => throw Pd.TypeError($"Index(...) must be called with a collection of some kind, {value.Repr()} was passed"),
    };

    /// <summary><c>pd.Series(data, index, dtype, name)</c>.</summary>
    public static PdSeries Series(PdArgs arguments)
    {
        var data = arguments.Given("data");
        var index = arguments.Given("index") is { } given ? IndexFrom(given) : null;
        var name = arguments.Given("name");
        PdSeries result;

        switch (data)
        {
            case null:
                result = new PdSeries(index is null ? new PdColumn(DType.Object, []) : PdColumn.Repeat(Pd.NaN, index.Count), index ?? PdIndex.Default(0), null);
                break;

            case PdSeries series:
                result = index is null
                    ? new PdSeries(series.Data.Clone(), series.Index, series.Name)
                    : new PdSeries(series.Data.Take([.. Enumerable.Range(0, index.Count).Select(i => series.Index.Find(index[i]))]), index, series.Name);
                break;

            case PyDict dict:
            {
                var keys = dict.Entries.Select(static e => e.Key).ToList();
                var values = dict.Entries.Select(static e => e.Value).ToList();
                var source = new PdSeries(PdColumn.Infer(values), PdIndex.Of(keys), null);
                result = index is null
                    ? source
                    : new PdSeries(source.Data.Take([.. Enumerable.Range(0, index.Count).Select(i => source.Index.Find(index[i]))]), index, null);
                break;
            }

            case PdIndex labels:
                result = new PdSeries(labels.Labels.Clone(), index ?? PdIndex.Default(labels.Count), labels.Name);
                break;

            default:
                if (Pd.IsListLike(data) || (data is not PyStr && data.Iterate() is not null && data is not PdFrame))
                {
                    var values = Pd.List(data);

                    if (index is not null && index.Count != values.Count)
                    {
                        throw Pd.ValueError($"Length of values ({values.Count}) does not match length of index ({index.Count})");
                    }

                    result = new PdSeries(PdColumn.Infer(values), index ?? PdIndex.Default(values.Count), null);
                }
                else
                {
                    result = new PdSeries(PdColumn.Repeat(data, index?.Count ?? 1), index ?? PdIndex.Default(1), null);
                }

                break;
        }

        if (arguments.Given("dtype") is { } dtype)
        {
            var (type, spelling) = PdDTypeObject.Parse(dtype);
            result = result.With(result.Data.Cast(type, spelling));
        }

        if (name is not null)
        {
            result.Name = name;
        }

        return result;
    }

    /// <summary><c>pd.DataFrame(data, index, columns, dtype)</c>.</summary>
    public static PdFrame Frame(PdArgs arguments)
    {
        var data = arguments.Given("data");
        var index = arguments.Given("index") is { } i ? IndexFrom(i) : null;
        var columns = arguments.Given("columns") is { } c ? IndexFrom(c) : null;
        var frame = Build(data, index, columns);

        if (arguments.Given("dtype") is { } dtype)
        {
            var (type, spelling) = PdDTypeObject.Parse(dtype);
            frame = frame.MapColumns(column => column.Cast(type, spelling));
        }

        return frame;
    }

    private static PdFrame Build(PyObject? data, PdIndex? index, PdIndex? columns)
    {
        switch (data)
        {
            case null:
            {
                var rows = index ?? PdIndex.Default(0);
                var labels = columns ?? new PdIndex(new PdColumn(DType.Object, []));
                return new PdFrame(labels, [.. labels.Labels.Values.Select(_ => new PdColumn(DType.Object, Enumerable.Repeat<PyObject>(Pd.NaN, rows.Count).ToArray()))], rows);
            }

            case PdFrame frame:
            {
                var result = frame.Copy();

                if (columns is not null)
                {
                    result = SelectColumns(result, columns);
                }

                if (index is not null)
                {
                    result = new PdFrame(result.Columns, [.. result.Data.Select(d => d.Take([.. Enumerable.Range(0, index.Count).Select(r => frame.Index.Find(index[r]))]))], index);
                }

                return result;
            }

            case PdSeries series:
            {
                var label = series.Name is PyNone ? PyInt.From(0) : series.Name;
                var result = new PdFrame(PdIndex.Of([label]), [series.Data.Clone()], series.Index);
                return columns is null ? result : SelectColumns(result, columns);
            }

            case PyDict dict:
                return FromDict(dict, index, columns);
        }

        if (!Pd.IsListLike(data) && data.Iterate() is null)
        {
            throw Pd.ValueError("DataFrame constructor not properly called!");
        }

        var items = Pd.List(data);

        if (items.Count > 0 && items.All(static item => item is PyDict))
        {
            return FromRecords(items.Cast<PyDict>().ToList(), index, columns);
        }

        if (items.Count > 0 && items.All(static item => item is PdSeries))
        {
            var series = items.Cast<PdSeries>().ToList();
            var keys = series.Select(static s =>
            {
                var dict = new PyDict();
                for (var r = 0; r < s.Count; r++)
                {
                    dict.Set(s.Index[r], s.Data.Values[r]);
                }

                return dict;
            }).ToList();
            return FromRecords(keys, index ?? PdIndex.Of([.. series.Select(static (s, i) => s.Name is PyNone ? PyInt.From(i) : s.Name)]), columns);
        }

        if (items.Count > 0 && items.All(static item => Pd.IsListLike(item) || item is PyTuple))
        {
            var rows = items.Select(Pd.List).ToList();
            var width = rows.Max(static r => r.Count);
            Pd.CheckSize((long)width * rows.Count);
            var labels = columns ?? PdIndex.Default(width);

            if (labels.Count != width)
            {
                throw Pd.ValueError($"{labels.Count} columns passed, passed data had {width} columns");
            }

            var data2 = Enumerable.Range(0, width)
                .Select(col => PdColumn.Infer([.. rows.Select(row => col < row.Count ? row[col] : PyNone.Instance)]))
                .ToList();
            return new PdFrame(labels, data2, CheckIndex(index, rows.Count));
        }

        // A flat list is one column.
        var column = PdColumn.Infer(items);
        return new PdFrame(columns ?? PdIndex.Default(1), [column], CheckIndex(index, items.Count));
    }

    private static PdIndex CheckIndex(PdIndex? index, int rows)
    {
        if (index is null)
        {
            return PdIndex.Default(rows);
        }

        return index.Count == rows
            ? index
            : throw Pd.ValueError($"Length of values ({rows}) does not match length of index ({index.Count})");
    }

    private static PdFrame SelectColumns(PdFrame frame, PdIndex columns) =>
        new(columns, [.. columns.Labels.Values.Select(label =>
            frame.Columns.Find(label) is var at && at >= 0 ? frame.Data[at] : PdColumn.Repeat(Pd.NaN, frame.RowCount))], frame.Index);

    private static PdFrame FromDict(PyDict dict, PdIndex? index, PdIndex? columns)
    {
        var labels = dict.Entries.Select(static e => e.Key).ToList();
        var values = dict.Entries.Select(static e => e.Value).ToList();

        // The row index: given, or the union of the Series' and dicts' indexes, or the length of the lists.
        if (index is null)
        {
            var keyed = values.Where(static v => v is PdSeries or PyDict).ToList();

            if (keyed.Count > 0)
            {
                static PdIndex Own(PyObject value) => value is PdSeries s ? s.Index : PdIndex.Of([.. ((PyDict)value).Entries.Select(static e => e.Key)]);
                var union = Own(keyed[0]);

                foreach (var value in keyed.Skip(1))
                {
                    union = PdOps.UnionIndex(union, Own(value), sort: values.All(static v => v is PdSeries));
                }

                index = union;
            }
            else
            {
                var lengths = values.Where(Pd.IsListLike).Select(static v => Pd.List(v).Count).Distinct().ToList();

                if (lengths.Count > 1)
                {
                    throw Pd.ValueError("All arrays must be of the same length");
                }

                if (lengths.Count == 0 && values.Count > 0)
                {
                    throw Pd.ValueError("If using all scalar values, you must pass an index");
                }

                index = PdIndex.Default(lengths.Count == 0 ? 0 : lengths[0]);
            }
        }

        var rows = index;
        var data = new List<PdColumn>();

        foreach (var value in values)
        {
            switch (value)
            {
                case PdSeries series:
                    data.Add(series.Index.SameAs(rows) ? series.Data.Clone() : series.Data.Take([.. Enumerable.Range(0, rows.Count).Select(r => series.Index.Find(rows[r]))]));
                    break;
                case PyDict inner:
                    data.Add(PdColumn.Infer([.. Enumerable.Range(0, rows.Count).Select(r => inner.TryGetValue(rows[r], out var v) ? v : Pd.NaN)]));
                    break;
                default:
                    if (Pd.IsListLike(value))
                    {
                        var list = Pd.List(value);

                        if (list.Count != rows.Count)
                        {
                            throw Pd.ValueError($"Length of values ({list.Count}) does not match length of rows ({rows.Count})");
                        }

                        data.Add(PdColumn.Infer(list));
                    }
                    else
                    {
                        data.Add(PdColumn.Repeat(value, rows.Count));
                    }

                    break;
            }
        }

        var frame = new PdFrame(PdIndex.Of(labels), data, rows);
        return columns is null ? frame : SelectColumns(frame, columns);
    }

    private static PdFrame FromRecords(List<PyDict> records, PdIndex? index, PdIndex? columns)
    {
        var labels = new List<PyObject>();
        var seen = new HashSet<PyKey>();

        foreach (var record in records)
        {
            foreach (var (key, _) in record.Entries)
            {
                if (seen.Add(new PyKey(key)))
                {
                    labels.Add(key);
                }
            }
        }

        var target = columns ?? PdIndex.Of(labels);
        Pd.CheckSize((long)target.Count * records.Count);
        var data = target.Labels.Values
            .Select(label => PdColumn.Infer([.. records.Select(r => r.TryGetValue(label, out var v) ? v : Pd.NaN)]))
            .ToList();
        return new PdFrame(target, data, CheckIndex(index, records.Count));
    }

    /// <summary>
    /// <c>replace</c> over one column: a scalar, a list (to one value or pairwise), a mapping,
    /// or with <c>regex=True</c> a pattern substituted in every string.
    /// </summary>
    public static PdColumn ReplaceValues(PdColumn column, PyObject? toReplace, PyObject? value, bool regex)
    {
        if (toReplace is null or PyNone && value is PyDict dict)
        {
            toReplace = dict;
            value = null;
        }

        var pairs = new List<(PyObject From, PyObject To)>();

        switch (toReplace)
        {
            case PyDict mapping:
                pairs.AddRange(mapping.Entries.Select(static e => (e.Key, e.Value)));
                break;
            case PyList or PyTuple:
            {
                var from = Pd.List(toReplace);

                if (value is PyList or PyTuple)
                {
                    var to = Pd.List(value);

                    if (to.Count != from.Count)
                    {
                        throw Pd.ValueError($"Replacement lists must match in length. Expecting {from.Count} got {to.Count} ");
                    }

                    pairs.AddRange(from.Zip(to));
                }
                else
                {
                    pairs.AddRange(from.Select(f => (f, value ?? PyNone.Instance)));
                }

                break;
            }

            case null:
                return column.Clone();
            default:
                pairs.Add((toReplace, value ?? PyNone.Instance));
                break;
        }

        var raw = new PyObject[column.Length];
        var changed = false;
        var runtime = PdRuntime.Current;

        for (var i = 0; i < raw.Length; i++)
        {
            var current = column.Values[i];

            foreach (var (from, to) in pairs)
            {
                if (regex && from is PyStr pattern && current is PyStr text && runtime is not null)
                {
                    var sub = runtime.Machine.Import("re").GetAttribute("sub")!;
                    var replaced = runtime.Call(sub, pattern, to is PyStr ? to : new PyStr(to.Display()), text);

                    if (!replaced.PyEquals(text))
                    {
                        current = replaced;
                        changed = true;
                    }

                    continue;
                }

                if (Pd.Same(current, from) || (current is PdTimestamp && PdTimestamp.From(from) is { } moment && moment.PyEquals(current)))
                {
                    current = to;
                    changed = true;
                    break;
                }
            }

            raw[i] = current;
        }

        if (!changed)
        {
            return column.Clone();
        }

        var result = PdColumn.Infer(raw);
        return column.Type == DType.Float64 && result.Type == DType.Int64 ? PdColumn.OfType(DType.Float64, result.Values) : result;
    }
}

/// <summary>A dtype as a Python value: <c>s.dtype</c>, which compares equal to its name.</summary>
internal sealed class PdDTypeObject : PyObject, IPyOperand
{
    public PdDTypeObject(DType type) => Type = type;

    public DType Type { get; }

    public override string TypeName => "dtype";

    public override string Repr() => Type == DType.DateTime ? "dtype('<M8[ns]')" : Type == DType.TimeDelta ? "dtype('<m8[ns]')" : $"dtype('{(Type == DType.Object ? "O" : Pd.Name(Type))}')";

    public override string Display() => Pd.Name(Type);

    public override BigInteger PyHash() => new(StringComparer.Ordinal.GetHashCode(Pd.Name(Type)));

    public override bool PyEquals(PyObject other) => other switch
    {
        PdDTypeObject dtype => dtype.Type == Type,
        _ => TryParse(other) is { } parsed && parsed.Type == Type,
    };

    public override PyObject? GetAttribute(string name) => name switch
    {
        "name" => new PyStr(Pd.Name(Type)),
        "kind" => new PyStr(Type switch
        {
            DType.Int64 => "i",
            DType.Float64 => "f",
            DType.Bool => "b",
            DType.DateTime => "M",
            DType.TimeDelta => "m",
            _ => "O",
        }),
        "char" => new PyStr(Type switch { DType.Int64 => "l", DType.Float64 => "d", DType.Bool => "?", _ => "O" }),
        "itemsize" => PyInt.From(Type == DType.Bool ? 1 : 8),
        _ => null,
    };

    public PyObject? Binary(string op, PyObject other, bool reflected) => null;

    public PyObject? Unary(string op) => null;

    public PyObject? Compare(string op, PyObject other) => op switch
    {
        "==" => PyBool.Of(PyEquals(other)),
        "!=" => PyBool.Of(!PyEquals(other)),
        _ => null,
    };

    /// <summary>Reads a dtype argument: a name, a Python type, or a dtype.</summary>
    public static (DType Type, string Spelling) Parse(PyObject value) =>
        TryParse(value) ?? throw Pd.TypeError($"data type {value.Repr()} not understood");

    private static (DType Type, string Spelling)? TryParse(PyObject value)
    {
        var text = value switch
        {
            PdDTypeObject dtype => Pd.Name(dtype.Type),
            PyType type => type.Name,
            PyStr name => name.Value,
            PyCallable callable => callable.Name,
            _ => value.Display(),
        };

        return text switch
        {
            "int" or "int64" or "int32" or "int16" or "int8" or "Int64" or "Int32" or "uint8" or "uint16" or "uint32" or "uint64" or "i" or "i8" or "long" => (DType.Int64, "int64"),
            "float" or "float64" or "float32" or "Float64" or "f" or "f8" or "double" => (DType.Float64, "float64"),
            "bool" or "boolean" or "bool_" or "?" => (DType.Bool, "bool"),
            "str" or "string" or "U" => (DType.Object, "str"),
            "object" or "O" or "category" => (DType.Object, "object"),
            "datetime64[ns]" or "datetime64" or "<M8[ns]" or "M8[ns]" or "datetime" => (DType.DateTime, "datetime64[ns]"),
            "timedelta64[ns]" or "timedelta64" or "<m8[ns]" => (DType.TimeDelta, "timedelta64[ns]"),
            _ => null,
        };
    }
}

/// <summary>The named arithmetic methods: <c>add</c>, <c>radd</c>, <c>eq</c> and the rest.</summary>
internal static class PdArith
{
    public static readonly Dictionary<string, (string Op, bool Reflected)> Methods = new(StringComparer.Ordinal)
    {
        ["add"] = ("+", false),
        ["radd"] = ("+", true),
        ["sub"] = ("-", false),
        ["subtract"] = ("-", false),
        ["rsub"] = ("-", true),
        ["mul"] = ("*", false),
        ["multiply"] = ("*", false),
        ["rmul"] = ("*", true),
        ["div"] = ("/", false),
        ["truediv"] = ("/", false),
        ["divide"] = ("/", false),
        ["rdiv"] = ("/", true),
        ["rtruediv"] = ("/", true),
        ["floordiv"] = ("//", false),
        ["rfloordiv"] = ("//", true),
        ["mod"] = ("%", false),
        ["rmod"] = ("%", true),
        ["pow"] = ("**", false),
        ["rpow"] = ("**", true),
    };

    public static readonly Dictionary<string, string> Comparisons = new(StringComparer.Ordinal)
    {
        ["eq"] = "==",
        ["ne"] = "!=",
        ["lt"] = "<",
        ["le"] = "<=",
        ["gt"] = ">",
        ["ge"] = ">=",
    };

    /// <summary><c>s.add(other, fill_value=...)</c>: a missing side takes the fill when the other is present.</summary>
    public static PyObject Series(PdSeries series, string op, bool reflected, PyObject other, PyObject? fill)
    {
        if (fill is null || other is not PdSeries right)
        {
            if (fill is not null)
            {
                series = series.FillNa(fill, null, null);
            }

            return series.Binary(op, other, reflected)!;
        }

        var (index, l, r) = PdOps.Align(series.Index, right.Index);
        var left = series.Data.Take(l).Values.ToArray();
        var rightValues = right.Data.Take(r).Values.ToArray();

        for (var i = 0; i < index.Count; i++)
        {
            var leftMissing = Pd.IsMissing(left[i]);
            var rightMissing = Pd.IsMissing(rightValues[i]);

            if (leftMissing && !rightMissing)
            {
                left[i] = fill;
            }
            else if (rightMissing && !leftMissing)
            {
                rightValues[i] = fill;
            }
        }

        var a = new PdSeries(PdColumn.Infer(left), index, series.Name);
        var b = new PdSeries(PdColumn.Infer(rightValues), index, right.Name);
        return a.Binary(op, b, reflected)!;
    }

    /// <summary>The DataFrame form, with <c>axis</c> choosing how a Series lines up.</summary>
    public static PyObject Frame(PdFrame frame, string op, bool reflected, PyObject other, PyObject? fill, PyObject? axis)
    {
        if (fill is not null)
        {
            frame = frame.MapColumns(c => new PdSeries(c, frame.Index, null).FillNa(fill, null, null).Data);

            if (other is PdFrame otherFrame)
            {
                other = otherFrame.MapColumns(c => new PdSeries(c, otherFrame.Index, null).FillNa(fill, null, null).Data);
            }
        }

        // axis=0 (or 'index') lines a Series up with the rows instead of the columns.
        if (other is PdSeries series && axis is PyStr { Value: "index" or "rows" } or PyInt { Value.IsZero: true })
        {
            return frame.MapColumns(c => ((PdSeries)new PdSeries(c, frame.Index, null).Binary(op, series, reflected)!).Data);
        }

        return frame.Binary(op, other, reflected)!;
    }
}
