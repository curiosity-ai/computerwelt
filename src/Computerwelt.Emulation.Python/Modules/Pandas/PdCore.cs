using System.Globalization;
using System.Numerics;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules.Pandas;

/// <summary>The element types a column can have, named as pandas names them.</summary>
internal enum DType
{
    Int64,
    Float64,
    Bool,
    Object,
    DateTime,
    TimeDelta,
}

/// <summary>Shared helpers: missing values, size caps, conversions and error factories.</summary>
internal static class Pd
{
    /// <summary>
    /// The most cells one call may build. Work inside a builtin is not counted by the
    /// instruction limit, so a single call is capped instead — the same 2^24 that
    /// <c>random.choices</c> uses.
    /// </summary>
    public const long MaxCells = 1 << 24;

    /// <summary>The float NaN every float column uses for "missing".</summary>
    public static readonly PyFloat NaN = new(double.NaN);

    /// <summary>The dtype's name as pandas prints it.</summary>
    public static string Name(DType type) => type switch
    {
        DType.Int64 => "int64",
        DType.Float64 => "float64",
        DType.Bool => "bool",
        DType.DateTime => "datetime64[ns]",
        DType.TimeDelta => "timedelta64[ns]",
        _ => "object",
    };

    /// <summary>Raises <c>MemoryError</c> when a result would exceed the cap.</summary>
    public static void CheckSize(long cells)
    {
        if (cells > MaxCells || cells < 0)
        {
            throw new PyRaise(new PyException(
                PyExceptionType.MemoryError,
                $"pandas: a result of {cells} cells exceeds the sandbox limit of {MaxCells}"));
        }
    }

    /// <summary>True for every spelling of "missing": None, NaN, NaT and NA.</summary>
    public static bool IsMissing(PyObject value) => value switch
    {
        PyNone => true,
        PyFloat number => double.IsNaN(number.Value),
        PdNaT or PdNA => true,
        _ => false,
    };

    /// <summary>True for an int or a float (bools count, as numpy counts them).</summary>
    public static bool IsNumber(PyObject value) => value is PyInt or PyFloat;

    /// <summary>A number as a double; NaN for anything missing.</summary>
    public static double ToDouble(PyObject value) => value switch
    {
        PyFloat number => number.Value,
        PyBool flag => flag.Value ? 1 : 0,
        PyInt integer => (double)integer.Value,
        _ when IsMissing(value) => double.NaN,
        _ => throw new PyRaise(PyErrors.TypeError($"could not convert {value.TypeName} to float: {Repr(value)}")),
    };

    /// <summary>A float, shared for NaN.</summary>
    public static PyFloat Float(double value) => double.IsNaN(value) ? NaN : new PyFloat(value);

    /// <summary>An integer from a long.</summary>
    public static PyInt Int(long value) => PyInt.From(value);

    /// <summary>A Python string.</summary>
    public static PyStr Str(string value) => new(value);

    /// <summary><c>repr</c> that never throws past its caller.</summary>
    public static string Repr(PyObject value) => value.Repr();

    /// <summary>A <c>KeyError</c> naming the key.</summary>
    public static PyRaise KeyError(PyObject key) => new(PyErrors.KeyError(key));

    /// <summary>A <c>ValueError</c>.</summary>
    public static PyRaise ValueError(string message) => new(PyErrors.ValueError(message));

    /// <summary>A <c>TypeError</c>.</summary>
    public static PyRaise TypeError(string message) => new(PyErrors.TypeError(message));

    /// <summary>An <c>IndexError</c>.</summary>
    public static PyRaise IndexError(string message) => new(PyErrors.IndexError(message));

