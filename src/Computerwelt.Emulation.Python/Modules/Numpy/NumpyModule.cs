using System.Globalization;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules.Numpy;

/// <summary>
/// The <c>numpy</c> module: a core subset of numpy, in managed C#.
/// </summary>
/// <remarks>
/// <para>
/// No native code, no BLAS, no memory mapping, no pickling, no file I/O: arrays live in the
/// sandbox's own memory, every function is a C# implementation over
/// <see cref="NdArray"/>, and every failure reaches the program as a Python exception with
/// numpy's wording. One call may not create more than <see cref="NdArray.MaxElements"/>
/// elements, because the instruction limit does not see work done inside a builtin.
/// </para>
/// <para>
/// Built per run, on first import (see <see cref="StandardLibrary.CreateOnDemand"/>), so the
/// global random state is one tenant's.
/// </para>
/// </remarks>
internal static class NumpyModule
{
    /// <summary><c>numpy.exceptions.AxisError</c>, which is both a <c>ValueError</c> and an <c>IndexError</c> in numpy.</summary>
    public static PyExceptionType AxisError { get; } =
        PyExceptionType.DefineHostException("AxisError", PyExceptionType.ValueError);

    private static readonly Dictionary<string, Func<PyObject[], PyDict?, PyObject>> Functions = Build();

    static NumpyModule()
    {
        // `type(a)` asks the registry for the type of a host value by its type name; making
        // that entry the module's own type object is what makes `type(a) is np.ndarray` hold.
        lock (TypeRegistry.All)
        {
            TypeRegistry.All.TryAdd(NdArray.Type.Name, NdArray.Type);
        }
    }

    /// <summary>Calls a module function by name — the route array methods take.</summary>
    public static PyObject Call(string name, PyObject[] arguments, PyDict? keywords) =>
        Guard(name, () => Functions[name](arguments, keywords));

    /// <summary>
    /// Turns a host exception that slipped through into a Python one, so no .NET exception
    /// ever reaches a sandboxed program from inside numpy.
    /// </summary>
    internal static T Guard<T>(string name, Func<T> body)
    {
        try
        {
            return body();
        }
        catch (PyRaise)
        {
            throw;
        }
        catch (OverflowException)
        {
            throw new PyRaise(new PyException(PyExceptionType.OverflowError, $"{name}(): value out of range"));
        }
        catch (OutOfMemoryException)
        {
            throw new PyRaise(new PyException(PyExceptionType.MemoryError, $"{name}(): out of memory"));
        }
        catch (Exception error) when (error is IndexOutOfRangeException or ArgumentException or InvalidCastException
            or InvalidOperationException or DivideByZeroException or NullReferenceException or KeyNotFoundException)
        {
            throw new PyRaise(PyErrors.ValueError($"{name}(): invalid arguments ({error.GetType().Name})"));
        }
    }

    public static PyModuleObject Create(VirtualMachine machine)
    {
        var module = new PyModuleObject("numpy");

        foreach (var (name, implementation) in Functions)
        {
            module.Add(name, new PyBuiltinFunction($"numpy.{name}", (arguments, keywords) =>
                Guard(name, () => implementation(arguments, keywords))));
        }

        module.Add("vectorize", new PyBuiltinFunction("numpy.vectorize", (arguments, keywords) =>
        {
            var args = new NpArgs("vectorize", arguments, keywords, "pyfunc", "otypes", "doc", "excluded", "cache", "signature");
            var function = args.Required("pyfunc");
            return new PyBuiltinFunction("vectorized", (inner, innerKeywords) =>
                Guard("vectorize", () => Vectorized(machine, function, inner, innerKeywords)));
        }));

        module.Add("apply_along_axis", new PyBuiltinFunction("numpy.apply_along_axis", (arguments, keywords) =>
            Guard("apply_along_axis", () => ApplyAlongAxis(machine, arguments, keywords))));

        module.Add("ndarray", NdArray.Type);
        module.Add("dtype", new PyBuiltinFunction("numpy.dtype", static (arguments, keywords) =>
        {
            var args = new NpArgs("dtype", arguments, keywords, "dtype", "align", "copy");
            return NdDType.Resolve(args.Required("dtype")) ?? NdDType.Float64;
        }));

        foreach (var (name, type) in NumpyTypes.Exported)
        {
            module.Add(name, type);
        }

        module.Add("pi", new PyFloat(Math.PI));
        module.Add("e", new PyFloat(Math.E));
        module.Add("euler_gamma", new PyFloat(0.5772156649015329));
        module.Add("inf", new PyFloat(double.PositiveInfinity));
        module.Add("nan", new PyFloat(double.NaN));
        module.Add("newaxis", PyNone.Instance);
        module.Add("__version__", new PyStr("2.0.0"));

        var linalg = new PyModuleObject("numpy.linalg");
        AddLinAlg(linalg);
        module.Add("linalg", linalg);

        var random = new PyModuleObject("numpy.random");
        AddRandom(random);
        module.Add("random", random);

        var exceptions = new PyModuleObject("numpy.exceptions").Add("AxisError", AxisError);
        module.Add("exceptions", exceptions);
        module.Add("AxisError", AxisError);

        return module;
    }

    private static void AddLinAlg(PyModuleObject linalg)
    {
        void Add(string name, Func<PyObject[], PyDict?, PyObject> body) =>
            linalg.Add(name, new PyBuiltinFunction($"numpy.linalg.{name}", (a, k) => Guard(name, () => body(a, k))));

        Add("norm", static (a, k) =>
        {
            var args = new NpArgs("norm", a, k, "x", "ord", "axis", "keepdims");
            return LinAlg.Norm(args.Required("x"), args.Optional("ord"), args.Optional("axis"), args.Bool("keepdims", false));
        });
        Add("inv", static (a, k) => LinAlg.Inv(new NpArgs("inv", a, k, "a").Required("a")));
        Add("det", static (a, k) => LinAlg.Det(new NpArgs("det", a, k, "a").Required("a")));
        Add("solve", static (a, k) =>
        {
            var args = new NpArgs("solve", a, k, "a", "b");
            return LinAlg.Solve(args.Required("a"), args.Required("b"));
        });
        Add("eigh", static (a, k) => LinAlg.Eigh(new NpArgs("eigh", a, k, "a", "UPLO").Required("a"), vectors: true));
        Add("eigvalsh", static (a, k) => LinAlg.Eigh(new NpArgs("eigvalsh", a, k, "a", "UPLO").Required("a"), vectors: false));
        Add("cholesky", static (a, k) => LinAlg.Cholesky(new NpArgs("cholesky", a, k, "a").Required("a")));
        Add("matrix_rank", static (a, k) =>
        {
            var args = new NpArgs("matrix_rank", a, k, "A", "tol", "hermitian", "rtol");
            return LinAlg.MatrixRank(args.Required("A"), args.Optional("tol"));
        });
        Add("lstsq", static (a, k) =>
        {
            var args = new NpArgs("lstsq", a, k, "a", "b", "rcond");
            return LinAlg.Lstsq(args.Required("a"), args.Required("b"));
        });
        Add("matmul", static (a, k) =>
        {
            var args = new NpArgs("matmul", a, k, "x1", "x2");
            return LinAlg.MatMul(args.Array("x1"), args.Array("x2"));
        });
        linalg.Add("LinAlgError", LinAlg.LinAlgError);
    }

    private static void AddRandom(PyModuleObject random)
    {
        var shared = new NpRandomState(generator: false, seed: null);

        foreach (var name in NpRandomState.LegacyMethods)
        {
            random.Add(name, shared.GetAttribute(name)!);
        }

        random.Add("RandomState", new PyType(
            "numpy.random.RandomState",
            static value => value is NpRandomState { IsGenerator: false },
            static (arguments, keywords) =>
                new NpRandomState(false, new NpArgs("RandomState", arguments, keywords, "seed").Optional("seed"))));

        random.Add("Generator", new PyType(
            "numpy.random.Generator",
            static value => value is NpRandomState { IsGenerator: true },
            static (_, _) => throw new PyRaise(PyErrors.TypeError("use numpy.random.default_rng() to create a Generator"))));

        random.Add("default_rng", new PyBuiltinFunction("numpy.random.default_rng", static (arguments, keywords) =>
        {
            var seed = new NpArgs("default_rng", arguments, keywords, "seed").Optional("seed");
            return seed is NpRandomState { IsGenerator: true } existing ? existing : new NpRandomState(true, seed);
        }));
    }

