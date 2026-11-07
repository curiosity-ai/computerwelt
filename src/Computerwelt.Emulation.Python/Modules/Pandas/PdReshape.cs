using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules.Pandas;

/// <summary>Combining and reshaping: merge, join, concat, pivot_table, pivot, melt, get_dummies, cut.</summary>
internal static class PdReshape
{
    public static readonly string[] MergeParameters =
        ["left", "right", "how", "on", "left_on", "right_on", "left_index", "right_index", "sort", "suffixes", "copy", "indicator", "validate"];

    public static readonly string[] PivotTableParameters =
        ["data", "values", "index", "columns", "aggfunc", "fill_value", "margins", "dropna", "margins_name", "observed", "sort"];

    public static readonly string[] MeltParameters =
        ["frame", "id_vars", "value_vars", "var_name", "value_name", "col_level", "ignore_index"];

    private static List<PyObject> Labels(PyObject? value) =>
        value is null ? [] : Pd.IsListLike(value) ? Pd.List(value) : [value];

    /// <summary><c>merge</c>: a database-style join on columns or on the index.</summary>
    public static PdFrame Merge(PdFrame left, PdArgs arguments)
    {
        var right = arguments.Required("right") switch
        {
            PdFrame frame => frame,
            PdSeries series when series.Name is not PyNone => series.ToFrame(null),
            var other => throw Pd.TypeError($"Can only merge Series or DataFrame objects, a {other.TypeName} was passed"),
        };

        var how = arguments.Str("how", "inner");
        var leftIndex = arguments.Bool("left_index", false);
        var rightIndex = arguments.Bool("right_index", false);
        var leftOn = Labels(arguments.Given("left_on") ?? arguments.Given("on"));
        var rightOn = Labels(arguments.Given("right_on") ?? arguments.Given("on"));

        if (how == "cross")
        {
            // Every pairing, keyed on nothing: a constant key on both sides does exactly that.
            var constant = new PyStr("__cross__");
            var l = new PdFrame(PdIndex.Of([.. left.Columns.Labels.Values, constant]), [.. left.Data, PdColumn.Repeat(PyInt.From(0), left.RowCount)], left.Index);
            var r = new PdFrame(PdIndex.Of([.. right.Columns.Labels.Values, constant]), [.. right.Data, PdColumn.Repeat(PyInt.From(0), right.RowCount)], right.Index);
            var keywords = new PyDict();
            keywords.Set(new PyStr("on"), constant);
            keywords.Set(new PyStr("how"), new PyStr("inner"));

            if (arguments.Given("suffixes") is { } given)
            {
                keywords.Set(new PyStr("suffixes"), given);
            }

            var crossed = Merge(l, new PdArgs("merge", [r], keywords, MergeParameters.Skip(1).ToArray()));
            return crossed.TakeColumns([.. Enumerable.Range(0, crossed.Columns.Count).Where(c => !Pd.Same(crossed.Columns[c], constant))]);
        }

        if (leftOn.Count == 0 && rightOn.Count == 0 && !leftIndex && !rightIndex)
        {
            leftOn = [.. left.Columns.Labels.Values.Where(l => right.Columns.Find(l) >= 0)];
            rightOn = leftOn;

            if (leftOn.Count == 0)
            {
                throw Pd.ValueError("No common columns to perform merge on. Merge options: left_on=None, right_on=None, left_index=False, right_index=False");
            }
        }

        var leftKeys = leftIndex ? IndexLevels(left.Index) : leftOn.Select(l => left.Column(l).Data).ToList();
        var rightKeys = rightIndex ? IndexLevels(right.Index) : rightOn.Select(l => right.Column(l).Data).ToList();

        if (leftKeys.Count != rightKeys.Count)
        {
            throw Pd.ValueError("len(right_on) must equal len(left_on)");
        }

        var suffixes = arguments.Given("suffixes") is { } s ? Pd.List(s).Select(static x => x is PyNone ? string.Empty : Pd.Text(x)).ToList() : ["_x", "_y"];
        var pairs = JoinRows(leftKeys, rightKeys, how, arguments.Bool("sort", false));

        // Columns: the keys once (when both sides share the name), then each side's others,
        // with suffixes on the names both sides use.
        var sharedKeys = leftIndex || rightIndex
            ? []
            : leftOn.Where((l, i) => Pd.Same(l, rightOn[i])).ToList();
        var labels = new List<PyObject>();
        var data = new List<PdColumn>();
        var leftPositions = pairs.Select(static p => p.Left).ToList();
        var rightPositions = pairs.Select(static p => p.Right).ToList();

        for (var c = 0; c < left.Columns.Count; c++)
        {
            var label = left.Columns[c];
            var isKey = sharedKeys.Any(k => Pd.Same(k, label));
            var clash = !isKey && right.Columns.Find(label) >= 0;

            if (isKey)
            {
                // A shared key takes the left value, or the right one where the left had no row.
                var k = leftOn.FindIndex(l => Pd.Same(l, label));
                var raw = pairs.Select(p => p.Left >= 0 ? leftKeys[k].Values[p.Left] : rightKeys[k].Values[p.Right]).ToList();
                var column = PdColumn.Infer(raw);
                data.Add(leftKeys[k].Type == rightKeys[k].Type && column.Type != leftKeys[k].Type && leftKeys[k].Type == DType.Float64 ? PdColumn.OfType(DType.Float64, column.Values) : column);
                labels.Add(label);
                continue;
            }

            labels.Add(clash ? Suffix(label, suffixes[0]) : label);
            data.Add(left.Data[c].Take(leftPositions));
        }

        for (var c = 0; c < right.Columns.Count; c++)
        {
            var label = right.Columns[c];

            if (sharedKeys.Any(k => Pd.Same(k, label)))
            {
                continue;
            }

            var clash = left.Columns.Find(label) >= 0;
            labels.Add(clash ? Suffix(label, suffixes[1]) : label);
            data.Add(right.Data[c].Take(rightPositions));
        }

        if (arguments.Given("indicator") is { } indicator && indicator.IsTruthy())
        {
            labels.Add(indicator is PyStr name ? name : new PyStr("_merge"));
            data.Add(new PdColumn(DType.Object, [.. pairs.Select(static p => (PyObject)new PyStr(p.Left < 0 ? "right_only" : p.Right < 0 ? "left_only" : "both"))]));
        }

        var index = leftIndex && rightIndex
            ? PdIndex.Of([.. pairs.Select(p => p.Left >= 0 ? left.Index[p.Left] : right.Index[p.Right])], left.Index.Name)
            : leftIndex && !rightIndex ? right.Index.Take([.. pairs.Select(static p => Math.Max(p.Right, 0))])
            : rightIndex && !leftIndex ? left.Index.Take([.. pairs.Select(static p => Math.Max(p.Left, 0))])
            : PdIndex.Default(pairs.Count);

        return new PdFrame(PdIndex.Of(labels), data, index);
    }