    /// <summary>
    /// Runs a builtin body, translating any .NET exception into a Python one so nothing from
    /// the host escapes into the program. A <see cref="PyRaise"/> passes through untouched.
    /// </summary>
    public static T Guard<T>(Func<T> body)
    {
        try
        {
            return body();
        }
        catch (PyRaise)
        {
            throw;
        }
        catch (InvalidCastException error)
        {
            throw new PyRaise(PyErrors.TypeError($"pandas: unsupported operand ({error.Message})"));
        }
        catch (Exception error) when (error is IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            throw new PyRaise(PyErrors.IndexError("pandas: index out of bounds"));
        }
        catch (OverflowException)
        {
            throw new PyRaise(PyErrors.OverflowErrorFor("pandas: value out of range"));
        }
        catch (FormatException error)
        {
            throw new PyRaise(PyErrors.ValueError($"pandas: {error.Message}"));
        }
        catch (KeyNotFoundException)
        {
            throw new PyRaise(PyErrors.KeyError(PyNone.Instance));
        }
        catch (OutOfMemoryException)
        {
            throw new PyRaise(new PyException(PyExceptionType.MemoryError, string.Empty));
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or NullReferenceException or DivideByZeroException or NotSupportedException)
        {
            throw new PyRaise(PyErrors.RuntimeError($"pandas: internal error ({error.GetType().Name})"));
        }
    }

    /// <summary>A label as text, as pandas shows a column name.</summary>
    public static string Text(PyObject value) => value switch
    {
        PyStr text => text.Value,
        PyFloat number => FloatText(number.Value),
        _ => value.Display(),
    };

    /// <summary>A float as numpy's <c>str</c> shows it: <c>1.0</c>, <c>nan</c>.</summary>
    public static string FloatText(double value) => PyFloat.Format(value);

    /// <summary>Iterates any iterable, raising <c>TypeError</c> when it is not one.</summary>
    public static List<PyObject> List(PyObject value)
    {
        if (value is PyList list)
        {
            return [.. list.Items];
        }

        if (value is PyTuple tuple)
        {
            return [.. tuple.Items];
        }

        // A sized iterable (a range, an array) is measured before anything is materialized.
        if (value.Length() is { } length)
        {
            CheckSize(length);
        }

        var items = value.Iterate() ?? throw TypeError($"'{value.TypeName}' object is not iterable");
        var result = new List<PyObject>();

        foreach (var item in items)
        {
            result.Add(item);

            if (result.Count > MaxCells)
            {
                CheckSize(result.Count);
            }
        }

        return result;
    }

    /// <summary>True for a list-like argument: list, tuple, Index, Series, array, set, range.</summary>
    public static bool IsListLike(PyObject value) =>
        value is PyList or PyTuple or PdIndex or PdSeries or PySet or PyRange
        || (value is not (PyStr or PyBytes or PyDict or PdFrame) && value.Length() is not null && value.Iterate() is not null);

    /// <summary>A Python integer argument.</summary>
    public static long ToLong(PyObject value, string what) => value switch
    {
        PyInt integer when integer.Value >= long.MinValue && integer.Value <= long.MaxValue => (long)integer.Value,
        PyFloat number when number.Value == Math.Floor(number.Value) && Math.Abs(number.Value) < 9e18 => (long)number.Value,
        _ => throw TypeError($"{what} must be an integer, not {value.TypeName}"),
    };

    /// <summary>A Python integer argument, as an int.</summary>
    public static int ToInt(PyObject value, string what)
    {
        var number = ToLong(value, what);
        return number is > int.MaxValue or < int.MinValue ? throw ValueError($"{what} is out of range") : (int)number;
    }

    /// <summary>Compares two values for sorting: missing values are handled by the caller.</summary>
    public static int Order(PyObject left, PyObject right)
    {
        if (left is PyInt a && right is PyInt b)
        {
            return a.Value.CompareTo(b.Value);
        }

        if (IsNumber(left) && IsNumber(right))
        {
            return ToDouble(left).CompareTo(ToDouble(right));
        }

        if (left is PyStr x && right is PyStr y)
        {
            return string.CompareOrdinal(x.Value, y.Value);
        }

        if (left.PyCompare(right) is { } order)
        {
            return order;
        }

        if (Operators.Compare("<", left, right).IsTruthy())
        {
            return -1;
        }

        return Operators.Compare(">", left, right).IsTruthy() ? 1 : 0;
    }

    /// <summary>Value equality for labels and keys, with NaN equal to NaN.</summary>
    public static bool Same(PyObject left, PyObject right)
    {
        if (IsMissing(left) || IsMissing(right))
        {
            return IsMissing(left) && IsMissing(right);
        }

        return PyObject.SameOrEqual(left, right);
    }

