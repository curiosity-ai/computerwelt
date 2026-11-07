using System.Numerics.Tensors;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules.Numpy;

/// <summary>
/// The vectorised reductions: the whole-array and the per-axis cases over contiguous
/// float and int data, read straight out of the buffer instead of copied first.
/// </summary>
/// <remarks>
/// <para>
/// Three shapes cover nearly every reduction a program writes: the whole array
/// (<c>a.sum()</c>), the trailing axis (<c>a.mean(axis=1)</c> — each output's inputs are
/// one contiguous run) and the leading axes (<c>a.sum(axis=0)</c> — each output's inputs
/// are a column). The first two read spans of the buffer; the third accumulates whole rows
/// into a vector of results with SIMD adds, which is also how numpy itself reduces along a
/// leading axis, instead of transposing the array into a copy first.
/// </para>
/// <para>
/// Accuracy: sums stay pairwise above <see cref="Leaf"/> elements, and each leaf is summed
/// by <see cref="TensorPrimitives.Sum{T}(ReadOnlySpan{T})"/> with several vector
/// accumulators — a different association than numpy's eight-way pairwise blocks, so a total
/// can differ from numpy's in the last digits. Below <see cref="NdSimd.Threshold"/>
/// elements every reduction takes the original, numpy-identical path.
/// </para>
/// </remarks>
internal static partial class Reductions
{
    /// <summary>The block a vectorised sum adds directly before splitting pairwise.</summary>
    private const int Leaf = 4096;

    /// <summary>A sum of a contiguous run: vectorised leaves under a pairwise split.</summary>
    internal static double FastSum(ReadOnlySpan<double> values)
    {
        if (values.Length <= Leaf)
        {
            return TensorPrimitives.Sum(values);
        }

        var half = values.Length / 2;
        return FastSum(values[..half]) + FastSum(values[half..]);
    }

    /// <summary>A contiguous float or int array's elements as doubles without copying a float one.</summary>
    private static ReadOnlySpan<double> DoublesOf(NdArray array, out double[]? copy)
    {
        if (array.Kind == DKind.Float && array.IsContiguous)
        {
            copy = null;
            return array.Buffer.F.AsSpan(array.Offset, array.Size);
        }

        copy = array.ToDoubles();
        return copy;
    }

    private static bool Large(NdArray array) => array.Size >= NdSimd.Threshold && array.IsContiguous;

    private static bool Whole(NdArray array, int[]? axes) =>
        axes is null || (axes.Length == array.NDim && axes.Distinct().Count() == array.NDim);

    /// <summary>How many leading axes are reduced, when the reduced axes are exactly <c>0..k-1</c> with <c>k &lt; ndim</c>; else 0.</summary>
    private static int LeadingAxes(NdArray array, int[]? axes)
    {
        if (axes is null || axes.Length == 0 || axes.Length >= array.NDim)
        {
            return 0;
        }

        var sorted = axes.Order().ToArray();

        for (var i = 0; i < sorted.Length; i++)
        {
            if (sorted[i] != i)
            {
                return 0;
            }
        }

        return sorted.Length;
    }

    private static PyObject Scalar(NdArray array, double value, bool keepDims) =>
        keepDims
            ? NdConvert.Wrap(NdArray.FromDoubles([value], [.. Enumerable.Repeat(1, array.NDim)]))
            : new PyFloat(value);

    private static PyObject Scalar(NdArray array, long value, bool keepDims) =>
        keepDims
            ? NdConvert.Wrap(NdArray.FromLongs([value], [.. Enumerable.Repeat(1, array.NDim)]))
            : PyInt.From(value);

    /// <summary>The reduced result's shape, and its kept-dims shape, for a leading-axes reduction.</summary>
    private static (int[] Shape, int[] Kept, int Rows, int Width) Columns(NdArray array, int leading)
    {
        var shape = array.Shape[leading..];
        var kept = array.Shape.Select((size, d) => d < leading ? 1 : size).ToArray();
        var rows = 1;

        for (var d = 0; d < leading; d++)
        {
            rows *= array.Shape[d];
        }

        return (shape, kept, rows, array.Size / Math.Max(rows, 1));
    }

