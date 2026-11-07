using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules.Pandas;

/// <summary>
/// <c>rolling(window)</c> and <c>expanding()</c> windows over a Series or each column of a
/// frame, plus the other order-dependent column transforms: cumulative sums, shift, diff,
/// percentage change, rank, forward and backward fill, and linear interpolation.
/// </summary>
internal sealed class PdWindow : PyObject
{
    private readonly PdSeries? _series;
    private readonly PdFrame? _frame;
    private readonly int _window;
    private readonly int _minPeriods;
    private readonly bool _center;
    private readonly bool _expanding;

    public PdWindow(PdSeries? series, PdFrame? frame, int window, int? minPeriods, bool center, bool expanding)
    {
        if (!expanding && window <= 0)
        {
            throw Pd.ValueError("window must be an integer 0 or greater");
        }

        _series = series;
        _frame = frame;
        _window = window;
        _minPeriods = minPeriods ?? (expanding ? 1 : window);
        _center = center;
        _expanding = expanding;
    }

    public override string TypeName => _expanding ? "Expanding" : "Rolling";

    public override string Repr() => _expanding
        ? $"Expanding [min_periods={_minPeriods},axis=0,method=single]"
        : $"Rolling [window={_window},min_periods={_minPeriods},center={(_center ? "True" : "False")},axis=0,method=single]";

    public override PyObject? GetAttribute(string name) => Pd.Guard(() => AttributeCore(name));

    private PyObject? AttributeCore(string name)
    {
        if (PdRuntime.Current is not { } runtime)
        {
            return null;
        }

        if (name is "mean" or "sum" or "min" or "max" or "std" or "var" or "count" or "median")
        {
            return runtime.Method(name, ["ddof", "numeric_only"], a => Apply(column => Compute(column, values => Statistic(values, name, a.Int("ddof", 1)), name == "count")));
        }

        return name switch
        {
            "apply" => runtime.Method(name, ["func", "raw", "engine", "args", "kwargs"], a =>
            {
                var function = a.Required("func");
                var raw = a.Bool("raw", false);
                return Apply(column => Compute(column, values =>
                {
                    var list = new PyList([.. values.Select(static v => (PyObject)Pd.Float(v))]);
                    PyObject argument = raw ? runtime.Array(list) : new PdSeries(PdColumn.Infer(list.Items), PdIndex.Default(list.Items.Count), null);
                    return Pd.ToDouble(runtime.Call(function, argument));
                }, false));
            }),
            "agg" or "aggregate" => runtime.Method(name, ["func"], a => runtime.Call(GetAttribute(Pd.Text(a.Required("func")))!)),
            _ => null,
        };
    }

    private PyObject Apply(Func<PdColumn, PdColumn> compute) => _series is { } series
        ? series.With(compute(series.Data))
        : _frame!.MapColumns(c => c.IsNumeric ? compute(c) : throw Pd.TypeError($"ops for {TypeName} for this dtype {Pd.Name(c.Type)} are not implemented"));

    private PdColumn Compute(PdColumn column, Func<List<double>, double> statistic, bool counting)
    {
        var values = column.Doubles();
        var result = new double[values.Length];

        for (var i = 0; i < values.Length; i++)
        {
            int start, end;

            if (_expanding)
            {
                (start, end) = (0, i);
            }
            else if (_center)
            {
                var offset = _window / 2;
                (start, end) = (i - offset, i - offset + _window - 1);
            }
            else
            {
                (start, end) = (i - _window + 1, i);
            }

            var window = new List<double>();

            for (var j = Math.Max(0, start); j <= Math.Min(values.Length - 1, end); j++)
            {
                if (!double.IsNaN(values[j]))
                {
                    window.Add(values[j]);
                }
            }

            result[i] = window.Count >= _minPeriods && (counting || window.Count > 0) ? statistic(window) : double.NaN;
        }

        return PdColumn.Floats(result);
    }

