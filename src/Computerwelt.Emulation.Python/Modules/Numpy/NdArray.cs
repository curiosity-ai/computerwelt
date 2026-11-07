using System.Globalization;
using System.Numerics;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules.Numpy;

/// <summary>
/// The storage an array and all of its views share.
/// </summary>
/// <remarks>
/// One typed array per kind rather than a boxed <c>object[]</c>: a million floats are eight
/// megabytes here instead of a million heap objects. Text is stored as <see cref="PyStr"/>
/// in the object slot.
/// </remarks>
internal sealed class NdBuffer
{
    public NdBuffer(DKind kind, int length)
    {
        Kind = kind;
        Length = length;

        switch (kind)
        {
            case DKind.Bool:
                B = new bool[length];
                break;
            case DKind.Int:
                I = new long[length];
                break;
            case DKind.Float:
                F = new double[length];
                break;
            case DKind.Str:
                O = new PyObject[length];
                Array.Fill(O, PyStr.Empty);
                break;
            default:
                O = new PyObject[length];
                Array.Fill(O, PyInt.From(0));
                break;
        }
    }

    public NdBuffer(double[] values) => (Kind, F, Length) = (DKind.Float, values, values.Length);

    public NdBuffer(long[] values) => (Kind, I, Length) = (DKind.Int, values, values.Length);

    public NdBuffer(bool[] values) => (Kind, B, Length) = (DKind.Bool, values, values.Length);

    public NdBuffer(DKind kind, PyObject[] values) => (Kind, O, Length) = (kind, values, values.Length);

    public DKind Kind { get; }

    public int Length { get; }

    public double[]? F { get; }

    public long[]? I { get; }

    public bool[]? B { get; }

    public PyObject[]? O { get; }
}

/// <summary>
/// <c>numpy.ndarray</c>: an N-dimensional array over a shared, typed buffer.
/// </summary>
/// <remarks>
/// <para>
/// Row-major, with strides counted in elements rather than bytes. Basic indexing (integers,
/// slices, <c>...</c>, <c>None</c>), <c>.T</c>, <c>transpose</c>, <c>reshape</c> of a
/// contiguous array and <c>ravel</c> return <b>views</b> over the same buffer, as numpy's
/// do, so writing through a slice changes the original. Advanced indexing (masks and index
/// arrays) copies, as numpy's does.
/// </para>
/// <para>
/// Elements leave the array as Python <c>int</c>, <c>float</c>, <c>bool</c> and
/// <c>str</c>, not numpy scalar objects — the one difference a program printing
/// <c>repr(a[0])</c> can see (<c>1.5</c> where numpy 2 prints <c>np.float64(1.5)</c>).
/// </para>
/// </remarks>
internal sealed partial class NdArray : PyObject
{
    /// <summary>
    /// The most elements any one call may create.
    /// </summary>
    /// <remarks>
    /// The instruction limit does not count work done inside a builtin, so without a cap one
    /// <c>np.zeros(10**9)</c> would allocate gigabytes and one <c>np.outer</c> of two large
    /// vectors would spend unbounded time — the same reasoning, and the same 2^24, as
    /// <c>random</c>'s cap on what one call may build.
    /// </remarks>
    public const int MaxElements = 1 << 24;

    /// <summary>The <c>numpy.ndarray</c> type object.</summary>
    public static PyType Type { get; } = new(
        "numpy.ndarray",
        static value => value is NdArray,
        static (arguments, keywords) =>
        {
            var args = new NpArgs("ndarray", arguments, keywords, "shape", "dtype");
            return Zeros(NdConvert.ToShape(args.Required("shape")), NdDType.Resolve(args["dtype"]) ?? NdDType.Float64);
        });

    public NdArray(NdDType dtype, NdBuffer buffer, int[] shape, int[] strides, int offset)
    {
        DType = dtype;
        Buffer = buffer;
        Shape = shape;
        Strides = strides;
        Offset = offset;

        long size = 1;

        foreach (var dimension in shape)
        {
            size *= dimension;
        }

        Size = (int)size;
    }

    public NdDType DType { get; private set; }

    public DKind Kind => DType.Kind;

    public NdBuffer Buffer { get; }

    public int[] Shape { get; }

    public int[] Strides { get; }

    public int Offset { get; }

    public int Size { get; }

    public int NDim => Shape.Length;

    public override string TypeName => "numpy.ndarray";

