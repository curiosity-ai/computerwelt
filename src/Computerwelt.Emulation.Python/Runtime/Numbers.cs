using System.Numerics;

namespace Computerwelt.Emulation.Python.Runtime;

/// <summary>
/// The numeric hash every number type shares.
/// </summary>
/// <remarks>
/// Python guarantees that numbers which compare equal hash equally, across types and at
/// every magnitude: <c>hash(2**100) == hash(2.0**100)</c> must hold, or the two could not
/// be the same dict key. CPython gets that by hashing the exact mathematical value modulo
/// the Mersenne prime 2**61-1 rather than by hashing a representation, and this reproduces
/// that arithmetic.
/// </remarks>
public static class Numbers
{
    /// <summary>The modulus, 2**61-1.</summary>
    private const long Modulus = (1L << 61) - 1;

    /// <summary>Hashes an integer.</summary>
    public static BigInteger Hash(BigInteger value)
    {
        var reduced = BigInteger.Remainder(value, Modulus);

        // -1 is reserved as the "error" hash in CPython, so a value that lands there is
        // reported as -2 instead.
        return reduced == BigInteger.MinusOne ? -2 : reduced;
    }

    /// <summary>
    /// Converts an integer to the nearest float, rounding halves to even, or raises the
    /// <c>OverflowError</c> CPython does for one too large to represent.
    /// </summary>
    /// <remarks>
    /// <c>(double)BigInteger</c> truncates, which makes <c>float(2**53 + 1)</c> and every
    /// large mixed-type operation a unit in the last place off from CPython's.
    /// </remarks>
    public static double ToDouble(BigInteger value)
    {
        if (value.IsZero)
        {
            return 0.0;
        }

        var magnitude = BigInteger.Abs(value);
        var bits = (long)magnitude.GetBitLength();

        if (bits <= 53)
        {
            return value.Sign * (double)magnitude;
        }

        var shift = (int)(bits - 53);
        var quotient = magnitude >> shift;
        var remainder = magnitude - (quotient << shift);
        var half = BigInteger.One << (shift - 1);

        if (remainder > half || (remainder == half && !quotient.IsEven))
        {
            quotient += BigInteger.One;
        }

        var result = Math.ScaleB((double)quotient, shift);

        return double.IsInfinity(result)
            ? throw new PyRaise(new PyException(
                PyExceptionType.OverflowError, "int too large to convert to float"))
            : value.Sign * result;
    }

    /// <summary>
    /// <see cref="ToDouble"/>, answering an infinity instead of raising for an integer too
    /// large — for the callers that only need the float when the other operand is one.
    /// </summary>
    public static double ToDoubleOrInfinity(BigInteger value)
    {
        try
        {
            return ToDouble(value);
        }
        catch (PyRaise)
        {
            return value.Sign * double.PositiveInfinity;
        }
    }

    /// <summary>
    /// <c>left / right</c> for two integers, correctly rounded however large they are.
    /// </summary>
    /// <remarks>
    /// Dividing the two converted floats is wrong twice over: each conversion rounds, and a
    /// quotient well inside the float range overflows when its operands do not fit.
    /// </remarks>
    public static double TrueDivide(BigInteger left, BigInteger right)
    {
        var a = BigInteger.Abs(left);
        var b = BigInteger.Abs(right);
        var sign = left.Sign * right.Sign;
        var exact = BigInteger.One << 53;

        if (a < exact && b < exact)
        {
            return (double)left / (double)right;
        }

        if (a.IsZero)
        {
            return 0.0;
        }

        // Scale so the integer quotient carries at least 55 significant bits, then fold the
        // remainder into a sticky low bit: the conversion rounds to 53 bits exactly once.
        var shift = 55 - ((long)a.GetBitLength() - (long)b.GetBitLength());
        var quotient = shift >= 0
            ? BigInteger.DivRem(a << (int)shift, b, out var remainder)
            : BigInteger.DivRem(a, b << (int)-shift, out remainder);

        if (!remainder.IsZero)
        {
            quotient |= BigInteger.One;
        }

        var result = Math.ScaleB(ToDouble(quotient), (int)-shift);

        return double.IsInfinity(result)
            ? throw new PyRaise(new PyException(
                PyExceptionType.OverflowError, "integer division result too large for a float"))
            : sign * result;
    }

    /// <summary>Hashes a float, exactly as the equal integer would hash.</summary>
    public static BigInteger Hash(double value)
    {
        if (double.IsNaN(value))
        {
            // Every NaN hashes alike here; CPython uses the object's address, which a
            // sandbox has no business exposing.
            return 0;
        }

        if (double.IsInfinity(value))
        {
            return value > 0 ? 314159 : -314159;
        }

        var sign = value < 0 ? -1 : 1;
        var magnitude = Math.Abs(value);

        // Split into mantissa and exponent, then fold the mantissa into the modular
        // accumulator 28 bits at a time — the same loop CPython runs.
        var exponent = 0;

        while (magnitude != 0 && magnitude < 0.5)
        {
            magnitude *= 2;
            exponent--;
        }

        while (magnitude >= 1)
        {
            magnitude /= 2;
            exponent++;
        }

        BigInteger accumulated = 0;

        while (magnitude != 0)
        {
            accumulated = ((accumulated << 28) & Modulus) | (accumulated >> 33);
            magnitude *= 268435456.0;
            exponent -= 28;

            var digits = (long)magnitude;
            magnitude -= digits;
            accumulated += digits;

            if (accumulated >= Modulus)
            {
                accumulated -= Modulus;
            }
        }

        // Rotating by the exponent is multiplication by a power of two modulo 2**61-1,
        // because 2**61 is congruent to 1.
        var shift = exponent >= 0 ? exponent % 61 : 61 - (-exponent % 61);
        accumulated = ((accumulated << shift) & Modulus) | (accumulated >> (61 - shift));

        accumulated *= sign;
        return accumulated == BigInteger.MinusOne ? -2 : accumulated;
    }
}