    private static PyObject Suffix(PyObject label, string suffix) => new PyStr(Pd.Text(label) + suffix);

    private static List<PdColumn> IndexLevels(PdIndex index) => index.IsMulti
        ? [.. Enumerable.Range(0, index.Names!.Count).Select(level => PdColumn.Infer([.. index.Labels.Values.Select(v => ((PyTuple)v).Items[level])]))]
        : [index.Labels];

    /// <summary>
    /// The row pairs a join produces: inner and left keep the left's order (with the right's
    /// matches in their order); right keeps the right's; outer sorts by key.
    /// </summary>
    private static List<(int Left, int Right)> JoinRows(List<PdColumn> leftKeys, List<PdColumn> rightKeys, string how, bool sort)
    {
        var leftCount = leftKeys[0].Length;
        var rightCount = rightKeys[0].Length;
        Pd.CheckSize((long)leftCount + rightCount);

        var rightLookup = new Dictionary<PyKey, List<int>>();
        for (var r = 0; r < rightCount; r++)
        {
            var key = Key(rightKeys, r);
            if (!rightLookup.TryGetValue(key, out var rows))
            {
                rightLookup[key] = rows = [];
            }

            rows.Add(r);
        }

        // The result's size is known before any of it is built, so an oversized join is
        // refused up front rather than after it has filled memory.
        long expected = 0;

        for (var l = 0; l < leftCount; l++)
        {
            var key = Key(leftKeys, l);
            expected += rightLookup.TryGetValue(key, out var rows) ? rows.Count : 1;
        }

        expected += how is "right" or "outer" ? rightCount : 0;
        Pd.CheckSize(how == "cross" ? (long)leftCount * rightCount : expected);

        var pairs = new List<(int Left, int Right)>();

        void Add(int l, int r) => pairs.Add((l, r));

        switch (how)
        {
            case "inner":
            case "left":
            case "outer":
            {
                var matched = new HashSet<int>();

                for (var l = 0; l < leftCount; l++)
                {
                    if (rightLookup.TryGetValue(Key(leftKeys, l), out var rows))
                    {
                        foreach (var r in rows)
                        {
                            Add(l, r);
                            matched.Add(r);
                        }
                    }
                    else if (how is "left" or "outer")
                    {
                        Add(l, -1);
                    }
                }

                if (how == "outer")
                {
                    for (var r = 0; r < rightCount; r++)
                    {
                        if (!matched.Contains(r))
                        {
                            Add(-1, r);
                        }
                    }

                    sort = true;
                }

                break;
            }

            case "right":
            {
                var leftLookup = new Dictionary<PyKey, List<int>>();
                for (var l = 0; l < leftCount; l++)
                {
                    var key = Key(leftKeys, l);
                    if (!leftLookup.TryGetValue(key, out var rows))
                    {
                        leftLookup[key] = rows = [];
                    }

                    rows.Add(l);
                }

                for (var r = 0; r < rightCount; r++)
                {
                    if (leftLookup.TryGetValue(Key(rightKeys, r), out var rows))
                    {
                        foreach (var l in rows)
                        {
                            Add(l, r);
                        }
                    }
                    else
                    {
                        Add(-1, r);
                    }
                }

                break;
            }

            case "cross":
                for (var l = 0; l < leftCount; l++)
                {
                    for (var r = 0; r < rightCount; r++)
                    {
                        Add(l, r);
                    }
                }

                break;

            default:
                throw Pd.ValueError($"Do not recognize join method {how}");
        }

        if (sort)
        {
            var keys = Enumerable.Range(0, leftKeys.Count)
                .Select(k => PdColumn.Infer([.. pairs.Select(p => p.Left >= 0 ? leftKeys[k].Values[p.Left] : rightKeys[k].Values[p.Right])]))
                .ToList();
            var order = PdSort.Order(keys, [.. keys.Select(_ => true)], "last");
            pairs = [.. order.Select(o => pairs[o])];
        }

        return pairs;

        static PyKey Key(List<PdColumn> columns, int row) => PdOps.RowKey([.. columns.Select(c => c.Values[row])]);
    }

