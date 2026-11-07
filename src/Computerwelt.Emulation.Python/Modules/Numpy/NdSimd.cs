using System.Numerics.Tensors;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Computerwelt.Emulation.Python.Modules.Numpy;

/// <summary>
/// Vectorised fast paths for the element loops numpy programs spend their time in.
/// </summary>
/// <remarks>
/// <para>
/// The general loops in <see cref="Ufuncs"/> and <see cref="Reductions"/> handle every
/// layout and dtype by asking each element for its position and its value; that costs a
/// dtype switch and an operator switch per element. Here an operand that is a contiguous
/// run of the result's shape — or a single scalar broadcast over it — is handed to
/// <see cref="TensorPrimitives"/>, which processes it with SIMD instructions (AVX2/AVX-512
/// on x64, AdvSimd on Arm), or to a <see cref="Vector256{T}"/> loop where no primitive
/// exists. <c>//</c>, <c>%</c> and <c>fmod</c> have no vector form that keeps Python's
/// sign rules, so they get typed loops over the exact scalar rule instead. Anything else —
/// strided views, broadcasting that is not a scalar, object and string dtypes — returns
/// null and takes the general path, which stays the reference.
/// </para>
/// <para>
/// Accuracy: addition, subtraction, multiplication, division, comparisons, min/max and
/// square roots are exact IEEE operations either way. Transcendental functions
/// (<c>exp</c>, <c>sin</c>, <c>log</c>, …) use vectorised implementations
/// that may differ from the scalar <see cref="Math"/> result in the last bit or two, and a
/// vectorised sum adds in a different order than numpy's pairwise sum, so totals can differ
/// in the last digits. Arrays smaller than <see cref="Threshold"/> elements take the exact
/// path, so small results — the ones a program prints — are unchanged.
/// </para>
/// </remarks>
internal static partial class NdSimd
{
    /// <summary>The fewest elements a fast path is used for.</summary>
    public const int Threshold = 64;

    /// <summary>
    /// One operand of an element loop: a contiguous span over the whole result, or a scalar.
    /// </summary>
    internal readonly ref struct Operand<T>
    {
        public Operand(ReadOnlySpan<T> span)
        {
            Span = span;
            IsScalar = false;
            Scalar = default!;
        }

        public Operand(T scalar)
        {
            Span = default;
            IsScalar = true;
            Scalar = scalar;
        }

        public ReadOnlySpan<T> Span { get; }

        public bool IsScalar { get; }

        public T Scalar { get; }

        /// <summary>The value at <paramref name="i"/> — the scalar wherever it is broadcast.</summary>
        public T this[int i] => IsScalar ? Scalar : Span[i];
    }

    private static bool Covers(NdArray array, int[] shape) =>
        array.IsContiguous && array.Shape.AsSpan().SequenceEqual(shape);

    /// <summary>Whether <paramref name="array"/> can feed a fast loop producing <paramref name="shape"/>.</summary>
    private static bool Usable(NdArray array, int[] shape) => array.Size == 1 || Covers(array, shape);

    private static ReadOnlySpan<double> Doubles(NdArray array) => array.Buffer.F.AsSpan(array.Offset, array.Size);

    private static ReadOnlySpan<long> Longs(NdArray array) => array.Buffer.I.AsSpan(array.Offset, array.Size);

    private static ReadOnlySpan<bool> Bools(NdArray array) => array.Buffer.B.AsSpan(array.Offset, array.Size);

    /// <summary>A float view of a float or int operand; an int one is converted into <paramref name="scratch"/>.</summary>
    private static Operand<double> AsDoubles(NdArray array, ref double[]? scratch)
    {
        if (array.Size == 1)
        {
            return new Operand<double>(array.GetDouble(array.Offset));
        }

        if (array.Kind == DKind.Float)
        {
            return new Operand<double>(Doubles(array));
        }

        scratch = System.Buffers.ArrayPool<double>.Shared.Rent(array.Size);
        var converted = scratch.AsSpan(0, array.Size);
        TensorPrimitives.ConvertTruncating<long, double>(Longs(array), converted);
        return new Operand<double>(converted);
    }

    private static Operand<long> AsLongs(NdArray array) =>
        array.Size == 1 ? new Operand<long>(array.GetLong(array.Offset)) : new Operand<long>(Longs(array));

