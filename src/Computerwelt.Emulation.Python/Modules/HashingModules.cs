using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules;

/// <summary>The <c>hashlib</c> and <c>hmac</c> modules.</summary>
/// <remarks>
/// Hashing is managed code over the platform's primitives — nothing is spawned and nothing
/// is read from outside the sandbox. A hash object buffers what it is fed and digests on
/// demand, so <c>copy</c> is a copy of the buffer and <c>digest</c> can be asked for at any
/// point without disturbing the stream.
/// </remarks>
public static class HashingModules
{
    /// <summary>The most iterations <c>pbkdf2_hmac</c> will run: the work is native and uncounted.</summary>
    private const int MaxIterations = 2_000_000;

    private static readonly (string Name, HashAlgorithmName Algorithm, int Size, int Block)[] Algorithms =
    [
        ("md5", HashAlgorithmName.MD5, 16, 64),
        ("sha1", HashAlgorithmName.SHA1, 20, 64),
        ("sha256", HashAlgorithmName.SHA256, 32, 64),
        ("sha384", HashAlgorithmName.SHA384, 48, 128),
        ("sha512", HashAlgorithmName.SHA512, 64, 128),
        ("sha3_256", HashAlgorithmName.SHA3_256, 32, 136),
        ("sha3_384", HashAlgorithmName.SHA3_384, 48, 104),
        ("sha3_512", HashAlgorithmName.SHA3_512, 64, 72),
    ];

    private static IEnumerable<(string Name, HashAlgorithmName Algorithm, int Size, int Block)> Available() =>
        Algorithms.Where(static a => !a.Name.StartsWith("sha3", StringComparison.Ordinal) || SHA3_256.IsSupported);

    /// <summary>The algorithm named by a string or a <c>hashlib</c> constructor.</summary>
    internal static (string Name, HashAlgorithmName Algorithm, int Size, int Block) Find(PyObject nameOrConstructor)
    {
        var name = (nameOrConstructor switch
        {
            PyStr text => text.Value,
            PyCallable callable => callable.Name,
            _ => throw new PyRaise(PyErrors.TypeError("Missing required argument 'digestmod'.")),
        }).ToLowerInvariant().Replace("-", "_", StringComparison.Ordinal);

        if (name.StartsWith("hashlib.", StringComparison.Ordinal))
        {
            name = name["hashlib.".Length..];
        }

        foreach (var algorithm in Available())
        {
            if (algorithm.Name == name)
            {
                return algorithm;
            }
        }

        throw new PyRaise(PyErrors.ValueError($"unsupported hash type {name}"));
    }

    /// <summary>The bytes of a bytes-like argument, refusing text.</summary>
    internal static byte[] Bytes(PyObject value, string refusal) => value switch
    {
        PyBytes bytes => bytes.Value,
        PyStr => throw new PyRaise(PyErrors.TypeError(refusal)),
        _ => throw new PyRaise(PyErrors.TypeError($"object supporting the buffer API required")),
    };