    // ---- helpers ----

    private static NdArray A(PyObject value) => NdConvert.ToArray(value);

    private static PyObject W(NdArray array) => NdConvert.Wrap(array);

    private static PyObject Tuple(params PyObject[] items) => new PyTuple(items);

    private static int[]? Axes(NpArgs args, NdArray array) => NdConvert.ToAxes(args.Optional("axis"), array.NDim);

    private static Dictionary<string, Func<PyObject[], PyDict?, PyObject>> Build()
    {
        var f = new Dictionary<string, Func<PyObject[], PyDict?, PyObject>>(StringComparer.Ordinal);

        // ---- creation ----

        f["array"] = static (a, k) =>
        {
            var args = new NpArgs("array", a, k, "object", "dtype", "copy", "order", "subok", "ndmin", "like");
            var result = NdConvert.NewArray(args.Required("object"), args.DType(), args.Bool("copy", true));
            var ndmin = args.Int("ndmin", 0);
            return ndmin > result.NDim ? result.Reshape([.. Enumerable.Repeat(1, ndmin - result.NDim), .. result.Shape]) : result;
        };
        f["asarray"] = static (a, k) =>
        {
            var args = new NpArgs("asarray", a, k, "a", "dtype", "order", "device", "copy", "like");
            return NdConvert.ToArray(args.Required("a"), args.DType());
        };
        f["asanyarray"] = f["asarray"];
        f["ascontiguousarray"] = static (a, k) =>
        {
            var args = new NpArgs("ascontiguousarray", a, k, "a", "dtype", "like");
            var result = NdConvert.ToArray(args.Required("a"), args.DType());
            return result.NDim == 0 ? result.Reshape([1]) : result.Contiguous();
        };
        f["copy"] = static (a, k) => NdConvert.NewArray(new NpArgs("copy", a, k, "a", "order", "subok").Required("a"), null);

        foreach (var (name, fill) in new[] { ("zeros", 0.0), ("ones", 1.0), ("empty", 0.0) })
        {
            f[name] = (a, k) =>
            {
                var args = new NpArgs(name, a, k, "shape", "dtype", "order", "like");
                var result = NdArray.Zeros(NdConvert.ToShape(args.Required("shape")), args.DType() ?? NdDType.Float64);
                return fill == 0 ? result : Filled(result, new PyFloat(fill));
            };

            f[name + "_like"] = (a, k) =>
            {
                var args = new NpArgs(name + "_like", a, k, "a", "dtype", "order", "subok", "shape");
                var model = args.Array("a");
                var shape = args.Optional("shape") is { } given ? NdConvert.ToShape(given) : model.Shape;
                var result = NdArray.Zeros((int[])shape.Clone(), args.DType() ?? model.DType);
                return fill == 0 ? result : Filled(result, new PyFloat(fill));
            };
        }

        f["full"] = static (a, k) =>
        {
            var args = new NpArgs("full", a, k, "shape", "fill_value", "dtype", "order", "like");
            var value = args.Required("fill_value");
            var dtype = args.DType() ?? NdConvert.ToArray(value).DType;
            return Filled(NdArray.Zeros(NdConvert.ToShape(args.Required("shape")), dtype.Kind == DKind.Str && dtype.Width == 0 ? NdConvert.ToArray(value).DType : dtype), value);
        };
        f["full_like"] = static (a, k) =>
        {
            var args = new NpArgs("full_like", a, k, "a", "fill_value", "dtype", "order", "subok", "shape");
            var model = args.Array("a");
            return Filled(NdArray.Zeros((int[])model.Shape.Clone(), args.DType() ?? model.DType), args.Required("fill_value"));
        };
        f["arange"] = static (a, k) => Arange(new NpArgs("arange", a, k, "start", "stop", "step", "dtype", "like"));
        f["linspace"] = static (a, k) =>
        {
            var args = new NpArgs("linspace", a, k, "start", "stop", "num", "endpoint", "retstep", "dtype", "axis");
            var num = args.Int("num", 50);

            if (num < 0)
            {
                throw new PyRaise(PyErrors.ValueError($"Number of samples, {num}, must be non-negative."));
            }

            NdArray.CheckedSize([num]);
            var endpoint = args.Bool("endpoint", true);
            var start = NdArray.ToDouble(args.Required("start"));
            var stop = NdArray.ToDouble(args.Required("stop"));
            var values = ArrayFunctions.Linspace(start, stop, num, endpoint);
            NdArray result = NdArray.Vector(values);

            if (args.DType() is { Kind: DKind.Int } integer)
            {
                result = NdArray.FromDoubles([.. values.Select(Math.Floor)], [num]).AsType(integer);
            }

            if (!args.Bool("retstep", false))
            {
                return result;
            }

            var divisor = endpoint ? num - 1 : num;
            return Tuple(result, new PyFloat(divisor > 0 ? (stop - start) / divisor : double.NaN));
        };
        f["logspace"] = static (a, k) =>
        {
            var args = new NpArgs("logspace", a, k, "start", "stop", "num", "endpoint", "base", "dtype", "axis");
            var num = args.Int("num", 50);
            NdArray.CheckedSize([num]);
            var values = ArrayFunctions.Linspace(args.Double("start", 0), args.Double("stop", 0), num, args.Bool("endpoint", true));
            var b = args.Double("base", 10);
            return NdArray.Vector([.. values.Select(v => Math.Pow(b, v))]);
        };
        f["eye"] = static (a, k) =>
        {
            var args = new NpArgs("eye", a, k, "N", "M", "k", "dtype", "order", "like");
            var n = NdConvert.ToInt(args.Required("N"));
            var m = args.Optional("M") is { } columns ? NdConvert.ToInt(columns) : n;
            var diagonal = args.Int("k", 0);
            var result = NdArray.Zeros([n, m], args.DType() ?? NdDType.Float64);

            for (var i = 0; i < n; i++)
            {
                var j = i + diagonal;

                if (j >= 0 && j < m)
                {
                    result.SetBoxed((i * m) + j, PyInt.From(1));
                }
            }

            return result;
        };
        f["identity"] = static (a, k) =>
        {
            var args = new NpArgs("identity", a, k, "n", "dtype", "like");
            return Functions["eye"]([args.Required("n")], Keywords(("dtype", args["dtype"])));
        };
        f["diag"] = static (a, k) =>
        {
            var args = new NpArgs("diag", a, k, "v", "k");
            var v = args.Array("v");
            var offset = args.Int("k", 0);

            if (v.NDim == 2)
            {
                return ArrayFunctions.Diagonal(v, offset);
            }

            if (v.NDim != 1)
            {
                throw new PyRaise(PyErrors.ValueError("Input must be 1- or 2-d."));
            }

            var n = v.Shape[0] + Math.Abs(offset);
            var result = NdArray.Zeros([n, n], v.DType);

            for (var i = 0; i < v.Shape[0]; i++)
            {
                var (row, column) = offset >= 0 ? (i, i + offset) : (i - offset, i);
                result.SetFrom((row * n) + column, v, v.Offset + (i * v.Strides[0]));
            }

            return result;
        };
        f["diagonal"] = static (a, k) =>
        {
            var args = new NpArgs("diagonal", a, k, "a", "offset", "axis1", "axis2");
            return ArrayFunctions.Diagonal(args.Array("a"), args.Int("offset", 0));
        };
        f["trace"] = static (a, k) =>
        {
            var args = new NpArgs("trace", a, k, "a", "offset", "axis1", "axis2", "dtype", "out");
            return Reductions.Sum(ArrayFunctions.Diagonal(args.Array("a"), args.Int("offset", 0)), null, false);
        };
        f["tril"] = static (a, k) => Triangle(new NpArgs("tril", a, k, "m", "k"), lower: true);
        f["triu"] = static (a, k) => Triangle(new NpArgs("triu", a, k, "m", "k"), lower: false);
        f["meshgrid"] = static (a, k) =>
        {
            var indexing = "xy";

            foreach (var (key, value) in k?.Entries ?? [])
            {
                if (key.Display() == "indexing")
                {
                    indexing = value.Display();
                }
                else if (key.Display() is not ("sparse" or "copy"))
                {
                    throw new PyRaise(PyErrors.TypeError($"meshgrid() got an unexpected keyword argument '{key.Display()}'"));
                }
            }

            if (indexing is not ("xy" or "ij"))
            {
                throw new PyRaise(PyErrors.ValueError("Valid values for `indexing` are 'xy' and 'ij'."));
            }

            var vectors = a.Select(static v => A(v).Reshape([-1])).ToArray();
            var n = vectors.Length;
            var shape = vectors.Select(static v => v.Shape[0]).ToArray();

            if (indexing == "xy" && n >= 2)
            {
                (shape[0], shape[1]) = (shape[1], shape[0]);
            }

            var grids = new List<PyObject>();

            for (var i = 0; i < n; i++)
            {
                var axis = indexing == "xy" && n >= 2 && i < 2 ? 1 - i : i;
                var view = Enumerable.Repeat(1, n).ToArray();
                view[axis] = vectors[i].Shape[0];
                grids.Add(vectors[i].Reshape(view).BroadcastTo(shape).Copy());
            }

            return new PyTuple(grids);
        };
        f["tile"] = static (a, k) =>
        {
            var args = new NpArgs("tile", a, k, "A", "reps");
            return ArrayFunctions.Tile(args.Array("A"), NdConvert.ToShape(args.Required("reps")));
        };
        f["repeat"] = static (a, k) =>
        {
            var args = new NpArgs("repeat", a, k, "a", "repeats", "axis");
            return ArrayFunctions.Repeat(args.Array("a"), args.Required("repeats"), args.Optional("axis"));
        };

        // ---- shape ----

        f["reshape"] = static (a, k) =>
        {
            var args = new NpArgs("reshape", a, k, "a", "shape", "order", "newshape", "copy");
            return args.Array("a").Reshape(NdConvert.ToShape(args["shape"] ?? args.Required("newshape")));
        };
        f["ravel"] = static (a, k) => new NpArgs("ravel", a, k, "a", "order").Array("a").Reshape([-1]);
        f["transpose"] = static (a, k) =>
        {
            var args = new NpArgs("transpose", a, k, "a", "axes");
            return args.Array("a").Transpose(args.Optional("axes") is { } axes ? NdConvert.ToShape(axes) : null);
        };
        f["swapaxes"] = static (a, k) =>
        {
            var args = new NpArgs("swapaxes", a, k, "a", "axis1", "axis2");
            return args.Array("a").SwapAxes(NdConvert.ToInt(args.Required("axis1")), NdConvert.ToInt(args.Required("axis2")));
        };
        f["moveaxis"] = static (a, k) =>
        {
            var args = new NpArgs("moveaxis", a, k, "a", "source", "destination");
            var array = args.Array("a");
            var source = NdConvert.ToShape(args.Required("source")).Select(s => NdArray.NormalizeAxis(s, array.NDim)).ToArray();
            var destination = NdConvert.ToShape(args.Required("destination")).Select(d => NdArray.NormalizeAxis(d, array.NDim)).ToArray();

            if (source.Length != destination.Length)
            {
                throw new PyRaise(PyErrors.ValueError("`source` and `destination` arguments must have the same number of elements"));
            }

            var order = Enumerable.Range(0, array.NDim).Where(d => !source.Contains(d)).ToList();

            foreach (var (d, s) in destination.Zip(source).OrderBy(static p => p.First))
            {
                order.Insert(d, s);
            }

            return array.Transpose([.. order]);
        };
        f["squeeze"] = static (a, k) =>
        {
            var args = new NpArgs("squeeze", a, k, "a", "axis");
            return ArrayFunctions.Squeeze(args.Array("a"), args.Optional("axis"));
        };
        f["expand_dims"] = static (a, k) =>
        {
            var args = new NpArgs("expand_dims", a, k, "a", "axis");
            return ArrayFunctions.ExpandDims(args.Array("a"), args.Required("axis"));
        };

        foreach (var d in new[] { 1, 2, 3 })
        {
            f[$"atleast_{d}d"] = (a, k) =>
            {
                PyBuiltinFunction.RejectKeywords($"atleast_{d}d", k);
                var results = a.Select(v => (PyObject)ArrayFunctions.AtLeast(A(v), d)).ToArray();
                return results.Length == 1 ? results[0] : new PyTuple(results);
            };
        }

        f["shape"] = static (a, k) => new NpArgs("shape", a, k, "a").Array("a").ShapeTuple();
        f["ndim"] = static (a, k) => PyInt.From(new NpArgs("ndim", a, k, "a").Array("a").NDim);
        f["size"] = static (a, k) =>
        {
            var args = new NpArgs("size", a, k, "a", "axis");
            var array = args.Array("a");
            return PyInt.From(args.Optional("axis") is { } axis ? array.Shape[NdArray.NormalizeAxis(NdConvert.ToInt(axis), array.NDim)] : array.Size);
        };
        f["isscalar"] = static (a, k) =>
            PyBool.Of(new NpArgs("isscalar", a, k, "element").Required("element") is PyInt or PyFloat or PyStr or PyBytes);
        f["issubdtype"] = static (a, k) =>
        {
            var args = new NpArgs("issubdtype", a, k, "arg1", "arg2");
            var kind = NdDType.Resolve(args.Required("arg1"))?.Kind ?? DKind.Float;
            return PyBool.Of(NumpyTypes.IsSubDType(kind, args.Required("arg2")));
        };
        f["finfo"] = static (a, k) =>
        {
            _ = new NpArgs("finfo", a, k, "dtype");
            return new NpNamespace("finfo(resolution=1e-15, min=-1.7976931348623157e+308, max=1.7976931348623157e+308, dtype=float64)")
                .Add("eps", new PyFloat(2.220446049250313e-16))
                .Add("max", new PyFloat(double.MaxValue))
                .Add("min", new PyFloat(double.MinValue))
                .Add("tiny", new PyFloat(2.2250738585072014e-308))
                .Add("smallest_normal", new PyFloat(2.2250738585072014e-308))
                .Add("resolution", new PyFloat(1e-15))
                .Add("precision", PyInt.From(15))
                .Add("bits", PyInt.From(64))
                .Add("dtype", NdDType.Float64);
        };
        f["iinfo"] = static (a, k) =>
        {
            _ = new NpArgs("iinfo", a, k, "int_type");
            return new NpNamespace("iinfo(min=-9223372036854775808, max=9223372036854775807, dtype=int64)")
                .Add("max", PyInt.From(long.MaxValue))
                .Add("min", PyInt.From(long.MinValue))
                .Add("bits", PyInt.From(64))
                .Add("dtype", NdDType.Int64);
        };

        // ---- joining and splitting ----

        f["concatenate"] = static (a, k) =>
        {
            var args = new NpArgs("concatenate", a, k, "arrays", "axis", "out", "dtype", "casting");
            var axis = args.Has("axis") ? args["axis"] : PyInt.From(0);
            var result = ArrayFunctions.Concatenate(ArrayFunctions.Arrays(args.Required("arrays"), "concatenate"), axis);
            return args.DType() is { } dtype ? result.AsType(dtype) : result;
        };
        f["stack"] = static (a, k) =>
        {
            var args = new NpArgs("stack", a, k, "arrays", "axis", "out", "dtype", "casting");
            return ArrayFunctions.Stack(ArrayFunctions.Arrays(args.Required("arrays"), "stack"), args.Optional("axis"));
        };
        f["vstack"] = static (a, k) =>
        {
            var args = new NpArgs("vstack", a, k, "tup", "dtype", "casting");
            var arrays = ArrayFunctions.Arrays(args.Required("tup"), "vstack").Select(static x => ArrayFunctions.AtLeast(x, 2)).ToList();
            return ArrayFunctions.Concatenate(arrays, PyInt.From(0));
        };
        f["row_stack"] = f["vstack"];
        f["hstack"] = static (a, k) =>
        {
            var args = new NpArgs("hstack", a, k, "tup", "dtype", "casting");
            var arrays = ArrayFunctions.Arrays(args.Required("tup"), "hstack").Select(static x => ArrayFunctions.AtLeast(x, 1)).ToList();
            return ArrayFunctions.Concatenate(arrays, PyInt.From(arrays[0].NDim == 1 ? 0 : 1));
        };
        f["dstack"] = static (a, k) =>
        {
            var args = new NpArgs("dstack", a, k, "tup");
            var arrays = ArrayFunctions.Arrays(args.Required("tup"), "dstack").Select(static x => ArrayFunctions.AtLeast(x, 3)).ToList();
            return ArrayFunctions.Concatenate(arrays, PyInt.From(2));
        };
        f["column_stack"] = static (a, k) =>
        {
            var args = new NpArgs("column_stack", a, k, "tup");
            var arrays = ArrayFunctions.Arrays(args.Required("tup"), "column_stack")
                .Select(static x => x.NDim < 2 ? x.Reshape([-1, 1]) : x).ToList();
            return ArrayFunctions.Concatenate(arrays, PyInt.From(1));
        };
        f["append"] = static (a, k) =>
        {
            var args = new NpArgs("append", a, k, "arr", "values", "axis");
            var axis = args.Optional("axis");
            var arr = args.Array("arr");
            var values = args.Array("values");
            return axis is null
                ? ArrayFunctions.Concatenate([arr.Reshape([-1]), values.Reshape([-1])], PyInt.From(0))
                : ArrayFunctions.Concatenate([arr, values], axis);
        };
        f["delete"] = static (a, k) =>
        {
            var args = new NpArgs("delete", a, k, "arr", "obj", "axis");
            var arr = args.Optional("axis") is null ? args.Array("arr").Reshape([-1]) : args.Array("arr");
            var axis = args.Optional("axis") is { } given ? NdArray.NormalizeAxis(NdConvert.ToInt(given), arr.NDim) : 0;
            var length = arr.Shape[axis];
            var obj = args.Required("obj");
            var remove = obj is PySlice slice
                ? NdArray.ResolveSlice(slice, length) is var (start, step, count) ? Enumerable.Range(0, count).Select(i => start + (i * step)).ToHashSet() : []
                : A(obj).ToLongs().Select(i => (int)(i < 0 ? i + length : i)).ToHashSet();
            var keep = NdArray.Vector([.. Enumerable.Range(0, length).Where(i => !remove.Contains(i)).Select(static i => (long)i)]);
            return ArrayFunctions.Take(arr, keep, axis);
        };
        f["split"] = static (a, k) => Split(new NpArgs("split", a, k, "ary", "indices_or_sections", "axis"), exact: true, axis: null);
        f["array_split"] = static (a, k) => Split(new NpArgs("array_split", a, k, "ary", "indices_or_sections", "axis"), exact: false, axis: null);
        f["hsplit"] = static (a, k) =>
        {
            var args = new NpArgs("hsplit", a, k, "ary", "indices_or_sections");
            return ArrayFunctions.Split(args.Array("ary"), args.Required("indices_or_sections"), PyInt.From(args.Array("ary").NDim > 1 ? 1 : 0), true);
        };
        f["vsplit"] = static (a, k) =>
        {
            var args = new NpArgs("vsplit", a, k, "ary", "indices_or_sections");
            return ArrayFunctions.Split(args.Array("ary"), args.Required("indices_or_sections"), PyInt.From(0), true);
        };
        f["flip"] = static (a, k) =>
        {
            var args = new NpArgs("flip", a, k, "m", "axis");
            return ArrayFunctions.Flip(args.Array("m"), args.Optional("axis"));
        };
        f["fliplr"] = static (a, k) => ArrayFunctions.Flip(new NpArgs("fliplr", a, k, "m").Array("m"), PyInt.From(1));
        f["flipud"] = static (a, k) => ArrayFunctions.Flip(new NpArgs("flipud", a, k, "m").Array("m"), PyInt.From(0));
        f["roll"] = static (a, k) =>
        {
            var args = new NpArgs("roll", a, k, "a", "shift", "axis");
            return ArrayFunctions.Roll(args.Array("a"), args.Required("shift"), args.Optional("axis"));
        };

        // ---- sorting and searching ----

        f["sort"] = static (a, k) =>
        {
            var args = new NpArgs("sort", a, k, "a", "axis", "kind", "order", "stable");
            return ArrayFunctions.Sort(args.Array("a"), args.Has("axis") ? args["axis"] : PyInt.From(-1));
        };
        f["argsort"] = static (a, k) =>
        {
            var args = new NpArgs("argsort", a, k, "a", "axis", "kind", "order", "stable");
            return ArrayFunctions.ArgSort(args.Array("a"), args.Has("axis") ? args["axis"] : PyInt.From(-1));
        };
        f["unique"] = static (a, k) =>
        {
            var args = new NpArgs("unique", a, k, "ar", "return_index", "return_inverse", "return_counts", "axis", "equal_nan");

            if (args.Optional("axis") is not null)
            {
                throw new PyRaise(new PyException(PyExceptionType.NotImplementedError, "unique() with axis= is not supported"));
            }

            return ArrayFunctions.Unique(args.Array("ar"), args.Bool("return_index", false), args.Bool("return_inverse", false), args.Bool("return_counts", false));
        };
        f["searchsorted"] = static (a, k) =>
        {
            var args = new NpArgs("searchsorted", a, k, "a", "v", "side", "sorter");
            var side = args.Optional("side")?.Display() ?? "left";

            if (side is not ("left" or "right"))
            {
                throw new PyRaise(PyErrors.ValueError($"side must be 'left' or 'right' (got '{side}')"));
            }

            return ArrayFunctions.SearchSorted(args.Array("a"), args.Required("v"), side == "right");
        };
        f["where"] = static (a, k) =>
        {
            var args = new NpArgs("where", a, k, "condition", "x", "y");

            if (args.Has("x") != args.Has("y"))
            {
                throw new PyRaise(PyErrors.ValueError("either both or neither of x and y should be given"));
            }

            return args.Has("x")
                ? Ufuncs.Where(args.Required("condition"), args.Required("x"), args.Required("y"))
                : new PyTuple([.. NdArray.NonZero(args.Array("condition"))]);
        };
        f["nonzero"] = static (a, k) => new PyTuple([.. NdArray.NonZero(new NpArgs("nonzero", a, k, "a").Array("a"))]);
        f["flatnonzero"] = static (a, k) => NdArray.NonZero(new NpArgs("flatnonzero", a, k, "a").Array("a").Reshape([-1]))[0];
        f["argwhere"] = static (a, k) =>
        {
            var array = new NpArgs("argwhere", a, k, "a").Array("a");
            var indices = NdArray.NonZero(array);
            return array.NDim == 0
                ? NdArray.Zeros([array.GetBool(array.Offset) ? 1 : 0, 0], NdDType.Int64)
                : ArrayFunctions.Stack([.. indices], PyInt.From(1));
        };
        f["count_nonzero"] = static (a, k) =>
        {
            var args = new NpArgs("count_nonzero", a, k, "a", "axis", "keepdims");
            var array = args.Array("a");
            return Reductions.CountNonZero(array, Axes(args, array), args.Bool("keepdims", false));
        };
        f["take"] = static (a, k) =>
        {
            var args = new NpArgs("take", a, k, "a", "indices", "axis", "out", "mode");
            var array = args.Array("a");
            var axis = args.Optional("axis") is { } given ? NdArray.NormalizeAxis(NdConvert.ToInt(given), array.NDim) : (int?)null;
            return W(ArrayFunctions.Take(array, args.Array("indices"), axis));
        };
        f["select"] = static (a, k) =>
        {
            var args = new NpArgs("select", a, k, "condlist", "choicelist", "default");
            var conditions = VirtualMachine.RequireIterable(args.Required("condlist")).ToList();
            var choices = VirtualMachine.RequireIterable(args.Required("choicelist")).ToList();

            if (conditions.Count != choices.Count)
            {
                throw new PyRaise(PyErrors.ValueError("list of cases must be same length as list of conditions"));
            }

            PyObject result = args.Optional("default") ?? PyInt.From(0);

            for (var i = conditions.Count - 1; i >= 0; i--)
            {
                result = Ufuncs.Where(conditions[i], choices[i], result);
            }

            return result is NdArray ? result : A(result);
        };
        f["diff"] = static (a, k) =>
        {
            var args = new NpArgs("diff", a, k, "a", "n", "axis", "prepend", "append");
            var array = args.Array("a");

            if (args.Optional("prepend") is { } before)
            {
                array = ArrayFunctions.Concatenate([A(before).BroadcastTo(EdgeShape(array, args.Int("axis", -1))), array], PyInt.From(args.Int("axis", -1)));
            }

            if (args.Optional("append") is { } after)
            {
                array = ArrayFunctions.Concatenate([array, A(after).BroadcastTo(EdgeShape(array, args.Int("axis", -1)))], PyInt.From(args.Int("axis", -1)));
            }

            if (array.NDim == 0)
            {
                throw new PyRaise(PyErrors.ValueError("diff requires input that is at least one dimensional"));
            }

            return ArrayFunctions.Diff(array, args.Int("n", 1), args.Int("axis", -1));
        };
        f["bincount"] = static (a, k) =>
        {
            var args = new NpArgs("bincount", a, k, "x", "weights", "minlength");
            return ArrayFunctions.Bincount(args.Array("x"), args.Optional("weights"), args.Int("minlength", 0));
        };
        f["histogram"] = static (a, k) =>
        {
            var args = new NpArgs("histogram", a, k, "a", "bins", "range", "density", "weights");
            return ArrayFunctions.Histogram(args.Array("a").Reshape([-1]), args.Optional("bins"), args.Optional("range"), args.Bool("density", false));
        };
        f["digitize"] = static (a, k) =>
        {
            var args = new NpArgs("digitize", a, k, "x", "bins", "right");
            var bins = args.Array("bins").ToDoubles();
            var right = args.Bool("right", false);
            var increasing = bins.Length < 2 || bins[^1] >= bins[0];
            var x = args.Array("x");
            var values = x.ToDoubles().Select(v =>
            {
                if (increasing)
                {
                    return (long)ArrayFunctions.Bisect(bins, v, !right);
                }

                var reversed = bins.Reverse().ToArray();
                return (long)(bins.Length - ArrayFunctions.Bisect(reversed, v, right));
            }).ToArray();
            return W(NdArray.FromLongs(values, (int[])x.Shape.Clone()));
        };
        f["isin"] = static (a, k) =>
        {
            var args = new NpArgs("isin", a, k, "element", "test_elements", "assume_unique", "invert", "kind");
            return IsIn(args.Array("element"), args.Array("test_elements"), args.Bool("invert", false));
        };
        f["in1d"] = static (a, k) =>
        {
            var args = new NpArgs("in1d", a, k, "ar1", "ar2", "assume_unique", "invert", "kind");
            return IsIn(args.Array("ar1").Reshape([-1]), args.Array("ar2"), args.Bool("invert", false));
        };
        f["union1d"] = static (a, k) =>
        {
            var args = new NpArgs("union1d", a, k, "ar1", "ar2");
            return ArrayFunctions.Unique(ArrayFunctions.Concatenate([args.Array("ar1").Reshape([-1]), args.Array("ar2").Reshape([-1])], PyInt.From(0)), false, false, false);
        };
        f["intersect1d"] = static (a, k) =>
        {
            var args = new NpArgs("intersect1d", a, k, "ar1", "ar2", "assume_unique", "return_indices");
            var left = (NdArray)ArrayFunctions.Unique(args.Array("ar1"), false, false, false);
            var right = args.Array("ar2");
            return NdConvert.ToArray(left.GetItem(IsIn(left, right, false)));
        };
        f["setdiff1d"] = static (a, k) =>
        {
            var args = new NpArgs("setdiff1d", a, k, "ar1", "ar2", "assume_unique");
            var left = (NdArray)ArrayFunctions.Unique(args.Array("ar1"), false, false, false);
            return NdConvert.ToArray(left.GetItem(IsIn(left, args.Array("ar2"), true)));
        };

        // ---- comparison ----

        f["allclose"] = static (a, k) =>
        {
            var args = new NpArgs("allclose", a, k, "a", "b", "rtol", "atol", "equal_nan");
            return PyBool.Of(ArrayFunctions.IsClose(args.Array("a"), args.Array("b"), args.Double("rtol", 1e-5), args.Double("atol", 1e-8), args.Bool("equal_nan", false)).ToBools().All(static b => b));
        };
        f["isclose"] = static (a, k) =>
        {
            var args = new NpArgs("isclose", a, k, "a", "b", "rtol", "atol", "equal_nan");
            return W(ArrayFunctions.IsClose(args.Array("a"), args.Array("b"), args.Double("rtol", 1e-5), args.Double("atol", 1e-8), args.Bool("equal_nan", false)));
        };
        f["array_equal"] = static (a, k) =>
        {
            var args = new NpArgs("array_equal", a, k, "a1", "a2", "equal_nan");
            return PyBool.Of(ArrayFunctions.ArrayEqual(args.Required("a1"), args.Required("a2"), args.Bool("equal_nan", false)));
        };
        f["array_equiv"] = static (a, k) =>
        {
            var args = new NpArgs("array_equiv", a, k, "a1", "a2");

            try
            {
                return PyBool.Of(Ufuncs.BinaryArray("equal", args.Array("a1"), args.Array("a2")).ToBools().All(static b => b));
            }
            catch (PyRaise raise) when (raise.Exception.IsInstanceOf(PyExceptionType.ValueError))
            {
                return PyBool.False;
            }
        };

        // ---- products ----

        f["dot"] = static (a, k) =>
        {
            var args = new NpArgs("dot", a, k, "a", "b", "out");
            return LinAlg.Dot(args.Array("a"), args.Array("b"));
        };
        f["vdot"] = static (a, k) =>
        {
            var args = new NpArgs("vdot", a, k, "a", "b");
            return LinAlg.Dot(args.Array("a").Reshape([-1]), args.Array("b").Reshape([-1]));
        };
        f["matmul"] = static (a, k) =>
        {
            var args = new NpArgs("matmul", a, k, "x1", "x2", "out");
            return LinAlg.MatMul(args.Array("x1"), args.Array("x2"));
        };
        f["inner"] = static (a, k) =>
        {
            var args = new NpArgs("inner", a, k, "a", "b");
            return LinAlg.Inner(args.Array("a"), args.Array("b"));
        };
        f["outer"] = static (a, k) =>
        {
            var args = new NpArgs("outer", a, k, "a", "b", "out");
            return ArrayFunctions.Outer(args.Array("a"), args.Array("b"));
        };
        f["cross"] = static (a, k) =>
        {
            var args = new NpArgs("cross", a, k, "a", "b", "axisa", "axisb", "axisc", "axis");
            return LinAlg.Cross(args.Array("a"), args.Array("b"));
        };
        f["convolve"] = static (a, k) =>
        {
            var args = new NpArgs("convolve", a, k, "a", "v", "mode");
            return Convolve(args.Array("a"), args.Array("v"), args.Optional("mode")?.Display() ?? "full");
        };
        f["polyfit"] = static (a, k) =>
        {
            var args = new NpArgs("polyfit", a, k, "x", "y", "deg", "rcond", "full", "w", "cov");
            var x = args.Array("x").ToDoubles();
            var y = args.Array("y").ToDoubles();
            var degree = NdConvert.ToInt(args.Required("deg"));

            if (degree < 0)
            {
                throw new PyRaise(PyErrors.ValueError("expected deg >= 0"));
            }

            if (x.Length != y.Length)
            {
                throw new PyRaise(PyErrors.TypeError("expected x and y to have same length"));
            }

            var vandermonde = new double[x.Length, degree + 1];
            var rhs = new double[x.Length, 1];

            for (var i = 0; i < x.Length; i++)
            {
                for (var j = 0; j <= degree; j++)
                {
                    vandermonde[i, j] = Math.Pow(x[i], degree - j);
                }

                rhs[i, 0] = y[i];
            }

            var solution = LinAlg.LeastSquares(vandermonde, rhs);
            return NdArray.Vector([.. Enumerable.Range(0, degree + 1).Select(j => solution[j, 0])]);
        };
        f["polyval"] = static (a, k) =>
        {
            var args = new NpArgs("polyval", a, k, "p", "x");
            var coefficients = args.Array("p").ToBoxed();
            PyObject result = PyInt.From(0);
            var x = args.Array("x");

            foreach (var c in coefficients)
            {
                result = Ufuncs.Binary("add", Ufuncs.Binary("multiply", result, x), c);
            }

            return result is NdArray ? result : x.NDim == 0 ? result : W(A(result));
        };

        // ---- statistics ----

        f["corrcoef"] = static (a, k) =>
        {
            var args = new NpArgs("corrcoef", a, k, "x", "y", "rowvar", "bias", "ddof", "dtype");
            return ArrayFunctions.CorrCoef(args.Array("x"), args.Optional("y") is { } y ? A(y) : null, args.Bool("rowvar", true));
        };
        f["cov"] = static (a, k) =>
        {
            var args = new NpArgs("cov", a, k, "m", "y", "rowvar", "bias", "ddof", "fweights", "aweights", "dtype");
            var ddof = args.Optional("ddof") is { } given ? NdConvert.ToInt(given) : args.Bool("bias", false) ? 0 : 1;
            return ArrayFunctions.Cov(args.Array("m"), args.Optional("y") is { } y ? A(y) : null, args.Bool("rowvar", true), ddof);
        };
        f["interp"] = static (a, k) =>
        {
            var args = new NpArgs("interp", a, k, "x", "xp", "fp", "left", "right", "period");
            return ArrayFunctions.Interp(
                args.Required("x"),
                args.Array("xp"),
                args.Array("fp"),
                args.Optional("left") is { } left ? NdArray.ToDouble(left) : null,
                args.Optional("right") is { } right ? NdArray.ToDouble(right) : null);
        };
        f["nan_to_num"] = static (a, k) =>
        {
            var args = new NpArgs("nan_to_num", a, k, "x", "copy", "nan", "posinf", "neginf");
            var x = args.Array("x");

            if (x.Kind != DKind.Float)
            {
                return W(x.Copy());
            }

            var nan = args.Double("nan", 0);
            var positive = args.Double("posinf", double.MaxValue);
            var negative = args.Double("neginf", double.MinValue);
            return W(NdArray.FromDoubles(
                [.. x.ToDoubles().Select(v => double.IsNaN(v) ? nan : double.IsPositiveInfinity(v) ? positive : double.IsNegativeInfinity(v) ? negative : v)],
                (int[])x.Shape.Clone()));
        };

        // ---- element-wise ----

        foreach (var name in Ufuncs.UnaryNames)
        {
            f[name] = (a, k) => Ufuncs.Unary(name, new NpArgs(name, a, k, "x", "out", "where", "dtype").Required("x"));
        }

        foreach (var name in Ufuncs.BinaryNames)
        {
            f[name] = (a, k) =>
            {
                var args = new NpArgs(name, a, k, "x1", "x2", "out", "where", "dtype");
                return Ufuncs.Binary(name, args.Required("x1"), args.Required("x2"));
            };
        }

        f["round"] = static (a, k) =>
        {
            var args = new NpArgs("round", a, k, "a", "decimals", "out");
            return W(Ufuncs.Round(args.Array("a"), args.Int("decimals", 0)));
        };
        f["around"] = f["round"];
        f["clip"] = static (a, k) =>
        {
            var args = new NpArgs("clip", a, k, "a", "a_min", "a_max", "out", "min", "max");
            return Ufuncs.Clip(args.Required("a"), args.Optional("a_min") ?? args.Optional("min"), args.Optional("a_max") ?? args.Optional("max"));
        };

        // ---- reductions ----

        f["sum"] = static (a, k) =>
        {
            var args = new NpArgs("sum", a, k, "a", "axis", "dtype", "out", "keepdims", "initial", "where");
            var array = args.Array("a");
            var result = Reductions.Sum(array, Axes(args, array), args.Bool("keepdims", false), args.DType());
            return args.Optional("initial") is { } initial ? Ufuncs.Binary("add", result, initial) : result;
        };
        f["prod"] = static (a, k) =>
        {
            var args = new NpArgs("prod", a, k, "a", "axis", "dtype", "out", "keepdims", "initial", "where");
            var array = args.Array("a");
            return Reductions.Prod(array, Axes(args, array), args.Bool("keepdims", false));
        };
        f["product"] = f["prod"];
        f["nansum"] = static (a, k) =>
        {
            var args = new NpArgs("nansum", a, k, "a", "axis", "dtype", "out", "keepdims", "initial", "where");
            var array = WithoutNaN(args.Array("a"), 0);
            return Reductions.Sum(array, Axes(args, array), args.Bool("keepdims", false));
        };
        f["nanprod"] = static (a, k) =>
        {
            var args = new NpArgs("nanprod", a, k, "a", "axis", "dtype", "out", "keepdims", "initial", "where");
            var array = WithoutNaN(args.Array("a"), 1);
            return Reductions.Prod(array, Axes(args, array), args.Bool("keepdims", false));
        };
        f["mean"] = static (a, k) => FloatReduction("mean", a, k, static (v, p, _) => Reductions.Mean(v, p));
        f["average"] = static (a, k) => Average(new NpArgs("average", a, k, "a", "axis", "weights", "returned", "keepdims"));
        f["std"] = static (a, k) => FloatReduction("std", a, k, static (v, p, ddof) => Math.Sqrt(Reductions.Variance(v, p, ddof)));
        f["var"] = static (a, k) => FloatReduction("var", a, k, static (v, p, ddof) => Reductions.Variance(v, p, ddof));
        f["median"] = static (a, k) => FloatReduction("median", a, k, static (v, _, _) => Reductions.Median(v));
        f["nanmean"] = static (a, k) => FloatReduction("nanmean", a, k, static (v, p, _) => Reductions.Mean(NonNaN(v), p));
        f["nanstd"] = static (a, k) => FloatReduction("nanstd", a, k, static (v, p, ddof) => Math.Sqrt(Reductions.Variance(NonNaN(v), p, ddof)));
        f["nanvar"] = static (a, k) => FloatReduction("nanvar", a, k, static (v, p, ddof) => Reductions.Variance(NonNaN(v), p, ddof));
        f["nanmedian"] = static (a, k) => FloatReduction("nanmedian", a, k, static (v, _, _) => Reductions.Median(NonNaN(v)));

        foreach (var (name, smallest, ignoreNaN) in new[]
        {
            ("min", true, false), ("amin", true, false), ("max", false, false), ("amax", false, false),
            ("nanmin", true, true), ("nanmax", false, true),
        })
        {
            f[name] = (a, k) =>
            {
                var args = new NpArgs(name, a, k, "a", "axis", "out", "keepdims", "initial", "where");
                var array = args.Array("a");
                return Reductions.Extreme(array, Axes(args, array), args.Bool("keepdims", false), smallest, ignoreNaN);
            };
        }

        foreach (var (name, smallest) in new[] { ("argmin", true), ("argmax", false), ("nanargmin", true), ("nanargmax", false) })
        {
            f[name] = (a, k) =>
            {
                var args = new NpArgs(name, a, k, "a", "axis", "out", "keepdims");
                var array = args.Array("a");

                if (name.StartsWith("nan", StringComparison.Ordinal))
                {
                    array = WithoutNaN(array, smallest ? double.PositiveInfinity : double.NegativeInfinity);
                }

                return Reductions.ArgExtreme(array, args.Optional("axis"), smallest, args.Bool("keepdims", false));
            };
        }

        f["ptp"] = static (a, k) =>
        {
            var args = new NpArgs("ptp", a, k, "a", "axis", "out", "keepdims");
            var array = args.Array("a");
            var axes = Axes(args, array);
            var keep = args.Bool("keepdims", false);
            return Ufuncs.Binary("subtract", Reductions.Extreme(array, axes, keep, false), Reductions.Extreme(array, axes, keep, true));
        };

        foreach (var (name, all) in new[] { ("any", false), ("all", true) })
        {
            f[name] = (a, k) =>
            {
                var args = new NpArgs(name, a, k, "a", "axis", "out", "keepdims", "where");
                var array = args.Array("a");
                return Reductions.Logical(array, Axes(args, array), args.Bool("keepdims", false), all);
            };
        }

        foreach (var (name, product) in new[] { ("cumsum", false), ("cumprod", true), ("nancumsum", false), ("nancumprod", true) })
        {
            f[name] = (a, k) =>
            {
                var args = new NpArgs(name, a, k, "a", "axis", "dtype", "out");
                var array = args.Array("a");

                if (name.StartsWith("nan", StringComparison.Ordinal))
                {
                    array = WithoutNaN(array, product ? 1 : 0);
                }

                var result = Reductions.Cumulative(array, args.Optional("axis"), product);
                return args.DType() is { } dtype ? result.AsType(dtype) : result;
            };
        }

        foreach (var (name, scale) in new[] { ("percentile", 100.0), ("quantile", 1.0), ("nanpercentile", 100.0), ("nanquantile", 1.0) })
        {
            f[name] = (a, k) => Quantiles(new NpArgs(name, a, k, "a", "q", "axis", "out", "overwrite_input", "method", "keepdims", "weights", "interpolation"), scale, name.StartsWith("nan", StringComparison.Ordinal));
        }

        f["errstate"] = static (_, k) => new NpErrState(k);
        f["seterr"] = static (_, _) => NpErrState.Defaults();
        f["geterr"] = static (_, _) => NpErrState.Defaults();

        return f;
    }