    // ---- binary ----

    /// <summary>
    /// The strides of a view that is not row-major but still covers a dense block of its
    /// buffer — every element once, no gaps — in some order of its axes (a transpose, a
    /// Fortran-ordered array); null otherwise. Strides here count elements.
    /// </summary>
    private static int[]? DenseLayout(NdArray array)
    {
        var order = Enumerable.Range(0, array.NDim).Where(d => array.Shape[d] != 1).OrderBy(d => array.Strides[d]).ToArray();
        var expected = 1;

        foreach (var d in order)
        {
            if (array.Strides[d] != expected)
            {
                return null;
            }

            expected *= array.Shape[d];
        }

        return (int[])array.Strides.Clone();
    }

    /// <summary>The common dense layout of two operands of the result's shape (or one beside a scalar), when it is not row-major; else null.</summary>
    private static int[]? SharedLayout(NdArray x, NdArray y, int[] shape)
    {
        var full = x.Size == 1 ? y : x;

        if (full.IsContiguous || !full.Shape.AsSpan().SequenceEqual(shape) || DenseLayout(full) is not { } strides)
        {
            return null;
        }

        var other = ReferenceEquals(full, x) ? y : x;
        var matches = other.Size == 1 || (other.Shape.AsSpan().SequenceEqual(shape) && other.Strides.AsSpan().SequenceEqual(strides));
        return matches ? strides : null;
    }

    /// <summary>A dense operand's memory as a one-dimensional contiguous view; a scalar stays as it is.</summary>
    private static NdArray Flat(NdArray array) => array.Size == 1 ? array : array.View([array.Size], [1], array.Offset);

    /// <summary>
    /// A strided or broadcast operand gathered into a new contiguous array of
    /// <paramref name="shape"/>, so the vector loops can take it: a transpose, a slice with a
    /// step, a row vector spread over a matrix (<c>x - x.mean(axis=0)</c>).
    /// </summary>
    /// <remarks>
    /// One gather of n elements, then a vector loop, instead of the general loop's
    /// per-element dtype and operator dispatch. The copy is no larger than the result the
    /// operation allocates anyway, and the shape has already passed the size cap.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static NdArray Dense(NdArray array, int[] shape)
    {
        var view = array.BroadcastTo(shape);
        var target = (int[])shape.Clone();

        switch (view.Kind)
        {
            case DKind.Float:
            {
                var result = GC.AllocateUninitializedArray<double>(view.Size);
                Gather(view, view.Buffer.F!, result);
                return NdArray.FromDoubles(result, target);
            }

            case DKind.Int:
            {
                var result = GC.AllocateUninitializedArray<long>(view.Size);
                Gather(view, view.Buffer.I!, result);
                return NdArray.FromLongs(result, target);
            }

            default:
            {
                var result = new bool[view.Size];
                Gather(view, view.Buffer.B!, result);
                return NdArray.FromBools(result, target);
            }
        }
    }

    /// <summary>A view's elements in row-major order: an odometer over the outer axes, a strided run along the last.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Gather<T>(NdArray view, T[] source, Span<T> destination)
    {
        var dims = view.NDim;
        var length = view.Shape[dims - 1];
        var stride = view.Strides[dims - 1];
        Span<int> index = stackalloc int[dims];
        var start = view.Offset;
        var written = 0;

        while (written < destination.Length)
        {
            var run = destination.Slice(written, length);

            if (stride == 1)
            {
                source.AsSpan(start, length).CopyTo(run);
            }
            else if (stride == 0)
            {
                run.Fill(source[start]);
            }
            else
            {
                for (int i = 0, at = start; i < run.Length; i++, at += stride)
                {
                    run[i] = source[at];
                }
            }

            written += length;

            // Advance the outer axes, carrying like an odometer.
            for (var d = dims - 2; d >= 0; d--)
            {
                start += view.Strides[d];

                if (++index[d] < view.Shape[d])
                {
                    break;
                }

                start -= view.Strides[d] * view.Shape[d];
                index[d] = 0;
            }
        }
    }

