using System.Numerics;
using System.Security.Cryptography;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules;

/// <summary>
/// The <c>secrets</c> module: tokens and choices drawn from the host's cryptographic
/// generator, as CPython draws them from <c>os.urandom</c>.
/// </summary>
/// <remarks>
/// Like an unseeded <c>random</c>, this reads entropy from outside the sandbox — which is the
/// point of the module. A token is capped at 2^24 bytes, because one call is one builtin step
/// to the instruction limit however much it allocates.
/// </remarks>
public static class SecretsModule
{
    private const int DefaultEntropy = 32;

    private const int MaxBytes = 1 << 24;

    /// <summary>Builds the module.</summary>
    /// <param name="systemRandom">The <c>random.SystemRandom</c> class, which <c>secrets</c> re-exports.</param>
    public static PyModuleObject Create(PyObject? systemRandom)
    {
        var module = new PyModuleObject("secrets");

        module.Add("DEFAULT_ENTROPY", PyInt.From(DefaultEntropy));

        if (systemRandom is not null)
        {
            module.Add("SystemRandom", systemRandom);
        }

        module.Add("token_bytes", arguments => new PyBytes(Bytes(arguments, "token_bytes")));
        module.Add("token_hex", arguments => new PyStr(Convert.ToHexStringLower(Bytes(arguments, "token_hex"))));
        module.Add("token_urlsafe", arguments => new PyStr(Convert.ToBase64String(Bytes(arguments, "token_urlsafe"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_')));

        module.Add("randbelow", arguments =>
        {
            Arity.ExactCount("randbelow", arguments, 1);
            var bound = Builtins.BuiltinNamespace.RequireInt(arguments[0], "randbelow");

            return bound <= 0
                ? throw new PyRaise(PyErrors.ValueError("Upper bound must be positive."))
                : PyInt.From(Below(bound));
        });

        module.Add("randbits", arguments =>
        {
            Arity.ExactCount("randbits", arguments, 1);
            var bits = Builtins.BuiltinNamespace.RequireInt(arguments[0], "randbits");

            if (bits < 0)
            {
                throw new PyRaise(PyErrors.ValueError("number of bits must be non-negative"));
            }

            if (bits > MaxBytes * 8L)
            {
                throw new PyRaise(new PyException(PyExceptionType.MemoryError, string.Empty));
            }

            return PyInt.From(Bits((int)bits));
        });

        module.Add("choice", arguments =>
        {
            Arity.ExactCount("choice", arguments, 1);
            var sequence = arguments[0];
            var length = sequence.Length() ?? throw new PyRaise(PyErrors.TypeError(
                $"object of type '{sequence.TypeName}' has no len()"));

            return length == 0
                ? throw new PyRaise(PyErrors.IndexError("Cannot choose from an empty sequence"))
                : sequence.GetItem(PyInt.From(Below(length)));
        });

        module.Add("compare_digest", arguments =>
        {
            Arity.ExactCount("compare_digest", arguments, 2);

            byte[] Raw(PyObject value) => value switch
            {
                PyBytes bytes => bytes.Value,
                PyStr text when text.Value.All(static c => c < 128) => System.Text.Encoding.ASCII.GetBytes(text.Value),
                PyStr => throw new PyRaise(PyErrors.TypeError("comparing strings with non-ASCII characters is not supported")),
                _ => throw new PyRaise(PyErrors.TypeError(
                    $"unsupported operand types(s) or combination of types: '{arguments[0].TypeName}' and '{arguments[1].TypeName}'")),
            };

            if (arguments[0].GetType() != arguments[1].GetType())
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"unsupported operand types(s) or combination of types: '{arguments[0].TypeName}' and '{arguments[1].TypeName}'"));
            }

            return PyBool.Of(CryptographicOperations.FixedTimeEquals(Raw(arguments[0]), Raw(arguments[1])));
        });

        return module;
    }

    private static byte[] Bytes(PyObject[] arguments, string function)
    {
        Arity.Between(function, arguments, 0, 1);
        var count = arguments.Length == 0 || arguments[0] is PyNone
            ? DefaultEntropy
            : Builtins.BuiltinNamespace.RequireInt(arguments[0], function);

        if (count < 0)
        {
            throw new PyRaise(PyErrors.ValueError("negative argument not allowed"));
        }

        if (count > MaxBytes)
        {
            throw new PyRaise(new PyException(PyExceptionType.MemoryError, string.Empty));
        }

        return RandomNumberGenerator.GetBytes((int)count);
    }

    private static BigInteger Bits(int bits)
    {
        if (bits == 0)
        {
            return BigInteger.Zero;
        }

        var bytes = RandomNumberGenerator.GetBytes((bits + 7) / 8);
        var value = new BigInteger(bytes, isUnsigned: true);
        return value >> ((bytes.Length * 8) - bits);
    }

    /// <summary>A uniform integer in [0, bound), by rejection, as <c>_randbelow</c> draws it.</summary>
    private static BigInteger Below(BigInteger bound)
    {
        var bits = (int)bound.GetBitLength();

        while (true)
        {
            var candidate = Bits(bits);

            if (candidate < bound)
            {
                return candidate;
            }
        }
    }
}
