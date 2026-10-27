using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules;

/// <summary>The <c>copy</c> module.</summary>
/// <remarks>
/// <para>
/// <c>deepcopy</c> keeps a memo of what it has already copied, keyed by identity, so a
/// structure that contains itself copies to a structure that contains its copy rather than
/// recursing without end; and it is bounded by the run's recursion limit, so a structure
/// nested a million deep is an error the program can see rather than a host stack overflow.
/// </para>
/// <para>
/// An instance of a class the program defined is copied by its attributes, unless the class
/// has a <c>__copy__</c> or <c>__deepcopy__</c> of its own, which is called as CPython calls
/// it. Immutable values — numbers, strings, bytes, <c>None</c>, functions, classes and types
/// — are returned as they are.
/// </para>
/// </remarks>
public static class CopyModule
{
    private static readonly PyExceptionType Error = PyExceptionType.DefineHostException("Error");

    /// <summary>Builds the module.</summary>
    public static PyModuleObject Create(VirtualMachine machine)
    {
        var module = new PyModuleObject("copy");

        module.Add("Error", Error);
        module.Add("error", Error);

        module.Add("copy", arguments =>
        {
            Arity.Exact("copy", arguments, 1);
            return Shallow(machine, arguments[0]);
        });

        module.Add("deepcopy", arguments =>
        {
            Arity.Between("deepcopy", arguments, 1, 2);
            var memo = new Dictionary<PyObject, PyObject>(ReferenceEqualityComparer.Instance);
            return Deep(machine, arguments[0], memo, 0);
        });

        module.Add("replace", (arguments, keywords) =>
        {
            Arity.Exact("replace", arguments, 1);

            if (Attributes.TryGet(machine, arguments[0], "__replace__") is not { } replace)
            {
                throw new PyRaise(PyErrors.TypeError($"replace() does not support {arguments[0].TypeName} objects"));
            }

            return machine.Call(replace, [], keywords);
        });

        return module;
    }

    private static bool Immutable(PyObject value) =>
        value is PyNone or PyInt or PyFloat or PyStr or PyBytes or PyEllipsis or PyCallable or PyExceptionType
            or PyRange or PyModuleObject
        || (value is PySet { IsFrozen: true })
        || (value is PyTuple && value is not PyNamedTuple);

    private static PyObject Shallow(VirtualMachine machine, PyObject value)
    {
        if (Immutable(value) && value is not PyTuple)
        {
            return value;
        }

        switch (value)
        {
            case PyInstance instance:
                if (instance.Dunder("__copy__") is { } custom)
                {
                    return instance.Invoke(custom, []);
                }

                var clone = new PyInstance(instance.Class, machine);

                foreach (var (key, field) in instance.Fields.Entries)
                {
                    clone.Fields.Set(key, field);
                }

                return clone;

            case PyNamedTuple named:
                return new PyNamedTuple(named.Type, named.Items);

            case PyTuple:
                return value;

            case PyList list:
                return new PyList([.. list.Items]);

            case PyCounter counter:
            {
                var copy = new PyCounter();

                foreach (var (key, count) in counter.Entries)
                {
                    copy.Set(key, count);
                }

                return copy;
            }

            case PyDict dict:
                return dict.Copy();

            case PySet set:
                return new PySet(set.Items);

            case PyDeque deque:
                return new PyDeque(deque.Items, deque.MaxLength);

            default:
                return value;
        }
    }

    private static PyObject Deep(VirtualMachine machine, PyObject value, Dictionary<PyObject, PyObject> memo, int depth)
    {
        if (depth > machine.RecursionLimit)
        {
            throw new PyRaise(new PyException(
                PyExceptionType.RecursionError, "maximum recursion depth exceeded while copying an object"));
        }

        if (Immutable(value) && value is not PyTuple)
        {
            return value;
        }

        if (memo.TryGetValue(value, out var done))
        {
            return done;
        }

        switch (value)
        {
            case PyList list:
            {
                var copy = new PyList();
                memo[value] = copy;

                foreach (var item in list.Items.ToList())
                {
                    copy.Items.Add(Deep(machine, item, memo, depth + 1));
                }

                return copy;
            }

            case PyNamedTuple named:
            {
                var copy = new PyNamedTuple(named.Type, [.. named.Items.Select(item => Deep(machine, item, memo, depth + 1))]);
                memo[value] = copy;
                return copy;
            }

            case PyTuple tuple:
            {
                var items = tuple.Items.Select(item => Deep(machine, item, memo, depth + 1)).ToList();

                // A tuple of immutable things is itself returned, as CPython returns it.
                var copy = items.Zip(tuple.Items).All(pair => ReferenceEquals(pair.First, pair.Second)) ? tuple : new PyTuple(items);
                memo[value] = copy;
                return copy;
            }

            case PyDict dict:
            {
                var copy = dict is PyCounter ? new PyCounter() : dict.Copy();
                memo[value] = copy;

                foreach (var (key, item) in dict.Entries.ToList())
                {
                    copy.Set(Deep(machine, key, memo, depth + 1), Deep(machine, item, memo, depth + 1));
                }

                // `Copy` kept the old values; the loop above rewrote every one that was there.
                return copy;
            }

            case PySet set:
            {
                var copy = new PySet(set.Items.Select(item => Deep(machine, item, memo, depth + 1)).ToList());
                memo[value] = copy;
                return copy;
            }

            case PyDeque deque:
            {
                var copy = new PyDeque(deque.Items.Select(item => Deep(machine, item, memo, depth + 1)).ToList(), deque.MaxLength);
                memo[value] = copy;
                return copy;
            }

            case PyInstance instance:
            {
                if (instance.Dunder("__deepcopy__") is { } custom)
                {
                    var made = instance.Invoke(custom, [new PyDict()]);
                    memo[value] = made;
                    return made;
                }

                var clone = new PyInstance(instance.Class, machine);
                memo[value] = clone;

                foreach (var (key, field) in instance.Fields.Entries.ToList())
                {
                    clone.Fields.Set(key, Deep(machine, field, memo, depth + 1));
                }

                return clone;
            }

            default:
                return value;
        }
    }
}
