using System.Globalization;
using System.Numerics;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules.Numpy;

/// <summary>The element kinds an array can hold, in promotion order.</summary>
internal enum DKind
{
    /// <summary><c>bool</c>.</summary>
    Bool,

    /// <summary><c>int64</c>.</summary>
    Int,

    /// <summary><c>float64</c>.</summary>
    Float,

    /// <summary><c>&lt;U</c>n: fixed-width text.</summary>
    Str,

    /// <summary><c>object</c>: any Python value.</summary>
    Object,
}

/// <summary>
/// <c>numpy.dtype</c>: what an array's elements are.
/// </summary>
/// <remarks>
/// Only five exist: <c>bool</c>, <c>int64</c>, <c>float64</c>, <c>&lt;U</c>n and
/// <c>object</c>. The narrower numeric names a program may spell (<c>int32</c>,
/// <c>float32</c>, <c>uint8</c>…) are accepted and widened to the 64-bit kind, which is the
/// one documented divergence in how values are stored.
/// </remarks>
internal sealed class NdDType : PyObject
{
    private NdDType(DKind kind, int width = 0)
    {
        Kind = kind;
        Width = width;
    }

    public static NdDType Bool { get; } = new(DKind.Bool);

    public static NdDType Int64 { get; } = new(DKind.Int);

    public static NdDType Float64 { get; } = new(DKind.Float);

    public static NdDType Object { get; } = new(DKind.Object);

    /// <summary>A text dtype of the given width (characters); 0 is the unsized <c>str</c>, sized by its data.</summary>
    public static NdDType Str(int width) => new(DKind.Str, Math.Max(width, 0));

    /// <summary>The element kind.</summary>
    public DKind Kind { get; }

    /// <summary>For text, the most characters an element holds.</summary>
    public int Width { get; }

    /// <summary>Returns the dtype for <paramref name="kind"/>; text gets width 1.</summary>
    public static NdDType Of(DKind kind) => kind switch
    {
        DKind.Bool => Bool,
        DKind.Int => Int64,
        DKind.Float => Float64,
        DKind.Str => Str(1),
        _ => Object,
    };

    /// <summary>The name <c>dtype.name</c> reports.</summary>
    public string Name => Kind switch
    {
        DKind.Bool => "bool",
        DKind.Int => "int64",
        DKind.Float => "float64",
        DKind.Str => "str" + (Width * 32).ToString(CultureInfo.InvariantCulture),
        _ => "object",
    };

    /// <summary>The spelling in <c>repr(dtype)</c> and in an array's <c>dtype=</c> suffix.</summary>
    public string Spelling => Kind switch
    {
        DKind.Str => "<U" + Width.ToString(CultureInfo.InvariantCulture),
        DKind.Object => "O",
        _ => Name,
    };

    public override string TypeName => "numpy.dtype";

    public override string Repr() => $"dtype('{Spelling}')";

    public override string Display() => Kind switch
    {
        DKind.Str => Spelling,
        _ => Name,
    };

    public override BigInteger PyHash() => new(StringComparer.Ordinal.GetHashCode(Spelling));

    public override bool PyEquals(PyObject other) =>
        other is PyNone ? Kind == DKind.Float
        : TryResolve(other) is { } resolved && resolved.Kind == Kind
            && (Kind != DKind.Str || other is not NdDType || resolved.Width == Width);

    public override PyObject? GetAttribute(string name) => name switch
    {
        "name" => new PyStr(Name),
        "kind" => new PyStr(Kind switch
        {
            DKind.Bool => "b",
            DKind.Int => "i",
            DKind.Float => "f",
            DKind.Str => "U",
            _ => "O",
        }),
        "char" => new PyStr(Kind switch
        {
            DKind.Bool => "?",
            DKind.Int => "l",
            DKind.Float => "d",
            DKind.Str => "U",
            _ => "O",
        }),
        "str" => new PyStr(Kind switch
        {
            DKind.Bool => "|b1",
            DKind.Int => "<i8",
            DKind.Float => "<f8",
            DKind.Str => Spelling,
            _ => "|O",
        }),
        "itemsize" => PyInt.From(Kind switch
        {
            DKind.Bool => 1,
            DKind.Str => 4 * Width,
            _ => 8,
        }),
        "type" => NumpyTypes.ScalarTypeOf(Kind),
        "ndim" => PyInt.From(0),
        "shape" => new PyTuple([]),
        _ => null,
    };