    /// <summary>Builds <c>hashlib</c>.</summary>
    public static PyModuleObject CreateHashlib()
    {
        var module = new PyModuleObject("hashlib");

        foreach (var algorithm in Available())
        {
            var entry = algorithm;

            module.Add(entry.Name, new PyBuiltinFunction(entry.Name, (arguments, keywords) =>
            {
                Arity.Between(entry.Name, arguments, 0, 1);
                var hash = new PyHash(entry, false);
                var data = arguments.Length > 0 ? arguments[0] : Keyword(keywords, "string");

                if (data is not null)
                {
                    hash.Update(Bytes(data, "Strings must be encoded before hashing"));
                }

                return hash;
            }));
        }

        module.Add("new", new PyBuiltinFunction("new", (arguments, keywords) =>
        {
            Arity.Between("new", arguments, 1, 2);
            var hash = new PyHash(Find(arguments[0]), false);
            var data = arguments.Length > 1 ? arguments[1] : Keyword(keywords, "data");

            if (data is not null)
            {
                hash.Update(Bytes(data, "Strings must be encoded before hashing"));
            }

            return hash;
        }));

        var names = new PySet(Available().Select(static a => (PyObject)new PyStr(a.Name)));
        module.Add("algorithms_guaranteed", names);
        module.Add("algorithms_available", names);

        module.Add("pbkdf2_hmac", new PyBuiltinFunction("pbkdf2_hmac", (arguments, keywords) =>
        {
            Arity.Between("pbkdf2_hmac", arguments, 4, 5);
            var algorithm = Find(arguments[0]);
            var password = Bytes(arguments[1], "a bytes-like object is required, not 'str'");
            var salt = Bytes(arguments[2], "a bytes-like object is required, not 'str'");

            var iterations = arguments[3] is PyInt count
                ? count.Value
                : throw new PyRaise(PyErrors.TypeError("'str' object cannot be interpreted as an integer"));

            if (iterations < 1)
            {
                throw new PyRaise(PyErrors.ValueError("iteration value must be greater than 0."));
            }

            if (iterations > MaxIterations)
            {
                throw new PyRaise(PyErrors.ValueError(
                    $"iteration value is limited to {MaxIterations} in this sandbox"));
            }

            var length = (arguments.Length > 4 ? arguments[4] : Keyword(keywords, "dklen")) switch
            {
                null or PyNone => algorithm.Size,
                PyInt n when n.Value > 0 && n.Value <= 1 << 20 => (int)n.Value,
                _ => throw new PyRaise(PyErrors.ValueError("key length must be greater than 0.")),
            };

            return new PyBytes(Rfc2898DeriveBytes.Pbkdf2(password, salt, (int)iterations, algorithm.Algorithm, length));
        }));

        return module;
    }