    private static PyObject FinishColumns(NdArray result, int[] kept, bool keepDims) =>
        NdConvert.Wrap(keepDims ? result.Reshape(kept) : result);

    /// <summary><c>sum</c> over contiguous float/int data, whole or along leading axes, or null.</summary>
    private static PyObject? TrySumFast(NdArray array, int[]? axes, bool keepDims, NdDType? dtype)
    {
        if (dtype is null && array.Kind == DKind.Bool && Whole(array, axes) && NdSimd.CountNonZero(array) is >= 0 and var trues)
        {
            return Scalar(array, trues, keepDims);
        }

        if (dtype is not null || !Large(array) || array.Kind is not (DKind.Float or DKind.Int))
        {
            return null;
        }

        if (Whole(array, axes))
        {
            return array.Kind == DKind.Float
                ? Scalar(array, FastSum(array.Buffer.F.AsSpan(array.Offset, array.Size)), keepDims)
                : Scalar(array, TensorPrimitives.Sum<long>(array.Buffer.I.AsSpan(array.Offset, array.Size)), keepDims);
        }

        var leading = LeadingAxes(array, axes);

        if (leading == 0)
        {
            return null;
        }

        var (shape, kept, rows, width) = Columns(array, leading);

        if (array.Kind == DKind.Float)
        {
            var data = array.Buffer.F.AsSpan(array.Offset, array.Size);
            var sums = new double[width];

            for (var r = 0; r < rows; r++)
            {
                TensorPrimitives.Add(sums, data.Slice(r * width, width), sums);
            }

            return FinishColumns(NdArray.FromDoubles(sums, shape), kept, keepDims);
        }

        var longs = array.Buffer.I.AsSpan(array.Offset, array.Size);
        var totals = new long[width];

        for (var r = 0; r < rows; r++)
        {
            TensorPrimitives.Add<long>(totals, longs.Slice(r * width, width), totals);
        }

        return FinishColumns(NdArray.FromLongs(totals, shape), kept, keepDims);
    }

    /// <summary>
    /// <c>mean</c>, <c>var</c> and <c>std</c> over contiguous float/int data, whole or along
    /// leading axes, or null.
    /// </summary>
    internal static PyObject? TryMomentFast(string name, NdArray array, int[]? axes, bool keepDims, int ddof)
    {
        if (!Large(array) || array.Kind is not (DKind.Float or DKind.Int))
        {
            return null;
        }

        if (Whole(array, axes))
        {
            var values = DoublesOf(array, out _);
            return Scalar(array, Moment(name, values, ddof), keepDims);
        }

        var leading = LeadingAxes(array, axes);

        if (leading == 0)
        {
            return null;
        }

        var (shape, kept, rows, width) = Columns(array, leading);
        var data = DoublesOf(array, out _);
        var mean = new double[width];

        for (var r = 0; r < rows; r++)
        {
            TensorPrimitives.Add(mean, data.Slice(r * width, width), mean);
        }

        TensorPrimitives.Divide(mean, rows, mean);

        if (name == "mean")
        {
            return FinishColumns(NdArray.FromDoubles(mean, shape), kept, keepDims);
        }

        var squares = new double[width];
        var deviation = System.Buffers.ArrayPool<double>.Shared.Rent(width);

        try
        {
            var scratch = deviation.AsSpan(0, width);

            for (var r = 0; r < rows; r++)
            {
                TensorPrimitives.Subtract(data.Slice(r * width, width), mean, scratch);
                TensorPrimitives.MultiplyAdd(scratch, scratch, squares, squares);
            }
        }
        finally
        {
            System.Buffers.ArrayPool<double>.Shared.Return(deviation);
        }

        TensorPrimitives.Divide(squares, Math.Max(rows - ddof, 0), squares);

        if (name == "std")
        {
            TensorPrimitives.Sqrt(squares, squares);
        }

        return FinishColumns(NdArray.FromDoubles(squares, shape), kept, keepDims);
    }

