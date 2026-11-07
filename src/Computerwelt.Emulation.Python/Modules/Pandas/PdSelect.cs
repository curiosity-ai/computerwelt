using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules.Pandas;

/// <summary>Turning a key — a label, a list, a slice, a mask — into row positions.</summary>
internal static class PdSelect
{
    /// <summary>
    /// The positions a list-like, a boolean mask or a slice selects. With
    /// <paramref name="positional"/> the integers are positions (<c>iloc</c>); otherwise they
    /// are labels (<c>loc</c>).
    /// </summary>
    public static List<int> Positions(PyObject key, int count, PdIndex index, bool positional)
    {
        switch (key)
        {
            case PySlice slice:
            {
                var (start, _, step, length) = positional ? slice.Resolve(count) : LabelSlice(slice, index);
                return [.. Enumerable.Range(0, length).Select(i => start + (i * step))];
            }

            case PdSeries { Data.Type: DType.Bool } mask:
                return Mask(mask, index);

            case PdSeries { Data.Type: DType.Object } mask when mask.Data.Values.All(static v => v is PyBool):
                return Mask(mask, index);

            case PyStr when !positional:
                return LabelPositions(key, index);
        }

        if (!Pd.IsListLike(key) && key is not PdSeries)
        {
            if (positional)
            {
                return [Position(key, count)];
            }

            return LabelPositions(key, index);
        }

        var items = key is PdSeries series ? [.. series.Data.Values] : Pd.List(key);

        if (items.Count > 0 && items.All(static i => i is PyBool))
        {
            if (items.Count != count)
            {
                throw Pd.IndexError($"Boolean index has wrong length: {items.Count} instead of {count}");
            }

            return [.. Enumerable.Range(0, count).Where(i => items[i].IsTruthy())];
        }

        if (positional)
        {
            return [.. items.Select(i => Position(i, count))];
        }

        var result = new List<int>(items.Count);
        var missing = new List<PyObject>();

        foreach (var label in items)
        {
            var found = index.FindAll(label);

            if (found is null)
            {
                missing.Add(label);
            }
            else
            {
                result.AddRange(found);
            }
        }

        if (missing.Count > 0)
        {
            throw Pd.KeyError(new PyStr(missing.Count == items.Count
                ? $"None of [{PdIndex.Of(items).Repr()}] are in the [index]"
                : $"[{string.Join(", ", missing.Select(static m => m.Repr()))}] not in index"));
        }

        return result;
    }

    /// <summary>The rows a boolean Series selects, aligned on labels when its index differs.</summary>
    public static List<int> Mask(PdSeries mask, PdIndex index)
    {
        if (mask.Count == index.Count && mask.Index.SameAs(index))
        {
            return [.. Enumerable.Range(0, mask.Count).Where(i => !Pd.IsMissing(mask.Data.Values[i]) && mask.Data.Values[i].IsTruthy())];
        }

        var result = new List<int>();

        for (var i = 0; i < index.Count; i++)
        {
            var at = mask.Index.Find(index[i]);

            if (at < 0)
            {
                throw Pd.ValueError("Unalignable boolean Series provided as indexer (index of the boolean Series and of the indexed object do not match).");
            }

            if (mask.Data.Values[at].IsTruthy() && !Pd.IsMissing(mask.Data.Values[at]))
            {
                result.Add(i);
            }
        }

        return result;
    }

    /// <summary>All the rows holding a label (or, on a multi-index, a leading part of one).</summary>
    public static List<int> LabelPositions(PyObject label, PdIndex index)
    {
        if (index.FindAll(label) is { } found)
        {
            return found;
        }

        if (Prefix(label, index) is { Count: > 0 } prefixed)
        {
            return prefixed;
        }

        throw Pd.KeyError(label);
    }

    /// <summary>On a multi-index, the rows whose leading levels equal <paramref name="label"/>.</summary>
    public static List<int>? Prefix(PyObject label, PdIndex index)
    {
        if (!index.IsMulti)
        {
            return null;
        }

        var parts = label is PyTuple tuple ? tuple.Items : [label];
        var result = new List<int>();

        for (var i = 0; i < index.Count; i++)
        {
            if (index[i] is PyTuple row && row.Items.Count > parts.Count && parts.Select((p, k) => Pd.Same(p, row.Items[k])).All(static b => b))
            {
                result.Add(i);
            }
        }

        return result;
    }

    /// <summary>The index left after a prefix selection: the remaining levels.</summary>
    public static PdIndex DropLevels(PdIndex index, int levels)
    {
        var names = index.Names!;
        var remaining = names.Count - levels;
        var labels = index.Labels.Values.Select(v =>
        {
            var items = ((PyTuple)v).Items;
            return remaining == 1 ? items[levels] : new PyTuple([.. items.Skip(levels)]);
        }).ToList();

        return remaining == 1
            ? PdIndex.Of(labels, names[levels])
            : new PdIndex(PdColumn.Infer(labels), null, [.. names.Skip(levels)]);
    }

    /// <summary>A position from an integer key, negative counting from the end.</summary>
    public static int Position(PyObject key, int count)
    {
        if (key is not PyInt integer || key is PyBool)
        {
            throw Pd.TypeError($"Cannot index by location index with a non-integer key");
        }

        var position = integer.Value < 0 ? integer.Value + count : integer.Value;

        if (position < 0 || position >= count)
        {
            throw Pd.IndexError("single positional indexer is out-of-bounds");
        }

        return (int)position;
    }

    /// <summary>
    /// A label slice, inclusive of both ends, as <c>loc</c> reads one: an absent bound on a
    /// sorted index falls where it would sort.
    /// </summary>
    public static (int Start, int Stop, int Step, int Count) LabelSlice(PySlice slice, PdIndex index)
    {
        var step = slice.Step is PyInt s ? (int)s.Value : 1;

        if (step == 0)
        {
            throw Pd.ValueError("slice step cannot be zero");
        }

        int Bound(PyObject? label, bool isStart)
        {
            if (label is null or PyNone)
            {
                return (isStart ^ (step < 0)) ? 0 : index.Count - 1;
            }

            if (index.FindAll(label) is { } found)
            {
                return (isStart ^ (step < 0)) ? found[0] : found[^1];
            }

            // An absent bound is placed by binary search on a sorted index, as pandas does.
            if (index.GetAttribute("is_monotonic_increasing") is PyBool { Value: true })
            {
                if (index.Labels.Type == DType.DateTime && label is PyStr text && PdTimestamp.Parse(text, null) is PdTimestamp moment)
                {
                    label = moment;
                }

                var low = 0;
                var high = index.Count;

                while (low < high)
                {
                    var middle = (low + high) / 2;
                    if (Pd.Order(index[middle], label) < 0)
                    {
                        low = middle + 1;
                    }
                    else
                    {
                        high = middle;
                    }
                }

                return isStart ? low : low - 1;
            }

            throw Pd.KeyError(label);
        }

        var start = Bound(slice.Start, true);
        var stop = Bound(slice.Stop, false);
        var count = step > 0
            ? stop < start ? 0 : ((stop - start) / step) + 1
            : stop > start ? 0 : ((start - stop) / -step) + 1;

        return (start, stop, step, count);
    }
}