    /// <summary>Builds <c>hmac</c>.</summary>
    public static PyModuleObject CreateHmac()
    {
        var module = new PyModuleObject("hmac");

        module.Add("new", new PyBuiltinFunction("new", (arguments, keywords) =>
        {
            Arity.Between("new", arguments, 1, 3);
            var key = Bytes(arguments[0], "key: expected bytes or bytearray, but got 'str'");
            var message = arguments.Length > 1 ? arguments[1] : Keyword(keywords, "msg");
            var mode = arguments.Length > 2 ? arguments[2] : Keyword(keywords, "digestmod");

            var mac = new PyHash(Find(mode ?? throw new PyRaise(PyErrors.TypeError("Missing required argument 'digestmod'."))), true, key);

            if (message is not null and not PyNone)
            {
                mac.Update(Bytes(message, "Strings must be encoded before hashing"));
            }

            return mac;
        }));

        module.Add("HMAC", module.GetAttribute("new")!);

        module.Add("digest", new PyBuiltinFunction("digest", arguments =>
        {
            Arity.Exact("digest", arguments, 3);
            var mac = new PyHash(Find(arguments[2]), true, Bytes(arguments[0], "key: expected bytes or bytearray, but got 'str'"));
            mac.Update(Bytes(arguments[1], "Strings must be encoded before hashing"));
            return new PyBytes(mac.Digest());
        }));

        module.Add("compare_digest", new PyBuiltinFunction("compare_digest", arguments =>
        {
            Arity.Exact("compare_digest", arguments, 2);

            byte[] Both(PyObject value) => value switch
            {
                PyBytes bytes => bytes.Value,
                PyStr text when text.Value.All(static c => c < 128) => Encoding.ASCII.GetBytes(text.Value),
                PyStr => throw new PyRaise(PyErrors.TypeError("comparing strings with non-ASCII characters is not supported")),
                _ => throw new PyRaise(PyErrors.TypeError($"unsupported operand types(s) or combination of types: '{arguments[0].TypeName}' and '{arguments[1].TypeName}'")),
            };

            if (arguments[0].GetType() != arguments[1].GetType())
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"unsupported operand types(s) or combination of types: '{arguments[0].TypeName}' and '{arguments[1].TypeName}'"));
            }

            return PyBool.Of(CryptographicOperations.FixedTimeEquals(Both(arguments[0]), Both(arguments[1])));
        }));

        return module;
    }

    private static PyObject? Keyword(PyDict? keywords, string name) =>
        keywords is not null && keywords.TryGetValue(new PyStr(name), out var value) ? value : null;

    /// <summary>A hash or HMAC object.</summary>
    private sealed class PyHash : PyObject
    {
        private readonly (string Name, HashAlgorithmName Algorithm, int Size, int Block) _algorithm;
        private readonly bool _keyed;
        private readonly byte[] _key;
        private readonly List<byte> _data = [];

        public PyHash((string Name, HashAlgorithmName Algorithm, int Size, int Block) algorithm, bool keyed, byte[]? key = null)
        {
            _algorithm = algorithm;
            _keyed = keyed;
            _key = key ?? [];
        }

        public override string TypeName => _keyed ? "hmac.HMAC" : $"_hashlib.HASH";

        public override string Repr() => _keyed
            ? $"<hmac.HMAC object>"
            : $"<{_algorithm.Name} _hashlib.HASH object>";

        public void Update(byte[] bytes) => _data.AddRange(bytes);

        public byte[] Digest()
        {
            var data = _data.ToArray();

            return _keyed
                ? CryptographicOperations.HmacData(_algorithm.Algorithm, _key, data)
                : CryptographicOperations.HashData(_algorithm.Algorithm, data);
        }

        public override PyObject? GetAttribute(string name) => name switch
        {
            "name" => new PyStr(_keyed ? $"hmac-{_algorithm.Name}" : _algorithm.Name),
            "digest_size" => PyInt.From(_algorithm.Size),
            "block_size" => PyInt.From(_algorithm.Block),

            "update" => new PyBuiltinFunction("update", arguments =>
            {
                Arity.Exact("update", arguments, 1);
                Update(Bytes(arguments[0], "Strings must be encoded before hashing"));
                return PyNone.Instance;
            }),

            "digest" => new PyBuiltinFunction("digest", arguments =>
            {
                Arity.Exact("digest", arguments, 0);
                return new PyBytes(Digest());
            }),

            "hexdigest" => new PyBuiltinFunction("hexdigest", arguments =>
            {
                Arity.Exact("hexdigest", arguments, 0);
                return new PyStr(Convert.ToHexStringLower(Digest()));
            }),

            "copy" => new PyBuiltinFunction("copy", arguments =>
            {
                Arity.Exact("copy", arguments, 0);
                var clone = new PyHash(_algorithm, _keyed, _key);
                clone._data.AddRange(_data);
                return clone;
            }),

            _ => null,
        };
    }
}

/// <summary>The <c>binascii</c> and <c>base64</c> modules.</summary>
public static class EncodingModules
{
    private const string Standard = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    private const string Base32 = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary><c>binascii.Error</c>, a <c>ValueError</c>.</summary>
    internal static readonly PyExceptionType BinasciiError = PyExceptionType.DefineHostException("Error", PyExceptionType.ValueError);

    private static readonly PyExceptionType Incomplete = PyExceptionType.DefineHostException("Incomplete");

    private static PyRaise Fail(string message) => new(new PyException(BinasciiError, message));

