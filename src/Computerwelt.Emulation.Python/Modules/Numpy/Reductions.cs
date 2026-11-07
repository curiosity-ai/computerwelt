using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules.Numpy;

/// <summary>
/// The reductions — <c>sum</c>, <c>mean</c>, <c>std</c>, <c>min</c>, <c>argmax</c>,
/// <c>median</c>, <c>percentile</c> … — over all axes or the ones named.
/// </summary>
/// <remarks>
/// A float sum uses numpy's pairwise summation (eight accumulators, blocks of 128) when the
/// elements being summed are contiguous, and a running sum otherwise, which is what numpy's
/// own loops do — so <c>np.sum([0.1] * 10)</c> is <c>1.0</c> here as there, where Python's
/// <c>sum</c> gives <c>0.9999999999999999</c>.
/// </remarks>
internal static class Reductions
{
    /// <summary>The elements regrouped so each output element's inputs are one contiguous run.</summary>
    internal sealed record Groups(NdArray Data, int[] Shape, int[] KeptShape, int GroupSize, int Count, bool Contiguous);

    public static Groups Group(NdArray array, int[]? axes)
    {
        var reduced = axes ?? [.. Enumerable.Range(0, array.NDim)];
        var kept = Enumerable.Range(0, array.NDim).Where(d => !reduced.Contains(d)).ToArray();
        var ordered = reduced.Order().ToArray();
        var permutation = kept.Concat(ordered).ToArray();
        var data = array.Transpose(permutation).Contiguous();
        var groupSize = ordered.Aggregate(1, (size, d) => size * array.Shape[d]);
        var shape = kept.Select(d => array.Shape[d]).ToArray();
        var keptShape = array.Shape.Select((size, d) => reduced.Contains(d) ? 1 : size).ToArray();

        // The reduced axes being the trailing ones means each run was contiguous to begin with.
        var trailing = ordered.Length == 0 || ordered.SequenceEqual(Enumerable.Range(array.NDim - ordered.Length, ordered.Length));
        var count = shape.Aggregate(1, static (a, b) => a * b);
        return new Groups(data, shape, keptShape, groupSize, count, trailing);
    }

    private static PyObject Finish(NdArray result, Groups groups, bool keepDims) =>
        NdConvert.Wrap(keepDims ? result.Reshape(groups.KeptShape) : result);

    /// <summary>numpy's <c>pairwise_sum</c>.</summary>
    public static double PairwiseSum(ReadOnlySpan<double> values)
    {
        var n = values.Length;

        if (n < 8)
        {
            var result = 0.0;

            for (var i = 0; i < n; i++)
            {
                result += values[i];
            }

            return result;
        }

        if (n <= 128)
        {
            Span<double> r = stackalloc double[8];
            values[..8].CopyTo(r);
            int i;

            for (i = 8; i < n - (n % 8); i += 8)
            {
                for (var j = 0; j < 8; j++)
                {
                    r[j] += values[i + j];
                }
            }

            var result = ((r[0] + r[1]) + (r[2] + r[3])) + ((r[4] + r[5]) + (r[6] + r[7]));

            for (; i < n; i++)
            {
                result += values[i];
            }

            return result;
        }

        var half = n / 2;
        half -= half % 8;
        return PairwiseSum(values[..half]) + PairwiseSum(values[half..]);
    }

    private static double Sum(ReadOnlySpan<double> values, bool pairwise)
    {
        if (pairwise)
        {
            return PairwiseSum(values);
        }

        var total = 0.0;

        foreach (var v in values)
        {
            total += v;
        }

        return total;
    }

