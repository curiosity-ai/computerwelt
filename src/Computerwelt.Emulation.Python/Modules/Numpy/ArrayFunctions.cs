using System.Globalization;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules.Numpy;

/// <summary>Shape manipulation, joining, splitting, sorting and searching.</summary>
internal static class ArrayFunctions
{
    /// <summary>Orders floats as numpy sorts them: NaN last.</summary>
    public static int CompareDoubles(double a, double b) =>
        double.IsNaN(a) ? (double.IsNaN(b) ? 0 : 1) : double.IsNaN(b) ? -1 : a.CompareTo(b);

    private static int CompareBoxed(PyObject a, PyObject b) =>
        Operators.Compare("<", a, b).IsTruthy() ? -1 : Operators.Compare("<", b, a).IsTruthy() ? 1 : 0;

    /// <summary>Applies a row function along an axis (null: the flattened array).</summary>
    public static NdArray AlongAxis(NdArray array, PyObject? axis, Func<NdArray, NdArray> row)
    {
        if (axis is null or PyNone)
        {
            return row(array.Reshape([-1]));
        }

        if (array.NDim == 0)
        {
            return row(array.Reshape([1])).Reshape([]);
        }

        var a = NdArray.NormalizeAxis(NdConvert.ToInt(axis), array.NDim);
        var permutation = Enumerable.Range(0, array.NDim).Where(d => d != a).Append(a).ToArray();
        var moved = array.Transpose(permutation).Contiguous();
        var length = array.Shape[a];
        var rows = length == 0 ? 0 : moved.Size / length;
        NdArray? result = null;

        for (var r = 0; r < rows; r++)
        {
            var output = row(moved.View([length], [1], moved.Offset + (r * length)));

            result ??= NdArray.Zeros([.. moved.Shape[..^1], output.Size], output.DType);

            var into = result.Offset + (r * output.Size);
            var from = output.Offsets();

            for (var j = 0; j < from.Length; j++)
            {
                result.SetFrom(into + j, output, from[j]);
            }
        }

        // No rows at all: the row function still decides the result's dtype.
        result ??= NdArray.Zeros([.. moved.Shape[..^1], length], row(NdArray.Zeros([0], array.DType)).DType);

        var inverse = new int[permutation.Length];

        for (var i = 0; i < permutation.Length; i++)
        {
            inverse[permutation[i]] = i;
        }

        return result.Transpose(inverse).Contiguous();
    }

    public static NdArray Sort(NdArray array, PyObject? axis) => AlongAxis(array, axis, static row =>
    {
        switch (row.Kind)
        {
            case DKind.Float:
            {
                var values = row.ToDoubles();
                Array.Sort(values, CompareDoubles);
                return NdArray.Vector(values);
            }

            case DKind.Int:
            {
                var values = row.ToLongs();
                Array.Sort(values);
                return NdArray.Vector(values);
            }

            case DKind.Bool:
            {
                var values = row.ToBools();
                Array.Sort(values);
                return NdArray.FromBools(values, [values.Length]);
            }

            default:
            {
                var values = row.ToBoxed().ToList();
                values.Sort(CompareBoxed);
                return NdArray.FromObjects(row.Kind, [.. values], [values.Count], row.DType.Width);
            }
        }
    });

    /// <summary>A stable argsort of one row.</summary>
    public static long[] ArgSortRow(NdArray row)
    {
        var indices = Enumerable.Range(0, row.Size).ToArray();

        switch (row.Kind)
        {
            case DKind.Float:
            {
                var values = row.ToDoubles();
                return [.. indices.OrderBy(i => values[i], Comparer<double>.Create(CompareDoubles)).Select(static i => (long)i)];
            }

            case DKind.Int or DKind.Bool:
            {
                var values = row.ToLongs();
                return [.. indices.OrderBy(i => values[i]).Select(static i => (long)i)];
            }

            default:
            {
                var values = row.ToBoxed();
                return [.. indices.OrderBy(i => values[i], Comparer<PyObject>.Create(CompareBoxed)).Select(static i => (long)i)];
            }
        }
    }

    public static NdArray ArgSort(NdArray array, PyObject? axis) =>
        AlongAxis(array, axis, static row => NdArray.Vector(ArgSortRow(row)));

