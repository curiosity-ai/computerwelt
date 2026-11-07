using System.Numerics;

namespace Computerwelt.Emulation.Python.Runtime;

/// <summary>
/// A class defined in Python, with bases and a method resolution order.
/// </summary>
/// <remarks>
/// <para>
/// Upstream Monty has no inheritance (<c>.reference/monty/limitations/classes.md</c>); this
/// port adds it because code people paste into a notebook subclasses things as a matter of
/// course. The model is CPython's: bases are linearized with C3 into <see cref="Mro"/>, an
/// attribute is the first one found along it, and <c>super()</c> continues the search past
/// a given class.
/// </para>
/// <para>
/// A class may also derive from one built-in type (<see cref="BuiltinBase"/> — <c>dict</c>,
/// <c>list</c>, <c>str</c>, …), in which case each instance carries a value of that type
/// and every protocol the class does not override falls through to it; or from exception
/// classes, in which case its instances are real exceptions (<see cref="ExceptionType"/>)
/// so that <c>raise</c> and <c>except</c> work on them unchanged.
/// </para>
/// </remarks>
public sealed class PyClass : PyCallable
{
    private readonly List<PyClass> _mro;

    /// <summary>Creates a class with no bases.</summary>
    public PyClass(string name, PyDict members)
        : this(name, members, [], machine: null)
    {
    }

    /// <summary>Creates a class from its namespace and bases.</summary>
    /// <param name="name">The class name.</param>
    /// <param name="members">The class namespace.</param>
    /// <param name="bases">
    /// The bases, as the class statement listed them: other classes, built-in types and
    /// exception classes.
    /// </param>
    /// <param name="machine">The machine the class's methods run on.</param>
    public PyClass(string name, PyDict members, IReadOnlyList<PyObject> bases, VirtualMachine? machine)
    {
        Name = name;
        Members = members;
        Machine = machine;

        var classBases = new List<PyClass>();
        var exceptionBases = new List<PyExceptionType>();
        var declared = new List<PyObject>();

        foreach (var candidate in bases)
        {
            switch (candidate)
            {
                case PyClass type:
                    classBases.Add(type);
                    declared.Add(type);

                    if (type.ExceptionType is { } inherited)
                    {
                        exceptionBases.Add(inherited);
                    }

                    if (type.BuiltinBase is { } builtin)
                    {
                        SetBuiltinBase(builtin);
                    }

                    Behaviour ??= type.Behaviour;
                    break;

                case PyExceptionType exception:
                    exceptionBases.Add(exception);
                    declared.Add(exception);
                    break;

                case PyType builtinType:
                    declared.Add(builtinType);

                    // `object` and the `collections.abc` interfaces add nothing to an
                    // instance's layout; any other built-in becomes the value it extends.
                    if (!ReferenceEquals(builtinType, TypeRegistry.Object)
                        && !builtinType.Members.ContainsKey("__abc_protocol__"))
                    {
                        SetBuiltinBase(builtinType);
                    }

                    break;

                default:
                    throw new PyRaise(PyErrors.TypeError(
                        $"bases must be types, not {candidate.TypeName}"));
            }
        }

        if (classBases.Distinct().Count() != classBases.Count)
        {
            throw new PyRaise(PyErrors.TypeError(
                $"duplicate base class {classBases.GroupBy(static c => c).First(static g => g.Count() > 1).Key.Name}"));
        }

        Bases = declared;
        _mro = Linearize(this, classBases);

        if (exceptionBases.Count > 0)
        {
            if (BuiltinBase is not null)
            {
                throw new PyRaise(PyErrors.TypeError("multiple bases have instance lay-out conflict"));
            }

            ExceptionType = PyExceptionType.DefineUserException(
                this,
                exceptionBases[0],
                exceptionBases.Skip(1).FirstOrDefault(other => !exceptionBases[0].IsSubclassOf(other)));
        }
    }

    private void SetBuiltinBase(PyType builtin)
    {
        if (BuiltinBase is not null && !ReferenceEquals(BuiltinBase, builtin))
        {
            throw new PyRaise(PyErrors.TypeError("multiple bases have instance lay-out conflict"));
        }

        if (!ClassModel.Subclassable(builtin))
        {
            throw new PyRaise(PyErrors.TypeError(
                $"type '{builtin.Name}' is not an acceptable base type"));
        }

        BuiltinBase = builtin;
    }

