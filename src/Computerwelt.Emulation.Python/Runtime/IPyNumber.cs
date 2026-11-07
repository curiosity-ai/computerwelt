using System.Numerics;

namespace Computerwelt.Emulation.Python.Runtime;

/// <summary>
/// A numeric type a module supplies — <c>decimal.Decimal</c>, <c>fractions.Fraction</c> —
/// that takes part in arithmetic and comparison with the built-in numbers.
/// </summary>
/// <remarks>
/// <para>
/// The built-in operators know <c>int</c> and <c>float</c> and nothing else, and a user
/// class reaches them through its dunders. A host number is neither: it answers for itself
/// here, on either side of an operator, the way CPython's numeric tower answers through
/// <c>__add__</c> and <c>__radd__</c>. A null answer is <c>NotImplemented</c> — the other
/// side is asked, and with neither answering the operator raises its usual
/// <c>TypeError</c>.
/// </para>
/// <para>
/// Ordering and equality stay on <see cref="PyObject.PyCompare"/> and
/// <see cref="PyObject.PyEquals"/>; the operators ask the host number when it is on the right
/// and the built-in on the left did not know it, which is what makes <c>1 &lt; Fraction(3, 2)</c>
/// and <c>1 == Decimal(1)</c> work.
/// </para>
/// </remarks>
public interface IPyNumber
{
    /// <summary>
    /// Applies a binary operator with this value as the left operand, or — when
    /// <paramref name="reflected"/> — as the right one. Null when the pair is not supported.
    /// </summary>
    PyObject? Binary(string op, PyObject other, bool reflected);

    /// <summary>Applies <c>-</c>, <c>+</c> or <c>abs</c>; null for anything else.</summary>
    PyObject? Unary(string op);

    /// <summary><c>float(x)</c>.</summary>
    double ToDouble();

    /// <summary><c>int(x)</c> and <c>math.trunc(x)</c>: the value rounded towards zero.</summary>
    BigInteger Truncate();

    /// <summary><c>math.floor(x)</c>.</summary>
    BigInteger Floor();

    /// <summary><c>math.ceil(x)</c>.</summary>
    BigInteger Ceiling();

    /// <summary><c>round(x)</c> and <c>round(x, ndigits)</c>.</summary>
    PyObject Round(PyObject? digits);
}

/// <summary>
/// A value whose class a module supplies, and which answers <c>type(x)</c> with that class
/// object — so <c>type(x) is decimal.Decimal</c> holds even when the module builds its class
/// per run.
/// </summary>
public interface IPyHostTyped
{
    /// <summary>The class object <c>type(x)</c> returns.</summary>
    PyObject Type { get; }
}

/// <summary>
/// A mapping a module supplies (<c>collections.ChainMap</c>, <c>types.MappingProxyType</c>)
/// that <c>dict(m)</c> copies key by key rather than reading as a sequence of pairs.
/// </summary>
public interface IPyMapping
{
    /// <summary>The mapping's current contents, as a new dict.</summary>
    PyDict Snapshot();
}

/// <summary>The hooks the operators and builtins use to reach a <see cref="IPyNumber"/>.</summary>
public static class HostNumbers
{
    /// <summary>A binary operator over a host number on either side, or null.</summary>
    public static PyObject? Binary(string op, PyObject left, PyObject right)
    {
        if (left is IPyNumber forward && forward.Binary(op, right, reflected: false) is { } result)
        {
            return result;
        }

        return right is IPyNumber reflected && !ReferenceEquals(left, right)
            ? reflected.Binary(op, left, reflected: true)
            : null;
    }

    /// <summary>Orders two values, asking a host number on the right when the left did not know it.</summary>
    public static int? Compare(PyObject left, PyObject right) =>
        left.PyCompare(right) ?? (right is IPyNumber ? -right.PyCompare(left) : null);

    /// <summary>Equality, asking a host number on the right when the left did not know it.</summary>
    public static bool Equal(PyObject left, PyObject right) =>
        left.PyEquals(right) || (right is IPyNumber && left is not IPyNumber && right.PyEquals(left));
}
