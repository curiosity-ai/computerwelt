using System.Globalization;
using System.Numerics;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules.Numpy;

/// <summary>
/// Reads a function's arguments by position or by name, as a Python signature binds them.
/// </summary>
internal sealed class NpArgs
{
    private readonly PyObject?[] _values;
    private readonly string[] _names;
    private readonly string _function;

    public NpArgs(string function, PyObject[] arguments, PyDict? keywords, params string[] names)
    {
        _function = function;
        _names = names;
        _values = new PyObject?[names.Length];

        if (arguments.Length > names.Length)
        {
            throw new PyRaise(PyErrors.TypeError(
                $"{function}() takes at most {names.Length} positional arguments ({arguments.Length} given)"));
        }

        for (var i = 0; i < arguments.Length; i++)
        {
            _values[i] = arguments[i];
        }

        foreach (var (key, value) in keywords?.Entries ?? [])
        {
            var name = key.Display();
            var index = System.Array.IndexOf(names, name);

            if (index < 0)
            {
                throw new PyRaise(PyErrors.TypeError($"{function}() got an unexpected keyword argument '{name}'"));
            }

            if (_values[index] is not null)
            {
                throw new PyRaise(PyErrors.TypeError($"{function}() got multiple values for argument '{name}'"));
            }

            _values[index] = value;
        }
    }

    /// <summary>The argument, or null when it was not given.</summary>
    public PyObject? this[string name] => _values[Index(name)];

    /// <summary>The argument, or null when it was not given or was <c>None</c>.</summary>
    public PyObject? Optional(string name) => this[name] is PyNone ? null : this[name];

    public bool Has(string name) => _values[Index(name)] is not null;

    public PyObject Required(string name) =>
        this[name] ?? throw new PyRaise(PyErrors.TypeError(
            $"{_function}() missing required argument '{name}' (pos {Index(name) + 1})"));

    public NdArray Array(string name) => NdConvert.ToArray(Required(name));

    public int Int(string name, int fallback) => Optional(name) is { } value ? NdConvert.ToInt(value) : fallback;

    public double Double(string name, double fallback) =>
        Optional(name) is { } value ? NdArray.ToDouble(value) : fallback;

    public bool Bool(string name, bool fallback) => Optional(name) is { } value ? value.IsTruthy() : fallback;

    public NdDType? DType(string name = "dtype") => NdDType.Resolve(this[name]);

    private int Index(string name)
    {
        var index = System.Array.IndexOf(_names, name);
        return index >= 0 ? index : throw new InvalidOperationException($"no parameter {name}");
    }
}

/// <summary>Conversions between Python values and arrays.</summary>
internal static class NdConvert
{
    /// <summary>
    /// <c>np.asarray</c>: the array itself when it already is one of the right kind, otherwise
    /// a new array built from nested lists, tuples, ranges and scalars.
    /// </summary>
    public static NdArray ToArray(PyObject value, NdDType? dtype = null)
    {
        if (value is NdArray array)
        {
            return dtype is null || (dtype.Kind == array.Kind && (dtype.Kind != DKind.Str || dtype.Width == 0 || dtype.Width == array.DType.Width))
                ? array
                : array.AsType(dtype);
        }

        var built = Build(value);
        return dtype is null || (dtype.Kind == built.Kind && (dtype.Kind != DKind.Str || dtype.Width == 0)) ? built : built.AsType(dtype);
    }

    /// <summary><c>np.array</c>: always a new array (unless <c>copy=False</c> allows reuse).</summary>
    public static NdArray NewArray(PyObject value, NdDType? dtype, bool copy = true)
    {
        var result = ToArray(value, dtype);
        return copy && ReferenceEquals(result, value) ? result.Copy() : result;
    }

    /// <summary>An array result as numpy hands it back: a 0-d array becomes its scalar.</summary>
    public static PyObject Wrap(NdArray array) =>
        array.NDim == 0 ? array.GetBoxed(array.Offset) : array;

    public static bool IsSequence(PyObject value) =>
        value is PyList or PyTuple or PyRange or PyDeque || value is NdArray { NDim: > 0 };

    private static List<PyObject> Elements(PyObject value) => value switch
    {
        PyList list => list.Items,
        PyTuple tuple => [.. tuple.Items],
        NdArray array => [.. array.Iterate()!],
        _ => [.. VirtualMachine.RequireIterable(value)],
    };

    private static NdArray Build(PyObject value)
    {
        if (!IsSequence(value))
        {
            return Scalar(value);
        }

        // The shape is read off the first element at each depth, then every branch is
        // checked against it on the way down.
        var dimensions = new List<int>();
        var current = value;

        while (IsSequence(current))
        {
            if (current is NdArray nested)
            {
                dimensions.AddRange(nested.Shape);
                break;
            }

            var items = Elements(current);
            dimensions.Add(items.Count);

            if (dimensions.Count > 32)
            {
                throw new PyRaise(PyErrors.ValueError("maximum supported dimension for an ndarray is 32"));
            }

            if (items.Count == 0)
            {
                break;
            }

            current = items[0];
        }

        var shape = dimensions.ToArray();
        var size = NdArray.CheckedSize(shape);
        var leaves = new List<PyObject>(size);
        Flatten(value, 0, shape, leaves);

        return FromLeaves(leaves, shape);
    }