    /// <summary>Builds <c>binascii</c>.</summary>
    public static PyModuleObject CreateBinascii()
    {
        var module = new PyModuleObject("binascii");
        module.Add("Error", BinasciiError);
        module.Add("Incomplete", Incomplete);

        foreach (var name in new[] { "hexlify", "b2a_hex" })
        {
            module.Add(name, new PyBuiltinFunction(name, arguments =>
            {
                Arity.Between(name, arguments, 1, 3);
                var data = Raw(arguments[0]);
                var hex = Convert.ToHexStringLower(data);

                if (arguments.Length > 1 && arguments[1] is PyBytes or PyStr)
                {
                    var separator = arguments[1] is PyBytes sb ? Encoding.Latin1.GetString(sb.Value) : arguments[1].Display();
                    hex = string.Join(separator, Enumerable.Range(0, data.Length).Select(i => hex.Substring(i * 2, 2)));
                }

                return new PyBytes(Encoding.ASCII.GetBytes(hex));
            }));
        }

        foreach (var name in new[] { "unhexlify", "a2b_hex" })
        {
            module.Add(name, new PyBuiltinFunction(name, arguments =>
            {
                Arity.Exact(name, arguments, 1);
                return new PyBytes(FromHex(arguments[0]));
            }));
        }

        module.Add("b2a_base64", new PyBuiltinFunction("b2a_base64", (arguments, keywords) =>
        {
            Arity.Exact("b2a_base64", arguments, 1);
            var newline = keywords is null || !keywords.TryGetValue(new PyStr("newline"), out var flag) || flag.IsTruthy();
            return new PyBytes(Encoding.ASCII.GetBytes(Convert.ToBase64String(Raw(arguments[0])) + (newline ? "\n" : string.Empty)));
        }));

        module.Add("a2b_base64", new PyBuiltinFunction("a2b_base64", (arguments, keywords) =>
        {
            Arity.Exact("a2b_base64", arguments, 1);
            return new PyBytes(DecodeBase64(Ascii(arguments[0]), strict: keywords is not null && keywords.TryGetValue(new PyStr("strict_mode"), out var s) && s.IsTruthy()));
        }));

        module.Add("crc32", new PyBuiltinFunction("crc32", arguments =>
        {
            Arity.Between("crc32", arguments, 1, 2);
            var crc = arguments.Length > 1 && arguments[1] is PyInt start ? (uint)(start.Value & 0xffffffff) : 0u;
            return PyInt.From(Crc32(Raw(arguments[0]), crc));
        }));

        return module;
    }

