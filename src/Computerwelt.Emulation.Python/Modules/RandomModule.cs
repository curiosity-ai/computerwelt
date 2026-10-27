using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules;

/// <summary>The <c>random</c> module.</summary>
/// <remarks>
/// <para>
/// A port of CPython's, down to the generator: a Mersenne Twister seeded the way
/// <c>_randommodule.c</c> seeds it, with the algorithms of <c>random.py</c> layered on top in
/// the same order they draw. That is the whole reason to write it out instead of reaching
/// for <see cref="System.Random"/> — <c>random.seed(42); random.random()</c> yields the same
/// number here as anywhere else, and so does every <c>shuffle</c>, <c>sample</c> and
/// <c>choice</c> that follows, which is what a script that prints its result depends on.
/// </para>
/// <para>
/// Each run builds its own module, so the module-level functions share one generator per
/// run and no state is shared between runs. Unseeded, it starts from the operating system's
/// entropy, as CPython's does. <c>Random</c> and <c>SystemRandom</c> are constructible but not
/// subclassable, because classes in this interpreter have no bases.
/// </para>
/// </remarks>
public static class RandomModule
{
    /// <summary>Builds the module.</summary>
    public static PyModuleObject Create()
    {
        var module = new PyModuleObject("random");
        var shared = new PyRandom(system: false);
        shared.Seed(PyNone.Instance);

        var randomType = new PyType(
            "random.Random",
            static value => value is PyRandom,
            static (arguments, keywords) =>
            {
                PyBuiltinFunction.RejectKeywords("Random", keywords);
                Arity.Between("Random", arguments, 0, 1);

                var instance = new PyRandom(system: false);
                instance.Seed(arguments.Length > 0 ? arguments[0] : PyNone.Instance);
                return instance;
            });

        var systemType = new PyType(
            "random.SystemRandom",
            static value => value is PyRandom { IsSystem: true },
            static (arguments, keywords) =>
            {
                _ = arguments;
                _ = keywords;
                return new PyRandom(system: true);
            });

        module.Add("Random", randomType);
        module.Add("SystemRandom", systemType);

        foreach (var name in PyRandom.Methods)
        {
            module.Add(name, shared.GetAttribute(name)!);
        }

        module.Add("__all__", new PyList([.. new[] { "Random", "SystemRandom" }.Concat(PyRandom.Methods).Select(static n => (PyObject)new PyStr(n))]));

        return module;
    }
}

/// <summary>
/// A generator: <c>random.Random</c>, or <c>random.SystemRandom</c> when it draws on the
/// operating system instead.
/// </summary>
internal sealed class PyRandom(bool system) : PyObject
{
    private const int N = 624;
    private const int M = 397;
    private const double TwoPi = 2.0 * Math.PI;
    private const double Recip = 1.0 / 9007199254740992.0;

    // The most bits (or bytes times eight) one call may ask for, and the most items one call
    // may build. Drawing is linear, but nothing else bounds how large a number a script may
    // request, and the instruction limit does not count work done in here.
    private const int MaxBits = 1 << 27;
    private const int MaxItems = 1 << 24;

    /// <summary>The method names, in the order <c>random.py</c> defines them.</summary>
    public static readonly string[] Methods =
    [
        "seed", "getstate", "setstate", "random", "getrandbits", "randbytes", "randrange", "randint",
        "choice", "shuffle", "sample", "choices", "uniform", "triangular", "binomialvariate",
        "normalvariate", "gauss", "lognormvariate", "expovariate", "vonmisesvariate", "gammavariate",
        "betavariate", "paretovariate", "weibullvariate",
    ];

    private readonly uint[] _mt = new uint[N];
    private int _index = N + 1;
    private PyObject _gaussNext = PyNone.Instance;

    public bool IsSystem { get; } = system;

    public override string TypeName => IsSystem ? "SystemRandom" : "Random";

    public override string Repr() => $"<random.{TypeName} object>";