    /// <summary>The integer power of ten, for rounding.</summary>
    public static double Round(double value, int digits)
    {
        if (!double.IsFinite(value))
        {
            return value;
        }

        // numpy rounds half to even on the scaled value, which is why 2.675 rounds to 2.67.
        if (digits >= 0)
        {
            var factor = Math.Pow(10, digits);
            var scaled = value * factor;
            return double.IsFinite(scaled) ? Math.Round(scaled, MidpointRounding.ToEven) / factor : value;
        }

        var divisor = Math.Pow(10, -digits);
        return Math.Round(value / divisor, MidpointRounding.ToEven) * divisor;
    }

    /// <summary>
    /// numpy's pairwise summation, so a float sum agrees with pandas to the last bit.
    /// </summary>
    public static double PairwiseSum(double[] values, int start, int count)
    {
        if (count < 8)
        {
            var total = 0.0;
            for (var i = 0; i < count; i++)
            {
                total += values[start + i];
            }

            return total;
        }

        if (count <= 128)
        {
            var r = new double[8];
            for (var j = 0; j < 8; j++)
            {
                r[j] = values[start + j];
            }

            var i = 8;
            for (; i < count - (count % 8); i += 8)
            {
                for (var j = 0; j < 8; j++)
                {
                    r[j] += values[start + i + j];
                }
            }

            var result = ((r[0] + r[1]) + (r[2] + r[3])) + ((r[4] + r[5]) + (r[6] + r[7]));

            for (; i < count; i++)
            {
                result += values[start + i];
            }

            return result;
        }

        var half = count / 2;
        half -= half % 8;
        return PairwiseSum(values, start, half) + PairwiseSum(values, start + half, count - half);
    }

    /// <summary>Formats a double with a fixed number of decimals, invariantly.</summary>
    public static string Fixed(double value, int decimals) =>
        value.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    /// <summary>Formats a double as Python's <c>{:.Ne}</c> does: at least two exponent digits.</summary>
    public static string Scientific(double value, int decimals)
    {
        var text = value.ToString("E" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        var e = text.IndexOf('E', StringComparison.Ordinal);
        var mantissa = text[..e];
        var exponent = int.Parse(text[(e + 1)..], CultureInfo.InvariantCulture);
        return $"{mantissa}e{(exponent < 0 ? '-' : '+')}{Math.Abs(exponent):00}";
    }

    /// <summary>Python's <c>str.center</c>, which puts an odd pad on the left for odd widths.</summary>
    public static string Center(string text, int width)
    {
        var margin = width - text.Length;

        if (margin <= 0)
        {
            return text;
        }

        var left = (margin / 2) + (margin & width & 1);
        return new string(' ', left) + text + new string(' ', margin - left);
    }

    /// <summary>A big integer as a long, refusing one that does not fit int64.</summary>
    public static bool FitsLong(BigInteger value) => value >= long.MinValue && value <= long.MaxValue;
}

/// <summary>
/// One column's values with their dtype. Shared between a frame and the Series it hands
/// out until either side writes, at which point the writer copies (copy-on-write).
/// </summary>
internal sealed class PdColumn
{
    public PdColumn(DType type, PyObject[] values)
    {
        Type = type;
        Values = values;
    }

    /// <summary>The dtype.</summary>
    public DType Type { get; }

    /// <summary>The values, normalized for the dtype (floats are PyFloat, NaN for missing).</summary>
    public PyObject[] Values { get; }

    /// <summary>True once the column is reachable from more than one owner.</summary>
    public bool Shared { get; set; }

    public int Length => Values.Length;

    public bool IsNumeric => Type is DType.Int64 or DType.Float64 or DType.Bool;

    /// <summary>The value a missing slot holds for this dtype.</summary>
    public static PyObject MissingFor(DType type) => type switch
    {
        DType.DateTime or DType.TimeDelta => PdNaT.Instance,
        _ => Pd.NaN,
    };

    /// <summary>A copy that nothing else references.</summary>
    public PdColumn Clone() => new(Type, (PyObject[])Values.Clone());

    /// <summary>A column of one repeated value.</summary>
    public static PdColumn Repeat(PyObject value, int count)
    {
        Pd.CheckSize(count);
        var first = Infer([value]);
        var values = new PyObject[count];
        Array.Fill(values, count == 0 ? value : first.Values[0]);
        return new PdColumn(first.Type, values);
    }

