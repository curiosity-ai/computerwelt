using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules.Pandas;

/// <summary>Sorting: a stable order over one or more key columns.</summary>
internal static class PdSort
{
    /// <summary>
    /// The positions that sort the rows by the keys, stably, with missing values placed
    /// first or last whatever the direction.
    /// </summary>
    public static int[] Order(IReadOnlyList<PdColumn> keys, IReadOnlyList<bool> ascending, string naPosition)
    {
        if (naPosition is not ("last" or "first"))
        {
            throw Pd.ValueError($"invalid na_position: {naPosition}");
        }

        var count = keys.Count > 0 ? keys[0].Length : 0;
        var order = Enumerable.Range(0, count).ToArray();
        var naLast = naPosition == "last";

        // Numeric keys are compared as doubles once, rather than through the object model
        // on every comparison.
        var doubles = keys.Select(static k => k.IsNumeric ? k.Doubles() : null).ToArray();

        int Compare(int a, int b)
        {
            for (var k = 0; k < keys.Count; k++)
            {
                var column = keys[k];
                var missingA = column.IsMissing(a);
                var missingB = column.IsMissing(b);

                if (missingA || missingB)
                {
                    if (missingA && missingB)
                    {
                        continue;
                    }

                    return missingA == naLast ? 1 : -1;
                }

                var result = doubles[k] is { } values
                    ? column.Type == DType.Int64 ? ((PyInt)column.Values[a]).Value.CompareTo(((PyInt)column.Values[b]).Value) : values[a].CompareTo(values[b])
                    : Pd.Order(column.Values[a], column.Values[b]);

                if (result != 0)
                {
                    return ascending[Math.Min(k, ascending.Count - 1)] ? result : -result;
                }
            }

            return a.CompareTo(b);
        }

        // A merge sort through LINQ's OrderBy is stable; the tie-break on position makes the
        // comparison total, so Array.Sort would do as well.
        Array.Sort(order, Compare);
        return order;
    }
}

/// <summary>Reductions over a column: the work behind sum, mean, std and the rest.</summary>
internal static class PdReduce
{
    /// <summary>The reductions <c>agg</c> and friends accept by name.</summary>
    public static readonly HashSet<string> Names = new(StringComparer.Ordinal)
    {
        "sum", "prod", "product", "mean", "median", "min", "max", "std", "var", "count", "nunique",
        "first", "last", "size", "any", "all", "sem", "skew",
    };

    /// <summary>Whether a reduction needs numbers (so a string column refuses it).</summary>
    public static bool NeedsNumbers(string how) => how is "mean" or "median" or "std" or "var" or "sem" or "skew" or "prod" or "product";