    /// <summary>Builds <c>base64</c>.</summary>
    public static PyModuleObject CreateBase64()
    {
        var module = new PyModuleObject("base64");

        module.Add("b64encode", new PyBuiltinFunction("b64encode", (arguments, keywords) =>
        {
            var bound = Bound("b64encode", arguments, keywords, "s", "altchars");
            var text = Convert.ToBase64String(Raw(bound[0] ?? throw Missing("b64encode", "s")));

            if (bound[1] is { } and not PyNone)
            {
                var alt = Raw(bound[1]!);

                if (alt.Length != 2)
                {
                    throw new PyRaise(PyErrors.ValueError("Bad altchars"));
                }

                text = text.Replace('+', (char)alt[0]).Replace('/', (char)alt[1]);
            }

            return new PyBytes(Encoding.ASCII.GetBytes(text));
        }));

        module.Add("b64decode", new PyBuiltinFunction("b64decode", (arguments, keywords) =>
        {
            var bound = Bound("b64decode", arguments, keywords, "s", "altchars", "validate");
            var text = Ascii(bound[0] ?? throw Missing("b64decode", "s"));

            if (bound[1] is { } and not PyNone)
            {
                var alt = Raw(bound[1]!);

                if (alt.Length != 2)
                {
                    throw new PyRaise(PyErrors.ValueError("Bad altchars"));
                }

                text = text.Replace((char)alt[0], '+').Replace((char)alt[1], '/');
            }

            var validate = bound[2]?.IsTruthy() ?? false;

            return new PyBytes(DecodeBase64(text, strict: validate));
        }));

        module.Add("standard_b64encode", new PyBuiltinFunction("standard_b64encode", arguments =>
        {
            Arity.Exact("standard_b64encode", arguments, 1);
            return new PyBytes(Encoding.ASCII.GetBytes(Convert.ToBase64String(Raw(arguments[0]))));
        }));

        module.Add("standard_b64decode", new PyBuiltinFunction("standard_b64decode", arguments =>
        {
            Arity.Exact("standard_b64decode", arguments, 1);
            return new PyBytes(DecodeBase64(Ascii(arguments[0]), strict: false));
        }));

        module.Add("urlsafe_b64encode", new PyBuiltinFunction("urlsafe_b64encode", arguments =>
        {
            Arity.Exact("urlsafe_b64encode", arguments, 1);
            return new PyBytes(Encoding.ASCII.GetBytes(Convert.ToBase64String(Raw(arguments[0])).Replace('+', '-').Replace('/', '_')));
        }));

        module.Add("urlsafe_b64decode", new PyBuiltinFunction("urlsafe_b64decode", arguments =>
        {
            Arity.Exact("urlsafe_b64decode", arguments, 1);
            return new PyBytes(DecodeBase64(Ascii(arguments[0]).Replace('-', '+').Replace('_', '/'), strict: false));
        }));

        module.Add("encodebytes", new PyBuiltinFunction("encodebytes", arguments =>
        {
            Arity.Exact("encodebytes", arguments, 1);
            var text = Convert.ToBase64String(Raw(arguments[0]));
            var lines = Enumerable.Range(0, (text.Length + 75) / 76).Select(i => text.Substring(i * 76, Math.Min(76, text.Length - (i * 76))) + "\n");
            return new PyBytes(Encoding.ASCII.GetBytes(string.Concat(lines)));
        }));

        module.Add("decodebytes", new PyBuiltinFunction("decodebytes", arguments =>
        {
            Arity.Exact("decodebytes", arguments, 1);
            return new PyBytes(DecodeBase64(Ascii(arguments[0]), strict: false));
        }));

        module.Add("b16encode", new PyBuiltinFunction("b16encode", arguments =>
        {
            Arity.Exact("b16encode", arguments, 1);
            return new PyBytes(Encoding.ASCII.GetBytes(Convert.ToHexString(Raw(arguments[0]))));
        }));

        module.Add("b16decode", new PyBuiltinFunction("b16decode", (arguments, keywords) =>
        {
            var bound = Bound("b16decode", arguments, keywords, "s", "casefold");
            var text = Ascii(bound[0] ?? throw Missing("b16decode", "s"));

            if (bound[1]?.IsTruthy() != true)
            {
                text = text.Any(static c => c is >= 'a' and <= 'f') ? throw Fail("Non-base16 digit found") : text;
            }

            return new PyBytes(FromHex(new PyStr(text), "Non-base16 digit found"));
        }));

        module.Add("b32encode", new PyBuiltinFunction("b32encode", arguments =>
        {
            Arity.Exact("b32encode", arguments, 1);
            return new PyBytes(Encoding.ASCII.GetBytes(EncodeBase32(Raw(arguments[0]))));
        }));

        module.Add("b32decode", new PyBuiltinFunction("b32decode", (arguments, keywords) =>
        {
            var bound = Bound("b32decode", arguments, keywords, "s", "casefold", "map01");
            return new PyBytes(DecodeBase32(Ascii(bound[0] ?? throw Missing("b32decode", "s")), bound[1]?.IsTruthy() ?? false));
        }));

        return module;
    }

    private static PyRaise Missing(string function, string name) =>
        new(PyErrors.TypeError($"{function}() missing required argument '{name}' (pos 1)"));

    private static PyObject?[] Bound(string function, PyObject[] arguments, PyDict? keywords, params string[] names)
    {
        if (arguments.Length > names.Length)
        {
            throw new PyRaise(PyErrors.TypeError(
                $"{function}() takes at most {names.Length} arguments ({arguments.Length} given)"));
        }

        var bound = new PyObject?[names.Length];
        arguments.CopyTo(bound, 0);

        foreach (var (key, value) in keywords?.Entries ?? [])
        {
            var at = Array.IndexOf(names, key.Display());
            bound[at >= 0 ? at : throw new PyRaise(PyErrors.TypeError(
                $"{function}() got an unexpected keyword argument '{key.Display()}'"))] = value;
        }

        return bound;
    }

    private static byte[] Raw(PyObject value) => value switch
    {
        PyBytes bytes => bytes.Value,
        _ => throw new PyRaise(PyErrors.TypeError($"a bytes-like object is required, not '{value.TypeName}'")),
    };

    private static string Ascii(PyObject value)
    {
        switch (value)
        {
            case PyBytes bytes:
                return Encoding.Latin1.GetString(bytes.Value);

            case PyStr text:
                return text.Value.All(static c => c < 128)
                    ? text.Value
                    : throw new PyRaise(PyErrors.ValueError("string argument should contain only ASCII characters"));

            default:
                throw new PyRaise(PyErrors.TypeError(
                    $"argument should be a bytes-like object or ASCII string, not '{value.TypeName}'"));
        }
    }