    /// <summary>The mean, variance or standard deviation of one contiguous run.</summary>
    internal static double Moment(string name, ReadOnlySpan<double> values, int ddof)
    {
        var n = values.Length;
        var mean = FastSum(values) / n;

        if (name == "mean")
        {
            return mean;
        }

        var deviation = System.Buffers.ArrayPool<double>.Shared.Rent(n);

        try
        {
            var scratch = deviation.AsSpan(0, n);
            TensorPrimitives.Subtract(values, mean, scratch);
            var variance = TensorPrimitives.SumOfSquares(scratch) / Math.Max(n - ddof, 0);
            return name == "std" ? Math.Sqrt(variance) : variance;
        }
        finally
        {
            System.Buffers.ArrayPool<double>.Shared.Return(deviation);
        }
    }

    /// <summary><c>min</c> / <c>max</c> over contiguous float/int data, whole or along leading axes, or null.</summary>
    private static PyObject? TryExtremeFast(NdArray array, int[]? axes, bool keepDims, bool smallest, bool ignoreNaN)
    {
        // nanmax/nanmin keep the exact loop: TensorPrimitives' MaxNumber/MinNumber reductions
        // were seen to return NaN for a span holding one, which is the one answer they must
        // not give (the element-wise forms are right, and fmax/fmin use those).
        if (ignoreNaN || !Large(array) || array.Kind is not (DKind.Float or DKind.Int))
        {
            return null;
        }

        if (Whole(array, axes))
        {
            if (array.Kind == DKind.Int)
            {
                var longs = array.Buffer.I.AsSpan(array.Offset, array.Size);
                return Scalar(array, smallest ? TensorPrimitives.Min<long>(longs) : TensorPrimitives.Max<long>(longs), keepDims);
            }

            // Max and Min propagate NaN, as numpy's max and min do.
            var values = array.Buffer.F.AsSpan(array.Offset, array.Size);
            return Scalar(array, smallest ? TensorPrimitives.Min(values) : TensorPrimitives.Max(values), keepDims);
        }

        var leading = LeadingAxes(array, axes);

        if (leading == 0 || array.Kind != DKind.Float)
        {
            return null;
        }

        var (shape, kept, rows, width) = Columns(array, leading);
        var data = array.Buffer.F.AsSpan(array.Offset, array.Size);
        var result = data[..width].ToArray();

        for (var r = 1; r < rows; r++)
        {
            var row = data.Slice(r * width, width);

            if (smallest)
            {
                TensorPrimitives.Min(result, row, result);
            }
            else
            {
                TensorPrimitives.Max(result, row, result);
            }
        }

        return FinishColumns(NdArray.FromDoubles(result, shape), kept, keepDims);
    }

    /// <summary>The first index of the extreme (or of the first NaN) in a contiguous run, or -1 to take the general path.</summary>
    private static long FastArg(NdArray flat, int start, int count, bool smallest)
    {
        if (count < NdSimd.Threshold || !flat.IsContiguous)
        {
            return -1;
        }

        return flat.Kind switch
        {
            DKind.Float => smallest
                ? TensorPrimitives.IndexOfMin(flat.Buffer.F.AsSpan(flat.Offset + start, count))
                : TensorPrimitives.IndexOfMax(flat.Buffer.F.AsSpan(flat.Offset + start, count)),
            DKind.Int => smallest
                ? TensorPrimitives.IndexOfMin<long>(flat.Buffer.I.AsSpan(flat.Offset + start, count))
                : TensorPrimitives.IndexOfMax<long>(flat.Buffer.I.AsSpan(flat.Offset + start, count)),
            _ => -1,
        };
    }
}
