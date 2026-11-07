using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules.Numpy;

/// <summary>
/// Boolean masks over contiguous data: <c>a[mask]</c>, <c>a[mask] = v</c>,
/// <c>np.where(c, x, y)</c> and counting the set elements.
/// </summary>
/// <remarks>
/// A mask is a run of bytes that are 0 or 1, so it is read eight at a time as one
/// <see cref="ulong"/>: an all-clear word is skipped and an all-set word copied whole, which
/// is the common case for the clustered masks programs build from comparisons. Every result
/// is exact — these loops only choose which elements move, never change their values.
/// </remarks>
internal static partial class NdSimd
{
    private const ulong AllSet = 0x0101010101010101UL;

    private static bool SameShape(NdArray array, NdArray mask) =>
        array.IsContiguous && mask.IsContiguous && array.Shape.AsSpan().SequenceEqual(mask.Shape);

    /// <summary>How many elements of a bool run are set.</summary>
    public static int CountTrue(ReadOnlySpan<bool> mask) =>
        mask.Length - MemoryMarshal.AsBytes(mask).Count((byte)0);

    /// <summary><c>a[mask]</c> for a contiguous array and a mask of its own shape, or null.</summary>
    public static NdArray? TryMaskGet(NdArray array, PyObject index)
    {
        if (index is not NdArray { Kind: DKind.Bool } mask || array.Size < Threshold || !SameShape(array, mask))
        {
            return null;
        }

        var bits = Bools(mask);

        switch (array.Kind)
        {
            case DKind.Float:
            {
                var result = GC.AllocateUninitializedArray<double>(CountTrue(bits));
                Compress(Doubles(array), bits, result);
                return NdArray.FromDoubles(result, [result.Length]);
            }

            case DKind.Int:
            {
                var result = GC.AllocateUninitializedArray<long>(CountTrue(bits));
                Compress(Longs(array), bits, result);
                return NdArray.FromLongs(result, [result.Length]);
            }

            case DKind.Bool:
            {
                var result = new bool[CountTrue(bits)];
                Compress(Bools(array), bits, result);
                return NdArray.FromBools(result, [result.Length]);
            }

            default:
                return null;
        }
    }

    /// <summary>The elements of <paramref name="source"/> whose mask byte is set, in order.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Compress<T>(ReadOnlySpan<T> source, ReadOnlySpan<bool> mask, Span<T> destination)
    {
        var words = MemoryMarshal.Cast<bool, ulong>(mask);
        var j = 0;

        for (var w = 0; w < words.Length; w++)
        {
            var word = words[w];

            if (word == 0)
            {
                continue;
            }

            var start = w * 8;

            if (word == AllSet)
            {
                source.Slice(start, 8).CopyTo(destination[j..]);
                j += 8;
                continue;
            }

            for (var i = start; i < start + 8; i++)
            {
                if (mask[i])
                {
                    destination[j++] = source[i];
                }
            }
        }

        for (var i = words.Length * 8; i < mask.Length; i++)
        {
            if (mask[i])
            {
                destination[j++] = source[i];
            }
        }
    }

    /// <summary>
    /// <c>a[mask] = scalar</c> for a contiguous float or int array and a mask of its shape;
    /// false when the general assignment has to run.
    /// </summary>
    public static bool TryMaskSet(NdArray array, PyObject index, PyObject value)
    {
        if (index is not NdArray { Kind: DKind.Bool } mask || array.Size < Threshold || !SameShape(array, mask))
        {
            return false;
        }

        // Only a value that converts without a cast rule: an int or bool into either kind,
        // a float into a float array.
        var bits = Bools(mask);

        switch (array.Kind, value)
        {
            case (DKind.Float, PyFloat f):
                Fill(array.Buffer.F.AsSpan(array.Offset, array.Size), bits, f.Value);
                return true;

            case (DKind.Float, PyInt i) when NdConvert.ToArray(i) is { Kind: DKind.Int or DKind.Bool } scalar:
                Fill(array.Buffer.F.AsSpan(array.Offset, array.Size), bits, scalar.GetDouble(scalar.Offset));
                return true;

            case (DKind.Int, PyInt i) when NdConvert.ToArray(i) is { Kind: DKind.Int or DKind.Bool } scalar:
                Fill(array.Buffer.I.AsSpan(array.Offset, array.Size), bits, scalar.GetLong(scalar.Offset));
                return true;

            default:
                return false;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Fill<T>(Span<T> target, ReadOnlySpan<bool> mask, T value)
    {
        var words = MemoryMarshal.Cast<bool, ulong>(mask);

        for (var w = 0; w < words.Length; w++)
        {
            var word = words[w];

            if (word == 0)
            {
                continue;
            }

            var start = w * 8;

            if (word == AllSet)
            {
                target.Slice(start, 8).Fill(value);
                continue;
            }

            for (var i = start; i < start + 8; i++)
            {
                if (mask[i])
                {
                    target[i] = value;
                }
            }
        }

        for (var i = words.Length * 8; i < mask.Length; i++)
        {
            if (mask[i])
            {
                target[i] = value;
            }
        }
    }

    /// <summary><c>np.where(c, x, y)</c> over a contiguous bool condition and float/int operands, or null.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static NdArray? TryWhere(NdArray c, NdArray x, NdArray y, int[] shape, int n)
    {
        if (n < Threshold || c.Kind != DKind.Bool || !Covers(c, shape) || !Usable(x, shape) || !Usable(y, shape)
            || x.Kind is not (DKind.Float or DKind.Int) || y.Kind is not (DKind.Float or DKind.Int))
        {
            return null;
        }

        var condition = Bools(c);

        if (x.Kind == DKind.Int && y.Kind == DKind.Int)
        {
            var a = AsLongs(x);
            var b = AsLongs(y);
            var longs = GC.AllocateUninitializedArray<long>(n);

            for (var i = 0; i < longs.Length; i++)
            {
                longs[i] = condition[i] ? a[i] : b[i];
            }

            return NdArray.FromLongs(longs, shape);
        }

        double[]? xScratch = null;
        double[]? yScratch = null;

        try
        {
            var a = AsDoubles(x, ref xScratch);
            var b = AsDoubles(y, ref yScratch);
            var doubles = GC.AllocateUninitializedArray<double>(n);

            for (var i = 0; i < doubles.Length; i++)
            {
                doubles[i] = condition[i] ? a[i] : b[i];
            }

            return NdArray.FromDoubles(doubles, shape);
        }
        finally
        {
            ReturnScratch(xScratch);
            ReturnScratch(yScratch);
        }
    }

    /// <summary>The number of non-zero elements of a contiguous array, or -1 to take the general path.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static long CountNonZero(NdArray array)
    {
        if (array.Size < Threshold || !array.IsContiguous)
        {
            return -1;
        }

        switch (array.Kind)
        {
            case DKind.Bool:
                return CountTrue(Bools(array));

            case DKind.Int:
                return array.Size - Longs(array).Count(0L);

            case DKind.Float:
            {
                // -0.0 is zero and NaN is not, which is what `!= 0` says.
                var values = Doubles(array);
                var count = 0;

                foreach (var value in values)
                {
                    count += value != 0 ? 1 : 0;
                }

                return count;
            }

            default:
                return -1;
        }
    }
}