    private static double Statistic(List<double> values, string how, int ddof)
    {
        switch (how)
        {
            case "count":
                return values.Count;
            case "sum":
                return values.Sum();
            case "mean":
                return values.Sum() / values.Count;
            case "min":
                return values.Min();
            case "max":
                return values.Max();
            case "median":
            {
                var sorted = values.Order().ToList();
                var middle = sorted.Count / 2;
                return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
            }

            default:
            {
                if (values.Count - ddof <= 0)
                {
                    return double.NaN;
                }

                var mean = values.Sum() / values.Count;
                var variance = values.Sum(v => (v - mean) * (v - mean)) / (values.Count - ddof);
                return how == "var" ? variance : Math.Sqrt(variance);
            }
        }
    }

    // ---- column transforms --------------------------------------------------------------

    /// <summary><c>cumsum</c>, <c>cumprod</c>, <c>cummax</c>, <c>cummin</c>; a missing value stays missing.</summary>
    public static PdColumn Cumulative(PdColumn column, string how, bool skipna)
    {
        if (column.Type == DType.Int64 || column.Type == DType.Bool)
        {
            var values = new long[column.Length];
            long running = how == "cumprod" ? 1 : 0;

            for (var i = 0; i < values.Length; i++)
            {
                var value = column.Values[i] is PyBool flag ? flag.Value ? 1 : 0 : (long)((PyInt)column.Values[i]).Value;
                running = i == 0 && how is "cummax" or "cummin" ? value : how switch
                {
                    "cumsum" => unchecked(running + value),
                    "cumprod" => unchecked(running * value),
                    "cummax" => Math.Max(running, value),
                    _ => Math.Min(running, value),
                };
                values[i] = running;
            }

            return PdColumn.Ints(values);
        }

        if (column.Type == DType.Object && !column.Values.All(static v => Pd.IsNumber(v) || Pd.IsMissing(v)))
        {
            var raw = new PyObject[column.Length];
            PyObject? current = null;

            for (var i = 0; i < raw.Length; i++)
            {
                var value = column.Values[i];

                if (Pd.IsMissing(value))
                {
                    raw[i] = Pd.NaN;
                    continue;
                }

                current = current is null ? value : how switch
                {
                    "cumsum" => Operators.Binary("+", current, value),
                    "cummax" => Operators.Compare(">", value, current).IsTruthy() ? value : current,
                    "cummin" => Operators.Compare("<", value, current).IsTruthy() ? value : current,
                    _ => Operators.Binary("*", current, value),
                };
                raw[i] = current;
            }

            return new PdColumn(DType.Object, raw);
        }

        var doubles = column.Doubles();
        var result = new double[doubles.Length];
        double? acc = null;
        var poisoned = false;

        for (var i = 0; i < doubles.Length; i++)
        {
            var value = doubles[i];

            if (double.IsNaN(value))
            {
                result[i] = double.NaN;
                poisoned |= !skipna;
                continue;
            }

            if (poisoned)
            {
                result[i] = double.NaN;
                continue;
            }

            acc = acc is not { } a ? value : how switch
            {
                "cumsum" => a + value,
                "cumprod" => a * value,
                "cummax" => Math.Max(a, value),
                _ => Math.Min(a, value),
            };
            result[i] = acc.Value;
        }

        return PdColumn.Floats(result);
    }

    /// <summary><c>shift(periods)</c>: values move down (or up), the gap filled with missing.</summary>
    public static PdColumn Shift(PdColumn column, int periods, PyObject? fill)
    {
        var positions = Enumerable.Range(0, column.Length).Select(i => i - periods is var source && source >= 0 && source < column.Length ? source : -1).ToList();
        var shifted = column.Take(positions);

        if (fill is not null)
        {
            var gaps = positions.Select((p, i) => (p, i)).Where(static x => x.p < 0).Select(static x => x.i).ToList();
            return gaps.Count == 0 ? shifted : column.Take([.. positions.Select(static p => Math.Max(p, 0))]).With(gaps, fill);
        }

        return shifted;
    }