    /// <summary>A float column.</summary>
    public static PdColumn Floats(double[] values)
    {
        var result = new PyObject[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            result[i] = Pd.Float(values[i]);
        }

        return new PdColumn(DType.Float64, result);
    }

    /// <summary>An int column.</summary>
    public static PdColumn Ints(long[] values)
    {
        var result = new PyObject[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            result[i] = PyInt.From(values[i]);
        }

        return new PdColumn(DType.Int64, result);
    }

    /// <summary>A bool column.</summary>
    public static PdColumn Bools(bool[] values)
    {
        var result = new PyObject[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            result[i] = PyBool.Of(values[i]);
        }

        return new PdColumn(DType.Bool, result);
    }

    /// <summary>The values as doubles, NaN where missing.</summary>
    public double[] Doubles()
    {
        var result = new double[Values.Length];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = Pd.ToDouble(Values[i]);
        }

        return result;
    }

    /// <summary>Whether slot <paramref name="i"/> is missing.</summary>
    public bool IsMissing(int i) => Type switch
    {
        DType.Int64 or DType.Bool => false,
        _ => Pd.IsMissing(Values[i]),
    };

    /// <summary>
    /// Infers the dtype of raw Python values, as the Series constructor does, and normalizes
    /// them for it: ints and None become floats in a float column, and so on.
    /// </summary>
    public static PdColumn Infer(IReadOnlyList<PyObject> raw)
    {
        Pd.CheckSize(raw.Count);

        bool allBool = true, allInt = true, allNumeric = true, allTime = true, allDelta = true;
        bool anyFloatOrMissing = false, anyValue = false, anyTime = false, anyDelta = false, anyNonMissing = false;

        foreach (var item in raw)
        {
            var value = Unwrap(item);

            if (value is PyNone or PdNA)
            {
                allBool = false;
                allInt = false;
                anyFloatOrMissing = true;
                continue;
            }

            if (value is PdNaT)
            {
                allBool = allInt = allNumeric = false;
                continue;
            }

            anyNonMissing = true;

            switch (value)
            {
                case PyBool:
                    allInt = allNumeric = allTime = allDelta = false;
                    anyValue = true;
                    break;
                case PyInt integer:
                    allBool = allTime = allDelta = false;
                    anyValue = true;
                    if (!Pd.FitsLong(integer.Value))
                    {
                        allInt = allNumeric = false;
                    }

                    break;
                case PyFloat:
                    allBool = allInt = allTime = allDelta = false;
                    anyFloatOrMissing = true;
                    anyValue = true;
                    break;
                case PdTimestamp or PyDateTime:
                    allBool = allInt = allNumeric = allDelta = false;
                    anyTime = true;
                    break;
                case PyDelta:
                    allBool = allInt = allNumeric = allTime = false;
                    anyDelta = true;
                    break;
                default:
                    allBool = allInt = allNumeric = allTime = allDelta = false;
                    break;
            }
        }

        var values = new PyObject[raw.Count];

        if (raw.Count == 0)
        {
            return new PdColumn(DType.Object, values);
        }

        if (anyNonMissing && allBool && !anyFloatOrMissing)
        {
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = Unwrap(raw[i]);
            }

            return new PdColumn(DType.Bool, values);
        }

        if (anyValue && allInt && !anyFloatOrMissing)
        {
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = Unwrap(raw[i]) is PyBool flag ? PyInt.From(flag.Value ? 1 : 0) : Unwrap(raw[i]);
            }

