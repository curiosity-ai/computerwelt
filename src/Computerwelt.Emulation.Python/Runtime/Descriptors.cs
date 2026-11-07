namespace Computerwelt.Emulation.Python.Runtime;

/// <summary>
/// <c>classmethod(f)</c>: a function that receives the class rather than the instance.
/// </summary>
/// <remarks>
/// Not an extension of upstream's object model so much as the part of CPython's that
/// upstream left out (<c>.reference/monty/limitations/classes.md</c>): a class body holding
/// one of these is binding a method to the class, which is how alternative constructors
/// (<c>Point.from_tuple</c>) are written.
/// </remarks>
public sealed class PyClassMethod(PyObject function) : PyObject
{
    /// <summary>The wrapped callable.</summary>
    public PyObject Function { get; } = function;

    /// <inheritdoc />
    public override string TypeName => "classmethod";

    /// <inheritdoc />
    public override string Repr() => $"<classmethod({Function.Repr()})>";

    /// <inheritdoc />
    public override PyObject? GetAttribute(string name) => name switch
    {
        "__func__" or "__wrapped__" => Function,
        "__isabstractmethod__" => Function.GetAttribute("__isabstractmethod__"),
        _ => Function.GetAttribute(name),
    };

    /// <inheritdoc />
    public override bool SetAttribute(string name, PyObject value) => Function.SetAttribute(name, value);
}

/// <summary><c>staticmethod(f)</c>: a function that receives no receiver at all.</summary>
public sealed class PyStaticMethod(PyObject function) : PyObject
{
    /// <summary>The wrapped callable.</summary>
    public PyObject Function { get; } = function;

    /// <inheritdoc />
    public override string TypeName => "staticmethod";

    /// <inheritdoc />
    public override string Repr() => $"<staticmethod({Function.Repr()})>";

    /// <inheritdoc />
    public override PyObject? GetAttribute(string name) => name switch
    {
        "__func__" or "__wrapped__" => Function,
        _ => Function.GetAttribute(name),
    };

    /// <inheritdoc />
    public override bool SetAttribute(string name, PyObject value) => Function.SetAttribute(name, value);
}

/// <summary>
/// <c>property(fget, fset, fdel, doc)</c>: a data descriptor computing an attribute.
/// </summary>
public sealed class PyProperty : PyObject
{
    /// <summary>Creates a property from its accessors.</summary>
    public PyProperty(PyObject? getter, PyObject? setter, PyObject? deleter, PyObject? doc)
    {
        Getter = getter is PyNone ? null : getter;
        Setter = setter is PyNone ? null : setter;
        Deleter = deleter is PyNone ? null : deleter;
        Doc = doc is null or PyNone ? Getter?.GetAttribute("__doc__") : doc;
    }

    /// <summary>The getter, or null.</summary>
    public PyObject? Getter { get; }

    /// <summary>The setter, or null.</summary>
    public PyObject? Setter { get; }

    /// <summary>The deleter, or null.</summary>
    public PyObject? Deleter { get; }

    /// <summary>The docstring.</summary>
    public PyObject? Doc { get; }

    /// <summary>The attribute name the property was assigned to, once the class knows it.</summary>
    internal string? Name { get; set; }

    /// <inheritdoc />
    public override string TypeName => "property";

    /// <inheritdoc />
    public override string Repr() =>
        $"<property object at 0x{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this):x8}>";

    /// <inheritdoc />
    public override PyObject? GetAttribute(string name) => name switch
    {
        "fget" => Getter ?? PyNone.Instance,
        "fset" => Setter ?? PyNone.Instance,
        "fdel" => Deleter ?? PyNone.Instance,
        "__doc__" => Doc ?? PyNone.Instance,
        "__isabstractmethod__" => Getter?.GetAttribute("__isabstractmethod__") ?? PyBool.False,
        "getter" => new PyBuiltinFunction("getter", arguments => Copy(getter: Single(arguments, "getter"))),
        "setter" => new PyBuiltinFunction("setter", arguments => Copy(setter: Single(arguments, "setter"))),
        "deleter" => new PyBuiltinFunction("deleter", arguments => Copy(deleter: Single(arguments, "deleter"))),
        _ => null,
    };