    /// <summary>
    /// Reads a dtype argument: a dtype, a type (<c>float</c>, <c>np.int64</c>), or a name.
    /// Null for <c>None</c>; raises <c>TypeError</c> for anything else.
    /// </summary>
    public static NdDType? Resolve(PyObject? spec)
    {
        if (spec is null or PyNone)
        {
            return null;
        }

        return TryResolve(spec) ?? throw new PyRaise(PyErrors.TypeError(
            spec is PyStr text
                ? $"data type '{text.Value}' not understood"
                : $"Cannot interpret '{spec.Repr()}' as a data type"));
    }

    private static NdDType? TryResolve(PyObject spec)
    {
        switch (spec)
        {
            case NdDType dtype:
                return dtype;

            case NdArray array:
                return array.DType;

            case PyType type:
                if (ReferenceEquals(type, TypeRegistry.Float))
                {
                    return Float64;
                }

                if (ReferenceEquals(type, TypeRegistry.Int))
                {
                    return Int64;
                }

                if (ReferenceEquals(type, TypeRegistry.Bool))
                {
                    return Bool;
                }

                if (ReferenceEquals(type, TypeRegistry.Str))
                {
                    return Str(0);
                }

                if (ReferenceEquals(type, TypeRegistry.Object))
                {
                    return Object;
                }

                return NumpyTypes.KindOfScalarType(type) is { } kind ? Of(kind) : null;

            case PyStr text:
                return FromName(text.Value);

            default:
                return null;
        }
    }

    private static NdDType? FromName(string name)
    {
        switch (name)
        {
            case "float64" or "float" or "f8" or "<f8" or "d" or "double" or "float32" or "f4" or "<f4"
                or "f" or "float16" or "f2" or "single" or "half" or "longdouble" or "float_":
                return Float64;

            case "int64" or "int" or "i8" or "<i8" or "l" or "int32" or "i4" or "<i4" or "i" or "int16"
                or "i2" or "int8" or "i1" or "uint8" or "u1" or "uint16" or "u2" or "uint32" or "u4"
                or "uint64" or "u8" or "uint" or "intp" or "int_" or "long" or "short" or "byte":
                return Int64;

            case "bool" or "?" or "b1" or "bool_" or "bool8":
                return Bool;

            case "object" or "O" or "object_":
                return Object;

            case "str" or "U" or "unicode" or "str_" or "<U":
                return Str(0);
        }

        // `<U5`, `U5`.
        var digits = name.StartsWith("<U", StringComparison.Ordinal) ? name[2..]
            : name.StartsWith('U') ? name[1..]
            : null;

        return digits is not null && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var width)
            ? Str(width)
            : null;
    }

    /// <summary>The kind both operands promote to.</summary>
    public static DKind Promote(DKind left, DKind right)
    {
        if (left == right)
        {
            return left;
        }

        if (left == DKind.Object || right == DKind.Object)
        {
            return DKind.Object;
        }

        if (left == DKind.Str || right == DKind.Str)
        {
            // Text and numbers have no common numeric type; numpy refuses most such
            // operations, and the ones it allows are decided by the caller.
            return DKind.Object;
        }

        return (DKind)Math.Max((int)left, (int)right);
    }
}

/// <summary>
/// The numpy scalar types — <c>np.float64</c>, <c>np.int64</c>, <c>np.bool_</c> and the
/// abstract ones <c>isinstance</c> and <c>issubdtype</c> are asked about.
/// </summary>
/// <remarks>
/// Elements come back from an array as Python <c>int</c>, <c>float</c> and <c>bool</c>, not
/// numpy scalars, so each type is matched against the Python value it stands for: an
/// element of a float array satisfies <c>isinstance(x, np.float64)</c>, and calling
/// <c>np.float64(2)</c> returns the Python float <c>2.0</c>.
/// </remarks>
internal static class NumpyTypes
{
    private static readonly Dictionary<string, DKind> Kinds = new(StringComparer.Ordinal);

    public static PyType Float64 { get; } = Scalar("float64", DKind.Float, static v => v is PyFloat);

    public static PyType Int64 { get; } = Scalar("int64", DKind.Int, static v => v is PyInt and not PyBool);

    public static PyType Bool { get; } = Scalar("bool", DKind.Bool, static v => v is PyBool);

    public static PyType StrType { get; } = Scalar("str_", DKind.Str, static v => v is PyStr);

    public static PyType ObjectType { get; } = Scalar("object_", DKind.Object, static _ => true);

    // The narrower names: accepted, and stored as the 64-bit kind (see NdDType).
    public static PyType Float32 { get; } = Scalar("float32", DKind.Float, static v => v is PyFloat);