    private static byte[] FromHex(PyObject value, string badDigit = "Non-hexadecimal digit found")
    {
        var text = Ascii(value);

        if (text.Length % 2 != 0)
        {
            throw Fail("Odd-length string");
        }

        var result = new byte[text.Length / 2];

        for (var i = 0; i < result.Length; i++)
        {
            var high = Uri.IsHexDigit(text[2 * i]) ? Convert.ToInt32(text[2 * i].ToString(), 16) : -1;
            var low = Uri.IsHexDigit(text[(2 * i) + 1]) ? Convert.ToInt32(text[(2 * i) + 1].ToString(), 16) : -1;

            if (high < 0 || low < 0)
            {
                throw Fail(badDigit);
            }

            result[i] = (byte)((high << 4) | low);
        }

        return result;
    }

    /// <summary><c>binascii.a2b_base64</c>: tolerant of stray characters unless strict, and exact about padding.</summary>
    private static byte[] DecodeBase64(string text, bool strict)
    {
        var output = new List<byte>();
        var leftover = 0;
        var position = 0;
        var padding = 0;
        var dataCharacters = 0;
        var sawPadEnd = false;

        foreach (var c in text)
        {
            if (c == '=')
            {
                if (strict && position == 0 && dataCharacters == 0)
                {
                    throw Fail("Leading padding not allowed");
                }

                padding++;

                // A pad sequence ends the data; only strict mode refuses what follows it.
                if (strict && position >= 2 && position + padding >= 4)
                {
                    sawPadEnd = true;
                }

                continue;
            }

            if (sawPadEnd)
            {
                throw Fail("Excess data after padding");
            }

            var digit = Standard.IndexOf(c, StringComparison.Ordinal);

            if (digit < 0)
            {
                if (strict)
                {
                    throw Fail("Only base64 data is allowed");
                }

                continue;
            }

            padding = 0;
            dataCharacters++;

            switch (position)
            {
                case 0:
                    leftover = digit;
                    position = 1;
                    break;

                case 1:
                    output.Add((byte)((leftover << 2) | (digit >> 4)));
                    leftover = digit & 0xf;
                    position = 2;
                    break;

                case 2:
                    output.Add((byte)((leftover << 4) | (digit >> 2)));
                    leftover = digit & 3;
                    position = 3;
                    break;

                default:
                    output.Add((byte)((leftover << 6) | digit));
                    position = 0;
                    break;
            }
        }

        // Trailing padding makes up a short final group; anything less is an error.
        if (position != 0 && position + padding < 4)
        {
            throw Fail(position == 1
                ? $"Invalid base64-encoded string: number of data characters ({dataCharacters}) cannot be 1 more than a multiple of 4"
                : "Incorrect padding");
        }

        return [.. output];
    }