    /// <summary><c>join</c>: a merge on the index (or on a column of the left against the right's index).</summary>
    public static PdFrame Join(PdFrame left, PdArgs arguments)
    {
        var other = arguments.Required("other") switch
        {
            PdFrame frame => frame,
            PdSeries series => series.ToFrame(null),
            var value => throw Pd.TypeError($"Can only join a DataFrame or Series, not {value.TypeName}"),
        };

        var how = arguments.Str("how", "left");
        var leftSuffix = arguments.Str("lsuffix", string.Empty);
        var rightSuffix = arguments.Str("rsuffix", string.Empty);
        var overlap = other.Columns.Labels.Values.Where(l => left.Columns.Find(l) >= 0).ToList();

        if (overlap.Count > 0 && leftSuffix.Length == 0 && rightSuffix.Length == 0)
        {
            throw Pd.ValueError($"columns overlap but no suffix specified: {PdIndex.Of(overlap).Repr()}");
        }

        var keywords = new PyDict();
        keywords.Set(new PyStr("how"), new PyStr(how));
        keywords.Set(new PyStr("right_index"), PyBool.True);
        keywords.Set(new PyStr("suffixes"), new PyTuple([new PyStr(leftSuffix), new PyStr(rightSuffix)]));
        keywords.Set(new PyStr("sort"), PyBool.Of(arguments.Bool("sort", false)));

        if (arguments.Given("on") is { } on)
        {
            keywords.Set(new PyStr("left_on"), on);
        }
        else
        {
            keywords.Set(new PyStr("left_index"), PyBool.True);
        }

        return Merge(left, new PdArgs("join", [other], keywords, MergeParameters.Skip(1).ToArray()));
    }

