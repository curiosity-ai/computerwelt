using System.Numerics;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules;

/// <summary><c>cmath</c>: the math functions over complex numbers.</summary>
/// <remarks>
/// Built on <see cref="Complex"/>. Branch cuts follow .NET's, which agree with C99's (and so
/// with CPython's) on the principal values; signed-zero subtleties on the cuts themselves
/// are not reproduced.
/// </remarks>
internal static class CmathModule
{
    public static PyModuleObject Create()
    {
        var module = new PyModuleObject("cmath");

        void Unary(string name, Func<Complex, Complex> function) =>
            module.Add(name, arguments =>
            {
                Arity.Exact(name, arguments, 1);
                var result = function(Require(arguments[0], name));

                if (double.IsNaN(result.Real) && double.IsNaN(result.Imaginary) && !IsNaN(arguments[0]))
                {
                    throw new PyRaise(PyErrors.ValueError("math domain error"));
                }

                return new PyComplex(result.Real, result.Imaginary);
            });

        Unary("sqrt", Complex.Sqrt);
        Unary("exp", Complex.Exp);
        Unary("log10", Complex.Log10);
        Unary("sin", Complex.Sin);
        Unary("cos", Complex.Cos);
        Unary("tan", Complex.Tan);
        Unary("asin", Complex.Asin);
        Unary("acos", Complex.Acos);
        Unary("atan", Complex.Atan);
        Unary("sinh", Complex.Sinh);
        Unary("cosh", Complex.Cosh);
        Unary("tanh", Complex.Tanh);
        Unary("asinh", static z => Complex.Log(z + Complex.Sqrt((z * z) + 1)));
        Unary("acosh", static z => Complex.Log(z + (Complex.Sqrt(z + 1) * Complex.Sqrt(z - 1))));
        Unary("atanh", static z => 0.5 * (Complex.Log(1 + z) - Complex.Log(1 - z)));

        module.Add("log", arguments =>
        {
            Arity.Between("log", arguments, 1, 2);
            var z = Require(arguments[0], "log");

            if (z == Complex.Zero)
            {
                throw new PyRaise(PyErrors.ValueError("math domain error"));
            }

            var result = arguments.Length == 2 ? Complex.Log(z) / Complex.Log(Require(arguments[1], "log")) : Complex.Log(z);
            return new PyComplex(result.Real, result.Imaginary);
        });

        module.Add("phase", arguments =>
        {
            Arity.Exact("phase", arguments, 1);
            return new PyFloat(Require(arguments[0], "phase").Phase);
        });

        module.Add("polar", arguments =>
        {
            Arity.Exact("polar", arguments, 1);
            var z = Require(arguments[0], "polar");
            return new PyTuple([new PyFloat(Complex.Abs(z)), new PyFloat(z.Phase)]);
        });

        module.Add("rect", arguments =>
        {
            Arity.Exact("rect", arguments, 2);
            var z = Complex.FromPolarCoordinates(Real(arguments[0]), Real(arguments[1]));
            return new PyComplex(z.Real, z.Imaginary);
        });

        module.Add("isnan", arguments =>
        {
            Arity.Exact("isnan", arguments, 1);
            return PyBool.Of(IsNaN(arguments[0]));
        });

        module.Add("isinf", arguments =>
        {
            Arity.Exact("isinf", arguments, 1);
            var z = Require(arguments[0], "isinf");
            return PyBool.Of(double.IsInfinity(z.Real) || double.IsInfinity(z.Imaginary));
        });

        module.Add("isfinite", arguments =>
        {
            Arity.Exact("isfinite", arguments, 1);
            var z = Require(arguments[0], "isfinite");
            return PyBool.Of(double.IsFinite(z.Real) && double.IsFinite(z.Imaginary));
        });

        module.Add("isclose", (arguments, keywords) =>
        {
            Arity.Exact("isclose", arguments, 2);
            var a = Require(arguments[0], "isclose");
            var b = Require(arguments[1], "isclose");
            var relative = keywords is not null && keywords.TryGetValue(new PyStr("rel_tol"), out var r) ? Real(r) : 1e-09;
            var absolute = keywords is not null && keywords.TryGetValue(new PyStr("abs_tol"), out var t) ? Real(t) : 0.0;

            if (a == b)
            {
                return PyBool.True;
            }

            var difference = Complex.Abs(a - b);
            return PyBool.Of(difference <= Math.Max(relative * Math.Max(Complex.Abs(a), Complex.Abs(b)), absolute));
        });

        module.Add("pi", new PyFloat(Math.PI));
        module.Add("e", new PyFloat(Math.E));
        module.Add("tau", new PyFloat(Math.Tau));
        module.Add("inf", new PyFloat(double.PositiveInfinity));
        module.Add("nan", new PyFloat(double.NaN));
        module.Add("infj", new PyComplex(0, double.PositiveInfinity));
        module.Add("nanj", new PyComplex(0, double.NaN));

        return module;
    }

    private static bool IsNaN(PyObject value) =>
        PyComplex.From(value) is { } z && (double.IsNaN(z.Real) || double.IsNaN(z.Imaginary));

    private static Complex Require(PyObject value, string name) =>
        PyComplex.From(value) ?? throw new PyRaise(PyErrors.TypeError(
            $"must be real number, not {value.TypeName}"));

    private static double Real(PyObject value) => value switch
    {
        PyFloat number => number.Value,
        PyInt integer => Numbers.ToDouble(integer.Value),
        _ => throw new PyRaise(PyErrors.TypeError($"must be real number, not {value.TypeName}")),
    };
}