    private static PyDict Keywords(params (string Name, PyObject? Value)[] entries)
    {
        var dict = new PyDict();

        foreach (var (name, value) in entries)
        {
            if (value is not null)
            {
                dict.Set(new PyStr(name), value);
            }
        }

        return dict;
    }

    private static NdArray Filled(NdArray array, PyObject value)
    {
        if (array.Size > 0)
        {
            array.Assign(array.Offsets(), array.Shape, value);
        }

        return array;
    }

    private static PyObject Arange(NpArgs args)
    {
        var start = args.Required("start");
        var stop = args.Optional("stop");
        var step = args.Optional("step") ?? PyInt.From(1);

        if (stop is null)
        {
            (start, stop) = (PyInt.From(0), start);
        }

        var dtype = args.DType();
        var integral = start is PyInt && stop is PyInt && step is PyInt;
        var kind = dtype?.Kind ?? (integral ? DKind.Int : DKind.Float);

        if (kind == DKind.Int && integral)
        {
            var (a, b, s) = (NdArray.ToLong(start), NdArray.ToLong(stop), NdArray.ToLong(step));

            if (s == 0)
            {
                throw new PyRaise(new PyException(PyExceptionType.ZeroDivisionError, "division by zero"));
            }

            var length = Math.Max(0, (long)Math.Ceiling((double)(b - a) / s));
            NdArray.CheckedSize([(int)Math.Min(length, int.MaxValue)]);
            var longs = GC.AllocateUninitializedArray<long>((int)length);

            for (var i = 0; i < longs.Length; i++)
            {
                longs[i] = a + (i * s);
            }

            return NdArray.FromLongs(longs, [longs.Length]);
        }

        var (x, y, z) = (NdArray.ToDouble(start), NdArray.ToDouble(stop), NdArray.ToDouble(step));

        if (z == 0)
        {
            throw new PyRaise(new PyException(PyExceptionType.ZeroDivisionError, "division by zero"));
        }

        var count = Math.Ceiling((y - x) / z);

        if (double.IsNaN(count))
        {
            throw new PyRaise(PyErrors.ValueError("arange: cannot compute length"));
        }

        var n = (int)Math.Max(0, Math.Min(count, int.MaxValue));
        NdArray.CheckedSize([n]);
        var values = new double[n];

        for (var i = 0; i < n; i++)
        {
            values[i] = x + (i * z);
        }

        var result = NdArray.Vector(values);
        return dtype is null || dtype.Kind == DKind.Float ? result : result.AsType(dtype);
    }