    public static PyObject Squeeze(NdArray array, PyObject? axis)
    {
        var axes = NdConvert.ToAxes(axis, array.NDim);

        if (axes is not null)
        {
            foreach (var a in axes)
            {
                if (array.Shape[a] != 1)
                {
                    throw new PyRaise(PyErrors.ValueError("cannot select an axis to squeeze out which has size not equal to one"));
                }
            }
        }

        var keep = Enumerable.Range(0, array.NDim).Where(d => axes is null ? array.Shape[d] != 1 : !axes.Contains(d)).ToArray();
        return array.View([.. keep.Select(d => array.Shape[d])], [.. keep.Select(d => array.Strides[d])], array.Offset);
    }

    public static NdArray ExpandDims(NdArray array, PyObject axis)
    {
        var requested = axis is PyTuple or PyList ? NdConvert.ToShape(axis) : [NdConvert.ToInt(axis)];
        var nd = array.NDim + requested.Length;
        var positions = requested.Select(a => NdArray.NormalizeAxis(a, nd)).ToHashSet();
        var shape = new int[nd];
        var strides = new int[nd];
        var source = 0;

        for (var d = 0; d < nd; d++)
        {
            if (positions.Contains(d))
            {
                shape[d] = 1;
                strides[d] = 0;
            }
            else
            {
                shape[d] = array.Shape[source];
                strides[d] = array.Strides[source];
                source++;
            }
        }

        return array.View(shape, strides, array.Offset);
    }

    public static NdArray Diagonal(NdArray array, int offset)
    {
        if (array.NDim < 2)
        {
            throw new PyRaise(PyErrors.ValueError("diag requires an array of at least two dimensions"));
        }

        var rows = array.Shape[0];
        var columns = array.Shape[1];
        var (row0, column0) = offset >= 0 ? (0, offset) : (-offset, 0);
        var length = Math.Max(0, Math.Min(rows - row0, columns - column0));
        var start = array.Offset + (row0 * array.Strides[0]) + (column0 * array.Strides[1]);

        // A view, but numpy's is read-only; a copy keeps writes from surprising anyone.
        return array.View([.. array.Shape[2..], length], [.. array.Strides[2..], array.Strides[0] + array.Strides[1]], start).Copy();
    }

    /// <summary>Reads a sequence of arrays (the argument of <c>concatenate</c>, <c>stack</c> …).</summary>
    public static List<NdArray> Arrays(PyObject value, string function)
    {
        if (value is NdArray array)
        {
            return array.NDim == 0
                ? throw new PyRaise(PyErrors.ValueError("zero-dimensional arrays cannot be concatenated"))
                : [.. array.Iterate()!.Select(static row => NdConvert.ToArray(row))];
        }

        if (value is not (PyList or PyTuple))
        {
            throw new PyRaise(PyErrors.TypeError($"{function}() argument must be a sequence of arrays"));
        }

        var items = VirtualMachine.RequireIterable(value).Select(static v => NdConvert.ToArray(v)).ToList();

        return items.Count == 0
            ? throw new PyRaise(PyErrors.ValueError("need at least one array to concatenate"))
            : items;
    }

    public static NdDType CommonDType(IEnumerable<NdArray> arrays)
    {
        var list = arrays.ToList();

        if (list.All(static a => a.Kind == DKind.Str))
        {
            return NdDType.Str(list.Max(static a => a.DType.Width));
        }

        var kind = list.Select(static a => a.Kind).Aggregate(NdDType.Promote);
        return kind == DKind.Str ? NdDType.Object : NdDType.Of(kind);
    }