    /// <summary><c>pd.concat</c>.</summary>
    public static PyObject Concat(PdArgs arguments)
    {
        var objects = arguments.Required("objs") is PyDict dict
            ? [.. dict.Entries.Select(static e => e.Value)]
            : Pd.List(arguments.Required("objs"));
        var keys = arguments.Given("keys") is { } k ? Pd.List(k) : arguments.Required("objs") is PyDict d ? [.. d.Entries.Select(static e => e.Key)] : null;
        objects = [.. objects.Where(static o => o is not PyNone)];

        if (objects.Count == 0)
        {
            throw Pd.ValueError("No objects to concatenate");
        }

        var axis = arguments["axis"] switch
        {
            null or PyNone => 0,
            PyStr { Value: "columns" } => 1,
            PyStr => 0,
            var value => Pd.ToInt(value, "axis"),
        };
        var ignoreIndex = arguments.Bool("ignore_index", false);
        var join = arguments.Str("join", "outer");

        if (axis == 1)
        {
            var series = objects.SelectMany(o => o switch
            {
                PdSeries s => [s],
                PdFrame f => Enumerable.Range(0, f.Columns.Count).Select(f.Column),
                _ => throw Pd.TypeError($"cannot concatenate object of type '{o.TypeName}'; only Series and DataFrame objs are valid"),
            }).ToList();

            var unnamed = 0;
            var renamed = series.Select(s => s.Name is PyNone ? new PdSeries(s.Data, s.Index, PyInt.From(unnamed++)) : s).ToList();

            if (keys is not null && objects.All(static o => o is PdSeries))
            {
                renamed = [.. renamed.Select((s, i) => new PdSeries(s.Data, s.Index, keys[i]))];
            }

            var frame = ConcatColumns(renamed, sortLabels: false, inner: join == "inner");
            return ignoreIndex ? new PdFrame(PdIndex.Default(frame.Columns.Count), frame.Data, frame.Index) : frame;
        }

        if (objects.All(static o => o is PdSeries))
        {
            var parts = objects.Cast<PdSeries>().ToList();
            var data = PdColumn.Concat([.. parts.Select(static p => p.Data)]);
            var index = ignoreIndex ? PdIndex.Default(data.Length) : ConcatIndex([.. parts.Select(static p => p.Index)], keys);
            var name = parts.All(p => Pd.Same(p.Name, parts[0].Name)) ? parts[0].Name : PyNone.Instance;
            return new PdSeries(data, index, name);
        }

        var frames = objects.Select(o => o switch
        {
            PdFrame f => f,
            PdSeries s => s.ToFrame(null),
            _ => throw Pd.TypeError($"cannot concatenate object of type '{o.TypeName}'; only Series and DataFrame objs are valid"),
        }).ToList();

        var result = ConcatRows(frames, ignoreIndex, inner: join == "inner");

        if (!ignoreIndex && keys is not null)
        {
            result.Index = ConcatIndex([.. frames.Select(static f => f.Index)], keys);
        }

        return result;
    }

    private static PdIndex ConcatIndex(List<PdIndex> indexes, List<PyObject>? keys)
    {
        if (keys is null)
        {
            var name = indexes.All(i => Pd.Same(i.Name, indexes[0].Name)) ? indexes[0].Name : PyNone.Instance;
            return new PdIndex(PdColumn.Concat([.. indexes.Select(static i => i.Labels)]), name, indexes[0].IsMulti ? indexes[0].Names : null);
        }

        var labels = indexes.SelectMany((index, i) => index.Labels.Values.Select(l => (PyObject)new PyTuple([keys[i], l]))).ToList();
        return new PdIndex(new PdColumn(DType.Object, [.. labels]), null, [PyNone.Instance, indexes[0].Name]);
    }

    /// <summary>Stacks frames: the union of their columns in order of appearance, missing cells NaN.</summary>
    public static PdFrame ConcatRows(List<PdFrame> frames, bool ignoreIndex, bool inner = false)
    {
        var labels = new List<PyObject>();

        foreach (var frame in frames)
        {
            foreach (var label in frame.Columns.Labels.Values)
            {
                if (!labels.Any(l => Pd.Same(l, label)))
                {
                    labels.Add(label);
                }
            }
        }

        if (inner)
        {
            labels = [.. labels.Where(l => frames.All(f => f.Columns.Find(l) >= 0))];
        }

        var data = labels.Select(label => PdColumn.Concat([.. frames.Select(f =>
            f.Columns.Find(label) is var at && at >= 0 ? f.Data[at] : new PdColumn(DType.Float64, Enumerable.Repeat<PyObject>(Pd.NaN, f.RowCount).ToArray()))])).ToList();

        // A column absent from some frames is filled with NaN; object stays object, numbers widen.
        var index = ignoreIndex
            ? PdIndex.Default(frames.Sum(static f => f.RowCount))
            : ConcatIndex([.. frames.Select(static f => f.Index)], null);

        var columnName = frames.All(f => Pd.Same(f.Columns.Name, frames[0].Columns.Name)) ? frames[0].Columns.Name : PyNone.Instance;
        var columns = frames.All(static f => f.Columns.IsMulti) ? new PdIndex(new PdColumn(DType.Object, [.. labels]), null, frames[0].Columns.Names) : PdIndex.Of(labels, columnName);
        return new PdFrame(columns, data, index);
    }