    /// <summary><c>diff(periods)</c>: each value minus the one <paramref name="periods"/> before.</summary>
    public static PdColumn Diff(PdColumn column, int periods)
    {
        if (column.Type == DType.DateTime)
        {
            var raw = new PyObject[column.Length];
            for (var i = 0; i < raw.Length; i++)
            {
                var j = i - periods;
                raw[i] = j >= 0 && j < raw.Length && column.Values[i] is PdTimestamp a && column.Values[j] is PdTimestamp b
                    ? PyDelta.FromMicroseconds(a.Micro - b.Micro)
                    : PdNaT.Instance;
            }

            return new PdColumn(DType.TimeDelta, raw);
        }

        var values = column.Doubles();
        var result = new double[values.Length];

        for (var i = 0; i < values.Length; i++)
        {
            var j = i - periods;
            result[i] = j >= 0 && j < values.Length ? values[i] - values[j] : double.NaN;
        }

        return PdColumn.Floats(result);
    }

    /// <summary><c>pct_change(periods)</c>, after a forward fill as pandas does by default.</summary>
    public static PdColumn PctChange(PdColumn column, int periods)
    {
        var values = Fill(column, forward: true, int.MaxValue).Doubles();
        var result = new double[values.Length];

        for (var i = 0; i < values.Length; i++)
        {
            var j = i - periods;
            result[i] = j >= 0 && j < values.Length ? (values[i] / values[j]) - 1 : double.NaN;
        }

        return PdColumn.Floats(result);
    }

    /// <summary><c>rank</c>: average, min, max, first or dense; missing values keep NaN.</summary>
    public static PdColumn Rank(PdColumn column, string method, bool ascending, bool percent, string naOption)
    {
        var valid = PdReduce.Valid(column);
        var order = PdSort.Order([column.Take(valid)], [ascending], "last").Select(o => valid[o]).ToList();
        var ranks = Enumerable.Repeat(double.NaN, column.Length).ToArray();
        var dense = 0;
        var i = 0;

        while (i < order.Count)
        {
            var j = i;
            while (j + 1 < order.Count && Pd.Order(column.Values[order[j + 1]], column.Values[order[i]]) == 0)
            {
                j++;
            }

            dense++;

            for (var k = i; k <= j; k++)
            {
                ranks[order[k]] = method switch
                {
                    "average" => ((i + 1) + (j + 1)) / 2.0,
                    "min" => i + 1,
                    "max" => j + 1,
                    "first" => k + 1,
                    "dense" => dense,
                    _ => throw Pd.ValueError("invalid method"),
                };
            }

            i = j + 1;
        }

        if (naOption is "top" or "bottom")
        {
            var missing = Enumerable.Range(0, column.Length).Where(column.IsMissing).ToList();
            var offset = naOption == "top" ? missing.Count : 0;

            for (var k = 0; k < ranks.Length; k++)
            {
                ranks[k] = double.IsNaN(ranks[k]) ? (naOption == "top" ? missing.IndexOf(k) + 1 : valid.Count + missing.IndexOf(k) + 1) : ranks[k] + offset;
            }
        }

        if (percent)
        {
            var total = method == "dense" ? dense : ranks.Count(static r => !double.IsNaN(r));
            ranks = [.. ranks.Select(r => r / total)];
        }

        return PdColumn.Floats(ranks);
    }

    /// <summary>Forward or backward fill, at most <paramref name="limit"/> in a row.</summary>
    public static PdColumn Fill(PdColumn column, bool forward, int limit)
    {
        var values = column.Values.ToArray();
        var count = values.Length;
        PyObject? last = null;
        var run = 0;

        for (var step = 0; step < count; step++)
        {
            var i = forward ? step : count - 1 - step;

            if (!column.IsMissing(i))
            {
                last = values[i];
                run = 0;
                continue;
            }

            if (last is not null && run < limit)
            {
                values[i] = last;
                run++;
            }
        }

        return new PdColumn(column.Type, values);
    }