    public static NdArray Concatenate(List<NdArray> arrays, PyObject? axisArgument)
    {
        if (axisArgument is PyNone)
        {
            arrays = [.. arrays.Select(static a => a.Reshape([-1]))];
            axisArgument = PyInt.From(0);
        }

        var first = arrays[0];

        if (first.NDim == 0)
        {
            throw new PyRaise(PyErrors.ValueError("zero-dimensional arrays cannot be concatenated"));
        }

        var axis = NdArray.NormalizeAxis(axisArgument is null ? 0 : NdConvert.ToInt(axisArgument), first.NDim);

        for (var i = 1; i < arrays.Count; i++)
        {
            var other = arrays[i];

            if (other.NDim != first.NDim)
            {
                throw new PyRaise(PyErrors.ValueError(
                    "all the input array dimensions except for the concatenation axis must match exactly, but along "
                    + $"dimension 0, the array at index 0 has {first.NDim} dimension(s) and the array at index {i} has {other.NDim} dimension(s)"));
            }

            for (var d = 0; d < first.NDim; d++)
            {
                if (d != axis && other.Shape[d] != first.Shape[d])
                {
                    throw new PyRaise(PyErrors.ValueError(
                        "all the input array dimensions except for the concatenation axis must match exactly, but along "
                        + $"dimension {d}, the array at index 0 has size {first.Shape[d]} and the array at index {i} has size {other.Shape[d]}"));
                }
            }
        }

        var shape = (int[])first.Shape.Clone();
        shape[axis] = arrays.Sum(a => a.Shape[axis]);
        var result = NdArray.Zeros(shape, CommonDType(arrays));
        var position = 0;

        foreach (var array in arrays)
        {
            var length = array.Shape[axis];
            var window = result.View(
                [.. array.Shape],
                result.Strides,
                result.Offset + (position * result.Strides[axis]));
            window.Assign(window.Offsets(), window.Shape, array);
            position += length;
        }

        return result;
    }

    public static NdArray Stack(List<NdArray> arrays, PyObject? axisArgument)
    {
        var shape = arrays[0].Shape;

        if (arrays.Any(a => !a.Shape.SequenceEqual(shape)))
        {
            throw new PyRaise(PyErrors.ValueError("all input arrays must have the same shape"));
        }

        var axis = NdArray.NormalizeAxis(axisArgument is null or PyNone ? 0 : NdConvert.ToInt(axisArgument), shape.Length + 1);
        var expanded = arrays.Select(a => ExpandDims(a, PyInt.From(axis))).ToList();
        return Concatenate(expanded, PyInt.From(axis));
    }

    public static NdArray AtLeast(NdArray array, int dimensions) => dimensions switch
    {
        1 when array.NDim == 0 => array.Reshape([1]),
        2 when array.NDim == 0 => array.Reshape([1, 1]),
        2 when array.NDim == 1 => array.Reshape([1, array.Shape[0]]),
        3 when array.NDim == 0 => array.Reshape([1, 1, 1]),
        3 when array.NDim == 1 => array.Reshape([1, array.Shape[0], 1]),
        3 when array.NDim == 2 => array.Reshape([.. array.Shape, 1]),
        _ => array,
    };

    /// <summary><c>split</c> / <c>array_split</c>.</summary>
    public static PyList Split(NdArray array, PyObject sections, PyObject? axisArgument, bool exact)
    {
        var axis = NdArray.NormalizeAxis(axisArgument is null or PyNone ? 0 : NdConvert.ToInt(axisArgument), array.NDim);
        var length = array.Shape[axis];
        var bounds = new List<int> { 0 };

        if (sections is PyInt or NdArray { NDim: 0 })
        {
            var count = NdConvert.ToInt(sections);

            if (count <= 0)
            {
                throw new PyRaise(PyErrors.ValueError("number sections must be larger than 0."));
            }

            if (exact && length % count != 0)
            {
                throw new PyRaise(PyErrors.ValueError("array split does not result in an equal division"));
            }

            var each = length / count;
            var extra = length % count;

            for (var i = 0; i < count; i++)
            {
                bounds.Add(bounds[^1] + each + (i < extra ? 1 : 0));
            }
        }
        else
        {
            foreach (var index in NdConvert.ToShape(sections))
            {
                bounds.Add(Math.Clamp(index < 0 ? index + length : index, 0, length));
            }

            bounds.Add(length);
        }

        var parts = new List<PyObject>();

        for (var i = 0; i + 1 < bounds.Count; i++)
        {
            var start = bounds[i];
            var stop = Math.Max(bounds[i + 1], start);
            var shape = (int[])array.Shape.Clone();
            shape[axis] = stop - start;
            parts.Add(array.View(shape, array.Strides, array.Offset + (start * array.Strides[axis])));
        }

        return new PyList(parts);
    }

    public static NdArray Flip(NdArray array, PyObject? axis)
    {
        var axes = NdConvert.ToAxes(axis, array.NDim) ?? [.. Enumerable.Range(0, array.NDim)];
        var strides = (int[])array.Strides.Clone();
        var offset = array.Offset;

        foreach (var a in axes)
        {
            if (array.Shape[a] > 0)
            {
                offset += (array.Shape[a] - 1) * strides[a];
            }

            strides[a] = -strides[a];
        }

        return array.View((int[])array.Shape.Clone(), strides, offset);
    }

