using System.Numerics;

namespace Computerwelt.Emulation.Python.Runtime;

/// <summary>
/// <c>bytearray</c>: the mutable counterpart of <c>bytes</c>.
/// </summary>
/// <remarks>
/// Upstream Monty has no <c>bytearray</c> (<c>.reference/monty/limitations/builtins.md</c>).
/// Every non-mutating method is <c>bytes</c>'s, run over a snapshot and re-wrapped, so the
/// two cannot drift apart; what this type adds is the mutation.
/// </remarks>
public sealed class PyByteArray : PyObject
{
    /// <summary>Creates a bytearray holding <paramref name="value"/>.</summary>
    public PyByteArray(IEnumerable<byte>? value = null) => Value = [.. value ?? []];

    /// <summary>The contents.</summary>
    public List<byte> Value { get; }

    /// <summary>A snapshot of the contents as <c>bytes</c>.</summary>
    public PyBytes Snapshot() => new([.. Value]);

    /// <inheritdoc />
    public override string TypeName => "bytearray";

    /// <inheritdoc />
    public override string Repr() => $"bytearray({Snapshot().Repr()})";

    /// <inheritdoc />
    public override bool IsTruthy() => Value.Count > 0;

    /// <inheritdoc />
    public override int? Length() => Value.Count;

    /// <inheritdoc />
    public override bool PyEquals(PyObject other) => other switch
    {
        PyByteArray array => Value.SequenceEqual(array.Value),
        PyBytes bytes => Value.SequenceEqual(bytes.Value),
        _ => false,
    };

    /// <inheritdoc />
    public override BigInteger PyHash() =>
        throw new PyRaise(PyErrors.TypeError("unhashable type: 'bytearray'"));

    /// <inheritdoc />
    public override int? PyCompare(PyObject other) => other switch
    {
        PyByteArray array => System.MemoryExtensions.SequenceCompareTo<byte>([.. Value], [.. array.Value]),
        PyBytes bytes => System.MemoryExtensions.SequenceCompareTo<byte>([.. Value], bytes.Value),
        _ => null,
    };

    /// <inheritdoc />
    public override IEnumerable<PyObject>? Iterate() => Walk();

    private IEnumerable<PyObject> Walk()
    {
        // By index rather than with an enumerator, so mutating the array while iterating it
        // behaves as CPython's does instead of throwing a host exception.
        for (var i = 0; i < Value.Count; i++)
        {
            yield return PyInt.From(Value[i]);
        }
    }

    /// <inheritdoc />
    public override bool Contains(PyObject item) => Snapshot().Contains(Unwrap(item));

    /// <inheritdoc />
    public override PyObject GetItem(PyObject index) =>
        Snapshot().GetItem(index) switch
        {
            PyBytes slice => new PyByteArray(slice.Value),
            var single => single,
        };

    /// <inheritdoc />
    public override void SetItem(PyObject index, PyObject value)
    {
        if (index is PySlice slice)
        {
            var replacement = Bytes(value, "can assign only bytes, buffers, or iterables of ints in range(0, 256)");
            var (start, stop, step, count) = slice.Resolve(Value.Count);

            if (step == 1)
            {
                var end = Math.Max(start, stop);
                Value.RemoveRange(start, end - start);
                Value.InsertRange(start, replacement);
                return;
            }

            if (replacement.Count != count)
            {
                throw new PyRaise(PyErrors.ValueError(
                    $"attempt to assign bytes of size {replacement.Count} to extended slice of size {count}"));
            }

            for (var i = 0; i < count; i++)
            {
                Value[start + (i * step)] = replacement[i];
            }

            return;
        }

        Value[Position(index)] = Byte(value);
    }

    /// <inheritdoc />
    public override void DeleteItem(PyObject index)
    {
        if (index is PySlice slice)
        {
            var (start, _, step, count) = slice.Resolve(Value.Count);
            var doomed = Enumerable.Range(0, count).Select(i => start + (i * step)).OrderDescending();

            foreach (var position in doomed)
            {
                Value.RemoveAt(position);
            }

            return;
        }

        Value.RemoveAt(Position(index));
    }

    private int Position(PyObject index) => index is PyInt integer
        ? PyStr.Normalize(integer.ToIndex(), Value.Count, "bytearray index out of range")
        : throw new PyRaise(PyErrors.TypeError($"bytearray indices must be integers or slices, not {index.TypeName}"));

    /// <summary>Reads an integer argument, as an index.</summary>
    internal static PyObject Index(PyObject value) => value is PyInt
        ? value
        : throw new PyRaise(PyErrors.TypeError($"'{value.TypeName}' object cannot be interpreted as an integer"));

    /// <summary>Reads one byte value: an int in <c>range(0, 256)</c>.</summary>
    internal static byte Byte(PyObject value) => value switch
    {
        PyInt integer when integer.Value >= 0 && integer.Value <= 255 => (byte)integer.Value,
        PyInt => throw new PyRaise(PyErrors.ValueError("byte must be in range(0, 256)")),
        _ => throw new PyRaise(PyErrors.TypeError(
            $"'{value.TypeName}' object cannot be interpreted as an integer")),
    };

    /// <summary>Reads a bytes-like value or an iterable of ints.</summary>
    internal static List<byte> Bytes(PyObject value, string message) => value switch
    {
        PyBytes bytes => [.. bytes.Value],
        PyByteArray array => [.. array.Value],
        PyStr => throw new PyRaise(PyErrors.TypeError(message)),
        _ => value.Iterate() is { } items
            ? [.. items.Select(Byte)]
            : throw new PyRaise(PyErrors.TypeError(message)),
    };

    /// <summary>Turns a bytearray argument into the bytes a bytes method expects.</summary>
    internal static PyObject Unwrap(PyObject value) => value is PyByteArray array ? array.Snapshot() : value;

    /// <summary>Re-wraps a bytes method's result as a bytearray's would be.</summary>
    internal static PyObject Rewrap(PyObject result) => result switch
    {
        PyBytes bytes => new PyByteArray(bytes.Value),
        PyList list => new PyList([.. list.Items.Select(Rewrap)]),
        PyTuple tuple => new PyTuple([.. tuple.Items.Select(Rewrap)]),
        _ => result,
    };

    /// <summary>Builds a bytearray as the constructor does.</summary>
    internal static PyByteArray Construct(PyObject[] arguments, PyDict? keywords)
    {
        if (arguments.Length == 0 && keywords is not { Count: > 0 })
        {
            return new PyByteArray();
        }

        return arguments.Length > 0 && arguments[0] is PyInt count && keywords is not { Count: > 0 }
            ? count.Value < 0
                ? throw new PyRaise(PyErrors.ValueError("negative count"))
                : count.Value > 1 << 28
                    ? throw new PyRaise(new PyException(PyExceptionType.MemoryError, string.Empty))
                    : new PyByteArray(new byte[(int)count.Value])
            : arguments.Length > 0 && arguments[0] is PyByteArray copy && keywords is not { Count: > 0 }
                ? new PyByteArray(copy.Value)
                : new PyByteArray(((PyBytes)Builtins.Conversions.ToBytes(arguments, keywords)).Value);
    }
}
