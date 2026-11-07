using System.Numerics;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules.Pandas;

/// <summary><c>pd.DataFrame</c>: labelled columns sharing one row index.</summary>
internal sealed partial class PdFrame : PyObject, IPyOperand
{
    public PdFrame(PdIndex columns, List<PdColumn> data, PdIndex index)
    {
        if (columns.Count != data.Count)
        {
            throw Pd.ValueError($"{columns.Count} columns passed, passed data had {data.Count} columns");
        }

        foreach (var column in data)
        {
            if (column.Length != index.Count)
            {
                throw Pd.ValueError($"Length of values ({column.Length}) does not match length of index ({index.Count})");
            }
        }

        Pd.CheckSize((long)columns.Count * index.Count);
        Columns = columns;
        Data = data;
        Index = index;
    }

    public PdIndex Columns { get; set; }

    public List<PdColumn> Data { get; private set; }

    public PdIndex Index { get; set; }

    public int RowCount => Index.Count;

    public override string TypeName => "DataFrame";

    public override int? Length() => RowCount;

    public override IEnumerable<PyObject> Iterate() => Columns.Labels.Values;

    public override bool Contains(PyObject item) => Columns.Find(item) >= 0;

    public override bool IsTruthy() =>
        throw Pd.ValueError("The truth value of a DataFrame is ambiguous. Use a.empty, a.bool(), a.item(), a.any() or a.all().");

    public override BigInteger PyHash() => throw Pd.TypeError("unhashable type: 'DataFrame'");

    public override string Repr() => Pd.Guard(() => PdFormat.Frame(this));

    /// <summary>A column as a Series that shares its values until either side writes.</summary>
    public PdSeries Column(int position)
    {
        var data = Data[position];
        data.Shared = true;
        return new PdSeries(data, Index, Columns[position]);
    }

    /// <summary>The column with a label, raising <c>KeyError</c> when there is none.</summary>
    public PdSeries Column(PyObject label) => Column(Columns.Locate(label));

    /// <summary>One row as a Series over the columns, in their common dtype.</summary>
    public PdSeries Row(int position)
    {
        var type = PdColumn.Common(Data.Select(static d => d.Type));
        var values = Data.Select(d => d.Values[position]).ToArray();
        return new PdSeries(PdColumn.OfType(type, values), Columns, Index[position]);
    }

    public PdFrame TakeRows(IReadOnlyList<int> positions) =>
        new(Columns, [.. Data.Select(d => d.Take(positions))], Index.Take(positions));

    public PdFrame TakeColumns(IReadOnlyList<int> positions) =>
        new(Columns.Take(positions), [.. positions.Select(p => Share(Data[p]))], Index);

    private static PdColumn Share(PdColumn column)
    {
        column.Shared = true;
        return column;
    }

    public PdFrame Copy() => new(Columns, [.. Data.Select(static d => d.Clone())], Index);

    /// <summary>A frame whose columns are copied on first write, for methods that return "a copy".</summary>
    public PdFrame ShallowCopy() => new(Columns, [.. Data.Select(Share)], Index);

    /// <summary>The column's values, copied first when they are shared.</summary>
    public PdColumn OwnColumn(int position)
    {
        if (Data[position].Shared)
        {
            Data[position] = Data[position].Clone();
        }

        return Data[position];
    }

    /// <summary>Sets or adds a column.</summary>
    public void SetColumn(PyObject label, PdColumn column)
    {
        if (column.Length != RowCount)
        {
            throw Pd.ValueError($"Length of values ({column.Length}) does not match length of index ({RowCount})");
        }

        var at = Columns.Find(label);

        if (at >= 0)
        {
            Data[at] = column;
            return;
        }

        Pd.CheckSize((long)(Columns.Count + 1) * RowCount);
        Columns = PdIndex.Of([.. Columns.Labels.Values, label], Columns.Name);
        Data.Add(column);
    }

    /// <summary>Replaces the whole contents, as an in-place method does.</summary>
    public void Become(PdFrame other)
    {
        Columns = other.Columns;
        Data = other.Data;
        Index = other.Index;
    }