    /// <summary>
    /// Series side by side as columns, aligned on the union of their indexes (sorted unless
    /// every index is the same), as <c>concat(axis=1)</c> and <c>describe</c> build frames.
    /// </summary>
    public static PdFrame ConcatColumns(List<PdSeries> series, bool sortLabels = true, IReadOnlyList<string>? preferredOrder = null, bool inner = false)
    {
        if (series.Count == 0)
        {
            return new PdFrame(PdIndex.Of([]), [], PdIndex.Default(0));
        }

        PdIndex index = series[0].Index;

        foreach (var s in series.Skip(1))
        {
            if (inner)
            {
                index = index.Take([.. Enumerable.Range(0, index.Count).Where(i => s.Index.Find(index[i]) >= 0)]);
            }
            else if (!index.SameAs(s.Index))
            {
                index = UnionKeepingOrder(index, s.Index, sortLabels, preferredOrder);
            }
        }

        var data = series.Select(s => s.Index.SameAs(index) ? s.Data : s.Data.Take([.. Enumerable.Range(0, index.Count).Select(i => s.Index.Find(index[i]))])).ToList();
        return new PdFrame(PdIndex.Of([.. series.Select(static s => s.Name)]), data, index);
    }

    private static PdIndex UnionKeepingOrder(PdIndex left, PdIndex right, bool sort, IReadOnlyList<string>? preferredOrder)
    {
        if (preferredOrder is null)
        {
            return PdOps.UnionIndex(left, right, sort);
        }

        // describe(include='all'): the categorical rows, then the numeric ones, max last.
        var union = PdOps.UnionIndex(left, right, sort: false).Labels.Values.ToList();
        var head = preferredOrder.Select(o => union.FirstOrDefault(l => Pd.Text(l) == o)).OfType<PyObject>().ToList();
        var rest = union.Where(l => !preferredOrder.Contains(Pd.Text(l)) && Pd.Text(l) != "max").ToList();
        var max = union.Where(static l => Pd.Text(l) == "max").ToList();
        return PdIndex.Of([.. head, .. rest, .. max]);
    }

    /// <summary><c>pivot_table</c>: group by index and columns keys, reduce the values, spread the columns key.</summary>
    public static PdFrame PivotTable(PdFrame frame, PdArgs arguments)
    {
        var indexKeys = Labels(arguments.Given("index"));
        var columnKeys = Labels(arguments.Given("columns"));
        var aggfunc = arguments.Given("aggfunc") ?? new PyStr("mean");
        var fill = arguments.Given("fill_value");
        var valuesGiven = arguments.Given("values");
        var values = valuesGiven is null
            ? frame.Columns.Labels.Values.Where(l => !indexKeys.Any(k => Pd.Same(k, l)) && !columnKeys.Any(k => Pd.Same(k, l)) && frame.Column(l).Data.IsNumeric).ToList()
            : Labels(valuesGiven);

        var keys = indexKeys.Concat(columnKeys).ToList();
        var groupArguments = new PyDict();
        groupArguments.Set(new PyStr("by"), new PyList(keys));
        var grouped = new PdGroupBy(frame, new PdArgs("groupby", [], groupArguments, ["by", "axis", "level", "as_index", "sort", "group_keys", "observed", "dropna"]));
        var selection = (PdGroupBy)grouped.GetItem(new PyList(values));
        var aggKeywords = new PyDict();
        aggKeywords.Set(new PyStr("func"), aggfunc);
        var reduced = PdRuntime.Current!.Machine.Call(selection.GetAttribute("agg")!, [], aggKeywords) switch
        {
            PdFrame f => f,
            PdSeries s => s.ToFrame(null),
            var other => throw Pd.TypeError($"unexpected aggregation result {other.TypeName}"),
        };

        if (columnKeys.Count == 0)
        {
            return Fill(reduced, fill);
        }

        // Split each group key into its row part and its column part, then spread.
        var rowParts = new List<PyObject>();
        var columnParts = new List<PyObject>();

        foreach (var label in reduced.Index.Labels.Values)
        {
            var parts = label is PyTuple tuple ? tuple.Items : [label];
            rowParts.Add(indexKeys.Count == 1 ? parts[0] : new PyTuple([.. parts.Take(indexKeys.Count)]));
            columnParts.Add(columnKeys.Count == 1 ? parts[indexKeys.Count] : new PyTuple([.. parts.Skip(indexKeys.Count)]));
        }

        var rowIndex = UniqueSorted(rowParts);
        var columnValues = UniqueSorted(columnParts);
        var outLabels = new List<PyObject>();
        var outData = new List<PdColumn>();
        var multiValues = valuesGiven is not null && Pd.IsListLike(valuesGiven) || values.Count > 1 || reduced.Columns.IsMulti;

        var rowAt = Positions(rowIndex, rowParts);
        var columnAt = Positions(columnValues, columnParts);

        for (var v = 0; v < reduced.Columns.Count; v++)
        {
            for (var cv = 0; cv < columnValues.Count; cv++)
            {
                var columnValue = columnValues[cv];
                var raw = new PyObject[rowIndex.Count];
                Array.Fill(raw, fill ?? Pd.NaN);

                for (var r = 0; r < reduced.RowCount; r++)
                {
                    if (columnAt[r] == cv)
                    {
                        raw[rowAt[r]] = reduced.Data[v].Values[r];
                    }
                }

                var label = reduced.Columns[v];
                outLabels.Add(multiValues ? new PyTuple([.. label is PyTuple t ? t.Items : [label], .. columnValue is PyTuple ct ? ct.Items : [columnValue]]) : columnValue);
                var column = PdColumn.Infer(raw);
                outData.Add(reduced.Data[v].Type == DType.Float64 && column.Type == DType.Int64 && fill is null ? PdColumn.OfType(DType.Float64, column.Values) : column);
            }
        }

        var columnsIndex = multiValues
            ? new PdIndex(new PdColumn(DType.Object, [.. outLabels]), null, [.. Enumerable.Repeat<PyObject>(PyNone.Instance, ((PyTuple)outLabels[0]).Items.Count - columnKeys.Count), .. columnKeys])
            : new PdIndex(PdColumn.Infer(outLabels), columnKeys.Count == 1 ? columnKeys[0] : null);
        var rows = indexKeys.Count == 1
            ? new PdIndex(PdColumn.Infer(rowIndex), indexKeys[0])
            : new PdIndex(new PdColumn(DType.Object, [.. rowIndex]), null, indexKeys);

        return new PdFrame(columnsIndex, outData, rows);

        static List<PyObject> UniqueSorted(List<PyObject> items)
        {
            var unique = new List<PyObject>();
            var seen = new HashSet<PyKey>();

            foreach (var item in items)
            {
                if (seen.Add(new PyKey(item)))
                {
                    unique.Add(item);
                }
            }

            unique.Sort(Pd.Order);
            return unique;
        }
    }