    private static string EncodeBase32(byte[] data)
    {
        var builder = new StringBuilder();
        var buffer = 0;
        var bits = 0;

        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;

            while (bits >= 5)
            {
                builder.Append(Base32[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            builder.Append(Base32[(buffer << (5 - bits)) & 31]);
        }

        while (builder.Length % 8 != 0)
        {
            builder.Append('=');
        }

        return builder.ToString();
    }

    private static byte[] DecodeBase32(string text, bool casefold)
    {
        if (casefold)
        {
            text = text.ToUpperInvariant();
        }

        if (text.Length % 8 != 0)
        {
            throw Fail("Incorrect padding");
        }

        var trimmed = text.TrimEnd('=');
        var output = new List<byte>();
        var buffer = 0;
        var bits = 0;

        foreach (var c in trimmed)
        {
            var digit = Base32.IndexOf(c, StringComparison.Ordinal);

            if (digit < 0)
            {
                throw Fail("Non-base32 digit found");
            }

            buffer = (buffer << 5) | digit;
            bits += 5;

            if (bits >= 8)
            {
                output.Add((byte)((buffer >> (bits - 8)) & 0xff));
                bits -= 8;
            }
        }

        return [.. output];
    }

    private static uint Crc32(byte[] data, uint crc)
    {
        crc = ~crc;

        foreach (var b in data)
        {
            crc ^= b;

            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320u : crc >> 1;
            }
        }

        return ~crc;
    }
}

/// <summary>The <c>uuid</c> module.</summary>
/// <remarks>
/// Random UUIDs draw on the operating system's entropy, as <c>random.SystemRandom</c> does;
/// name-based ones (<c>uuid3</c>, <c>uuid5</c>) are pure functions of their inputs.
/// <c>uuid1</c> is absent: it embeds the host's MAC address, which the sandbox has no business
/// reading.
/// </remarks>
public static class UuidModule
{
    /// <summary>Builds the module.</summary>
    public static PyModuleObject Create()
    {
        var module = new PyModuleObject("uuid");

        var type = new PyType(
            "uuid.UUID",
            static value => value is PyUuid,
            static (arguments, keywords) =>
            {
                string? hex = arguments.Length > 0 ? arguments[0].Display() : null;
                byte[]? bytes = null;
                BigInteger? integer = null;
                int? version = null;

                foreach (var (key, value) in keywords?.Entries ?? [])
                {
                    switch (key.Display())
                    {
                        case "hex": hex = value.Display(); break;
                        case "bytes": bytes = value is PyBytes b ? b.Value : throw new PyRaise(PyErrors.TypeError("a bytes-like object is required")); break;
                        case "int": integer = value is PyInt i ? i.Value : throw new PyRaise(PyErrors.TypeError("'int' expected")); break;
                        case "version": version = value is PyInt v ? (int)v.Value : null; break;
                        default:
                            throw new PyRaise(PyErrors.TypeError($"UUID.__init__() got an unexpected keyword argument '{key.Display()}'"));
                    }
                }

                if ((hex is null ? 0 : 1) + (bytes is null ? 0 : 1) + (integer is null ? 0 : 1) != 1)
                {
                    throw new PyRaise(PyErrors.TypeError("one of the hex, bytes, bytes_le, fields, or int arguments must be given"));
                }

                BigInteger value2;

                if (hex is not null)
                {
                    var digits = hex.Replace("urn:", string.Empty, StringComparison.Ordinal).Replace("uuid:", string.Empty, StringComparison.Ordinal).Trim('{', '}').Replace("-", string.Empty, StringComparison.Ordinal);

                    if (digits.Length != 32 || !digits.All(Uri.IsHexDigit))
                    {
                        throw new PyRaise(PyErrors.ValueError("badly formed hexadecimal UUID string"));
                    }

                    value2 = BigInteger.Parse("0" + digits, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
                }
                else if (bytes is not null)
                {
                    value2 = bytes.Length == 16
                        ? new BigInteger(bytes, isUnsigned: true, isBigEndian: true)
                        : throw new PyRaise(PyErrors.ValueError("bytes is not a 16-char string"));
                }
                else
                {
                    value2 = integer!.Value >= 0 && integer.Value < BigInteger.One << 128
                        ? integer.Value
                        : throw new PyRaise(PyErrors.ValueError("int is out of range (need a 128-bit value)"));
                }

                if (version is { } given)
                {
                    if (given is < 1 or > 5)
                    {
                        throw new PyRaise(PyErrors.ValueError("illegal version number"));
                    }

                    // Variant RFC 4122 (the top two bits of byte 8 are 10), then the version.
                    var cleared = value2 & ~((BigInteger)3 << 62) & ~((BigInteger)0xf << 76);
                    value2 = cleared | ((BigInteger)2 << 62) | ((BigInteger)given << 76);
                }

                return new PyUuid(value2);
            });

        module.Add("UUID", type);

        module.Add("uuid4", new PyBuiltinFunction("uuid4", arguments =>
        {
            Arity.Exact("uuid4", arguments, 0);
            return Versioned(RandomNumberGenerator.GetBytes(16), 4);
        }));

        module.Add("uuid3", new PyBuiltinFunction("uuid3", arguments => Named("uuid3", arguments, 3)));
        module.Add("uuid5", new PyBuiltinFunction("uuid5", arguments => Named("uuid5", arguments, 5)));

        module.Add("NAMESPACE_DNS", new PyUuid(Parse("6ba7b810-9dad-11d1-80b4-00c04fd430c8")));
        module.Add("NAMESPACE_URL", new PyUuid(Parse("6ba7b811-9dad-11d1-80b4-00c04fd430c8")));
        module.Add("NAMESPACE_OID", new PyUuid(Parse("6ba7b812-9dad-11d1-80b4-00c04fd430c8")));
        module.Add("NAMESPACE_X500", new PyUuid(Parse("6ba7b814-9dad-11d1-80b4-00c04fd430c8")));
        module.Add("NIL", new PyUuid(BigInteger.Zero));
        module.Add("MAX", new PyUuid((BigInteger.One << 128) - 1));
        module.Add("RESERVED_NCS", new PyStr("reserved for NCS compatibility"));
        module.Add("RFC_4122", new PyStr("specified in RFC 4122"));
        module.Add("RESERVED_MICROSOFT", new PyStr("reserved for Microsoft compatibility"));
        module.Add("RESERVED_FUTURE", new PyStr("reserved for future definition"));

        return module;
    }

    private static BigInteger Parse(string text) =>
        BigInteger.Parse("0" + text.Replace("-", string.Empty, StringComparison.Ordinal), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);

    private static PyUuid Versioned(byte[] hash, int version)
    {
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0f) | (version << 4));
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new PyUuid(new BigInteger(bytes, isUnsigned: true, isBigEndian: true));
    }

