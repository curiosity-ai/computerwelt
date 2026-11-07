using System.Buffers;
using System.Numerics.Tensors;
using System.Runtime.Intrinsics;

namespace Computerwelt.Emulation.Python.Modules.Numpy;

/// <summary>
/// The matrix-product kernels: a packed, register-blocked float GEMM and a vectorised
/// int64 one.
/// </summary>
/// <remarks>
/// <para>
/// The float kernel is the classic shape of a BLAS <c>dgemm</c>, one level down: the
/// right-hand matrix is copied into column strips <see cref="StripWidth"/> wide and
/// <see cref="DepthBlock"/> deep, so the inner loop reads it sequentially, and each 4×8
/// block of the result is held in eight <see cref="Vector256{T}"/> accumulators across that
/// depth, updated with fused multiply-adds. Every element of the right-hand strip is loaded
/// once per four result rows instead of once per row, which is what makes the product
/// compute-bound rather than memory-bound. A matrix-vector product is a dot product per row
/// and a vector-matrix one a scaled row added per element of the vector.
/// </para>
/// <para>
/// Accuracy: each result element is a sum over the depth in blocks of
/// <see cref="DepthBlock"/>, rounded through fused multiply-adds — the association BLAS uses,
/// and not the left-to-right sum the small-product loop takes. Integer-valued data is exact
/// either way. Products smaller than <see cref="SmallWork"/> multiply-adds keep that loop,
/// so the small results a program prints do not move.
/// </para>
/// <para>
/// One thread, deliberately: numpy's BLAS spreads a large product over every core, and a
/// sandbox that did the same would hand one tenant's program the host's CPUs.
/// </para>
/// </remarks>
internal static partial class LinAlg
{
    private const int StripWidth = 8;

    private const int DepthBlock = 256;

    private const int SmallWork = 1 << 14;

    /// <summary>Whether a product is large enough for the blocked kernels.</summary>
    private static bool UsesKernel(int p, int k, int q) => (long)p * k * q >= SmallWork;

    /// <summary><c>C = A·B</c> for row-major A (p×k), B (k×q) and C (p×q).</summary>
    internal static void Gemm(ReadOnlySpan<double> a, ReadOnlySpan<double> b, Span<double> c, int p, int k, int q)
    {
        c.Clear();

        if (q == 1)
        {
            for (var i = 0; i < p; i++)
            {
                c[i] = TensorPrimitives.Dot(a.Slice(i * k, k), b);
            }

            return;
        }

        if (p == 1 || !Vector256.IsHardwareAccelerated)
        {
            for (var i = 0; i < p; i++)
            {
                var row = c.Slice(i * q, q);

                for (var m = 0; m < k; m++)
                {
                    TensorPrimitives.MultiplyAdd(b.Slice(m * q, q), a[(i * k) + m], row, row);
                }
            }

            return;
        }

        var strips = (q + StripWidth - 1) / StripWidth;
        var packed = ArrayPool<double>.Shared.Rent(strips * DepthBlock * StripWidth);

        try
        {
            for (var depth = 0; depth < k; depth += DepthBlock)
            {
                var kb = Math.Min(DepthBlock, k - depth);
                Pack(b, packed, depth, kb, q, strips);

                for (var i = 0; i < p; i += 4)
                {
                    var rows = Math.Min(4, p - i);

                    for (var s = 0; s < strips; s++)
                    {
                        var columns = Math.Min(StripWidth, q - (s * StripWidth));
                        Block(a, packed.AsSpan(s * kb * StripWidth, kb * StripWidth), c, i, rows, depth, kb, k, q, s * StripWidth, columns);
                    }
                }
            }
        }
        finally
        {
            ArrayPool<double>.Shared.Return(packed);
        }
    }

    /// <summary>B's rows <c>depth..depth+kb</c>, as strips of eight columns, the last padded with zeros.</summary>
    private static void Pack(ReadOnlySpan<double> b, double[] packed, int depth, int kb, int q, int strips)
    {
        for (var s = 0; s < strips; s++)
        {
            var column = s * StripWidth;
            var width = Math.Min(StripWidth, q - column);
            var strip = packed.AsSpan(s * kb * StripWidth, kb * StripWidth);

            for (var m = 0; m < kb; m++)
            {
                var target = strip.Slice(m * StripWidth, StripWidth);
                b.Slice(((depth + m) * q) + column, width).CopyTo(target);
                target[width..].Clear();
            }
        }
    }

