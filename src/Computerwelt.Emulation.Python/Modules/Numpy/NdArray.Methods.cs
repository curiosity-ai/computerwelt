using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules.Numpy;

/// <summary>The attributes and methods of an array.</summary>
internal sealed partial class NdArray
{
    public override PyObject? GetAttribute(string name) => name switch
    {
        "shape" => ShapeTuple(),
        "ndim" => PyInt.From(NDim),
        "size" => PyInt.From(Size),
        "dtype" => DType,
        "T" => Transpose(),
        "mT" => SwapAxes(-2, -1),
        "flat" => new NdFlat(this),
        "itemsize" => DType.GetAttribute("itemsize"),
        "nbytes" => PyInt.From(Size * ((PyInt)DType.GetAttribute("itemsize")!).ToIndex()),
        "strides" => new PyTuple([.. Strides.Select(s => (PyObject)PyInt.From(s * ((PyInt)DType.GetAttribute("itemsize")!).ToIndex()))]),
        "real" => this,
        "__class__" => Type,
        _ => Method(name),
    };

    public override bool SetAttribute(string name, PyObject value)
    {
        if (name == "shape")
        {
            throw new PyRaise(new PyException(
                PyExceptionType.AttributeError,
                "assigning to 'shape' is not supported here: use reshape()"));
        }

        if (name is "dtype" or "ndim" or "size" or "T")
        {
            throw new PyRaise(new PyException(
                PyExceptionType.AttributeError,
                $"attribute '{name}' of 'numpy.ndarray' objects is not writable"));
        }

        return false;
    }

    private PyBoundMethod? Method(string name)
    {
        Func<PyObject[], PyDict?, PyObject>? body = name switch
        {
            "tolist" => (a, k) => NoArgs(name, a, k, ToList()),
            "item" => (a, k) => ItemOf(a, k),
            "copy" => (a, k) => NoArgs(name, a, k, Copy()),
            "reshape" => (a, k) => Reshape(ShapeArguments("reshape", a, k)),
            "flatten" => (a, k) => NoArgs(name, a, k, Copy().Reshape([-1])),
            "ravel" => (a, k) => NoArgs(name, a, k, Reshape([-1])),
            "transpose" => (a, k) => a.Length == 0 ? Transpose() : Transpose(ShapeArguments("transpose", a, k)),
            "swapaxes" => (a, k) => SwapAxes(NdConvert.ToInt(a[0]), NdConvert.ToInt(a[1])),
            "squeeze" => (a, k) => ArrayFunctions.Squeeze(this, new NpArgs(name, a, k, "axis").Optional("axis")),
            "astype" => (a, k) =>
            {
                var args = new NpArgs(name, a, k, "dtype", "order", "casting", "subok", "copy");
                return AsType(args.DType() ?? NdDType.Float64);
            },
            "fill" => (a, k) =>
            {
                Assign(Offsets(), Shape, new NpArgs(name, a, k, "value").Required("value"));
                return PyNone.Instance;
            },
            "sort" => (a, k) =>
            {
                var args = new NpArgs(name, a, k, "axis", "kind", "order");
                var sorted = ArrayFunctions.Sort(this, args.Has("axis") ? args["axis"] : PyInt.From(-1));
                Assign(Offsets(), Shape, sorted);
                return PyNone.Instance;
            },
            "argsort" => (a, k) =>
            {
                var args = new NpArgs(name, a, k, "axis", "kind", "order");
                return ArrayFunctions.ArgSort(this, args.Has("axis") ? args["axis"] : PyInt.From(-1));
            },
            "nonzero" => (a, k) => NoArgs(name, a, k, new PyTuple([.. NonZero(this)])),
            "clip" => (a, k) =>
            {
                var args = new NpArgs(name, a, k, "min", "max");
                return Ufuncs.Clip(this, args.Optional("min"), args.Optional("max"));
            },
            "round" => (a, k) => NdConvert.Wrap(Ufuncs.Round(this, new NpArgs(name, a, k, "decimals").Int("decimals", 0))),
            "dot" => (a, k) => LinAlg.Dot(this, NdConvert.ToArray(new NpArgs(name, a, k, "b").Required("b"))),
            "diagonal" => (a, k) => ArrayFunctions.Diagonal(this, new NpArgs(name, a, k, "offset").Int("offset", 0)),
            "trace" => (a, k) => Reductions.Sum(ArrayFunctions.Diagonal(this, new NpArgs(name, a, k, "offset").Int("offset", 0)), null, false),
            "repeat" => (a, k) => NumpyModule.Call("repeat", [this, .. a], k),
            "take" => (a, k) => NumpyModule.Call("take", [this, .. a], k),
            "searchsorted" => (a, k) => NumpyModule.Call("searchsorted", [this, .. a], k),
            "ptp" => (a, k) => NumpyModule.Call("ptp", [this, .. a], k),
            "conj" or "conjugate" => (a, k) => NoArgs(name, a, k, this),
            "view" => (a, k) => NoArgs(name, a, k, View(Shape, Strides, Offset)),

            "sum" or "mean" or "std" or "var" or "min" or "max" or "argmin" or "argmax" or "prod" or "any"
                or "all" or "cumsum" or "cumprod" => (a, k) => NumpyModule.Call(name, [this, .. a], k),
            _ => null,
        };

        return body is null ? null : new PyBoundMethod(name, this, (_, arguments, keywords) => NumpyModule.Guard(name, () => body(arguments, keywords)));
    }