    /// <summary>Reduces a column to a scalar.</summary>
    public static PyObject Reduce(PdColumn column, string how, bool skipna = true, int ddof = 1, int minCount = 0)
    {
        switch (how)
        {
            case "count":
                return PyInt.From(Valid(column).Count);
            case "size":
                return PyInt.From(column.Length);
            case "nunique":
                return PyInt.From(new PdSeries(column, PdIndex.Default(column.Length), null).CountUnique(true));
            case "first":
            case "last":
            {
                var valid = Valid(column);
                return valid.Count == 0 ? PdColumn.MissingFor(column.Type) : column.Values[how == "first" ? valid[0] : valid[^1]];
            }

            case "any":
            case "all":
            {
                var valid = Valid(column);
                return PyBool.Of(how == "any" ? valid.Any(i => column.Values[i].IsTruthy()) : valid.All(i => column.Values[i].IsTruthy()));
            }
        }

        if (!skipna && Enumerable.Range(0, column.Length).Any(column.IsMissing))
        {
            return column.Type == DType.DateTime && how is "min" or "max" ? PdNaT.Instance : Pd.NaN;
        }

        var positions = Valid(column);

        if (column.Type == DType.DateTime)
        {
            return Times(column, positions, how);
        }

        // An object column sums, multiplies and orders with Python's own operators, so a big
        // integer stays exact; only the statistics need its values as doubles.
        if (column.Type == DType.Object && (how is "sum" or "prod" or "product" or "min" or "max" || !positions.All(i => Pd.IsNumber(column.Values[i]))))
        {
            return Objects(column, positions, how);
        }

        if (column.Type == DType.TimeDelta)
        {
            return Objects(column, positions, how);
        }

        // Numbers from here on, whether the column is int, float, bool or object-of-numbers.
        var integral = column.Type is DType.Int64 or DType.Bool
            || (column.Type == DType.Object && positions.All(i => column.Values[i] is PyInt));

        switch (how)
        {
            case "sum":
            {
                if (positions.Count < minCount)
                {
                    return Pd.NaN;
                }

                if (integral)
                {
                    long total = 0;
                    foreach (var i in positions)
                    {
                        total = unchecked(total + Long(column.Values[i]));
                    }

                    return PyInt.From(total);
                }

                return Pd.Float(Pd.PairwiseSum(Filled(column), 0, column.Length));
            }

            case "prod":
            case "product":
            {
                if (positions.Count < minCount)
                {
                    return Pd.NaN;
                }

                if (integral)
                {
                    long total = 1;
                    foreach (var i in positions)
                    {
                        total = unchecked(total * Long(column.Values[i]));
                    }

                    return PyInt.From(total);
                }

                var product = 1.0;
                foreach (var i in positions)
                {
                    product *= Pd.ToDouble(column.Values[i]);
                }

                return Pd.Float(product);
            }

            case "mean":
                return positions.Count == 0 ? Pd.NaN : Pd.Float(Pd.PairwiseSum(Filled(column), 0, column.Length) / positions.Count);

            case "median":
            {
                if (positions.Count == 0)
                {
                    return Pd.NaN;
                }

                var sorted = positions.Select(i => Pd.ToDouble(column.Values[i])).Order().ToArray();
                var middle = sorted.Length / 2;
                return Pd.Float(sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2);
            }

            case "var":
            case "std":
            case "sem":
                return Pd.Float(Spread(column, positions, how, ddof));

            case "skew":
                return Pd.Float(Skew(column, positions));

            case "min":
            case "max":
            {
                if (positions.Count == 0)
                {
                    return Pd.NaN;
                }

                var best = positions[0];
                foreach (var i in positions)
                {
                    var order = Pd.Order(column.Values[i], column.Values[best]);
                    if (how == "min" ? order < 0 : order > 0)
                    {
                        best = i;
                    }
                }

                return column.Values[best];
            }
        }

        throw Pd.ValueError($"unknown reduction '{how}'");
    }

    /// <summary>numpy's (and so pandas') variance: the mean, then the squared deviations, pairwise.</summary>
    private static double Spread(PdColumn column, List<int> positions, string how, int ddof)
    {
        var count = positions.Count;

        if (count - ddof <= 0)
        {
            return double.NaN;
        }

        var values = Filled(column);
        var mean = Pd.PairwiseSum(values, 0, values.Length) / count;
        var squares = new double[values.Length];

        foreach (var i in positions)
        {
            var deviation = mean - values[i];
            squares[i] = deviation * deviation;
        }

        var variance = Pd.PairwiseSum(squares, 0, squares.Length) / (count - ddof);
        return how switch
        {
            "var" => variance,
            "std" => Math.Sqrt(variance),
            _ => Math.Sqrt(variance) / Math.Sqrt(count),
        };
    }

    private static double Skew(PdColumn column, List<int> positions)
    {
        var count = positions.Count;

        if (count < 3)
        {
            return double.NaN;
        }

        var values = positions.Select(i => Pd.ToDouble(column.Values[i])).ToArray();
        var mean = Pd.PairwiseSum(values, 0, count) / count;
        var adjusted = values.Select(v => v - mean).ToArray();
        var m2 = Pd.PairwiseSum(adjusted.Select(static a => a * a).ToArray(), 0, count);
        var m3 = Pd.PairwiseSum(adjusted.Select(static a => a * a * a).ToArray(), 0, count);

        if (m2 == 0)
        {
            return 0;
        }

        return count * Math.Sqrt(count - 1) / (count - 2) * (m3 / Math.Pow(m2, 1.5));
    }