    /// <summary>A binary ufunc over contiguous float, int or bool operands, or null.</summary>
    public static NdArray? TryBinary(string name, NdArray x, NdArray y, int[] shape, int n)
    {
        if (n < Threshold || (x.Size == 1 && y.Size == 1)
            || x.Kind is not (DKind.Float or DKind.Int or DKind.Bool) || y.Kind is not (DKind.Float or DKind.Int or DKind.Bool))
        {
            return null;
        }

        if (Rows(name, x, y, shape, n) is { } byRow)
        {
            return byRow;
        }

        if (SharedLayout(x, y, shape) is { } strides)
        {
            // Both operands (or one, beside a scalar) cover their buffers densely in the same
            // axis order — a transpose, typically. Run over the memory as one flat span and
            // hand the result back in that same layout, as numpy's default order does.
            var flat = TryBinary(name, Flat(x), Flat(y), [n], n);
            return flat?.View((int[])shape.Clone(), strides, 0);
        }

        x = Usable(x, shape) ? x : Dense(x, shape);
        y = Usable(y, shape) ? y : Dense(y, shape);
        var xk = x.Kind;
        var yk = y.Kind;

        if (xk == DKind.Bool && yk == DKind.Bool)
        {
            return BoolBinary(name, x, y, shape, n);
        }

        if (xk is not (DKind.Float or DKind.Int) || yk is not (DKind.Float or DKind.Int))
        {
            return null;
        }

        if (name is "equal" or "not_equal" or "less" or "less_equal" or "greater" or "greater_equal")
        {
            return Comparison(name, x, y, shape, n);
        }

        if (xk == DKind.Int && yk == DKind.Int)
        {
            // Division and the other float-valued functions of two ints are float loops.
            return name is "true_divide" or "divide" or "arctan2" or "hypot" or "copysign" or "float_power"
                ? FloatBinary(name, x, y, shape, n)
                : IntBinary(name, x, y, shape, n);
        }

        return FloatBinary(name, x, y, shape, n);
    }

    private static bool IsFloatBinary(string name) => name is "add" or "subtract" or "multiply" or "true_divide" or "divide"
        or "maximum" or "minimum" or "fmax" or "fmin" or "power" or "float_power" or "arctan2" or "hypot" or "copysign"
        or "floor_divide" or "remainder" or "mod" or "fmod";

    /// <summary>
    /// A float operation between a full contiguous array and a row (<c>x - x.mean(axis=0)</c>)
    /// or a column (<c>x - c[:, None]</c>) broadcast over it, row by row with no copy of the
    /// broadcast operand; null for any other shape.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static NdArray? Rows(string name, NdArray x, NdArray y, int[] shape, int n)
    {
        if (shape.Length < 2 || x.Kind != DKind.Float || y.Kind != DKind.Float || !IsFloatBinary(name))
        {
            return null;
        }

        var xFull = Covers(x, shape);
        var other = xFull ? y : x;

        if ((!xFull && !Covers(y, shape)) || other.Size == 1)
        {
            return null;
        }

        var width = shape[^1];
        var rows = n / width;
        var full = Doubles(xFull ? x : y);
        var result = GC.AllocateUninitializedArray<double>(n);

        if (IsRow(other, shape))
        {
            var row = Doubles(other);

            for (var r = 0; r < rows; r++)
            {
                var range = new Range(r * width, (r + 1) * width);
                var a = new Operand<double>(full[range]);
                var b = new Operand<double>(row);
                Apply(name, xFull ? a : b, xFull ? b : a, result.AsSpan(range));
            }

            return NdArray.FromDoubles(result, (int[])shape.Clone());
        }

        if (IsColumn(other, shape))
        {
            var column = other.IsContiguous ? Doubles(other) : Dense(other, other.Shape).Buffer.F;

            for (var r = 0; r < rows; r++)
            {
                var range = new Range(r * width, (r + 1) * width);
                var a = new Operand<double>(full[range]);
                var b = new Operand<double>(column[r]);
                Apply(name, xFull ? a : b, xFull ? b : a, result.AsSpan(range));
            }

            return NdArray.FromDoubles(result, (int[])shape.Clone());
        }

        return null;
    }

    /// <summary>A contiguous operand whose shape, leading ones aside, is the last axis of <paramref name="shape"/>.</summary>
    private static bool IsRow(NdArray array, int[] shape) =>
        array.IsContiguous && array.Size == shape[^1] && array.Shape.Length > 0 && array.Shape[^1] == shape[^1];

