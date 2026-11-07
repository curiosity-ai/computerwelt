using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules.Pandas;

/// <summary>
/// <c>.loc</c>, <c>.iloc</c>, <c>.at</c> and <c>.iat</c> over a Series or a DataFrame: by
/// label or by position, for reading and for writing.
/// </summary>
internal sealed class PdIndexer : PyObject
{
    private readonly PdSeries? _series;
    private readonly PdFrame? _frame;
    private readonly bool _positional;
    private readonly bool _scalar;

    public PdIndexer(PdSeries series, bool positional, bool scalar)
    {
        _series = series;
        _positional = positional;
        _scalar = scalar;
    }

    public PdIndexer(PdFrame frame, bool positional, bool scalar)
    {
        _frame = frame;
        _positional = positional;
        _scalar = scalar;
    }

    public override string TypeName => _positional ? "_iLocIndexer" : "_LocIndexer";

    public override string Repr() => $"<pandas.core.indexing.{TypeName} object>";

    public override PyObject GetItem(PyObject index) => PdRuntime.Current is { } runtime
        ? runtime.Run(() => Read(index))
        : Read(index);

    public override void SetItem(PyObject index, PyObject value)
    {
        if (PdRuntime.Current is { } runtime)
        {
            runtime.Run(() =>
            {
                WriteKey(index, value);
                return PyNone.Instance;
            });
        }
        else
        {
            WriteKey(index, value);
        }
    }

    /// <summary>A callable key is called with the object, as pandas allows.</summary>
    private PyObject Resolve(PyObject key) =>
        key is PyCallable and not PyType && PdRuntime.Current is { } runtime
            ? runtime.Call(key, (PyObject?)_series ?? _frame!)
            : key;

    /// <summary>True when a key names one row (or column) rather than a selection of them.</summary>
    private static bool IsScalarKey(PyObject key) => !Pd.IsListLike(key) && key is not (PySlice or PdSeries or PdIndex);

    private List<int> Rows(PyObject key, PdIndex index, int count) =>
        PdSelect.Positions(key, count, index, _positional);

    private PyObject Read(PyObject key)
    {
        if (_series is { } series)
        {
            key = Resolve(key);

            if (IsScalarKey(key) || (key is PyTuple && (series.Index.IsMulti || series.Index.Find(key) >= 0)))
            {
                if (_positional)
                {
                    return series.Data.Values[PdSelect.Position(key, series.Count)];
                }

                var found = PdSelect.LabelPositions(key, series.Index);

                if (found.Count == 1 && series.Index.Find(key) >= 0)
                {
                    return series.Data.Values[found[0]];
                }

                var part = series.Take(found);

                if (series.Index.Find(key) < 0 && series.Index.IsMulti)
                {
                    part.Index = PdSelect.DropLevels(part.Index, key is PyTuple t ? t.Items.Count : 1);
                }

                return part;
            }

            return series.Take(Rows(key, series.Index, series.Count));
        }

        var frame = _frame!;
        PyObject rowKey;
        PyObject columnKey;

        if (key is PyTuple { Items.Count: 2 } pair && !(frame.Index.IsMulti && !_positional && frame.Index.Find(key) >= 0))
        {
            (rowKey, columnKey) = (Resolve(pair.Items[0]), Resolve(pair.Items[1]));
        }
        else
        {
            (rowKey, columnKey) = (Resolve(key), new PySlice(null, null, null));
        }

        var singleRow = IsScalarKey(rowKey);
        var singleColumn = IsScalarKey(columnKey);
        List<int> rows;
        var droppedLevels = 0;

        if (singleRow && !_positional)
        {
            rows = PdSelect.LabelPositions(rowKey, frame.Index);

            if (frame.Index.Find(rowKey) < 0 && frame.Index.IsMulti)
            {
                droppedLevels = rowKey is PyTuple t ? t.Items.Count : 1;
                singleRow = false;
            }
            else if (rows.Count > 1)
            {
                singleRow = false;
            }
        }
        else
        {
            rows = singleRow ? [PdSelect.Position(rowKey, frame.RowCount)] : Rows(rowKey, frame.Index, frame.RowCount);
        }

        var columns = singleColumn
            ? _positional ? [PdSelect.Position(columnKey, frame.Columns.Count)] : PdSelect.LabelPositions(columnKey, frame.Columns)
            : PdSelect.Positions(columnKey, frame.Columns.Count, frame.Columns, _positional);

        if (singleColumn && columns.Count > 1)
        {
            singleColumn = false;
        }

        if (singleRow && singleColumn)
        {
            return frame.Data[columns[0]].Values[rows[0]];
        }

        if (singleRow)
        {
            return frame.TakeColumns(columns).Row(rows[0]);
        }

        if (singleColumn)
        {
            var column = frame.Column(columns[0]).Take(rows);

            if (droppedLevels > 0)
            {
                column.Index = PdSelect.DropLevels(column.Index, droppedLevels);
            }

            return column;
        }

        var result = frame.TakeColumns(columns).TakeRows(rows);

        if (droppedLevels > 0)
        {
            result.Index = PdSelect.DropLevels(result.Index, droppedLevels);
        }

        return result;
    }

