namespace Computerwelt.Emulation.Python.Runtime;

/// <summary>
/// A value implemented in C# that defines its own operators.
/// </summary>
/// <remarks>
/// <para>
/// A class written in Python reaches the operators through its dunders; a host type has no
/// dunders to look up, so it answers here instead. <see cref="Operators"/> asks before any
/// of its own rules — a pandas <c>Series</c> compared with <c>==</c> must answer with a
/// Series of booleans, never fall back to identity.
/// </para>
/// <para>
/// Every method returns <see langword="null"/> for "not mine", which sends the operator on
/// to the ordinary rules (and from there to the usual <c>TypeError</c>).
/// </para>
/// </remarks>
public interface IPyOperatorOverloads
{
    /// <summary>
    /// Applies a binary arithmetic or bitwise operator (<c>+ - * / // % ** &amp; | ^ &lt;&lt; &gt;&gt; @</c>).
    /// </summary>
    /// <param name="op">The operator, without any augmented-assignment <c>=</c>.</param>
    /// <param name="other">The other operand.</param>
    /// <param name="reflected">True when this value is the <i>right</i> operand.</param>
    /// <returns>The result, or null to decline.</returns>
    PyObject? BinaryOperator(string op, PyObject other, bool reflected);

    /// <summary>Applies a unary operator (<c>-</c>, <c>+</c>, <c>~</c>, <c>abs</c>).</summary>
    /// <returns>The result, or null to decline.</returns>
    PyObject? UnaryOperator(string op);

    /// <summary>
    /// Applies a comparison with this value on the left (<c>== != &lt; &lt;= &gt; &gt;=</c>).
    /// When this value was on the right the caller has already mirrored the operator.
    /// </summary>
    /// <returns>The result, or null to decline.</returns>
    PyObject? CompareOperator(string op, PyObject other);
}
