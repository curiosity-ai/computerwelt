using System.Numerics;
using System.Text.RegularExpressions;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules;

/// <summary>
/// The <c>zoneinfo</c> module: IANA time zones as <c>datetime</c> tzinfo objects.
/// </summary>
/// <remarks>
/// <para>
/// The rules come from the host's time-zone database through <see cref="TimeZoneInfo"/>,
/// which is read-only reference data rather than anything a program could use to reach
/// the host — but it is the host's copy, so a rule change in a newer tzdata can change an
/// answer. A key is checked before it is looked up, as CPython checks it, so a name such as
/// <c>../../etc/passwd</c> never reaches the database reader.
/// </para>
/// <para>
/// Offsets follow CPython's <c>fold=0</c> reading of an ambiguous or missing local time
/// (the earlier offset). Abbreviations (<c>CET</c>, <c>EDT</c>) are what the runtime reports
/// under invariant globalization, which this repository builds with; a host running with
/// ICU names would see long names instead.
/// </para>
/// </remarks>
public static partial class ZoneinfoModule
{
    /// <summary><c>zoneinfo.ZoneInfoNotFoundError</c>, a <c>KeyError</c>.</summary>
    public static PyExceptionType ZoneInfoNotFoundError { get; } =
        PyExceptionType.DefineHostException("ZoneInfoNotFoundError", PyExceptionType.KeyError);

    [GeneratedRegex(@"\A[A-Za-z0-9_+\-]+(?:/[A-Za-z0-9_+\-.]+)*\z")]
    private static partial Regex KeyPattern();

    /// <summary>Builds the module, with a cache of its own for this run.</summary>
    public static PyModuleObject Create()
    {
        var module = new PyModuleObject("zoneinfo");
        var cache = new Dictionary<string, PyZoneInfo>(StringComparer.Ordinal);
        PyType? type = null;

        PyZoneInfo Load(string key)
        {
            if (key.Length == 0 || key.StartsWith('/') || key.Split('/').Any(static part => part is "" or "." or "..")
                || !KeyPattern().IsMatch(key))
            {
                throw new PyRaise(PyErrors.ValueError($"ZoneInfo keys must be normalized relative paths, got: {key}"));
            }

            TimeZoneInfo zone;

            try
            {
                zone = TimeZoneInfo.FindSystemTimeZoneById(key);
            }
            catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException or System.Security.SecurityException or IOException)
            {
                throw new PyRaise(new PyException(ZoneInfoNotFoundError, PyStr.Quote($"No time zone found with key {key}")));
            }

            return new PyZoneInfo(key, zone, type!);
        }

        type = new PyType("ZoneInfo", static value => value is PyZoneInfo, (arguments, keywords) =>
        {
            var key = Compression.Argument(arguments, keywords, 0, "key")
                ?? throw new PyRaise(PyErrors.TypeError("ZoneInfo() missing required argument 'key' (pos 1)"));

            if (key is not PyStr text)
            {
                throw new PyRaise(PyErrors.TypeError($"ZoneInfo() argument 'key' must be str, not {key.TypeName}"));
            }

            if (!cache.TryGetValue(text.Value, out var zone))
            {
                zone = Load(text.Value);
                cache[text.Value] = zone;
            }

            return zone;
        });

        type.Members["no_cache"] = new PyBuiltinFunction("no_cache", arguments =>
        {
            Arity.ExactCount("no_cache", arguments, 1);
            return Load(arguments[0].Display());
        });

        type.Members["clear_cache"] = new PyBuiltinFunction("clear_cache", (_, keywords) =>
        {
            if (keywords?.Entries.FirstOrDefault(static e => e.Key.Display() == "only_keys").Value is { } only and not PyNone)
            {
                foreach (var key in VirtualMachine.RequireIterable(only))
                {
                    cache.Remove(key.Display());
                }
            }
            else
            {
                cache.Clear();
            }

            return PyNone.Instance;
        });

        module.Add("ZoneInfo", type);
        module.Add("ZoneInfoNotFoundError", ZoneInfoNotFoundError);
        module.Add("TZPATH", new PyTuple([]));
        module.Add("available_timezones", new PyBuiltinFunction("available_timezones", _ =>
            new PySet(TimeZoneInfo.GetSystemTimeZones()
                .Where(static zone => zone.HasIanaId && KeyPattern().IsMatch(zone.Id))
                .Select(static zone => (PyObject)new PyStr(zone.Id)))));

        return module;
    }
}

/// <summary>A <c>zoneinfo.ZoneInfo</c>: the rules of one IANA time zone.</summary>
public sealed class PyZoneInfo : PyTzInfo, IPyHostTyped
{
    private readonly TimeZoneInfo _zone;
    private readonly PyType _type;

    internal PyZoneInfo(string key, TimeZoneInfo zone, PyType type)
    {
        Key = key;
        _zone = zone;
        _type = type;
    }

