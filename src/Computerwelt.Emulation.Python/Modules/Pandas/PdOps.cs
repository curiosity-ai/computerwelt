using System.Numerics;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules.Pandas;

/// <summary>
/// The vectorized operators: elementwise arithmetic and comparison over columns, with
/// numpy's rules for each dtype, and label alignment between two Series.
/// </summary>
internal static class PdOps
{
    /// <summary>One side of an elementwise operation: a column, or a scalar broadcast.</summary>
    internal readonly struct Operand
    {
        private readonly PdColumn? _column;
        private readonly PyObject? _scalar;

        public Operand(PdColumn column)
        {
            _column = column;
            _scalar = null;
            Type = column.Type;
        }

        public Operand(PyObject scalar)
        {
            _column = null;
            _scalar = PdColumn.Unwrap(scalar);
            Type = _scalar switch
            {
                PyBool => DType.Bool,
                PyInt integer when Pd.FitsLong(integer.Value) => DType.Int64,
                PyFloat => DType.Float64,
                PdTimestamp or PyDateTime => DType.DateTime,
                PyDelta => DType.TimeDelta,
                _ => DType.Object,
            };
        }

        public DType Type { get; }

        /// <summary>The column, or null for a scalar.</summary>
        public PdColumn? Column => _column;

        /// <summary>The scalar, or null for a column.</summary>
        public PyObject? Scalar => _scalar;

        public PyObject this[int i] => _column is { } column ? column.Values[i] : _scalar!;

        public bool IsNumeric => Type is DType.Int64 or DType.Float64 or DType.Bool;

        public double Double(int i) => Pd.ToDouble(this[i]);

        public long Long(int i) => this[i] switch
        {
            PyBool flag => flag.Value ? 1 : 0,
            PyInt integer => (long)integer.Value,
            var other => (long)Pd.ToDouble(other),
        };
    }

    /// <summary>Applies an arithmetic or bitwise operator elementwise.</summary>
    public static PdColumn Arithmetic(string op, Operand left, Operand right, int count)
    {
        Pd.CheckSize(count);

        if (op is "&" or "|" or "^")
        {
            return Logical(op, left, right, count);
        }

        if (PdVector.Arithmetic(op, left, right, count) is { } vectorised)
        {
            return vectorised;
        }

        if (left.IsNumeric && right.IsNumeric)
        {
            var integral = left.Type != DType.Float64 && right.Type != DType.Float64;

            if (integral && op != "/" && Integers(op, left, right, count) is { } exact)
            {
                return exact;
            }

            var result = new double[count];

            for (var i = 0; i < count; i++)
            {
                result[i] = Floats(op, left.Double(i), right.Double(i));
            }

            return PdColumn.Floats(result);
        }

        var raw = new PyObject[count];

        for (var i = 0; i < count; i++)
        {
            var x = left[i];
            var y = right[i];

            if (Pd.IsMissing(x) || Pd.IsMissing(y))
            {
                raw[i] = left.Type is DType.DateTime || right.Type is DType.DateTime ? PdNaT.Instance : Pd.NaN;
                continue;
            }

            raw[i] = Operators.Binary(op, x, y);
        }

        return PdColumn.Infer(raw);
    }

    private static PdColumn Logical(string op, Operand left, Operand right, int count)
    {
        if (PdVector.Logical(op, left, right, count) is { } vectorised)
        {
            return vectorised;
        }

        if (left.Type == DType.Int64 && right.Type == DType.Int64)
        {
            var values = new long[count];
            for (var i = 0; i < count; i++)
            {
                var (x, y) = (left.Long(i), right.Long(i));
                values[i] = op switch { "&" => x & y, "|" => x | y, _ => x ^ y };
            }

            return PdColumn.Ints(values);
        }

        var result = new bool[count];

        for (var i = 0; i < count; i++)
        {
            var x = Truth(left[i]);
            var y = Truth(right[i]);
            result[i] = op switch { "&" => x && y, "|" => x || y, _ => x ^ y };
        }

        return PdColumn.Bools(result);

        static bool Truth(PyObject value) => !Pd.IsMissing(value) && value.IsTruthy();
    }