    /// <summary>An operand of <paramref name="shape"/> with the last axis 1: one value per row.</summary>
    private static bool IsColumn(NdArray array, int[] shape) =>
        array.NDim == shape.Length && array.Shape[^1] == 1 && array.Shape.AsSpan(0, shape.Length - 1).SequenceEqual(shape.AsSpan(0, shape.Length - 1));

    private static NdArray? FloatBinary(string name, NdArray x, NdArray y, int[] shape, int n)
    {
        if (!IsFloatBinary(name))
        {
            return null;
        }

        double[]? xScratch = null;
        double[]? yScratch = null;

        try
        {
            var a = AsDoubles(x, ref xScratch);
            var b = AsDoubles(y, ref yScratch);
            var result = GC.AllocateUninitializedArray<double>(n);
            Apply(name, a, b, result);
            return NdArray.FromDoubles(result, shape);
        }
        finally
        {
            ReturnScratch(xScratch);
            ReturnScratch(yScratch);
        }
    }

    private static void ReturnScratch(double[]? scratch)
    {
        if (scratch is not null)
        {
            System.Buffers.ArrayPool<double>.Shared.Return(scratch);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Apply(string name, Operand<double> a, Operand<double> b, Span<double> d)
    {
        switch (name)
        {
            case "add":
                if (a.IsScalar) TensorPrimitives.Add(b.Span, a.Scalar, d);
                else if (b.IsScalar) TensorPrimitives.Add(a.Span, b.Scalar, d);
                else TensorPrimitives.Add(a.Span, b.Span, d);
                return;

            case "subtract":
                if (a.IsScalar) TensorPrimitives.Subtract(a.Scalar, b.Span, d);
                else if (b.IsScalar) TensorPrimitives.Subtract(a.Span, b.Scalar, d);
                else TensorPrimitives.Subtract(a.Span, b.Span, d);
                return;

            case "multiply":
                if (a.IsScalar) TensorPrimitives.Multiply(b.Span, a.Scalar, d);
                else if (b.IsScalar) TensorPrimitives.Multiply(a.Span, b.Scalar, d);
                else TensorPrimitives.Multiply(a.Span, b.Span, d);
                return;

            case "true_divide" or "divide":
                if (a.IsScalar) TensorPrimitives.Divide(a.Scalar, b.Span, d);
                else if (b.IsScalar) TensorPrimitives.Divide(a.Span, b.Scalar, d);
                else TensorPrimitives.Divide(a.Span, b.Span, d);
                return;

            // numpy's maximum/minimum propagate NaN, as IEEE 754-2019 maximum does; fmax/fmin
            // ignore it, as maximumNumber does.
            case "maximum":
                if (a.IsScalar) TensorPrimitives.Max(b.Span, a.Scalar, d);
                else if (b.IsScalar) TensorPrimitives.Max(a.Span, b.Scalar, d);
                else TensorPrimitives.Max(a.Span, b.Span, d);
                return;

            case "minimum":
                if (a.IsScalar) TensorPrimitives.Min(b.Span, a.Scalar, d);
                else if (b.IsScalar) TensorPrimitives.Min(a.Span, b.Scalar, d);
                else TensorPrimitives.Min(a.Span, b.Span, d);
                return;

            case "fmax":
                if (a.IsScalar) TensorPrimitives.MaxNumber(b.Span, a.Scalar, d);
                else if (b.IsScalar) TensorPrimitives.MaxNumber(a.Span, b.Scalar, d);
                else TensorPrimitives.MaxNumber(a.Span, b.Span, d);
                return;

            case "fmin":
                if (a.IsScalar) TensorPrimitives.MinNumber(b.Span, a.Scalar, d);
                else if (b.IsScalar) TensorPrimitives.MinNumber(a.Span, b.Scalar, d);
                else TensorPrimitives.MinNumber(a.Span, b.Span, d);
                return;

            case "power" or "float_power":
                if (a.IsScalar) TensorPrimitives.Pow(a.Scalar, b.Span, d);
                else if (b.IsScalar) PowerOfScalar(a.Span, b.Scalar, d);
                else TensorPrimitives.Pow(a.Span, b.Span, d);
                return;

            case "arctan2":
                if (a.IsScalar) TensorPrimitives.Atan2(a.Scalar, b.Span, d);
                else if (b.IsScalar) TensorPrimitives.Atan2(a.Span, b.Scalar, d);
                else TensorPrimitives.Atan2(a.Span, b.Span, d);
                return;

            // Python's floor division and remainder have no vector form that keeps their
            // sign and rounding rules, so these are typed loops over the exact scalar rule —
            // still a long way from the general loop's per-element dispatch.
            case "floor_divide":
                for (var i = 0; i < d.Length; i++) d[i] = Ufuncs.DivMod(a[i], b[i]).Floor;
                return;

            case "remainder" or "mod":
                for (var i = 0; i < d.Length; i++) d[i] = Ufuncs.DivMod(a[i], b[i]).Mod;
                return;

            case "fmod":
                for (var i = 0; i < d.Length; i++) d[i] = a[i] % b[i];
                return;

            case "hypot":
                Fill(a, b, d, static (x, y, r) => TensorPrimitives.Hypot(x, y, r));
                return;

            default:
                Fill(a, b, d, static (x, y, r) => TensorPrimitives.CopySign(x, y, r));
                return;
        }
    }

    /// <summary>
    /// <c>x ** y</c> for one exponent: the exponents numpy itself special-cases are a
    /// multiply, a square root or a reciprocal; any other is the exact pow.
    /// </summary>
    /// <remarks>
    /// <c>exp(y·log x)</c> through the vector exp and log was measured at about 1.8 times
    /// faster, and rejected: it turns exact answers into near misses that print
    /// (<c>4.0 ** 1.5</c> as <c>7.999999999999998</c>).
    /// </remarks>
    private static void PowerOfScalar(ReadOnlySpan<double> x, double y, Span<double> d)
    {
        switch (y)
        {
            case 2.0: TensorPrimitives.Multiply(x, x, d); return;
            case 0.5: TensorPrimitives.Sqrt(x, d); return;
            case 1.0: x.CopyTo(d); return;
            case -1.0: TensorPrimitives.Divide(1.0, x, d); return;
            default: TensorPrimitives.Pow(x, y, d); return;
        }
    }

    private delegate void SpanOperation(ReadOnlySpan<double> x, ReadOnlySpan<double> y, Span<double> result);

    /// <summary>A two-span primitive with a scalar operand spread over a rented span first.</summary>
    private static void Fill(Operand<double> a, Operand<double> b, Span<double> d, SpanOperation operation)
    {
        if (!a.IsScalar && !b.IsScalar)
        {
            operation(a.Span, b.Span, d);
            return;
        }

        var spread = System.Buffers.ArrayPool<double>.Shared.Rent(d.Length);

        try
        {
            var filled = spread.AsSpan(0, d.Length);
            filled.Fill(a.IsScalar ? a.Scalar : b.Scalar);
            operation(a.IsScalar ? filled : a.Span, b.IsScalar ? filled : b.Span, d);
        }
        finally
        {
            System.Buffers.ArrayPool<double>.Shared.Return(spread);
        }
    }

    /// <summary>
    /// The int64 operators whose numpy result is the wrapping machine result — so a SIMD
    /// add is the same answer. Division, floor division, modulo and power keep their loops.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static NdArray? IntBinary(string name, NdArray x, NdArray y, int[] shape, int n)
    {
        if (name is not ("add" or "subtract" or "multiply" or "maximum" or "fmax" or "minimum" or "fmin"
            or "bitwise_and" or "bitwise_or" or "bitwise_xor" or "floor_divide" or "remainder" or "mod" or "fmod"))
        {
            return null;
        }

        var a = AsLongs(x);
        var b = AsLongs(y);
        var d = GC.AllocateUninitializedArray<long>(n);

        switch (name)
        {
            case "add":
                if (a.IsScalar) TensorPrimitives.Add(b.Span, a.Scalar, d);
                else if (b.IsScalar) TensorPrimitives.Add(a.Span, b.Scalar, d);
                else TensorPrimitives.Add(a.Span, b.Span, d);
                break;

            case "subtract":
                if (a.IsScalar) TensorPrimitives.Subtract(a.Scalar, b.Span, d);
                else if (b.IsScalar) TensorPrimitives.Subtract(a.Span, b.Scalar, d);
                else TensorPrimitives.Subtract(a.Span, b.Span, d);
                break;

            // TensorPrimitives' long-times-scalar form is several times slower than its
            // span-span form, so the scalar case is a Vector256 loop of its own.
            case "multiply" when a.IsScalar || b.IsScalar:
                MultiplyByScalar(a.IsScalar ? b.Span : a.Span, a.IsScalar ? a.Scalar : b.Scalar, d);
                break;

            case "multiply":
                TensorPrimitives.Multiply(a.Span, b.Span, d);
                break;

            case "maximum" or "fmax":
                if (a.IsScalar) TensorPrimitives.Max(b.Span, a.Scalar, d);
                else if (b.IsScalar) TensorPrimitives.Max(a.Span, b.Scalar, d);
                else TensorPrimitives.Max(a.Span, b.Span, d);
                break;

            case "minimum" or "fmin":
                if (a.IsScalar) TensorPrimitives.Min(b.Span, a.Scalar, d);
                else if (b.IsScalar) TensorPrimitives.Min(a.Span, b.Scalar, d);
                else TensorPrimitives.Min(a.Span, b.Span, d);
                break;

            case "bitwise_and":
                if (a.IsScalar) TensorPrimitives.BitwiseAnd(b.Span, a.Scalar, d);
                else if (b.IsScalar) TensorPrimitives.BitwiseAnd(a.Span, b.Scalar, d);
                else TensorPrimitives.BitwiseAnd(a.Span, b.Span, d);
                break;

            case "bitwise_or":
                if (a.IsScalar) TensorPrimitives.BitwiseOr(b.Span, a.Scalar, d);
                else if (b.IsScalar) TensorPrimitives.BitwiseOr(a.Span, b.Scalar, d);
                else TensorPrimitives.BitwiseOr(a.Span, b.Span, d);
                break;

            case "floor_divide":
                for (var i = 0; i < d.Length; i++) d[i] = Ufuncs.FloorDivide(a[i], b[i]);
                break;

            case "remainder" or "mod":
                for (var i = 0; i < d.Length; i++) d[i] = Ufuncs.Remainder(a[i], b[i]);
                break;

            case "fmod":
                for (var i = 0; i < d.Length; i++) d[i] = Ufuncs.Fmod(a[i], b[i]);
                break;

            case "bitwise_xor":
                if (a.IsScalar) TensorPrimitives.Xor(b.Span, a.Scalar, d);
                else if (b.IsScalar) TensorPrimitives.Xor(a.Span, b.Scalar, d);
                else TensorPrimitives.Xor(a.Span, b.Span, d);
                break;

            default:
                return null;
        }

        return NdArray.FromLongs(d, shape);
    }

    /// <summary>Wrapping int64 multiplication by one factor.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void MultiplyByScalar(ReadOnlySpan<long> values, long factor, Span<long> destination)
    {
        var i = 0;

        if (Vector256.IsHardwareAccelerated)
        {
            var f = Vector256.Create(factor);

            for (; i <= values.Length - Vector256<long>.Count; i += Vector256<long>.Count)
            {
                (Vector256.Create(values.Slice(i, Vector256<long>.Count)) * f).CopyTo(destination[i..]);
            }
        }

        for (; i < values.Length; i++)
        {
            destination[i] = unchecked(values[i] * factor);
        }
    }

    /// <summary><c>&amp;</c>, <c>|</c>, <c>^</c> and the logical forms over bool arrays: bytes, 32 at a time.</summary>
    private static NdArray? BoolBinary(string name, NdArray x, NdArray y, int[] shape, int n)
    {
        if (x.Size == 1 || y.Size == 1)
        {
            return null;
        }

        var a = MemoryMarshal.Cast<bool, byte>(Bools(x));
        var b = MemoryMarshal.Cast<bool, byte>(Bools(y));
        var result = GC.AllocateUninitializedArray<bool>(n);
        var d = MemoryMarshal.Cast<bool, byte>(result.AsSpan());

        switch (name)
        {
            case "bitwise_and" or "logical_and" or "multiply" or "minimum" or "fmin":
                TensorPrimitives.BitwiseAnd(a, b, d);
                break;
            case "bitwise_or" or "logical_or" or "add" or "maximum" or "fmax":
                TensorPrimitives.BitwiseOr(a, b, d);
                break;
            case "bitwise_xor" or "logical_xor":
                TensorPrimitives.Xor(a, b, d);
                break;
            default:
                return null;
        }

        return NdArray.FromBools(result, shape);
    }

    // ---- comparisons ----

    private static NdArray? Comparison(string name, NdArray x, NdArray y, int[] shape, int n)
    {
        var result = GC.AllocateUninitializedArray<bool>(n);

        // Two int arrays compare as integers; anything else as doubles, as numpy's promotion does.
        if (x.Kind == DKind.Int && y.Kind == DKind.Int)
        {
            Compare(name, AsLongs(x), AsLongs(y), result);
            return NdArray.FromBools(result, shape);
        }

        double[]? xScratch = null;
        double[]? yScratch = null;

        try
        {
            Compare(name, AsDoubles(x, ref xScratch), AsDoubles(y, ref yScratch), result);
            return NdArray.FromBools(result, shape);
        }
        finally
        {
            ReturnScratch(xScratch);
            ReturnScratch(yScratch);
        }
    }

    /// <summary>
    /// Compares element-wise into a bool array: <see cref="Vector256{T}"/> compares produce a
    /// lane mask, whose most significant bits become the bools. IEEE semantics either way —
    /// NaN is unequal to everything and unordered — exactly as the scalar loop.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void Compare<T>(string name, Operand<T> a, Operand<T> b, Span<bool> result)
        where T : unmanaged, System.Numerics.INumber<T>
    {
        var op = name switch
        {
            "equal" => 0,
            "not_equal" => 1,
            "less" => 2,
            "less_equal" => 3,
            "greater" => 4,
            _ => 5,
        };

        var n = result.Length;
        var i = 0;

        if (Vector256.IsHardwareAccelerated && Vector256<T>.IsSupported)
        {
            var width = Vector256<T>.Count;
            var scalarA = Vector256.Create(a.IsScalar ? a.Scalar : default);
            var scalarB = Vector256.Create(b.IsScalar ? b.Scalar : default);

            for (; i <= n - width; i += width)
            {
                var va = a.IsScalar ? scalarA : Vector256.Create(a.Span.Slice(i, width));
                var vb = b.IsScalar ? scalarB : Vector256.Create(b.Span.Slice(i, width));

                var mask = op switch
                {
                    0 => Vector256.Equals(va, vb),
                    1 => ~Vector256.Equals(va, vb),
                    2 => Vector256.LessThan(va, vb),
                    3 => Vector256.LessThanOrEqual(va, vb),
                    4 => Vector256.GreaterThan(va, vb),
                    _ => Vector256.GreaterThanOrEqual(va, vb),
                };

                var bits = mask.ExtractMostSignificantBits();
                StoreBools(result.Slice(i, width), bits);
            }
        }

        for (; i < n; i++)
        {
            var va = a.IsScalar ? a.Scalar : a.Span[i];
            var vb = b.IsScalar ? b.Scalar : b.Span[i];

            result[i] = op switch
            {
                0 => va == vb,
                1 => va != vb,
                2 => va < vb,
                3 => va <= vb,
                4 => va > vb,
                _ => va >= vb,
            };
        }
    }

    /// <summary>
    /// Every 4-bit lane mask as four bool bytes, so a compare of four lanes is stored as one
    /// 32-bit write instead of four branches.
    /// </summary>
    private static readonly uint[] LaneBytes = BuildLaneBytes();

    private static uint[] BuildLaneBytes()
    {
        var table = new uint[16];

        for (var bits = 0; bits < 16; bits++)
        {
            uint word = 0;

            for (var lane = 0; lane < 4; lane++)
            {
                if (((bits >> lane) & 1) != 0)
                {
                    word |= 1u << (lane * 8);
                }
            }

            table[bits] = word;
        }

        return table;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void StoreBools(Span<bool> destination, uint bits)
    {
        var bytes = MemoryMarshal.AsBytes(destination);

        for (var lane = 0; lane < destination.Length; lane += 4)
        {
            System.Runtime.CompilerServices.Unsafe.WriteUnaligned(
                ref bytes[lane],
                BitConverter.IsLittleEndian ? LaneBytes[(bits >> lane) & 0xF] : System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(LaneBytes[(bits >> lane) & 0xF]));
        }
    }

    // ---- unary ----

    /// <summary>A float-valued unary ufunc over a contiguous float or int array, or null.</summary>
    public static NdArray? TryUnary(string name, NdArray x)
    {
        if (x.Size < Threshold || x.Kind is not (DKind.Float or DKind.Int))
        {
            return null;
        }

        // The int functions that stay int (negative, abs, square, …) keep their own loop: the
        // vectorised absolute value refuses long.MinValue where numpy wraps.
        if (x.Kind == DKind.Int && name is "negative" or "positive" or "absolute" or "abs" or "sign" or "square"
            or "invert" or "bitwise_not" or "reciprocal")
        {
            return null;
        }

        if (!IsVectorisedUnary(name))
        {
            return null;
        }

        if (!x.IsContiguous && DenseLayout(x) is { } strides)
        {
            var flat = TryUnary(name, Flat(x));
            return flat?.View((int[])x.Shape.Clone(), strides, 0);
        }

        x = x.IsContiguous ? x : Dense(x, x.Shape);
        double[]? scratch = null;

        try
        {
            var input = AsDoubles(x, ref scratch).Span;
            var d = GC.AllocateUninitializedArray<double>(x.Size);

            switch (name)
            {
                case "negative": TensorPrimitives.Negate(input, d); break;
                case "positive": input.CopyTo(d); break;
                case "absolute" or "abs" or "fabs": TensorPrimitives.Abs(input, d); break;
                case "square": TensorPrimitives.Multiply(input, input, d); break;
                case "sqrt": TensorPrimitives.Sqrt(input, d); break;
                case "cbrt": TensorPrimitives.Cbrt(input, d); break;
                case "exp": TensorPrimitives.Exp(input, d); break;
                case "exp2": TensorPrimitives.Exp2(input, d); break;
                case "expm1": TensorPrimitives.ExpM1(input, d); break;
                case "log": TensorPrimitives.Log(input, d); break;
                case "log2": TensorPrimitives.Log2(input, d); break;
                case "log10": TensorPrimitives.Log10(input, d); break;
                case "log1p": TensorPrimitives.LogP1(input, d); break;
                case "sin": TensorPrimitives.Sin(input, d); break;
                case "cos": TensorPrimitives.Cos(input, d); break;
                case "tan": TensorPrimitives.Tan(input, d); break;
                case "arcsin": TensorPrimitives.Asin(input, d); break;
                case "arccos": TensorPrimitives.Acos(input, d); break;
                case "arctan": TensorPrimitives.Atan(input, d); break;
                case "sinh": TensorPrimitives.Sinh(input, d); break;
                case "cosh": TensorPrimitives.Cosh(input, d); break;
                case "tanh": TensorPrimitives.Tanh(input, d); break;
                case "arcsinh": TensorPrimitives.Asinh(input, d); break;
                case "arccosh": TensorPrimitives.Acosh(input, d); break;
                case "arctanh": TensorPrimitives.Atanh(input, d); break;
                case "degrees" or "rad2deg": TensorPrimitives.RadiansToDegrees(input, d); break;
                case "radians" or "deg2rad": TensorPrimitives.DegreesToRadians(input, d); break;
                case "floor": TensorPrimitives.Floor(input, d); break;
                case "ceil": TensorPrimitives.Ceiling(input, d); break;
                case "trunc" or "fix": TensorPrimitives.Truncate(input, d); break;
                case "rint": TensorPrimitives.Round(input, d); break;
                default: TensorPrimitives.Reciprocal(input, d); break;
            }

            return NdArray.FromDoubles(d, (int[])x.Shape.Clone());
        }
        finally
        {
            ReturnScratch(scratch);
        }
    }

    private static bool IsVectorisedUnary(string name) => name is "negative" or "positive" or "absolute" or "abs"
        or "fabs" or "square" or "sqrt" or "cbrt" or "exp" or "exp2" or "expm1" or "log" or "log2" or "log10"
        or "log1p" or "sin" or "cos" or "tan" or "arcsin" or "arccos" or "arctan" or "sinh" or "cosh" or "tanh"
        or "arcsinh" or "arccosh" or "arctanh" or "degrees" or "rad2deg" or "radians" or "deg2rad" or "floor"
        or "ceil" or "trunc" or "fix" or "rint" or "reciprocal";
}
