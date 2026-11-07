using System.Globalization;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules.Pandas;

/// <summary>
/// <c>.str</c>: vectorized string methods. A missing value stays missing; a non-string value
/// becomes NaN, as pandas' object-dtype string methods treat it. Patterns go through the
/// sandbox's own <c>re</c> module, so a regex means here what it means in <c>re</c>.
/// </summary>
internal sealed class PdStringAccessor : PyObject
{
    private readonly PdSeries _series;
    private readonly bool _asIndex;

    public PdStringAccessor(PdSeries series, bool asIndex)
    {
        _series = series;
        _asIndex = asIndex;
    }

    public override string TypeName => "StringMethods";

    public override string Repr() => "<pandas.core.strings.accessor.StringMethods object>";

    /// <summary><c>s.str[i]</c> and <c>s.str[a:b]</c>: index or slice every string.</summary>
    public override PyObject GetItem(PyObject index) => PdRuntime.Current!.Run(() => index switch
    {
        PySlice slice => Each(s => new PyStr(s).GetItem(slice)),
        _ => Each(s =>
        {
            var at = Pd.ToInt(index, "index");
            at = at < 0 ? at + s.Length : at;
            return at >= 0 && at < s.Length ? new PyStr(s.Substring(at, 1)) : Pd.NaN;
        }),
    });

    private PyObject Result(PdColumn column) => _asIndex ? PdIndex.Of(column.Values, _series.Name) : _series.With(column);

    /// <summary>Applies a function to every string; missing and non-string values give NaN.</summary>
    private PyObject Each(Func<string, PyObject> function, PyObject? missing = null)
    {
        var raw = new PyObject[_series.Count];

        for (var i = 0; i < raw.Length; i++)
        {
            raw[i] = _series.Data.Values[i] is PyStr text ? function(text.Value) : missing ?? Pd.NaN;
        }

        return Result(PdColumn.Infer(raw));
    }

    private static PyObject Re(string name) =>
        PdRuntime.Current!.Machine.Modules["re"].GetAttribute(name)!;

    private static PyObject Compile(string pattern, bool caseSensitive, int flags = 0)
    {
        var compile = Re("compile");
        return PdRuntime.Current!.Call(compile, new PyStr(pattern), PyInt.From(flags | (caseSensitive ? 0 : 2)));
    }

    private static PyObject Call(PyObject target, string method, params PyObject[] arguments) =>
        PdRuntime.Current!.Call(Attributes.Get(PdRuntime.Current.Machine, target, method), arguments);

    public override PyObject? GetAttribute(string name) => Pd.Guard(() => AttributeCore(name));