    /// <summary>
    /// The most element operations one call may perform where that is more than linear in
    /// what it returns — a matrix product, an inverse, a convolution.
    /// </summary>
    /// <remarks>
    /// The array cap bounds memory but not time: two 4096-square matrices are within it and
    /// their product is seventy billion multiply-adds, all inside one builtin the run's
    /// timeout cannot interrupt. 2^30 is about a 1000-square product.
    /// </remarks>
    public const long MaxOperations = 1L << 30;

    /// <summary>Raises <c>MemoryError</c> when one call would do more than <see cref="MaxOperations"/>.</summary>
    public static void CheckWork(double operations, string what)
    {
        if (operations > MaxOperations)
        {
            throw new PyRaise(new PyException(
                PyExceptionType.MemoryError,
                $"{what} would take {operations.ToString("0", CultureInfo.InvariantCulture)} element operations: "
                + $"the sandbox caps one call at {MaxOperations.ToString(CultureInfo.InvariantCulture)}"));
        }
    }

    // ---- construction ----

    /// <summary>Raises <c>MemoryError</c> when a shape holds more than <see cref="MaxElements"/>.</summary>
    public static int CheckedSize(IReadOnlyList<int> shape)
    {
        long size = 1;

        foreach (var dimension in shape)
        {
            if (dimension < 0)
            {
                throw new PyRaise(PyErrors.ValueError("negative dimensions are not allowed"));
            }

            size *= dimension;

            if (size > MaxElements)
            {
                throw new PyRaise(new PyException(
                    PyExceptionType.MemoryError,
                    $"Unable to allocate an array with shape {FormatShape([.. shape])}: the sandbox "
                    + $"caps one array at {MaxElements} elements"));
            }
        }

        return (int)size;
    }

    public static int[] ContiguousStrides(int[] shape)
    {
        var strides = new int[shape.Length];
        var step = 1;

        for (var d = shape.Length - 1; d >= 0; d--)
        {
            strides[d] = step;
            step *= Math.Max(shape[d], 1);
        }

        return strides;
    }

    /// <summary>A new, zero-filled array.</summary>
    public static NdArray Zeros(int[] shape, NdDType dtype)
    {
        var size = CheckedSize(shape);
        return new NdArray(dtype, new NdBuffer(dtype.Kind, size), shape, ContiguousStrides(shape), 0);
    }

    public static NdArray FromDoubles(double[] values, int[] shape) =>
        new(NdDType.Float64, new NdBuffer(Checked(values, shape)), shape, ContiguousStrides(shape), 0);

    public static NdArray FromLongs(long[] values, int[] shape) =>
        new(NdDType.Int64, new NdBuffer(Checked(values, shape)), shape, ContiguousStrides(shape), 0);

    public static NdArray FromBools(bool[] values, int[] shape) =>
        new(NdDType.Bool, new NdBuffer(Checked(values, shape)), shape, ContiguousStrides(shape), 0);

    /// <summary>An array of text or objects; text gets the width of its longest element.</summary>
    public static NdArray FromObjects(DKind kind, PyObject[] values, int[] shape, int width = 0)
    {
        Checked(values, shape);

        if (kind == DKind.Str)
        {
            var widest = values.Length == 0 ? 1 : values.Max(static v => ((PyStr)v).Value.Length);
            return new NdArray(NdDType.Str(Math.Max(Math.Max(widest, width), 1)), new NdBuffer(kind, values), shape, ContiguousStrides(shape), 0);
        }

        return new NdArray(NdDType.Object, new NdBuffer(kind, values), shape, ContiguousStrides(shape), 0);
    }

    /// <summary>A one-dimensional float array.</summary>
    public static NdArray Vector(double[] values) => FromDoubles(values, [values.Length]);

    /// <summary>A one-dimensional int array.</summary>
    public static NdArray Vector(long[] values) => FromLongs(values, [values.Length]);

    private static T[] Checked<T>(T[] values, int[] shape)
    {
        _ = CheckedSize(shape);
        return values;
    }

    // ---- element access ----

    /// <summary>True when the elements are laid out contiguously in row-major order.</summary>
    public bool IsContiguous
    {
        get
        {
            var expected = 1;

            for (var d = NDim - 1; d >= 0; d--)
            {
                if (Shape[d] != 1 && Strides[d] != expected)
                {
                    return false;
                }

                expected *= Shape[d];
            }

            return true;
        }
    }

