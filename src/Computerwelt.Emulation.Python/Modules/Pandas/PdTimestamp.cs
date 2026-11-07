using System.Globalization;
using System.Numerics;
using System.Text;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules.Pandas;

/// <summary>
/// <c>pd.Timestamp</c>: a naive point in time, held as microseconds on the same scale as
/// <c>datetime.datetime</c> (so the two convert exactly). pandas holds nanoseconds; this
/// port's resolution is the microsecond, which is <c>datetime</c>'s.
/// </summary>
internal sealed class PdTimestamp : PyObject, IPyOperand
{
    public PdTimestamp(long micro) => Micro = micro;

    /// <summary>Microseconds since 0001-01-01 minus one day (the datetime instant scale).</summary>
    public long Micro { get; }

    public override string TypeName => "Timestamp";

    private static long EpochMicro => (long)DatetimeModule.Epoch;

    /// <summary>Nanoseconds since the Unix epoch, as <c>Timestamp.value</c> reports.</summary>
    public long Nanoseconds => (Micro - EpochMicro) * 1000;

    internal DatetimeModule.Fields Parts => DatetimeModule.Split(Micro);

    public bool IsMidnight => Micro % PyDelta.MicrosecondsPerDay == 0;

    public static PdTimestamp FromFields(int year, int month, int day, int hour = 0, int minute = 0, int second = 0, int microsecond = 0)
    {
        if (month is < 1 or > 12 || day < 1 || year is < 1 or > 9999 || day > PyDate.DaysInMonth(year, month))
        {
            throw Pd.ValueError($"day is out of range for month");
        }

        var micro = ((long)PyDate.OrdinalOf(year, month, day) * PyDelta.MicrosecondsPerDay)
            + ((((hour * 60L) + minute) * 60L) + second) * 1_000_000L + microsecond;
        return new PdTimestamp(micro);
    }

    /// <summary>Converts a datetime-like value, or returns null.</summary>
    public static PdTimestamp? From(PyObject value) => value switch
    {
        PdTimestamp moment => moment,
        // An aware datetime becomes its UTC instant: this port's timestamps are naive.
        PyDateTime moment => new PdTimestamp((long)moment.Instant),
        PyDate date => FromFields(date.Year, date.Month, date.Day),
        _ => null,
    };

    /// <summary>The text pandas shows: the date alone when <paramref name="dateOnly"/>.</summary>
    public string Text(bool dateOnly)
    {
        var p = Parts;
        var text = $"{p.Year:D4}-{p.Month:D2}-{p.Day:D2}";

        if (dateOnly)
        {
            return text;
        }

        text += $" {p.Hour:D2}:{p.Minute:D2}:{p.Second:D2}";
        return p.Microsecond != 0 ? text + $".{p.Microsecond:D6}" : text;
    }

    public override string Repr() => $"Timestamp('{Text(false)}')";

    public override string Display() => Text(false);

    public override bool PyEquals(PyObject other) => From(other) is { } moment && moment.Micro == Micro;

    public override int? PyCompare(PyObject other) => (From(other) ?? (other is PyStr text ? Parse(text, null) as PdTimestamp : null)) is { } moment
        ? Micro.CompareTo(moment.Micro)
        : null;

    public override BigInteger PyHash() => Numbers.Hash(new BigInteger(Micro));

    public override string? PyFormat(string spec) => spec.Length == 0 ? Display() : DatetimeModule.Expand(Parts, spec);

    public PyDateTime ToDateTime() => new(Parts, null);

    public override PyObject? GetAttribute(string name) => Pd.Guard(() => AttributeCore(name));

