using System.Numerics;
using System.Text;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules;

/// <summary>
/// The later additions to <c>collections</c> and <c>itertools</c>: <c>ChainMap</c>, and
/// <c>groupby</c>, <c>starmap</c>, <c>tee</c>, <c>batched</c> and
/// <c>combinations_with_replacement</c>.
/// </summary>
/// <remarks>
/// The <c>itertools</c> additions are lazy, as CPython's are: each pulls one item from its
/// source per item it produces, so they compose with an infinite <c>count()</c>.
/// </remarks>
internal static class CollectionsExtras
{
    /// <summary>Adds <c>ChainMap</c> to <c>collections</c>.</summary>
    public static void AddCollections(PyModuleObject module, VirtualMachine machine)
    {
        PyType? chainType = null;
        chainType = new PyType("ChainMap", static value => value is PyChainMap, (arguments, keywords) =>
        {
            PyBuiltinFunction.RejectKeywords("ChainMap", keywords);
            return new PyChainMap(machine, chainType!, arguments.Length == 0 ? [new PyDict()] : [.. arguments]);
        });

        module.Add("ChainMap", chainType);
    }

    /// <summary>Adds the newer adaptors to <c>itertools</c>.</summary>
    public static void AddItertools(PyModuleObject module, VirtualMachine machine)
    {
        module.Add("groupby", new PyBuiltinFunction("groupby", (arguments, keywords) =>
        {
            var source = arguments.Length > 0 ? arguments[0]
                : Keyword(keywords, "iterable") ?? throw new PyRaise(PyErrors.TypeError(
                    "groupby() missing required argument 'iterable' (pos 1)"));
            var keyFunction = arguments.Length > 1 ? arguments[1] : Keyword(keywords, "key");

            if (keyFunction is PyNone)
            {
                keyFunction = null;
            }

            var pull = VirtualMachine.Puller(source);
            PyObject? pendingValue = null;
            PyObject? pendingKey = null;
            var sourceEnded = false;

            PyObject KeyOf(PyObject value) => keyFunction is null ? value : machine.Call(keyFunction, [value]);

            // Each group is gathered when groupby hands it out (CPython reads it lazily from
            // the shared source), so a group saved and read after the outer iterator moved
            // on still holds its items, where CPython's would be empty.
            return new PyIterator(() =>
            {
                if (pendingValue is null)
                {
                    if (sourceEnded || pull() is not { } first)
                    {
                        sourceEnded = true;
                        return null;
                    }

                    pendingValue = first;
                    pendingKey = KeyOf(first);
                }

                var group = new List<PyObject> { pendingValue };
                var key = pendingKey!;
                pendingValue = null;

                while (true)
                {
                    if (pull() is not { } next)
                    {
                        sourceEnded = true;
                        break;
                    }

                    var nextKey = KeyOf(next);

                    if (!PyObject.SameOrEqual(nextKey, key))
                    {
                        pendingValue = next;
                        pendingKey = nextKey;
                        break;
                    }

                    group.Add(next);
                }

                return new PyTuple([key, new PyIterator(group, "itertools._grouper")]);
            }, "itertools.groupby");
        }));

        module.Add("starmap", new PyBuiltinFunction("starmap", (arguments, keywords) =>
        {
            PyBuiltinFunction.RejectKeywords("starmap", keywords);
            Arity.ExactCount("starmap", arguments, 2);

            var function = arguments[0];
            var pull = VirtualMachine.Puller(arguments[1]);

            return new PyIterator(() => pull() is { } item
                ? machine.Call(function, [.. VirtualMachine.RequireIterable(item)])
                : null, "itertools.starmap");
        }));

        module.Add("batched", new PyBuiltinFunction("batched", (arguments, keywords) =>
        {
            var source = arguments.Length > 0 ? arguments[0]
                : Keyword(keywords, "iterable") ?? throw new PyRaise(PyErrors.TypeError(
                    "batched() missing required argument 'iterable' (pos 1)"));
            var sizeArgument = arguments.Length > 1 ? arguments[1]
                : Keyword(keywords, "n") ?? throw new PyRaise(PyErrors.TypeError(
                    "batched() missing required argument 'n' (pos 2)"));
            var strict = Keyword(keywords, "strict")?.IsTruthy() ?? false;
            var size = Builtins.BuiltinNamespace.RequireInt(sizeArgument, "batched");

            if (size < 1)
            {
                throw new PyRaise(PyErrors.ValueError("n must be at least one"));
            }

            var count = (int)BigInteger.Min(size, int.MaxValue);
            var pull = VirtualMachine.Puller(source);

            return new PyIterator(() =>
            {
                var batch = new List<PyObject>();

                while (batch.Count < count && pull() is { } item)
                {
                    batch.Add(item);
                }

                if (batch.Count == 0)
                {
                    return null;
                }

                if (strict && batch.Count < count)
                {
                    throw new PyRaise(PyErrors.ValueError("batched(): incomplete batch"));
                }

                return new PyTuple(batch);
            }, "itertools.batched");
        }));

        module.Add("combinations_with_replacement", new PyBuiltinFunction("combinations_with_replacement", (arguments, keywords) =>
        {
            PyBuiltinFunction.RejectKeywords("combinations_with_replacement", keywords);
            Arity.ExactCount("combinations_with_replacement", arguments, 2);

            var pool = VirtualMachine.RequireIterable(arguments[0]).ToList();
            var length = Builtins.BuiltinNamespace.RequireInt(arguments[1], "combinations_with_replacement");

            if (length < 0)
            {
                throw new PyRaise(PyErrors.ValueError("r must be non-negative"));
            }

            var r = (int)BigInteger.Min(length, int.MaxValue);
            var n = pool.Count;

            if (n == 0 && r > 0)
            {
                return new PyIterator([], "itertools.combinations_with_replacement");
            }

            int[]? indices = null;

            return new PyIterator(() =>
            {
                if (indices is null)
                {
                    indices = new int[r];
                    return new PyTuple([.. indices.Select(i => pool[i])]);
                }

                var position = r - 1;

                while (position >= 0 && indices[position] == n - 1)
                {
                    position--;
                }

                if (position < 0)
                {
                    return null;
                }

                var value = indices[position] + 1;

                for (var i = position; i < r; i++)
                {
                    indices[i] = value;
                }

                return new PyTuple([.. indices.Select(i => pool[i])]);
            }, "itertools.combinations_with_replacement");
        }));

        module.Add("tee", new PyBuiltinFunction("tee", (arguments, keywords) =>
        {
            PyBuiltinFunction.RejectKeywords("tee", keywords);
            Arity.Between("tee", arguments, 1, 2);

            var count = arguments.Length > 1 ? Builtins.BuiltinNamespace.RequireInt(arguments[1], "tee") : 2;

            if (count < 0)
            {
                throw new PyRaise(PyErrors.ValueError("n must be >= 0"));
            }

            if (count > 1_000_000)
            {
                throw new PyRaise(new PyException(PyExceptionType.MemoryError, string.Empty));
            }

            // One buffer shared by every copy: an item is pulled from the source once, and
            // kept until every copy has read past it.
            var pull = VirtualMachine.Puller(arguments[0]);
            var buffer = new List<PyObject>();
            var offset = 0;
            var positions = new long[(int)count];
            var ended = false;

            PyIterator Copy(int index) => new(() =>
            {
                var position = positions[index];

                if (position - offset >= buffer.Count)
                {
                    if (ended || pull() is not { } next)
                    {
                        ended = true;
                        return null;
                    }

                    buffer.Add(next);
                }

                var item = buffer[(int)(position - offset)];
                positions[index] = position + 1;

                // Drop what every copy has consumed.
                var slowest = positions.Min();

                if (slowest - offset > 64)
                {
                    var drop = (int)(slowest - offset);
                    buffer.RemoveRange(0, drop);
                    offset += drop;
                }

                return item;
            }, "itertools._tee");

            return new PyTuple([.. Enumerable.Range(0, (int)count).Select(i => (PyObject)Copy(i))]);
        }));
    }

