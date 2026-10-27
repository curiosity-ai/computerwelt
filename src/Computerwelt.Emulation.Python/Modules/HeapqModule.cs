using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules;

/// <summary>The <c>heapq</c> and <c>bisect</c> modules.</summary>
/// <remarks>
/// <para>
/// The sifting is CPython's, comparison for comparison, because the layout of the list
/// after a push or pop is observable — scripts print heaps — and only the same sequence of
/// swaps reproduces it. Comparisons go through <c>&lt;</c>, so they dispatch to a user
/// class's <c>__lt__</c> as any other comparison does.
/// </para>
/// <para>
/// Written in C# rather than Python because they are loops over a list that a program may
/// run a million times, and the instruction limit counts every step of a loop written in
/// Python.
/// </para>
/// </remarks>
public static class HeapqModule
{
    /// <summary>Builds <c>heapq</c>.</summary>
    public static PyModuleObject CreateHeapq(VirtualMachine machine)
    {
        var module = new PyModuleObject("heapq");

        module.Add("heappush", arguments =>
        {
            Arity.Exact("heappush", arguments, 2);
            var heap = Heap(arguments[0], "heappush");
            heap.Add(arguments[1]);
            SiftDown(heap, 0, heap.Count - 1);
            return PyNone.Instance;
        });

        module.Add("heappop", arguments =>
        {
            Arity.Exact("heappop", arguments, 1);
            var heap = Heap(arguments[0], "heappop");

            if (heap.Count == 0)
            {
                throw new PyRaise(PyErrors.IndexError("index out of range"));
            }

            var last = heap[^1];
            heap.RemoveAt(heap.Count - 1);

            if (heap.Count == 0)
            {
                return last;
            }

            var top = heap[0];
            heap[0] = last;
            SiftUp(heap, 0);
            return top;
        });

        module.Add("heapreplace", arguments =>
        {
            Arity.Exact("heapreplace", arguments, 2);
            var heap = Heap(arguments[0], "heapreplace");

            if (heap.Count == 0)
            {
                throw new PyRaise(PyErrors.IndexError("index out of range"));
            }

            var top = heap[0];
            heap[0] = arguments[1];
            SiftUp(heap, 0);
            return top;
        });

        module.Add("heappushpop", arguments =>
        {
            Arity.Exact("heappushpop", arguments, 2);
            var heap = Heap(arguments[0], "heappushpop");
            var item = arguments[1];

            if (heap.Count > 0 && Less(heap[0], item))
            {
                (item, heap[0]) = (heap[0], item);
                SiftUp(heap, 0);
            }

            return item;
        });

        module.Add("heapify", arguments =>
        {
            Arity.Exact("heapify", arguments, 1);
            var heap = Heap(arguments[0], "heapify");

            for (var i = (heap.Count / 2) - 1; i >= 0; i--)
            {
                SiftUp(heap, i);
            }

            return PyNone.Instance;
        });

        // The n smallest or largest are the head of a stable sort, which is exactly what
        // CPython's decorated heap yields — ties keep their original order.
        module.Add("nsmallest", (arguments, keywords) => Extreme(machine, "nsmallest", arguments, keywords, reverse: false));
        module.Add("nlargest", (arguments, keywords) => Extreme(machine, "nlargest", arguments, keywords, reverse: true));

        module.Add("merge", (arguments, keywords) =>
        {
            var everything = new List<PyObject>();

            foreach (var source in arguments)
            {
                everything.AddRange(source.Iterate() ?? throw new PyRaise(PyErrors.TypeError(
                    $"'{source.TypeName}' object is not iterable")));
            }

            var sortKeywords = new PyDict();

            foreach (var (key, value) in keywords?.Entries ?? [])
            {
                sortKeywords.Set(key, value);
            }

            return new PyIterator(Sorting.Sort(machine, everything, sortKeywords), "generator");
        });

        return module;
    }