    private static PdColumn? Integers(string op, Operand left, Operand right, int count)
    {
        var values = new long[count];

        for (var i = 0; i < count; i++)
        {
            var x = left.Long(i);
            var y = right.Long(i);

            switch (op)
            {
                case "+":
                    values[i] = unchecked(x + y);
                    break;
                case "-":
                    values[i] = unchecked(x - y);
                    break;
                case "*":
                    values[i] = unchecked(x * y);
                    break;
                case "//" or "%" when y == 0:
                    return null;
                case "//":
                    values[i] = FloorDiv(x, y);
                    break;
                case "%":
                    values[i] = x - (FloorDiv(x, y) * y);
                    break;
                case "**":
                    if (y < 0)
                    {
                        throw Pd.ValueError("Integers to negative integer powers are not allowed.");
                    }

                    values[i] = unchecked((long)BigInteger.Pow(x, (int)Math.Min(y, 64)));
                    break;
                case "<<":
                    values[i] = x << (int)y;
                    break;
                case ">>":
                    values[i] = x >> (int)y;
                    break;
                default:
                    throw Pd.TypeError($"unsupported operand type(s) for {op}: 'int' and 'int'");
            }
        }

        return PdColumn.Ints(values);

        static long FloorDiv(long x, long y)
        {
            var quotient = x / y;
            return (x % y != 0) && ((x < 0) != (y < 0)) ? quotient - 1 : quotient;
        }
    }

    /// <summary>numpy's float operators, including its divmod and division by zero.</summary>
    public static double Floats(string op, double a, double b) => op switch
    {
        "+" => a + b,
        "-" => a - b,
        "*" => a * b,
        "/" => a / b,
        "**" => Math.Pow(a, b),
        "//" => DivMod(a, b).Floor,
        "%" => DivMod(a, b).Mod,
        _ => throw Pd.TypeError($"unsupported operand type(s) for {op}: 'float' and 'float'"),
    };

    private static (double Floor, double Mod) DivMod(double a, double b)
    {
        var mod = a % b;

        if (b == 0)
        {
            return (a / b, double.NaN);
        }

        var div = (a - mod) / b;

        if (mod != 0)
        {
            if ((b < 0) != (mod < 0))
            {
                mod += b;
                div -= 1.0;
            }
        }
        else
        {
            mod = Math.CopySign(0.0, b);
        }

        double floor;

        if (div != 0)
        {
            floor = Math.Floor(div);

            if (div - floor > 0.5)
            {
                floor += 1.0;
            }
        }
        else
        {
            floor = Math.CopySign(0.0, a / b);
        }

        return (floor, mod);
    }

    /// <summary>Applies a comparison elementwise; missing values compare false (true for !=).</summary>
    public static PdColumn Compare(string op, Operand left, Operand right, int count)
    {
        Pd.CheckSize(count);

        if (PdVector.Compare(op, left, right, count) is { } vectorised)
        {
            return vectorised;
        }

        var result = new bool[count];

        if (left.IsNumeric && right.IsNumeric)
        {
            var integral = left.Type != DType.Float64 && right.Type != DType.Float64;

            for (var i = 0; i < count; i++)
            {
                int? order = integral
                    ? left.Long(i).CompareTo(right.Long(i))
                    : double.IsNaN(left.Double(i)) || double.IsNaN(right.Double(i)) ? null : left.Double(i).CompareTo(right.Double(i));
                result[i] = Decide(op, order);
            }

            return PdColumn.Bools(result);
        }

        for (var i = 0; i < count; i++)
        {
            var x = left[i];
            var y = right[i];

            if (left.Type == DType.DateTime && y is PyStr text)
            {
                y = PdTimestamp.Parse(text, null) ?? y;
            }

            if (right.Type == DType.DateTime && x is PyStr text2)
            {
                x = PdTimestamp.Parse(text2, null) ?? x;
            }

            if (Pd.IsMissing(x) || Pd.IsMissing(y))
            {
                result[i] = op == "!=";
                continue;
            }

            if (op is "==" or "!=")
            {
                var equal = PyObject.SameOrEqual(x, y);
                result[i] = op == "==" ? equal : !equal;
                continue;
            }

            if (x is PyStr && Pd.IsNumber(y) || y is PyStr && Pd.IsNumber(x))
            {
                throw Pd.TypeError($"'{op}' not supported between instances of '{x.TypeName}' and '{y.TypeName}'");
            }

            result[i] = Operators.Compare(op, x, y).IsTruthy();
        }

        return PdColumn.Bools(result);
    }

