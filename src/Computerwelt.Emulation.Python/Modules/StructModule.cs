using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules;

/// <summary>
/// The <c>struct</c> module: values packed to and unpacked from bytes by a format string,
/// with CPython 3.13's sizes, alignment and messages.
/// </summary>
/// <remarks>
/// Native mode (<c>@</c>, the default) describes a 64-bit little-endian Linux host, as the
/// sandbox presents itself everywhere: <c>l</c>, <c>L</c>, <c>n</c>, <c>N</c> and <c>P</c>
/// are eight bytes and items are aligned to their size. <c>pack_into</c> is absent, because
/// there is no writable buffer type (no <c>bytearray</c>) for it to write into.
/// </remarks>
public static class StructModule
{
    /// <summary><c>struct.error</c>.</summary>
    public static PyExceptionType Error { get; } = PyExceptionType.DefineHostException("error");

    /// <summary>The most bytes one format may describe.</summary>
    /// <remarks>
    /// <c>pack('1000000000s', b'')</c> asks the host to allocate a gigabyte inside one
    /// builtin call, where the instruction limit cannot see it.
    /// </remarks>
    private const long MaxSize = 1 << 26;

    /// <summary>Builds the module.</summary>
    public static PyModuleObject Create()
    {
        var module = new PyModuleObject("struct");

        module.Add("error", Error);
        module.Add("calcsize", arguments => PyInt.From(Compile(Format(arguments, "calcsize")).Size));
        module.Add("pack", arguments => Compile(Format(arguments, "pack")).Pack(arguments[1..]));
        // `pack_into(format, buffer, offset, *values)`: pack straight into a bytearray.
        module.Add("pack_into", arguments =>
        {
            if (arguments.Length < 3)
            {
                throw new PyRaise(PyErrors.TypeError("pack_into expected at least 3 arguments"));
            }

            var packed = Compile(Format(arguments, "pack_into")).Pack(arguments[3..]).Value;

            if (arguments[1] is not PyByteArray buffer)
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"argument must be read-write bytes-like object, not {arguments[1].TypeName}"));
            }

            var offset = (long)Builtins.BuiltinNamespace.RequireInt(arguments[2], "pack_into");
            offset = offset < 0 ? offset + buffer.Value.Count : offset;

            if (offset < 0 || offset + packed.Length > buffer.Value.Count)
            {
                throw new PyRaise(new PyException(Error, offset < 0
                    ? $"offset {offset - buffer.Value.Count} out of range for {buffer.Value.Count}-byte buffer"
                    : $"pack_into requires a buffer of at least {packed.Length + offset} bytes for packing {packed.Length} bytes at offset {offset} (actual buffer size is {buffer.Value.Count})"));
            }

            for (var i = 0; i < packed.Length; i++)
            {
                buffer.Value[(int)offset + i] = packed[i];
            }