    /// <inheritdoc />
    public override string Name { get; }

    /// <summary>The class-level namespace: methods and class attributes.</summary>
    public PyDict Members { get; }

    /// <summary>The bases as declared, <c>object</c> omitted.</summary>
    public IReadOnlyList<PyObject> Bases { get; }

    /// <summary>The method resolution order: this class first, then its Python bases.</summary>
    public IReadOnlyList<PyClass> Mro => _mro;

    /// <summary>The MRO as its list, so a loop over it enumerates without boxing.</summary>
    internal List<PyClass> MroList => _mro;

    /// <summary>The built-in type instances of this class extend, if any.</summary>
    public PyType? BuiltinBase { get; private set; }

    /// <summary>
    /// The exception class standing behind this one when it derives from an exception —
    /// what <c>except</c> matching and the traceback see.
    /// </summary>
    public PyExceptionType? ExceptionType { get; }

    /// <summary>The machine the class was created on.</summary>
    public VirtualMachine? Machine { get; internal set; }

    /// <summary>
    /// Class-level behaviour a metaclass would supply in CPython — iteration over the
    /// class, subscripting it, calling it — for the classes a module defines in C#, such
    /// as <c>enum.Enum</c>. Inherited by subclasses.
    /// </summary>
    public ClassBehaviour? Behaviour { get; set; }

    /// <summary>State a <see cref="Behaviour"/> keeps per class.</summary>
    public object? BehaviourState { get; set; }

    /// <inheritdoc />
    public override string TypeName => "type";

    /// <inheritdoc />
    public override string Repr() => Behaviour?.ClassRepr(this) ?? $"<class '{Name}'>";

    /// <summary>True when this class is <paramref name="other"/> or derives from it.</summary>
    public bool IsSubclassOf(PyClass other) => _mro.Contains(other);

    /// <summary>
    /// Bumped whenever any class's namespace changes, which is what invalidates every
    /// lookup cache: a subclass caches what it found on its bases, so a change to a base
    /// has to reach it, and a global counter is the cheapest way to say "something changed".
    /// Classes rarely change after they are built, so the caches almost never refill.
    /// </summary>
    private static int s_epoch;

    private readonly Dictionary<string, PyObject?> _lookups = new(StringComparer.Ordinal);
    private int _lookupEpoch = -1;

    /// <summary>Records that a class namespace changed, so cached lookups are redone.</summary>
    public static void NamespaceChanged() => Interlocked.Increment(ref s_epoch);

    private int _shapeEpoch = -1;
    private bool _plainAttributes;

    /// <summary>
    /// True when reading an attribute of an instance can start with the instance's own
    /// fields: no class along the MRO defines <c>__getattribute__</c> or a data descriptor
    /// (a property, or an object with <c>__set__</c>) that would have to win over them.
    /// Most classes are like this, and it is the hot path of every <c>self.x</c>.
    /// </summary>
    internal bool HasPlainAttributes
    {
        get
        {
            var epoch = Volatile.Read(ref s_epoch);

            if (_shapeEpoch != epoch)
            {
                _plainAttributes = Lookup("__getattribute__") is not PyFunction
                    && !_mro.Any(static type => type.Members.Entries.Any(static e => ClassModel.IsDataDescriptor(e.Value)));
                _shapeEpoch = epoch;
            }

            return _plainAttributes;
        }
    }

    /// <summary>Finds an attribute along the MRO without binding it.</summary>
    public PyObject? Lookup(string name)
    {
        var epoch = Volatile.Read(ref s_epoch);

        if (_lookupEpoch != epoch)
        {
            _lookups.Clear();
            _lookupEpoch = epoch;
        }
        else if (_lookups.TryGetValue(name, out var cached))
        {
            return cached;
        }

        var found = Walk(name);
        _lookups[name] = found;
        return found;
    }