    private static PyObject? Keyword(PyDict? keywords, string name) =>
        keywords is not null && keywords.TryGetValue(new PyStr(name), out var value) ? value : null;
}

/// <summary>A <c>collections.ChainMap</c>: several mappings searched as one.</summary>
public sealed class PyChainMap : PyObject, IPyHostTyped, IPyMapping
{
    /// <inheritdoc />
    public PyDict Snapshot() => Merged();

    private readonly VirtualMachine _machine;
    private readonly PyType _type;

    internal PyChainMap(VirtualMachine machine, PyType type, List<PyObject> maps)
    {
        _machine = machine;
        _type = type;
        Maps = new PyList(maps);
    }

    /// <summary>The mappings, searched first to last; writes go to the first.</summary>
    public PyList Maps { get; }

    /// <inheritdoc />
    PyObject IPyHostTyped.Type => _type;

    /// <inheritdoc />
    public override string TypeName => "ChainMap";

    /// <inheritdoc />
    public override string Repr()
    {
        if (!PyReprGuard.TryEnter(this))
        {
            return "...";
        }

        try
        {
            var builder = new StringBuilder("ChainMap(");
            builder.AppendJoin(", ", Maps.Items.Select(static map => map.Repr()));
            return builder.Append(')').ToString();
        }
        finally
        {
            PyReprGuard.Exit(this);
        }
    }

