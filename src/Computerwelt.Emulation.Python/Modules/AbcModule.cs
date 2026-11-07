using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules;

/// <summary>
/// <c>abc</c> and <c>collections.abc</c>.
/// </summary>
/// <remarks>
/// <para>
/// Abstract-method enforcement is built into instantiation (a class carrying
/// <c>__abstractmethods__</c> refuses to be instantiated while any abstract method is left
/// unimplemented), so <c>ABC</c> is an ordinary class with that marker and <c>ABCMeta</c> is
/// accepted as the one <c>metaclass=</c> a program may name.
/// </para>
/// <para>
/// The <c>collections.abc</c> names answer <c>isinstance</c> structurally — a value is an
/// <c>Iterable</c> when it can be iterated — and may be listed as bases, where they add
/// nothing: CPython's mixin methods are not provided.
/// </para>
/// </remarks>
internal static class AbcModule
{
    public static PyModuleObject Create(VirtualMachine machine)
    {
        var module = new PyModuleObject("abc");

        var abcMeta = new PyClass("ABCMeta", new PyDict(), [], machine);
        var abc = new PyClass("ABC", new PyDict(), [], machine);
        abc.Members.Set(new PyStr("__abstractmethods__"), PyBool.True);

        module.Add("ABC", abc);
        module.Add("ABCMeta", abcMeta);

        module.Add("abstractmethod", static arguments =>
        {
            Arity.Exact("abstractmethod", arguments, 1);
            MarkAbstract(arguments[0]);
            return arguments[0];
        });

        module.Add("abstractproperty", static arguments =>
        {
            Arity.Exact("abstractproperty", arguments, 1);
            MarkAbstract(arguments[0]);
            return new PyProperty(arguments[0], null, null, null);
        });

        module.Add("abstractclassmethod", static arguments =>
        {
            Arity.Exact("abstractclassmethod", arguments, 1);
            MarkAbstract(arguments[0]);
            return new PyClassMethod(arguments[0]);
        });

        module.Add("abstractstaticmethod", static arguments =>
        {
            Arity.Exact("abstractstaticmethod", arguments, 1);
            MarkAbstract(arguments[0]);
            return new PyStaticMethod(arguments[0]);
        });

        module.Add("get_cache_token", static _ => PyInt.From(0));

        return module;
    }

    private static void MarkAbstract(PyObject function)
    {
        if (!function.SetAttribute("__isabstractmethod__", PyBool.True))
        {
            throw new PyRaise(PyErrors.TypeError(
                $"abstractmethod() cannot mark a '{function.TypeName}' object"));
        }
    }

    /// <summary>Builds <c>collections.abc</c>.</summary>
    public static PyModuleObject CreateCollectionsAbc()
    {
        var module = new PyModuleObject("collections.abc");

        void Add(string name, Func<PyObject, bool> matches) =>
            module.Add(name, Protocol(name, matches));

        Add("Iterable", static v => Has(v, "__iter__") || (v is not PyInstance && v.Iterate() is not null));
        Add("Iterator", static v => v is PyIterator or PyGenerator || (Has(v, "__next__") && Has(v, "__iter__")));
        Add("Generator", static v => v is PyGenerator);
        Add("Reversible", static v => v is PyList or PyTuple or PyStr or PyRange or PyDict || Has(v, "__reversed__"));
        Add("Container", static v => Has(v, "__contains__") || (v is not PyInstance && v.Iterate() is not null));
        Add("Sized", static v => Has(v, "__len__") || (v is not PyInstance && v.Length() is not null));
        Add("Collection", static v => (Has(v, "__len__") && Has(v, "__iter__")) || (v is not PyInstance && v.Length() is not null && v.Iterate() is not null));
        Add("Hashable", static v => !(v is PyList or PyDict or PySet { IsFrozen: false } or PyByteArray)
            && !(v is PyInstance instance && instance.Class.Lookup("__hash__") is PyNone));
        Add("Callable", static v => v is PyCallable or PyExceptionType || Has(v, "__call__"));
        Add("Sequence", static v => ClassModel.Unwrap(v) is PyList or PyTuple or PyStr or PyRange or PyBytes or PyByteArray or PyNamedTuple);
        Add("MutableSequence", static v => ClassModel.Unwrap(v) is PyList or PyByteArray or PyDeque);
        Add("Mapping", static v => ClassModel.Unwrap(v) is PyDict);
        Add("MutableMapping", static v => ClassModel.Unwrap(v) is PyDict);
        Add("Set", static v => ClassModel.Unwrap(v) is PySet);
        Add("MutableSet", static v => ClassModel.Unwrap(v) is PySet { IsFrozen: false });
        Add("MappingView", static v => v is PyView);
        Add("KeysView", static v => v is PyView);
        Add("ItemsView", static v => v is PyView);
        Add("ValuesView", static v => v is PyView);
        Add("Awaitable", static v => v is PyCoroutine or PyFuture);
        Add("Coroutine", static v => v is PyCoroutine);
        Add("AsyncIterable", static v => Has(v, "__aiter__"));
        Add("AsyncIterator", static v => Has(v, "__anext__"));
        Add("ByteString", static v => v is PyBytes or PyByteArray);
        Add("Buffer", static v => v is PyBytes or PyByteArray);

        return module;
    }

    private static bool Has(PyObject value, string dunder) =>
        value is PyInstance instance && instance.Class.Lookup(dunder) is { } found && found is not PyNone;

    /// <summary>A type that matches structurally and may be named as a base without effect.</summary>
    private static PyType Protocol(string name, Func<PyObject, bool> matches)
    {
        var type = new PyType(name, matches, (_, _) =>
            throw new PyRaise(PyErrors.TypeError($"Can't instantiate abstract class {name}")));

        // Marks the type as a pure interface: listing it among a class's bases is allowed
        // and contributes nothing to the instance's layout.
        type.Members["__abc_protocol__"] = PyBool.True;
        return type;
    }
}