    /// <summary>For each item, its position among <paramref name="distinct"/> (missing values match each other).</summary>
    private static int[] Positions(IReadOnlyList<PyObject> distinct, IReadOnlyList<PyObject> items)
    {
        var lookup = new Dictionary<PyKey, int>();

        for (var i = 0; i < distinct.Count; i++)
        {
            lookup.TryAdd(new PyKey(PdOps.Normalize(distinct[i])), i);
        }

        return [.. items.Select(item => lookup.TryGetValue(new PyKey(PdOps.Normalize(item)), out var at) ? at : -1)];
    }

    private static PdFrame Fill(PdFrame frame, PyObject? fill) =>
        fill is null ? frame : frame.MapColumns(c => new PdSeries(c, frame.Index, null).FillNa(fill, null, null).Data);

    /// <summary><c>pivot</c>: reshape without aggregation; a repeated pair is an error.</summary>
    public static PdFrame Pivot(PdFrame frame, PdArgs arguments)
    {
        var indexLabel = arguments.Given("index");
        var columnsLabel = arguments.Required("columns");
        var valuesGiven = arguments.Given("values");
        var rowKeys = indexLabel is null ? frame.Index.Labels : frame.Column(indexLabel).Data;
        var columnKeys = frame.Column(columnsLabel).Data;
        var values = valuesGiven is null
            ? frame.Columns.Labels.Values.Where(l => !Pd.Same(l, columnsLabel) && (indexLabel is null || !Pd.Same(l, indexLabel))).ToList()
            : Labels(valuesGiven);

        var seen = new HashSet<PyKey>();
        for (var r = 0; r < frame.RowCount; r++)
        {
            if (!seen.Add(PdOps.RowKey([rowKeys.Values[r], columnKeys.Values[r]])))
            {
                throw Pd.ValueError("Index contains duplicate entries, cannot reshape");
            }
        }

        var rows = Sorted(rowKeys.Values);
        var columns = Sorted(columnKeys.Values);
        var multi = valuesGiven is null && values.Count > 1 || valuesGiven is not null && Pd.IsListLike(valuesGiven);
        var labels = new List<PyObject>();
        var data = new List<PdColumn>();

        var rowAt = Positions(rows, rowKeys.Values);
        var columnAt = Positions(columns, columnKeys.Values);

        foreach (var value in values)
        {
            var source = frame.Column(value).Data;

            for (var cv = 0; cv < columns.Count; cv++)
            {
                var column = columns[cv];
                var raw = new PyObject[rows.Count];
                Array.Fill(raw, PdColumn.MissingFor(source.Type));

                for (var r = 0; r < frame.RowCount; r++)
                {
                    if (columnAt[r] == cv)
                    {
                        raw[rowAt[r]] = source.Values[r];
                    }
                }

                labels.Add(multi ? new PyTuple([value, column]) : column);
                var built = PdColumn.Infer(raw);
                data.Add(source.Type == DType.Float64 && built.Type == DType.Int64 ? PdColumn.OfType(DType.Float64, built.Values) : built);
            }
        }

        var columnIndex = multi
            ? new PdIndex(new PdColumn(DType.Object, [.. labels]), null, [PyNone.Instance, columnsLabel])
            : new PdIndex(PdColumn.Infer(labels), columnsLabel);
        return new PdFrame(columnIndex, data, new PdIndex(PdColumn.Infer(rows), indexLabel ?? frame.Index.Name));

        static List<PyObject> Sorted(IEnumerable<PyObject> values)
        {
            var unique = new List<PyObject>();
            var seen = new HashSet<PyKey>();

            foreach (var value in values)
            {
                if (seen.Add(new PyKey(PdOps.Normalize(value))))
                {
                    unique.Add(value);
                }
            }

            unique.Sort(Pd.Order);
            return unique;
        }
    }