    private static NdArray Triangle(NpArgs args, bool lower)
    {
        var m = args.Array("m");
        var k = args.Int("k", 0);
        var result = m.Copy();

        if (result.NDim < 2)
        {
            throw new PyRaise(PyErrors.ValueError("tril/triu requires an array of at least two dimensions"));
        }

        var (rows, columns) = (result.Shape[^2], result.Shape[^1]);
        var offsets = result.Offsets();
        var zero = NdArray.Zeros([1], result.DType);

        for (var index = 0; index < offsets.Length; index++)
        {
            var j = index % columns;
            var i = index / columns % rows;
            var outside = lower ? j - i > k : j - i < k;

            if (outside)
            {
                result.SetFrom(offsets[index], zero, 0);
            }
        }

        return result;
    }

    private static PyObject Split(NpArgs args, bool exact, PyObject? axis) =>
        ArrayFunctions.Split(args.Array("ary"), args.Required("indices_or_sections"), axis ?? args.Optional("axis"), exact);

    private static int[] EdgeShape(NdArray array, int axis)
    {
        var shape = (int[])array.Shape.Clone();
        shape[NdArray.NormalizeAxis(axis, array.NDim)] = 1;
        return shape;
    }

    private static PyObject IsIn(NdArray element, NdArray test, bool invert)
    {
        var candidates = test.ToBoxed();

        if (test.Kind is not (DKind.Int or DKind.Float or DKind.Bool))
        {
            NdArray.CheckWork((double)element.Size * test.Size, "isin");
        }

        var floats = test.Kind is DKind.Int or DKind.Float or DKind.Bool ? test.ToDoubles().ToHashSet() : null;
        var values = element.ToBoxed();
        var result = new bool[values.Length];

        for (var i = 0; i < values.Length; i++)
        {
            var found = floats is not null && element.Kind is DKind.Int or DKind.Float or DKind.Bool
                ? floats.Contains(NdArray.ToDouble(values[i]))
                : candidates.Any(c => PyObject.SameOrEqual(c, values[i]));
            result[i] = found != invert;
        }

        return W(NdArray.FromBools(result, (int[])element.Shape.Clone()));
    }

