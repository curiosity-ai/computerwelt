namespace Computerwelt.Emulation.Python.Runtime;

/// <summary>
/// The rules of the class model that do not belong to any one object: how a member found on
/// a class is bound, what a class extending a built-in inherits from it, and what lies past
/// the end of a user-defined MRO (<c>object</c>, <c>BaseException</c>, the built-in).
/// </summary>
internal static class ClassModel
{
    /// <summary>The built-in types a class may extend.</summary>
    /// <remarks>
    /// Each needs an instance to carry a value of the type, and every one listed here is a
    /// plain value the rest of the runtime already knows how to operate on. Anything else —
    /// <c>bool</c>, <c>range</c>, <c>function</c> — CPython itself refuses, or nobody
    /// subclasses.
    /// </remarks>
    public static bool Subclassable(PyType type) =>
        type.Name is "dict" or "list" or "set" or "frozenset" or "str" or "int" or "float" or "tuple" or "bytes"
        || type.Members.ContainsKey("__subclassable__");

    /// <summary>Whether a built-in base is immutable, and so built entirely by <c>__new__</c>.</summary>
    public static bool Immutable(PyType type) =>
        type.Name is "str" or "int" or "float" or "tuple" or "bytes" or "frozenset";

    /// <summary>A data descriptor: one that takes precedence over an instance's own field.</summary>
    public static bool IsDataDescriptor(PyObject member) =>
        member is PyProperty
        || (member is PyInstance descriptor
            && (descriptor.Class.Lookup("__set__") is not null || descriptor.Class.Lookup("__delete__") is not null)
            && descriptor.Class.Lookup("__get__") is not null);

    /// <summary>Binds a member found on a class for access through an instance.</summary>
    public static PyObject BindToInstance(VirtualMachine machine, PyObject member, PyObject instance, PyClass type) =>
        member switch
        {
            PyFunction function => function.Bind(instance),
            PyClassMethod classMethod => BindCallable(classMethod.Function, type),
            PyStaticMethod staticMethod => staticMethod.Function,
            PyProperty property => property.Getter is { } getter
                ? machine.Call(getter, [instance])
                : throw new PyRaise(new PyException(
                    PyExceptionType.AttributeError,
                    $"property '{property.Name}' of '{type.Name}' object has no getter")),
            PyBuiltinFunction { BindsAsMethod: true } builtin => new PyMethod(builtin, instance),
            PyInstance descriptor when descriptor.Class.Lookup("__get__") is not null
                && descriptor.Dunder("__get__") is { } get => descriptor.Invoke(get, [instance, type]),
            _ => member,
        };

    /// <summary>Binds a member found on a class for access through the class itself.</summary>
    public static PyObject BindToClass(VirtualMachine machine, PyObject member, PyClass type)
    {
        _ = machine;

        return member switch
        {
            PyClassMethod classMethod => BindCallable(classMethod.Function, type),
            PyStaticMethod staticMethod => staticMethod.Function,
            PyInstance descriptor when descriptor.Class.Lookup("__get__") is not null
                && descriptor.Dunder("__get__") is { } get => descriptor.Invoke(get, [PyNone.Instance, type]),
            _ => member,
        };
    }

    /// <summary>Binds any callable to a receiver.</summary>
    public static PyObject BindCallable(PyObject function, PyObject self) =>
        function is PyFunction { BoundSelf: null } plain ? plain.Bind(self) : new PyMethod(function, self);

    /// <summary>The value of a built-in-derived instance, or the object itself.</summary>
    public static PyObject Unwrap(PyObject value) =>
        value is PyInstance { BuiltinValue: { } inner } ? inner : value;

    private static readonly Dictionary<string, string> Forward = new(StringComparer.Ordinal)
    {
        ["__add__"] = "+", ["__sub__"] = "-", ["__mul__"] = "*", ["__truediv__"] = "/",
        ["__floordiv__"] = "//", ["__mod__"] = "%", ["__pow__"] = "**", ["__and__"] = "&",
        ["__or__"] = "|", ["__xor__"] = "^", ["__lshift__"] = "<<", ["__rshift__"] = ">>",
    };