    private static PyObject Times(PdColumn column, List<int> positions, string how)
    {
        if (positions.Count == 0)
        {
            return how == "sum" ? throw Pd.TypeError("datetime64 type does not support sum operations") : PdNaT.Instance;
        }

        var micros = positions.Select(i => ((PdTimestamp)column.Values[i]).Micro).ToList();

        return how switch
        {
            "min" => new PdTimestamp(micros.Min()),
            "max" => new PdTimestamp(micros.Max()),
            "mean" => new PdTimestamp((long)micros.Average(static m => (decimal)m)),
            "median" => new PdTimestamp(Median(micros)),
            _ => throw Pd.TypeError($"datetime64 type does not support {how} operations"),
        };

        static long Median(List<long> values)
        {
            values.Sort();
            var middle = values.Count / 2;
            return values.Count % 2 == 1 ? values[middle] : (values[middle - 1] / 2) + (values[middle] / 2);
        }
    }

    private static PyObject Objects(PdColumn column, List<int> positions, string how)
    {
        switch (how)
        {
            case "sum":
            {
                if (positions.Count == 0)
                {
                    return PyInt.From(0);
                }

                var total = column.Values[positions[0]];
                foreach (var i in positions.Skip(1))
                {
                    total = Operators.Binary("+", total, column.Values[i]);
                }

                return total;
            }

            case "prod" or "product":
            {
                PyObject product = PyInt.From(1);
                foreach (var i in positions)
                {
                    product = Operators.Binary("*", product, column.Values[i]);
                }

                return product;
            }

            case "min":
            case "max":
            {
                if (positions.Count == 0)
                {
                    return Pd.NaN;
                }

                var best = column.Values[positions[0]];
                foreach (var i in positions.Skip(1))
                {
                    var candidate = column.Values[i];
                    if (Operators.Compare(how == "min" ? "<" : ">", candidate, best).IsTruthy())
                    {
                        best = candidate;
                    }
                }

                return best;
            }

            default:
            {
                var sample = positions.Count > 0 ? column.Values[positions[0]] : PyNone.Instance;
                throw Pd.TypeError(
                    $"Could not convert {(sample is PyStr text ? text.Value : sample.Repr())} to numeric ({how} of a column of dtype {Pd.Name(column.Type)})");
            }
        }
    }

    /// <summary>The positions of the non-missing values.</summary>
    public static List<int> Valid(PdColumn column)
    {
        var result = new List<int>(column.Length);

        for (var i = 0; i < column.Length; i++)
        {
            if (!column.IsMissing(i))
            {
                result.Add(i);
            }
        }

        return result;
    }

    /// <summary>The values as doubles with every missing slot filled with 0, as nansum fills them.</summary>
    private static double[] Filled(PdColumn column)
    {
        var values = new double[column.Length];

        for (var i = 0; i < values.Length; i++)
        {
            values[i] = column.IsMissing(i) ? 0 : Pd.ToDouble(column.Values[i]);
        }

        return values;
    }

    private static long Long(PyObject value) => value switch
    {
        PyBool flag => flag.Value ? 1 : 0,
        PyInt integer => (long)integer.Value,
        _ => (long)Pd.ToDouble(value),
    };

    /// <summary>numpy's default (linear) quantile over the non-missing values.</summary>
    public static double Quantile(IReadOnlyList<double> sortedValues, double q)
    {
        if (sortedValues.Count == 0)
        {
            return double.NaN;
        }

        var position = q * (sortedValues.Count - 1);
        var lower = (int)Math.Floor(position);
        var upper = Math.Min(lower + 1, sortedValues.Count - 1);
        var fraction = position - lower;

        // numpy's lerp: exact at both ends.
        var a = sortedValues[lower];
        var b = sortedValues[upper];
        var diff = b - a;
        return fraction >= 0.5 ? b - (diff * (1 - fraction)) : a + (diff * fraction);
    }
}