            return new PdColumn(DType.Int64, values);
        }

        if (allNumeric && (anyValue || raw.Any(static v => v is PyFloat)) && !raw.Any(static v => Unwrap(v) is PyBool))
        {
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = Pd.Float(Pd.ToDouble(Unwrap(raw[i])));
            }

            return new PdColumn(DType.Float64, values);
        }

        if (anyTime && allTime)
        {
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = (PyObject?)PdTimestamp.From(Unwrap(raw[i])) ?? PdNaT.Instance;
            }

            return new PdColumn(DType.DateTime, values);
        }

        if (anyDelta && allDelta)
        {
            for (var i = 0; i < values.Length; i++)
            {
                var value = Unwrap(raw[i]);
                values[i] = value is PyDelta ? value : PdNaT.Instance;
            }

            return new PdColumn(DType.TimeDelta, values);
        }

        for (var i = 0; i < values.Length; i++)
        {
            values[i] = Unwrap(raw[i]);
        }

        return new PdColumn(DType.Object, values);
    }

    /// <summary>
    /// Unwraps a numpy scalar — anything unsized with both <c>item</c> and <c>dtype</c> — to
    /// the Python value it holds, so an array handed to pandas reads as numbers.
    /// </summary>
    public static PyObject Unwrap(PyObject value)
    {
        if (value is PyInt or PyFloat or PyStr or PyNone or PdTimestamp or PdNaT or PdNA or PyDelta or PyDateTime
            || value.Length() is not null || PdRuntime.Current is not { } runtime)
        {
            return value;
        }

        if (value.GetAttribute("dtype") is null || Attributes.TryGet(runtime.Machine, value, "item") is not { } item)
        {
            return value;
        }

        return runtime.Machine.Call(item, []);
    }

    /// <summary>Converts the column to another dtype (the <c>astype</c> rules).</summary>
    public PdColumn Cast(DType target, string spelling)
    {
        if (target == Type)
        {
            return Clone();
        }

        var result = new PyObject[Values.Length];

        for (var i = 0; i < result.Length; i++)
        {
            var value = Values[i];
            result[i] = target switch
            {
                DType.Float64 => Pd.Float(value switch
                {
                    PyStr text => ParseFloat(text.Value),
                    _ => Pd.ToDouble(value),
                }),
                DType.Int64 => PyInt.From(ToInt64(value)),
                DType.Bool => PyBool.Of(Pd.IsMissing(value) ? value is not PyNone : value.IsTruthy()),
                DType.DateTime => PdTimestamp.Parse(value, null) ?? PdNaT.Instance,
                DType.Object when spelling == "str" => Pd.Str(PdFormat.StrOf(value, Type)),
                _ => value,
            };
        }

        return new PdColumn(target, result);

        long ToInt64(PyObject value)
        {
            switch (value)
            {
                case PyInt integer when Pd.FitsLong(integer.Value):
                    return (long)integer.Value;
                case PyInt:
                    throw new PyRaise(PyErrors.OverflowErrorFor("Python int too large to convert to C long"));
                case PyFloat number when double.IsFinite(number.Value):
                    return (long)Math.Truncate(number.Value);
                case PyFloat:
                    throw Pd.ValueError("Cannot convert non-finite values (NA or inf) to integer");
                case PyStr text when long.TryParse(text.Value.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed):
                    return parsed;
                case PyStr text:
                    throw Pd.ValueError($"invalid literal for int() with base 10: {Pd.Repr(new PyStr(text.Value))}");
                case PdTimestamp moment:
                    return moment.Nanoseconds;
                default:
                    if (Pd.IsMissing(value))
                    {
                        throw Pd.ValueError("Cannot convert non-finite values (NA or inf) to integer");
                    }

                    throw Pd.TypeError($"int() argument must be a string, a bytes-like object or a real number, not '{value.TypeName}'");
            }
        }
    }

    /// <summary>Parses a float the way <c>float(str)</c> does.</summary>
    public static double ParseFloat(string text)
    {
        var trimmed = text.Trim();

        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        return trimmed.ToLowerInvariant() switch
        {
            "nan" or "+nan" or "-nan" => double.NaN,
            "inf" or "+inf" or "infinity" or "+infinity" => double.PositiveInfinity,
            "-inf" or "-infinity" => double.NegativeInfinity,
            _ => throw Pd.ValueError($"could not convert string to float: '{text}'"),
        };
    }

    /// <summary>
    /// The values at <paramref name="positions"/>; a position of -1 is a missing slot, which
    /// promotes an int column to float and a bool column to object, as reindexing does.
    /// </summary>
    public PdColumn Take(IReadOnlyList<int> positions)
    {
        Pd.CheckSize(positions.Count);
        var anyMissing = false;

        foreach (var position in positions)
        {
            if (position < 0)
            {
                anyMissing = true;
                break;
            }
        }

        var type = !anyMissing ? Type : Type switch
        {
            DType.Int64 => DType.Float64,
            DType.Bool => DType.Object,
            _ => Type,
        };

        var result = new PyObject[positions.Count];

        for (var i = 0; i < result.Length; i++)
        {
            var position = positions[i];

            if (position < 0)
            {
                result[i] = MissingFor(type);
            }
            else if (type == DType.Float64 && Type == DType.Int64)
            {
                result[i] = Pd.Float(Pd.ToDouble(Values[position]));
            }
            else
            {
                result[i] = Values[position];
            }
        }

        return new PdColumn(type, result);
    }

    /// <summary>A contiguous run of values.</summary>
    public PdColumn Range(int start, int count)
    {
        var result = new PyObject[count];
        Array.Copy(Values, start, result, 0, count);
        return new PdColumn(Type, result);
    }

    /// <summary>
    /// Writes one value, returning the column that results — this one when the value fits
    /// the dtype, otherwise a new, wider column (an int column given a float becomes float64,
    /// given a string becomes object). The caller must own the column.
    /// </summary>
    public PdColumn With(int position, PyObject value) => With([position], value);

    /// <summary>Writes one value to several slots; see <see cref="With(int, PyObject)"/>.</summary>
    public PdColumn With(IReadOnlyList<int> positions, PyObject value)
    {
        value = Unwrap(value);

        if (Fits(value) is { } normalized)
        {
            foreach (var position in positions)
            {
                Values[position] = normalized;
            }

            return this;
        }

        var raw = (PyObject[])Values.Clone();

        foreach (var position in positions)
        {
            raw[position] = value;
        }

        var widened = Infer(raw);
        return widened;
    }

    /// <summary>Writes several values (one per slot).</summary>
    public PdColumn WithMany(IReadOnlyList<int> positions, IReadOnlyList<PyObject> values)
    {
        var column = this;

        for (var i = 0; i < positions.Count; i++)
        {
            column = column.With(positions[i], values[i]);
        }

        return column;
    }

    /// <summary>The value normalized for this dtype, or null when the dtype cannot hold it.</summary>
    public PyObject? Fits(PyObject value) => Type switch
    {
        DType.Object => value,
        DType.Int64 => value is PyInt integer and not PyBool && Pd.FitsLong(integer.Value) ? value : null,
        DType.Float64 => value is PyInt or PyFloat ? Pd.Float(Pd.ToDouble(value)) : value is PyNone or PdNA ? Pd.NaN : null,
        DType.Bool => value is PyBool ? value : null,
        DType.DateTime => value is PyNone or PdNA ? PdNaT.Instance : PdTimestamp.From(value) ?? (value is PdNaT ? value : null),
        DType.TimeDelta => value is PyDelta or PdNaT ? value : value is PyNone ? PdNaT.Instance : null,
        _ => null,
    };

    /// <summary>Concatenates columns, finding the dtype that holds them all.</summary>
    public static PdColumn Concat(IReadOnlyList<PdColumn> parts)
    {
        var total = parts.Sum(static p => (long)p.Length);
        Pd.CheckSize(total);

        if (parts.Count > 0 && parts.All(p => p.Type == parts[0].Type))
        {
            var values = new PyObject[total];
            var at = 0;

            foreach (var part in parts)
            {
                Array.Copy(part.Values, 0, values, at, part.Length);
                at += part.Length;
            }

            return new PdColumn(parts[0].Type, values);
        }

        var raw = new List<PyObject>((int)total);

        foreach (var part in parts)
        {
            raw.AddRange(part.Values);
        }

        // A numeric column next to a bool column is object in pandas, not a number.
        if (parts.Any(static p => p.Type == DType.Bool) && parts.Any(static p => p.Type is DType.Int64 or DType.Float64))
        {
            return new PdColumn(DType.Object, [.. raw]);
        }

        return parts.Any(static p => p.Type == DType.Object) ? new PdColumn(DType.Object, [.. raw]) : Infer(raw);
    }

    /// <summary>The common dtype of a row taken across columns.</summary>
    public static DType Common(IEnumerable<DType> types)
    {
        DType? result = null;

        foreach (var type in types)
        {
            result = result switch
            {
                null => type,
                var same when same == type => type,
                DType.Int64 when type == DType.Float64 => DType.Float64,
                DType.Float64 when type == DType.Int64 => DType.Float64,
                _ => DType.Object,
            };
        }

        return result ?? DType.Object;
    }

    /// <summary>Builds a column of a known common dtype from values of possibly narrower ones.</summary>
    public static PdColumn OfType(DType type, PyObject[] values)
    {
        if (type == DType.Float64)
        {
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = Pd.Float(Pd.ToDouble(values[i]));
            }
        }

        return new PdColumn(type, values);
    }
}