    private static bool TryLookup(PyObject mapping, PyObject key, out PyObject value)
    {
        if (mapping is PyDict dict && dict.GetType() == typeof(PyDict))
        {
            return dict.TryGetValue(key, out value!);
        }

        if (mapping.Contains(key))
        {
            value = mapping.GetItem(key);
            return true;
        }

        value = PyNone.Instance;
        return false;
    }

    /// <inheritdoc />
    public override PyObject GetItem(PyObject index)
    {
        foreach (var map in Maps.Items)
        {
            if (TryLookup(map, index, out var value))
            {
                return value;
            }
        }

        throw new PyRaise(PyErrors.KeyError(index));
    }

    /// <inheritdoc />
    public override void SetItem(PyObject index, PyObject value) => First.SetItem(index, value);

    /// <inheritdoc />
    public override void DeleteItem(PyObject index)
    {
        if (!First.Contains(index))
        {
            throw new PyRaise(PyErrors.KeyError(new PyStr($"Key not found in the first mapping: {index.Repr()}")));
        }

        First.DeleteItem(index);
    }

    private PyObject First => Maps.Items.Count > 0
        ? Maps.Items[0]
        : throw new PyRaise(PyErrors.KeyError(new PyStr("No mappings")));

    /// <inheritdoc />
    public override bool Contains(PyObject item) => Maps.Items.Any(map => map.Contains(item));

    /// <summary>The merged mapping, in CPython's order: the last map's keys first.</summary>
    private PyDict Merged()
    {
        var keys = new PyDict();

        for (var i = Maps.Items.Count - 1; i >= 0; i--)
        {
            foreach (var key in VirtualMachine.RequireIterable(Maps.Items[i]))
            {
                keys.Set(key, PyNone.Instance);
            }
        }

        var merged = new PyDict();

        foreach (var key in keys.Entries.Select(static e => e.Key).ToList())
        {
            merged.Set(key, GetItem(key));
        }

        return merged;
    }

    /// <inheritdoc />
    public override IEnumerable<PyObject>? Iterate() => Merged().Entries.Select(static e => e.Key).ToList();

    /// <inheritdoc />
    public override int? Length() => Merged().Count;

    /// <inheritdoc />
    public override bool IsTruthy() => Maps.Items.Any(static map => map.IsTruthy());

    /// <inheritdoc />
    public override bool PyEquals(PyObject other) => other switch
    {
        PyChainMap chain => Merged().PyEquals(chain.Merged()),
        PyDict dict => Merged().PyEquals(dict),
        _ => false,
    };