    private static readonly Dictionary<string, string> Reflected = new(StringComparer.Ordinal)
    {
        ["__radd__"] = "+", ["__rsub__"] = "-", ["__rmul__"] = "*", ["__rtruediv__"] = "/",
        ["__rfloordiv__"] = "//", ["__rmod__"] = "%", ["__rpow__"] = "**", ["__rand__"] = "&",
        ["__ror__"] = "|", ["__rxor__"] = "^", ["__rlshift__"] = "<<", ["__rrshift__"] = ">>",
    };

    private static readonly Dictionary<string, string> Comparisons = new(StringComparer.Ordinal)
    {
        ["__lt__"] = "<", ["__le__"] = "<=", ["__gt__"] = ">", ["__ge__"] = ">=",
    };

    /// <summary>
    /// A special method of a built-in value, as a subclass inherits it: the protocol the
    /// value already implements, exposed under its dunder name.
    /// </summary>
    /// <returns>A method bound to <paramref name="inner"/>, or null when the value has no such protocol.</returns>
    public static PyObject? BuiltinDunder(PyObject inner, string name)
    {
        var container = inner.Iterate() is not null;

        return name switch
        {
            "__repr__" => Method(name, inner, static (v, _) => new PyStr(v.Repr())),
            "__str__" => Method(name, inner, static (v, _) => new PyStr(v.Display())),
            "__eq__" => Method(name, inner, static (v, a) => PyBool.Of(v.PyEquals(Unwrap(a[0])))),
            "__ne__" => Method(name, inner, static (v, a) => PyBool.Of(!v.PyEquals(Unwrap(a[0])))),
            "__hash__" => Method(name, inner, static (v, _) => PyInt.From(v.PyHash())),
            "__bool__" => Method(name, inner, static (v, _) => PyBool.Of(v.IsTruthy())),
            "__len__" when inner.Length() is not null => Method(name, inner, static (v, _) => PyInt.From(v.Length()!.Value)),
            "__iter__" when container => Method(name, inner, static (v, _) => new PyIterator(v.Iterate()!, v.TypeName + "_iterator")),
            "__contains__" when container => Method(name, inner, static (v, a) => PyBool.Of(v.Contains(a[0]))),
            "__getitem__" when container => Method(name, inner, static (v, a) => v.GetItem(a[0])),
            "__setitem__" when container && inner is PyDict or PyList => Method(name, inner, static (v, a) =>
            {
                v.SetItem(a[0], a[1]);
                return PyNone.Instance;
            }),
            "__delitem__" when container && inner is PyDict or PyList => Method(name, inner, static (v, a) =>
            {
                v.DeleteItem(a[0]);
                return PyNone.Instance;
            }),
            _ when Forward.TryGetValue(name, out var op) =>
                Method(name, inner, (v, a) => Operators.Binary(op, v, Unwrap(a[0]))),
            _ when Reflected.TryGetValue(name, out var reflected) =>
                Method(name, inner, (v, a) => Operators.Binary(reflected, Unwrap(a[0]), v)),
            _ when Comparisons.TryGetValue(name, out var comparison) =>
                Method(name, inner, (v, a) => Operators.Compare(comparison, v, Unwrap(a[0]))),
            "__neg__" => Method(name, inner, static (v, _) => Operators.Unary("-", v)),
            "__abs__" when inner is PyInt or PyFloat => Method(name, inner, static (v, _) => v is PyInt i
                ? PyInt.From(System.Numerics.BigInteger.Abs(i.Value))
                : new PyFloat(Math.Abs(((PyFloat)v).Value))),
            "__int__" when inner is PyInt or PyFloat => Method(name, inner, static (v, _) => v is PyInt ? v : PyInt.From(new System.Numerics.BigInteger(Math.Truncate(((PyFloat)v).Value)))),
            "__float__" when inner is PyInt or PyFloat => Method(name, inner, static (v, _) => v is PyFloat ? v : new PyFloat((double)((PyInt)v).Value)),
            _ => null,
        };

        static PyBoundMethod Method(string name, PyObject receiver, Func<PyObject, PyObject[], PyObject> body) =>
            new(name, receiver, body);
    }