    /// <summary>The buffer position of every element, in row-major order.</summary>
    public int[] Offsets()
    {
        var size = Size;
        var result = new int[size];

        if (size == 0)
        {
            return result;
        }

        if (IsContiguous)
        {
            for (var k = 0; k < size; k++)
            {
                result[k] = Offset + k;
            }

            return result;
        }

        var nd = NDim;
        var index = new int[nd];
        var position = Offset;

        for (var k = 0; k < size; k++)
        {
            result[k] = position;

            for (var d = nd - 1; d >= 0; d--)
            {
                index[d]++;
                position += Strides[d];

                if (index[d] < Shape[d])
                {
                    break;
                }

                position -= Strides[d] * Shape[d];
                index[d] = 0;
            }
        }

        return result;
    }

    public double GetDouble(int position) => Kind switch
    {
        DKind.Float => Buffer.F![position],
        DKind.Int => Buffer.I![position],
        DKind.Bool => Buffer.B![position] ? 1 : 0,
        _ => ToDouble(Buffer.O![position]),
    };

    public long GetLong(int position) => Kind switch
    {
        DKind.Int => Buffer.I![position],
        DKind.Bool => Buffer.B![position] ? 1 : 0,
        DKind.Float => FloatToLong(Buffer.F![position]),
        _ => ToLong(Buffer.O![position]),
    };

    public bool GetBool(int position) => Kind switch
    {
        DKind.Bool => Buffer.B![position],
        DKind.Int => Buffer.I![position] != 0,
        DKind.Float => Buffer.F![position] != 0,
        _ => Buffer.O![position].IsTruthy(),
    };

    public PyObject GetBoxed(int position) => Kind switch
    {
        DKind.Float => new PyFloat(Buffer.F![position]),
        DKind.Int => PyInt.From(Buffer.I![position]),
        DKind.Bool => PyBool.Of(Buffer.B![position]),
        _ => Buffer.O![position],
    };

    /// <summary>Stores a Python value, converting it to this array's kind.</summary>
    public void SetBoxed(int position, PyObject value)
    {
        switch (Kind)
        {
            case DKind.Float:
                Buffer.F![position] = ToDouble(value);
                break;
            case DKind.Int:
                Buffer.I![position] = value is PyFloat number ? FloatToLong(number.Value) : ToLong(value);
                break;
            case DKind.Bool:
                Buffer.B![position] = value.IsTruthy();
                break;
            case DKind.Str:
                var text = value is PyStr s ? s.Value : value.Display();
                Buffer.O![position] = new PyStr(text.Length > DType.Width ? text[..DType.Width] : text);
                break;
            default:
                Buffer.O![position] = value;
                break;
        }
    }

    /// <summary>Copies one element of <paramref name="source"/> into this array, converting.</summary>
    public void SetFrom(int position, NdArray source, int sourcePosition)
    {
        switch (Kind)
        {
            case DKind.Float:
                Buffer.F![position] = source.GetDouble(sourcePosition);
                break;
            case DKind.Int:
                Buffer.I![position] = source.GetLong(sourcePosition);
                break;
            case DKind.Bool:
                Buffer.B![position] = source.GetBool(sourcePosition);
                break;
            default:
                SetBoxed(position, source.GetBoxed(sourcePosition));
                break;
        }
    }

    /// <summary>The canonical Python scalar of <paramref name="kind"/> for a value.</summary>
    public static PyObject Box(DKind kind, PyObject value) => kind switch
    {
        DKind.Float => new PyFloat(ToDouble(value)),
        DKind.Int => PyInt.From(value is PyFloat number ? FloatToLong(number.Value) : ToLong(value)),
        DKind.Bool => PyBool.Of(value.IsTruthy()),
        DKind.Str => value is PyStr ? value : new PyStr(value.Display()),
        _ => value,
    };