    private static PyObject Named(string function, PyObject[] arguments, int version)
    {
        Arity.Exact(function, arguments, 2);

        var space = arguments[0] as PyUuid ?? throw new PyRaise(PyErrors.AttributeError(arguments[0].TypeName, "bytes"));
        var name = arguments[1] switch
        {
            PyStr text => Encoding.UTF8.GetBytes(text.Value),
            PyBytes bytes => bytes.Value,
            _ => throw new PyRaise(PyErrors.TypeError($"name must be str or bytes, not {arguments[1].TypeName}")),
        };

        byte[] data = [.. space.ToBytes(), .. name];

        return Versioned(
            version == 3 ? MD5.HashData(data) : SHA1.HashData(data),
            version);
    }

    /// <summary>A UUID value.</summary>
    private sealed class PyUuid(BigInteger value) : PyObject
    {
        public BigInteger Value { get; } = value;

        public override string TypeName => "UUID";

        public byte[] ToBytes()
        {
            var written = Value.ToByteArray(isUnsigned: true, isBigEndian: true);
            var bytes = new byte[16];
            written.CopyTo(bytes, 16 - written.Length);
            return bytes;
        }

        private string Hex() => Convert.ToHexStringLower(ToBytes());

        public override string Display()
        {
            var hex = Hex();
            return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
        }

        public override string Repr() => $"UUID('{Display()}')";

        public override bool PyEquals(PyObject other) => other is PyUuid uuid && uuid.Value == Value;

        public override BigInteger PyHash() => Numbers.Hash(Value);

        public override int? PyCompare(PyObject other) => other is PyUuid uuid ? Value.CompareTo(uuid.Value) : null;

        public override PyObject? GetAttribute(string name) => name switch
        {
            "hex" => new PyStr(Hex()),
            "int" => PyInt.From(Value),
            "bytes" => new PyBytes(ToBytes()),
            "urn" => new PyStr("urn:uuid:" + Display()),
            "version" => ((Value >> 76) & 0xf) is var version && (Value >> 62 & 3) == 2 ? PyInt.From(version) : PyNone.Instance,
            "variant" => new PyStr((Value >> 63 & 1) == 0
                ? "reserved for NCS compatibility"
                : (Value >> 62 & 1) == 0 ? "specified in RFC 4122"
                : (Value >> 61 & 1) == 0 ? "reserved for Microsoft compatibility" : "reserved for future definition"),
            "is_safe" => PyNone.Instance,
            _ => null,
        };
    }
}
