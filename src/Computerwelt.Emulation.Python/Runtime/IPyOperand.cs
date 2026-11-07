namespace Computerwelt.Emulation.Python.Runtime;

/// <summary>
/// A host-implemented value that defines its own operators, as a CPython extension type
/// fills its number slots.
/// </summary>
/// <remarks>
/// <para>
/// The interpreter's operators are a closed switch over the built-in types, which is right
/// for them and wrong for a C# type such as <c>numpy.ndarray</c>, whose <c>+</c> broadcasts,
/// whose <c>==</c> returns an array, and which must answer <c>2 * a</c> as readily as
/// <c>a * 2</c>. Rather than teach <see cref="Operators"/> about each such type, a type
/// implements this and <see cref="Operators"/> asks it first — the C# equivalent of the
/// <c>__add__</c> / <c>__radd__</c> protocol, without a dunder lookup per operation.
/// </para>
/// <para>
/// Every member returns <see langword="null"/> for "not mine", which falls through to the
/// ordinary rules, so implementing it can only ever add behaviour.
/// </para>
/// </remarks>
public interface IPyOperand
{
    /// <summary>
    /// A binary operator (<c>+ - * / // % ** @ &amp; | ^ &lt;&lt; &gt;&gt;</c>).
    /// </summary>
    /// <param name="op">The operator, without any trailing <c>=</c>.</param>
    /// <param name="other">The other operand.</param>
    /// <param name="reflected">True when this value is the right-hand operand.</param>
    PyObject? Binary(string op, PyObject other, bool reflected);

    /// <summary>An augmented assignment (<c>+=</c> …), given the plain operator. Null to fall back to <see cref="Binary"/>.</summary>
    PyObject? InPlace(string op, PyObject other);

    /// <summary>A unary operator: <c>-</c>, <c>+</c>, <c>~</c>, or <c>abs</c> for the builtin.</summary>
    PyObject? Unary(string op);

    /// <summary>A rich comparison (<c>== != &lt; &lt;= &gt; &gt;=</c>).</summary>
    /// <param name="op">The operator as written, with this value on the left.</param>
    /// <param name="other">The other operand.</param>
    PyObject? Compare(string op, PyObject other);

    /// <summary>
    /// A conversion builtin applied to this value: <c>float</c>, <c>int</c> or <c>round</c>
    /// (whose <paramref name="argument"/> is <c>ndigits</c>, or null when omitted).
    /// </summary>
    PyObject? Convert(string conversion, PyObject? argument);
}