    /// <summary>Converts a value assigned to a column into a column of this frame's length.</summary>
    public PdColumn ColumnFrom(PyObject value)
    {
        switch (value)
        {
            case PdSeries series:
                if (series.Index.SameAs(Index))
                {
                    return series.Data.Clone();
                }

                return series.Data.Take([.. Enumerable.Range(0, RowCount).Select(i => series.Index.Find(Index[i]))]);

            case PdFrame frame when frame.Columns.Count == 1:
                return ColumnFrom(frame.Column(0));

            case PdIndex index:
                return CheckLength(index.Labels.Clone());

            case PyDict:
                throw Pd.TypeError("cannot set a column from a dict; build a Series from it first");

            default:
                if (Pd.IsListLike(value))
                {
                    return CheckLength(PdColumn.Infer(Pd.List(value)));
                }

                return PdColumn.Repeat(value, RowCount);
        }

        PdColumn CheckLength(PdColumn column) => column.Length == RowCount
            ? column
            : throw Pd.ValueError($"Length of values ({column.Length}) does not match length of index ({RowCount})");
    }

    // ---- subscripting -------------------------------------------------------------------

    public override PyObject GetItem(PyObject index) => PdRuntime.Current is { } runtime
        ? runtime.Run(() => Select(index))
        : Select(index);

    private PyObject Select(PyObject key)
    {
        switch (key)
        {
            case PySlice slice:
            {
                var positional = (slice.Start is null or PyNone or PyInt && slice.Stop is null or PyNone or PyInt) && Index.Labels.Type != DType.Float64;
                return TakeRows(PdSelect.Positions(slice, RowCount, Index, positional));
            }

            case PdSeries { Data.Type: DType.Bool } mask:
                return TakeRows(PdSelect.Mask(mask, Index));

            case PdFrame mask:
                return WhereFrame(mask, null, keep: true);

            case PdIndex or PyList or PyTuple or PdSeries when key is not PyTuple || Columns.Find(key) < 0:
            {
                var items = Pd.List(key);

                if (items.Count > 0 && items.All(static i => i is PyBool))
                {
                    return TakeRows(PdSelect.Positions(key, RowCount, Index, positional: false));
                }

                return TakeColumns(ColumnPositions(items));
            }
        }

        if (PdRuntime.Current is { } runtime && key is PyCallable and not PyType)
        {
            return Select(runtime.Call(key, this));
        }

        if (Columns.FindAll(key) is { } found)
        {
            return found.Count == 1 ? Column(found[0]) : TakeColumns(found);
        }

        if (PdSelect.Prefix(key, Columns) is { Count: > 0 } prefixed)
        {
            var part = TakeColumns(prefixed);
            part.Columns = PdSelect.DropLevels(part.Columns, key is PyTuple t ? t.Items.Count : 1);
            return part.Columns.Count == 1 && part.Columns[0] is PyStr { Value: "" } ? part.Column(0) : part;
        }

        throw Pd.KeyError(key);
    }

    /// <summary>The positions of the named columns, with pandas' message for those missing.</summary>
    public List<int> ColumnPositions(IReadOnlyList<PyObject> labels)
    {
        var positions = new List<int>();
        var missing = new List<PyObject>();

        foreach (var label in labels)
        {
            if (Columns.FindAll(label) is { } found)
            {
                positions.AddRange(found);
            }
            else
            {
                missing.Add(label);
            }
        }

        if (missing.Count > 0)
        {
            throw Pd.KeyError(new PyStr(missing.Count == labels.Count
                ? $"None of [{PdIndex.Of(labels).Repr()}] are in the [columns]"
                : $"[{string.Join(", ", missing.Select(static m => m.Repr()))}] not in index"));
        }

        return positions;
    }

    public override void SetItem(PyObject index, PyObject value)
    {
        if (PdRuntime.Current is { } runtime)
        {
            runtime.Run(() =>
            {
                Assign(index, value);
                return PyNone.Instance;
            });
        }
        else
        {
            Assign(index, value);
        }
    }

    private void Assign(PyObject key, PyObject value)
    {
        // A frame with no columns takes its length from the first one it is given.
        if (Columns.Count == 0 && RowCount == 0 && key is not (PyList or PdSeries or PdFrame))
        {
            if (value is PdSeries series)
            {
                Index = series.Index;
            }
            else if (Pd.IsListLike(value))
            {
                Index = PdIndex.Default(Pd.List(value).Count);
            }
        }

        switch (key)
        {
            case PdSeries { Data.Type: DType.Bool } mask:
                new PdIndexer(this, positional: false, scalar: false).Write(PdSelect.Mask(mask, Index), null, value);
                return;

            case PdFrame mask:
                Become(WhereFrame(mask, value, keep: false));
                return;

            case PyList or PdIndex:
            {
                var labels = Pd.List(key);

                if (value is PdFrame frame)
                {
                    for (var i = 0; i < labels.Count; i++)
                    {
                        SetColumn(labels[i], ColumnFrom(frame.Column(i)));
                    }

                    return;
                }

                foreach (var label in labels)
                {
                    SetColumn(label, ColumnFrom(value));
                }

                return;
            }
        }

        SetColumn(key, ColumnFrom(value));
    }