    private static PyObject Single(PyObject[] arguments, string name)
    {
        Arity.Exact(name, arguments, 1);
        return arguments[0];
    }

    private PyProperty Copy(PyObject? getter = null, PyObject? setter = null, PyObject? deleter = null) =>
        new(getter ?? Getter, setter ?? Setter, deleter ?? Deleter, Doc) { Name = Name };
}

/// <summary>
/// A callable bound to a receiver that is not a <see cref="PyFunction"/> — a classmethod
/// wrapping a builtin, or a decorated method whose decorator returned some other callable.
/// </summary>
public sealed class PyMethod(PyObject function, PyObject self) : PyCallable
{
    /// <summary>The underlying callable.</summary>
    public PyObject Function { get; } = function;

    /// <summary>The receiver passed as the first argument.</summary>
    public PyObject Self { get; } = self;

    /// <inheritdoc />
    public override string Name => (Function.GetAttribute("__name__") as PyStr)?.Value ?? "method";

    /// <inheritdoc />
    public override string TypeName => "method";

    /// <inheritdoc />
    public override string Repr() => $"<bound method {Name} of {Self.Repr()}>";

    /// <inheritdoc />
    public override PyObject? GetAttribute(string name) => name switch
    {
        "__func__" => Function,
        "__self__" => Self,
        _ => Function.GetAttribute(name) ?? base.GetAttribute(name),
    };
}

/// <summary>
/// <c>super()</c>: attribute lookup that starts after a given class in the receiver's MRO.
/// </summary>
public sealed class PySuper : PyObject
{
    private readonly VirtualMachine _machine;

    /// <summary>Creates a super object for <paramref name="start"/> and <paramref name="receiver"/>.</summary>
    public PySuper(VirtualMachine machine, PyClass start, PyObject receiver)
    {
        _machine = machine;
        Start = start;
        Receiver = receiver;
        ReceiverClass = receiver switch
        {
            PyClass type => type,
            PyInstance instance => instance.Class,
            PyException { UserClass: { } userClass } => userClass,
            _ => throw new PyRaise(PyErrors.TypeError(
                "super(type, obj): obj must be an instance or subtype of type")),
        };

        if (!ReceiverClass.IsSubclassOf(start))
        {
            throw new PyRaise(PyErrors.TypeError(
                "super(type, obj): obj must be an instance or subtype of type"));
        }
    }

    /// <summary>The class lookup starts after.</summary>
    public PyClass Start { get; }

    /// <summary>The object methods are bound to.</summary>
    public PyObject Receiver { get; }

    /// <summary>The receiver's class (or the receiver itself, when it is a class).</summary>
    public PyClass ReceiverClass { get; }

    /// <inheritdoc />
    public override string TypeName => "super";

    /// <inheritdoc />
    public override string Repr() => $"<super: <class '{Start.Name}'>, <{ReceiverClass.Name} object>>";

    /// <inheritdoc />
    public override PyObject? GetAttribute(string name)
    {
        var mro = ReceiverClass.Mro;
        var index = -1;

        for (var i = 0; i < mro.Count; i++)
        {
            if (ReferenceEquals(mro[i], Start))
            {
                index = i;
                break;
            }
        }

        for (var i = index + 1; i < mro.Count; i++)
        {
            if (mro[i].Members.TryGetValue(new PyStr(name), out var member))
            {
                return Receiver is PyClass
                    ? ClassModel.BindToClass(_machine, member, ReceiverClass)
                    : ClassModel.BindToInstance(_machine, member, Receiver, ReceiverClass);
            }
        }

        return ClassModel.BaseAttribute(_machine, ReceiverClass, Receiver, name);
    }
}