    private PyObject? AttributeCore(string name)
    {
        if (PdRuntime.Current is not { } runtime)
        {
            return null;
        }

        PyBuiltinFunction M(string[] parameters, Func<PdArgs, PyObject> body) => runtime.Method(name, parameters, body);

        return name switch
        {
            "lower" => M([], _ => Each(static s => new PyStr(s.ToLowerInvariant()))),
            "upper" => M([], _ => Each(static s => new PyStr(s.ToUpperInvariant()))),
            "title" => M([], _ => Each(s => Call(new PyStr(s), "title"))),
            "capitalize" => M([], _ => Each(s => Call(new PyStr(s), "capitalize"))),
            "swapcase" => M([], _ => Each(s => Call(new PyStr(s), "swapcase"))),
            "casefold" => M([], _ => Each(s => Call(new PyStr(s), "casefold"))),
            "strip" or "lstrip" or "rstrip" => M(["to_strip"], a => Each(s => a.Given("to_strip") is { } chars ? Call(new PyStr(s), name, chars) : Call(new PyStr(s), name))),
            "len" => M([], _ => Each(static s => PyInt.From(CodePoints(s)))),
            "startswith" => M(["pat", "na"], a => Each(s => Call(new PyStr(s), "startswith", a.Required("pat")), a.Given("na"))),
            "endswith" => M(["pat", "na"], a => Each(s => Call(new PyStr(s), "endswith", a.Required("pat")), a.Given("na"))),
            "contains" => M(["pat", "case", "flags", "na", "regex"], a =>
            {
                var pattern = a.Str("pat") ?? throw Pd.TypeError("contains() missing 1 required positional argument: 'pat'");
                var caseSensitive = a.Bool("case", true);

                if (!a.Bool("regex", true))
                {
                    return Each(s => PyBool.Of(caseSensitive ? s.Contains(pattern, StringComparison.Ordinal) : s.Contains(pattern, StringComparison.OrdinalIgnoreCase)), a.Given("na"));
                }

                var compiled = Compile(pattern, caseSensitive, a.Int("flags", 0));
                return Each(s => PyBool.Of(Call(compiled, "search", new PyStr(s)) is not PyNone), a.Given("na"));
            }),
            "match" or "fullmatch" => M(["pat", "case", "flags", "na"], a =>
            {
                var compiled = Compile(a.Str("pat") ?? string.Empty, a.Bool("case", true), a.Int("flags", 0));
                return Each(s => PyBool.Of(Call(compiled, name, new PyStr(s)) is not PyNone), a.Given("na"));
            }),
            "replace" => M(["pat", "repl", "n", "case", "flags", "regex"], a =>
            {
                var pattern = a.Str("pat") ?? string.Empty;
                var replacement = a.Required("repl");
                var count = a.Int("n", -1);

                if (!a.Bool("regex", false) && replacement is PyStr literal)
                {
                    return Each(s => Call(new PyStr(s), "replace", new PyStr(pattern), literal, PyInt.From(count)));
                }

                var compiled = Compile(pattern, a.Bool("case", true), a.Int("flags", 0));
                return Each(s => Call(compiled, "sub", replacement, new PyStr(s), PyInt.From(Math.Max(0, count))));
            }),
            "split" or "rsplit" => M(["pat", "n", "expand", "regex"], a =>
            {
                var separator = a.Given("pat");
                var count = a.Int("n", -1);
                var split = Each(s => separator is null ? Call(new PyStr(s), name, PyNone.Instance, PyInt.From(count)) : Call(new PyStr(s), name, separator, PyInt.From(count)));

                if (!a.Bool("expand", false) || split is not PdSeries parts)
                {
                    return split is PdSeries s2 ? s2.With(new PdColumn(DType.Object, s2.Data.Values)) : split;
                }

                var rows = parts.Data.Values.Select(v => v is PyList list ? list.Items : (IReadOnlyList<PyObject>)[]).ToList();
                var width = rows.Count == 0 ? 0 : rows.Max(static r => r.Count);
                var columns = Enumerable.Range(0, width)
                    .Select(c => PdColumn.Infer([.. rows.Select(r => c < r.Count ? r[c] : PyNone.Instance)]))
                    .ToList();
                return new PdFrame(PdIndex.Default(width), columns, _series.Index);
            }),
            "join" => M(["sep"], a => Each(s => new PyStr(string.Join(a.Str("sep", string.Empty), s.EnumerateRunes().Select(static r => r.ToString()))))),
            "slice" => M(["start", "stop", "step"], a => Each(s => new PyStr(s).GetItem(new PySlice(a["start"], a["stop"], a["step"])))),
            "get" => M(["i"], a => Each(s =>
            {
                var at = Pd.ToInt(a.Required("i"), "i");
                at = at < 0 ? at + s.Length : at;
                return at >= 0 && at < s.Length ? new PyStr(s.Substring(at, 1)) : Pd.NaN;
            })),
            "zfill" => M(["width"], a => Each(s => Call(new PyStr(s), "zfill", a.Required("width")))),
            "pad" => M(["width", "side", "fillchar"], a =>
            {
                var width = a.Required("width");
                var fill = a["fillchar"] ?? new PyStr(" ");
                var method = a.Str("side", "left") switch { "left" => "rjust", "right" => "ljust", _ => "center" };
                return Each(s => Call(new PyStr(s), method, width, fill));
            }),
            "center" or "ljust" or "rjust" => M(["width", "fillchar"], a => Each(s => Call(new PyStr(s), name, a.Required("width"), a["fillchar"] ?? new PyStr(" ")))),
            "find" or "rfind" or "count" => M(["sub", "start", "end"], a => Each(s => Call(new PyStr(s), name, a.Required("sub")))),
            "repeat" => M(["repeats"], a => Each(s => Operators.Binary("*", new PyStr(s), a.Required("repeats")))),
            "isdigit" or "isalpha" or "isalnum" or "isspace" or "islower" or "isupper" or "istitle" or "isnumeric" or "isdecimal" =>
                M([], _ => Each(s => Call(new PyStr(s), name))),
            "cat" => M(["others", "sep", "na_rep", "join"], a =>
            {
                var separator = a.Str("sep", string.Empty);

                if (a.Given("others") is { } others)
                {
                    var right = others is PdSeries series ? series.Data.Values : Pd.List(others).ToArray();
                    var raw = _series.Data.Values.Select((v, i) => v is PyStr x && right[i] is PyStr y ? (PyObject)new PyStr(x.Value + separator + y.Value) : Pd.NaN).ToList();
                    return Result(PdColumn.Infer(raw));
                }

                var naRep = a.Str("na_rep");
                return new PyStr(string.Join(separator, _series.Data.Values
                    .Where(v => v is PyStr || naRep is not null)
                    .Select(v => v is PyStr t ? t.Value : naRep!)));
            }),
            "extract" => M(["pat", "flags", "expand"], a =>
            {
                var compiled = Compile(a.Str("pat") ?? string.Empty, true, a.Int("flags", 0));
                var groups = (int)((PyInt)compiled.GetAttribute("groups")!).Value;
                var matches = _series.Data.Values.Select(v => v is PyStr s ? Call(compiled, "search", s) : PyNone.Instance).ToList();
                var columns = Enumerable.Range(1, groups)
                    .Select(g => PdColumn.Infer([.. matches.Select(m => m is PyNone ? Pd.NaN : Call(m, "group", PyInt.From(g)) is var v && v is PyNone ? Pd.NaN : v)]))
                    .ToList();

                if (groups == 1 && !a.Bool("expand", true))
                {
                    return _series.With(columns[0]);
                }

                return new PdFrame(PdIndex.Default(groups), columns, _series.Index);
            }),
            "findall" => M(["pat", "flags"], a =>
            {
                var compiled = Compile(a.Str("pat") ?? string.Empty, true, a.Int("flags", 0));
                var result = Each(s => Call(compiled, "findall", new PyStr(s)));
                return result is PdSeries s2 ? s2.With(new PdColumn(DType.Object, s2.Data.Values)) : result;
            }),
            "normalize" => M(["form"], a => Each(s =>
            {
                var normalize = PdRuntime.Current!.Machine.Modules["unicodedata"].GetAttribute("normalize")!;
                return PdRuntime.Current.Call(normalize, a.Required("form"), new PyStr(s));
            })),
            _ => null,
        };
    }