    public static NdArray Roll(NdArray array, PyObject shift, PyObject? axis)
    {
        if (axis is null or PyNone)
        {
            return Roll(array.Reshape([-1]), shift, PyInt.From(0)).Reshape(array.Shape);
        }

        var shifts = NdConvert.ToShape(shift);
        var axes = NdConvert.ToShape(axis);

        if (shifts.Length == 1 && axes.Length > 1)
        {
            shifts = [.. Enumerable.Repeat(shifts[0], axes.Length)];
        }

        var result = array;

        for (var i = 0; i < axes.Length; i++)
        {
            var a = NdArray.NormalizeAxis(axes[i], array.NDim);
            var n = result.Shape[a];

            if (n == 0)
            {
                continue;
            }

            var s = ((shifts[i] % n) + n) % n;
            var indices = NdArray.Vector([.. Enumerable.Range(0, n).Select(j => (long)(((j - s) % n + n) % n))]);
            result = Take(result, indices, a);
        }

        return result.Contiguous();
    }

    /// <summary><c>np.take(a, indices, axis)</c>.</summary>
    public static NdArray Take(NdArray array, NdArray indices, int? axis)
    {
        if (axis is null)
        {
            var flat = array.Reshape([-1]);
            return NdConvert.ToArray(flat.GetItem(indices));
        }

        var items = new List<PyObject>();

        for (var d = 0; d < axis; d++)
        {
            items.Add(new PySlice(null, null, null));
        }

        items.Add(indices);
        return NdConvert.ToArray(array.GetItem(new PyTuple(items)));
    }

    public static NdArray Repeat(NdArray array, PyObject repeats, PyObject? axisArgument)
    {
        var source = axisArgument is null or PyNone ? array.Reshape([-1]) : array;
        var axis = axisArgument is null or PyNone ? 0 : NdArray.NormalizeAxis(NdConvert.ToInt(axisArgument), array.NDim);
        var length = source.Shape[axis];
        var counts = NdConvert.ToArray(repeats).ToLongs();

        if (counts.Length == 1)
        {
            counts = [.. Enumerable.Repeat(counts[0], length)];
        }

        if (counts.Length != length)
        {
            throw new PyRaise(PyErrors.ValueError(
                $"operands could not be broadcast together with shape ({length},) ({counts.Length},)"));
        }

        if (counts.Any(static c => c < 0))
        {
            throw new PyRaise(PyErrors.ValueError("repeats may not contain negative values."));
        }

        var total = counts.Sum();

        if (total > NdArray.MaxElements)
        {
            NdArray.CheckedSize([(int)Math.Min(total, int.MaxValue)]);
        }

        var indices = new long[total];
        var k = 0;

        for (var i = 0; i < length; i++)
        {
            for (var j = 0; j < counts[i]; j++)
            {
                indices[k++] = i;
            }
        }

        return Take(source, NdArray.Vector(indices), axis);
    }

    public static NdArray Tile(NdArray array, int[] reps)
    {
        var nd = Math.Max(array.NDim, reps.Length);

        if (reps.Any(static r => r < 0))
        {
            throw new PyRaise(PyErrors.ValueError("negative dimensions are not allowed"));
        }

        var padded = Enumerable.Repeat(1, nd - array.NDim).Concat(array.Shape).ToArray();
        var repeated = Enumerable.Repeat(1, nd - reps.Length).Concat(reps).ToArray();
        NdArray.CheckedSize([.. padded.Select((size, d) => (int)Math.Min((long)size * repeated[d], int.MaxValue))]);
        var source = array.Reshape([.. Enumerable.Repeat(1, nd - array.NDim), .. array.Shape]);
        var full = Enumerable.Repeat(1, nd - reps.Length).Concat(reps).ToArray();
        var result = source;

        for (var d = 0; d < nd; d++)
        {
            if (full[d] != 1)
            {
                var n = result.Shape[d];
                var indices = NdArray.Vector([.. Enumerable.Range(0, n * full[d]).Select(j => (long)(n == 0 ? 0 : j % n))]);
                result = n == 0 ? result : Take(result, indices, d);
            }
        }

        return result.Copy();
    }

