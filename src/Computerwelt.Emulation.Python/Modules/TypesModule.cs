using System.Text;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules;

/// <summary>
/// The <c>types</c> module: <c>SimpleNamespace</c>, <c>MappingProxyType</c> and the type
/// objects of the interpreter's own values.
/// </summary>
public static class TypesModule
{
    /// <summary><c>types.SimpleNamespace</c>.</summary>
    public static PyType SimpleNamespace { get; } = new(
        "SimpleNamespace",
        static value => value is PySimpleNamespace,
        static (arguments, keywords) =>
        {
            if (arguments.Length > 1)
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"SimpleNamespace expected at most 1 positional argument, got {arguments.Length}"));
            }

            var space = new PySimpleNamespace();

            if (arguments.Length == 1)
            {
                var pairs = arguments[0] is PyDict mapping
                    ? mapping.Entries.Select(static e => (e.Key, e.Value))
                    : VirtualMachine.RequireIterable(arguments[0]).Select(static pair =>
                    {
                        var items = VirtualMachine.RequireIterable(pair).ToList();
                        return items.Count == 2
                            ? (items[0], items[1])
                            : throw new PyRaise(PyErrors.ValueError("dictionary update sequence element has the wrong length"));
                    });

                foreach (var (key, value) in pairs)
                {
                    space.Set(key is PyStr name ? name.Value : throw new PyRaise(PyErrors.TypeError(
                        $"keywords must be strings, not '{key.TypeName}'")), value);
                }
            }

            foreach (var (key, value) in keywords?.Entries ?? [])
            {
                space.Set(key.Display(), value);
            }

            return space;
        });

    /// <summary>Builds the module.</summary>
    /// <param name="machine">The machine a proxy binds its mapping's methods through.</param>
    public static PyModuleObject Create(VirtualMachine machine)
    {
        var module = new PyModuleObject("types");

        PyType? proxyType = null;
        proxyType = new PyType(
            "mappingproxy",
            static value => value is PyMappingProxy,
            (arguments, _) =>
            {
                Arity.ExactCount("mappingproxy", arguments, 1);

                return arguments[0] is PyDict or PyMappingProxy or PyChainMap
                    ? new PyMappingProxy(arguments[0], machine, proxyType!)
                    : throw new PyRaise(PyErrors.TypeError(
                        $"mappingproxy() argument must be a mapping, not {arguments[0].TypeName}"));
            });

        module.Add("SimpleNamespace", SimpleNamespace);
        module.Add("MappingProxyType", proxyType);
        module.Add("FunctionType", TypeRegistry.Function);
        module.Add("LambdaType", TypeRegistry.Function);
        module.Add("GeneratorType", TypeRegistry.Generator);
        module.Add("NoneType", TypeRegistry.NoneType);
        module.Add("ModuleType", new PyType("module", static value => value is PyModuleObject or PyPackageObject,
            static (arguments, _) => new PyModuleObject(arguments.Length > 0 ? arguments[0].Display() : "module")));
        module.Add("BuiltinFunctionType", new PyType("builtin_function_or_method",
            static value => value is PyBuiltinFunction or PyBoundMethod,
            static (_, _) => throw new PyRaise(PyErrors.TypeError("cannot create 'builtin_function_or_method' instances"))));
        module.Add("BuiltinMethodType", new PyType("builtin_function_or_method",
            static value => value is PyBuiltinFunction or PyBoundMethod,
            static (_, _) => throw new PyRaise(PyErrors.TypeError("cannot create 'builtin_function_or_method' instances"))));
        module.Add("EllipsisType", new PyType("ellipsis", static value => value is PyEllipsis,
            static (_, _) => PyEllipsis.Instance));

        return module;
    }
}

/// <summary>A <c>types.SimpleNamespace</c>: an object whose attributes are whatever was set on it.</summary>
public sealed class PySimpleNamespace : PyObject, IPyHostTyped
{
    private readonly Dictionary<string, PyObject> _attributes = new(StringComparer.Ordinal);

    /// <inheritdoc />
    PyObject IPyHostTyped.Type => TypesModule.SimpleNamespace;

    /// <inheritdoc />
    public override string TypeName => "types.SimpleNamespace";

    internal void Set(string name, PyObject value) => _attributes[name] = value;

