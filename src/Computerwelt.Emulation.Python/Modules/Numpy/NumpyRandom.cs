using System.Numerics;
using System.Security.Cryptography;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules.Numpy;

/// <summary>The Mersenne Twister, with numpy's legacy draws layered on it.</summary>
/// <remarks>
/// <para>
/// <c>np.random.seed(n)</c> seeds it as numpy's legacy <c>RandomState</c> does
/// (<c>init_genrand(n)</c>), and the draws below follow numpy's own C —
/// <c>random_standard_uniform</c> (53-bit), the polar Gaussian with its cached second
/// value, masked rejection for bounded integers, and <c>random_interval</c> for shuffles — so
/// <c>rand</c>, <c>randn</c>, <c>normal</c>, <c>uniform</c>, <c>randint</c>, <c>choice</c>,
/// <c>shuffle</c> and <c>permutation</c> reproduce a seeded numpy run. That is a property of
/// the port, not a promise the tests rely on beyond a few vectors.
/// </para>
/// <para>
/// <c>default_rng(seed)</c>'s <c>Generator</c> is <b>not</b> numpy's PCG64: it runs the same
/// algorithms over this generator (seeded from the seed's 32-bit words), so a seeded program
/// is deterministic here but does not print the numbers numpy prints.
/// </para>
/// </remarks>
internal sealed class MersenneTwister
{
    private const int N = 624;
    private const int M = 397;

    private readonly uint[] _mt = new uint[N];
    private int _index = N + 1;
    private double? _gauss;

    public void Seed(uint seed)
    {
        _mt[0] = seed;

        for (var i = 1; i < N; i++)
        {
            _mt[i] = (1812433253u * (_mt[i - 1] ^ (_mt[i - 1] >> 30))) + (uint)i;
        }

        _index = N;
        _gauss = null;
    }

    public void Seed(uint[] key)
    {
        Seed(19650218u);
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
        _gauss = null;
    }

    /// <summary>Seeds from the operating system, as an unseeded numpy generator does.</summary>
    public void SeedFromEntropy()
    {
        var key = new uint[N];
        var bytes = RandomNumberGenerator.GetBytes(N * 4);
        Buffer.BlockCopy(bytes, 0, key, 0, bytes.Length);
        Seed(key);
    }

    public uint NextUInt32()
    {
        if (_index >= N)
        {
            for (var k = 0; k < N; k++)
            {
                var y = (_mt[k] & 0x80000000u) | (_mt[(k + 1) % N] & 0x7fffffffu);
                _mt[k] = _mt[(k + M) % N] ^ (y >> 1) ^ ((y & 1) != 0 ? 0x9908b0dfu : 0u);
            }

            _index = 0;
        }

        var value = _mt[_index++];
        value ^= value >> 11;
        value ^= (value << 7) & 0x9d2c5680u;
        value ^= (value << 15) & 0xefc60000u;
        value ^= value >> 18;
        return value;
    }

    public ulong NextUInt64() => ((ulong)NextUInt32() << 32) | NextUInt32();

    /// <summary>A double in [0, 1) with 53 random bits.</summary>
    public double NextDouble()
    {
        var a = NextUInt32() >> 5;
        var b = NextUInt32() >> 6;
        return ((a * 67108864.0) + b) / 9007199254740992.0;
    }

    /// <summary>The polar method, keeping the second value for the next call.</summary>
    public double NextGaussian()
    {
        if (_gauss is { } cached)
        {
            _gauss = null;
            return cached;
        }

        double x1;
        double x2;
        double r2;

        do
        {
            x1 = (2.0 * NextDouble()) - 1.0;
            x2 = (2.0 * NextDouble()) - 1.0;
            r2 = (x1 * x1) + (x2 * x2);
        }
        while (r2 >= 1.0 || r2 == 0.0);

        var f = Math.Sqrt(-2.0 * Math.Log(r2) / r2);
        _gauss = f * x1;
        return f * x2;
    }

    private static ulong Mask(ulong max)
    {
        var mask = max;
        mask |= mask >> 1;
        mask |= mask >> 2;
        mask |= mask >> 4;
        mask |= mask >> 8;
        mask |= mask >> 16;
        mask |= mask >> 32;
        return mask;
    }