    /// <summary>Linear interpolation over position; a leading gap stays, a trailing gap takes the last value.</summary>
    public static PdColumn Interpolate(PdColumn column)
    {
        if (!column.IsNumeric)
        {
            return column.Clone();
        }

        var values = column.Doubles();
        var previous = -1;

        for (var i = 0; i < values.Length; i++)
        {
            if (double.IsNaN(values[i]))
            {
                continue;
            }

            if (previous >= 0 && i - previous > 1)
            {
                for (var k = previous + 1; k < i; k++)
                {
                    values[k] = values[previous] + ((values[i] - values[previous]) * (k - previous) / (i - previous));
                }
            }

            previous = i;
        }

        if (previous >= 0)
        {
            for (var k = previous + 1; k < values.Length; k++)
            {
                values[k] = values[previous];
            }
        }

        return PdColumn.Floats(values);
    }
}

/// <summary>
/// <c>sample</c>: numpy's legacy <c>RandomState</c> (MT19937 seeded with <c>init_genrand</c>)
/// and its <c>permutation</c>, which is what <c>pandas.sample(random_state=n)</c> draws from.
/// </summary>
internal static class PdSample
{
    public static List<int> Positions(int count, PdArgs arguments)
    {
        if (arguments.Given("weights") is not null)
        {
            throw Pd.TypeError("sample(weights=...) is not supported in this sandbox");
        }

        var replace = arguments.Bool("replace", false);
        int size;

        if (arguments.Given("frac") is { } frac)
        {
            if (arguments.Given("n") is not null)
            {
                throw Pd.ValueError("Please enter a value for `frac` OR `n`, not both");
            }

            size = (int)Math.Round(Pd.ToDouble(frac) * count, MidpointRounding.ToEven);
        }
        else
        {
            size = arguments.Int("n", 1);
        }

        if (size < 0)
        {
            throw Pd.ValueError("A negative number of rows requested. Please provide `n` >= 0.");
        }

        if (!replace && size > count)
        {
            throw Pd.ValueError("Cannot take a larger sample than population when 'replace=False'");
        }

        Pd.CheckSize(size);
        var seed = arguments.Given("random_state") is { } state ? (uint)Pd.ToLong(state, "random_state") : (uint)Random.Shared.NextInt64(0, uint.MaxValue);
        var generator = new Mt19937(seed);

        if (replace)
        {
            return [.. Enumerable.Range(0, size).Select(_ => (int)generator.Interval((uint)(count - 1)))];
        }

        var permutation = Enumerable.Range(0, count).ToArray();

        for (var i = count - 1; i > 0; i--)
        {
            var j = (int)generator.Interval((uint)i);
            (permutation[i], permutation[j]) = (permutation[j], permutation[i]);
        }

        return [.. permutation.Take(size)];
    }

    private sealed class Mt19937
    {
        private readonly uint[] _state = new uint[624];
        private int _position;

        public Mt19937(uint seed)
        {
            _state[0] = seed;

            for (var i = 1; i < 624; i++)
            {
                _state[i] = unchecked((1812433253u * (_state[i - 1] ^ (_state[i - 1] >> 30))) + (uint)i);
            }

            _position = 624;
        }

        private uint Next()
        {
            if (_position >= 624)
            {
                for (var k = 0; k < 624; k++)
                {
                    var y = (_state[k] & 0x80000000u) | (_state[(k + 1) % 624] & 0x7fffffffu);
                    _state[k] = _state[(k + 397) % 624] ^ (y >> 1) ^ ((y & 1) != 0 ? 0x9908b0dfu : 0);
                }

                _position = 0;
            }

            var value = _state[_position++];
            value ^= value >> 11;
            value ^= (value << 7) & 0x9d2c5680u;
            value ^= (value << 15) & 0xefc60000u;
            value ^= value >> 18;
            return value;
        }

        /// <summary>numpy's <c>random_interval</c>: a masked rejection draw in [0, max].</summary>
        public uint Interval(uint max)
        {
            if (max == 0)
            {
                return 0;
            }

            var mask = max;
            mask |= mask >> 1;
            mask |= mask >> 2;
            mask |= mask >> 4;
            mask |= mask >> 8;
            mask |= mask >> 16;

            uint value;
            while ((value = Next() & mask) > max)
            {
            }

            return value;
        }
    }
}