    private static PyObject NoArgs(string name, PyObject[] arguments, PyDict? keywords, PyObject result)
    {
        if (arguments.Length > 0 || keywords is { Count: > 0 })
        {
            throw new PyRaise(PyErrors.TypeError($"{name}() takes no arguments"));
        }

        return result;
    }

    /// <summary>A shape given as separate integers, one tuple, or one list.</summary>
    private static int[] ShapeArguments(string name, PyObject[] arguments, PyDict? keywords)
    {
        if (keywords is { Count: > 0 })
        {
            foreach (var (key, value) in keywords.Entries)
            {
                if (key.Display() is "order" or "copy")
                {
                    continue;
                }

                if (key.Display() == "shape" && arguments.Length == 0)
                {
                    return NdConvert.ToShape(value);
                }

                throw new PyRaise(PyErrors.TypeError($"{name}() got an unexpected keyword argument '{key.Display()}'"));
            }
        }

        if (arguments.Length == 1 && arguments[0] is not PyInt)
        {
            return NdConvert.ToShape(arguments[0]);
        }

        return [.. arguments.Select(NdConvert.ToInt)];
    }

    public NdArray SwapAxes(int first, int second)
    {
        var a = NormalizeAxis(first, NDim);
        var b = NormalizeAxis(second, NDim);
        var permutation = Enumerable.Range(0, NDim).ToArray();
        (permutation[a], permutation[b]) = (permutation[b], permutation[a]);
        return Transpose(permutation);
    }

    /// <summary><c>tolist()</c>: nested lists of Python scalars.</summary>
    public PyObject ToList()
    {
        if (NDim == 0)
        {
            return GetBoxed(Offset);
        }

        var items = new List<PyObject>(Shape[0]);

        for (var i = 0; i < Shape[0]; i++)
        {
            items.Add(NDim == 1 ? GetBoxed(Offset + (i * Strides[0])) : ((NdArray)Row(i)).ToList());
        }

        return new PyList(items);
    }

    private PyObject ItemOf(PyObject[] arguments, PyDict? keywords)
    {
        PyBuiltinFunction.RejectKeywords("item", keywords);

        if (arguments.Length == 0)
        {
            return Size == 1
                ? GetBoxed(Offset)
                : throw new PyRaise(PyErrors.ValueError("can only convert an array of size 1 to a Python scalar"));
        }

        if (arguments.Length == 1 && arguments[0] is PyInt)
        {
            var index = NdConvert.ToInt(arguments[0]);
            var resolved = index < 0 ? index + Size : index;

            return resolved < 0 || resolved >= Size
                ? throw new PyRaise(PyErrors.IndexError($"index {index} is out of bounds for size {Size}"))
                : FlatItem(resolved);
        }

        var result = GetItem(new PyTuple(arguments.Length == 1 && arguments[0] is PyTuple t ? t.Items : arguments));
        return result is NdArray ? throw new PyRaise(PyErrors.ValueError("incorrect number of indices for array")) : result;
    }
}

/// <summary><c>a.flat</c>: the elements in row-major order, readable and writable by index.</summary>
internal sealed class NdFlat(NdArray array) : PyObject
{
    public override string TypeName => "numpy.flatiter";

    public override string Repr() => "<numpy.flatiter object>";

    public override int? Length() => array.Size;

    public override IEnumerable<PyObject>? Iterate() => array.ToBoxed();

    private NdArray Flat => array.IsContiguous
        ? array.View([array.Size], [1], array.Offset)
        : array.Reshape([-1]);

    public override PyObject GetItem(PyObject index) => Flat.GetItem(index);

    public override void SetItem(PyObject index, PyObject value)
    {
        if (!array.IsContiguous)
        {
            // A strided array's flat positions are its own offsets, written one by one.
            var offsets = array.Offsets();
            var positions = NdArray.Vector([.. Enumerable.Range(0, offsets.Length).Select(static i => (long)i)]);
            var chosen = NdConvert.ToArray(positions.GetItem(index)).ToLongs();
            array.Assign([.. chosen.Select(i => offsets[i])], [chosen.Length], value);
            return;
        }

        Flat.SetItem(index, value);
    }
}