    public override void DeleteItem(PyObject index)
    {
        var at = Columns.Locate(index);
        var keep = Enumerable.Range(0, Columns.Count).Where(i => i != at).ToList();
        Columns = Columns.Take(keep);
        Data = [.. keep.Select(k => Data[k])];
    }

    public override PyObject? GetAttribute(string name) => Pd.Guard(() => Attribute(name));

    // ---- operators ----------------------------------------------------------------------

    public PyObject? Binary(string op, PyObject other, bool reflected) => Pd.Guard<PyObject?>(() => Combine(other, (left, right, count) =>
        reflected ? PdOps.Arithmetic(op, right, left, count) : PdOps.Arithmetic(op, left, right, count), comparison: false));

    public PyObject? Unary(string op) => Pd.Guard(() => UnaryCore(op));

    private PyObject? UnaryCore(string op) => op switch
    {
        "-" or "+" or "abs" or "~" => new PdFrame(Columns, [.. Enumerable.Range(0, Columns.Count).Select(c => ((PdSeries)Column(c).Unary(op)!).Data)], Index),
        _ => null,
    };

    public PyObject? Compare(string op, PyObject other) =>
        Pd.Guard<PyObject?>(() => Combine(other, (left, right, count) => PdOps.Compare(op, left, right, count), comparison: true));

    private PdFrame Combine(PyObject other, Func<PdOps.Operand, PdOps.Operand, int, PdColumn> kernel, bool comparison)
    {
        switch (other)
        {
            case PdFrame frame:
            {
                if (comparison && !(Index.SameAs(frame.Index) && Columns.SameAs(frame.Columns)))
                {
                    throw Pd.ValueError("Can only compare identically-labeled (both index and columns) DataFrame objects");
                }

                var (rows, leftRows, rightRows) = PdOps.Align(Index, frame.Index);
                var (columns, leftColumns, rightColumns) = PdOps.Align(Columns, frame.Columns);
                var data = new List<PdColumn>();

                for (var c = 0; c < columns.Count; c++)
                {
                    var left = leftColumns[c] >= 0 ? Data[leftColumns[c]].Take(leftRows) : PdColumn.Repeat(Pd.NaN, rows.Count);
                    var right = rightColumns[c] >= 0 ? frame.Data[rightColumns[c]].Take(rightRows) : PdColumn.Repeat(Pd.NaN, rows.Count);
                    data.Add(kernel(new PdOps.Operand(left), new PdOps.Operand(right), rows.Count));
                }

                return new PdFrame(columns, data, rows);
            }

            case PdSeries series:
            {
                // A Series lines up with the columns, one value per column.
                var (columns, leftColumns, rightColumns) = PdOps.Align(Columns, series.Index);
                var data = new List<PdColumn>();

                for (var c = 0; c < columns.Count; c++)
                {
                    var left = leftColumns[c] >= 0 ? Data[leftColumns[c]] : PdColumn.Repeat(Pd.NaN, RowCount);
                    var right = rightColumns[c] >= 0 ? series.Data.Values[rightColumns[c]] : Pd.NaN;
                    data.Add(kernel(new PdOps.Operand(left), new PdOps.Operand(right), RowCount));
                }

                return new PdFrame(columns, data, Index);
            }

            default:
            {
                if (Pd.IsListLike(other))
                {
                    var values = Pd.List(other);

                    if (values.Count != Columns.Count)
                    {
                        throw Pd.ValueError($"Unable to coerce to Series, length must be {Columns.Count}: given {values.Count}");
                    }

                    return new PdFrame(Columns, [.. Data.Select((d, c) => kernel(new PdOps.Operand(d), new PdOps.Operand(values[c]), RowCount))], Index);
                }

                return new PdFrame(Columns, [.. Data.Select(d => kernel(new PdOps.Operand(d), new PdOps.Operand(other), RowCount))], Index);
            }
        }
    }