    /// <summary><c>melt</c>: wide to long.</summary>
    public static PdFrame Melt(PdFrame frame, PdArgs arguments)
    {
        var ids = Labels(arguments.Given("id_vars"));
        var values = arguments.Given("value_vars") is { } given
            ? Labels(given)
            : frame.Columns.Labels.Values.Where(l => !ids.Any(i => Pd.Same(i, l))).ToList();
        var variableName = arguments.Given("var_name") ?? (frame.Columns.Name is PyNone ? new PyStr("variable") : frame.Columns.Name);
        var valueName = arguments.Given("value_name") ?? new PyStr("value");
        var rows = frame.RowCount;
        Pd.CheckSize((long)rows * values.Count * (ids.Count + 2));

        var data = new List<PdColumn>();
        var positions = Enumerable.Range(0, values.Count).SelectMany(_ => Enumerable.Range(0, rows)).ToList();

        foreach (var id in ids)
        {
            data.Add(frame.Column(id).Data.Take(positions));
        }

        data.Add(PdColumn.Infer([.. values.SelectMany(v => Enumerable.Repeat(v, rows))]));
        data.Add(PdColumn.Concat([.. values.Select(v => frame.Column(v).Data)]));

        var index = arguments.Bool("ignore_index", true)
            ? PdIndex.Default(positions.Count)
            : frame.Index.Take(positions);
        return new PdFrame(PdIndex.Of([.. ids, variableName, valueName]), data, index);
    }

    /// <summary><c>get_dummies</c>: one indicator column per distinct value, sorted.</summary>
    public static PdFrame GetDummies(PdArgs arguments)
    {
        var data = arguments.Required("data");
        var prefixSeparator = arguments.Str("prefix_sep", "_");
        var dropFirst = arguments.Bool("drop_first", false);
        var asType = arguments.Given("dtype") is { } dtype ? PdDTypeObject.Parse(dtype).Type : DType.Bool;

        if (data is PdFrame frame)
        {
            var targets = arguments.Given("columns") is { } c
                ? Labels(c)
                : frame.Columns.Labels.Values.Where(l => frame.Column(l).Data.Type == DType.Object).ToList();
            var keep = Enumerable.Range(0, frame.Columns.Count).Where(i => !targets.Any(t => Pd.Same(t, frame.Columns[i]))).ToList();
            var labels = keep.Select(i => frame.Columns[i]).ToList();
            var columns = keep.Select(i => frame.Data[i]).ToList();

            foreach (var target in targets)
            {
                var (dummyLabels, dummyData) = Dummies(frame.Column(target), Pd.Text(target) + prefixSeparator, dropFirst, asType);
                labels.AddRange(dummyLabels);
                columns.AddRange(dummyData);
            }

            return new PdFrame(PdIndex.Of(labels), columns, frame.Index);
        }

        var series = data as PdSeries ?? new PdSeries(PdColumn.Infer(Pd.List(data)), PdIndex.Default(Pd.List(data).Count), null);
        var prefix = arguments.Str("prefix") is { } p ? p + prefixSeparator : string.Empty;
        var (names, values) = Dummies(series, prefix, dropFirst, asType);
        return new PdFrame(PdIndex.Of(names), values, series.Index);
    }

    private static (List<PyObject>, List<PdColumn>) Dummies(PdSeries series, string prefix, bool dropFirst, DType type)
    {
        var categories = series.UniqueValues().Where(static v => !Pd.IsMissing(v)).ToList();
        categories.Sort(Pd.Order);

        if (dropFirst && categories.Count > 0)
        {
            categories.RemoveAt(0);
        }

        var labels = categories.Select(c => prefix.Length == 0 ? c : new PyStr(prefix + Pd.Text(c))).ToList();
        var data = categories.Select(c =>
        {
            var flags = PdColumn.Bools([.. series.Data.Values.Select(v => Pd.Same(v, c))]);
            return type == DType.Bool ? flags : flags.Cast(type, Pd.Name(type));
        }).ToList();
        return (labels, data);
    }

