using System.Buffers;
using System.Numerics.Tensors;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Computerwelt.Emulation.Python.Modules.Numpy;

/// <summary>
/// The matrix-product kernels: a packed, register-blocked float GEMM and a vectorised
/// int64 one.
/// </summary>
/// <remarks>
/// <para>
/// The float kernel is the classic shape of a BLAS <c>dgemm</c>, one level down: the
/// right-hand matrix is copied into column strips and <see cref="DepthBlock"/> deep, so the
/// inner loop reads it sequentially, and a block of the result is held in registers across
/// that depth, updated with fused multiply-adds — 8×16 in sixteen <see cref="Vector512{T}"/>
/// accumulators where AVX-512 is available, 4×8 in eight <see cref="Vector256{T}"/> ones
/// otherwise. Every element of a right-hand strip is loaded once per block of rows instead of
/// once per row, which is what makes the product compute-bound rather than memory-bound. A
/// matrix-vector product is a dot product per row and a vector-matrix one a scaled row added
/// per element of the vector.
/// </para>
/// <para>
/// Accuracy: each result element is a sum over the depth in blocks of
/// <see cref="DepthBlock"/> (or <see cref="WideDepthBlock"/>), rounded through fused
/// multiply-adds — the association BLAS uses,
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
    /// <summary>The depth of a packed strip: 256 rows for the 8-wide kernel; the 16-wide one uses <see cref="WideDepthBlock"/>.</summary>
    private const int DepthBlock = 256;

    /// <summary>
    /// The 16-wide kernel's depth: a 128-deep strip (16 KB) and its eight rows of A (8 KB)
    /// stay in a 48 KB L1 together, where a 256-deep one did not (1000×1000: 44–50 GFLOPS
    /// against 32–35).
    /// </summary>
    private const int WideDepthBlock = 128;

    private const int SmallWork = 1 << 14;

    /// <summary>Whether a product is large enough for the blocked kernels.</summary>
    private static bool UsesKernel(int p, int k, int q) => (long)p * k * q >= SmallWork;

    /// <summary>Whether the 512-bit kernel runs: AVX-512 here, and the JIT willing to use it.</summary>
    private static bool Wide => Vector512.IsHardwareAccelerated;

    /// <summary><c>C = A·B</c> for row-major A (p×k), B (k×q) and C (p×q).</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
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

        // An 8×16 block in sixteen 512-bit accumulators where AVX-512 is available (32
        // registers), a 4×8 block in eight 256-bit ones otherwise (16 registers).
        var width = Wide ? 16 : 8;
        var height = Wide ? 8 : 4;
        var block = Wide ? WideDepthBlock : DepthBlock;
        var strips = (q + width - 1) / width;
        var packed = ArrayPool<double>.Shared.Rent(strips * block * width);

        try
        {
            for (var depth = 0; depth < k; depth += block)
            {
                var kb = Math.Min(block, k - depth);
                Pack(b, packed, depth, kb, q, strips, width);

                for (var i = 0; i < p; i += height)
                {
                    var rows = Math.Min(height, p - i);

                    for (var s = 0; s < strips; s++)
                    {
                        var columns = Math.Min(width, q - (s * width));
                        var strip = packed.AsSpan(s * kb * width, kb * width);

                        if (Wide)
                        {
                            Block8x16(a, strip, c, i, rows, depth, kb, k, q, s * width, columns);
                        }
                        else
                        {
                            Block4x8(a, strip, c, i, rows, depth, kb, k, q, s * width, columns);
                        }
                    }
                }
            }
        }
        finally
        {
            ArrayPool<double>.Shared.Return(packed);
        }
    }

    /// <summary>B's rows <c>depth..depth+kb</c>, as strips <paramref name="width"/> columns wide, the last padded with zeros.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Pack(ReadOnlySpan<double> b, double[] packed, int depth, int kb, int q, int strips, int width)
    {
        for (var s = 0; s < strips; s++)
        {
            var column = s * width;
            var count = Math.Min(width, q - column);
            var strip = packed.AsSpan(s * kb * width, kb * width);

            for (var m = 0; m < kb; m++)
            {
                var target = strip.Slice(m * width, width);
                b.Slice(((depth + m) * q) + column, count).CopyTo(target);
                target[count..].Clear();
            }
        }
    }

    /// <summary>The start of row <paramref name="r"/> of a block at the current depth; a row past the edge reads the last real one, whose sums are then discarded.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static ref double RowStart(ReadOnlySpan<double> a, int row, int rows, int r, int depth, int k) =>
        ref Unsafe.AsRef(in a[((row + Math.Min(r, rows - 1)) * k) + depth]);

    /// <summary>Adds the first <paramref name="columns"/> of each block row's sums into C.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Store(Span<double> c, ReadOnlySpan<double> sums, int width, int row, int rows, int q, int column, int columns)
    {
        for (var r = 0; r < rows; r++)
        {
            var target = c.Slice(((row + r) * q) + column, columns);
            var sum = sums.Slice(r * width, columns);

            for (var j = 0; j < target.Length; j++)
            {
                target[j] += sum[j];
            }
        }
    }

    /// <summary>A 8×16 block of C accumulated over one depth block.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Block8x16(
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
        Span<double> sums = stackalloc double[8 * 16];
        Kernel8x16(
            ref RowStart(a, row, rows, 0, depth, k),
            ref RowStart(a, row, rows, 1, depth, k),
            ref RowStart(a, row, rows, 2, depth, k),
            ref RowStart(a, row, rows, 3, depth, k),
            ref RowStart(a, row, rows, 4, depth, k),
            ref RowStart(a, row, rows, 5, depth, k),
            ref RowStart(a, row, rows, 6, depth, k),
            ref RowStart(a, row, rows, 7, depth, k),
            ref MemoryMarshal.GetReference(strip),
            (nuint)kb,
            ref MemoryMarshal.GetReference(sums));
        Store(c, sums, 16, row, rows, q, column, columns);
    }

    /// <summary>A 4×8 block of C accumulated over one depth block.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Block4x8(
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
        Span<double> sums = stackalloc double[4 * 8];
        Kernel4x8(
            ref RowStart(a, row, rows, 0, depth, k),
            ref RowStart(a, row, rows, 1, depth, k),
            ref RowStart(a, row, rows, 2, depth, k),
            ref RowStart(a, row, rows, 3, depth, k),
            ref MemoryMarshal.GetReference(strip),
            (nuint)kb,
            ref MemoryMarshal.GetReference(sums));
        Store(c, sums, 8, row, rows, q, column, columns);
    }

    // The kernels below are the whole of the arithmetic, and are kept free of calls on
    // purpose: vector registers do not survive a call on the System V ABI, and with a call
    // after the loop the JIT kept the 512-bit accumulators on the stack, storing and
    // reloading all of them every iteration (about 20 GFLOPS instead of 50). The row and
    // strip references are in bounds by construction — every row holds `depth` elements
    // from where it starts and the strip is `depth` rows of the block's width — so the loop
    // reads through them without a bounds check per load.

    [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.NoInlining)]
    private static void Kernel8x16(
        ref double r0,
        ref double r1,
        ref double r2,
        ref double r3,
        ref double r4,
        ref double r5,
        ref double r6,
        ref double r7,
        ref double strip,
        nuint      depth,
        ref double sums)
    {
        var c00 = Vector512<double>.Zero;
        var c01 = Vector512<double>.Zero;
        var c10 = Vector512<double>.Zero;
        var c11 = Vector512<double>.Zero;
        var c20 = Vector512<double>.Zero;
        var c21 = Vector512<double>.Zero;
        var c30 = Vector512<double>.Zero;
        var c31 = Vector512<double>.Zero;
        var c40 = Vector512<double>.Zero;
        var c41 = Vector512<double>.Zero;
        var c50 = Vector512<double>.Zero;
        var c51 = Vector512<double>.Zero;
        var c60 = Vector512<double>.Zero;
        var c61 = Vector512<double>.Zero;
        var c70 = Vector512<double>.Zero;
        var c71 = Vector512<double>.Zero;

        for (nuint m = 0; m < depth; m++)
        {
            var b0 = Vector512.LoadUnsafe(ref strip, m * 16);
            var b1 = Vector512.LoadUnsafe(ref strip, (m * 16) + 8);
            Vector512<double> x;

            x = Vector512.Create(Unsafe.Add(ref r0, m));
            c00 = Vector512.FusedMultiplyAdd(x, b0, c00);
            c01 = Vector512.FusedMultiplyAdd(x, b1, c01);

            x = Vector512.Create(Unsafe.Add(ref r1, m));
            c10 = Vector512.FusedMultiplyAdd(x, b0, c10);
            c11 = Vector512.FusedMultiplyAdd(x, b1, c11);

            x = Vector512.Create(Unsafe.Add(ref r2, m));
            c20 = Vector512.FusedMultiplyAdd(x, b0, c20);
            c21 = Vector512.FusedMultiplyAdd(x, b1, c21);

            x = Vector512.Create(Unsafe.Add(ref r3, m));
            c30 = Vector512.FusedMultiplyAdd(x, b0, c30);
            c31 = Vector512.FusedMultiplyAdd(x, b1, c31);

            x = Vector512.Create(Unsafe.Add(ref r4, m));
            c40 = Vector512.FusedMultiplyAdd(x, b0, c40);
            c41 = Vector512.FusedMultiplyAdd(x, b1, c41);

            x = Vector512.Create(Unsafe.Add(ref r5, m));
            c50 = Vector512.FusedMultiplyAdd(x, b0, c50);
            c51 = Vector512.FusedMultiplyAdd(x, b1, c51);

            x = Vector512.Create(Unsafe.Add(ref r6, m));
            c60 = Vector512.FusedMultiplyAdd(x, b0, c60);
            c61 = Vector512.FusedMultiplyAdd(x, b1, c61);

            x = Vector512.Create(Unsafe.Add(ref r7, m));
            c70 = Vector512.FusedMultiplyAdd(x, b0, c70);
            c71 = Vector512.FusedMultiplyAdd(x, b1, c71);
        }

        c00.StoreUnsafe(ref sums, 0);
        c01.StoreUnsafe(ref sums, 8);
        c10.StoreUnsafe(ref sums, 16);
        c11.StoreUnsafe(ref sums, 24);
        c20.StoreUnsafe(ref sums, 32);
        c21.StoreUnsafe(ref sums, 40);
        c30.StoreUnsafe(ref sums, 48);
        c31.StoreUnsafe(ref sums, 56);
        c40.StoreUnsafe(ref sums, 64);
        c41.StoreUnsafe(ref sums, 72);
        c50.StoreUnsafe(ref sums, 80);
        c51.StoreUnsafe(ref sums, 88);
        c60.StoreUnsafe(ref sums, 96);
        c61.StoreUnsafe(ref sums, 104);
        c70.StoreUnsafe(ref sums, 112);
        c71.StoreUnsafe(ref sums, 120);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.NoInlining)]
    private static void Kernel4x8(
        ref double r0,
        ref double r1,
        ref double r2,
        ref double r3,
        ref double strip,
        nuint      depth,
        ref double sums)
    {
        var c00 = Vector256<double>.Zero;
        var c01 = Vector256<double>.Zero;
        var c10 = Vector256<double>.Zero;
        var c11 = Vector256<double>.Zero;
        var c20 = Vector256<double>.Zero;
        var c21 = Vector256<double>.Zero;
        var c30 = Vector256<double>.Zero;
        var c31 = Vector256<double>.Zero;

        for (nuint m = 0; m < depth; m++)
        {
            var b0 = Vector256.LoadUnsafe(ref strip, m * 8);
            var b1 = Vector256.LoadUnsafe(ref strip, (m * 8) + 4);
            Vector256<double> x;

            x = Vector256.Create(Unsafe.Add(ref r0, m));
            c00 = Vector256.FusedMultiplyAdd(x, b0, c00);
            c01 = Vector256.FusedMultiplyAdd(x, b1, c01);

            x = Vector256.Create(Unsafe.Add(ref r1, m));
            c10 = Vector256.FusedMultiplyAdd(x, b0, c10);
            c11 = Vector256.FusedMultiplyAdd(x, b1, c11);

            x = Vector256.Create(Unsafe.Add(ref r2, m));
            c20 = Vector256.FusedMultiplyAdd(x, b0, c20);
            c21 = Vector256.FusedMultiplyAdd(x, b1, c21);

            x = Vector256.Create(Unsafe.Add(ref r3, m));
            c30 = Vector256.FusedMultiplyAdd(x, b0, c30);
            c31 = Vector256.FusedMultiplyAdd(x, b1, c31);
        }

        c00.StoreUnsafe(ref sums, 0);
        c01.StoreUnsafe(ref sums, 4);
        c10.StoreUnsafe(ref sums, 8);
        c11.StoreUnsafe(ref sums, 12);
        c20.StoreUnsafe(ref sums, 16);
        c21.StoreUnsafe(ref sums, 20);
        c30.StoreUnsafe(ref sums, 24);
        c31.StoreUnsafe(ref sums, 28);
    }

    /// <summary><c>C = A·B</c> over int64, wrapping: each row of C gathers scaled rows of B.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
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

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
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