            return PyNone.Instance;
        });

        module.Add("unpack", arguments => Unpack(Compile(Format(arguments, "unpack")), arguments, 1));
        module.Add("unpack_from", (arguments, keywords) =>
            UnpackFrom(Compile(Format(arguments, "unpack_from")), arguments, keywords, 1));
        module.Add("iter_unpack", arguments => IterUnpack(Compile(Format(arguments, "iter_unpack")), arguments, 1));

        module.Add("Struct", new PyType("Struct", static value => value is PyStruct, static (arguments, keywords) =>
        {
            var format = arguments.Length > 0 ? arguments[0]
                : keywords?.Entries.FirstOrDefault(static e => e.Key.Display() == "format").Value
                ?? throw new PyRaise(PyErrors.TypeError("Struct() missing required argument 'format' (pos 1)"));
            return new PyStruct(Compile(FormatText(format)));
        }));

        return module;
    }

    private static string Format(PyObject[] arguments, string function) =>
        arguments.Length == 0
            ? throw new PyRaise(PyErrors.TypeError($"{function}() missing required argument 'format' (pos 1)"))
            : FormatText(arguments[0]);

    private static string FormatText(PyObject value) => value switch
    {
        PyStr text => text.Value,
        PyBytes bytes => Encoding.ASCII.GetString(bytes.Value),
        _ => throw new PyRaise(PyErrors.TypeError(
            $"Struct() argument 1 must be a str or bytes object, not {value.TypeName}")),
    };

    private static byte[] Buffer(PyObject value) => value switch
    {
        PyBytes bytes => bytes.Value,
        PyByteArray array => [.. array.Value],
        _ => throw new PyRaise(PyErrors.TypeError($"a bytes-like object is required, not '{value.TypeName}'")),
    };

    internal static PyObject Unpack(CompiledFormat format, PyObject[] arguments, int first)
    {
        Arity.ExactCount("unpack", arguments, first + 1);
        var buffer = Buffer(arguments[first]);

        if (buffer.Length != format.Size)
        {
            throw new PyRaise(new PyException(Error, $"unpack requires a buffer of {format.Size} bytes"));
        }

        return format.Unpack(buffer, 0);
    }

    internal static PyObject UnpackFrom(CompiledFormat format, PyObject[] arguments, PyDict? keywords, int first)
    {
        var bufferArgument = arguments.Length > first ? arguments[first]
            : keywords?.Entries.FirstOrDefault(static e => e.Key.Display() == "buffer").Value
            ?? throw new PyRaise(PyErrors.TypeError("unpack_from() missing required argument 'buffer' (pos 2)"));
        var offsetArgument = arguments.Length > first + 1 ? arguments[first + 1]
            : keywords?.Entries.FirstOrDefault(static e => e.Key.Display() == "offset").Value;

        var buffer = Buffer(bufferArgument);
        var offset = offsetArgument is null ? 0 : (long)Builtins.BuiltinNamespace.RequireInt(offsetArgument, "unpack_from");

        if (offset < 0)
        {
            if (offset + buffer.Length < 0)
            {
                throw new PyRaise(new PyException(Error, $"offset {offset} out of range for {buffer.Length}-byte buffer"));
            }

            offset += buffer.Length;
        }

        if (buffer.Length - offset < format.Size)
        {
            throw new PyRaise(new PyException(Error, offset == 0
                ? $"unpack_from requires a buffer of at least {format.Size} bytes (actual buffer size is {buffer.Length})"
                : $"unpack_from requires a buffer of at least {format.Size + offset} bytes for unpacking {format.Size} bytes at offset {offset} (actual buffer size is {buffer.Length})"));
        }

        return format.Unpack(buffer, (int)offset);
    }

    internal static PyObject IterUnpack(CompiledFormat format, PyObject[] arguments, int first)
    {
        Arity.ExactCount("iter_unpack", arguments, first + 1);
        var buffer = Buffer(arguments[first]);

        if (format.Size == 0)
        {
            throw new PyRaise(new PyException(Error, "cannot iteratively unpack with a struct of length 0"));
        }

        if (buffer.Length % format.Size != 0)
        {
            throw new PyRaise(new PyException(Error,
                $"iterative unpacking requires a buffer of a multiple of {format.Size} bytes"));
        }

        var offset = 0;

        return new PyIterator(() =>
        {
            if (offset >= buffer.Length)
            {
                return null;
            }

            var item = format.Unpack(buffer, offset);
            offset += format.Size;
            return item;
        }, "unpack_iterator");
    }

    /// <summary>Parses a format string into its items, sizes and offsets.</summary>
    internal static CompiledFormat Compile(string format)
    {
        var index = 0;
        var native = true;
        var little = BitConverter.IsLittleEndian;

        if (format.Length > 0 && format[0] is '@' or '=' or '<' or '>' or '!')
        {
            native = format[0] == '@';
            little = format[0] switch
            {
                '<' => true,
                '>' or '!' => false,
                _ => BitConverter.IsLittleEndian,
            };
            index = 1;
        }

        var items = new List<FormatItem>();
        long size = 0;

        while (index < format.Length)
        {
            var c = format[index];

            if (char.IsWhiteSpace(c))
            {
                index++;
                continue;
            }

            long count = 1;
            var hasCount = false;

            if (char.IsAsciiDigit(c))
            {
                count = 0;

                while (index < format.Length && char.IsAsciiDigit(format[index]))
                {
                    count = (count * 10) + (format[index++] - '0');

                    if (count > MaxSize)
                    {
                        throw new PyRaise(new PyException(Error, "total struct size too long"));
                    }
                }

                hasCount = true;

                if (index >= format.Length)
                {
                    throw new PyRaise(new PyException(Error, "repeat count given without format specifier"));
                }

                c = format[index];
            }

            index++;
            var itemSize = SizeOf(c, native);

            if (native && itemSize > 1 && c is not ('s' or 'p'))
            {
                size = (size + itemSize - 1) / itemSize * itemSize;
            }

            if (c is 's' or 'p')
            {
                items.Add(new FormatItem(c, (int)count, size, itemSize));
                size += count;
            }
            else
            {
                for (var i = 0; i < count; i++)
                {
                    items.Add(new FormatItem(c, 1, size, itemSize));
                    size += itemSize;
                }
            }

            _ = hasCount;

            if (size > MaxSize)
            {
                throw new PyRaise(new PyException(Error, "total struct size too long"));
            }
        }

        return new CompiledFormat(format, items, (int)size, little, native);
    }

    private static int SizeOf(char code, bool native) => code switch
    {
        'x' or 'c' or 'b' or 'B' or '?' or 's' or 'p' => 1,
        'h' or 'H' or 'e' => 2,
        'i' or 'I' or 'f' => 4,
        'l' or 'L' => native ? 8 : 4,
        'q' or 'Q' or 'd' => 8,
        'n' or 'N' or 'P' when native => 8,
        _ => throw new PyRaise(new PyException(Error, "bad char in struct format")),
    };

    /// <summary>One packed item: its code, repeat (for strings), offset and size.</summary>
    internal readonly record struct FormatItem(char Code, int Count, long Offset, int Size);

    /// <summary>A compiled format.</summary>
    internal sealed class CompiledFormat(string text, List<FormatItem> items, int size, bool little, bool native)
    {
        public string Text { get; } = text;

        public int Size { get; } = size;

        private int ValueCount => items.Count(static item => item.Code != 'x');

        public PyBytes Pack(PyObject[] values)
        {
            if (values.Length != ValueCount)
            {
                throw new PyRaise(new PyException(Error, $"pack expected {ValueCount} items for packing (got {values.Length})"));
            }

            var buffer = new byte[Size];
            var next = 0;

            foreach (var item in items)
            {
                if (item.Code == 'x')
                {
                    continue;
                }

                Write(buffer.AsSpan((int)item.Offset), item, values[next++]);
            }

            return new PyBytes(buffer);
        }

        public PyTuple Unpack(byte[] buffer, int start)
        {
            var values = new List<PyObject>(ValueCount);

            foreach (var item in items)
            {
                if (item.Code != 'x')
                {
                    values.Add(Read(buffer.AsSpan(start + (int)item.Offset), item));
                }
            }

            return new PyTuple(values);
        }

        private void Write(Span<byte> target, FormatItem item, PyObject value)
        {
            switch (item.Code)
            {
                case 'c':
                    if (value is not PyBytes { Value.Length: 1 } character)
                    {
                        throw new PyRaise(new PyException(Error, "char format requires a bytes object of length 1"));
                    }

                    target[0] = character.Value[0];
                    return;

                case '?':
                    target[0] = (byte)(value.IsTruthy() ? 1 : 0);
                    return;

                case 's' or 'p':
                {
                    if (value is not PyBytes bytes)
                    {
                        throw new PyRaise(new PyException(Error, $"argument for '{item.Code}' must be a bytes object"));
                    }

                    if (item.Code == 's')
                    {
                        bytes.Value.AsSpan(0, Math.Min(bytes.Value.Length, item.Count)).CopyTo(target);
                        return;
                    }

                    if (item.Count == 0)
                    {
                        return;
                    }

                    var length = Math.Min(Math.Min(bytes.Value.Length, item.Count - 1), 255);
                    target[0] = (byte)length;
                    bytes.Value.AsSpan(0, length).CopyTo(target[1..]);
                    return;
                }

                case 'e' or 'f' or 'd':
                {
                    var number = value switch
                    {
                        PyFloat f => f.Value,
                        PyInt i => Numbers.ToDouble(i.Value),
                        IPyNumber n => n.ToDouble(),
                        _ => throw new PyRaise(new PyException(Error, "required argument is not a float")),
                    };

                    if (item.Code == 'd')
                    {
                        WriteInteger(target, BitConverter.DoubleToInt64Bits(number), 8);
                    }
                    else if (item.Code == 'f')
                    {
                        var single = (float)number;

                        if (float.IsInfinity(single) && double.IsFinite(number))
                        {
                            throw new PyRaise(new PyException(PyExceptionType.OverflowError, "float too large to pack with f format"));
                        }

                        WriteInteger(target, BitConverter.SingleToInt32Bits(single), 4);
                    }
                    else
                    {
                        var half = (Half)number;

                        if (Half.IsInfinity(half) && double.IsFinite(number))
                        {
                            throw new PyRaise(new PyException(PyExceptionType.OverflowError, "float too large to pack with e format"));
                        }

                        WriteInteger(target, BitConverter.HalfToInt16Bits(half), 2);
                    }

                    return;
                }

                default:
                {
                    if (value is not PyInt integer)
                    {
                        throw new PyRaise(new PyException(Error, "required argument is not an integer"));
                    }

                    var signed = item.Code is 'b' or 'h' or 'i' or 'l' or 'q' or 'n';
                    var bits = item.Size * 8;
                    var minimum = signed ? -(BigInteger.One << (bits - 1)) : BigInteger.Zero;
                    var maximum = signed ? (BigInteger.One << (bits - 1)) - 1 : (BigInteger.One << bits) - 1;

                    if (integer.Value < minimum || integer.Value > maximum)
                    {
                        throw new PyRaise(new PyException(Error,
                            $"'{item.Code}' format requires {minimum} <= number <= {maximum}"));
                    }

                    if (item.Code == 'P' && !native)
                    {
                        throw new PyRaise(new PyException(Error, "bad char in struct format"));
                    }

                    var raw = integer.Value < 0 ? integer.Value + (BigInteger.One << bits) : integer.Value;
                    WriteInteger(target, (long)(ulong)raw, item.Size);
                    return;
                }
            }
        }

        private void WriteInteger(Span<byte> target, long value, int size)
        {
            for (var i = 0; i < size; i++)
            {
                var b = (byte)((ulong)value >> (8 * i));
                target[little ? i : size - 1 - i] = b;
            }
        }

        private ulong ReadInteger(ReadOnlySpan<byte> source, int size)
        {
            ulong value = 0;

            for (var i = 0; i < size; i++)
            {
                value |= (ulong)source[little ? i : size - 1 - i] << (8 * i);
            }

            return value;
        }

        private PyObject Read(ReadOnlySpan<byte> source, FormatItem item)
        {
            switch (item.Code)
            {
                case 'c':
                    return new PyBytes([source[0]]);
                case '?':
                    return PyBool.Of(source[0] != 0);
                case 's':
                    return new PyBytes(source[..item.Count].ToArray());
                case 'p':
                {
                    if (item.Count == 0)
                    {
                        return new PyBytes([]);
                    }

                    var length = Math.Min(source[0], item.Count - 1);
                    return new PyBytes(source.Slice(1, length).ToArray());
                }

                case 'd':
                    return new PyFloat(BitConverter.Int64BitsToDouble((long)ReadInteger(source, 8)));
                case 'f':
                    return new PyFloat(BitConverter.Int32BitsToSingle((int)ReadInteger(source, 4)));
                case 'e':
                    return new PyFloat((double)BitConverter.Int16BitsToHalf((short)ReadInteger(source, 2)));
            }

            var raw = ReadInteger(source, item.Size);
            var signed = item.Code is 'b' or 'h' or 'i' or 'l' or 'q' or 'n';

            if (!signed)
            {
                return PyInt.From(new BigInteger(raw));
            }

            var bits = item.Size * 8;
            var value = new BigInteger(raw);

            if (value >= BigInteger.One << (bits - 1))
            {
                value -= BigInteger.One << bits;
            }

            return PyInt.From(value);
        }
    }
}

/// <summary>A <c>struct.Struct</c>: a compiled format.</summary>
internal sealed class PyStruct(StructModule.CompiledFormat format) : PyObject
{
    public override string TypeName => "_struct.Struct";

    public override string Repr() => $"Struct({PyStr.Quote(format.Text)})";

    public override PyObject? GetAttribute(string name) => name switch
    {
        "format" => new PyStr(format.Text),
        "size" => PyInt.From(format.Size),
        "pack" => new PyBuiltinFunction("pack", arguments => format.Pack(arguments)),
        "unpack" => new PyBuiltinFunction("unpack", arguments =>
            StructModule.Unpack(format, [PyNone.Instance, .. arguments], 1)),
        "unpack_from" => new PyBuiltinFunction("unpack_from", (arguments, keywords) =>
            StructModule.UnpackFrom(format, [PyNone.Instance, .. arguments], keywords, 1)),
        "iter_unpack" => new PyBuiltinFunction("iter_unpack", arguments =>
            StructModule.IterUnpack(format, [PyNone.Instance, .. arguments], 1)),
        _ => null,
    };
}