    private PyObject? AttributeCore(string name)
    {
        var p = Parts;
        var weekday = PyDate.WeekdayOf(PyDate.OrdinalOf(p.Year, p.Month, p.Day));

        return name switch
        {
            "year" => PyInt.From(p.Year),
            "month" => PyInt.From(p.Month),
            "day" => PyInt.From(p.Day),
            "hour" => PyInt.From(p.Hour),
            "minute" => PyInt.From(p.Minute),
            "second" => PyInt.From(p.Second),
            "microsecond" => PyInt.From(p.Microsecond),
            "nanosecond" => PyInt.From(0),
            "value" => PyInt.From(Nanoseconds),
            "dayofweek" or "day_of_week" => PyInt.From(weekday),
            "dayofyear" or "day_of_year" => PyInt.From(PyDate.DayOfYear(p.Year, p.Month, p.Day)),
            "quarter" => PyInt.From(((p.Month - 1) / 3) + 1),
            "days_in_month" or "daysinmonth" => PyInt.From(PyDate.DaysInMonth(p.Year, p.Month)),
            "is_month_start" => PyBool.Of(p.Day == 1),
            "is_month_end" => PyBool.Of(p.Day == PyDate.DaysInMonth(p.Year, p.Month)),
            "is_year_start" => PyBool.Of(p.Day == 1 && p.Month == 1),
            "is_year_end" => PyBool.Of(p.Day == 31 && p.Month == 12),
            "is_leap_year" => PyBool.Of(PyDate.IsLeap(p.Year)),
            "tzinfo" or "tz" => PyNone.Instance,
            "weekday" => new PyBuiltinFunction("weekday", _ => PyInt.From(weekday)),
            "isoweekday" => new PyBuiltinFunction("isoweekday", _ => PyInt.From(weekday + 1)),
            "day_name" => new PyBuiltinFunction("day_name", (_, _) => new PyStr(DayName(weekday))),
            "month_name" => new PyBuiltinFunction("month_name", (_, _) =>
                new PyStr(CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(p.Month))),
            "date" => new PyBuiltinFunction("date", _ => new PyDate(p.Year, p.Month, p.Day)),
            "to_pydatetime" => new PyBuiltinFunction("to_pydatetime", _ => ToDateTime()),
            "normalize" => new PyBuiltinFunction("normalize", _ =>
                new PdTimestamp(Micro - (Micro % PyDelta.MicrosecondsPerDay))),
            "isoformat" => new PyBuiltinFunction("isoformat", (arguments, _) =>
                new PyStr(Text(false).Replace(' ', arguments.Length > 0 ? arguments[0].Display()[0] : 'T'))),
            "strftime" => new PyBuiltinFunction("strftime", arguments =>
                new PyStr(DatetimeModule.Expand(p, Arity.At("strftime", arguments, 0).Display()))),
            "timestamp" => new PyBuiltinFunction("timestamp", _ =>
                new PyFloat((Micro - EpochMicro) / 1_000_000.0)),
            "isocalendar" => new PyBuiltinFunction("isocalendar", _ =>
            {
                var date = new DateTime(p.Year, p.Month, p.Day, 0, 0, 0, DateTimeKind.Unspecified);
                return new PyTuple([PyInt.From(ISOWeek.GetYear(date)), PyInt.From(ISOWeek.GetWeekOfYear(date)), PyInt.From(weekday + 1)]);
            }),
            _ => null,
        };
    }

    public static string DayName(int weekday) =>
        CultureInfo.InvariantCulture.DateTimeFormat.DayNames[(weekday + 1) % 7];

    public PyObject? Binary(string op, PyObject other, bool reflected) => Pd.Guard(() => BinaryCore(op, other, reflected));

    private PyObject? BinaryCore(string op, PyObject other, bool reflected)
    {
        switch (op, other, reflected)
        {
            case ("+", PyDelta delta, _):
                return new PdTimestamp(Micro + (long)delta.Total);
            case ("-", PyDelta delta, false):
                return new PdTimestamp(Micro - (long)delta.Total);
            case ("-", _, _) when From(other) is { } moment:
                return PyDelta.FromMicroseconds(reflected ? moment.Micro - Micro : Micro - moment.Micro);
            default:
                return null;
        }
    }

    public PyObject? Unary(string op) => null;

    public PyObject? Compare(string op, PyObject other) => Pd.Guard(() => CompareCore(op, other));

    private PyObject? CompareCore(string op, PyObject other)
    {
        if (other is PdSeries or PdFrame or PdIndex)
        {
            return null;
        }

        var order = PyCompare(other);

        if (order is not { } decided)
        {
            return op switch
            {
                "==" => PyBool.False,
                "!=" => PyBool.True,
                _ => null,
            };
        }

        return PyBool.Of(op switch
        {
            "==" => decided == 0,
            "!=" => decided != 0,
            "<" => decided < 0,
            "<=" => decided <= 0,
            ">" => decided > 0,
            _ => decided >= 0,
        });
    }