    /// <summary>
    /// Builds an instance of <paramref name="type"/> without running <c>__init__</c>: what
    /// <c>object.__new__</c> — or a built-in base's <c>__new__</c> — does.
    /// </summary>
    /// <param name="machine">The machine the instance's methods run on.</param>
    /// <param name="type">The class to instantiate.</param>
    /// <param name="arguments">
    /// What the built-in base's constructor receives: the value of an immutable base is
    /// fixed here, a mutable one starts empty unless <paramref name="fillMutable"/>.
    /// </param>
    /// <param name="keywords">Keyword arguments for the built-in constructor.</param>
    /// <param name="fillMutable">Whether a mutable built-in base is built from the arguments too.</param>
    public static PyInstance CreateInstance(
        VirtualMachine machine,
        PyClass type,
        PyObject[] arguments,
        PyDict? keywords,
        bool fillMutable)
    {
        if (type.ExceptionType is not null)
        {
            throw new PyRaise(PyErrors.TypeError($"cannot create '{type.Name}' instances this way"));
        }

        var instance = new PyInstance(type, machine);

        if (type.BuiltinBase is { } builtin)
        {
            instance.BuiltinValue = Immutable(builtin) || fillMutable
                ? builtin.Construct([.. arguments.Select(Unwrap)], keywords)
                : builtin.Construct([], null);
        }

        return instance;
    }

    /// <summary>
    /// An attribute reached past the end of a user MRO — through <c>super()</c> from the
    /// last user class, or on a class that does not override it.
    /// </summary>
    public static PyObject? BaseAttribute(VirtualMachine machine, PyClass type, PyObject receiver, string name)
    {
        switch (receiver)
        {
            case PyException exception:
                return name switch
                {
                    "__init__" => Function(name, (arguments, _) =>
                    {
                        exception.Reinitialize(machine, arguments);
                        return PyNone.Instance;
                    }),
                    "__str__" => Function(name, (_, _) => new PyStr(exception.BaseDisplay())),
                    "__repr__" => Function(name, (_, _) => new PyStr(exception.BaseRepr())),
                    "__new__" => Function(name, (arguments, _) => machine.Call(type, arguments.Skip(1).ToArray())),
                    _ => exception.BaseAttribute(name) ?? ObjectAttribute(machine, type, receiver, name),
                };

            case PyInstance { BuiltinValue: { } inner } instance:
                if (name == "__init__")
                {
                    return Function(name, (arguments, keywords) =>
                    {
                        FillBuiltin(instance, arguments, keywords);
                        return PyNone.Instance;
                    });
                }

                if (name == "__new__")
                {
                    return New(machine, type);
                }

                return BuiltinDunder(inner, name)
                    ?? Attributes.TryGet(machine, inner, name)
                    ?? ObjectAttribute(machine, type, receiver, name);

            case PyClass:
                return name switch
                {
                    "__new__" => New(machine, type),
                    "__init_subclass__" => Function(name, static (_, _) => PyNone.Instance),
                    _ => type.BuiltinBase?.GetAttribute(name) ?? type.ExceptionType?.GetAttribute(name),
                };

            default:
                return ObjectAttribute(machine, type, receiver, name);
        }
    }