    private static int CodePoints(string text) => text.EnumerateRunes().Count();
}

/// <summary><c>.dt</c>: the datetime fields of a datetime column, and <c>strftime</c>.</summary>
internal sealed class PdDateAccessor : PyObject
{
    private readonly PdSeries _series;
    private readonly bool _asIndex;

    public PdDateAccessor(PdSeries series, bool asIndex)
    {
        _series = series;
        _asIndex = asIndex;
    }

    public override string TypeName => "DatetimeProperties";

    public override string Repr() => "<pandas.core.indexes.accessors.DatetimeProperties object>";

    private PyObject Each(Func<PdTimestamp, PyObject> function, bool floatWhenMissing = true)
    {
        if (_series.Data.Type is not DType.DateTime and not DType.TimeDelta && !_series.Data.Values.All(static v => v is PdTimestamp or PdNaT or PyNone))
        {
            throw new PyRaise(new PyException(PyExceptionType.AttributeError, "Can only use .dt accessor with datetimelike values"));
        }

        var raw = _series.Data.Values.Select(v => v is PdTimestamp t ? function(t) : floatWhenMissing ? Pd.NaN : PdNaT.Instance).ToList();
        var column = PdColumn.Infer(raw);
        return _asIndex ? PdIndex.Of(column.Values, _series.Name) : _series.With(column);
    }

    private PyObject Deltas(Func<PyDelta, PyObject> function)
    {
        var raw = _series.Data.Values.Select(v => v is PyDelta d ? function(d) : Pd.NaN).ToList();
        return _series.With(PdColumn.Infer(raw));
    }

    public override PyObject? GetAttribute(string name) => Pd.Guard(() => AttributeCore(name));