    public static PyType Int32 { get; } = Scalar("int32", DKind.Int, static v => v is PyInt and not PyBool);

    public static PyType Int16 { get; } = Scalar("int16", DKind.Int, static v => v is PyInt and not PyBool);

    public static PyType Int8 { get; } = Scalar("int8", DKind.Int, static v => v is PyInt and not PyBool);

    public static PyType UInt8 { get; } = Scalar("uint8", DKind.Int, static v => v is PyInt and not PyBool);

    // The abstract types, for isinstance and issubdtype.
    public static PyType Number { get; } = Abstract("number", static v => v is (PyInt and not PyBool) or PyFloat);

    public static PyType Integer { get; } = Abstract("integer", static v => v is PyInt and not PyBool);

    public static PyType SignedInteger { get; } = Abstract("signedinteger", static v => v is PyInt and not PyBool);

    public static PyType Floating { get; } = Abstract("floating", static v => v is PyFloat);

    public static PyType Inexact { get; } = Abstract("inexact", static v => v is PyFloat);

    public static PyType Generic { get; } = Abstract("generic", static v => v is PyInt or PyFloat or PyStr);

    /// <summary>The scalar type objects, by the name the module exports them under.</summary>
    public static IEnumerable<(string Name, PyType Type)> Exported =>
    [
        ("float64", Float64), ("double", Float64), ("float_", Float64), ("float32", Float32),
        ("single", Float32), ("float16", Float32), ("half", Float32),
        ("int64", Int64), ("int_", Int64), ("intp", Int64), ("int32", Int32), ("int16", Int16),
        ("int8", Int8), ("uint8", UInt8), ("uint16", Int32), ("uint32", Int64), ("uint64", Int64),
        ("bool_", Bool), ("bool", Bool), ("str_", StrType), ("object_", ObjectType),
        ("number", Number), ("integer", Integer), ("signedinteger", SignedInteger),
        ("floating", Floating), ("inexact", Inexact), ("generic", Generic),
    ];

    public static PyType ScalarTypeOf(DKind kind) => kind switch
    {
        DKind.Bool => Bool,
        DKind.Int => Int64,
        DKind.Float => Float64,
        DKind.Str => StrType,
        _ => ObjectType,
    };

    public static DKind? KindOfScalarType(PyType type) =>
        Kinds.TryGetValue(type.Name, out var kind) ? kind : null;

    /// <summary>
    /// <c>np.issubdtype(a, b)</c>: whether the kind <paramref name="kind"/> falls under the
    /// type or dtype <paramref name="parent"/>.
    /// </summary>
    public static bool IsSubDType(DKind kind, PyObject parent)
    {
        if (parent is PyType type)
        {
            if (ReferenceEquals(type, Number))
            {
                return kind is DKind.Int or DKind.Float;
            }

            if (ReferenceEquals(type, Integer) || ReferenceEquals(type, SignedInteger))
            {
                return kind == DKind.Int;
            }

            if (ReferenceEquals(type, Floating) || ReferenceEquals(type, Inexact))
            {
                return kind == DKind.Float;
            }

            if (ReferenceEquals(type, Generic))
            {
                return true;
            }
        }

        return NdDType.Resolve(parent)?.Kind == kind;
    }

    private static PyType Scalar(string name, DKind kind, Func<PyObject, bool> matches)
    {
        var qualified = "numpy." + name;
        Kinds[qualified] = kind;

        return new PyType(qualified, matches, (arguments, keywords) =>
        {
            PyBuiltinFunction.RejectKeywords(name, keywords);
            Arity.Between(name, arguments, 0, 1);

            if (arguments.Length == 0)
            {
                return NdArray.Box(kind, kind switch
                {
                    DKind.Bool => PyBool.False,
                    DKind.Int => PyInt.From(0),
                    DKind.Float => new PyFloat(0),
                    DKind.Str => new PyStr(string.Empty),
                    _ => PyNone.Instance,
                });
            }

            // A sequence becomes an array of the type; a scalar becomes a Python scalar.
            var source = arguments[0];

            if (source is NdArray or PyList or PyTuple)
            {
                return NdConvert.Wrap(NdConvert.ToArray(source).AsType(NdDType.Of(kind)));
            }

            return NdArray.Box(kind, source);
        });
    }

    private static PyType Abstract(string name, Func<PyObject, bool> matches) =>
        new("numpy." + name, matches, (_, _) =>
            throw new PyRaise(PyErrors.TypeError($"cannot create 'numpy.{name}' instances")));
}
