using System.Numerics.Tensors;
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
/// exists. Anything else — strided views, broadcasting that is not a scalar, object and
/// string dtypes, the operators with Python semantics such as <c>//</c> and <c>%</c> —
/// returns null and takes the general path, which stays the reference.
/// </para>
/// <para>
/// Accuracy: addition, subtraction, multiplication, division, comparisons, min/max and
/// square roots are exact IEEE operations either way. Transcendental functions
/// (<c>exp</c>, <c>sin</c>, <c>log</c>, <c>power</c>, …) use vectorised implementations
/// that may differ from the scalar <see cref="Math"/> result in the last bit or two, and a
/// vectorised sum adds in a different order than numpy's pairwise sum, so totals can differ
/// in the last digits. Arrays smaller than <see cref="Threshold"/> elements take the exact
/// path, so small results — the ones a program prints — are unchanged.
/// </para>
/// </remarks>
internal static class NdSimd
{
    /// <summary>The fewest elements a fast path is used for.</summary>
    public const int Threshold = 64;

    /// <summary>
    /// One operand of an element loop: a contiguous span over the whole result, or a scalar.
    /// </summary>
    private readonly ref struct Operand<T>
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

    /// <summary>A binary ufunc over contiguous float, int or bool operands, or null.</summary>
    public static NdArray? TryBinary(string name, NdArray x, NdArray y, int[] shape, int n)
    {
        if (n < Threshold || (x.Size == 1 && y.Size == 1) || !Usable(x, shape) || !Usable(y, shape))
        {
            return null;
        }

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
            return IntBinary(name, x, y, shape, n);
        }

        return FloatBinary(name, x, y, shape, n);
    }

    private static NdArray? FloatBinary(string name, NdArray x, NdArray y, int[] shape, int n)
    {
        if (name is not ("add" or "subtract" or "multiply" or "true_divide" or "divide" or "maximum" or "minimum"
            or "fmax" or "fmin" or "power" or "float_power" or "arctan2" or "hypot" or "copysign"))
        {
            return null;
        }

        double[]? xScratch = null;
        double[]? yScratch = null;

        try
        {
            var a = AsDoubles(x, ref xScratch);
            var b = AsDoubles(y, ref yScratch);
            var result = new double[n];
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
                else if (b.IsScalar && b.Scalar == 2.0) TensorPrimitives.Multiply(a.Span, a.Span, d);
                else if (b.IsScalar) TensorPrimitives.Pow(a.Span, b.Scalar, d);
                else TensorPrimitives.Pow(a.Span, b.Span, d);
                return;

            case "arctan2":
                if (a.IsScalar) TensorPrimitives.Atan2(a.Scalar, b.Span, d);
                else if (b.IsScalar) TensorPrimitives.Atan2(a.Span, b.Scalar, d);
                else TensorPrimitives.Atan2(a.Span, b.Span, d);
                return;

            case "hypot":
                Fill(a, b, d, static (x, y, r) => TensorPrimitives.Hypot(x, y, r));
                return;

            default:
                Fill(a, b, d, static (x, y, r) => TensorPrimitives.CopySign(x, y, r));
                return;
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
    private static NdArray? IntBinary(string name, NdArray x, NdArray y, int[] shape, int n)
    {
        var a = AsLongs(x);
        var b = AsLongs(y);
        var d = new long[n];

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

            case "multiply":
                if (a.IsScalar) TensorPrimitives.Multiply(b.Span, a.Scalar, d);
                else if (b.IsScalar) TensorPrimitives.Multiply(a.Span, b.Scalar, d);
                else TensorPrimitives.Multiply(a.Span, b.Span, d);
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

    /// <summary><c>&amp;</c>, <c>|</c>, <c>^</c> and the logical forms over bool arrays: bytes, 32 at a time.</summary>
    private static NdArray? BoolBinary(string name, NdArray x, NdArray y, int[] shape, int n)
    {
        if (x.Size == 1 || y.Size == 1)
        {
            return null;
        }

        var a = MemoryMarshal.Cast<bool, byte>(Bools(x));
        var b = MemoryMarshal.Cast<bool, byte>(Bools(y));
        var result = new bool[n];
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
        var result = new bool[n];

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
    private static void Compare<T>(string name, Operand<T> a, Operand<T> b, Span<bool> result)
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

                for (var lane = 0; lane < width; lane++)
                {
                    result[i + lane] = ((bits >> lane) & 1) != 0;
                }
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

    // ---- unary ----

    /// <summary>A float-valued unary ufunc over a contiguous float or int array, or null.</summary>
    public static NdArray? TryUnary(string name, NdArray x)
    {
        if (x.Size < Threshold || !x.IsContiguous || x.Kind is not (DKind.Float or DKind.Int))
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

        double[]? scratch = null;

        try
        {
            var input = AsDoubles(x, ref scratch).Span;
            var d = new double[x.Size];

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
