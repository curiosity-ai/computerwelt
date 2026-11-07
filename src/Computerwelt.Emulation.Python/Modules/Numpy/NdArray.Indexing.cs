using System.Globalization;
using System.Numerics;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules.Numpy;

/// <summary>Subscripting: basic indexing makes views, advanced indexing gathers.</summary>
internal sealed partial class NdArray
{
    public override PyObject GetItem(PyObject index) => NumpyModule.Guard("__getitem__", () => GetItemCore(index));

    public override void SetItem(PyObject index, PyObject value) =>
        NumpyModule.Guard("__setitem__", () =>
        {
            SetItemCore(index, value);
            return true;
        });

    private PyObject GetItemCore(PyObject index)
    {
        var plan = Plan(index);

        if (plan.View is { } view)
        {
            return view.NDim == 0 && !plan.KeepZeroD ? view.GetBoxed(view.Offset) : view;
        }

        var result = Zeros(plan.Shape, DType);

        for (var k = 0; k < plan.Offsets.Length; k++)
        {
            result.SetFrom(k, this, plan.Offsets[k]);
        }

        return NdConvert.Wrap(result);
    }

    private void SetItemCore(PyObject index, PyObject value)
    {
        var plan = Plan(index);
        var (offsets, shape) = plan.View is { } view ? (view.Offsets(), view.Shape) : (plan.Offsets, plan.Shape);
        Assign(offsets, shape, value);
    }

    /// <summary>Writes a value, broadcast to <paramref name="shape"/>, at the given positions.</summary>
    public void Assign(int[] offsets, int[] shape, PyObject value)
    {
        var source = NdConvert.ToArray(value);

        // A source that shares this buffer is read in full before anything is written, so
        // `a[1:] = a[:-1]` shifts rather than smears.
        if (ReferenceEquals(source.Buffer, Buffer))
        {
            source = source.Copy();
        }

        NdArray broadcast;

        try
        {
            broadcast = source.BroadcastTo(shape);
        }
        catch (PyRaise)
        {
            throw new PyRaise(PyErrors.ValueError(
                $"could not broadcast input array from shape {ShapeText(source.Shape)} into shape {ShapeText(shape)}"));
        }

        var from = broadcast.Offsets();

        for (var k = 0; k < offsets.Length; k++)
        {
            SetFrom(offsets[k], broadcast, from[k]);
        }
    }

    public override void DeleteItem(PyObject index) =>
        throw new PyRaise(PyErrors.ValueError("cannot delete array elements"));

    /// <summary>How a subscript resolves: a view, or a list of gathered positions.</summary>
    internal sealed record IndexPlan(NdArray? View, int[] Offsets, int[] Shape, bool KeepZeroD);

    private abstract record Item;

    private sealed record IntItem(long Value) : Item;

    private sealed record SliceItem(PySlice Slice) : Item;

    private sealed record NewAxisItem : Item;

    private sealed record EllipsisItem : Item;

    private sealed record ArrayItem(NdArray Indices) : Item;

    private sealed record MaskItem(NdArray Mask) : Item;

    private const string ValidIndices =
        "only integers, slices (`:`), ellipsis (`...`), numpy.newaxis (`None`) and integer or boolean arrays are valid indices";

    private static Item Classify(PyObject index)
    {
        switch (index)
        {
            case PyNone:
                return new NewAxisItem();
            case PyEllipsis:
                return new EllipsisItem();
            case PySlice slice:
                return new SliceItem(slice);
            case PyBool:
                throw new PyRaise(PyErrors.IndexError(ValidIndices));
            case PyInt integer:
                return integer.Value < long.MinValue || integer.Value > long.MaxValue
                    ? throw new PyRaise(PyErrors.IndexError("cannot fit 'int' into an index-sized integer"))
                    : new IntItem((long)integer.Value);
            case NdArray { NDim: 0, Kind: DKind.Int } scalar:
                return new IntItem(scalar.GetLong(scalar.Offset));
            case NdArray or PyList or PyTuple or PyRange:
            {
                var array = NdConvert.ToArray(index);

                if (array.Kind == DKind.Bool)
                {
                    return new MaskItem(array);
                }

                if (array.Kind == DKind.Int || (array.Size == 0 && array.Kind == DKind.Float))
                {
                    return new ArrayItem(array.Kind == DKind.Int ? array : array.AsType(NdDType.Int64));
                }

                throw new PyRaise(PyErrors.IndexError("arrays used as indices must be of integer (or boolean) type"));
            }

            default:
                throw new PyRaise(PyErrors.IndexError(ValidIndices));
        }
    }