    private static NdArray WithoutNaN(NdArray array, double replacement) =>
        array.Kind != DKind.Float
            ? array
            : NdArray.FromDoubles([.. array.ToDoubles().Select(v => double.IsNaN(v) ? replacement : v)], (int[])array.Shape.Clone());

    private static double[] NonNaN(double[] values) => [.. values.Where(static v => !double.IsNaN(v))];

    private static PyObject FloatReduction(string name, PyObject[] arguments, PyDict? keywords, Func<double[], bool, int, double> reduce)
    {
        var args = new NpArgs(name, arguments, keywords, "a", "axis", "dtype", "out", "ddof", "keepdims", "where", "mean", "correction", "overwrite_input");
        var array = args.Array("a");
        var ddof = args.Int("ddof", args.Int("correction", 0));

        if (name is "mean" or "std" or "var"
            && args.Optional("dtype") is null or PyNone && args.Optional("out") is null or PyNone && args.Optional("where") is null or PyNone
            && Reductions.TryMomentFast(name, array, Axes(args, array), args.Bool("keepdims", false), ddof) is { } fast)
        {
            return fast;
        }

        return Reductions.MapFloat(array, Axes(args, array), args.Bool("keepdims", false), (values, pairwise) => reduce(values, pairwise, ddof));
    }