    /// <summary>The methods every object inherits from <c>object</c>.</summary>
    public static PyObject? ObjectAttribute(VirtualMachine machine, PyClass type, PyObject receiver, string name) =>
        name switch
        {
            "__init__" => Function(name, (arguments, keywords) =>
                arguments.Length > 0 || keywords is { Count: > 0 }
                    ? throw new PyRaise(PyErrors.TypeError(
                        "object.__init__() takes exactly one argument (the instance to initialize)"))
                    : PyNone.Instance),
            "__new__" => New(machine, type),
            "__setattr__" => Function(name, (arguments, _) =>
            {
                Arity.Exact("__setattr__", arguments, 2);
                RawSet(receiver, Arity.AttributeName(arguments[0]), arguments[1]);
                return PyNone.Instance;
            }),
            "__getattribute__" => Function(name, (arguments, _) =>
            {
                Arity.Exact("__getattribute__", arguments, 1);
                var attribute = Arity.AttributeName(arguments[0]);
                return RawGet(receiver, attribute)
                    ?? throw new PyRaise(PyErrors.AttributeError(receiver.TypeName, attribute));
            }),
            "__delattr__" => Function(name, (arguments, _) =>
            {
                Arity.Exact("__delattr__", arguments, 1);

                if (receiver is PyInstance instance)
                {
                    instance.RawDeleteAttribute(Arity.AttributeName(arguments[0]));
                }

                return PyNone.Instance;
            }),
            "__repr__" => Function(name, (_, _) =>
                new PyStr(receiver is PyInstance instance ? instance.DefaultRepr() : receiver.Repr())),
            "__str__" => Function(name, (_, _) => new PyStr(receiver.Repr())),
            "__eq__" => Function(name, (arguments, _) =>
                ReferenceEquals(receiver, arguments[0]) ? PyBool.True : Builtins.NotImplementedSingleton.Instance),
            "__ne__" => Function(name, (arguments, _) => PyBool.Of(!receiver.PyEquals(arguments[0]))),
            "__hash__" => Function(name, (_, _) =>
                PyInt.From(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(receiver))),
            "__init_subclass__" => Function(name, static (_, _) => PyNone.Instance),
            "__format__" => Function(name, (arguments, _) =>
                new PyStr(arguments.Length > 0 && arguments[0].Display().Length > 0
                    ? throw new PyRaise(PyErrors.TypeError(
                        $"unsupported format string passed to {receiver.TypeName}.__format__"))
                    : receiver.Display())),
            "__dir__" => Function(name, (_, _) => Builtins.Introspection.Dir(machine, receiver)),
            "__class__" => type,
            _ => null,
        };

    /// <summary><c>object.__new__</c> for a class: an instance with nothing run on it.</summary>
    private static PyBuiltinFunction New(VirtualMachine machine, PyClass owner) =>
        new("__new__", (arguments, keywords) =>
        {
            if (arguments.Length == 0 || arguments[0] is not PyClass target)
            {
                throw new PyRaise(PyErrors.TypeError("object.__new__(X): X is not a type object"));
            }

            if (!target.IsSubclassOf(owner) && !owner.IsSubclassOf(target))
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"object.__new__({target.Name}): {target.Name} is not a subtype of object"));
            }

            if (target.ExceptionType is not null)
            {
                return machine.Instantiate(target, arguments.Skip(1).ToArray(), keywords, runInit: false);
            }

            var rest = arguments.Skip(1).ToArray();

            // A mutable built-in starts empty, as `dict.__new__` does; its `__init__` fills it.
            return CreateInstance(machine, target, rest, keywords, fillMutable: false);
        });

    /// <summary>The body of a built-in base's <c>__init__</c>: fill a mutable value from the arguments.</summary>
    private static void FillBuiltin(PyInstance instance, PyObject[] arguments, PyDict? keywords)
    {
        switch (instance.BuiltinValue)
        {
            case PyDict dict:
                foreach (var (key, value) in ((PyDict)TypeRegistry.Dict.Construct([.. arguments.Select(Unwrap)], keywords)).Entries)
                {
                    dict.Set(key, value);
                }

                break;

            case PyList list:
                if (arguments.Length > 0)
                {
                    list.Items.Clear();
                    list.Items.AddRange(VirtualMachine.RequireIterable(Unwrap(arguments[0])));
                }

                break;

            case PySet set when !set.IsFrozen:
                if (arguments.Length > 0)
                {
                    foreach (var item in VirtualMachine.RequireIterable(Unwrap(arguments[0])))
                    {
                        set.Add(item);
                    }
                }

                break;
        }
    }

    private static PyObject? RawGet(PyObject receiver, string name) => receiver switch
    {
        PyInstance instance => instance.RawGetAttribute(name),
        _ => receiver.GetAttribute(name),
    };

    private static void RawSet(PyObject receiver, string name, PyObject value)
    {
        var accepted = receiver switch
        {
            PyInstance instance => instance.RawSetAttribute(name, value),
            _ => receiver.SetAttribute(name, value),
        };

        if (!accepted)
        {
            throw new PyRaise(PyErrors.AttributeError(receiver.TypeName, name));
        }
    }

    private static PyBuiltinFunction Function(string name, Func<PyObject[], PyDict?, PyObject> body) => new(name, body);
}
