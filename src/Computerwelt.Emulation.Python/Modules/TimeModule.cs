using System.Globalization;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules;

/// <summary>The <c>time</c> module.</summary>
/// <remarks>
/// <para>
/// Every reading comes from the run's <see cref="TimeProvider"/>, so a host that fixes the
/// clock fixes this too, and nothing here asks the operating system directly.
/// </para>
/// <para>
/// <c>sleep</c> really waits, as the shell's <c>sleep</c> does — a program polling something
/// outside the sandbox through a host function needs the time to pass. It is bounded three
/// ways: a sleep longer than <see cref="ExecutionLimits.MaxSleep"/> raises instead of waiting,
/// a host that runs Python inside a wider budget shortens it to what is left of that budget
/// (<see cref="VirtualMachine.SleepBudget"/>), and cancelling the run
/// (<see cref="VirtualMachine.Cancellation"/>) ends it at once.
/// </para>
/// <para>
/// There is no local time zone in the sandbox: local time is UTC, <c>timezone</c> is 0 and
/// <c>tzname</c> is <c>('UTC', 'UTC')</c>.
/// </para>
/// </remarks>
public static class TimeModule
{
    /// <summary>Builds the module.</summary>
    public static PyModuleObject Create(VirtualMachine machine, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(machine);

        var module = new PyModuleObject("time");
        var start = clock.GetTimestamp();

        DateTimeOffset Now() => clock.GetUtcNow();
        double Seconds() => (Now() - DateTimeOffset.UnixEpoch).Ticks / (double)TimeSpan.TicksPerSecond;
        long Nanoseconds() => (Now() - DateTimeOffset.UnixEpoch).Ticks * 100;
        double Elapsed() => clock.GetElapsedTime(start).TotalSeconds;

        var structTime = new PyNamedTupleType(
            "time.struct_time",
            ["tm_year", "tm_mon", "tm_mday", "tm_hour", "tm_min", "tm_sec", "tm_wday", "tm_yday", "tm_isdst"],
            structSeq: true);

        PyObject StructTime(DateTime moment) => new PyNamedTuple(structTime,
        [
            PyInt.From(moment.Year), PyInt.From(moment.Month), PyInt.From(moment.Day),
            PyInt.From(moment.Hour), PyInt.From(moment.Minute), PyInt.From(moment.Second),
            PyInt.From(((int)moment.DayOfWeek + 6) % 7), PyInt.From(moment.DayOfYear), PyInt.From(0),
        ]);

        DateTime Moment(PyObject[] arguments, int index)
        {
            if (arguments.Length <= index || arguments[index] is PyNone)
            {
                return Now().UtcDateTime;
            }

            switch (arguments[index])
            {
                case PyInt or PyFloat:
                {
                    var seconds = arguments[index] is PyFloat f ? f.Value : (double)((PyInt)arguments[index]).Value;

                    if (!double.IsFinite(seconds) || Math.Abs(seconds) > 253402300799)
                    {
                        throw new PyRaise(PyErrors.ValueError("year out of range"));
                    }

                    return DateTime.UnixEpoch.AddTicks((long)Math.Floor(seconds * TimeSpan.TicksPerSecond));
                }

                case PyTuple { Items.Count: >= 6 } parts:
                    return FromParts(parts);

                default:
                    throw new PyRaise(PyErrors.TypeError(
                        "Tuple or struct_time argument required"));
            }
        }

        module.Add("time", arguments =>
        {
            Arity.Exact("time", arguments, 0);
            return new PyFloat(Seconds());
        });

        module.Add("time_ns", arguments =>
        {
            Arity.Exact("time_ns", arguments, 0);
            return PyInt.From(Nanoseconds());
        });

        foreach (var name in new[] { "monotonic", "perf_counter", "process_time", "thread_time" })
        {
            module.Add(name, arguments =>
            {
                Arity.Exact(name, arguments, 0);
                return new PyFloat(Elapsed());
            });

            module.Add(name + "_ns", arguments =>
            {
                Arity.Exact(name + "_ns", arguments, 0);
                return PyInt.From((long)(Elapsed() * 1_000_000_000));
            });
        }

        module.Add("sleep", arguments =>
        {
            Arity.Exact("sleep", arguments, 1);

            var seconds = arguments[0] switch
            {
                PyFloat f => f.Value,
                PyInt i => (double)i.Value,
                var other => throw new PyRaise(PyErrors.TypeError(
                    $"'{other.TypeName}' object cannot be interpreted as an integer or float")),
            };

            if (double.IsNaN(seconds))
            {
                throw new PyRaise(PyErrors.ValueError("Invalid value NaN (not a number)"));
            }

            if (seconds < 0)
            {
                throw new PyRaise(PyErrors.ValueError("sleep length must be non-negative"));
            }

            var maximum = machine.Limits.MaxSleep;

            if (seconds > maximum.TotalSeconds)
            {
                throw new PyRaise(PyErrors.ValueError(
                    $"sleep length {seconds.ToString("0.###", CultureInfo.InvariantCulture)} seconds exceeds the maximum of "
                    + $"{maximum.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} seconds this sandbox allows"));
            }

            var duration = TimeSpan.FromSeconds(seconds);

            // Shortened to what is left of a host's wider budget, as the shell's sleep is to its
            // timeout: the program still sees time pass, and the run around it still ends.
            if (machine.SleepBudget?.Invoke() is { } remaining && duration > remaining)
            {
                duration = remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }

            if (duration > TimeSpan.Zero)
            {
                // The machine runs synchronously, so the wait blocks its thread. A cancelled run
                // ends the wait with OperationCanceledException, which no `except` can catch -
                // the same way an interrupt ends the machine between instructions.
                Task.Delay(duration, machine.Cancellation).GetAwaiter().GetResult();
            }

            return PyNone.Instance;
        });

        module.Add("gmtime", arguments =>
        {
            Arity.Between("gmtime", arguments, 0, 1);
            return StructTime(Moment(arguments, 0));
        });

        module.Add("localtime", arguments =>
        {
            Arity.Between("localtime", arguments, 0, 1);
            return StructTime(Moment(arguments, 0));
        });

        module.Add("mktime", arguments =>
        {
            Arity.Exact("mktime", arguments, 1);
            return new PyFloat((Moment(arguments, 0) - DateTime.UnixEpoch).TotalSeconds);
        });

        module.Add("strftime", arguments =>
        {
            Arity.Between("strftime", arguments, 1, 2);

            var format = arguments[0] is PyStr text
                ? text.Value
                : throw new PyRaise(PyErrors.TypeError($"strftime() argument 1 must be str, not {arguments[0].TypeName}"));

            return new PyStr(Format(format, Moment(arguments, 1)));
        });

        module.Add("asctime", arguments =>
        {
            Arity.Between("asctime", arguments, 0, 1);
            return new PyStr(Format("%a %b %e %H:%M:%S %Y", Moment(arguments, 0)));
        });

        module.Add("ctime", arguments =>
        {
            Arity.Between("ctime", arguments, 0, 1);
            return new PyStr(Format("%a %b %e %H:%M:%S %Y", Moment(arguments, 0)));
        });

        module.Add("get_clock_info", arguments =>
        {
            Arity.Exact("get_clock_info", arguments, 1);
            return PyNone.Instance;
        });

        module.Add("struct_time", new PyType(
            "time.struct_time",
            static value => value is PyNamedTuple { Type.Name: "time.struct_time" },
            (arguments, _) =>
            {
                Arity.Exact("struct_time", arguments, 1);
                var items = arguments[0].Iterate()?.ToList() ?? throw new PyRaise(PyErrors.TypeError(
                    "constructor requires a sequence"));
                return new PyNamedTuple(structTime, items.Count == 9 ? items : [.. items, .. Enumerable.Repeat<PyObject>(PyInt.From(0), 9 - items.Count)]);
            }));

        module.Add("timezone", PyInt.From(0));
        module.Add("altzone", PyInt.From(0));
        module.Add("daylight", PyInt.From(0));
        module.Add("tzname", new PyTuple([new PyStr("UTC"), new PyStr("UTC")]));

        return module;
    }