    private static void Flatten(PyObject value, int level, int[] shape, List<PyObject> leaves)
    {
        if (level == shape.Length)
        {
            if (IsSequence(value))
            {
                throw Inhomogeneous(shape, level);
            }

            leaves.Add(value);
            return;
        }

        if (!IsSequence(value))
        {
            throw Inhomogeneous(shape, level);
        }

        if (value is NdArray array)
        {
            if (!array.Shape.SequenceEqual(shape[level..]))
            {
                throw Inhomogeneous(shape, level);
            }

            leaves.AddRange(array.ToBoxed());
            return;
        }

        var items = Elements(value);

        if (items.Count != shape[level])
        {
            throw Inhomogeneous(shape, level);
        }

        foreach (var item in items)
        {
            Flatten(item, level + 1, shape, leaves);
        }
    }

    private static PyRaise Inhomogeneous(int[] shape, int level) =>
        new(PyErrors.ValueError(
            "setting an array element with a sequence. The requested array has an inhomogeneous shape after "
            + $"{level.ToString(CultureInfo.InvariantCulture)} dimensions. The detected shape was "
            + $"{NdArray.FormatShape(shape[..level])} + inhomogeneous part."));

    /// <summary>The kind numpy infers for one Python value.</summary>
    public static DKind KindOf(PyObject value) => value switch
    {
        PyBool => DKind.Bool,
        PyInt integer => integer.Value >= long.MinValue && integer.Value <= long.MaxValue ? DKind.Int : DKind.Object,
        PyFloat => DKind.Float,
        PyStr => DKind.Str,
        _ => DKind.Object,
    };

    /// <summary>Builds an array from row-major leaves, inferring the dtype.</summary>
    public static NdArray FromLeaves(IReadOnlyList<PyObject> leaves, int[] shape)
    {
        var kind = DKind.Bool;
        var sawText = false;
        var sawOther = false;

        foreach (var leaf in leaves)
        {
            var leafKind = KindOf(leaf);

            if (leafKind == DKind.Str)
            {
                sawText = true;
            }
            else if (leafKind == DKind.Object)
            {
                sawOther = true;
            }
            else if (leafKind > kind)
            {
                kind = leafKind;
            }
        }

        if (sawOther)
        {
            kind = DKind.Object;
        }
        else if (sawText)
        {
            kind = DKind.Str;
        }
        else if (leaves.Count == 0)
        {
            kind = DKind.Float;
        }

        return FromLeaves(leaves, shape, kind);
    }

    public static NdArray FromLeaves(IReadOnlyList<PyObject> leaves, int[] shape, DKind kind)
    {
        switch (kind)
        {
            case DKind.Float:
            {
                var values = new double[leaves.Count];

                for (var i = 0; i < values.Length; i++)
                {
                    values[i] = NdArray.ToDouble(leaves[i]);
                }

                return NdArray.FromDoubles(values, shape);
            }

            case DKind.Int:
            {
                var values = new long[leaves.Count];

                for (var i = 0; i < values.Length; i++)
                {
                    values[i] = leaves[i] is PyFloat f ? NdArray.FloatToLong(f.Value) : NdArray.ToLong(leaves[i]);
                }

                return NdArray.FromLongs(values, shape);
            }

            case DKind.Bool:
            {
                var values = new bool[leaves.Count];

                for (var i = 0; i < values.Length; i++)
                {
                    values[i] = leaves[i].IsTruthy();
                }

                return NdArray.FromBools(values, shape);
            }

            case DKind.Str:
                return NdArray.FromObjects(DKind.Str, [.. leaves.Select(static l => l is PyStr ? l : new PyStr(l.Display()))], shape);

            default:
                return NdArray.FromObjects(DKind.Object, [.. leaves], shape);
        }
    }

    private static NdArray Scalar(PyObject value) => FromLeaves([value], []);

    /// <summary>Reads an integer argument, accepting an integral one-element array too.</summary>
    public static int ToInt(PyObject value)
    {
        var raw = value switch
        {
            PyInt integer => integer.Value,
            NdArray { Size: 1, Kind: DKind.Int or DKind.Bool } array => new BigInteger(array.GetLong(array.Offset)),
            _ => throw new PyRaise(PyErrors.TypeError(
                $"'{value.TypeName}' object cannot be interpreted as an integer")),
        };

        return raw < int.MinValue || raw > int.MaxValue
            ? throw new PyRaise(PyErrors.ValueError("Maximum allowed size exceeded"))
            : (int)raw;
    }

    /// <summary>Reads a shape: an integer or a sequence of them.</summary>
    public static int[] ToShape(PyObject value) => value switch
    {
        PyInt => [ToInt(value)],
        NdArray { NDim: 0 } => [ToInt(value)],
        _ when IsSequence(value) => [.. Elements(value).Select(ToInt)],
        _ => throw new PyRaise(PyErrors.TypeError(
            $"'{value.TypeName}' object cannot be interpreted as an integer")),
    };

    /// <summary>Reads an <c>axis</c> argument: null for all axes, else the normalized axes.</summary>
    public static int[]? ToAxes(PyObject? value, int ndim)
    {
        if (value is null or PyNone)
        {
            return null;
        }

        var axes = value is PyTuple or PyList ? Elements(value).Select(ToInt).ToArray() : [ToInt(value)];
        var normalized = axes.Select(a => NdArray.NormalizeAxis(a, ndim)).ToArray();

        if (normalized.Distinct().Count() != normalized.Length)
        {
            throw new PyRaise(PyErrors.ValueError("duplicate value in 'axis'"));
        }

        return normalized;
    }

    /// <summary>A list of Python values as a 1-D array, inferring the dtype.</summary>
    public static NdArray FromList(IReadOnlyList<PyObject> values) => FromLeaves(values, [values.Count]);
}