    public static PyObject Sum(NdArray array, int[]? axes, bool keepDims, NdDType? dtype = null)
    {
        var groups = Group(array, axes);
        var kind = dtype?.Kind ?? (array.Kind == DKind.Bool ? DKind.Int : array.Kind);
        var g = groups.GroupSize;

        switch (kind)
        {
            case DKind.Int or DKind.Bool:
            {
                var values = groups.Data.ToLongs();
                var result = new long[groups.Count];

                unchecked
                {
                    for (var k = 0; k < result.Length; k++)
                    {
                        long total = 0;

                        for (var j = 0; j < g; j++)
                        {
                            total += values[(k * g) + j];
                        }

                        result[k] = total;
                    }
                }

                return Finish(NdArray.FromLongs(result, groups.Shape), groups, keepDims);
            }

            case DKind.Float:
            {
                var values = groups.Data.ToDoubles();
                var result = new double[groups.Count];

                for (var k = 0; k < result.Length; k++)
                {
                    result[k] = Sum(values.AsSpan(k * g, g), groups.Contiguous);
                }

                return Finish(NdArray.FromDoubles(result, groups.Shape), groups, keepDims);
            }

            default:
            {
                var values = groups.Data.ToBoxed();
                var result = new PyObject[groups.Count];

                for (var k = 0; k < result.Length; k++)
                {
                    PyObject total = PyInt.From(0);

                    for (var j = 0; j < g; j++)
                    {
                        total = Operators.Binary("+", total, values[(k * g) + j]);
                    }

                    result[k] = total;
                }

                return Finish(NdArray.FromObjects(DKind.Object, result, groups.Shape), groups, keepDims);
            }
        }
    }

    public static PyObject Prod(NdArray array, int[]? axes, bool keepDims)
    {
        var groups = Group(array, axes);
        var g = groups.GroupSize;

        if (array.Kind is DKind.Int or DKind.Bool)
        {
            var values = groups.Data.ToLongs();
            var result = new long[groups.Count];

            unchecked
            {
                for (var k = 0; k < result.Length; k++)
                {
                    long total = 1;

                    for (var j = 0; j < g; j++)
                    {
                        total *= values[(k * g) + j];
                    }

                    result[k] = total;
                }
            }

            return Finish(NdArray.FromLongs(result, groups.Shape), groups, keepDims);
        }

        var floats = groups.Data.ToDoubles();
        var products = new double[groups.Count];

        for (var k = 0; k < products.Length; k++)
        {
            var total = 1.0;

            for (var j = 0; j < g; j++)
            {
                total *= floats[(k * g) + j];
            }

            products[k] = total;
        }

        return Finish(NdArray.FromDoubles(products, groups.Shape), groups, keepDims);
    }

    /// <summary>Applies a function to each group's floats, giving one float per group.</summary>
    public static PyObject MapFloat(NdArray array, int[]? axes, bool keepDims, Func<double[], bool, double> reduce)
    {
        RequireNumeric(array);
        var groups = Group(array, axes);
        var values = groups.Data.ToDoubles();
        var g = groups.GroupSize;
        var result = new double[groups.Count];

        for (var k = 0; k < result.Length; k++)
        {
            result[k] = reduce(values.AsSpan(k * g, g).ToArray(), groups.Contiguous);
        }

        return Finish(NdArray.FromDoubles(result, groups.Shape), groups, keepDims);
    }

    private static void RequireNumeric(NdArray array)
    {
        if (array.Kind is DKind.Str)
        {
            throw new PyRaise(PyErrors.TypeError(
                "the resolved dtypes are not compatible with add.reduce. Resolved (dtype('<U'), dtype('<U'), dtype('<U'))"));
        }
    }

    public static double Mean(double[] values, bool pairwise) =>
        Sum(values, pairwise) / values.Length;

    public static double Variance(double[] values, bool pairwise, int ddof)
    {
        var mean = Mean(values, pairwise);
        var squares = new double[values.Length];

        for (var i = 0; i < values.Length; i++)
        {
            var deviation = values[i] - mean;
            squares[i] = deviation * deviation;
        }

        return Sum(squares, pairwise) / Math.Max(values.Length - ddof, 0);
    }