    private static DateTime FromParts(PyTuple parts)
    {
        int Part(int index) => parts.Items[index] is PyInt value
            ? value.ToIndex()
            : throw new PyRaise(PyErrors.TypeError("an integer is required"));

        try
        {
            return new DateTime(Part(0), 1, 1, 0, 0, 0, DateTimeKind.Utc)
                .AddMonths(Part(1) - 1).AddDays(Part(2) - 1)
                .AddHours(Part(3)).AddMinutes(Part(4)).AddSeconds(Part(5));
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new PyRaise(PyErrors.ValueError("year out of range"));
        }
    }

    /// <summary>Expands a <c>strftime</c> format, in the "C" locale and UTC.</summary>
    private static string Format(string format, DateTime moment)
    {
        var fields = new DatetimeModule.Fields(
            moment.Year, moment.Month, moment.Day, moment.Hour, moment.Minute, moment.Second, 0);

        return DatetimeModule.Expand(fields, Spelled(format, moment));
    }

    /// <summary>
    /// Rewrites the directives the datetime formatter does not know in terms of those it does.
    /// </summary>
    private static string Spelled(string format, DateTime moment)
    {
        var expanded = new System.Text.StringBuilder();

        for (var i = 0; i < format.Length; i++)
        {
            if (format[i] != '%' || i + 1 >= format.Length)
            {
                expanded.Append(format[i]);
                continue;
            }

            var directive = format[++i];

            expanded.Append(directive switch
            {
                'c' => Spelled("%a %b %e %H:%M:%S %Y", moment),
                'x' => "%m/%d/%y",
                'X' => "%H:%M:%S",
                'D' => "%m/%d/%y",
                'F' => "%Y-%m-%d",
                'T' => "%H:%M:%S",
                'R' => "%H:%M",
                'r' => "%I:%M:%S %p",
                'e' => moment.Day.ToString(CultureInfo.InvariantCulture).PadLeft(2),
                'Z' => "UTC",
                'z' => "+0000",
                'u' => (((int)moment.DayOfWeek + 6) % 7 + 1).ToString(CultureInfo.InvariantCulture),
                'w' => ((int)moment.DayOfWeek).ToString(CultureInfo.InvariantCulture),
                'n' => "\n",
                't' => "\t",
                'h' => "%b",
                '%' => "%%",
                _ => "%" + directive,
            });
        }

        return expanded.ToString();
    }
}