    private static PyObject Average(NpArgs args)
    {
        var array = args.Array("a");
        var axes = Axes(args, array);
        var keep = args.Bool("keepdims", false);

        if (args.Optional("weights") is not { } given)
        {
            var mean = Reductions.MapFloat(array, axes, keep, static (v, p) => Reductions.Mean(v, p));
            return args.Bool("returned", false)
                ? Tuple(mean, new PyFloat(axes is null ? array.Size : axes.Aggregate(1, (s, d) => s * array.Shape[d])))
                : mean;
        }

        var weights = A(given).AsType(NdDType.Float64);

        if (!weights.Shape.SequenceEqual(array.Shape))
        {
            if (axes is null)
            {
                throw new PyRaise(PyErrors.TypeError("Axis must be specified when shapes of a and weights differ."));
            }

            if (weights.NDim != 1 || axes.Length != 1 || weights.Shape[0] != array.Shape[axes[0]])
            {
                throw new PyRaise(PyErrors.ValueError("Length of weights not compatible with specified axis."));
            }

            var shape = Enumerable.Repeat(1, array.NDim).ToArray();
            shape[axes[0]] = weights.Shape[0];
            weights = weights.Reshape(shape).BroadcastTo(array.Shape).Copy();
        }

        var total = Reductions.Sum(weights, axes, keep);

        if (NdConvert.ToArray(total).ToDoubles().Any(static t => t == 0))
        {
            throw new PyRaise(PyErrors.ZeroDivisionError("Weights sum to zero, can't be normalized"));
        }

        var weighted = Reductions.Sum(Ufuncs.BinaryArray("multiply", array.AsType(NdDType.Float64), weights), axes, keep);
        var average = Ufuncs.Binary("true_divide", weighted, total);
        return args.Bool("returned", false) ? Tuple(average, total) : average;
    }