    /// <summary>
    /// <c>pd.cut</c>: each value's bin. pandas returns a Categorical of Intervals; here the
    /// result is an object Series of the labels (or the interval text, <c>(0, 10]</c>).
    /// </summary>
    public static PdSeries Cut(PdArgs arguments)
    {
        var data = arguments.Required("x");
        var series = data as PdSeries ?? new PdSeries(PdColumn.Infer(Pd.List(data)), PdIndex.Default(Pd.List(data).Count), null);
        var values = series.Data.Doubles();
        var right = arguments.Bool("right", true);
        var includeLowest = arguments.Bool("include_lowest", false);
        List<double> edges;

        if (arguments.Required("bins") is PyInt count)
        {
            var finite = values.Where(static v => !double.IsNaN(v)).ToList();
            var (low, high) = (finite.Min(), finite.Max());
            var n = (int)count.Value;
            var span = high - low;
            edges = [.. Enumerable.Range(0, n + 1).Select(i => low + (span * i / n))];
            var adjust = span * 0.001;

            if (right)
            {
                edges[0] -= adjust;
            }
            else
            {
                edges[^1] += adjust;
            }
        }
        else
        {
            edges = Pd.List(arguments.Required("bins")).Select(Pd.ToDouble).ToList();
        }

        var labels = arguments.Given("labels") is { } given && given is not PyBool ? Pd.List(given) : null;

        if (labels is not null && labels.Count != edges.Count - 1)
        {
            throw Pd.ValueError("Bin labels must be one fewer than the number of bin edges");
        }

        var precision = arguments.Int("precision", 3);
        var raw = values.Select(v =>
        {
            if (double.IsNaN(v))
            {
                return (PyObject)Pd.NaN;
            }

            for (var b = 0; b < edges.Count - 1; b++)
            {
                var inside = right
                    ? (v > edges[b] || (includeLowest && b == 0 && v == edges[b])) && v <= edges[b + 1]
                    : v >= edges[b] && (v < edges[b + 1] || (includeLowest && b == edges.Count - 2 && v == edges[b + 1]));

                if (inside)
                {
                    return arguments.Given("labels") is PyBool { Value: false }
                        ? PyInt.From(b)
                        : labels is not null ? labels[b] : new PyStr(Interval(edges[b], edges[b + 1], right, precision));
                }
            }

            return Pd.NaN;
        }).ToList();

        return new PdSeries(new PdColumn(DType.Object, [.. raw]), series.Index, series.Name);

        static string Interval(double low, double high, bool right, int precision)
        {
            string Text(double v) => v == Math.Floor(v) && Math.Abs(v) < 1e15 ? Pd.FloatText(v).Replace(".0", string.Empty, StringComparison.Ordinal) : Pd.FloatText(Math.Round(v, precision));
            return right ? $"({Text(low)}, {Text(high)}]" : $"[{Text(low)}, {Text(high)})";
        }
    }

    /// <summary><c>pd.crosstab(index, columns)</c>: counts of each pair.</summary>
    public static PdFrame Crosstab(PdArgs arguments)
    {
        var rows = (PdSeries)PdBuild.Series(new PdArgs("crosstab", [arguments.Required("index")], null, ["data", "index", "dtype", "name", "copy"]));
        var columns = (PdSeries)PdBuild.Series(new PdArgs("crosstab", [arguments.Required("columns")], null, ["data", "index", "dtype", "name", "copy"]));
        var rowName = arguments.Required("index") is PdSeries rs && rs.Name is not PyNone ? rs.Name : new PyStr("row_0");
        var columnName = arguments.Required("columns") is PdSeries cs && cs.Name is not PyNone ? cs.Name : new PyStr("col_0");
        var keywords = new PyDict();
        keywords.Set(new PyStr("index"), rowName);
        keywords.Set(new PyStr("columns"), columnName);
        keywords.Set(new PyStr("aggfunc"), new PyStr("size"));
        keywords.Set(new PyStr("fill_value"), PyInt.From(0));

        // A size count needs a value column to group; any column will do.
        var withCount = new PdFrame(PdIndex.Of([rowName, columnName, new PyStr("__count__")]), [rows.Data, columns.Data, PdColumn.Repeat(PyInt.From(1), rows.Count)], PdIndex.Default(rows.Count));
        keywords.Set(new PyStr("values"), new PyStr("__count__"));
        return PivotTable(withCount, new PdArgs("crosstab", [], keywords, PivotTableParameters.Skip(1).ToArray()));
    }
}