    /// <summary><c>where</c> with a boolean frame: keep (or replace) values where it is true.</summary>
    public PdFrame WhereFrame(PdFrame mask, PyObject? other, bool keep)
    {
        var data = new List<PdColumn>();

        for (var c = 0; c < Columns.Count; c++)
        {
            var at = mask.Columns.Find(Columns[c]);
            var raw = new PyObject[RowCount];

            for (var r = 0; r < RowCount; r++)
            {
                var row = at >= 0 ? mask.Index.Find(Index[r]) : -1;
                var condition = row >= 0 && mask.Data[at].Values[row].IsTruthy() && !Pd.IsMissing(mask.Data[at].Values[row]);
                raw[r] = condition == keep
                    ? Data[c].Values[r]
                    : other switch
                    {
                        null => PdColumn.MissingFor(Data[c].Type),
                        PdFrame frame => frame.Columns.Find(Columns[c]) is var oc && oc >= 0 && frame.Index.Find(Index[r]) is var orow && orow >= 0 ? frame.Data[oc].Values[orow] : Pd.NaN,
                        PdSeries series => series.Index.Find(Columns[c]) is var sc && sc >= 0 ? series.Data.Values[sc] : Pd.NaN,
                        _ => other,
                    };
            }

            data.Add(PdColumn.Infer(raw));
        }

        return new PdFrame(Columns, data, Index);
    }

    // ---- shared computations ------------------------------------------------------------

    /// <summary>Moves the index into the columns (or drops it), leaving the default index.</summary>
    public PdFrame ResetIndex(bool drop)
    {
        if (drop)
        {
            return new PdFrame(Columns, [.. Data.Select(Share)], PdIndex.Default(RowCount));
        }

        var labels = new List<PyObject>();
        var columns = new List<PdColumn>();
        var names = Index.LevelNames;

        for (var level = 0; level < names.Count; level++)
        {
            var name = names[level] is PyNone
                ? names.Count == 1
                    ? Columns.Find(new PyStr("index")) >= 0 ? new PyStr("level_0") : new PyStr("index")
                    : new PyStr($"level_{level}")
                : names[level];

            labels.Add(name);
            columns.Add(Index.IsMulti
                ? PdColumn.Infer([.. Index.Labels.Values.Select(v => ((PyTuple)v).Items[level])])
                : Index.IsRange ? Index.Labels.Clone() : Index.Labels.Clone());
        }

        return new PdFrame(
            PdIndex.Of([.. labels, .. Columns.Labels.Values], Columns.Name),
            [.. columns, .. Data.Select(Share)],
            PdIndex.Default(RowCount));
    }

    /// <summary>Pearson correlation over the rows where both are present.</summary>
    public static double Pearson(PdSeries left, PdSeries right)
    {
        var (x, y) = Paired(left, right);

        if (x.Length < 2)
        {
            return double.NaN;
        }

        var meanX = x.Average();
        var meanY = y.Average();
        double sxy = 0, sxx = 0, syy = 0;

        for (var i = 0; i < x.Length; i++)
        {
            var dx = x[i] - meanX;
            var dy = y[i] - meanY;
            sxy += dx * dy;
            sxx += dx * dx;
            syy += dy * dy;
        }

        var result = sxy / Math.Sqrt(sxx * syy);
        return Math.Clamp(result, -1, 1);
    }

    /// <summary>Sample covariance over the rows where both are present.</summary>
    public static double Covariance(PdSeries left, PdSeries right, int ddof)
    {
        var (x, y) = Paired(left, right);

        if (x.Length - ddof <= 0)
        {
            return double.NaN;
        }

        var meanX = x.Average();
        var meanY = y.Average();
        var total = 0.0;

        for (var i = 0; i < x.Length; i++)
        {
            total += (x[i] - meanX) * (y[i] - meanY);
        }

        return total / (x.Length - ddof);
    }

    private static (double[] X, double[] Y) Paired(PdSeries left, PdSeries right)
    {
        var (_, l, r) = PdOps.Align(left.Index, right.Index);
        var x = new List<double>();
        var y = new List<double>();

        for (var i = 0; i < l.Length; i++)
        {
            if (l[i] < 0 || r[i] < 0)
            {
                continue;
            }

            var a = Pd.ToDouble(left.Data.Values[l[i]]);
            var b = Pd.ToDouble(right.Data.Values[r[i]]);

            if (!double.IsNaN(a) && !double.IsNaN(b))
            {
                x.Add(a);
                y.Add(b);
            }
        }

        return ([.. x], [.. y]);
    }
}