    public override BigInteger PyHash() =>
        System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);

    public override PyObject? GetAttribute(string name) =>
        name == "VERSION" ? PyInt.From(3)
        : Methods.Contains(name, StringComparer.Ordinal)
            ? new PyBuiltinFunction($"Random.{name}", (arguments, keywords) => Call(name, arguments, keywords))
            : null;

    // ---- the Mersenne Twister ----

    private void InitGenrand(uint seed)
    {
        _mt[0] = seed;

        for (var i = 1; i < N; i++)
        {
            _mt[i] = (1812433253u * (_mt[i - 1] ^ (_mt[i - 1] >> 30))) + (uint)i;
        }

        _index = N;
    }

    private void InitByArray(uint[] key)
    {
        InitGenrand(19650218u);

        var i = 1;
        var j = 0;

        for (var k = Math.Max(N, key.Length); k > 0; k--)
        {
            _mt[i] = (_mt[i] ^ ((_mt[i - 1] ^ (_mt[i - 1] >> 30)) * 1664525u)) + key[j] + (uint)j;
            i++;
            j++;

            if (i >= N)
            {
                _mt[0] = _mt[N - 1];
                i = 1;
            }

            if (j >= key.Length)
            {
                j = 0;
            }
        }

        for (var k = N - 1; k > 0; k--)
        {
            _mt[i] = (_mt[i] ^ ((_mt[i - 1] ^ (_mt[i - 1] >> 30)) * 1566083941u)) - (uint)i;
            i++;

            if (i >= N)
            {
                _mt[0] = _mt[N - 1];
                i = 1;
            }
        }

        _mt[0] = 0x80000000u;
    }

    private uint GenrandUint32()
    {
        if (_index >= N)
        {
            Generate();
        }

        var y = _mt[_index++];
        y ^= y >> 11;
        y ^= (y << 7) & 0x9d2c5680u;
        y ^= (y << 15) & 0xefc60000u;
        y ^= y >> 18;
        return y;
    }

    private void Generate()
    {
        static uint Mix(uint u, uint v) => (u & 0x80000000u) | (v & 0x7fffffffu);
        static uint Twist(uint u, uint v) => (Mix(u, v) >> 1) ^ ((v & 1u) != 0 ? 0x9908b0dfu : 0u);

        int k;

        for (k = 0; k < N - M; k++)
        {
            _mt[k] = _mt[k + M] ^ Twist(_mt[k], _mt[k + 1]);
        }

        for (; k < N - 1; k++)
        {
            _mt[k] = _mt[k + (M - N)] ^ Twist(_mt[k], _mt[k + 1]);
        }

        _mt[N - 1] = _mt[M - 1] ^ Twist(_mt[N - 1], _mt[0]);
        _index = 0;
    }

    // ---- the primitives ----

    private double NextDouble()
    {
        if (IsSystem)
        {
            Span<byte> bytes = stackalloc byte[8];
            bytes.Clear();
            RandomNumberGenerator.Fill(bytes[..7]);
            return (BitConverter.ToUInt64(bytes) >> 3) * Recip;
        }

        var a = GenrandUint32() >> 5;
        var b = GenrandUint32() >> 6;
        return ((a * 67108864.0) + b) * Recip;
    }

    private BigInteger GetRandBits(BigInteger bits)
    {
        if (bits < 0)
        {
            throw new PyRaise(PyErrors.ValueError("number of bits must be non-negative"));
        }

        if (bits > MaxBits)
        {
            throw new PyRaise(new PyException(PyExceptionType.MemoryError, string.Empty));
        }

        var k = (int)bits;

        if (k == 0)
        {
            return BigInteger.Zero;
        }

        if (IsSystem)
        {
            var count = (k + 7) / 8;
            var bytes = RandomNumberGenerator.GetBytes(count);
            return new BigInteger(bytes, isUnsigned: true, isBigEndian: true) >> ((count * 8) - k);
        }

        if (k <= 32)
        {
            return GenrandUint32() >> (32 - k);
        }

        var words = ((k - 1) / 32) + 1;
        var buffer = new byte[(words * 4) + 1];

        for (var i = 0; i < words; i++, k -= 32)
        {
            var r = GenrandUint32();

            if (k < 32)
            {
                r >>= 32 - k;
            }

            BitConverter.TryWriteBytes(buffer.AsSpan(i * 4), r);
        }

        return new BigInteger(buffer, isUnsigned: true);
    }

    /// <summary>A uniform integer in <c>[0, n)</c>, drawing bits until one fits, as CPython does.</summary>
    private BigInteger RandBelow(BigInteger n)
    {
        var k = n.IsZero ? 0 : (long)n.GetBitLength();
        var r = GetRandBits(k);

        while (r >= n)
        {
            r = GetRandBits(k);
        }

        return r;
    }

    // ---- seeding and state ----

    public void Seed(PyObject seed, int version = 2)
    {
        _gaussNext = PyNone.Instance;

        if (IsSystem)
        {
            return;
        }

        // Version 1 hashed text the way Python 2 did; it is kept because a script that
        // asks for it is asking for the sequence it used to get.
        if (version == 1 && seed is PyStr or PyBytes)
        {
            var text = seed is PyBytes raw ? Encoding.Latin1.GetString(raw.Value) : ((PyStr)seed).Value;
            var x = text.Length > 0 ? (ulong)text[0] << 7 : 0UL;

            foreach (var c in text)
            {
                x = ((1000003UL * x) ^ c) & 0xFFFFFFFFFFFFFFFFUL;
            }

            x ^= (ulong)text.Length;
            InitByArray(Words(x == ulong.MaxValue ? 2 : x));
            return;
        }

        switch (seed)
        {
            case PyNone:
                InitByArray(Words(new BigInteger(RandomNumberGenerator.GetBytes(32), isUnsigned: true)));
                return;

            case PyInt integer:
                InitByArray(Words(BigInteger.Abs(integer.Value)));
                return;

            case PyStr text:
                InitByArray(Words(Digest(Encoding.UTF8.GetBytes(text.Value))));
                return;

            case PyBytes bytes:
                InitByArray(Words(Digest(bytes.Value)));
                return;

            case PyFloat number:
            {
                // A float seeds through its hash, taken as the unsigned 64-bit value the C
                // code reads it as.
                var hash = Numbers.Hash(number.Value);
                InitByArray(Words(hash < 0 ? hash + (BigInteger.One << 64) : hash));
                return;
            }

            default:
                throw new PyRaise(PyErrors.TypeError(
                    "The only supported seed types are:\nNone, int, float, str, bytes, and bytearray."));
        }
    }

    /// <summary>The seed a <c>str</c> or <c>bytes</c> argument stands for: the data followed by its SHA-512, as one integer.</summary>
    private static BigInteger Digest(byte[] data)
    {
        var joined = new byte[data.Length + 64];
        data.CopyTo(joined, 0);
        SHA512.HashData(data).CopyTo(joined, data.Length);
        return new BigInteger(joined, isUnsigned: true, isBigEndian: true);
    }

    /// <summary>The little-endian 32-bit words of a non-negative integer, at least one.</summary>
    private static uint[] Words(BigInteger value)
    {
        if (value.IsZero)
        {
            return [0];
        }

        var bytes = value.ToByteArray(isUnsigned: true);
        var words = new uint[(bytes.Length + 3) / 4];

        for (var i = 0; i < words.Length; i++)
        {
            uint word = 0;

            for (var b = 3; b >= 0; b--)
            {
                var at = (i * 4) + b;
                word = (word << 8) | (at < bytes.Length ? bytes[at] : (uint)0);
            }

            words[i] = word;
        }

        return words;
    }

    private PyObject GetState()
    {
        if (IsSystem)
        {
            throw new PyRaise(new PyException(
                PyExceptionType.NotImplementedError, "System entropy source does not have state."));
        }

        if (_index > N)
        {
            // Never seeded or loaded — only reachable for a generator made without a seed.
            Seed(PyNone.Instance);
        }

        var words = new List<PyObject>(N + 1);

        foreach (var word in _mt)
        {
            words.Add(PyInt.From(word));
        }

        words.Add(PyInt.From(_index));
        return new PyTuple([PyInt.From(3), new PyTuple(words), _gaussNext]);
    }

    private void SetState(PyObject state)
    {
        if (IsSystem)
        {
            throw new PyRaise(new PyException(
                PyExceptionType.NotImplementedError, "System entropy source does not have state."));
        }

        if (state is not PyTuple { Items.Count: > 0 } outer || outer.Items[0] is not PyInt versionNumber)
        {
            throw new PyRaise(PyErrors.TypeError("state must be a tuple of (version, internalstate, gauss_next)"));
        }

        var version = (int)versionNumber.Value;

        if (version is not (2 or 3))
        {
            throw new PyRaise(PyErrors.ValueError(
                $"state with version {versionNumber.Value} passed to Random.setstate() of version 3"));
        }

        if (outer.Items.Count != 3)
        {
            throw new PyRaise(PyErrors.ValueError(
                $"{(outer.Items.Count < 3 ? "not enough" : "too many")} values to unpack (expected 3, got {outer.Items.Count})"));
        }

        if (outer.Items[1] is not PyTuple { Items.Count: var count } internalState)
        {
            throw new PyRaise(PyErrors.TypeError("state vector must be a tuple"));
        }

        if (count != N + 1)
        {
            throw new PyRaise(PyErrors.ValueError("state vector is the wrong size"));
        }

        var words = new uint[N];
        var modulus = BigInteger.One << 32;

        for (var i = 0; i < N; i++)
        {
            if (internalState.Items[i] is not PyInt word)
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"'{internalState.Items[i].TypeName}' object cannot be interpreted as an integer"));
            }

            // Version 2 saved the words signed; version 3 insists they are not.
            var value = version == 2 ? ((word.Value % modulus) + modulus) % modulus : word.Value;

            if (value < 0 || value >= modulus)
            {
                throw new PyRaise(new PyException(
                    PyExceptionType.OverflowError, "Python int too large to convert to C unsigned long"));
            }

            words[i] = (uint)value;
        }

        if (internalState.Items[N] is not PyInt position || position.Value < 0 || position.Value > N)
        {
            throw new PyRaise(PyErrors.ValueError("invalid state"));
        }

        words.CopyTo(_mt, 0);
        _index = (int)position.Value;
        _gaussNext = outer.Items[2];
    }

    // ---- the methods ----

    private PyObject Call(string name, PyObject[] arguments, PyDict? keywords)
    {
        switch (name)
        {
            case "seed":
            {
                var args = Bind(name, arguments, keywords, 2, "a", "version");

                Seed(args[0] ?? PyNone.Instance, args[1] is PyInt version && version.Value == 1 ? 1 : 2);
                return PyNone.Instance;
            }

            case "getstate":
                Bind(name, arguments, keywords, 0);
                return GetState();

            case "setstate":
                SetState(Bind(name, arguments, keywords, 1, "state")[0]!);
                return PyNone.Instance;

            case "random":
                Bind(name, arguments, keywords, 0);
                return new PyFloat(NextDouble());

            case "getrandbits":
                return Integer(GetRandBits(Index(Bind(name, arguments, keywords, 1, "k")[0]!)));

            case "randbytes":
            {
                var count = Index(Bind(name, arguments, keywords, 1, "n")[0]!);

                if (count < 0)
                {
                    throw new PyRaise(PyErrors.ValueError("number of bits must be non-negative"));
                }

                if (count > MaxBits / 8)
                {
                    throw new PyRaise(new PyException(PyExceptionType.MemoryError, string.Empty));
                }

                var bytes = new byte[(int)count];
                var bits = GetRandBits(count * 8);
                bits.TryWriteBytes(bytes, out _, isUnsigned: true);
                return new PyBytes(bytes);
            }

            case "randrange":
                return RandRange(Bind(name, arguments, keywords, 3, "start", "stop", "step"));

            case "randint":
            {
                var args = Bind(name, arguments, keywords, 2, "a", "b");
                return Integer(Index(args[0]!) + RandBelow(Index(args[1]!) + 1 - Index(args[0]!)));
            }

            case "choice":
            {
                var sequence = Bind(name, arguments, keywords, 1, "seq")[0]!;
                var length = Len(sequence);

                if (length == 0)
                {
                    throw new PyRaise(PyErrors.IndexError("Cannot choose from an empty sequence"));
                }

                return sequence.GetItem(Integer(RandBelow(length)));
            }

            case "shuffle":
            {
                var target = Bind(name, arguments, keywords, 1, "x")[0]!;
                var length = Len(target);

                for (var i = length - 1; i >= 1; i--)
                {
                    var j = (int)RandBelow(i + 1);
                    var (a, b) = (PyInt.From(i), PyInt.From(j));
                    var (left, right) = (target.GetItem(a), target.GetItem(b));
                    target.SetItem(a, right);
                    target.SetItem(b, left);
                }

                return PyNone.Instance;
            }

            case "sample":
                return Sample(Bind(name, arguments, keywords, 2, "population", "k", "counts"));

            case "choices":
                return Choices(Bind(name, arguments, keywords, 2, "population", "weights", "cum_weights", "k"), arguments.Length);

            case "uniform":
            {
                var args = Bind(name, arguments, keywords, 2, "a", "b");
                var (a, b) = (Real(args[0]!), Real(args[1]!));
                return new PyFloat(a + ((b - a) * NextDouble()));
            }

            case "triangular":
                return Triangular(Bind(name, arguments, keywords, 3, "low", "high", "mode"));

            case "binomialvariate":
                return Binomial(Bind(name, arguments, keywords, 2, "n", "p"));

            case "normalvariate":
            {
                var args = Bind(name, arguments, keywords, 2, "mu", "sigma");
                return new PyFloat(NormalVariate(Real(args[0], 0.0), Real(args[1], 1.0)));
            }

            case "gauss":
            {
                var args = Bind(name, arguments, keywords, 2, "mu", "sigma");
                return new PyFloat(Gauss(Real(args[0], 0.0), Real(args[1], 1.0)));
            }

            case "lognormvariate":
            {
                var args = Bind(name, arguments, keywords, 2, "mu", "sigma");
                return new PyFloat(Math.Exp(NormalVariate(Real(args[0]!), Real(args[1]!))));
            }

            case "expovariate":
            {
                var lambda = Real(Bind(name, arguments, keywords, 1, "lambd")[0], 1.0);
                return new PyFloat(-Math.Log(1.0 - NextDouble()) / lambda);
            }

            case "vonmisesvariate":
            {
                var args = Bind(name, arguments, keywords, 2, "mu", "kappa");
                return new PyFloat(VonMises(Real(args[0]!), Real(args[1]!)));
            }

            case "gammavariate":
            {
                var args = Bind(name, arguments, keywords, 2, "alpha", "beta");
                return new PyFloat(Gamma(Real(args[0]!), Real(args[1]!)));
            }

            case "betavariate":
            {
                var args = Bind(name, arguments, keywords, 2, "alpha", "beta");
                var (alpha, beta) = (Real(args[0]!), Real(args[1]!));
                var y = Gamma(alpha, 1.0);
                return new PyFloat(y != 0 ? y / (y + Gamma(beta, 1.0)) : 0.0);
            }

            case "paretovariate":
            {
                var alpha = Real(Bind(name, arguments, keywords, 1, "alpha")[0]!);
                return new PyFloat(Math.Pow(1.0 - NextDouble(), -1.0 / alpha));
            }

            case "weibullvariate":
            {
                var args = Bind(name, arguments, keywords, 2, "alpha", "beta");
                var (alpha, beta) = (Real(args[0]!), Real(args[1]!));
                return new PyFloat(alpha * Math.Pow(-Math.Log(1.0 - NextDouble()), 1.0 / beta));
            }

            default:
                throw new PyRaise(PyErrors.AttributeError(TypeName, name));
        }
    }

    private PyObject RandRange(PyObject?[] args)
    {
        var start = Index(args[0] ?? throw Missing("randrange", "start"));

        if (args[1] is null or PyNone)
        {
            if (args[2] is { } and not PyInt { Value.IsOne: true })
            {
                throw new PyRaise(PyErrors.TypeError("Missing a non-None stop argument"));
            }

            return start > 0
                ? Integer(RandBelow(start))
                : throw new PyRaise(PyErrors.ValueError("empty range for randrange()"));
        }

        var stop = Index(args[1]!);
        var width = stop - start;
        var step = args[2] is { } s ? Index(s) : BigInteger.One;

        if (step.IsOne)
        {
            return width > 0
                ? Integer(start + RandBelow(width))
                : throw new PyRaise(PyErrors.ValueError($"empty range in randrange({start}, {stop})"));
        }

        BigInteger count;

        if (step > 0)
        {
            count = FloorDiv(width + step - 1, step);
        }
        else if (step < 0)
        {
            count = FloorDiv(width + step + 1, step);
        }
        else
        {
            throw new PyRaise(PyErrors.ValueError("zero step for randrange()"));
        }

        return count > 0
            ? Integer(start + (step * RandBelow(count)))
            : throw new PyRaise(PyErrors.ValueError($"empty range in randrange({start}, {stop}, {step})"));
    }

    private PyObject Sample(PyObject?[] args)
    {
        var population = args[0]!;

        if (population is not (PyList or PyTuple or PyStr or PyRange or PyBytes or PyDeque))
        {
            throw new PyRaise(PyErrors.TypeError(
                "Population must be a sequence.  For dicts or sets, use sorted(d)."));
        }

        var n = Len(population);

        if (args[2] is { } counts and not PyNone)
        {
            var cumulative = new List<BigInteger>();
            var total = BigInteger.Zero;

            foreach (var count in counts.Iterate() ?? throw NotIterable(counts))
            {
                total += count is PyInt integer
                    ? integer.Value
                    : throw new PyRaise(PyErrors.TypeError("Counts must be integers"));
                cumulative.Add(total);
            }

            if (cumulative.Count != n)
            {
                throw new PyRaise(PyErrors.ValueError("The number of counts does not match the population"));
            }

            if (total <= 0)
            {
                throw new PyRaise(PyErrors.ValueError("Total of counts must be greater than zero"));
            }

            cumulative.RemoveAt(cumulative.Count - 1);

            var picks = Sample(new PyRange(0, total, 1), args[1] ?? throw Missing("sample", "k"));

            return new PyList([.. picks.Items.Select(pick =>
                population.GetItem(PyInt.From(UpperBound(cumulative, ((PyInt)pick).Value))))]);
        }

        return new PyList(Sample(population, args[1] ?? throw Missing("sample", "k")).Items);
    }

    private PyList Sample(PyObject population, PyObject kObject)
    {
        var n = Len(population);
        var k = Index(kObject);

        if (k < 0 || k > n)
        {
            throw new PyRaise(PyErrors.ValueError("Sample larger than population or is negative"));
        }

        if (k > MaxItems)
        {
            throw new PyRaise(new PyException(PyExceptionType.MemoryError, string.Empty));
        }

        var size = (int)k;
        var result = new PyObject[size];
        long setSize = 21;

        if (size > 5)
        {
            setSize += (long)Math.Pow(4, Math.Ceiling(Math.Log(size * 3.0) / Math.Log(4)));
        }

        if (n <= setSize)
        {
            // An n-length list is smaller than a k-length set.
            var pool = (population.Iterate() ?? throw NotIterable(population)).ToList();

            for (var i = 0; i < size; i++)
            {
                var j = (int)RandBelow(n - i);
                result[i] = pool[j];
                pool[j] = pool[n - i - 1];
            }
        }
        else
        {
            var selected = new HashSet<int>();

            for (var i = 0; i < size; i++)
            {
                var j = (int)RandBelow(n);

                while (selected.Contains(j))
                {
                    j = (int)RandBelow(n);
                }

                selected.Add(j);
                result[i] = population.GetItem(PyInt.From(j));
            }
        }

        return new PyList([.. result]);
    }

    private PyObject Choices(PyObject?[] args, int positional)
    {
        _ = positional;

        var population = args[0]!;
        var weights = args[1] is PyNone ? null : args[1];
        var cumulativeWeights = args[2] is PyNone ? null : args[2];
        var k = args[3] is { } given ? Index(given) : 1;
        var n = Len(population);

        if (k > MaxItems)
        {
            throw new PyRaise(new PyException(PyExceptionType.MemoryError, string.Empty));
        }

        var result = new List<PyObject>((int)BigInteger.Max(0, k));

        if (cumulativeWeights is null)
        {
            if (weights is null)
            {
                for (var i = 0; i < k; i++)
                {
                    result.Add(population.GetItem(PyInt.From((long)Math.Floor(NextDouble() * n))));
                }

                return new PyList(result);
            }

            var running = 0.0;
            var sums = new List<double>();

            foreach (var weight in weights.Iterate() ?? throw NotIterable(weights))
            {
                running += Real(weight);
                sums.Add(running);
            }

            return Weighted(population, sums, k, n);
        }

        if (weights is not null)
        {
            throw new PyRaise(PyErrors.TypeError("Cannot specify both weights and cumulative weights"));
        }

        return Weighted(
            population,
            [.. (cumulativeWeights.Iterate() ?? throw NotIterable(cumulativeWeights)).Select(static weight => Real(weight))],
            k,
            n);
    }

    private PyObject Weighted(PyObject population, List<double> cumulative, BigInteger k, int n)
    {
        if (cumulative.Count != n)
        {
            throw new PyRaise(PyErrors.ValueError("The number of weights does not match the population"));
        }

        var total = cumulative.Count > 0 ? cumulative[^1] : throw new PyRaise(PyErrors.IndexError("list index out of range"));

        if (total <= 0.0)
        {
            throw new PyRaise(PyErrors.ValueError("Total of weights must be greater than zero"));
        }

        if (!double.IsFinite(total))
        {
            throw new PyRaise(PyErrors.ValueError("Total of weights must be finite"));
        }

        var result = new List<PyObject>();

        for (var i = 0; i < k; i++)
        {
            // bisect_right over everything but the last entry: the final weight can never
            // be the answer, and excluding it is what keeps a rounding-up draw in range.
            var x = NextDouble() * total;
            int lo = 0, hi = n - 1;

            while (lo < hi)
            {
                var mid = (lo + hi) / 2;

                if (x < cumulative[mid])
                {
                    hi = mid;
                }
                else
                {
                    lo = mid + 1;
                }
            }

            result.Add(population.GetItem(PyInt.From(lo)));
        }

        return new PyList(result);
    }

    private PyObject Triangular(PyObject?[] args)
    {
        var low = Real(args[0], 0.0);
        var high = Real(args[1], 1.0);
        var u = NextDouble();
        double c;

        if (args[2] is null or PyNone)
        {
            c = 0.5;
        }
        else if (high - low == 0)
        {
            return new PyFloat(low);
        }
        else
        {
            c = (Real(args[2]!) - low) / (high - low);
        }

        if (u > c)
        {
            u = 1.0 - u;
            c = 1.0 - c;
            (low, high) = (high, low);
        }

        return new PyFloat(low + ((high - low) * Math.Sqrt(u * c)));
    }

    private double NormalVariate(double mu, double sigma)
    {
        // Kinderman and Monahan's ratio-of-uniforms method, as CPython has it.
        double z;

        while (true)
        {
            var u1 = NextDouble();
            var u2 = 1.0 - NextDouble();
            z = 4.0 * Math.Exp(-0.5) / Math.Sqrt(2.0) * (u1 - 0.5) / u2;
            var zz = z * z / 4.0;

            if (zz <= -Math.Log(u2))
            {
                break;
            }
        }

        return mu + (z * sigma);
    }

    private double Gauss(double mu, double sigma)
    {
        double z;

        if (_gaussNext is PyFloat pending)
        {
            z = pending.Value;
            _gaussNext = PyNone.Instance;
        }
        else
        {
            var x2pi = NextDouble() * TwoPi;
            var g2rad = Math.Sqrt(-2.0 * Math.Log(1.0 - NextDouble()));
            z = Math.Cos(x2pi) * g2rad;
            _gaussNext = new PyFloat(Math.Sin(x2pi) * g2rad);
        }

        return mu + (z * sigma);
    }

    private double VonMises(double mu, double kappa)
    {
        if (kappa <= 1e-6)
        {
            return TwoPi * NextDouble();
        }

        var s = 0.5 / kappa;
        var r = s + Math.Sqrt(1.0 + (s * s));
        double z, d;

        while (true)
        {
            var u1 = NextDouble();
            z = Math.Cos(Math.PI * u1);
            d = z / (r + z);
            var u2 = NextDouble();

            if (u2 < 1.0 - (d * d) || u2 <= (1.0 - d) * Math.Exp(d))
            {
                break;
            }
        }

        var q = 1.0 / r;
        var f = (q + z) / (1.0 + (q * z));
        var u3 = NextDouble();

        return u3 > 0.5
            ? Mod(mu + Math.Acos(f), TwoPi)
            : Mod(mu - Math.Acos(f), TwoPi);
    }

    private double Gamma(double alpha, double beta)
    {
        if (alpha <= 0.0 || beta <= 0.0)
        {
            throw new PyRaise(PyErrors.ValueError("gammavariate: alpha and beta must be > 0.0"));
        }

        if (alpha > 1.0)
        {
            // Cheng's rejection method.
            var ainv = Math.Sqrt((2.0 * alpha) - 1.0);
            var bbb = alpha - Math.Log(4.0);
            var ccc = alpha + ainv;

            while (true)
            {
                var u1 = NextDouble();

                if (!(1e-7 < u1 && u1 < 0.9999999))
                {
                    continue;
                }

                var u2 = 1.0 - NextDouble();
                var v = Math.Log(u1 / (1.0 - u1)) / ainv;
                var x = alpha * Math.Exp(v);
                var z = u1 * u1 * u2;
                var r = bbb + (ccc * v) - x;

                if (r + (1.0 + Math.Log(4.5)) - (4.5 * z) >= 0.0 || r >= Math.Log(z))
                {
                    return x * beta;
                }
            }
        }

        if (alpha == 1.0)
        {
            return -Math.Log(1.0 - NextDouble()) * beta;
        }

        // Algorithm GS of Kennedy and Gentle, for a shape between zero and one.
        while (true)
        {
            var u = NextDouble();
            var b = (Math.E + alpha) / Math.E;
            var p = b * u;
            var x = p <= 1.0 ? Math.Pow(p, 1.0 / alpha) : -Math.Log((b - p) / alpha);
            var u1 = NextDouble();

            if (p > 1.0)
            {
                if (u1 <= Math.Pow(x, alpha - 1.0))
                {
                    return x * beta;
                }
            }
            else if (u1 <= Math.Exp(-x))
            {
                return x * beta;
            }
        }
    }

    private PyObject Binomial(PyObject?[] args)
    {
        var n = args[0] is { } given ? Index(given) : BigInteger.One;
        var p = Real(args[1], 0.5);

        return Integer(BinomialVariate(n, p));
    }

    private BigInteger BinomialVariate(BigInteger n, double p)
    {
        if (n < 0)
        {
            throw new PyRaise(PyErrors.ValueError("n must be non-negative"));
        }

        if (p <= 0.0 || p >= 1.0)
        {
            return p == 0.0 ? 0
                : p == 1.0 ? n
                : throw new PyRaise(PyErrors.ValueError("p must be in the range 0.0 <= p <= 1.0"));
        }

        if (n.IsOne)
        {
            return NextDouble() < p ? 1 : 0;
        }

        // Exploit symmetry to establish p <= 0.5.
        if (p > 0.5)
        {
            return n - BinomialVariate(n, 1.0 - p);
        }

        var np = (double)n * p;

        if (np < 10.0)
        {
            // Devroye's geometric method, with running time O(np).
            BigInteger x = 0, y = 0;
            var c = Math.Log2(1.0 - p);

            if (c == 0)
            {
                return x;
            }

            while (true)
            {
                var draw = NextDouble();

                if (draw == 0)
                {
                    throw new PyRaise(PyErrors.ValueError("math domain error"));
                }

                y += new BigInteger(Math.Floor(Math.Log2(draw) / c)) + 1;

                if (y > n)
                {
                    return x;
                }

                x++;
            }
        }

        // BTRS: Hörmann's transformed rejection with squeeze.
        var dn = (double)n;
        var spq = Math.Sqrt(dn * p * (1.0 - p));
        var b = 1.15 + (2.53 * spq);
        var a = -0.0873 + (0.0248 * b) + (0.01 * p);
        var cc = (dn * p) + 0.5;
        var vr = 0.92 - (4.2 / b);
        var setup = false;
        double alpha = 0, lpq = 0, m = 0, h = 0;

        while (true)
        {
            var u = NextDouble() - 0.5;
            var us = 0.5 - Math.Abs(u);
            var k = Math.Floor((((2.0 * a / us) + b) * u) + cc);

            if (k < 0 || k > dn)
            {
                continue;
            }

            var v = NextDouble();

            if (us >= 0.07 && v <= vr)
            {
                return new BigInteger(k);
            }

            if (!setup)
            {
                alpha = (2.83 + (5.1 / b)) * spq;
                lpq = Math.Log(p / (1.0 - p));
                m = Math.Floor((dn + 1) * p);
                h = MathModule.LogGamma(m + 1) + MathModule.LogGamma(dn - m + 1);
                setup = true;
            }

            v *= alpha / ((a / (us * us)) + b);

            if (Math.Log(v) <= h - MathModule.LogGamma(k + 1) - MathModule.LogGamma(dn - k + 1) + ((k - m) * lpq))
            {
                return new BigInteger(k);
            }
        }
    }

    // ---- helpers ----

    /// <summary>
    /// Binds a call to a parameter list: the first <paramref name="positional"/> names may be
    /// given by position, all of them by keyword.
    /// </summary>
    private static PyObject?[] Bind(string name, PyObject[] arguments, PyDict? keywords, int positional, params string[] parameters)
    {
        var bound = new PyObject?[parameters.Length];
        var required = Required(name);

        // The two methods written in C take nothing, and say so in C's words.
        if (name is "random" or "getstate" && (arguments.Length > 0 || keywords is { Count: > 0 }))
        {
            throw new PyRaise(PyErrors.TypeError(arguments.Length > 0
                ? $"Random.{name}() takes no arguments ({arguments.Length} given)"
                : $"Random.{name}() takes no keyword arguments"));
        }

        if (arguments.Length > positional)
        {
            // `self` counts, as it does in CPython's message.
            var (low, high) = (required + 1, positional + 1);
            var takes = low == high
                ? $"{high} positional argument{(high == 1 ? string.Empty : "s")}"
                : $"from {low} to {high} positional arguments";

            throw new PyRaise(PyErrors.TypeError(
                $"Random.{name}() takes {takes} but {arguments.Length + 1} {(arguments.Length + 1 == 1 ? "was" : "were")} given"));
        }

        arguments.CopyTo(bound, 0);

        foreach (var (key, value) in keywords?.Entries ?? [])
        {
            var at = Array.IndexOf(parameters, key.Display());

            if (at < 0)
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"Random.{name}() got an unexpected keyword argument '{key.Display()}'"));
            }

            if (bound[at] is not null)
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"Random.{name}() got multiple values for argument '{key.Display()}'"));
            }

            bound[at] = value;
        }

        for (var i = 0; i < required; i++)
        {
            if (bound[i] is null)
            {
                throw Missing(name, parameters[i]);
            }
        }

        return bound;
    }

    /// <summary>How many leading parameters a method cannot do without; the rest have defaults.</summary>
    private static int Required(string name) => name switch
    {
        "setstate" or "getrandbits" or "randbytes" or "choice" or "shuffle" or "paretovariate"
            or "randrange" or "choices" => 1,
        "randint" or "uniform" or "lognormvariate" or "vonmisesvariate" or "gammavariate"
            or "betavariate" or "weibullvariate" or "sample" => 2,
        _ => 0,
    };

    private static PyRaise Missing(string function, string parameter) =>
        new(PyErrors.TypeError($"Random.{function}() missing 1 required positional argument: '{parameter}'"));

    private static PyRaise NotIterable(PyObject value) =>
        new(PyErrors.TypeError($"'{value.TypeName}' object is not iterable"));

    private static PyObject Integer(BigInteger value) => PyInt.From(value);

    private static int Len(PyObject value) =>
        value.Length() ?? throw new PyRaise(PyErrors.TypeError($"object of type '{value.TypeName}' has no len()"));

    private static BigInteger Index(PyObject value) => value switch
    {
        PyInt integer => integer.Value,
        _ => throw new PyRaise(PyErrors.TypeError(
            $"'{value.TypeName}' object cannot be interpreted as an integer")),
    };

    private static double Real(PyObject? value, double fallback) =>
        value is null or PyNone ? fallback : Real(value);

    private static double Real(PyObject? value) => value switch
    {
        PyFloat number => number.Value,
        PyInt integer => ToDouble(integer.Value),
        _ => throw new PyRaise(PyErrors.TypeError(
            $"must be real number, not {value?.TypeName ?? "NoneType"}")),
    };

    private static double ToDouble(BigInteger value)
    {
        var result = (double)value;

        return double.IsInfinity(result)
            ? throw new PyRaise(new PyException(PyExceptionType.OverflowError, "int too large to convert to float"))
            : result;
    }

    private static BigInteger FloorDiv(BigInteger left, BigInteger right)
    {
        var quotient = BigInteger.DivRem(left, right, out var remainder);
        return !remainder.IsZero && (remainder.Sign != right.Sign) ? quotient - 1 : quotient;
    }

    /// <summary>Python's float <c>%</c>: the result takes the sign of the divisor.</summary>
    private static double Mod(double value, double divisor)
    {
        var result = value % divisor;
        return result != 0 && (divisor < 0) != (result < 0) ? result + divisor : result;
    }

    /// <summary><c>bisect_right</c> over cumulative counts.</summary>
    private static int UpperBound(List<BigInteger> cumulative, BigInteger x)
    {
        int lo = 0, hi = cumulative.Count;

        while (lo < hi)
        {
            var mid = (lo + hi) / 2;

            if (x < cumulative[mid])
            {
                hi = mid;
            }
            else
            {
                lo = mid + 1;
            }
        }

        return lo;
    }
}