    /// <summary>A 4×8 block of C accumulated over one depth block; rows and columns past the edge are dropped.</summary>
    private static void Block(
        ReadOnlySpan<double> a,
        ReadOnlySpan<double> strip,
        Span<double>         c,
        int                  row,
        int                  rows,
        int                  depth,
        int                  kb,
        int                  k,
        int                  q,
        int                  column,
        int                  columns)
    {
        // Rows past the edge read the last real row again; their sums are discarded.
        var r0 = a.Slice((row * k) + depth, kb);
        var r1 = a.Slice(((row + Math.Min(1, rows - 1)) * k) + depth, kb);
        var r2 = a.Slice(((row + Math.Min(2, rows - 1)) * k) + depth, kb);
        var r3 = a.Slice(((row + Math.Min(3, rows - 1)) * k) + depth, kb);

        Vector256<double> c00 = default, c01 = default, c10 = default, c11 = default;
        Vector256<double> c20 = default, c21 = default, c30 = default, c31 = default;

        for (var m = 0; m < kb; m++)
        {
            var b0 = Vector256.Create(strip.Slice(m * StripWidth, 4));
            var b1 = Vector256.Create(strip.Slice((m * StripWidth) + 4, 4));

            var x = Vector256.Create(r0[m]);
            c00 = Vector256.FusedMultiplyAdd(x, b0, c00);
            c01 = Vector256.FusedMultiplyAdd(x, b1, c01);

            x = Vector256.Create(r1[m]);
            c10 = Vector256.FusedMultiplyAdd(x, b0, c10);
            c11 = Vector256.FusedMultiplyAdd(x, b1, c11);

            x = Vector256.Create(r2[m]);
            c20 = Vector256.FusedMultiplyAdd(x, b0, c20);
            c21 = Vector256.FusedMultiplyAdd(x, b1, c21);

            x = Vector256.Create(r3[m]);
            c30 = Vector256.FusedMultiplyAdd(x, b0, c30);
            c31 = Vector256.FusedMultiplyAdd(x, b1, c31);
        }

        Span<double> sums = stackalloc double[4 * StripWidth];
        c00.CopyTo(sums);
        c01.CopyTo(sums[4..]);
        c10.CopyTo(sums[8..]);
        c11.CopyTo(sums[12..]);
        c20.CopyTo(sums[16..]);
        c21.CopyTo(sums[20..]);
        c30.CopyTo(sums[24..]);
        c31.CopyTo(sums[28..]);

        for (var r = 0; r < rows; r++)
        {
            var target = c.Slice(((row + r) * q) + column, columns);
            TensorPrimitives.Add(target, sums.Slice(r * StripWidth, columns), target);
        }
    }

    /// <summary><c>C = A·B</c> over int64, wrapping: each row of C gathers scaled rows of B.</summary>
    internal static void Gemm(ReadOnlySpan<long> a, ReadOnlySpan<long> b, Span<long> c, int p, int k, int q)
    {
        c.Clear();

        for (var i = 0; i < p; i++)
        {
            var row = c.Slice(i * q, q);

            for (var m = 0; m < k; m++)
            {
                var factor = a[(i * k) + m];

                if (factor != 0)
                {
                    AddScaled(row, b.Slice(m * q, q), factor);
                }
            }
        }
    }

    private static void AddScaled(Span<long> target, ReadOnlySpan<long> source, long factor)
    {
        var j = 0;

        if (Vector256.IsHardwareAccelerated)
        {
            var f = Vector256.Create(factor);

            for (; j <= source.Length - Vector256<long>.Count; j += Vector256<long>.Count)
            {
                var sum = Vector256.Create((ReadOnlySpan<long>)target.Slice(j, Vector256<long>.Count))
                    + (Vector256.Create(source.Slice(j, Vector256<long>.Count)) * f);
                sum.CopyTo(target[j..]);
            }
        }

        for (; j < source.Length; j++)
        {
            target[j] = unchecked(target[j] + (source[j] * factor));
        }
    }
}
