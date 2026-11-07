using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules.Numpy;

/// <summary>The operators: every Python operator on an array is a ufunc.</summary>
internal sealed partial class NdArray : IPyOperand
{
    /// <summary>Whether a value can stand on the other side of an array operator.</summary>
    private static bool IsOperand(PyObject other) =>
        other is NdArray or PyInt or PyFloat or PyList or PyTuple or PyRange;

    public PyObject? Binary(string op, PyObject other, bool reflected) =>
        NumpyModule.Guard("Binary", () => BinaryCore(op, other, reflected));

    private PyObject? BinaryCore(string op, PyObject other, bool reflected)
    {
        if (!IsOperand(other) && !(other is PyStr && Kind == DKind.Str))
        {
            return null;
        }

        if (op == "@")
        {
            return reflected ? LinAlg.MatMul(NdConvert.ToArray(other), this) : LinAlg.MatMul(this, NdConvert.ToArray(other));
        }

        if (Ufuncs.NameOfOperator(op) is not { } name)
        {
            return null;
        }

        return reflected ? Ufuncs.Binary(name, other, this) : Ufuncs.Binary(name, this, other);
    }

    public PyObject? InPlace(string op, PyObject other) =>
        NumpyModule.Guard("InPlace", () => InPlaceCore(op, other));

    private PyObject? InPlaceCore(string op, PyObject other)
    {
        if (!IsOperand(other) && !(other is PyStr && Kind == DKind.Str))
        {
            return null;
        }

        NdArray result;

        if (op == "@")
        {
            result = NdConvert.ToArray(LinAlg.MatMul(this, NdConvert.ToArray(other)));
        }
        else if (Ufuncs.NameOfOperator(op) is { } name)
        {
            result = Ufuncs.BinaryArray(name, this, NdConvert.ToArray(other));

            // The result is written back into this array, so it may not be of a wider kind:
            // `ints += 0.5` is refused, as numpy refuses it, rather than silently truncated.
            if (result.Kind > Kind && Kind is DKind.Bool or DKind.Int && result.Kind is DKind.Int or DKind.Float)
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"Cannot cast ufunc '{name}' output from {result.DType.Repr()} to {DType.Repr()} with casting rule 'same_kind'"));
            }
        }
        else
        {
            return null;
        }

        if (!result.Shape.SequenceEqual(Shape))
        {
            throw new PyRaise(PyErrors.ValueError(
                $"non-broadcastable output operand with shape {ShapeText(Shape)} doesn't match the broadcast shape {ShapeText(result.Shape)}"));
        }

        Assign(Offsets(), Shape, result);
        return this;
    }

    public PyObject? Unary(string op) => NumpyModule.Guard("Unary", () => UnaryCore(op));

    private PyObject? UnaryCore(string op) => op switch
    {
        "-" => Ufuncs.Unary("negative", this),
        "+" => Ufuncs.Unary("positive", this),
        "~" => Ufuncs.Unary("invert", this),
        "abs" => Ufuncs.Unary("absolute", this),
        _ => null,
    };

    public PyObject? Compare(string op, PyObject other) =>
        NumpyModule.Guard("Compare", () => CompareCore(op, other));

    private PyObject? CompareCore(string op, PyObject other)
    {
        // Equality is defined against anything (an unrelated value is simply unequal);
        // ordering needs something comparable.
        if (other is PyInstance || (!IsOperand(other) && op is not ("==" or "!=") && other is not PyStr))
        {
            return null;
        }

        return Ufuncs.Compare(op, this, other);
    }

    public PyObject? Convert(string conversion, PyObject? argument) =>
        NumpyModule.Guard("Convert", () => ConvertCore(conversion, argument));

    private PyObject? ConvertCore(string conversion, PyObject? argument)
    {
        if (conversion == "round")
        {
            return NdConvert.Wrap(Ufuncs.Round(this, argument is null ? 0 : NdConvert.ToInt(argument)));
        }

        if (Size != 1)
        {
            throw new PyRaise(PyErrors.TypeError("only length-1 arrays can be converted to Python scalars"));
        }

        var value = GetBoxed(Offsets()[0]);

        return conversion switch
        {
            "float" => new PyFloat(ToDouble(value)),
            "int" => value is PyFloat number
                ? PyInt.From(new System.Numerics.BigInteger(Math.Truncate(FiniteForInt(number.Value))))
                : PyInt.From(ToLong(value)),
            _ => null,
        };
    }

    private static double FiniteForInt(double value) =>
        double.IsNaN(value) ? throw new PyRaise(PyErrors.ValueError("cannot convert float NaN to integer"))
        : double.IsInfinity(value) ? throw new PyRaise(new PyException(PyExceptionType.OverflowError, "cannot convert float infinity to integer"))
        : value;
}