    public static NdArray Diff(NdArray array, int n, int axisArgument)
    {
        var result = array;

        for (var i = 0; i < n; i++)
        {
            var axis = NdArray.NormalizeAxis(axisArgument, result.NDim);
            var length = result.Shape[axis];
            var upper = Slice(result, axis, 1, length);
            var lower = Slice(result, axis, 0, Math.Max(length - 1, 0));
            result = result.Kind == DKind.Bool
                ? Ufuncs.BinaryArray("not_equal", upper, lower)
                : Ufuncs.BinaryArray("subtract", upper, lower);
        }

        return result;
    }

    public static NdArray Slice(NdArray array, int axis, int start, int stop)
    {
        var shape = (int[])array.Shape.Clone();
        shape[axis] = Math.Max(0, stop - start);
        return array.View(shape, array.Strides, array.Offset + (shape[axis] > 0 ? start * array.Strides[axis] : 0));
    }

    /// <summary><c>np.searchsorted(a, v, side)</c> over a sorted 1-D array.</summary>
    public static PyObject SearchSorted(NdArray sorted, PyObject values, bool right)
    {
        var a = sorted.Reshape([-1]).ToDoubles();
        var v = NdConvert.ToArray(values);
        var result = v.ToDoubles().Select(x => (long)Bisect(a, x, right)).ToArray();
        return NdConvert.Wrap(NdArray.FromLongs(result, (int[])v.Shape.Clone()));
    }