    /// <summary>The key it was built from.</summary>
    public string Key { get; }

    /// <inheritdoc />
    PyObject IPyHostTyped.Type => _type;

    /// <inheritdoc />
    public override string TypeName => "zoneinfo.ZoneInfo";

    /// <inheritdoc />
    public override string Repr() => $"zoneinfo.ZoneInfo(key={PyStr.Quote(Key)})";

    /// <inheritdoc />
    public override string Display() => Key;

    /// <inheritdoc />
    public override BigInteger PyHash() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);

    private static DateTime? Local(DatetimeModule.Fields local)
    {
        try
        {
            return new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, local.Second, DateTimeKind.Unspecified);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private TimeSpan OffsetAtUtc(DateTime utc) => _zone.GetUtcOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));

    /// <summary>
    /// The offset for a wall-clock time, read as CPython reads it with <c>fold=0</c>: in a
    /// repeated hour the earlier offset, in a skipped hour the offset from before the change.
    /// </summary>
    private TimeSpan OffsetForLocal(DateTime local)
    {
        var before = OffsetAtUtc(local.AddDays(-1));
        var after = OffsetAtUtc(local.AddDays(1));

        if (before == after)
        {
            return before;
        }

        var beforeHolds = OffsetAtUtc(local - before) == before;
        var afterHolds = OffsetAtUtc(local - after) == after;

        return beforeHolds || !afterHolds ? before : after;
    }

    /// <inheritdoc />
    internal override PyDelta UtcOffsetAt(DatetimeModule.Fields local) =>
        Local(local) is { } moment
            ? PyDelta.FromMicroseconds(new BigInteger(OffsetForLocal(moment).Ticks / 10))
            : PyDelta.FromMicroseconds(new BigInteger(_zone.BaseUtcOffset.Ticks / 10));

    private bool IsDaylight(DatetimeModule.Fields local) =>
        Local(local) is { } moment && _zone.IsDaylightSavingTime(DateTime.SpecifyKind(moment - OffsetForLocal(moment), DateTimeKind.Utc));

    /// <inheritdoc />
    internal override string TzNameAt(DatetimeModule.Fields local)
    {
        var name = IsDaylight(local) ? _zone.DaylightName : _zone.StandardName;

        // Without an abbreviation the runtime reports a description; CPython would name UTC.
        return name.Contains(' ', StringComparison.Ordinal) && _zone.BaseUtcOffset == TimeSpan.Zero && !_zone.SupportsDaylightSavingTime
            ? "UTC"
            : name;
    }

    /// <inheritdoc />
    internal override PyObject DstAt(DatetimeModule.Fields local)
    {
        if (!IsDaylight(local) || Local(local) is not { } moment)
        {
            return PyDelta.FromMicroseconds(BigInteger.Zero);
        }

        var standard = _zone.GetAdjustmentRules()
            .FirstOrDefault(rule => rule.DateStart <= moment && moment <= rule.DateEnd) is { } current
            ? _zone.BaseUtcOffset + current.BaseUtcOffsetDelta
            : _zone.BaseUtcOffset;
        return PyDelta.FromMicroseconds(new BigInteger((OffsetForLocal(moment) - standard).Ticks / 10));
    }

    /// <inheritdoc />
    internal override BigInteger OffsetAtUtc(BigInteger utcMicroseconds)
    {
        var fields = DatetimeModule.Split(utcMicroseconds);
        var utc = Local(fields) ?? DateTime.UnixEpoch;
        return new BigInteger(OffsetAtUtc(utc).Ticks / 10);
    }

    private static DatetimeModule.Fields? FieldsOf(PyObject value) =>
        value is PyDateTime moment ? moment.Parts : null;

    /// <inheritdoc />
    public override PyObject? GetAttribute(string name) => name switch
    {
        "key" => new PyStr(Key),
        "__class__" => _type,
        "utcoffset" => new PyBuiltinFunction(name, arguments =>
            arguments.Length > 0 && FieldsOf(arguments[0]) is { } local ? UtcOffsetAt(local) : PyNone.Instance),
        "tzname" => new PyBuiltinFunction(name, arguments =>
            arguments.Length > 0 && FieldsOf(arguments[0]) is { } local ? new PyStr(TzNameAt(local)) : PyNone.Instance),
        "dst" => new PyBuiltinFunction(name, arguments =>
            arguments.Length > 0 && FieldsOf(arguments[0]) is { } local ? DstAt(local) : PyNone.Instance),
        "fromutc" => new PyBuiltinFunction(name, arguments =>
        {
            Arity.ExactCount("fromutc", arguments, 1);

            if (arguments[0] is not PyDateTime moment)
            {
                throw new PyRaise(PyErrors.TypeError("fromutc() requires a datetime argument"));
            }

            var utc = new PyDateTime(moment.Parts, null).Instant;
            return new PyDateTime(DatetimeModule.Split(utc + OffsetAtUtc(utc)), this);
        }),
        _ => null,
    };
}