    /// <summary>
    /// Parses a value into a timestamp: text (ISO and the common US forms, or a
    /// <c>strptime</c> format), a datetime, a date, or a number of <paramref name="unit"/>s
    /// since the epoch. Returns NaT for a missing value and null when the text cannot be read.
    /// </summary>
    public static PyObject? Parse(PyObject value, string? format, string unit = "ns", bool dayFirst = false)
    {
        if (Pd.IsMissing(value))
        {
            return PdNaT.Instance;
        }

        if (From(value) is { } moment)
        {
            return moment;
        }

        if (value is PyInt or PyFloat)
        {
            var number = Pd.ToDouble(value);
            var micro = unit switch
            {
                "s" => number * 1e6,
                "ms" => number * 1e3,
                "us" => number,
                "D" => number * 86_400e6,
                _ => number / 1e3,
            };

            return new PdTimestamp(EpochMicro + (long)Math.Round(micro));
        }

        if (value is not PyStr text)
        {
            return null;
        }

        var raw = text.Value.Trim();

        if (raw.Length == 0 || raw is "NaT" or "nat" or "NaN" or "nan" or "None")
        {
            return PdNaT.Instance;
        }

        if (format is not null && format != "ISO8601" && format != "mixed")
        {
            return ParseExact(raw, format);
        }

        return ParseText(raw, dayFirst);
    }

    private static PyObject? ParseExact(string raw, string format)
    {
        if (PdRuntime.Current is not { } runtime || runtime.Machine.Import("datetime") is not { } module
            || module.GetAttribute("datetime") is not { } type
            || Attributes.TryGet(runtime.Machine, type, "strptime") is not { } strptime)
        {
            return null;
        }

        try
        {
            return From(runtime.Call(strptime, new PyStr(raw), new PyStr(format)));
        }
        catch (PyRaise raise) when (raise.Exception.IsInstanceOf(PyExceptionType.ValueError))
        {
            return null;
        }
    }

    private static readonly string[] ExactFormats =
    [
        "yyyy-MM-dd", "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-ddTHH:mm:ss.FFFFFFF", "yyyy-MM-dd HH:mm:ss.FFFFFFF", "yyyy/MM/dd", "yyyy/MM/dd HH:mm:ss",
        "yyyy/MM/dd HH:mm", "yyyyMMdd", "yyyy-MM", "yyyy", "M/d/yyyy", "M/d/yyyy H:mm", "M/d/yyyy H:mm:ss",
        "M/d/yy", "M-d-yyyy", "yyyy-M-d", "yyyy.MM.dd", "d.M.yyyy",
    ];

    private static PyObject? ParseText(string raw, bool dayFirst)
    {
        var text = raw;

        // A UTC marker or an offset is applied, and the result is naive UTC: this port has
        // no time-zone-aware timestamps.
        TimeSpan offset = TimeSpan.Zero;

        if (text.EndsWith('Z'))
        {
            text = text[..^1];
        }
        else if (text.Length > 6 && (text[^6] == '+' || text[^6] == '-') && text[^3] == ':' && text.Contains('T', StringComparison.Ordinal) | text.Contains(' ', StringComparison.Ordinal))
        {
            if (TimeSpan.TryParse(text[^5..], CultureInfo.InvariantCulture, out var parsedOffset))
            {
                offset = text[^6] == '-' ? -parsedOffset : parsedOffset;
                text = text[..^6];
            }
        }

        var formats = dayFirst
            ? ExactFormats.Select(static f => f.Replace("M/d/", "d/M/", StringComparison.Ordinal)).ToArray()
            : ExactFormats;

        if (DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed)
            || DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out parsed))
        {
            parsed -= offset;
            var micro = ((long)PyDate.OrdinalOf(parsed.Year, parsed.Month, parsed.Day) * PyDelta.MicrosecondsPerDay)
                + (parsed.TimeOfDay.Ticks / 10);
            return new PdTimestamp(micro);
        }

        return null;
    }

    /// <summary>Formats a timedelta as pandas shows one: <c>1 days 02:00:00</c>.</summary>
    public static string DeltaText(PyDelta delta, bool daysOnly)
    {
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"{delta.Days} days");

        if (daysOnly)
        {
            return builder.ToString();
        }

        var seconds = delta.Seconds;
        builder.Append(CultureInfo.InvariantCulture, $" {seconds / 3600:D2}:{seconds / 60 % 60:D2}:{seconds % 60:D2}");

        if (delta.Microseconds != 0)
        {
            builder.Append(CultureInfo.InvariantCulture, $".{delta.Microseconds:D6}");
        }

        return builder.ToString();
    }
}