    private PyObject? Walk(string name)
    {
        foreach (var type in _mro)
        {
            if (type.Members.TryGetValue(name, out var value))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>Finds an attribute along the MRO, with the class that defines it.</summary>
    public (PyObject Value, PyClass Owner)? LookupWithOwner(string name)
    {
        var key = new PyStr(name);

        foreach (var type in _mro)
        {
            if (type.Members.TryGetValue(key, out var value))
            {
                return (value, type);
            }
        }

        return null;
    }

    /// <summary>The full <c>__mro__</c>, built-in ancestors and <c>object</c> included.</summary>
    public PyTuple FullMro()
    {
        var items = new List<PyObject>(_mro);

        if (ExceptionType is { } exception)
        {
            for (var current = exception; current is not null; current = current.BaseType)
            {
                if (current.UserClass is null)
                {
                    items.Add(current);
                }
            }
        }
        else if (BuiltinBase is { } builtin)
        {
            items.Add(builtin);
        }

        items.Add(TypeRegistry.Object);
        return new PyTuple([.. items]);
    }

    /// <inheritdoc />
    /// <remarks>
    /// A class's own name always wins over a member of the same name — <c>C.__name__</c>
    /// is <c>'C'</c> even when the body assigns <c>__name__</c>, which only an instance
    /// then sees.
    /// </remarks>
    public override PyObject? GetAttribute(string name)
    {
        switch (name)
        {
            case "__name__" or "__qualname__":
                return new PyStr(Name);
            case "__mro__":
                return FullMro();
            case "__bases__":
                return new PyTuple(Bases.Count == 0 ? [TypeRegistry.Object] : [.. Bases]);
            case "__base__":
                return Bases.Count == 0 ? TypeRegistry.Object : Bases[0];
            case "__dict__":
                // A copy, as CPython's mappingproxy is read-only: the namespace changes
                // only through attribute assignment, which keeps the lookup caches honest.
                return Members.Copy();
            case "__class__":
                return TypeRegistry.Type;
        }

        if (Behaviour?.GetClassAttribute(this, name) is { } special)
        {
            return special;
        }

        if (Lookup(name) is { } value)
        {
            return Machine is { } machine ? ClassModel.BindToClass(machine, value, this) : value;
        }

        if (name == "mro")
        {
            return new PyBuiltinFunction("mro", _ => new PyList([.. FullMro().Items]));
        }

        if (name == "__subclasses__")
        {
            return new PyBuiltinFunction("__subclasses__", _ => new PyList([]));
        }

        if (name == "__module__")
        {
            return new PyStr("__main__");
        }

        if (ExceptionType is { } exception && exception.GetAttribute(name) is { } inherited)
        {
            return inherited;
        }

        if (BuiltinBase?.GetAttribute(name) is { } builtinAttribute)
        {
            return builtinAttribute;
        }

        // A class with no docstring still has `__doc__`, holding None.
        return name == "__doc__" ? PyNone.Instance : TypeRegistry.Object.GetAttribute(name);
    }

    /// <inheritdoc />
    public override bool SetAttribute(string name, PyObject value)
    {
        if (Behaviour?.SetClassAttribute(this, name, value) == true)
        {
            return true;
        }

        Members.Set(new PyStr(name), value);
        NamespaceChanged();
        return true;
    }

    /// <inheritdoc />
    public override bool DeleteAttribute(string name)
    {
        var key = new PyStr(name);

        if (!Members.TryGetValue(key, out _))
        {
            throw new PyRaise(new PyException(
                PyExceptionType.AttributeError, $"type object '{Name}' has no attribute '{name}'"));
        }

        Members.Remove(key);
        NamespaceChanged();
        return true;
    }

    /// <inheritdoc />
    public override IEnumerable<PyObject>? Iterate() => Behaviour?.Iterate(this);

    /// <inheritdoc />
    public override int? Length() => Behaviour?.Length(this);

    /// <inheritdoc />
    public override bool Contains(PyObject item) =>
        Behaviour?.Contains(this, item)
        ?? throw new PyRaise(PyErrors.TypeError($"argument of type 'type' is not iterable"));

    /// <inheritdoc />
    /// <remarks>
    /// <c>Box[int]</c> is how a generic class is parameterized in an annotation; with no
    /// <c>__class_getitem__</c> it means the class itself, the way a subscripted built-in
    /// type does here.
    /// </remarks>
    public override PyObject GetItem(PyObject index)
    {
        if (Behaviour?.GetItem(this, index) is { } item)
        {
            return item;
        }

        if (Lookup("__class_getitem__") is { } getter && Machine is { } machine)
        {
            return machine.Call(ClassModel.BindToClass(machine, getter is PyFunction f ? new PyClassMethod(f) : getter, this), [index]);
        }

        return this;
    }

    /// <summary>C3 linearization of <paramref name="type"/>'s bases.</summary>
    private static List<PyClass> Linearize(PyClass type, List<PyClass> bases)
    {
        var sequences = bases.Select(static b => new List<PyClass>(b.Mro)).ToList();
        sequences.Add([.. bases]);

        var result = new List<PyClass> { type };

        while (true)
        {
            sequences.RemoveAll(static s => s.Count == 0);

            if (sequences.Count == 0)
            {
                return result;
            }

            PyClass? head = null;

            foreach (var sequence in sequences)
            {
                var candidate = sequence[0];

                if (!sequences.Any(other => other.IndexOf(candidate) > 0))
                {
                    head = candidate;
                    break;
                }
            }

            if (head is null)
            {
                throw new PyRaise(PyErrors.TypeError(
                    "Cannot create a consistent method resolution order (MRO) for bases "
                    + string.Join(", ", bases.Select(static b => b.Name))));
            }

            result.Add(head);

            foreach (var sequence in sequences)
            {
                if (ReferenceEquals(sequence[0], head))
                {
                    sequence.RemoveAt(0);
                }
            }
        }
    }
}

/// <summary>
/// What a metaclass would provide: class-level protocols for classes a C# module defines.
/// </summary>
/// <remarks>
/// Every method may decline by returning null, in which case the class behaves as any
/// other. There is no way to write one of these in Python — a metaclass in user code is
/// still refused — so the set of classes with unusual class-level behaviour stays a list
/// somebody wrote.
/// </remarks>
public abstract class ClassBehaviour
{
    /// <summary>Called once the class object exists, before decorators run.</summary>
    public virtual void Created(VirtualMachine machine, PyClass type, PyDict keywords)
    {
    }

    /// <summary>Calling the class, or null for ordinary instantiation.</summary>
    public virtual PyObject? Call(VirtualMachine machine, PyClass type, PyObject[] arguments, PyDict? keywords) => null;

    /// <summary>A class-level attribute, or null to look it up normally.</summary>
    public virtual PyObject? GetClassAttribute(PyClass type, string name) => null;

    /// <summary>Intercepts setting a class attribute; true when handled.</summary>
    public virtual bool SetClassAttribute(PyClass type, string name, PyObject value) => false;

    /// <summary>Iterating the class.</summary>
    public virtual IEnumerable<PyObject>? Iterate(PyClass type) => null;

    /// <summary><c>len()</c> of the class.</summary>
    public virtual int? Length(PyClass type) => null;

    /// <summary><c>x in C</c>.</summary>
    public virtual bool? Contains(PyClass type, PyObject item) => null;

    /// <summary><c>C[key]</c>.</summary>
    public virtual PyObject? GetItem(PyClass type, PyObject index) => null;

    /// <summary>The class's repr.</summary>
    public virtual string? ClassRepr(PyClass type) => null;
}

/// <summary>
/// An instance of a Python class.
/// </summary>
/// <remarks>
/// Every protocol the interpreter uses — truthiness, equality, hashing, iteration,
/// indexing, comparison — is answered by looking for the matching dunder along the class's
/// MRO and calling it. That is what makes a user class behave like a built-in one, and it
/// is why an instance holds a reference to the machine that will run those methods.
/// </remarks>
public sealed class PyInstance : PyObject
{
    private readonly VirtualMachine? _machine;

    /// <summary>Creates an instance of <paramref name="type"/>.</summary>
    public PyInstance(PyClass type, VirtualMachine? machine = null)
    {
        Class = type;
        _machine = machine ?? type.Machine;
    }

    /// <summary>The class this is an instance of.</summary>
    public PyClass Class { get; }

    /// <summary>The instance's own attributes.</summary>
    public PyDict Fields { get; } = new();

    /// <summary>
    /// The built-in value this instance extends, when its class derives from a built-in
    /// type: the dict a <c>class D(dict)</c> instance stores its items in.
    /// </summary>
    public PyObject? BuiltinValue { get; internal set; }

    /// <summary>The machine this instance's methods run on.</summary>
    internal VirtualMachine? Machine => _machine;

    /// <inheritdoc />
    public override string TypeName => Class.Name;

    /// <inheritdoc />
    public override string Repr() =>
        Dunder("__repr__") is { } repr
            ? Text(Invoke(repr, []), "__repr__")
            : DefaultRepr();

    /// <summary>
    /// The default repr naming the class and the identity, as CPython's does; the
    /// "address" is the same identity <c>id()</c> reports.
    /// </summary>
    internal string DefaultRepr() =>
        $"<{Class.Name} object at 0x{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this):x8}>";

    /// <inheritdoc />
    public override string Display() =>
        Dunder("__str__") is { } str ? Text(Invoke(str, []), "__str__") : Repr();

    /// <summary>Takes the string a <c>__repr__</c> or <c>__str__</c> returned, which must be one.</summary>
    private static string Text(PyObject returned, string dunder) =>
        returned switch
        {
            PyStr text => text.Value,
            PyInstance { BuiltinValue: PyStr inner } => inner.Value,
            _ => throw new PyRaise(PyErrors.TypeError(
                $"{dunder} returned non-string (type {returned.TypeName})")),
        };

    /// <inheritdoc />
    public override string? PyFormat(string spec)
    {
        if (Class.Lookup("__format__") is not null && Dunder("__format__") is { } format)
        {
            return Text(Invoke(format, [new PyStr(spec)]), "__format__");
        }

        if (BuiltinValue is { } inner)
        {
            return StringFormatter.Format(inner, spec);
        }

        return spec.Length == 0 ? Display() : null;
    }

    /// <inheritdoc />
    public override bool IsTruthy()
    {
        if (Dunder("__bool__") is { } boolean)
        {
            return Invoke(boolean, []).IsTruthy();
        }

        // Without `__bool__`, a container is falsy when empty; anything else is true.
        return Dunder("__len__") is not { } length || Invoke(length, []).IsTruthy();
    }

    /// <inheritdoc />
    /// <remarks>
    /// A <c>__eq__</c> that returns <c>NotImplemented</c> is declining to answer, not
    /// answering "true": the comparison then falls back to identity, which is what makes
    /// the usual <c>if not isinstance(other, C): return NotImplemented</c> guard work.
    /// </remarks>
    public override bool PyEquals(PyObject other)
    {
        if (Dunder("__eq__") is not { } equals)
        {
            return ReferenceEquals(this, other);
        }

        var result = Invoke(equals, [other]);

        return result is Builtins.NotImplementedSingleton ? ReferenceEquals(this, other) : result.IsTruthy();
    }

    /// <inheritdoc />
    /// <remarks>
    /// The rule is CPython's: the nearest class along the MRO that defines either
    /// <c>__eq__</c> or <c>__hash__</c> decides. One that defines <c>__eq__</c> without
    /// <c>__hash__</c> opts out — equal objects would otherwise hash apart — and
    /// <c>__hash__ = None</c> opts out explicitly.
    /// </remarks>
    public override BigInteger PyHash()
    {
        foreach (var type in Class.MroList)
        {
            if (type.Members.TryGetValue("__hash__", out var hash))
            {
                if (hash is PyNone)
                {
                    break;
                }

                return Dunder("__hash__") is { } method ? HashValue(Invoke(method, [])) : Identity();
            }

            if (type.Members.TryGetValue("__eq__", out _))
            {
                throw new PyRaise(PyErrors.TypeError($"unhashable type: '{Class.Name}'"));
            }
        }

        if (Class.Lookup("__hash__") is PyNone)
        {
            throw new PyRaise(PyErrors.TypeError($"unhashable type: '{Class.Name}'"));
        }

        return BuiltinValue is { } inner ? inner.PyHash() : Identity();

        BigInteger Identity() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
    }

    private static BigInteger HashValue(PyObject value) => value switch
    {
        PyInt integer => integer.Value,
        PyInstance { BuiltinValue: PyInt inner } => inner.Value,
        _ => throw new PyRaise(PyErrors.TypeError("__hash__ method should return an integer")),
    };

    /// <inheritdoc />
    public override int? PyCompare(PyObject other)
    {
        if (Dunder("__lt__") is { } less)
        {
            var answer = Invoke(less, [other]);

            if (answer is Builtins.NotImplementedSingleton)
            {
                return null;
            }

            if (answer.IsTruthy())
            {
                return -1;
            }

            return PyEquals(other) ? 0 : 1;
        }

        return null;
    }

    /// <inheritdoc />
    public override int? Length() =>
        Dunder("__len__") is { } length
            ? Invoke(length, []) switch
            {
                PyInt count when count.Value < 0 => throw new PyRaise(PyErrors.ValueError("__len__() should return >= 0")),
                PyInt count => count.ToIndex(),
                var other => throw new PyRaise(PyErrors.TypeError(
                    $"'{other.TypeName}' object cannot be interpreted as an integer")),
            }
            : null;

    /// <inheritdoc />
    /// <remarks>
    /// An <c>__iter__</c> of None opts out of iteration entirely: it is never called, and
    /// there is no fallback to <c>__getitem__</c>, so the object is simply not iterable.
    /// </remarks>
    public override IEnumerable<PyObject>? Iterate()
    {
        if (Dunder("__iter__") is { } iterator)
        {
            return iterator is PyNone ? null : Drain(Invoke(iterator, []));
        }

        // The legacy protocol: `__getitem__` from 0 until IndexError.
        return Dunder("__getitem__") is null ? null : IterateByIndex();
    }

    /// <inheritdoc />
    /// <remarks>
    /// A <c>__contains__</c> of None opts out of <c>in</c> entirely: it is never called,
    /// and there is no fallback to iteration, so a class that also defines
    /// <c>__iter__</c> still refuses.
    /// </remarks>
    public override bool Contains(PyObject item)
    {
        var contains = Dunder("__contains__");

        if (contains is PyNone)
        {
            throw new PyRaise(PyErrors.TypeError($"'{Class.Name}' object is not a container"));
        }

        if (contains is not null)
        {
            return Invoke(contains, [item]).IsTruthy();
        }

        return Iterate() is null
            ? throw new PyRaise(PyErrors.TypeError(
                $"argument of type '{Class.Name}' is not a container or iterable"))
            : base.Contains(item);
    }

    /// <inheritdoc />
    public override PyObject GetItem(PyObject index)
    {
        if (Dunder("__getitem__") is { } getter)
        {
            try
            {
                return Invoke(getter, [index]);
            }
            catch (PyRaise raise) when (BuiltinValue is PyDict
                && raise.Exception.IsInstanceOf(PyExceptionType.KeyError)
                && Class.Lookup("__getitem__") is null
                && Dunder("__missing__") is { } missing)
            {
                // A dict subclass's `__missing__` is what `__getitem__` falls back to.
                return Invoke(missing, [index]);
            }
        }

        throw new PyRaise(PyErrors.TypeError($"'{Class.Name}' object is not subscriptable"));
    }

    /// <inheritdoc />
    public override void SetItem(PyObject index, PyObject value)
    {
        if (Dunder("__setitem__") is not { } setter)
        {
            throw new PyRaise(PyErrors.TypeError(
                $"'{Class.Name}' object does not support item assignment"));
        }

        Invoke(setter, [index, value]);
    }

    /// <inheritdoc />
    public override void DeleteItem(PyObject index)
    {
        if (Dunder("__delitem__") is not { } deleter)
        {
            throw new PyRaise(PyErrors.TypeError($"'{Class.Name}' object doesn't support item deletion"));
        }

        Invoke(deleter, [index]);
    }

    /// <summary>
    /// Looks up a dunder method along the MRO, bound to this instance — or, for a class
    /// extending a built-in, the built-in's own when the class does not override it.
    /// </summary>
    /// <remarks>
    /// Special methods are looked up on the class, never on the instance and never through
    /// <c>__getattr__</c>, as CPython does.
    /// </remarks>
    public PyObject? Dunder(string name)
    {
        if (_machine is null)
        {
            return null;
        }

        var attribute = Class.Lookup(name);

        if (attribute is null)
        {
            return BuiltinValue is { } inner ? ClassModel.BuiltinDunder(inner, name) : null;
        }

        return attribute switch
        {
            PyFunction function => function.Bind(this),
            PyClassMethod classMethod => ClassModel.BindCallable(classMethod.Function, Class),
            PyStaticMethod staticMethod => staticMethod.Function,
            _ => attribute,
        };
    }

    /// <summary>Calls a bound dunder.</summary>
    public PyObject Invoke(PyObject callable, PyObject[] arguments) =>
        _machine is null
            ? throw new PyRaise(PyErrors.RuntimeError("no interpreter available"))
            // A builtin `__init__` installed by a decorator is unbound, so it needs the
            // receiver passed explicitly.
            : _machine.Call(callable, callable is PyBuiltinFunction ? [this, .. arguments] : arguments);

    private IEnumerable<PyObject> Drain(PyObject iterator)
    {
        if (iterator is PyIterator sequence)
        {
            while (sequence.Next() is { } value)
            {
                yield return value;
            }

            yield break;
        }

        if (iterator is PyGenerator generator)
        {
            while (generator.Next() is { } value)
            {
                yield return value;
            }

            yield break;
        }

        if (iterator is not PyInstance instance || instance.Dunder("__next__") is not { } next)
        {
            if (iterator.Iterate() is { } items && iterator is not PyInstance)
            {
                foreach (var item in items)
                {
                    yield return item;
                }

                yield break;
            }

            throw new PyRaise(PyErrors.TypeError(
                $"iter() returned non-iterator of type '{iterator.TypeName}'"));
        }

        while (true)
        {
            PyObject value;

            try
            {
                value = instance.Invoke(next, []);
            }
            catch (PyRaise raise) when (raise.Exception.IsInstanceOf(PyExceptionType.StopIteration))
            {
                yield break;
            }

            yield return value;
        }
    }

    private IEnumerable<PyObject> IterateByIndex()
    {
        for (var i = 0; ; i++)
        {
            PyObject value;

            try
            {
                value = GetItem(PyInt.From(i));
            }
            catch (PyRaise raise) when (raise.Exception.IsInstanceOf(PyExceptionType.IndexError))
            {
                yield break;
            }

            yield return value;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The lookup order is CPython's <c>object.__getattribute__</c>: a data descriptor on
    /// the class (a property), then the instance's own fields, then anything else on the
    /// class — bound if it is a method. <c>__getattr__</c> is not consulted here but by
    /// <see cref="Attributes"/>, because it is a fallback for an attribute <i>access</i>,
    /// not for the interpreter's own probing of special methods.
    /// </remarks>
    public override PyObject? GetAttribute(string name)
    {
        // The common case: no override and no data descriptor, so an instance field, if
        // there is one, is the answer.
        if (Class.HasPlainAttributes)
        {
            return Fields.TryGetValue(name, out var own) ? own : RawGetAttribute(name);
        }

        if (Class.Lookup("__getattribute__") is PyFunction custom && _machine is { } machine)
        {
            try
            {
                return machine.Call(custom.Bind(this), [new PyStr(name)]);
            }
            catch (PyRaise raise) when (raise.Exception.IsInstanceOf(PyExceptionType.AttributeError))
            {
                return null;
            }
        }

        return RawGetAttribute(name);
    }

    /// <summary><c>object.__getattribute__</c>: the lookup without any user override.</summary>
    internal PyObject? RawGetAttribute(string name)
    {
        switch (name)
        {
            case "__class__":
                return Class;
            case "__dict__":
                return Fields;
        }

        var classAttribute = Class.Lookup(name);

        if (classAttribute is not null && ClassModel.IsDataDescriptor(classAttribute) && _machine is { } machine)
        {
            return ClassModel.BindToInstance(machine, classAttribute, this, Class);
        }

        if (Fields.TryGetValue(name, out var field))
        {
            return field;
        }

        if (classAttribute is not null)
        {
            return _machine is { } running
                ? ClassModel.BindToInstance(running, classAttribute, this, Class)
                : classAttribute is PyFunction method ? method.Bind(this) : classAttribute;
        }

        if (BuiltinValue is { } inner && _machine is { } host
            && (ClassModel.BuiltinDunder(inner, name) ?? Attributes.TryGet(host, inner, name)) is { } inherited)
        {
            return inherited;
        }

        // An instance sees its class's docstring and module, which every class has.
        return name switch
        {
            "__doc__" => PyNone.Instance,
            "__module__" => new PyStr("__main__"),
            _ => null,
        };
    }

    /// <summary>Calls <c>__getattr__</c> for an attribute the normal lookup did not find.</summary>
    internal PyObject? FallbackAttribute(string name)
    {
        if (_machine is null || Class.Lookup("__getattr__") is not { } getter)
        {
            return null;
        }

        return _machine.Call(ClassModel.BindToInstance(_machine, getter, this, Class), [new PyStr(name)]);
    }

    /// <inheritdoc />
    public override bool SetAttribute(string name, PyObject value)
    {
        if (Class.Lookup("__setattr__") is { } custom && _machine is { } machine)
        {
            machine.Call(ClassModel.BindToInstance(machine, custom, this, Class), [new PyStr(name), value]);
            return true;
        }

        return RawSetAttribute(name, value);
    }

    /// <summary>
    /// Stores a field directly when nothing could intercept the assignment — no
    /// <c>__setattr__</c> and no descriptor of that name on the class — keyed by
    /// <paramref name="key"/>; returns false to take the general path.
    /// </summary>
    internal bool TrySetPlainField(string name, PyStr key, PyObject value)
    {
        if (Class.Lookup("__setattr__") is not null
            || (Class.Lookup(name) is { } member && (member is PyProperty || member is PyInstance))
            || name == "__class__")
        {
            return false;
        }

        Fields.Set(key, value);
        return true;
    }

    /// <summary><c>object.__setattr__</c>: assignment without any user override.</summary>
    internal bool RawSetAttribute(string name, PyObject value)
    {
        if (Class.Lookup(name) is { } classAttribute && _machine is { } machine)
        {
            switch (classAttribute)
            {
                case PyProperty property:
                    if (property.Setter is null)
                    {
                        throw new PyRaise(new PyException(
                            PyExceptionType.AttributeError,
                            $"property '{property.Name ?? name}' of '{Class.Name}' object has no setter"));
                    }

                    machine.Call(property.Setter, [this, value]);
                    return true;

                case PyInstance descriptor when descriptor.Dunder("__set__") is { } setter:
                    descriptor.Invoke(setter, [this, value]);
                    return true;
            }
        }

        if (name == "__class__")
        {
            throw new PyRaise(PyErrors.TypeError("__class__ assignment is not supported"));
        }

        Fields.Set(name, value);
        return true;
    }

    /// <inheritdoc />
    public override bool DeleteAttribute(string name)
    {
        if (Class.Lookup("__delattr__") is { } custom && _machine is { } machine)
        {
            machine.Call(ClassModel.BindToInstance(machine, custom, this, Class), [new PyStr(name)]);
            return true;
        }

        return RawDeleteAttribute(name);
    }

    /// <summary><c>object.__delattr__</c>.</summary>
    internal bool RawDeleteAttribute(string name)
    {
        if (Class.Lookup(name) is { } classAttribute && _machine is { } machine)
        {
            switch (classAttribute)
            {
                case PyProperty property:
                    if (property.Deleter is null)
                    {
                        throw new PyRaise(new PyException(
                            PyExceptionType.AttributeError,
                            $"property '{property.Name ?? name}' of '{Class.Name}' object has no deleter"));
                    }

                    machine.Call(property.Deleter, [this]);
                    return true;

                case PyInstance descriptor when descriptor.Dunder("__delete__") is { } deleter:
                    descriptor.Invoke(deleter, [this]);
                    return true;
            }
        }

        var key = new PyStr(name);

        if (!Fields.TryGetValue(key, out _))
        {
            throw new PyRaise(PyErrors.AttributeError(Class.Name, name));
        }

        Fields.Remove(key);
        return true;
    }
}
