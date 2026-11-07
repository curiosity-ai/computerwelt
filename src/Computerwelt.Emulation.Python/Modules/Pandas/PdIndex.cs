using System.Globalization;
using System.Numerics;
using System.Text;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules.Pandas;

/// <summary>
/// <c>pd.Index</c>: an immutable sequence of row or column labels, with a lookup from label
/// to position. A <c>RangeIndex</c> is the same type flagged as a range. A "multi-index" is an
/// index of tuples carrying one name per level — enough for <c>groupby</c> on several keys,
/// <c>set_index</c> with a list and <c>reset_index</c> to round-trip, though it is not a full
/// MultiIndex (no level-wise selection).
/// </summary>
internal sealed class PdIndex : PyObject, IPyOperand
{
    private Dictionary<PyKey, List<int>>? _lookup;

    public PdIndex(PdColumn labels, PyObject? name = null, IReadOnlyList<PyObject>? names = null)
    {
        Labels = labels;
        Name = name ?? PyNone.Instance;
        Names = names;
    }

    /// <summary>The labels, with their dtype.</summary>
    public PdColumn Labels { get; }

    /// <summary>The index's name, or None.</summary>
    public PyObject Name { get; set; }

    /// <summary>One name per level, for an index of tuples; null for an ordinary index.</summary>
    public IReadOnlyList<PyObject>? Names { get; set; }

    /// <summary>True for a <c>RangeIndex</c>.</summary>
    public bool IsRange { get; private init; }

    public long RangeStart { get; private init; }

    public long RangeStep { get; private init; } = 1;

    /// <summary>The frequency a <c>date_range</c> was built with, shown by its repr.</summary>
    public string? Freq { get; init; }

    public int Count => Labels.Length;

    public bool IsMulti => Names is { Count: > 1 };

    public PyObject this[int position] => Labels.Values[position];

    public override string TypeName => IsRange ? "RangeIndex" : IsMulti ? "MultiIndex" : Labels.Type == DType.DateTime ? "DatetimeIndex" : "Index";

    /// <summary>The default index, 0 to n-1.</summary>
    public static PdIndex Default(int count) => Range(0, count, 1);

    public static PdIndex Range(long start, long stop, long step, PyObject? name = null)
    {
        if (step == 0)
        {
            throw Pd.ValueError("Step must not be zero");
        }

        var count = step > 0 ? Math.Max(0, (stop - start + step - 1) / step) : Math.Max(0, (start - stop - step - 1) / -step);
        Pd.CheckSize(count);
        var values = new long[count];

        for (var i = 0; i < count; i++)
        {
            values[i] = start + (i * step);
        }

        return new PdIndex(PdColumn.Ints(values), name) { IsRange = true, RangeStart = start, RangeStep = step };
    }

    /// <summary>An index over raw labels, inferring their dtype.</summary>
    public static PdIndex Of(IReadOnlyList<PyObject> labels, PyObject? name = null)
    {
        var column = PdColumn.Infer(labels);

        // A tuple label set is a multi-index: one name per level, unnamed by default.
        if (labels.Count > 0 && labels.All(static l => l is PyTuple) && labels.Select(static l => ((PyTuple)l).Items.Count).Distinct().Count() == 1)
        {
            var levels = ((PyTuple)labels[0]).Items.Count;

            if (levels > 1)
            {
                return new PdIndex(column, name, [.. Enumerable.Repeat<PyObject>(PyNone.Instance, levels)]);
            }
        }

        return new PdIndex(column, name);
    }

    /// <summary>The position of the first occurrence of a label, or -1.</summary>
    public int Find(PyObject label)
    {
        var all = FindAll(label);
        return all is { Count: > 0 } ? all[0] : -1;
    }

    /// <summary>Every position holding a label, or null when there is none.</summary>
    public List<int>? FindAll(PyObject label)
    {
        if (_lookup is null)
        {
            var lookup = new Dictionary<PyKey, List<int>>();

            for (var i = 0; i < Count; i++)
            {
                var key = Key(this[i]);

                if (!lookup.TryGetValue(key, out var positions))
                {
                    lookup[key] = positions = [];
                }

                positions.Add(i);
            }

            _lookup = lookup;
        }

        if (Labels.Type == DType.DateTime && label is PyStr text && PdTimestamp.Parse(text, null) is PdTimestamp moment)
        {
            label = moment;
        }

        PyKey probe;

        try
        {
            probe = Key(label);
        }
        catch (PyRaise raise) when (raise.Exception.IsInstanceOf(PyExceptionType.TypeError))
        {
            return null;
        }

        return _lookup.TryGetValue(probe, out var found) ? found : null;
    }