    /// <summary>A uniform integer in [0, <paramref name="max"/>], by masked rejection.</summary>
    public ulong Interval(ulong max)
    {
        if (max == 0)
        {
            return 0;
        }

        var mask = Mask(max);
        ulong value;

        if (max <= 0xffffffffUL)
        {
            while ((value = NextUInt32() & mask) > max)
            {
            }
        }
        else
        {
            while ((value = NextUInt64() & mask) > max)
            {
            }
        }

        return value;
    }

    /// <summary><c>random_bounded_uint64</c>: low + a uniform draw from [0, range].</summary>
    public long Bounded(long low, ulong range)
    {
        if (range == 0)
        {
            return low;
        }

        if (range == 0xffffffffUL)
        {
            return unchecked(low + NextUInt32());
        }

        if (range == ulong.MaxValue)
        {
            return unchecked(low + (long)NextUInt64());
        }

        return unchecked(low + (long)Interval(range));
    }

    public double Exponential() => -Math.Log(1.0 - NextDouble());

    /// <summary>numpy's Poisson: multiplication for small means, PTRS for large ones.</summary>
    public long Poisson(double lambda)
    {
        if (lambda >= 10)
        {
            var slam = Math.Sqrt(lambda);
            var loglam = Math.Log(lambda);
            var b = 0.931 + (2.53 * slam);
            var a = -0.059 + (0.02483 * b);
            var invalpha = 1.1239 + (1.1328 / (b - 3.4));
            var vr = 0.9277 - (3.6224 / (b - 2));

            while (true)
            {
                var u = NextDouble() - 0.5;
                var v = NextDouble();
                var us = 0.5 - Math.Abs(u);
                var k = (long)Math.Floor((((2 * a) / us) + b) * u + lambda + 0.43);

                if (us >= 0.07 && v <= vr)
                {
                    return k;
                }

                if (k < 0 || (us < 0.013 && v > us))
                {
                    continue;
                }

                if (Math.Log(v) + Math.Log(invalpha) - Math.Log((a / (us * us)) + b)
                    <= -lambda + (k * loglam) - LogGamma(k + 1))
                {
                    return k;
                }
            }
        }

        if (lambda == 0)
        {
            return 0;
        }

        var enlam = Math.Exp(-lambda);
        long x = 0;
        var product = 1.0;

        while (true)
        {
            product *= NextDouble();

            if (product > enlam)
            {
                x++;
            }
            else
            {
                return x;
            }
        }
    }

    private static double LogGamma(double x)
    {
        // Stirling's series with the recurrence for small arguments (numpy's random_loggam).
        double[] coefficients =
        [
            8.333333333333333e-02, -2.777777777777778e-03, 7.936507936507937e-04, -5.952380952380952e-04,
            8.417508417508418e-04, -1.917526917526918e-03, 6.410256410256410e-03, -2.955065359477124e-02,
            1.796443723688307e-01, -1.39243221690590e+00,
        ];

        if (x is 1.0 or 2.0)
        {
            return 0.0;
        }

        var n = x < 7.0 ? (int)(7 - x) : 0;
        var x0 = x + n;
        var x2 = 1.0 / (x0 * x0);
        var gl0 = coefficients[9];

        for (var k = 8; k >= 0; k--)
        {
            gl0 = (gl0 * x2) + coefficients[k];
        }

        var gl = (gl0 / x0) + (0.5 * Math.Log(2 * Math.PI)) + ((x0 - 0.5) * Math.Log(x0)) - x0;

        if (x < 7.0)
        {
            for (var k = 1; k <= n; k++)
            {
                gl -= Math.Log(x0 - 1.0);
                x0 -= 1.0;
            }
        }

        return gl;
    }