    public static double Median(double[] values)
    {
        if (values.Length == 0 || values.Any(double.IsNaN))
        {
            return double.NaN;
        }

        var sorted = values.Order().ToArray();
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    /// <summary>numpy's default <c>linear</c> quantile, with its <c>_lerp</c>.</summary>
    public static double Quantile(double[] sortedValues, double q)
    {
        var n = sortedValues.Length;

        if (n == 0)
        {
            return double.NaN;
        }

        if (double.IsNaN(sortedValues[^1]))
        {
            return double.NaN;
        }

        var position = q * (n - 1);
        var below = (int)Math.Floor(position);
        var above = Math.Min(below + 1, n - 1);
        var t = position - below;
        var a = sortedValues[below];
        var b = sortedValues[above];
        var difference = b - a;
        return t >= 0.5 ? b - (difference * (1 - t)) : a + (difference * t);
    }

    public static PyObject Extreme(NdArray array, int[]? axes, bool keepDims, bool smallest, bool ignoreNaN = false)
    {
        var groups = Group(array, axes);
        var g = groups.GroupSize;
        var name = smallest ? "minimum" : "maximum";

        if (g == 0)
        {
            throw new PyRaise(PyErrors.ValueError($"zero-size array to reduction operation {name} which has no identity"));
        }

        switch (array.Kind)
        {
            case DKind.Float:
            {
                var values = groups.Data.ToDoubles();
                var result = new double[groups.Count];

                for (var k = 0; k < result.Length; k++)
                {
                    var best = double.NaN;
                    var started = false;

                    for (var j = 0; j < g; j++)
                    {
                        var v = values[(k * g) + j];

                        if (double.IsNaN(v))
                        {
                            if (ignoreNaN)
                            {
                                continue;
                            }

                            best = double.NaN;
                            break;
                        }

                        if (!started || (smallest ? v < best : v > best))
                        {
                            best = v;
                            started = true;
                        }
                    }

                    result[k] = best;
                }

                return Finish(NdArray.FromDoubles(result, groups.Shape), groups, keepDims);
            }

            case DKind.Int or DKind.Bool:
            {
                var values = groups.Data.ToLongs();
                var result = new long[groups.Count];

                for (var k = 0; k < result.Length; k++)
                {
                    var best = values[k * g];

                    for (var j = 1; j < g; j++)
                    {
                        var v = values[(k * g) + j];
                        best = smallest ? Math.Min(best, v) : Math.Max(best, v);
                    }

                    result[k] = best;
                }

                var reduced = NdArray.FromLongs(result, groups.Shape);
                return Finish(array.Kind == DKind.Bool ? reduced.AsType(NdDType.Bool) : reduced, groups, keepDims);
            }

            default:
            {
                var values = groups.Data.ToBoxed();
                var result = new PyObject[groups.Count];

                for (var k = 0; k < result.Length; k++)
                {
                    var best = values[k * g];

                    for (var j = 1; j < g; j++)
                    {
                        var v = values[(k * g) + j];

                        if (Operators.Compare(smallest ? "<" : ">", v, best).IsTruthy())
                        {
                            best = v;
                        }
                    }

                    result[k] = best;
                }

                return Finish(NdArray.FromObjects(array.Kind, result, groups.Shape), groups, keepDims);
            }
        }
    }

    /// <summary><c>argmin</c> / <c>argmax</c>: the first position of the extreme (or of a NaN).</summary>
    public static PyObject ArgExtreme(NdArray array, PyObject? axis, bool smallest, bool keepDims = false)
    {
        var name = smallest ? "argmin" : "argmax";

        if (axis is null or PyNone)
        {
            if (array.Size == 0)
            {
                throw new PyRaise(PyErrors.ValueError($"attempt to get {name} of an empty sequence"));
            }

            var flat = array.Reshape([-1]);
            var index = ArgOf(flat, 0, array.Size, smallest);
            return keepDims ? NdArray.FromLongs([index], [.. Enumerable.Repeat(1, array.NDim)]) : PyInt.From(index);
        }

        var axes = NdConvert.ToAxes(axis, array.NDim)!;
        var groups = Group(array, axes);

        if (groups.GroupSize == 0)
        {
            throw new PyRaise(PyErrors.ValueError($"attempt to get {name} of an empty sequence"));
        }

        var result = new long[groups.Count];
        var data = groups.Data.Reshape([-1]);

        for (var k = 0; k < result.Length; k++)
        {
            result[k] = ArgOf(data, k * groups.GroupSize, groups.GroupSize, smallest);
        }

        return Finish(NdArray.FromLongs(result, groups.Shape), groups, keepDims);
    }

    private static long ArgOf(NdArray flat, int start, int count, bool smallest)
    {
        var offsets = flat.Offsets();

        if (flat.Kind is DKind.Str or DKind.Object)
        {
            var bestIndex = 0;
            var best = flat.GetBoxed(offsets[start]);

            for (var j = 1; j < count; j++)
            {
                var v = flat.GetBoxed(offsets[start + j]);

                if (Operators.Compare(smallest ? "<" : ">", v, best).IsTruthy())
                {
                    best = v;
                    bestIndex = j;
                }
            }

            return bestIndex;
        }

        var bestValue = flat.GetDouble(offsets[start]);
        var at = 0;

        if (double.IsNaN(bestValue))
        {
            return 0;
        }

        for (var j = 1; j < count; j++)
        {
            var v = flat.GetDouble(offsets[start + j]);

            if (double.IsNaN(v))
            {
                return j;
            }

            if (smallest ? v < bestValue : v > bestValue)
            {
                bestValue = v;
                at = j;
            }
        }

        // Integers beyond 2**53 compare exactly as longs.
        if (flat.Kind == DKind.Int)
        {
            var bestLong = flat.GetLong(offsets[start]);
            at = 0;

            for (var j = 1; j < count; j++)
            {
                var v = flat.GetLong(offsets[start + j]);

                if (smallest ? v < bestLong : v > bestLong)
                {
                    bestLong = v;
                    at = j;
                }
            }
        }

        return at;
    }

    public static PyObject Logical(NdArray array, int[]? axes, bool keepDims, bool all)
    {
        var groups = Group(array, axes);
        var values = groups.Data.ToBools();
        var g = groups.GroupSize;
        var result = new bool[groups.Count];

        for (var k = 0; k < result.Length; k++)
        {
            var answer = all;

            for (var j = 0; j < g; j++)
            {
                if (values[(k * g) + j] != all)
                {
                    answer = !all;
                    break;
                }
            }

            result[k] = answer;
        }

        return Finish(NdArray.FromBools(result, groups.Shape), groups, keepDims);
    }

    public static PyObject CountNonZero(NdArray array, int[]? axes, bool keepDims)
    {
        var groups = Group(array, axes);
        var values = groups.Data.ToBools();
        var g = groups.GroupSize;
        var result = new long[groups.Count];

        for (var k = 0; k < result.Length; k++)
        {
            for (var j = 0; j < g; j++)
            {
                if (values[(k * g) + j])
                {
                    result[k]++;
                }
            }
        }

        return Finish(NdArray.FromLongs(result, groups.Shape), groups, keepDims);
    }

    /// <summary><c>cumsum</c> / <c>cumprod</c>: running totals along an axis, or the flattened array.</summary>
    public static NdArray Cumulative(NdArray array, PyObject? axis, bool product)
    {
        var source = array;
        int a;

        if (axis is null or PyNone)
        {
            source = array.Reshape([-1]);
            a = 0;
        }
        else
        {
            a = NdArray.NormalizeAxis(NdConvert.ToInt(axis), array.NDim);
        }

        // Move the axis last, run along contiguous rows, and move it back.
        var permutation = Enumerable.Range(0, source.NDim).Where(d => d != a).Append(a).ToArray();
        var moved = source.Transpose(permutation).Contiguous();
        var length = source.NDim == 0 ? 1 : source.Shape[a];
        NdArray result;

        if (source.Kind is DKind.Int or DKind.Bool)
        {
            var values = moved.ToLongs();

            unchecked
            {
                for (var start = 0; start < values.Length; start += Math.Max(length, 1))
                {
                    for (var j = 1; j < length; j++)
                    {
                        values[start + j] = product ? values[start + j] * values[start + j - 1] : values[start + j] + values[start + j - 1];
                    }
                }
            }

            result = NdArray.FromLongs(values, moved.Shape);
        }
        else
        {
            var values = moved.ToDoubles();

            for (var start = 0; start < values.Length; start += Math.Max(length, 1))
            {
                for (var j = 1; j < length; j++)
                {
                    values[start + j] = product ? values[start + j] * values[start + j - 1] : values[start + j] + values[start + j - 1];
                }
            }

            result = NdArray.FromDoubles(values, moved.Shape);
        }

        var inverse = new int[permutation.Length];

        for (var i = 0; i < permutation.Length; i++)
        {
            inverse[permutation[i]] = i;
        }

        return result.Transpose(inverse).Contiguous();
    }
}