    /// <inheritdoc />
    public override string Repr()
    {
        if (!PyReprGuard.TryEnter(this))
        {
            return "namespace(...)";
        }

        try
        {
            var builder = new StringBuilder("namespace(");
            builder.AppendJoin(", ", _attributes.Select(static pair => $"{pair.Key}={pair.Value.Repr()}"));
            return builder.Append(')').ToString();
        }
        finally
        {
            PyReprGuard.Exit(this);
        }
    }

    /// <inheritdoc />
    public override bool PyEquals(PyObject other) =>
        other is PySimpleNamespace space
        && space._attributes.Count == _attributes.Count
        && _attributes.All(pair => space._attributes.TryGetValue(pair.Key, out var value) && SameOrEqual(pair.Value, value));

    /// <inheritdoc />
    public override PyObject? GetAttribute(string name)
    {
        if (_attributes.TryGetValue(name, out var value))
        {
            return value;
        }

        return name switch
        {
            "__dict__" => new PyDict().Also(dict =>
            {
                foreach (var (key, item) in _attributes)
                {
                    dict.Set(new PyStr(key), item);
                }
            }),
            "__class__" => TypesModule.SimpleNamespace,
            _ => null,
        };
    }

    /// <inheritdoc />
    public override bool SetAttribute(string name, PyObject value)
    {
        _attributes[name] = value;
        return true;
    }

    /// <inheritdoc />
    public override bool DeleteAttribute(string name) =>
        _attributes.Remove(name) ? true : throw new PyRaise(PyErrors.AttributeError(TypeName, name));
}

/// <summary>A <c>types.MappingProxyType</c>: a read-only view of a mapping.</summary>
public sealed class PyMappingProxy(PyObject mapping, VirtualMachine machine, PyType type) : PyObject, IPyHostTyped, IPyMapping
{
    /// <inheritdoc />
    public PyDict Snapshot() => mapping switch
    {
        PyDict dict => dict.Copy(),
        IPyMapping inner => inner.Snapshot(),
        _ => new PyDict(),
    };

    /// <inheritdoc />
    PyObject IPyHostTyped.Type => type;

    /// <inheritdoc />
    public override string TypeName => "mappingproxy";

    /// <inheritdoc />
    public override string Repr() => $"mappingproxy({mapping.Repr()})";

    /// <inheritdoc />
    public override string Display() => mapping.Display();

    /// <inheritdoc />
    public override bool IsTruthy() => mapping.IsTruthy();

    /// <inheritdoc />
    public override int? Length() => mapping.Length();

    /// <inheritdoc />
    public override IEnumerable<PyObject>? Iterate() => mapping.Iterate();

    /// <inheritdoc />
    public override bool Contains(PyObject item) => mapping.Contains(item);

    /// <inheritdoc />
    public override PyObject GetItem(PyObject index) => mapping.GetItem(index);

    /// <inheritdoc />
    public override void SetItem(PyObject index, PyObject value) =>
        throw new PyRaise(PyErrors.TypeError("'mappingproxy' object does not support item assignment"));

    /// <inheritdoc />
    public override void DeleteItem(PyObject index) =>
        throw new PyRaise(PyErrors.TypeError("'mappingproxy' object does not support item deletion"));

    /// <inheritdoc />
    public override bool PyEquals(PyObject other) =>
        other is PyMappingProxy proxy ? SameOrEqual(mapping, proxy.Inner) : SameOrEqual(mapping, other);

    internal PyObject Inner => mapping;

    /// <inheritdoc />
    public override PyObject? GetAttribute(string name) => name switch
    {
        // Reading methods only: the view hands out the mapping's own, and nothing that writes.
        "get" or "keys" or "values" or "items" or "copy" =>
            mapping.GetAttribute(name) ?? BuiltinMethods.TryBind(machine, mapping, name),
        "__class__" => type,
        _ => null,
    };
}

/// <summary>A small extension for building a value and filling it in one expression.</summary>
internal static class ObjectExtensions
{
    public static T Also<T>(this T value, Action<T> fill)
    {
        fill(value);
        return value;
    }
}

/// <summary>Guards a repr against a value that contains itself.</summary>
internal static class PyReprGuard
{
    [ThreadStatic]
    private static HashSet<object>? active;

    public static bool TryEnter(object value) => (active ??= new HashSet<object>(ReferenceEqualityComparer.Instance)).Add(value);

    public static void Exit(object value) => active?.Remove(value);
}