    private PyObject? AttributeCore(string name)
    {
        if (_series.Data.Type == DType.TimeDelta)
        {
            return name switch
            {
                "days" => Deltas(static d => PyInt.From(d.Days)),
                "seconds" => Deltas(static d => PyInt.From(d.Seconds)),
                "microseconds" => Deltas(static d => PyInt.From(d.Microseconds)),
                "total_seconds" => new PyBuiltinFunction("total_seconds", _ => Deltas(static d => new PyFloat((d.Days * 86400.0) + d.Seconds + (d.Microseconds / 1e6)))),
                _ => null,
            };
        }

        switch (name)
        {
            case "year" or "month" or "day" or "hour" or "minute" or "second" or "microsecond" or "dayofweek" or "day_of_week"
                or "weekday" or "dayofyear" or "day_of_year" or "quarter" or "days_in_month" or "daysinmonth"
                or "is_month_start" or "is_month_end" or "is_year_start" or "is_year_end" or "is_leap_year":
            {
                var field = name == "weekday" ? "dayofweek" : name;
                return Each(t => t.GetAttribute(field)!);
            }

            case "date":
                return Each(static t => t.GetAttribute("date") is { } method ? PdRuntime.Current!.Call(method) : PyNone.Instance);
            case "week" or "weekofyear":
                return Each(static t => ((PyTuple)PdRuntime.Current!.Call(t.GetAttribute("isocalendar")!)).Items[1]);
            case "time":
                return Each(static t =>
                {
                    var p = t.Parts;
                    return new PyStr($"{p.Hour:D2}:{p.Minute:D2}:{p.Second:D2}");
                });
        }

        if (PdRuntime.Current is not { } runtime)
        {
            return null;
        }

        PyBuiltinFunction M(string[] parameters, Func<PdArgs, PyObject> body) => runtime.Method(name, parameters, body);

        return name switch
        {
            "strftime" => M(["date_format"], a => Each(t => new PyStr(DatetimeModule.Expand(t.Parts, a.Str("date_format") ?? string.Empty)))),
            "day_name" => M(["locale"], _ => Each(static t => new PyStr(PdTimestamp.DayName(PyDate.WeekdayOf(PyDate.OrdinalOf(t.Parts.Year, t.Parts.Month, t.Parts.Day)))))),
            "month_name" => M(["locale"], _ => Each(static t => new PyStr(CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(t.Parts.Month)))),
            "normalize" => M([], _ => Each(static t => new PdTimestamp(t.Micro - (t.Micro % PyDelta.MicrosecondsPerDay)), floatWhenMissing: false)),
            "floor" or "ceil" or "round" => M(["freq"], a => Each(t => RoundTo(t, a.Str("freq", "D"), name), floatWhenMissing: false)),
            "to_period" => M(["freq"], a => Each(t => new PyStr(Period(t, a.Str("freq", "M"))))),
            "isocalendar" => M([], _ =>
            {
                var tuples = _series.Data.Values.Select(v => v is PdTimestamp t ? (PyTuple)runtime.Call(t.GetAttribute("isocalendar")!) : null).ToList();
                var columns = Enumerable.Range(0, 3).Select(k => PdColumn.Infer([.. tuples.Select(t => t is null ? Pd.NaN : t.Items[k])])).ToList();
                return new PdFrame(PdIndex.Of([new PyStr("year"), new PyStr("week"), new PyStr("day")]), columns, _series.Index);
            }),
            _ => null,
        };
    }

    private static PdTimestamp RoundTo(PdTimestamp moment, string freq, string how)
    {
        var unit = freq switch
        {
            "D" => PyDelta.MicrosecondsPerDay,
            "h" or "H" => 3_600_000_000L,
            "min" or "T" => 60_000_000L,
            "s" or "S" => 1_000_000L,
            "ms" or "L" => 1000L,
            _ => throw Pd.ValueError($"Invalid frequency: {freq}"),
        };

        var floor = moment.Micro - (((moment.Micro % unit) + unit) % unit);
        return how switch
        {
            "floor" => new PdTimestamp(floor),
            "ceil" => new PdTimestamp(floor == moment.Micro ? floor : floor + unit),
            _ => new PdTimestamp(moment.Micro - floor >= unit / 2 ? floor + unit : floor),
        };
    }

    private static string Period(PdTimestamp moment, string freq)
    {
        var p = moment.Parts;
        return freq switch
        {
            "Y" or "A" => $"{p.Year:D4}",
            "Q" => $"{p.Year:D4}Q{((p.Month - 1) / 3) + 1}",
            "D" => $"{p.Year:D4}-{p.Month:D2}-{p.Day:D2}",
            _ => $"{p.Year:D4}-{p.Month:D2}",
        };
    }
}