    private static PyKey Key(PyObject label) => new(label is PyDateTime moment ? PdTimestamp.From(moment)! : label);

    /// <summary>The position of a label, raising <c>KeyError</c> when it is absent.</summary>
    public int Locate(PyObject label)
    {
        var position = Find(label);
        return position >= 0 ? position : throw Pd.KeyError(label);
    }

    public bool Contains(PyObject label, out int position)
    {
        position = Find(label);
        return position >= 0;
    }

    public override bool Contains(PyObject item) => Find(item) >= 0;

    public bool IsUnique
    {
        get
        {
            FindAll(PyNone.Instance);
            return _lookup!.Count == Count;
        }
    }

    /// <summary>True when integer keys mean labels, not positions.</summary>
    public bool IsIntegerLike => Labels.Type is DType.Int64 or DType.Float64;

    public PdIndex Take(IReadOnlyList<int> positions) =>
        new(Labels.Take(positions), Name, Names);

    /// <summary>A contiguous slice, which keeps a range a range.</summary>
    public PdIndex Slice(int start, int count, int step = 1)
    {
        if (step != 1)
        {
            var positions = new int[count];
            for (var i = 0; i < count; i++)
            {
                positions[i] = start + (i * step);
            }

            if (IsRange)
            {
                var first = RangeStart + (start * RangeStep);
                return Range(first, first + (count * step * RangeStep), step * RangeStep, Name);
            }

            return Take(positions);
        }

        if (IsRange)
        {
            var first = RangeStart + (start * RangeStep);
            return Range(first, first + (count * RangeStep), RangeStep, Name);
        }

        return new PdIndex(Labels.Range(start, count), Name, Names) { Freq = Freq };
    }

    public PdIndex WithName(PyObject name) => new(Labels, name, Names) { IsRange = IsRange, RangeStart = RangeStart, RangeStep = RangeStep, Freq = Freq };