    /// <summary>A binomial draw by inversion (a normal approximation past a million expected successes).</summary>
    public long Binomial(long n, double p)
    {
        if (n == 0 || p == 0)
        {
            return 0;
        }

        if (p == 1)
        {
            return n;
        }

        var flipped = p > 0.5;
        var q = flipped ? 1 - p : p;
        long result;

        if (n * q > 1e6)
        {
            var mean = n * q;
            var deviation = Math.Sqrt(n * q * (1 - q));
            result = Math.Clamp((long)Math.Round(mean + (deviation * NextGaussian())), 0, n);
        }
        else
        {
            var qn = Math.Pow(1 - q, n);
            var ratio = q / (1 - q);
            var bound = Math.Min(n, (long)((n * q) + (10.0 * Math.Sqrt((n * q * (1 - q)) + 1))));

            while (true)
            {
                long x = 0;
                var px = qn;
                var u = NextDouble();

                while (u > px)
                {
                    x++;

                    if (x > bound)
                    {
                        break;
                    }

                    u -= px;
                    px = ((n - x + 1) * ratio * px) / x;
                }

                if (x <= bound)
                {
                    result = x;
                    break;
                }
            }
        }

        return flipped ? n - result : result;
    }
}

/// <summary>
/// A numpy generator: the legacy <c>RandomState</c> behind <c>np.random.*</c>, or a
/// <c>Generator</c> from <c>default_rng</c>, which differ in a few method names and
/// defaults.
/// </summary>
internal sealed class NpRandomState : PyObject
{
    private readonly MersenneTwister _twister = new();

    public NpRandomState(bool generator, PyObject? seed)
    {
        IsGenerator = generator;
        Seed(seed);
    }

    public bool IsGenerator { get; }

    public static readonly string[] LegacyMethods =
    [
        "seed", "rand", "randn", "randint", "random", "random_sample", "ranf", "sample", "uniform", "normal",
        "standard_normal", "choice", "shuffle", "permutation", "exponential", "poisson", "binomial", "lognormal",
        "standard_exponential", "random_integers", "get_state",
    ];

    private static readonly string[] GeneratorMethods =
    [
        "random", "integers", "uniform", "normal", "standard_normal", "choice", "shuffle", "permutation",
        "exponential", "poisson", "binomial", "lognormal", "standard_exponential", "permuted",
    ];

    public override string TypeName => IsGenerator ? "numpy.random.Generator" : "numpy.random.RandomState";

    public override string Repr() => IsGenerator ? "Generator(MT19937)" : "RandomState(MT19937)";