    public static double ToDouble(PyObject value) => value switch
    {
        PyFloat number => number.Value,
        PyInt integer => Numbers.ToDouble(integer.Value),
        NdArray { Size: 1 } array => array.GetDouble(array.Offsets()[0]),
        PyStr text => double.TryParse(text.Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : text.Value.Trim().ToLowerInvariant() switch
            {
                "nan" => double.NaN,
                "inf" or "infinity" => double.PositiveInfinity,
                "-inf" or "-infinity" => double.NegativeInfinity,
                _ => throw new PyRaise(PyErrors.ValueError($"could not convert string to float: {text.Repr()}")),
            },
        PyNone => double.NaN,
        _ => throw new PyRaise(PyErrors.TypeError(
            $"float() argument must be a string or a real number, not '{value.TypeName}'")),
    };

    public static long ToLong(PyObject value)
    {
        switch (value)
        {
            case PyInt integer:
                if (integer.Value < long.MinValue || integer.Value > long.MaxValue)
                {
                    throw new PyRaise(new PyException(
                        PyExceptionType.OverflowError, "Python int too large to convert to C long"));
                }

                return (long)integer.Value;

            case PyFloat number:
                return FloatToLong(number.Value);

            case NdArray { Size: 1 } array:
                return array.GetLong(array.Offsets()[0]);

            case PyStr text when long.TryParse(text.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed):
                return parsed;

            case PyStr text:
                throw new PyRaise(PyErrors.ValueError($"invalid literal for int() with base 10: {text.Repr()}"));

            default:
                throw new PyRaise(PyErrors.TypeError(
                    $"int() argument must be a string, a bytes-like object or a real number, not '{value.TypeName}'"));
        }
    }

    /// <summary>Truncates a float to an integer, as a cast to int64 does.</summary>
    public static long FloatToLong(double value)
    {
        if (double.IsNaN(value))
        {
            throw new PyRaise(PyErrors.ValueError("cannot convert float NaN to integer"));
        }

        if (double.IsInfinity(value))
        {
            throw new PyRaise(new PyException(PyExceptionType.OverflowError, "cannot convert float infinity to integer"));
        }

        // Out of range is undefined in numpy (it yields the minimum on x86); saturate.
        return value >= 9.2233720368547758e18 ? long.MaxValue
            : value <= -9.2233720368547758e18 ? long.MinValue
            : (long)Math.Truncate(value);
    }

    // ---- whole-array reads ----

    public double[] ToDoubles()
    {
        if (Kind == DKind.Float && IsContiguous)
        {
            return Buffer.F!.AsSpan(Offset, Size).ToArray();
        }

        var offsets = Offsets();
        var result = new double[offsets.Length];

        for (var k = 0; k < offsets.Length; k++)
        {
            result[k] = GetDouble(offsets[k]);
        }

        return result;
    }

    public long[] ToLongs()
    {
        if (Kind == DKind.Int && IsContiguous)
        {
            return Buffer.I!.AsSpan(Offset, Size).ToArray();
        }

        var offsets = Offsets();
        var result = new long[offsets.Length];

        for (var k = 0; k < offsets.Length; k++)
        {
            result[k] = GetLong(offsets[k]);
        }

        return result;
    }

    public bool[] ToBools()
    {
        var offsets = Offsets();
        var result = new bool[offsets.Length];

        for (var k = 0; k < offsets.Length; k++)
        {
            result[k] = GetBool(offsets[k]);
        }

        return result;
    }

    public PyObject[] ToBoxed()
    {
        var offsets = Offsets();
        var result = new PyObject[offsets.Length];

        for (var k = 0; k < offsets.Length; k++)
        {
            result[k] = GetBoxed(offsets[k]);
        }

        return result;
    }

    /// <summary>A contiguous copy of the elements as <paramref name="dtype"/>.</summary>
    public NdArray AsType(NdDType dtype)
    {
        var shape = (int[])Shape.Clone();

        switch (dtype.Kind)
        {
            case DKind.Float: return FromDoubles(ToDoubles(), shape);
            case DKind.Int: return FromLongs(ToLongs(), shape);
            case DKind.Bool: return FromBools(ToBools(), shape);
            case DKind.Str:
            {
                var values = ToBoxed().Select(static v => (PyObject)(v is PyStr ? v : new PyStr(FormatScalar(v)))).ToArray();

                // Width 0 is numpy's unsized `str`: the width comes from the data. An explicit
                // width truncates, as numpy's does.
                if (dtype.Width == 0)
                {
                    return FromObjects(DKind.Str, values, shape);
                }

                var target = new NdArray(dtype, new NdBuffer(DKind.Str, values.Length), shape, ContiguousStrides(shape), 0);

                for (var k = 0; k < values.Length; k++)
                {
                    target.SetBoxed(k, values[k]);
                }

                return target;
            }

            default: return FromObjects(DKind.Object, ToBoxed(), shape);
        }
    }

    /// <summary>A contiguous copy.</summary>
    public NdArray Copy() => AsType(DType);

    /// <summary>This array, or a contiguous copy when it is a strided view.</summary>
    public NdArray Contiguous() => IsContiguous ? this : Copy();

    /// <summary>A view with another shape and strides over the same buffer.</summary>
    public NdArray View(int[] shape, int[] strides, int offset) => new(DType, Buffer, shape, strides, offset);

    /// <summary>A broadcast view: the same elements, repeated along new or unit dimensions.</summary>
    public NdArray BroadcastTo(int[] shape)
    {
        if (Shape.SequenceEqual(shape))
        {
            return this;
        }

        // A broadcast view is read element by element, so it counts against the cap as an
        // array of its full size would.
        CheckedSize(shape);
        var strides = new int[shape.Length];
        var shift = shape.Length - NDim;

        if (shift < 0)
        {
            throw BroadcastError(Shape, shape);
        }

        for (var d = 0; d < shape.Length; d++)
        {
            var source = d - shift;

            if (source < 0)
            {
                strides[d] = 0;
            }
            else if (Shape[source] == shape[d])
            {
                strides[d] = Strides[source];
            }
            else if (Shape[source] == 1)
            {
                strides[d] = 0;
            }
            else
            {
                throw new PyRaise(PyErrors.ValueError(
                    $"could not broadcast input array from shape {ShapeText(Shape)} into shape {ShapeText(shape)}"));
            }
        }

        return View(shape, strides, Offset);
    }

    /// <summary>The broadcast shape of several shapes, or the numpy error naming them.</summary>
    public static int[] BroadcastShapes(params int[][] shapes)
    {
        var nd = shapes.Max(static s => s.Length);
        var result = new int[nd];

        for (var d = 0; d < nd; d++)
        {
            var size = 1;

            foreach (var shape in shapes)
            {
                var source = d - (nd - shape.Length);

                if (source < 0)
                {
                    continue;
                }

                var dimension = shape[source];

                if (dimension == size || dimension == 1)
                {
                    continue;
                }

                if (size != 1)
                {
                    throw new PyRaise(PyErrors.ValueError(
                        "operands could not be broadcast together with shapes "
                        + string.Concat(shapes.Select(static s => ShapeText(s) + " "))));
                }

                size = dimension;
            }

            result[d] = size;
        }

        return result;
    }

    private static PyRaise BroadcastError(int[] from, int[] to) =>
        new(PyErrors.ValueError(
            $"could not broadcast input array from shape {ShapeText(from)} into shape {ShapeText(to)}"));

    /// <summary>Writes the shape as numpy prints it: <c>(2,)</c>, <c>(2, 3)</c>.</summary>
    public static string FormatShape(int[] shape) =>
        shape.Length == 1
            ? $"({shape[0].ToString(CultureInfo.InvariantCulture)},)"
            : "(" + string.Join(", ", shape.Select(static d => d.ToString(CultureInfo.InvariantCulture))) + ")";

    public PyTuple ShapeTuple() => new([.. Shape.Select(static d => (PyObject)PyInt.From(d))]);

    /// <summary>Reshapes, as a view when the elements are contiguous.</summary>
    public NdArray Reshape(int[] requested)
    {
        var shape = ResolveReshape(requested, Size, Shape);
        var source = Contiguous();
        return source.View(shape, ContiguousStrides(shape), source.Offset);
    }

    public static int[] ResolveReshape(int[] requested, int size, int[] from)
    {
        var shape = (int[])requested.Clone();
        var unknown = Array.IndexOf(shape, -1);

        if (shape.Count(static d => d == -1) > 1)
        {
            throw new PyRaise(PyErrors.ValueError("can only specify one unknown dimension"));
        }

        long known = 1;

        for (var d = 0; d < shape.Length; d++)
        {
            if (d != unknown)
            {
                if (shape[d] < 0)
                {
                    throw new PyRaise(PyErrors.ValueError("negative dimensions not allowed"));
                }

                known *= shape[d];
            }
        }

        if (unknown >= 0)
        {
            if (known == 0 || size % known != 0)
            {
                throw new PyRaise(PyErrors.ValueError(
                    $"cannot reshape array of size {size} into shape {FormatRequested(requested)}"));
            }

            shape[unknown] = (int)(size / known);
        }
        else if (known != size)
        {
            throw new PyRaise(PyErrors.ValueError(
                $"cannot reshape array of size {size} into shape {FormatRequested(requested)}"));
        }

        _ = from;
        return shape;
    }

    private static string FormatRequested(int[] shape) => ShapeText(shape);

    /// <summary>The shape as numpy's own error messages spell it: <c>(2,3)</c>, no spaces.</summary>
    public static string ShapeText(int[] shape) =>
        shape.Length == 1
            ? $"({shape[0].ToString(CultureInfo.InvariantCulture)},)"
            : "(" + string.Join(",", shape.Select(static d => d.ToString(CultureInfo.InvariantCulture))) + ")";

    /// <summary>Permutes the axes, as a view.</summary>
    public NdArray Transpose(int[]? axes = null)
    {
        var permutation = axes ?? [.. Enumerable.Range(0, NDim).Reverse()];

        if (permutation.Length != NDim)
        {
            throw new PyRaise(PyErrors.ValueError("axes don't match array"));
        }

        var seen = new bool[NDim];
        var shape = new int[NDim];
        var strides = new int[NDim];

        for (var d = 0; d < NDim; d++)
        {
            var axis = NormalizeAxis(permutation[d], NDim);

            if (seen[axis])
            {
                throw new PyRaise(PyErrors.ValueError("repeated axis in transpose"));
            }

            seen[axis] = true;
            shape[d] = Shape[axis];
            strides[d] = Strides[axis];
        }

        return View(shape, strides, Offset);
    }

    /// <summary>Resolves a possibly negative axis, raising numpy's <c>AxisError</c> wording.</summary>
    public static int NormalizeAxis(int axis, int ndim)
    {
        if (axis < -ndim || axis >= ndim)
        {
            throw new PyRaise(new PyException(
                NumpyModule.AxisError,
                $"axis {axis.ToString(CultureInfo.InvariantCulture)} is out of bounds for array of dimension {ndim.ToString(CultureInfo.InvariantCulture)}"));
        }

        return axis < 0 ? axis + ndim : axis;
    }

    /// <summary>The element at a flat row-major index.</summary>
    public PyObject FlatItem(int index)
    {
        var position = Offset;

        for (var d = NDim - 1; d >= 0; d--)
        {
            var dimension = Shape[d];
            position += index % dimension * Strides[d];
            index /= dimension;
        }

        return GetBoxed(position);
    }

    // ---- the object protocol ----

    public override bool IsTruthy()
    {
        if (Size == 1)
        {
            return GetBool(Offsets()[0]);
        }

        throw new PyRaise(PyErrors.ValueError(Size == 0
            ? "The truth value of an empty array is ambiguous. Use `array.size > 0` to check that an array is not empty."
            : "The truth value of an array with more than one element is ambiguous. Use a.any() or a.all()"));
    }

    public override int? Length() =>
        NDim == 0 ? throw new PyRaise(PyErrors.TypeError("len() of unsized object")) : Shape[0];

    public override IEnumerable<PyObject>? Iterate()
    {
        if (NDim == 0)
        {
            throw new PyRaise(PyErrors.TypeError("iteration over a 0-d array"));
        }

        return IterateRows();
    }

    private IEnumerable<PyObject> IterateRows()
    {
        for (var i = 0; i < Shape[0]; i++)
        {
            yield return Row(i);
        }
    }

    /// <summary><c>a[i]</c> for a non-negative <paramref name="i"/>: a scalar or a view.</summary>
    public PyObject Row(int i)
    {
        var position = Offset + (i * Strides[0]);

        return NDim == 1
            ? GetBoxed(position)
            : View(Shape[1..], Strides[1..], position);
    }

    public override bool Contains(PyObject item)
    {
        var mask = Ufuncs.Compare("==", this, item);
        return mask is NdArray array ? array.ToBools().Any(static b => b) : mask.IsTruthy();
    }

    public override BigInteger PyHash() =>
        throw new PyRaise(PyErrors.TypeError("unhashable type: 'numpy.ndarray'"));

    public override string Repr() => NumpyModule.Guard("repr", () => NdFormat.Repr(this));

    public override string Display() => NumpyModule.Guard("str", () => NdFormat.Str(this));

    public override string? PyFormat(string spec)
    {
        if (spec.Length == 0)
        {
            return Display();
        }

        // A one-element array formats as its element; anything else refuses, as numpy's does.
        if (Size == 1)
        {
            return StringFormatter.Format(GetBoxed(Offsets()[0]), spec);
        }

        throw new PyRaise(PyErrors.TypeError("unsupported format string passed to numpy.ndarray.__format__"));
    }

    /// <summary>Formats one element the way <c>str()</c> of a numpy scalar reads.</summary>
    public static string FormatScalar(PyObject value) => value.Display();
}