/// <summary>Reads a builtin's arguments by name, as a Python signature would bind them.</summary>
internal sealed class PdArgs
{
    private readonly PyObject?[] _bound;
    private readonly string[] _names;

    public PdArgs(string function, PyObject[] positional, PyDict? keywords, string[] names, bool allowExtra = false)
    {
        Function = function;
        _names = names;

        if (positional.Length > names.Length)
        {
            throw Pd.TypeError($"{function}() takes {names.Length} positional arguments but {positional.Length} were given");
        }

        _bound = new PyObject?[names.Length];
        positional.CopyTo(_bound, 0);

        foreach (var (key, value) in keywords?.Entries ?? [])
        {
            var name = key.Display();
            var at = Array.IndexOf(names, name);

            if (at < 0)
            {
                if (!allowExtra)
                {
                    throw Pd.TypeError($"{function}() got an unexpected keyword argument '{name}'");
                }

                Extra.Add((name, value));
                continue;
            }

            if (_bound[at] is not null)
            {
                throw Pd.TypeError($"{function}() got multiple values for argument '{name}'");
            }

            _bound[at] = value;
        }
    }

    public string Function { get; }

    /// <summary>Sets a parameter, as a wrapper that supplies a different default does.</summary>
    public void Set(string name, PyObject value)
    {
        var at = Array.IndexOf(_names, name);

        if (at >= 0)
        {
            _bound[at] = value;
        }
    }

