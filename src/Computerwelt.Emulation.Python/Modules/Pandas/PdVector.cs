using System.Numerics.Tensors;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Computerwelt.Emulation.Python.Modules.Numpy;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules.Pandas;

/// <summary>
/// Vectorised column operators: arithmetic, comparison and the logical operators over
/// unboxed int64, float64 and bool columns.
/// </summary>
/// <remarks>
/// Each operator returns null for anything it does not cover — object, datetime and string
/// columns, the operators with Python rounding rules (<c>//</c>, <c>%</c>, <c>**</c>) —
/// which then takes the boxed loop in <see cref="PdOps"/>. What it does cover is exact:
/// <c>+</c>, <c>-</c>, <c>*</c>, <c>/</c> and comparisons are single IEEE operations
/// whichever way they are computed, and int64 arithmetic wraps as numpy's does.
/// </remarks>
internal static class PdVector
{
    /// <summary>Whether a position list is <c>0..count-1</c>, which takes nothing a column does not already hold.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static bool IsIdentity(int[] positions, int count)
    {
        if (positions.Length != count)
        {
            return false;
        }

        for (var i = 0; i < positions.Length; i++)
        {
            if (positions[i] != i)
            {
                return false;
            }
        }

        return true;
    }

    private static bool Plain(PdOps.Operand operand) => operand.Type is DType.Float64 or DType.Int64;

    /// <summary>One side as doubles: a span over the column (converted when int), or a scalar.</summary>
    private static ReadOnlySpan<double> Doubles(PdOps.Operand operand, out double scalar, out bool isScalar)
    {
        scalar = 0;
        isScalar = operand.Column is null;

        if (isScalar)
        {
            scalar = Pd.ToDouble(operand.Scalar!);
            return default;
        }

        var column = operand.Column!;
        return column.Type == DType.Float64 ? column.FloatArray : column.Doubles();
    }

    private static ReadOnlySpan<long> Longs(PdOps.Operand operand, out long scalar, out bool isScalar)
    {
        scalar = 0;
        isScalar = operand.Column is null;

        if (isScalar)
        {
            scalar = (long)((PyInt)operand.Scalar!).Value;
            return default;
        }

        return operand.Column!.LongArray;
    }

    /// <summary><c>+ - * /</c> over int64 and float64 operands, or null.</summary>
    public static PdColumn? Arithmetic(string op, PdOps.Operand left, PdOps.Operand right, int count)
    {
        if (op is not ("+" or "-" or "*" or "/") || !Plain(left) || !Plain(right) || (left.Column is null && right.Column is null))
        {
            return null;
        }

        if (left.Type == DType.Int64 && right.Type == DType.Int64 && op != "/")
        {
            var a = Longs(left, out var sa, out var leftScalar);
            var b = Longs(right, out var sb, out var rightScalar);
            var longs = GC.AllocateUninitializedArray<long>(count);

            // A scalar operand is spread over the destination first: the span-span primitives
            // are the fast ones, and they allow the destination to be one of the inputs.
            if (leftScalar)
            {
                Array.Fill(longs, sa);
                a = longs;
            }
            else if (rightScalar)
            {
                Array.Fill(longs, sb);
                b = longs;
            }

            switch (op)
            {
                case "+": TensorPrimitives.Add(a, b, longs); break;
                case "-": TensorPrimitives.Subtract(a, b, longs); break;
                default: TensorPrimitives.Multiply(a, b, longs); break;
            }

            return PdColumn.Ints(longs);
        }

        var x = Doubles(left, out var sx, out var xScalar);
        var y = Doubles(right, out var sy, out var yScalar);
        var result = GC.AllocateUninitializedArray<double>(count);

        switch (op)
        {
            case "+":
                if (xScalar) TensorPrimitives.Add(y, sx, result);
                else if (yScalar) TensorPrimitives.Add(x, sy, result);
                else TensorPrimitives.Add(x, y, result);
                break;
            case "-":
                if (xScalar) TensorPrimitives.Subtract(sx, y, result);
                else if (yScalar) TensorPrimitives.Subtract(x, sy, result);
                else TensorPrimitives.Subtract(x, y, result);
                break;
            case "*":
                if (xScalar) TensorPrimitives.Multiply(y, sx, result);
                else if (yScalar) TensorPrimitives.Multiply(x, sy, result);
                else TensorPrimitives.Multiply(x, y, result);
                break;
            default:
                if (xScalar) TensorPrimitives.Divide(sx, y, result);
                else if (yScalar) TensorPrimitives.Divide(x, sy, result);
                else TensorPrimitives.Divide(x, y, result);
                break;
        }

        return PdColumn.Floats(result);
    }

    /// <summary>A comparison over int64 and float64 operands, or null. NaN compares false, and true for <c>!=</c>.</summary>
    /// <remarks>numpy's vector comparison, whose IEEE semantics are pandas' rule for a missing value.</remarks>
    public static PdColumn? Compare(string op, PdOps.Operand left, PdOps.Operand right, int count)
    {
        if (!Plain(left) || !Plain(right) || (left.Column is null && right.Column is null))
        {
            return null;
        }

        var name = op switch
        {
            "==" => "equal",
            "!=" => "not_equal",
            "<" => "less",
            "<=" => "less_equal",
            ">" => "greater",
            _ => "greater_equal",
        };

        var result = new bool[count];

        if (left.Type == DType.Int64 && right.Type == DType.Int64)
        {
            var a = Longs(left, out var sa, out var aScalar);
            var b = Longs(right, out var sb, out var bScalar);
            NdSimd.Compare(
                name,
                aScalar ? new NdSimd.Operand<long>(sa) : new NdSimd.Operand<long>(a),
                bScalar ? new NdSimd.Operand<long>(sb) : new NdSimd.Operand<long>(b),
                result);
            return PdColumn.Bools(result);
        }

        var x = Doubles(left, out var sx, out var xScalar);
        var y = Doubles(right, out var sy, out var yScalar);
        NdSimd.Compare(
            name,
            xScalar ? new NdSimd.Operand<double>(sx) : new NdSimd.Operand<double>(x),
            yScalar ? new NdSimd.Operand<double>(sy) : new NdSimd.Operand<double>(y),
            result);
        return PdColumn.Bools(result);
    }

    /// <summary><c>&amp; | ^</c> over two bool columns, as bytes, or null.</summary>
    public static PdColumn? Logical(string op, PdOps.Operand left, PdOps.Operand right, int count)
    {
        if (left.Column is not { Type: DType.Bool } x || right.Column is not { Type: DType.Bool } y)
        {
            return null;
        }

        var result = new bool[count];
        var a = MemoryMarshal.AsBytes(x.BoolArray.AsSpan());
        var b = MemoryMarshal.AsBytes(y.BoolArray.AsSpan());
        var d = MemoryMarshal.AsBytes(result.AsSpan());

        switch (op)
        {
            case "&": TensorPrimitives.BitwiseAnd(a, b, d); break;
            case "|": TensorPrimitives.BitwiseOr(a, b, d); break;
            default: TensorPrimitives.Xor(a, b, d); break;
        }

        return PdColumn.Bools(result);
    }
}