    private PyChainMap With(List<PyObject> maps) => new(_machine, _type, maps);

    /// <inheritdoc />
    public override PyObject? GetAttribute(string name)
    {
        switch (name)
        {
            case "maps":
                return Maps;
            case "parents":
                return With(Maps.Items.Count > 1 ? [.. Maps.Items.Skip(1)] : [new PyDict()]);
            case "__class__":
                return _type;
        }

        return name switch
        {
            "get" => new PyBuiltinFunction("get", arguments =>
            {
                Arity.Between("get", arguments, 1, 2);

                foreach (var map in Maps.Items)
                {
                    if (TryLookup(map, arguments[0], out var value))
                    {
                        return value;
                    }
                }

                return arguments.Length > 1 ? arguments[1] : PyNone.Instance;
            }),
            "keys" or "values" or "items" => BuiltinMethods.TryBind(_machine, Merged(), name),
            "new_child" => new PyBuiltinFunction("new_child", (arguments, keywords) =>
            {
                var child = arguments.Length > 0 && arguments[0] is not PyNone ? arguments[0] : new PyDict();

                foreach (var (key, value) in keywords?.Entries ?? [])
                {
                    child.SetItem(key, value);
                }

                return With([child, .. Maps.Items]);
            }),
            "copy" or "__copy__" => new PyBuiltinFunction(name, _ =>
            {
                var first = First is PyDict dict ? dict.Copy() : _machine.Call(
                    BuiltinMethods.TryBind(_machine, First, "copy") ?? First.GetAttribute("copy")
                    ?? throw new PyRaise(PyErrors.TypeError("first mapping cannot be copied")), []);
                return With([first, .. Maps.Items.Skip(1)]);
            }),
            "pop" => new PyBuiltinFunction("pop", arguments =>
            {
                Arity.Between("pop", arguments, 1, 2);

                if (First.Contains(arguments[0]))
                {
                    var value = First.GetItem(arguments[0]);
                    First.DeleteItem(arguments[0]);
                    return value;
                }

                return arguments.Length > 1
                    ? arguments[1]
                    : throw new PyRaise(PyErrors.KeyError(new PyStr($"Key not found in the first mapping: {arguments[0].Repr()}")));
            }),
            "popitem" => new PyBuiltinFunction("popitem", _ =>
            {
                var method = BuiltinMethods.TryBind(_machine, First, "popitem") ?? First.GetAttribute("popitem");

                try
                {
                    return _machine.Call(method ?? throw new PyRaise(PyErrors.TypeError("first mapping has no popitem")), []);
                }
                catch (PyRaise raise) when (raise.Exception.IsInstanceOf(PyExceptionType.KeyError))
                {
                    throw new PyRaise(PyErrors.KeyError(new PyStr("No keys found in the first mapping.")));
                }
            }),
            "clear" => new PyBuiltinFunction("clear", _ =>
            {
                var method = BuiltinMethods.TryBind(_machine, First, "clear") ?? First.GetAttribute("clear");
                return _machine.Call(method ?? throw new PyRaise(PyErrors.TypeError("first mapping has no clear")), []);
            }),
            "update" => new PyBuiltinFunction("update", (arguments, keywords) =>
            {
                var method = BuiltinMethods.TryBind(_machine, First, "update") ?? First.GetAttribute("update");
                return _machine.Call(method ?? throw new PyRaise(PyErrors.TypeError("first mapping has no update")), arguments, keywords);
            }),
            "setdefault" => new PyBuiltinFunction("setdefault", arguments =>
            {
                Arity.Between("setdefault", arguments, 1, 2);

                foreach (var map in Maps.Items)
                {
                    if (TryLookup(map, arguments[0], out var value))
                    {
                        return value;
                    }
                }

                var fallback = arguments.Length > 1 ? arguments[1] : PyNone.Instance;
                First.SetItem(arguments[0], fallback);
                return fallback;
            }),
            _ => null,
        };
    }
}