    /// <summary>Sets a parameter, for a call that forwards to another with one argument changed.</summary>
    public PdArgs WithAxis(PyObject? axis)
    {
        var at = Array.IndexOf(_names, "axis");

        if (at >= 0)
        {
            _bound[at] = axis;
        }

        return this;
    }

    /// <summary>Keyword arguments no parameter claimed, in the order given.</summary>
    public List<(string Name, PyObject Value)> Extra { get; } = [];

    /// <summary>The argument, or null when it was not passed.</summary>
    public PyObject? this[string name] => Array.IndexOf(_names, name) is var at && at >= 0 ? _bound[at] : null;

    /// <summary>The argument, or null when it was not passed or was None.</summary>
    public PyObject? Given(string name) => this[name] is { } value and not PyNone ? value : null;

    /// <summary>The argument, raising when it is absent.</summary>
    public PyObject Required(string name) =>
        this[name] ?? throw Pd.TypeError($"{Function}() missing 1 required positional argument: '{name}'");

    public bool Bool(string name, bool fallback) => Given(name) is { } value ? value.IsTruthy() : fallback;

    public int Int(string name, int fallback) => Given(name) is { } value ? Pd.ToInt(value, name) : fallback;

    public string? Str(string name) => Given(name) switch
    {
        null => null,
        PyStr text => text.Value,
        var other => throw Pd.TypeError($"{Function}(): '{name}' must be a string, not {other.TypeName}"),
    };

    public string Str(string name, string fallback) => Str(name) ?? fallback;
}

/// <summary>
/// The run's environment: the machine (for calling back into Python) and the filesystem
/// (for <c>read_csv</c> and <c>to_csv</c>).
/// </summary>
internal sealed class PdRuntime
{
    /// <summary>Each machine's pandas runtime, kept as long as the machine lives.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<VirtualMachine, PdRuntime> Runtimes = new();

    public PdRuntime(VirtualMachine machine, IPyFileSystem? fileSystem)
    {
        Machine = machine;
        FileSystem = fileSystem;
    }

    /// <summary>
    /// The runtime of the machine running on this thread.
    /// </summary>
    /// <remarks>
    /// A Series has no machine of its own, yet its methods call back into Python and its
    /// <c>read_csv</c> reads the run's filesystem, so they find both here — through
    /// <see cref="VirtualMachine.Running"/>, which is set for exactly the span of a run. It
    /// is deliberately not thread state of pandas' own: a notebook session's cells may run
    /// on different threads, and a pooled thread must never carry one tenant's runtime into
    /// another's run.
    /// </remarks>
    public static PdRuntime? Current =>
        VirtualMachine.Running is { } machine && Runtimes.TryGetValue(machine, out var runtime) ? runtime : null;