    public override BigInteger PyHash() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);

    public override PyObject? GetAttribute(string name) =>
        (IsGenerator ? GeneratorMethods : LegacyMethods).Contains(name, StringComparer.Ordinal)
            ? new PyBuiltinFunction(name, (arguments, keywords) => NumpyModule.Guard(name, () => Call(name, arguments, keywords)))
            : null;

    public void Seed(PyObject? seed)
    {
        switch (seed)
        {
            case null or PyNone:
                _twister.SeedFromEntropy();
                return;

            case PyInt integer when !IsGenerator:
                if (integer.Value < 0 || integer.Value > uint.MaxValue)
                {
                    throw new PyRaise(PyErrors.ValueError("Seed must be between 0 and 2**32 - 1"));
                }

                _twister.Seed((uint)integer.Value);
                return;

            case PyInt integer:
            {
                if (integer.Value < 0)
                {
                    throw new PyRaise(PyErrors.ValueError("Seed must be non-negative"));
                }

                var words = new List<uint>();
                var rest = integer.Value;

                do
                {
                    words.Add((uint)(rest & uint.MaxValue));
                    rest >>= 32;
                }
                while (rest > 0);

                _twister.Seed([.. words]);
                return;
            }

            case NdArray or PyList or PyTuple:
                _twister.Seed([.. NdConvert.ToArray(seed).ToLongs().Select(static v => (uint)v)]);
                return;

            default:
                throw new PyRaise(PyErrors.TypeError($"Cannot cast scalar from {seed.TypeName} to a seed"));
        }
    }

    /// <summary>Reads a <c>size</c> argument: null for a single scalar.</summary>
    private static int[]? Size(PyObject? size) =>
        size is null or PyNone ? null : NdConvert.ToShape(size);

    private static PyObject Draw(int[]? shape, Func<double> draw)
    {
        if (shape is null)
        {
            return new PyFloat(draw());
        }

        var values = new double[NdArray.CheckedSize(shape)];

        for (var i = 0; i < values.Length; i++)
        {
            values[i] = draw();
        }

        return NdArray.FromDoubles(values, shape);
    }

    /// <summary>A draw whose parameters may be arrays, broadcast against each other and the size.</summary>
    private static PyObject DrawWith(int[]? size, PyObject[] parameters, Func<double[], double> draw)
    {
        var arrays = parameters.Select(static p => NdConvert.ToArray(p).AsType(NdDType.Float64)).ToArray();

        if (size is null && arrays.All(static a => a.NDim == 0))
        {
            return new PyFloat(draw([.. arrays.Select(static a => a.GetDouble(a.Offset))]));
        }

        var shape = size ?? NdArray.BroadcastShapes([.. arrays.Select(static a => a.Shape)]);
        var broadcast = arrays.Select(a => a.BroadcastTo(shape).ToDoubles()).ToArray();
        var values = new double[NdArray.CheckedSize(shape)];
        var arguments = new double[arrays.Length];

        for (var i = 0; i < values.Length; i++)
        {
            for (var j = 0; j < arguments.Length; j++)
            {
                arguments[j] = broadcast[j][i];
            }

            values[i] = draw(arguments);
        }

        return NdArray.FromDoubles(values, shape);
    }

    private static int[]? DimensionArguments(PyObject[] arguments) =>
        arguments.Length == 0 ? null : [.. arguments.Select(NdConvert.ToInt)];

    private PyObject Call(string name, PyObject[] arguments, PyDict? keywords)
    {
        switch (name)
        {
            case "seed":
            {
                var args = new NpArgs(name, arguments, keywords, "seed");
                Seed(args["seed"]);
                return PyNone.Instance;
            }

            case "get_state":
                throw new PyRaise(new PyException(
                    PyExceptionType.NotImplementedError, "get_state() is not available in the sandbox; reseed instead"));

            case "rand":
                PyBuiltinFunction.RejectKeywords(name, keywords);
                return Draw(DimensionArguments(arguments), _twister.NextDouble);

            case "randn":
                PyBuiltinFunction.RejectKeywords(name, keywords);
                return Draw(DimensionArguments(arguments), _twister.NextGaussian);

            case "random" or "random_sample" or "ranf" or "sample":
            {
                var args = new NpArgs(name, arguments, keywords, "size", "dtype", "out");
                return Draw(Size(args["size"]), _twister.NextDouble);
            }

            case "standard_normal":
            {
                var args = new NpArgs(name, arguments, keywords, "size", "dtype", "out");
                return Draw(Size(args["size"]), _twister.NextGaussian);
            }

            case "standard_exponential":
            {
                var args = new NpArgs(name, arguments, keywords, "size", "dtype", "method", "out");
                return Draw(Size(args["size"]), _twister.Exponential);
            }

            case "uniform":
            {
                var args = new NpArgs(name, arguments, keywords, "low", "high", "size");
                return DrawWith(
                    Size(args["size"]),
                    [args.Optional("low") ?? new PyFloat(0), args.Optional("high") ?? new PyFloat(1)],
                    p => p[0] + ((p[1] - p[0]) * _twister.NextDouble()));
            }

            case "normal":
            {
                var args = new NpArgs(name, arguments, keywords, "loc", "scale", "size");
                var scale = args.Optional("scale") ?? new PyFloat(1);

                if (NdConvert.ToArray(scale).ToDoubles().Any(static s => s < 0))
                {
                    throw new PyRaise(PyErrors.ValueError("scale < 0"));
                }

                return DrawWith(
                    Size(args["size"]),
                    [args.Optional("loc") ?? new PyFloat(0), scale],
                    p => p[0] + (p[1] * _twister.NextGaussian()));
            }

            case "lognormal":
            {
                var args = new NpArgs(name, arguments, keywords, "mean", "sigma", "size");
                return DrawWith(
                    Size(args["size"]),
                    [args.Optional("mean") ?? new PyFloat(0), args.Optional("sigma") ?? new PyFloat(1)],
                    p => Math.Exp(p[0] + (p[1] * _twister.NextGaussian())));
            }

            case "exponential":
            {
                var args = new NpArgs(name, arguments, keywords, "scale", "size");
                return DrawWith(Size(args["size"]), [args.Optional("scale") ?? new PyFloat(1)], p => p[0] * _twister.Exponential());
            }

            case "poisson":
            {
                var args = new NpArgs(name, arguments, keywords, "lam", "size");
                var lam = NdArray.ToDouble(args.Optional("lam") ?? new PyFloat(1));

                if (lam < 0 || double.IsNaN(lam))
                {
                    throw new PyRaise(PyErrors.ValueError("lam < 0 or lam is NaN"));
                }

                return Integers(Size(args["size"]), () => _twister.Poisson(lam));
            }

            case "binomial":
            {
                var args = new NpArgs(name, arguments, keywords, "n", "p", "size");
                var n = NdArray.ToLong(args.Required("n"));
                var p = NdArray.ToDouble(args.Required("p"));

                if (n < 0)
                {
                    throw new PyRaise(PyErrors.ValueError("n < 0"));
                }

                if (p is < 0 or > 1 || double.IsNaN(p))
                {
                    throw new PyRaise(PyErrors.ValueError(p < 0 ? "p < 0" : p > 1 ? "p > 1" : "p is nan"));
                }

                return Integers(Size(args["size"]), () => _twister.Binomial(n, p));
            }

            case "randint" or "integers" or "random_integers":
            {
                var args = new NpArgs(name, arguments, keywords, "low", "high", "size", "dtype", "endpoint");
                var low = NdArray.ToLong(args.Required("low"));
                long high;

                if (args.Optional("high") is { } given)
                {
                    high = NdArray.ToLong(given);
                }
                else
                {
                    (low, high) = name == "random_integers" ? (1, low) : (0, low);
                }

                var inclusive = name == "random_integers" || args.Bool("endpoint", false);

                if (inclusive ? low > high : low >= high)
                {
                    throw new PyRaise(PyErrors.ValueError(inclusive ? "low > high" : "high <= low"));
                }

                var range = (ulong)(BigInteger.Abs(new BigInteger(high) - low) - (inclusive ? 0 : 1));
                var drawn = Integers(Size(args["size"]), () => _twister.Bounded(low, range));
                return args.DType() is { Kind: not DKind.Int } dtype && drawn is NdArray array ? array.AsType(dtype) : drawn;
            }

            case "choice":
                return Choice(new NpArgs(name, arguments, keywords, "a", "size", "replace", "p", "axis", "shuffle"));

            case "shuffle":
                Shuffle(new NpArgs(name, arguments, keywords, "x", "axis").Required("x"));
                return PyNone.Instance;

            case "permutation" or "permuted":
            {
                var source = new NpArgs(name, arguments, keywords, "x", "axis", "out").Required("x");
                var array = source is PyInt ? Arange(NdConvert.ToInt(source)) : NdConvert.ToArray(source).Copy();
                Shuffle(array);
                return array;
            }

            default:
                throw new PyRaise(PyErrors.AttributeError(TypeName, name));
        }
    }

    private static NdArray Arange(int n)
    {
        NdArray.CheckedSize([n]);
        return NdArray.Vector([.. Enumerable.Range(0, n).Select(static i => (long)i)]);
    }

    private static PyObject Integers(int[]? shape, Func<long> draw)
    {
        if (shape is null)
        {
            return PyInt.From(draw());
        }

        var values = new long[NdArray.CheckedSize(shape)];

        for (var i = 0; i < values.Length; i++)
        {
            values[i] = draw();
        }

        return NdArray.FromLongs(values, shape);
    }

    /// <summary>The legacy in-place shuffle: Fisher-Yates from the end, along the first axis.</summary>
    private void Shuffle(PyObject target)
    {
        switch (target)
        {
            case NdArray { NDim: 0 }:
                throw new PyRaise(PyErrors.TypeError("object of type 'numpy.ndarray' has no len()"));

            case NdArray array:
            {
                var n = array.Shape[0];

                for (var i = n - 1; i >= 1; i--)
                {
                    var j = (int)_twister.Interval((ulong)i);

                    if (array.NDim == 1)
                    {
                        var (a, b) = (array.Offset + (i * array.Strides[0]), array.Offset + (j * array.Strides[0]));
                        var held = array.GetBoxed(a);
                        array.SetBoxed(a, array.GetBoxed(b));
                        array.SetBoxed(b, held);
                    }
                    else
                    {
                        var rowI = (NdArray)array.Row(i);
                        var rowJ = (NdArray)array.Row(j);
                        var held = rowI.Copy();
                        rowI.Assign(rowI.Offsets(), rowI.Shape, rowJ);
                        rowJ.Assign(rowJ.Offsets(), rowJ.Shape, held);
                    }
                }

                return;
            }

            case PyList list:
                for (var i = list.Items.Count - 1; i >= 1; i--)
                {
                    var j = (int)_twister.Interval((ulong)i);
                    (list.Items[i], list.Items[j]) = (list.Items[j], list.Items[i]);
                }

                return;

            default:
                throw new PyRaise(PyErrors.TypeError("shuffle() argument must be an array or a list"));
        }
    }

    private PyObject Choice(NpArgs args)
    {
        var a = args.Required("a");
        var population = a is PyInt ? null : NdConvert.ToArray(a);
        var size = population?.NDim == 0
            ? throw new PyRaise(PyErrors.ValueError("a must be 1-dimensional or an integer"))
            : population?.Shape[0] ?? NdConvert.ToInt(a);
        var shape = Size(args["size"]);
        var count = shape is null ? 1 : NdArray.CheckedSize(shape);
        var replace = args.Bool("replace", true);
        double[]? p = null;

        if (size <= 0 && count > 0)
        {
            throw new PyRaise(PyErrors.ValueError(a is PyInt
                ? "a must be greater than 0 unless no samples are taken"
                : "'a' cannot be empty unless no samples are taken"));
        }

        if (args.Optional("p") is { } probabilities)
        {
            p = NdConvert.ToArray(probabilities).ToDoubles();

            if (p.Length != size)
            {
                throw new PyRaise(PyErrors.ValueError("'a' and 'p' must have same size"));
            }

            if (p.Any(static v => v < 0 || double.IsNaN(v)))
            {
                throw new PyRaise(PyErrors.ValueError("probabilities are not non-negative"));
            }

            if (Math.Abs(p.Sum() - 1) > Math.Sqrt(double.Epsilon) + 1e-8)
            {
                throw new PyRaise(PyErrors.ValueError("probabilities do not sum to 1"));
            }
        }

        long[] indices;

        if (replace)
        {
            if (p is null)
            {
                indices = [.. Enumerable.Range(0, count).Select(_ => _twister.Bounded(0, (ulong)(size - 1)))];
            }
            else
            {
                var cdf = Cdf(p);
                indices = [.. Enumerable.Range(0, count).Select(_ => (long)ArrayFunctions.Bisect(cdf, _twister.NextDouble(), right: true))];
            }
        }
        else
        {
            if (count > size)
            {
                throw new PyRaise(PyErrors.ValueError("Cannot take a larger sample than population when replace is False"));
            }

            if (p is null)
            {
                var permutation = Arange(size);
                Shuffle(permutation);
                indices = [.. permutation.ToLongs().Take(count)];
            }
            else
            {
                if (p.Count(static v => v > 0) < count)
                {
                    throw new PyRaise(PyErrors.ValueError("Fewer non-zero entries in p than size"));
                }

                var weights = (double[])p.Clone();
                var found = new List<long>();

                while (found.Count < count)
                {
                    var draws = Enumerable.Range(0, count - found.Count).Select(_ => _twister.NextDouble()).ToArray();

                    foreach (var taken in found)
                    {
                        weights[taken] = 0;
                    }

                    var cdf = Cdf(weights);
                    var picked = new List<long>();

                    foreach (var draw in draws)
                    {
                        var index = (long)ArrayFunctions.Bisect(cdf, draw, right: true);

                        if (!picked.Contains(index))
                        {
                            picked.Add(index);
                        }
                    }

                    found.AddRange(picked);
                }

                indices = [.. found];
            }
        }

        if (population is null)
        {
            return shape is null ? PyInt.From(indices[0]) : NdArray.FromLongs(indices, shape);
        }

        var chosen = NdConvert.ToArray(population.GetItem(NdArray.Vector(indices)));
        return shape is null ? chosen.Row(0) : chosen.Reshape([.. shape, .. population.Shape[1..]]);
    }

    private static double[] Cdf(double[] p)
    {
        var cdf = new double[p.Length];
        var total = 0.0;

        for (var i = 0; i < p.Length; i++)
        {
            total += p[i];
            cdf[i] = total;
        }

        for (var i = 0; i < cdf.Length; i++)
        {
            cdf[i] /= total;
        }

        return cdf;
    }
}