    private static PyObject Quantiles(NpArgs args, double scale, bool ignoreNaN)
    {
        var array = args.Array("a");
        var q = args.Array("q").AsType(NdDType.Float64);
        var method = args.Optional("method")?.Display() ?? args.Optional("interpolation")?.Display() ?? "linear";
        var fractions = q.ToDoubles().Select(v => v / scale).ToArray();

        if (fractions.Any(static f => f is < 0 or > 1 || double.IsNaN(f)))
        {
            throw new PyRaise(PyErrors.ValueError(scale == 100 ? "Percentiles must be in the range [0, 100]" : "Quantiles must be in the range [0, 1]"));
        }

        var axes = Axes(args, array);
        var keep = args.Bool("keepdims", false);
        var results = new List<NdArray>();
        NdArray.CheckWork((double)fractions.Length * array.Size * Math.Max(1, Math.Log2(Math.Max(array.Size, 2))), "percentile");

        foreach (var fraction in fractions)
        {
            var reduced = Reductions.MapFloat(array, axes, keep, (values, _) =>
            {
                var sorted = (ignoreNaN ? NonNaN(values) : values).OrderBy(static v => v, Comparer<double>.Create(ArrayFunctions.CompareDoubles)).ToArray();

                if (sorted.Length == 0)
                {
                    return double.NaN;
                }

                var position = fraction * (sorted.Length - 1);

                return method switch
                {
                    "linear" => Reductions.Quantile(sorted, fraction),
                    "lower" => sorted[(int)Math.Floor(position)],
                    "higher" => sorted[(int)Math.Ceiling(position)],
                    "nearest" => sorted[(int)Math.Round(position, MidpointRounding.ToEven)],
                    "midpoint" => (sorted[(int)Math.Floor(position)] + sorted[(int)Math.Ceiling(position)]) / 2,
                    _ => throw new PyRaise(PyErrors.ValueError($"'{method}' is not a valid method.")),
                };
            });

            results.Add(A(reduced));
        }

        if (q.NDim == 0)
        {
            return W(results[0]);
        }

        return ArrayFunctions.Stack(results, PyInt.From(0)).Reshape([.. q.Shape, .. results[0].Shape]);
    }