    /// <summary>True when both hold equal labels in the same order.</summary>
    public bool SameAs(PdIndex other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (Count != other.Count)
        {
            return false;
        }

        for (var i = 0; i < Count; i++)
        {
            if (!Pd.Same(this[i], other[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The level names, or a single-element list of the name.</summary>
    public IReadOnlyList<PyObject> LevelNames => Names ?? [Name];

    public override int? Length() => Count;

    public override IEnumerable<PyObject> Iterate() => Labels.Values;

    public override bool IsTruthy() =>
        throw Pd.ValueError("The truth value of a Index is ambiguous. Use a.empty, a.bool(), a.item(), a.any() or a.all().");

    public PyList ToList() => new([.. Labels.Values]);

    public override PyObject GetItem(PyObject index) => Pd.Guard(() => Subscript(index));

    private PyObject Subscript(PyObject index)
    {
        switch (index)
        {
            case PyInt integer and not PyBool:
            {
                var position = (int)integer.Value;
                position = position < 0 ? position + Count : position;
                return position >= 0 && position < Count
                    ? this[position]
                    : throw Pd.IndexError($"index {integer.Value} is out of bounds for axis 0 with size {Count}");
            }

            case PySlice slice:
            {
                var (start, _, step, count) = slice.Resolve(Count);
                return Slice(start, count, step);
            }

            default:
                return Take(PdSelect.Positions(index, Count, this, positional: true));
        }
    }

    public override bool SetAttribute(string name, PyObject value)
    {
        switch (name)
        {
            case "name":
                Name = value;
                return true;
            case "names":
                var names = Pd.List(value);
                if (IsMulti && names.Count == Names!.Count)
                {
                    Names = names;
                }
                else if (names.Count == 1)
                {
                    Name = names[0];
                }
                else
                {
                    throw Pd.ValueError($"Length of names must match number of levels in MultiIndex.");
                }

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
            case "names": return new PyList([.. LevelNames]);
            case "dtype": return new PdDTypeObject(IsMulti ? DType.Object : Labels.Type);
            case "size": return PyInt.From(Count);
            case "shape": return new PyTuple([PyInt.From(Count)]);
            case "ndim": return PyInt.From(1);
            case "nlevels": return PyInt.From(LevelNames.Count);
            case "empty": return PyBool.Of(Count == 0);
            case "is_unique": return PyBool.Of(IsUnique);
            case "hasnans": return PyBool.Of(Enumerable.Range(0, Count).Any(Labels.IsMissing));
            case "is_monotonic_increasing": return PyBool.Of(Monotonic(1));
            case "is_monotonic_decreasing": return PyBool.Of(Monotonic(-1));
            case "start" when IsRange: return PyInt.From(RangeStart);
            case "stop" when IsRange: return PyInt.From(RangeStart + (Count * RangeStep));
            case "step" when IsRange: return PyInt.From(RangeStep);
            case "freq": return Freq is null ? PyNone.Instance : new PyStr(Freq);
            case "str": return new PdStringAccessor(ToSeries(), asIndex: true);
            case "dt": return new PdDateAccessor(ToSeries(), asIndex: true);
        }

        if (runtime is null)
        {
            return null;
        }

        PyBuiltinFunction Method(string[] parameters, Func<PdArgs, PyObject> body) => runtime.Method(name, parameters, body);

        return name switch
        {
            "tolist" or "to_list" => Method([], _ => ToList()),
            "values" => runtime.Array(ToList()),
            "to_numpy" or "__array__" => Method(["dtype", "copy"], _ => runtime.Array(ToList())),
            "array" => runtime.Array(ToList()),
            "copy" => Method(["name", "deep"], a => WithName(a["name"] ?? Name)),
            "rename" or "set_names" => Method(["name", "inplace", "level"], a => WithName(a.Required("name"))),
            "to_series" => Method(["index", "name"], a => new PdSeries(Labels.Clone(), a.Given("index") is { } i ? PdBuild.IndexFrom(i) : this, a["name"] ?? Name)),
            "to_frame" => Method(["index", "name"], a => new PdFrame(
                PdIndex.Of([a.Given("name") ?? (Name is PyNone ? PyInt.From(0) : Name)]),
                [Labels.Clone()],
                a.Bool("index", true) ? this : Default(Count))),
            "unique" => Method(["level"], _ => Of(ToSeries().UniqueValues(), Name)),
            "nunique" => Method(["dropna"], a => PyInt.From(ToSeries().CountUnique(a.Bool("dropna", true)))),
            "duplicated" => Method(["keep"], a => runtime.Array(new PyList([.. PdOps.Duplicated(Labels.Values.Select(static v => (IReadOnlyList<PyObject>)[v]).ToList(), a["keep"] ?? new PyStr("first")).Select(static b => (PyObject)PyBool.Of(b))]))),
            "isin" => Method(["values", "level"], a => runtime.Array(new PyList([.. ToSeries().IsIn(a.Required("values")).Values]))),
            "isna" or "isnull" => Method([], _ => runtime.Array(new PyList([.. Enumerable.Range(0, Count).Select(i => (PyObject)PyBool.Of(Labels.IsMissing(i)))]))),
            "notna" or "notnull" => Method([], _ => runtime.Array(new PyList([.. Enumerable.Range(0, Count).Select(i => (PyObject)PyBool.Of(!Labels.IsMissing(i)))]))),
            "min" => Method(["axis", "skipna"], _ => ToSeries().Reduce("min", true, false)),
            "max" => Method(["axis", "skipna"], _ => ToSeries().Reduce("max", true, false)),
            "argmin" => Method(["axis", "skipna"], _ => PyInt.From(ToSeries().ArgExtreme(false))),
            "argmax" => Method(["axis", "skipna"], _ => PyInt.From(ToSeries().ArgExtreme(true))),
            "sort_values" => Method(["return_indexer", "ascending", "na_position", "key"], a =>
            {
                var order = PdSort.Order([Labels], [a.Bool("ascending", true)], a.Str("na_position", "last"));
                return Take(order);
            }),
            "astype" => Method(["dtype", "copy"], a =>
            {
                var (type, spelling) = PdDTypeObject.Parse(a.Required("dtype"));
                return new PdIndex(Labels.Cast(type, spelling), Name, Names);
            }),
            "map" => Method(["mapper", "na_action"], a => Of(ToSeries().Map(a.Required("mapper"), false).Data.Values, Name)),
            "get_loc" => Method(["key"], a => PyInt.From(Locate(a.Required("key")))),
            "get_indexer" => Method(["target"], a => runtime.Array(new PyList([.. Pd.List(a.Required("target")).Select(t => (PyObject)PyInt.From(Find(t)))]))),
            "equals" => Method(["other"], a => PyBool.Of(a.Required("other") is PdIndex other && SameAs(other))),
            "union" => Method(["other", "sort"], a => PdOps.UnionIndex(this, PdBuild.IndexFrom(a.Required("other")), sort: a["sort"] is not PyBool { Value: false })),
            "intersection" => Method(["other", "sort"], a =>
            {
                var other = PdBuild.IndexFrom(a.Required("other"));
                return Take(Enumerable.Range(0, Count).Where(i => other.Find(this[i]) >= 0 && Find(this[i]) == i).ToList());
            }),
            "difference" => Method(["other", "sort"], a =>
            {
                var other = PdBuild.IndexFrom(a.Required("other"));
                var kept = Take(Enumerable.Range(0, Count).Where(i => other.Find(this[i]) < 0 && Find(this[i]) == i).ToList());
                return a["sort"] is PyBool { Value: false } ? kept : kept.Take(PdSort.Order([kept.Labels], [true], "last"));
            }),
            "append" => Method(["other"], a =>
            {
                var others = a.Required("other") is PyList list ? list.Items.Select(PdBuild.IndexFrom).ToList() : [PdBuild.IndexFrom(a.Required("other"))];
                return Of(PdColumn.Concat([Labels, .. others.Select(static o => o.Labels)]).Values);
            }),
            "drop" => Method(["labels", "errors"], a =>
            {
                var drop = Pd.IsListLike(a.Required("labels")) ? Pd.List(a.Required("labels")) : [a.Required("labels")];
                foreach (var label in drop)
                {
                    if (Find(label) < 0 && a.Str("errors", "raise") == "raise")
                    {
                        throw Pd.KeyError(new PyStr($"[{Pd.Repr(label)}] not found in axis"));
                    }
                }

                return Take(Enumerable.Range(0, Count).Where(i => !drop.Any(d => Pd.Same(d, this[i]))).ToList());
            }),
            "insert" => Method(["loc", "item"], a =>
            {
                var values = Labels.Values.ToList();
                values.Insert(Pd.ToInt(a.Required("loc"), "loc"), a.Required("item"));
                return Of(values, Name);
            }),
            "value_counts" => Method(["normalize", "sort", "ascending", "dropna"], a => ToSeries().ValueCounts(a)),
            "item" => Method([], _ => Count == 1 ? this[0] : throw Pd.ValueError("can only convert an array of size 1 to a Python scalar")),
            _ => null,
        };
    }

    private bool Monotonic(int direction)
    {
        for (var i = 1; i < Count; i++)
        {
            if (Pd.Order(this[i - 1], this[i]) * direction > 0)
            {
                return false;
            }
        }

        return true;
    }

    public PdSeries ToSeries() => new(Labels, Default(Count), Name);

    public PyObject? Binary(string op, PyObject other, bool reflected) => Pd.Guard(() => BinaryCore(op, other, reflected));

    private PyObject? BinaryCore(string op, PyObject other, bool reflected)
    {
        var result = ToSeries().Binary(op, other is PdIndex index ? index.ToSeries() : other, reflected) as PdSeries;
        return result is null ? null : Of(result.Data.Values);
    }

    public PyObject? Unary(string op) => null;

    public PyObject? Compare(string op, PyObject other) => Pd.Guard(() => CompareCore(op, other));

    private PyObject? CompareCore(string op, PyObject other)
    {
        var result = ToSeries().Compare(op, other is PdIndex index ? index.ToSeries() : other) as PdSeries;
        return result is null ? null : PdRuntime.Current is { } runtime ? runtime.Array(new PyList([.. result.Data.Values])) : new PyList([.. result.Data.Values]);
    }

    public override string Repr() => Pd.Guard(ReprCore);

    private string ReprCore()
    {
        if (IsRange)
        {
            var stop = RangeStart + (Count * RangeStep);
            var named = Name is PyNone ? string.Empty : $", name={Name.Repr()}";
            return $"RangeIndex(start={RangeStart}, stop={stop}, step={RangeStep}{named})";
        }

        if (IsMulti)
        {
            return MultiRepr();
        }

        var klass = TypeName;
        var isDate = Labels.Type == DType.DateTime;
        var dateOnly = isDate && Labels.Values.All(static v => v is not PdTimestamp t || t.IsMidnight);
        var items = Labels.Values.Select(v => isDate
            ? v is PdTimestamp t ? $"'{t.Text(dateOnly)}'" : "'NaT'"
            : Labels.Type == DType.Float64 ? PdFormat.FloatLabel(Pd.ToDouble(v)) : Labels.Type == DType.Object ? v.Repr() : v.Repr()).ToList();
        var justify = Labels.Type != DType.Object || !Labels.Values.All(static v => v is PyStr);
        var attributes = new List<string> { $"dtype='{Pd.Name(Labels.Type)}'" };

        if (Name is not PyNone)
        {
            attributes.Add($"name={Name.Repr()}");
        }

        if (Count > 100)
        {
            attributes.Add($"length={Count}");
        }

        if (isDate)
        {
            attributes.Add($"freq={(Freq is null ? "None" : $"'{Freq}'")}");
        }

        var data = Summary(items, klass, justify);
        return $"{klass}({data}{string.Join(", ", attributes)})";
    }

    /// <summary>pandas' <c>format_object_summary</c>: the bracketed list, wrapped at 80 columns.</summary>
    private static string Summary(List<string> items, string klass, bool justify)
    {
        const int width = 80;
        var space1 = "\n" + new string(' ', klass.Length + 1);
        var space2 = "\n" + new string(' ', klass.Length + 2);
        var n = items.Count;

        if (n == 0)
        {
            return "[], ";
        }

        if (n <= 2)
        {
            return $"[{string.Join(", ", items)}], ";
        }

        var truncated = n > 100;
        var head = truncated ? items.Take(10).ToList() : [];
        var tail = truncated ? items.Skip(n - 10).ToList() : items;

        if (justify && (truncated || !(string.Join(", ", head).Length < width && string.Join(", ", tail).Length < width)))
        {
            var longest = Math.Max(head.Count > 0 ? head.Max(static h => h.Length) : 0, tail.Max(static t => t.Length));
            head = [.. head.Select(h => h.PadLeft(longest))];
            tail = [.. tail.Select(t => t.PadLeft(longest))];
        }

        var summary = new StringBuilder();
        var line = space2;

        void Extend(string value, int displayWidth)
        {
            if (line.TrimEnd().Length + value.TrimEnd().Length >= displayWidth)
            {
                summary.Append(line.TrimEnd());
                line = space2;
            }

            line += value;
        }

        foreach (var value in head)
        {
            Extend(value + ", ", width);
        }

        if (truncated)
        {
            summary.Append(line.TrimEnd()).Append(space2).Append("...");
            line = space2;
        }

        foreach (var value in tail.Take(tail.Count - 1))
        {
            Extend(value + ", ", width);
        }

        Extend(tail[^1], width - 2);
        summary.Append(line).Append("],");
        var text = summary.ToString();
        text += text.Length > width ? space1 : " ";
        return "[" + text[space2.Length..];
    }

    private string MultiRepr()
    {
        var lines = Labels.Values.Select(static v => v.Repr()).ToList();
        var builder = new StringBuilder("MultiIndex([");

        for (var i = 0; i < lines.Count; i++)
        {
            builder.Append(i == 0 ? string.Empty : "\n            ").Append(lines[i]).Append(i < lines.Count - 1 ? "," : "]");
        }

        if (lines.Count == 0)
        {
            builder.Append(']');
        }

        builder.Append(",\n           names=[").Append(string.Join(", ", LevelNames.Select(static n => n.Repr()))).Append("])");
        return builder.ToString();
    }

    public override bool PyEquals(PyObject other) => ReferenceEquals(this, other);

    public override BigInteger PyHash() => throw Pd.TypeError($"unhashable type: '{TypeName}'");

    /// <summary>The label a <c>str</c> header shows, as pandas prints it in a table.</summary>
    public string LabelText(int position) => PdFormat.LabelText(this[position], Labels.Type);

    public static string Join(IEnumerable<string> parts) => string.Join(", ", parts);

    public static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