    internal IndexPlan Plan(PyObject index)
    {
        var raw = index is PyTuple tuple and not PyNamedTuple ? tuple.Items : [index];
        var items = raw.Select(Classify).ToList();
        var keepZeroD = items.Any(static i => i is EllipsisItem);

        if (items.Count(static i => i is EllipsisItem) > 1)
        {
            throw new PyRaise(PyErrors.IndexError("an index can only have a single ellipsis ('...')"));
        }

        var consumed = items.Sum(static i => i switch
        {
            IntItem or SliceItem or ArrayItem => 1,
            MaskItem mask => mask.Mask.NDim,
            _ => 0,
        });

        if (consumed > NDim)
        {
            throw new PyRaise(PyErrors.IndexError(
                $"too many indices for array: array is {NDim.ToString(CultureInfo.InvariantCulture)}-dimensional, "
                + $"but {consumed.ToString(CultureInfo.InvariantCulture)} were indexed"));
        }

        // `...` stands for as many full slices as the other items leave over, and the dims
        // nothing names at the end are full slices too.
        var full = new SliceItem(new PySlice(null, null, null));
        var ellipsis = items.FindIndex(static i => i is EllipsisItem);

        if (ellipsis >= 0)
        {
            items.RemoveAt(ellipsis);
            items.InsertRange(ellipsis, Enumerable.Repeat<Item>(full, NDim - consumed));
        }
        else
        {
            items.AddRange(Enumerable.Repeat<Item>(full, NDim - consumed));
        }

        // A mask over k dimensions is the k index arrays of its true positions.
        var expanded = new List<Item>();
        var dimension = 0;

        foreach (var item in items)
        {
            if (item is MaskItem mask)
            {
                for (var d = 0; d < mask.Mask.NDim; d++)
                {
                    if (mask.Mask.Shape[d] != Shape[dimension + d])
                    {
                        throw new PyRaise(PyErrors.IndexError(
                            $"boolean index did not match indexed array along axis {(dimension + d).ToString(CultureInfo.InvariantCulture)}; "
                            + $"size of axis is {Shape[dimension + d].ToString(CultureInfo.InvariantCulture)} but size of corresponding "
                            + $"boolean axis is {mask.Mask.Shape[d].ToString(CultureInfo.InvariantCulture)}"));
                    }
                }

                foreach (var indices in NonZero(mask.Mask))
                {
                    expanded.Add(new ArrayItem(indices));
                }

                dimension += mask.Mask.NDim;
                continue;
            }

            expanded.Add(item);

            if (item is not NewAxisItem)
            {
                dimension++;
            }
        }

        return expanded.Any(static i => i is ArrayItem) ? Advanced(expanded) : new IndexPlan(Basic(expanded), [], [], keepZeroD);
    }

    /// <summary>The positions where a mask is true, one index array per dimension.</summary>
    public static NdArray[] NonZero(NdArray array)
    {
        var flags = array.ToBools();
        var count = flags.Count(static f => f);
        var nd = Math.Max(array.NDim, 1);
        var result = new long[nd][];

        for (var d = 0; d < nd; d++)
        {
            result[d] = new long[count];
        }

        var shape = array.NDim == 0 ? [1] : array.Shape;
        var index = new int[nd];
        var found = 0;

        for (var k = 0; k < flags.Length; k++)
        {
            if (flags[k])
            {
                for (var d = 0; d < nd; d++)
                {
                    result[d][found] = index[d];
                }

                found++;
            }

            for (var d = nd - 1; d >= 0; d--)
            {
                if (++index[d] < shape[d])
                {
                    break;
                }

                index[d] = 0;
            }
        }

        return [.. result.Select(static r => Vector(r))];
    }

    private static int Bound(long index, int size, int axis)
    {
        var resolved = index < 0 ? index + size : index;

        if (resolved < 0 || resolved >= size)
        {
            throw new PyRaise(PyErrors.IndexError(
                $"index {index.ToString(CultureInfo.InvariantCulture)} is out of bounds for axis "
                + $"{axis.ToString(CultureInfo.InvariantCulture)} with size {size.ToString(CultureInfo.InvariantCulture)}"));
        }

        return (int)resolved;
    }

    /// <summary>Resolves a slice against a length, as Python's <c>slice.indices</c> does.</summary>
    public static (int Start, int Step, int Count) ResolveSlice(PySlice slice, int length)
    {
        static long Read(PyObject? value, long fallback) => value switch
        {
            null or PyNone => fallback,
            PyInt integer => (long)BigInteger.Clamp(integer.Value, int.MinValue * 2L, int.MaxValue * 2L),
            NdArray { NDim: 0, Kind: DKind.Int } scalar => scalar.GetLong(scalar.Offset),
            _ => throw new PyRaise(PyErrors.TypeError(
                "slice indices must be integers or None or have an __index__ method")),
        };

        var step = Read(slice.Step, 1);

        if (step == 0)
        {
            throw new PyRaise(PyErrors.ValueError("slice step cannot be zero"));
        }

        long start;
        long stop;

        if (step > 0)
        {
            start = Read(slice.Start, 0);
            stop = Read(slice.Stop, length);
            start = start < 0 ? Math.Max(start + length, 0) : Math.Min(start, length);
            stop = stop < 0 ? Math.Max(stop + length, 0) : Math.Min(stop, length);
            return ((int)start, (int)step, (int)Math.Max(0, (stop - start + step - 1) / step));
        }

        start = Read(slice.Start, length - 1);
        stop = slice.Stop is null or PyNone ? -1 : Read(slice.Stop, -1);

        if (slice.Start is not (null or PyNone))
        {
            start = start < 0 ? Math.Max(start + length, -1) : Math.Min(start, length - 1);
        }

        if (slice.Stop is not (null or PyNone))
        {
            stop = stop < 0 ? Math.Max(stop + length, -1) : Math.Min(stop, length - 1);
        }

        return ((int)start, (int)step, (int)Math.Max(0, (start - stop - step - 1) / -step));
    }