    private static bool Decide(string op, int? order) => order is not { } decided
        ? op == "!="
        : op switch
        {
            "==" => decided == 0,
            "!=" => decided != 0,
            "<" => decided < 0,
            "<=" => decided <= 0,
            ">" => decided > 0,
            _ => decided >= 0,
        };

    /// <summary>
    /// Aligns two indexes for an arithmetic operation: the result index (their union, sorted
    /// when the two differ) and, for each result row, the position in each side or -1.
    /// </summary>
    public static (PdIndex Index, int[] Left, int[] Right) Align(PdIndex left, PdIndex right)
    {
        if (left.SameAs(right))
        {
            var identity = Enumerable.Range(0, left.Count).ToArray();
            return (left, identity, identity);
        }

        if (!left.IsUnique || !right.IsUnique)
        {
            throw Pd.ValueError("cannot reindex on an axis with duplicate labels");
        }

        var union = UnionIndex(left, right, sort: true);
        var l = new int[union.Count];
        var r = new int[union.Count];

        for (var i = 0; i < union.Count; i++)
        {
            l[i] = left.Find(union[i]);
            r[i] = right.Find(union[i]);
        }

        return (union, l, r);
    }

    /// <summary>The labels of both, the left's first; sorted when asked and when they order.</summary>
    public static PdIndex UnionIndex(PdIndex left, PdIndex right, bool sort)
    {
        if (left.SameAs(right))
        {
            return left;
        }

        var labels = new List<PyObject>(left.Labels.Values);
        var added = new HashSet<PyKey>();

        foreach (var label in right.Labels.Values)
        {
            if (left.Find(label) < 0 && added.Add(new PyKey(Normalize(label))))
            {
                labels.Add(label);
            }
        }

        var result = PdIndex.Of(labels);

        if (sort)
        {
            try
            {
                result = result.Take(PdSort.Order([result.Labels], [true], "last"));
            }
            catch (PyRaise raise) when (raise.Exception.IsInstanceOf(PyExceptionType.TypeError))
            {
                // Labels of types that do not order stay in the order they were found.
            }
        }

        var name = Pd.Same(left.Name, right.Name) || right.Name is PyNone ? left.Name : PyNone.Instance;
        return result.WithName(name);
    }

    /// <summary>Whether each row repeats an earlier (or, for keep='last', a later) one.</summary>
    public static List<bool> Duplicated(List<IReadOnlyList<PyObject>> rows, PyObject keep)
    {
        var mode = keep switch
        {
            PyStr { Value: "first" } => 0,
            PyStr { Value: "last" } => 1,
            PyBool { Value: false } => 2,
            _ => throw Pd.ValueError("keep must be either \"first\", \"last\" or False"),
        };

        var counts = new Dictionary<PyKey, int>();
        var keys = rows.Select(RowKey).ToList();

        foreach (var key in keys)
        {
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        var result = new List<bool>(rows.Count);
        var seen = new HashSet<PyKey>();

        if (mode == 1)
        {
            for (var i = keys.Count - 1; i >= 0; i--)
            {
                result.Add(!seen.Add(keys[i]));
            }

            result.Reverse();
            return result;
        }

        foreach (var key in keys)
        {
            result.Add(mode == 2 ? counts[key] > 1 : !seen.Add(key));
        }

        return result;
    }

    /// <summary>A hashable key for a row of values, with every missing value one key.</summary>
    public static PyKey RowKey(IReadOnlyList<PyObject> row) =>
        new(row.Count == 1 ? Normalize(row[0]) : new PyTuple([.. row.Select(Normalize)]));

    /// <summary>Maps every missing spelling to one value, so NaN groups with NaN.</summary>
    public static PyObject Normalize(PyObject value) => Pd.IsMissing(value) ? Pd.NaN : value;
}