    private static NdArray Convolve(NdArray a, NdArray v, string mode)
    {
        var x = a.Reshape([-1]);
        var y = v.Reshape([-1]);

        if (x.Size == 0 || y.Size == 0)
        {
            throw new PyRaise(PyErrors.ValueError(x.Size == 0 ? "a cannot be empty" : "v cannot be empty"));
        }

        var integral = x.Kind is DKind.Int or DKind.Bool && y.Kind is DKind.Int or DKind.Bool;
        NdArray.CheckWork((double)x.Size * y.Size, "convolve");
        var xs = x.ToDoubles();
        var ys = y.ToDoubles();
        var full = new double[xs.Length + ys.Length - 1];

        for (var i = 0; i < xs.Length; i++)
        {
            for (var j = 0; j < ys.Length; j++)
            {
                full[i + j] += xs[i] * ys[j];
            }
        }

        var (longer, shorter) = (Math.Max(xs.Length, ys.Length), Math.Min(xs.Length, ys.Length));
        var (start, length) = mode switch
        {
            "full" => (0, full.Length),
            "same" => ((shorter - 1) / 2, longer),
            "valid" => (shorter - 1, longer - shorter + 1),
            _ => throw new PyRaise(PyErrors.ValueError($"mode must be one of 'full', 'same', or 'valid' (got '{mode}')")),
        };

        var values = full.AsSpan(start, length).ToArray();
        return integral ? NdArray.Vector([.. values.Select(static d => (long)Math.Round(d))]) : NdArray.Vector(values);
    }

    private static PyObject Vectorized(VirtualMachine machine, PyObject function, PyObject[] arguments, PyDict? keywords)
    {
        var arrays = arguments.Select(static v => A(v)).ToArray();
        var shape = NdArray.BroadcastShapes([.. arrays.Select(static a => a.Shape)]);
        var count = NdArray.CheckedSize(shape);
        var columns = arrays.Select(a => a.BroadcastTo(shape).ToBoxed()).ToArray();
        var results = new PyObject[count];

        for (var i = 0; i < count; i++)
        {
            results[i] = machine.Call(function, [.. columns.Select(c => c[i])], keywords);
        }

        return W(NdConvert.FromLeaves(results, shape));
    }

    private static PyObject ApplyAlongAxis(VirtualMachine machine, PyObject[] arguments, PyDict? keywords)
    {
        if (arguments.Length < 3)
        {
            throw new PyRaise(PyErrors.TypeError("apply_along_axis() missing required arguments: 'func1d', 'axis' and 'arr'"));
        }

        var function = arguments[0];
        var array = A(arguments[2]);
        var axis = NdArray.NormalizeAxis(NdConvert.ToInt(arguments[1]), array.NDim);
        var extra = arguments[3..];
        var permutation = Enumerable.Range(0, array.NDim).Where(d => d != axis).Append(axis).ToArray();
        var moved = array.Transpose(permutation).Contiguous();
        var length = array.Shape[axis];
        var outer = moved.Shape[..^1];
        var rows = outer.Aggregate(1, static (a, b) => a * b);
        var outputs = new List<NdArray>();

        for (var r = 0; r < rows; r++)
        {
            var row = moved.View([length], [1], moved.Offset + (r * length));
            outputs.Add(A(machine.Call(function, [row.Copy(), .. extra], keywords)));
        }

        if (outputs.Count == 0)
        {
            throw new PyRaise(PyErrors.ValueError("Cannot apply_along_axis when any iteration dimensions are 0"));
        }

        var stacked = ArrayFunctions.Stack(outputs, PyInt.From(0));
        var resultShape = outer.Concat(outputs[0].Shape).ToArray();
        var result = stacked.Reshape(resultShape);

        // The function's own dimensions take the place of the axis it consumed.
        var produced = outputs[0].NDim;
        var order = new List<int>();

        for (var d = 0; d < axis; d++)
        {
            order.Add(d);
        }

        for (var d = 0; d < produced; d++)
        {
            order.Add(outer.Length + d);
        }

        for (var d = axis; d < outer.Length; d++)
        {
            order.Add(d);
        }

        return W(result.Transpose([.. order]).Contiguous());
    }
}

/// <summary>A plain attribute bag, for <c>finfo</c> and <c>iinfo</c>.</summary>
internal sealed class NpNamespace(string repr) : PyObject
{
    private readonly Dictionary<string, PyObject> _members = new(StringComparer.Ordinal);

    public override string TypeName => "numpy.namespace";

    public override string Repr() => repr;

    public override PyObject? GetAttribute(string name) => _members.GetValueOrDefault(name);

    public NpNamespace Add(string name, PyObject value)
    {
        _members[name] = value;
        return this;
    }
}

/// <summary>
/// <c>np.errstate(...)</c>: a context manager that changes nothing, because nothing here
/// warns — floating-point errors already produce <c>inf</c> and <c>nan</c> silently.
/// </summary>
internal sealed class NpErrState(PyDict? settings) : PyObject
{
    public override string TypeName => "numpy.errstate";

    public override string Repr() => "<numpy.errstate object>";

    public static PyDict Defaults()
    {
        var dict = new PyDict();

        foreach (var key in new[] { "divide", "over", "under", "invalid" })
        {
            dict.Set(new PyStr(key), new PyStr(key == "under" ? "ignore" : "warn"));
        }

        return dict;
    }

    public override PyObject? GetAttribute(string name) => name switch
    {
        "__enter__" => new PyBoundMethod(name, this, (self, _) => self),
        "__exit__" => new PyBoundMethod(name, this, static (_, _) => PyBool.False),
        _ => settings?.TryGetValue(new PyStr(name), out var value) == true ? value : null,
    };
}