    /// <summary>Builds <c>bisect</c>.</summary>
    public static PyModuleObject CreateBisect(VirtualMachine machine)
    {
        var module = new PyModuleObject("bisect");

        PyObject Search(string name, PyObject[] arguments, PyDict? keywords, bool right, bool insert)
        {
            var parameters = insert ? new[] { "a", "x", "lo", "hi" } : new[] { "a", "x", "lo", "hi" };
            var bound = Bind(name, arguments, keywords, parameters);
            var sequence = bound[0] ?? throw Missing(name, "a");
            var x = bound[1] ?? throw Missing(name, "x");
            var key = bound[4] is PyNone ? null : bound[4];
            var low = bound[2] is PyInt l ? l.ToIndex() : 0;

            if (low < 0)
            {
                throw new PyRaise(PyErrors.ValueError("lo must be non-negative"));
            }

            var high = bound[3] is PyInt h ? h.ToIndex() : sequence.Length() ?? throw new PyRaise(
                PyErrors.TypeError($"object of type '{sequence.TypeName}' has no len()"));

            // On insertion the key applies to the new value too, so it is compared like the rest.
            var probe = insert && key is not null ? machine.Call(key, [x]) : x;

            while (low < high)
            {
                var middle = (low + high) / 2;
                var item = sequence.GetItem(PyInt.From(middle));

                if (key is not null)
                {
                    item = machine.Call(key, [item]);
                }

                var goRight = right ? !Less(probe, item) : Less(item, probe);

                if (goRight)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }

            if (!insert)
            {
                return PyInt.From(low);
            }

            if (sequence is PyList list)
            {
                list.Items.Insert(low, x);
            }
            else
            {
                machine.Call(
                    Attributes.TryGet(machine, sequence, "insert") ?? throw new PyRaise(
                        PyErrors.AttributeError(sequence.TypeName, "insert")),
                    [PyInt.From(low), x]);
            }

            return PyNone.Instance;
        }

        foreach (var (name, right, insert) in new[]
        {
            ("bisect_left", false, false), ("bisect_right", true, false), ("bisect", true, false),
            ("insort_left", false, true), ("insort_right", true, true), ("insort", true, true),
        })
        {
            module.Add(name, (arguments, keywords) => Search(name, arguments, keywords, right, insert));
        }

        return module;
    }

    private static PyObject?[] Bind(string name, PyObject[] arguments, PyDict? keywords, string[] positional)
    {
        if (arguments.Length > positional.Length)
        {
            throw new PyRaise(PyErrors.TypeError(
                $"{name}() takes at most {positional.Length} arguments ({arguments.Length} given)"));
        }

        var bound = new PyObject?[positional.Length + 1];
        arguments.CopyTo(bound, 0);

        foreach (var (key, value) in keywords?.Entries ?? [])
        {
            var at = key.Display() == "key" ? positional.Length : Array.IndexOf(positional, key.Display());

            if (at < 0)
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"{name}() got an unexpected keyword argument '{key.Display()}'"));
            }

            bound[at] = value;
        }

        return bound;
    }

    private static PyRaise Missing(string function, string name) =>
        new(PyErrors.TypeError($"{function}() missing required argument '{name}'"));

    private static List<PyObject> Heap(PyObject value, string function) =>
        value is PyList list
            ? list.Items
            : throw new PyRaise(PyErrors.TypeError($"{function}() argument 1 must be list, not {value.TypeName}"));

    private static bool Less(PyObject left, PyObject right) => Operators.Compare("<", left, right).IsTruthy();

    private static void SiftDown(List<PyObject> heap, int start, int position)
    {
        var item = heap[position];

        while (position > start)
        {
            var parentPosition = (position - 1) >> 1;
            var parent = heap[parentPosition];

            if (Less(item, parent))
            {
                heap[position] = parent;
                position = parentPosition;
                continue;
            }

            break;
        }

        heap[position] = item;
    }

    private static void SiftUp(List<PyObject> heap, int position)
    {
        var end = heap.Count;
        var start = position;
        var item = heap[position];
        var child = (2 * position) + 1;

        while (child < end)
        {
            var right = child + 1;

            if (right < end && !Less(heap[child], heap[right]))
            {
                child = right;
            }

            heap[position] = heap[child];
            position = child;
            child = (2 * position) + 1;
        }

        heap[position] = item;
        SiftDown(heap, start, position);
    }

    private static PyObject Extreme(VirtualMachine machine, string name, PyObject[] arguments, PyDict? keywords, bool reverse)
    {
        Arity.Exact(name, arguments, 2);

        var count = arguments[0] is PyInt n
            ? n.Value
            : throw new PyRaise(PyErrors.TypeError($"'{arguments[0].TypeName}' object cannot be interpreted as an integer"));

        var items = arguments[1].Iterate() ?? throw new PyRaise(PyErrors.TypeError(
            $"'{arguments[1].TypeName}' object is not iterable"));

        var sortKeywords = new PyDict();

        foreach (var (key, value) in keywords?.Entries ?? [])
        {
            if (key.Display() != "key")
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"{name}() got an unexpected keyword argument '{key.Display()}'"));
            }

            sortKeywords.Set(key, value);
        }

        if (count <= 0)
        {
            return new PyList();
        }

        if (reverse)
        {
            sortKeywords.Set(new PyStr("reverse"), PyBool.True);
        }

        var sorted = Sorting.Sort(machine, items, sortKeywords);
        return new PyList(sorted.Take((int)System.Numerics.BigInteger.Min(count, sorted.Count)).ToList());
    }
}