    /// <summary>Makes this the runtime of its machine.</summary>
    public void Install() => Runtimes.AddOrUpdate(Machine, this);

    public VirtualMachine Machine { get; }

    public IPyFileSystem? FileSystem { get; }

    /// <summary>The options <c>pd.set_option</c> and <c>pd.options</c> set, for this run only.</summary>
    public Dictionary<string, PyObject> Options { get; } = new(StringComparer.Ordinal);

    /// <summary>An integer display option, or null when it is set to None (no limit).</summary>
    public int? IntOption(string key, int fallback) =>
        Options.TryGetValue(key, out var value) ? value is PyNone ? null : value is PyInt integer ? (int)System.Numerics.BigInteger.Min(integer.Value, int.MaxValue) : fallback : fallback;

    /// <summary>Calls a Python callable.</summary>
    public PyObject Call(PyObject callable, params PyObject[] arguments) => Machine.Call(callable, arguments);

    /// <summary>Runs a builtin body with this runtime current and exceptions translated.</summary>
    public PyObject Run(Func<PyObject> body)
    {
        // A pandas builtin may be called by host code outside a run (a display hook
        // formatting a frame); the machine is made the running one for its duration.
        if (ReferenceEquals(VirtualMachine.Running, Machine))
        {
            return Pd.Guard(body);
        }

        using var scope = Machine.Enter();
        return Pd.Guard(body);
    }

    /// <summary>A method of a pandas object: named parameters, bound and guarded.</summary>
    public PyBuiltinFunction Method(string name, string[] parameters, Func<PdArgs, PyObject> body, bool allowExtra = false) =>
        new(name, (positional, keywords) => Run(() => body(new PdArgs(name, positional, keywords, parameters, allowExtra))));

    /// <summary>The <c>numpy</c> module when this run can import it, else null.</summary>
    public PyObject? Numpy()
    {
        if (Machine.Modules.TryGetValue("numpy", out var module))
        {
            return module;
        }

        // Asked once per run: a run without numpy keeps answering with lists.
        if (_numpyAbsent)
        {
            return null;
        }

        if (Machine.ModuleResolver?.Invoke("numpy") is { } resolved)
        {
            Machine.Modules["numpy"] = resolved;
            return resolved;
        }

        _numpyAbsent = true;
        return null;
    }

    private bool _numpyAbsent;

    /// <summary>
    /// What pandas returns as an ndarray: <c>numpy.array(values)</c> when numpy is importable,
    /// and otherwise the plain list.
    /// </summary>
    public PyObject Array(PyList values)
    {
        if (Numpy() is { } numpy && numpy.GetAttribute("array") is { } array)
        {
            return Call(array, values);
        }

        return values;
    }
}

/// <summary><c>pd.NaT</c>: the missing value of a datetime column.</summary>
internal sealed class PdNaT : PyObject
{
    private PdNaT()
    {
    }

    public static PdNaT Instance { get; } = new();

    public override string TypeName => "NaTType";

    public override string Repr() => "NaT";

    public override bool IsTruthy() => true;

    public override bool PyEquals(PyObject other) => false;

    public override BigInteger PyHash() => 0x4E615400;

    public override PyObject? GetAttribute(string name) => name switch
    {
        "year" or "month" or "day" or "hour" or "minute" or "second" or "dayofweek" => Pd.NaN,
        "isoformat" => new PyBuiltinFunction("isoformat", static _ => new PyStr("NaT")),
        _ => null,
    };
}

/// <summary><c>pd.NA</c>: the scalar missing marker.</summary>
internal sealed class PdNA : PyObject
{
    private PdNA()
    {
    }

    public static PdNA Instance { get; } = new();

    public override string TypeName => "NAType";

    public override string Repr() => "<NA>";

    public override bool IsTruthy() =>
        throw Pd.TypeError("boolean value of NA is ambiguous");

    public override BigInteger PyHash() => 0x4E4100;

    public override bool PyEquals(PyObject other) => ReferenceEquals(this, other);
}