    private NdArray Basic(List<Item> items)
    {
        var shape = new List<int>();
        var strides = new List<int>();
        var offset = Offset;
        var d = 0;

        foreach (var item in items)
        {
            switch (item)
            {
                case IntItem integer:
                    offset += Bound(integer.Value, Shape[d], d) * Strides[d];
                    d++;
                    break;

                case SliceItem slice:
                {
                    var (start, step, count) = ResolveSlice(slice.Slice, Shape[d]);

                    if (count > 0)
                    {
                        offset += start * Strides[d];
                    }

                    shape.Add(count);
                    strides.Add(Strides[d] * step);
                    d++;
                    break;
                }

                case NewAxisItem:
                    shape.Add(1);
                    strides.Add(0);
                    break;
            }
        }

        return View([.. shape], [.. strides], offset);
    }

    private IndexPlan Advanced(List<Item> items)
    {
        // Each output axis is either a basic one (a size and a stride) or the single composite
        // axis all the index arrays broadcast into.
        var basics = new List<(int Size, int Stride, int Position)>();
        var arrays = new List<(NdArray Indices, int Axis, int Position)>();
        var offset = Offset;
        var d = 0;

        for (var position = 0; position < items.Count; position++)
        {
            switch (items[position])
            {
                case IntItem integer:
                    arrays.Add((FromLongs([integer.Value], []), d, position));
                    d++;
                    break;

                case ArrayItem array:
                    arrays.Add((array.Indices, d, position));
                    d++;
                    break;

                case SliceItem slice:
                {
                    var (start, step, count) = ResolveSlice(slice.Slice, Shape[d]);

                    if (count > 0)
                    {
                        offset += start * Strides[d];
                    }

                    basics.Add((count, Strides[d] * step, position));
                    d++;
                    break;
                }

                case NewAxisItem:
                    basics.Add((1, 0, position));
                    break;
            }
        }

        int[] broadcastShape;

        try
        {
            broadcastShape = BroadcastShapes([.. arrays.Select(static a => a.Indices.Shape)]);
        }
        catch (PyRaise)
        {
            throw new PyRaise(PyErrors.IndexError(
                "shape mismatch: indexing arrays could not be broadcast together with shapes "
                + string.Join(" ", arrays.Select(static a => ShapeText(a.Indices.Shape)))));
        }

        var combined = CheckedSize(broadcastShape);
        var composite = new int[combined];

        foreach (var (indices, axis, _) in arrays)
        {
            var values = indices.BroadcastTo(broadcastShape).ToLongs();

            for (var j = 0; j < combined; j++)
            {
                composite[j] += Bound(values[j], Shape[axis], axis) * Strides[axis];
            }
        }

        // Adjacent index arrays put their dimensions where they stand; separated ones put
        // them first. That is numpy's rule, odd as it looks for `a[0, :, [1, 2]]`.
        var positions = arrays.Select(static a => a.Position).ToArray();
        var adjacent = positions[^1] - positions[0] == positions.Length - 1;
        var insertAt = adjacent ? basics.Count(b => b.Position < positions[0]) : 0;

        var axes = new List<(int Size, Func<int, int> Step)>();
        var shape = new List<int>();

        for (var i = 0; i <= basics.Count; i++)
        {
            if (i == insertAt)
            {
                axes.Add((combined, j => composite[j]));
                shape.AddRange(broadcastShape);
            }

            if (i < basics.Count)
            {
                var (size, stride, _) = basics[i];
                axes.Add((size, j => j * stride));
                shape.Add(size);
            }
        }

        var total = CheckedSize(shape);
        var current = total > 0 ? new[] { offset } : [];

        foreach (var (size, step) in axes)
        {
            var next = new int[current.Length * size];
            var k = 0;

            foreach (var start in current)
            {
                for (var j = 0; j < size; j++)
                {
                    next[k++] = start + step(j);
                }
            }

            current = next;
        }

        return new IndexPlan(null, current, [.. shape], false);
    }
}