    public static int Bisect(double[] sorted, double value, bool right)
    {
        var low = 0;
        var high = sorted.Length;

        while (low < high)
        {
            var middle = (low + high) >> 1;
            var order = CompareDoubles(sorted[middle], value);

            if (right ? order <= 0 : order < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    /// <summary><c>np.unique</c> over the flattened array.</summary>
    public static PyObject Unique(NdArray array, bool returnIndex, bool returnInverse, bool returnCounts)
    {
        var flat = array.Reshape([-1]);
        var order = ArgSortRow(flat);
        var values = flat.ToBoxed();
        var uniqueIndex = new List<long>();
        var counts = new List<long>();
        var inverse = new long[values.Length];

        for (var i = 0; i < order.Length; i++)
        {
            var at = (int)order[i];
            var isNew = i == 0 || !SameValue(values[(int)order[i - 1]], values[at]);

            if (isNew)
            {
                uniqueIndex.Add(at);
                counts.Add(0);
            }

            counts[^1]++;
            inverse[at] = uniqueIndex.Count - 1;
        }

        var unique = Take(flat, NdArray.Vector([.. uniqueIndex]), null);

        if (!returnIndex && !returnInverse && !returnCounts)
        {
            return unique;
        }

        var parts = new List<PyObject> { unique };

        if (returnIndex)
        {
            parts.Add(NdArray.Vector([.. uniqueIndex]));
        }

        if (returnInverse)
        {
            parts.Add(NdArray.Vector(inverse));
        }

        if (returnCounts)
        {
            parts.Add(NdArray.Vector([.. counts]));
        }

        return new PyTuple(parts);
    }

    private static bool SameValue(PyObject a, PyObject b) =>
        (a is PyFloat x && b is PyFloat y && double.IsNaN(x.Value) && double.IsNaN(y.Value)) || PyObject.SameOrEqual(a, b);

    public static NdArray Bincount(NdArray x, PyObject? weights, int minLength)
    {
        if (x.NDim != 1)
        {
            throw new PyRaise(PyErrors.ValueError("object too deep for desired array"));
        }

        if (x.Kind is not (DKind.Int or DKind.Bool))
        {
            throw new PyRaise(PyErrors.TypeError(
                "Cannot cast array data from dtype('float64') to dtype('int64') according to the rule 'safe'"));
        }

        var values = x.ToLongs();

        if (values.Any(static v => v < 0))
        {
            throw new PyRaise(PyErrors.ValueError("'list' argument must have no negative elements"));
        }

        var wanted = Math.Max(values.Length == 0 ? 0 : values.Max() + 1, minLength);
        NdArray.CheckedSize([(int)Math.Min(wanted, int.MaxValue)]);
        var length = (int)wanted;

        if (weights is not null)
        {
            var w = NdConvert.ToArray(weights).ToDoubles();
            var sums = new double[length];

            for (var i = 0; i < values.Length; i++)
            {
                sums[values[i]] += w[i];
            }

            return NdArray.Vector(sums);
        }

        var counts = new long[length];

        foreach (var v in values)
        {
            counts[v]++;
        }

        return NdArray.Vector(counts);
    }

    /// <summary><c>np.histogram(a, bins, range)</c>: equal-width bins, the last one closed.</summary>
    public static PyTuple Histogram(NdArray array, PyObject? bins, PyObject? range, bool density)
    {
        var values = array.ToDoubles();
        double[] edges;

        if (bins is null or PyInt)
        {
            var count = bins is null ? 10 : NdConvert.ToInt(bins);

            if (count < 1)
            {
                throw new PyRaise(PyErrors.ValueError("`bins` must be positive, when an integer"));
            }

            NdArray.CheckedSize([count]);

            double low;
            double high;

            if (range is not null and not PyNone)
            {
                var pair = NdConvert.ToArray(range).ToDoubles();
                (low, high) = (pair[0], pair[1]);

                if (low > high)
                {
                    throw new PyRaise(PyErrors.ValueError("max must be larger than min in range parameter."));
                }
            }
            else if (values.Length == 0)
            {
                (low, high) = (0, 1);
            }
            else
            {
                (low, high) = (values.Min(), values.Max());

                if (!double.IsFinite(low) || !double.IsFinite(high))
                {
                    throw new PyRaise(PyErrors.ValueError($"autodetected range of [{low}, {high}] is not finite"));
                }
            }

            if (low == high)
            {
                (low, high) = (low - 0.5, high + 0.5);
            }

            edges = Linspace(low, high, count + 1, endpoint: true);
        }
        else
        {
            edges = NdConvert.ToArray(bins).ToDoubles();
        }

        var bucketCount = edges.Length - 1;
        var counts = new long[bucketCount];
        var (first, last) = (edges[0], edges[^1]);

        foreach (var v in values)
        {
            if (double.IsNaN(v) || v < first || v > last)
            {
                continue;
            }

            var bucket = v == last ? bucketCount - 1 : Bisect(edges, v, right: true) - 1;
            counts[Math.Clamp(bucket, 0, bucketCount - 1)]++;
        }

        PyObject hist = NdArray.Vector(counts);

        if (density)
        {
            var total = (double)counts.Sum();
            hist = NdArray.Vector([.. counts.Select((c, i) => c / total / (edges[i + 1] - edges[i]))]);
        }

        return new PyTuple([hist, NdArray.Vector(edges)]);
    }

    public static double[] Linspace(double start, double stop, int num, bool endpoint)
    {
        var result = new double[num];
        var divisor = endpoint ? num - 1 : num;
        var delta = stop - start;
        var step = divisor > 0 ? delta / divisor : double.NaN;

        for (var i = 0; i < num; i++)
        {
            result[i] = step == 0 ? (i / (double)divisor * delta) + start : (i * step) + start;
        }

        if (endpoint && num > 1)
        {
            result[^1] = stop;
        }

        return result;
    }

    public static NdArray Outer(NdArray a, NdArray b) =>
        Ufuncs.BinaryArray("multiply", a.Reshape([-1, 1]), b.Reshape([1, -1]));

    /// <summary><c>np.isclose</c> with numpy's asymmetric tolerance.</summary>
    public static NdArray IsClose(NdArray a, NdArray b, double rtol, double atol, bool equalNaN)
    {
        var shape = NdArray.BroadcastShapes(a.Shape, b.Shape);
        var x = a.BroadcastTo(shape).ToDoubles();
        var y = b.BroadcastTo(shape).ToDoubles();
        var result = new bool[x.Length];

        for (var i = 0; i < x.Length; i++)
        {
            result[i] = double.IsNaN(x[i]) || double.IsNaN(y[i])
                ? equalNaN && double.IsNaN(x[i]) && double.IsNaN(y[i])
                : x[i] == y[i] || Math.Abs(x[i] - y[i]) <= atol + (rtol * Math.Abs(y[i]));
        }

        return NdArray.FromBools(result, shape);
    }

    public static bool ArrayEqual(PyObject left, PyObject right, bool equalNaN)
    {
        var a = NdConvert.ToArray(left);
        var b = NdConvert.ToArray(right);

        if (!a.Shape.SequenceEqual(b.Shape))
        {
            return false;
        }

        var equal = Ufuncs.BinaryArray("equal", a, b).ToBools();

        if (!equalNaN || a.Kind != DKind.Float && b.Kind != DKind.Float)
        {
            return equal.All(static e => e);
        }

        var x = a.ToDoubles();
        var y = b.ToDoubles();
        return equal.Select((e, i) => e || (double.IsNaN(x[i]) && double.IsNaN(y[i]))).All(static e => e);
    }

    /// <summary><c>np.interp</c>: piecewise-linear interpolation over increasing <paramref name="xp"/>.</summary>
    public static PyObject Interp(PyObject x, NdArray xp, NdArray fp, double? left, double? right)
    {
        var xs = xp.ToDoubles();
        var fs = fp.ToDoubles();

        if (xs.Length != fs.Length)
        {
            throw new PyRaise(PyErrors.ValueError("fp and xp are not of the same length."));
        }

        if (xs.Length == 0)
        {
            throw new PyRaise(PyErrors.ValueError("array of sample points is empty"));
        }

        var points = NdConvert.ToArray(x);
        var values = points.ToDoubles();
        var result = new double[values.Length];

        for (var i = 0; i < values.Length; i++)
        {
            var v = values[i];

            if (double.IsNaN(v))
            {
                result[i] = v;
            }
            else if (v < xs[0])
            {
                result[i] = left ?? fs[0];
            }
            else if (v > xs[^1])
            {
                result[i] = right ?? fs[^1];
            }
            else if (v == xs[^1])
            {
                result[i] = fs[^1];
            }
            else
            {
                var j = Bisect(xs, v, right: true) - 1;
                var slope = (fs[j + 1] - fs[j]) / (xs[j + 1] - xs[j]);
                result[i] = (slope * (v - xs[j])) + fs[j];

                // numpy falls back to the other endpoint when the slope is not finite.
                if (double.IsNaN(result[i]))
                {
                    result[i] = (slope * (v - xs[j + 1])) + fs[j + 1];

                    if (double.IsNaN(result[i]) && fs[j] == fs[j + 1])
                    {
                        result[i] = fs[j];
                    }
                }
            }
        }

        return NdConvert.Wrap(NdArray.FromDoubles(result, (int[])points.Shape.Clone()));
    }

    /// <summary><c>np.cov</c>: rows are variables (unless <paramref name="rowVar"/> is false).</summary>
    public static PyObject Cov(NdArray m, NdArray? y, bool rowVar, int ddof)
    {
        var x = AtLeast(m.AsType(NdDType.Float64), 2);

        if (!rowVar && x.Shape[0] != 1)
        {
            x = x.Transpose();
        }

        if (y is not null)
        {
            var other = AtLeast(y.AsType(NdDType.Float64), 2);

            if (!rowVar && other.Shape[0] != 1)
            {
                other = other.Transpose();
            }

            x = Concatenate([x, other], PyInt.From(0));
        }

        var variables = x.Shape[0];
        var observations = x.Shape[1];
        NdArray.CheckWork((double)variables * variables * observations, "cov");
        var data = x.Contiguous().ToDoubles();
        var means = new double[variables];

        for (var i = 0; i < variables; i++)
        {
            means[i] = Reductions.Mean(data.AsSpan(i * observations, observations).ToArray(), true);
        }

        var result = new double[variables * variables];
        var factor = observations - ddof;

        for (var i = 0; i < variables; i++)
        {
            for (var j = 0; j < variables; j++)
            {
                var total = 0.0;

                for (var k = 0; k < observations; k++)
                {
                    total += (data[(i * observations) + k] - means[i]) * (data[(j * observations) + k] - means[j]);
                }

                result[(i * variables) + j] = total / factor;
            }
        }

        var matrix = NdArray.FromDoubles(result, [variables, variables]);
        return variables == 1 && y is null && m.NDim <= 1 ? new PyFloat(result[0]) : matrix;
    }

    public static PyObject CorrCoef(NdArray x, NdArray? y, bool rowVar)
    {
        var covariance = Cov(x, y, rowVar, 1);

        if (covariance is PyFloat single)
        {
            return new PyFloat(single.Value / single.Value);
        }

        var c = (NdArray)covariance;
        var n = c.Shape[0];
        var values = c.ToDoubles();
        var result = new double[values.Length];

        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++)
            {
                var r = values[(i * n) + j] / Math.Sqrt(values[(i * n) + i]) / Math.Sqrt(values[(j * n) + j]);
                result[(i * n) + j] = Math.Clamp(r, -1, 1);
            }
        }

        return NdArray.FromDoubles(result, [n, n]);
    }

    public static string FormatInt(int value) => value.ToString(CultureInfo.InvariantCulture);
}