    private void WriteKey(PyObject key, PyObject value)
    {
        if (_series is { } series)
        {
            key = Resolve(key);
            series.Assign(key, value, positional: _positional);
            return;
        }

        var frame = _frame!;
        PyObject rowKey;
        PyObject? columnKey;

        if (key is PyTuple { Items.Count: 2 } pair && !(frame.Index.IsMulti && frame.Index.Find(key) >= 0))
        {
            (rowKey, columnKey) = (Resolve(pair.Items[0]), Resolve(pair.Items[1]));
        }
        else
        {
            (rowKey, columnKey) = (Resolve(key), null);
        }

        // A row label that is not there yet is appended, as `df.loc[new] = ...` enlarges.
        if (!_positional && IsScalarKey(rowKey) && frame.Index.FindAll(rowKey) is null && PdSelect.Prefix(rowKey, frame.Index) is not { Count: > 0 })
        {
            var labels = frame.Index.Labels.Values.Append(rowKey).ToList();

            // A whole new row is appended value by value, so an int column given an int stays
            // int; a partial one is filled with missing values first and then written.
            if (columnKey is null && frame.Columns.Count > 0)
            {
                var values = RowValues(frame, value);
                var grown = frame.Data.Select((d, c) => PdColumn.Concat([d, PdColumn.Infer([values[c]])])).ToList();
                frame.Become(new PdFrame(frame.Columns, grown, PdIndex.Of(labels, frame.Index.Name)));
                return;
            }

            var padded = frame.Data.Select(d => d.Take([.. Enumerable.Range(0, frame.RowCount), -1])).ToList();
            frame.Become(new PdFrame(frame.Columns, padded, PdIndex.Of(labels, frame.Index.Name)));
        }

        List<int> rows = IsScalarKey(rowKey)
            ? _positional ? [PdSelect.Position(rowKey, frame.RowCount)] : PdSelect.LabelPositions(rowKey, frame.Index)
            : Rows(rowKey, frame.Index, frame.RowCount);

        List<int>? columns = null;

        if (columnKey is not null)
        {
            if (!_positional)
            {
                // Columns named that do not exist yet are created, filled with missing values.
                foreach (var label in IsScalarKey(columnKey) ? [columnKey] : columnKey is PySlice ? [] : Pd.List(columnKey))
                {
                    if (frame.Columns.Find(label) < 0)
                    {
                        frame.SetColumn(label, PdColumn.Repeat(Pd.NaN, frame.RowCount));
                    }
                }
            }

            columns = IsScalarKey(columnKey)
                ? _positional ? [PdSelect.Position(columnKey, frame.Columns.Count)] : [frame.Columns.Locate(columnKey)]
                : PdSelect.Positions(columnKey, frame.Columns.Count, frame.Columns, _positional);
        }

        Write(rows, columns, value);
    }

    /// <summary>One value per column for a new row: broadcast, by position, or by label.</summary>
    private static List<PyObject> RowValues(PdFrame frame, PyObject value)
    {
        switch (value)
        {
            case PdSeries series:
                return [.. frame.Columns.Labels.Values.Select(l => series.Index.Find(l) is var at && at >= 0 ? series.Data.Values[at] : Pd.NaN)];
            case PyDict dict:
                return [.. frame.Columns.Labels.Values.Select(l => dict.TryGetValue(l, out var v) ? v : Pd.NaN)];
            default:
                if (Pd.IsListLike(value))
                {
                    var items = Pd.List(value);
                    return items.Count == frame.Columns.Count
                        ? items
                        : throw Pd.ValueError("cannot set a row with mismatched columns");
                }

                return [.. Enumerable.Repeat(value, frame.Columns.Count)];
        }
    }

    /// <summary>Writes a value into the selected cells, broadcasting or aligning it as pandas does.</summary>
    public void Write(List<int> rows, List<int>? columns, PyObject value)
    {
        var frame = _frame!;
        columns ??= [.. Enumerable.Range(0, frame.Columns.Count)];

        for (var k = 0; k < columns.Count; k++)
        {
            var c = columns[k];
            var column = frame.OwnColumn(c);
            PdColumn updated;

            switch (value)
            {
                case PdFrame source:
                {
                    var sc = source.Columns.Find(frame.Columns[c]);
                    var values = rows.Select(r => sc >= 0 && source.Index.Find(frame.Index[r]) is var sr && sr >= 0 ? source.Data[sc].Values[sr] : Pd.NaN).ToList();
                    updated = column.WithMany(rows, values);
                    break;
                }

                case PdSeries series when columns.Count == 1 || rows.Count != 1:
                {
                    var values = rows.Select(r => series.Index.Find(frame.Index[r]) is var at && at >= 0 ? series.Data.Values[at] : PdColumn.MissingFor(series.Data.Type)).ToList();
                    updated = column.WithMany(rows, values);
                    break;
                }

                case PdSeries series:
                {
                    var at = series.Index.Find(frame.Columns[c]);
                    updated = column.With(rows, at >= 0 ? series.Data.Values[at] : Pd.NaN);
                    break;
                }

                default:
                    if (Pd.IsListLike(value))
                    {
                        var items = Pd.List(value);

                        if (items.Count > 0 && Pd.IsListLike(items[0]) && rows.Count > 1 && columns.Count > 1)
                        {
                            updated = column.WithMany(rows, [.. rows.Select((_, i) => Pd.List(items[i])[k])]);
                        }
                        else if (columns.Count == 1)
                        {
                            if (items.Count != rows.Count)
                            {
                                throw Pd.ValueError($"Must have equal len keys and value when setting with an iterable");
                            }

                            updated = column.WithMany(rows, items);
                        }
                        else if (rows.Count == 1 || items.Count == columns.Count)
                        {
                            if (items.Count != columns.Count)
                            {
                                throw Pd.ValueError("Must have equal len keys and value when setting with an iterable");
                            }

                            updated = column.With(rows, items[k]);
                        }
                        else
                        {
                            throw Pd.ValueError("Must have equal len keys and value when setting with an iterable");
                        }
                    }
                    else
                    {
                        updated = column.With(rows, value);
                    }

                    break;
            }

            frame.Data[c] = updated;
        }
    }
}
