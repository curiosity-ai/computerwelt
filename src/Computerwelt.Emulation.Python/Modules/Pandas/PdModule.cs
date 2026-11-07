using System.Globalization;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules.Pandas;

/// <summary>
/// The <c>pandas</c> module: a sandboxed subset of pandas implemented in C# over the
/// interpreter's object model. No network, no pickle, no SQL, no Excel or Parquet; files only
/// through the run's virtual filesystem.
/// </summary>
/// <remarks>
/// Built per run, so the display options a program sets (<c>pd.set_option</c>) are its own.
/// Behaviour follows pandas 2.2: strings are <c>object</c> dtype, missing values are NaN,
/// <c>NaT</c> or <c>None</c>. Writes follow copy-on-write — a column taken from a frame and
/// then changed does not change the frame — which is pandas 3's rule rather than 2.2's.
/// </remarks>
public static class PdModule
{
    /// <summary>The pandas version this subset reports and follows.</summary>
    public const string Version = "2.2.3";

    internal static readonly PyExceptionType ParserError = PyExceptionType.DefineHostException("ParserError", PyExceptionType.ValueError);

    internal static readonly PyExceptionType EmptyDataError = PyExceptionType.DefineHostException("EmptyDataError", PyExceptionType.ValueError);

    internal static readonly PyExceptionType UndefinedVariableError = PyExceptionType.DefineHostException("UndefinedVariableError", PyExceptionType.NameError);

    private static readonly string[] SeriesParameters = ["data", "index", "dtype", "name", "copy"];
    private static readonly string[] FrameParameters = ["data", "index", "columns", "dtype", "copy"];

    /// <summary>Builds the module for one run.</summary>
    /// <param name="machine">The machine callbacks run on.</param>
    /// <param name="fileSystem">The virtual filesystem, or null when the run has none.</param>
    /// <returns>The module.</returns>
    public static PyModuleObject Create(VirtualMachine machine, IPyFileSystem? fileSystem)
    {
        var runtime = new PdRuntime(machine, fileSystem);
        runtime.Install();
        var module = new PyModuleObject("pandas");

        PyBuiltinFunction F(string name, string[] parameters, Func<PdArgs, PyObject> body, bool extra = false) =>
            runtime.Method(name, parameters, body, extra);

        PyType Type(string name, Func<PyObject, bool> matches, string[] parameters, Func<PdArgs, PyObject> body) =>
            new(name, matches, (arguments, keywords) => runtime.Run(() => body(new PdArgs(name, arguments, keywords, parameters))));

        module.Add("__version__", new PyStr(Version));
        module.Add("Series", Type("Series", static v => v is PdSeries, SeriesParameters, PdBuild.Series));
        module.Add("DataFrame", Type("DataFrame", static v => v is PdFrame, FrameParameters, PdBuild.Frame));
        module.Add("Index", Type("Index", static v => v is PdIndex, ["data", "dtype", "copy", "name", "tupleize_cols"], a =>
        {
            var index = a.Given("data") is { } data ? PdBuild.IndexFrom(data) : PdIndex.Of([]);
            index = index.WithName(a.Given("name") ?? index.Name);

            if (a.Given("dtype") is { } dtype)
            {
                var (type, spelling) = PdDTypeObject.Parse(dtype);
                index = new PdIndex(index.Labels.Cast(type, spelling), index.Name);
            }

            return index;
        }));
        module.Add("RangeIndex", Type("RangeIndex", static v => v is PdIndex { IsRange: true }, ["start", "stop", "step", "dtype", "copy", "name"], a =>
        {
            var start = a.Given("start") is { } s ? Pd.ToLong(s, "start") : 0;
            var stop = a.Given("stop") is { } e ? Pd.ToLong(e, "stop") : (long?)null;
            return stop is null
                ? PdIndex.Range(0, start, a.Given("step") is { } st ? Pd.ToLong(st, "step") : 1, a.Given("name"))
                : PdIndex.Range(start, stop.Value, a.Given("step") is { } step ? Pd.ToLong(step, "step") : 1, a.Given("name"));
        }));
        module.Add("DatetimeIndex", Type("DatetimeIndex", static v => v is PdIndex { Labels.Type: DType.DateTime }, ["data", "freq", "name", "dtype"], a =>
            new PdIndex(ToDatetimeColumn(PdColumn.Infer(Pd.List(a.Required("data"))), null, false, coerce: false), a.Given("name"))));
        module.Add("MultiIndex", new PyType("MultiIndex", static v => v is PdIndex { IsMulti: true }, static (_, _) =>
            throw Pd.TypeError("MultiIndex(...) cannot be built directly here; use MultiIndex.from_tuples or set_index with a list")));
        ((PyType)module.GetAttribute("MultiIndex")!).Members["from_tuples"] = F("from_tuples", ["tuples", "sortorder", "names"], a =>
        {
            var tuples = Pd.List(a.Required("tuples"));
            var width = tuples.Count > 0 ? Pd.List(tuples[0]).Count : 0;
            var names = a.Given("names") is { } n ? Pd.List(n) : [.. Enumerable.Repeat<PyObject>(PyNone.Instance, width)];
            return new PdIndex(new PdColumn(DType.Object, [.. tuples.Select(t => (PyObject)new PyTuple(Pd.List(t)))]), null, names);
        });
        ((PyType)module.GetAttribute("MultiIndex")!).Members["from_product"] = F("from_product", ["iterables", "sortorder", "names"], a =>
        {
            var levels = Pd.List(a.Required("iterables")).Select(Pd.List).ToList();
            IEnumerable<List<PyObject>> product = [[]];
            foreach (var level in levels)
            {
                product = product.SelectMany(prefix => level.Select(item => (List<PyObject>)[.. prefix, item])).ToList();
            }

            var names = a.Given("names") is { } n ? Pd.List(n) : [.. Enumerable.Repeat<PyObject>(PyNone.Instance, levels.Count)];
            return new PdIndex(new PdColumn(DType.Object, [.. product.Select(static p => (PyObject)new PyTuple(p))]), null, names);
        });

        module.Add("Timestamp", new PdTimestampType(runtime));
        module.Add("Timedelta", F("Timedelta", ["value", "unit", "days", "hours", "minutes", "seconds", "milliseconds", "microseconds", "weeks"], Timedelta));
        module.Add("NaT", PdNaT.Instance);
        module.Add("NA", PdNA.Instance);
        module.Add("NamedAgg", F("NamedAgg", ["column", "aggfunc"], static a => new PyTuple([a.Required("column"), a.Required("aggfunc")])));

        module.Add("isna", F("isna", ["obj"], static a => Missing(a.Required("obj"), true)));
        module.Add("isnull", F("isnull", ["obj"], static a => Missing(a.Required("obj"), true)));
        module.Add("notna", F("notna", ["obj"], static a => Missing(a.Required("obj"), false)));
        module.Add("notnull", F("notnull", ["obj"], static a => Missing(a.Required("obj"), false)));

        module.Add("concat", F("concat", ["objs", "axis", "join", "ignore_index", "keys", "levels", "names", "verify_integrity", "sort", "copy"], PdReshape.Concat));
        module.Add("merge", F("merge", PdReshape.MergeParameters, a =>
        {
            var left = a.Required("left") switch
            {
                PdFrame frame => frame,
                PdSeries series => series.ToFrame(null),
                var other => throw Pd.TypeError($"Can only merge Series or DataFrame objects, a {other.TypeName} was passed"),
            };
            return PdReshape.Merge(left, a);
        }));
        module.Add("pivot_table", F("pivot_table", PdReshape.PivotTableParameters, a => PdReshape.PivotTable((PdFrame)a.Required("data"), a)));
        module.Add("pivot", F("pivot", ["data", "columns", "index", "values"], a => PdReshape.Pivot((PdFrame)a.Required("data"), a)));
        module.Add("melt", F("melt", PdReshape.MeltParameters, a => PdReshape.Melt((PdFrame)a.Required("frame"), a)));
        module.Add("get_dummies", F("get_dummies", ["data", "prefix", "prefix_sep", "dummy_na", "columns", "sparse", "drop_first", "dtype"], PdReshape.GetDummies));
        module.Add("cut", F("cut", ["x", "bins", "right", "labels", "retbins", "precision", "include_lowest", "duplicates", "ordered"], PdReshape.Cut));
        module.Add("crosstab", F("crosstab", ["index", "columns", "values", "rownames", "colnames", "aggfunc", "margins", "margins_name", "dropna", "normalize"], PdReshape.Crosstab));
        module.Add("unique", F("unique", ["values"], a => runtime.Array(new PyList(new PdSeries(PdColumn.Infer(Pd.List(a.Required("values"))), PdIndex.Default(Pd.List(a.Required("values")).Count), null).UniqueValues()))));

        module.Add("read_csv", F("read_csv", PdIO.ReadCsvParameters, PdIO.ReadCsv));
        module.Add("read_table", F("read_table", PdIO.ReadCsvParameters, a =>
        {
            if (a["sep"] is null && a["delimiter"] is null)
            {
                a.Set("sep", new PyStr("\t"));
            }

            return PdIO.ReadCsv(a);
        }));
        module.Add("read_json", F("read_json", PdIO.ReadJsonParameters, PdIO.ReadJson));

        module.Add("to_datetime", F("to_datetime", ["arg", "errors", "dayfirst", "yearfirst", "utc", "format", "exact", "unit", "infer_datetime_format", "origin", "cache"], ToDatetime));
        module.Add("to_numeric", F("to_numeric", ["arg", "errors", "downcast", "dtype_backend"], ToNumeric));
        module.Add("to_timedelta", F("to_timedelta", ["arg", "unit", "errors"], a => Timedelta(a)));
        module.Add("date_range", F("date_range", ["start", "end", "periods", "freq", "tz", "normalize", "name", "inclusive", "unit"], DateRange));

        var options = new PdOptions(runtime);
        module.Add("options", options);
        module.Add("set_option", new PyBuiltinFunction("set_option", (arguments, _) => runtime.Run(() =>
        {
            for (var i = 0; i + 1 < arguments.Length; i += 2)
            {
                runtime.Options[options.Resolve(Pd.Text(arguments[i]))] = arguments[i + 1];
            }

            return PyNone.Instance;
        })));
        module.Add("get_option", F("get_option", ["pat"], a => runtime.Options.TryGetValue(options.Resolve(Pd.Text(a.Required("pat"))), out var value) ? value : PdOptions.Default(options.Resolve(Pd.Text(a.Required("pat"))))));
        module.Add("reset_option", F("reset_option", ["pat"], a =>
        {
            var pattern = Pd.Text(a.Required("pat"));
            if (pattern == "all")
            {
                runtime.Options.Clear();
            }
            else
            {
                runtime.Options.Remove(options.Resolve(pattern));
            }

            return PyNone.Instance;
        }));

        module.Add("errors", new PyModuleObject("pandas.errors")
            .Add("ParserError", ParserError)
            .Add("EmptyDataError", EmptyDataError)
            .Add("UndefinedVariableError", UndefinedVariableError));

        module.Add("testing", new PyModuleObject("pandas.testing")
            .Add("assert_frame_equal", F("assert_frame_equal", ["left", "right", "check_dtype", "check_exact", "check_like", "check_names", "rtol", "atol", "check_index_type"], a => AssertEqual(a, frames: true)))
            .Add("assert_series_equal", F("assert_series_equal", ["left", "right", "check_dtype", "check_exact", "check_names", "rtol", "atol", "check_index_type"], a => AssertEqual(a, frames: false))));

        module.Add("api", new PyModuleObject("pandas.api").Add("types", new PyModuleObject("pandas.api.types")
            .Add("is_numeric_dtype", F("is_numeric_dtype", ["arr_or_dtype"], static a => PyBool.Of(DTypeOf(a.Required("arr_or_dtype")) is DType.Int64 or DType.Float64 or DType.Bool)))
            .Add("is_string_dtype", F("is_string_dtype", ["arr_or_dtype"], static a => PyBool.Of(DTypeOf(a.Required("arr_or_dtype")) == DType.Object)))
            .Add("is_datetime64_any_dtype", F("is_datetime64_any_dtype", ["arr_or_dtype"], static a => PyBool.Of(DTypeOf(a.Required("arr_or_dtype")) == DType.DateTime)))
            .Add("is_bool_dtype", F("is_bool_dtype", ["arr_or_dtype"], static a => PyBool.Of(DTypeOf(a.Required("arr_or_dtype")) == DType.Bool)))
            .Add("is_integer_dtype", F("is_integer_dtype", ["arr_or_dtype"], static a => PyBool.Of(DTypeOf(a.Required("arr_or_dtype")) == DType.Int64)))
            .Add("is_float_dtype", F("is_float_dtype", ["arr_or_dtype"], static a => PyBool.Of(DTypeOf(a.Required("arr_or_dtype")) == DType.Float64)))));

        return module;
    }

    private static DType DTypeOf(PyObject value) => value switch
    {
        PdSeries series => series.Data.Type,
        PdIndex index => index.Labels.Type,
        PdDTypeObject dtype => dtype.Type,
        _ => PdDTypeObject.Parse(value).Type,
    };

    private static PyObject Missing(PyObject value, bool missing) => value switch
    {
        PdSeries series => series.With(PdSeries.MissingMask(series.Data, missing)),
        PdFrame frame => frame.MapColumns(c => PdSeries.MissingMask(c, missing)),
        PdIndex index => PdRuntime.Current!.Array(new PyList([.. PdSeries.MissingMask(index.Labels, missing).Values])),
        PyList or PyTuple => PdRuntime.Current!.Array(new PyList([.. Pd.List(value).Select(v => (PyObject)PyBool.Of(Pd.IsMissing(v) == missing))])),
        _ => PyBool.Of(Pd.IsMissing(value) == missing),
    };

    /// <summary>Parses every value of a column to a timestamp.</summary>
    internal static PdColumn ToDatetimeColumn(PdColumn column, string? format, bool dayFirst, bool coerce, string unit = "ns")
    {
        var values = new PyObject[column.Length];

        for (var i = 0; i < values.Length; i++)
        {
            var parsed = PdTimestamp.Parse(column.Values[i], format, unit, dayFirst);

            values[i] = parsed ?? (coerce
                ? PdNaT.Instance
                : throw Pd.ValueError(format is null
                    ? $"Unknown datetime string format, unable to parse: {column.Values[i].Display()}, at position {i}"
                    : $"time data \"{column.Values[i].Display()}\" doesn't match format \"{format}\", at position {i}."));
        }

        return new PdColumn(DType.DateTime, values);
    }

    private static PyObject ToDatetime(PdArgs arguments)
    {
        var value = arguments.Required("arg");
        var errors = arguments.Str("errors", "raise");
        var format = arguments.Str("format");
        var dayFirst = arguments.Bool("dayfirst", false);
        var unit = arguments.Str("unit", "ns");
        var coerce = errors == "coerce";

        PdColumn Convert(PdColumn column)
        {
            try
            {
                return ToDatetimeColumn(column, format, dayFirst, coerce, unit);
            }
            catch (PyRaise) when (errors == "ignore")
            {
                return column;
            }
        }

        switch (value)
        {
            case PdSeries series:
                return series.With(Convert(series.Data));
            case PdIndex index:
                return new PdIndex(Convert(index.Labels), index.Name);
            case PdFrame frame:
            {
                // Assembling from year/month/day columns.
                int Field(string name, int fallback) => frame.Columns.Find(new PyStr(name)) is var at && at >= 0 ? at : fallback;
                var (y, m, d) = (Field("year", -1), Field("month", -1), Field("day", -1));

                if (y < 0 || m < 0 || d < 0)
                {
                    throw Pd.ValueError("to assemble mappings requires at least that [year, month, day] be specified");
                }

                var values = Enumerable.Range(0, frame.RowCount).Select(r => (PyObject)PdTimestamp.FromFields(
                    Pd.ToInt(frame.Data[y].Values[r], "year"), Pd.ToInt(frame.Data[m].Values[r], "month"), Pd.ToInt(frame.Data[d].Values[r], "day"))).ToArray();
                return new PdSeries(new PdColumn(DType.DateTime, values), frame.Index, null);
            }
        }

        if (Pd.IsListLike(value))
        {
            return new PdIndex(Convert(PdColumn.Infer(Pd.List(value))), null);
        }

        var single = Convert(PdColumn.Infer([value]));
        return single.Values[0];
    }

    private static PyObject ToNumeric(PdArgs arguments)
    {
        var value = arguments.Required("arg");
        var errors = arguments.Str("errors", "raise");

        PdColumn Convert(PdColumn column)
        {
            if (column.IsNumeric)
            {
                return column.Clone();
            }

            var raw = new PyObject[column.Length];

            for (var i = 0; i < raw.Length; i++)
            {
                var item = column.Values[i];

                if (Pd.IsMissing(item) || Pd.IsNumber(item))
                {
                    raw[i] = item is PyNone ? Pd.NaN : item;
                    continue;
                }

                var text = item.Display().Trim();

                if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer))
                {
                    raw[i] = PyInt.From(integer);
                }
                else if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                {
                    raw[i] = new PyFloat(number);
                }
                else if (errors == "coerce")
                {
                    raw[i] = Pd.NaN;
                }
                else if (errors == "ignore")
                {
                    return column;
                }
                else
                {
                    throw Pd.ValueError($"Unable to parse string \"{text}\" at position {i}");
                }
            }

            return PdColumn.Infer(raw);
        }

        return value switch
        {
            PdSeries series => series.With(Convert(series.Data)),
            _ when Pd.IsListLike(value) => PdRuntime.Current!.Array(new PyList([.. Convert(PdColumn.Infer(Pd.List(value))).Values])),
            _ => Convert(PdColumn.Infer([value])).Values[0],
        };
    }

    private static PyObject Timedelta(PdArgs arguments)
    {
        var runtime = PdRuntime.Current!;
        var type = runtime.Machine.Modules["datetime"].GetAttribute("timedelta")!;
        var keywords = new PyDict();

        foreach (var name in new[] { "days", "hours", "minutes", "seconds", "milliseconds", "microseconds", "weeks" })
        {
            if (arguments.Given(name) is { } part)
            {
                keywords.Set(new PyStr(name), part);
            }
        }

        if (arguments.Given("value") is { } value)
        {
            if (value is PyDelta)
            {
                return value;
            }

            if (Pd.IsListLike(value) || value is PdSeries)
            {
                var items = value is PdSeries series ? series.Data.Values : [.. Pd.List(value)];
                var deltas = items.Select(i =>
                {
                    var single = new PdArgs("to_timedelta", [i], arguments["unit"] is { } u ? Single("unit", u) : null, ["value", "unit", "days", "hours", "minutes", "seconds", "milliseconds", "microseconds", "weeks"]);
                    return Pd.IsMissing(i) ? PdNaT.Instance : Timedelta(single);
                }).ToArray();
                var column = new PdColumn(DType.TimeDelta, deltas);
                return value is PdSeries s ? s.With(column) : new PdIndex(column, null);
            }

            var (amount, unit) = value switch
            {
                PyStr text => ParseDelta(text.Value),
                _ => (Pd.ToDouble(value), arguments.Str("unit", "ns")),
            };

            var key = unit switch
            {
                "D" or "d" or "day" or "days" => "days",
                "h" or "H" or "hour" or "hours" or "hr" => "hours",
                "m" or "min" or "minute" or "minutes" or "T" => "minutes",
                "s" or "sec" or "second" or "seconds" or "S" => "seconds",
                "ms" or "millisecond" or "milliseconds" or "L" => "milliseconds",
                "us" or "microsecond" or "microseconds" => "microseconds",
                "W" or "w" or "week" or "weeks" => "weeks",
                _ => null,
            };

            if (key is null)
            {
                keywords.Set(new PyStr("microseconds"), new PyFloat(amount / 1000));
            }
            else
            {
                keywords.Set(new PyStr(key), new PyFloat(amount));
            }
        }

        return runtime.Machine.Call(type, [], keywords);

        static PyDict Single(string name, PyObject value)
        {
            var dict = new PyDict();
            dict.Set(new PyStr(name), value);
            return dict;
        }

        static (double, string) ParseDelta(string text)
        {
            var trimmed = text.Trim();
            var split = 0;

            while (split < trimmed.Length && (char.IsAsciiDigit(trimmed[split]) || trimmed[split] is '.' or '-' or '+'))
            {
                split++;
            }

            if (split == 0 || !double.TryParse(trimmed[..split], NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
            {
                throw Pd.ValueError($"unit abbreviation w/o a number: {text}");
            }

            return (amount, trimmed[split..].Trim());
        }
    }

    /// <summary><c>date_range</c>: the common fixed and calendar frequencies.</summary>
    private static PyObject DateRange(PdArgs arguments)
    {
        var start = arguments.Given("start") is { } s ? PdTimestamp.Parse(s, null) as PdTimestamp : null;
        var end = arguments.Given("end") is { } e ? PdTimestamp.Parse(e, null) as PdTimestamp : null;
        var periods = arguments.Given("periods") is { } p ? Pd.ToInt(p, "periods") : (int?)null;
        var freqText = arguments.Str("freq", "D");
        var given = (start is not null ? 1 : 0) + (end is not null ? 1 : 0) + (periods is not null ? 1 : 0);

        if (given != 2)
        {
            throw Pd.ValueError("Of the four parameters: start, end, periods, and freq, exactly three must be specified");
        }

        var digits = 0;
        while (digits < freqText.Length && char.IsAsciiDigit(freqText[digits]))
        {
            digits++;
        }

        var multiple = digits > 0 ? int.Parse(freqText[..digits], CultureInfo.InvariantCulture) : 1;
        var freq = freqText[digits..];

        if (arguments.Bool("normalize", false))
        {
            start = start is null ? null : new PdTimestamp(start.Micro - (start.Micro % PyDelta.MicrosecondsPerDay));
            end = end is null ? null : new PdTimestamp(end.Micro - (end.Micro % PyDelta.MicrosecondsPerDay));
        }

        long? fixedStep = freq switch
        {
            "D" => PyDelta.MicrosecondsPerDay,
            "h" or "H" => 3_600_000_000L,
            "min" or "T" => 60_000_000L,
            "s" or "S" => 1_000_000L,
            "ms" or "L" => 1000L,
            _ => null,
        };

        var values = new List<PyObject>();
        Pd.CheckSize(periods ?? 0);

        if (fixedStep is { } step)
        {
            step *= multiple;

            if (start is not null)
            {
                for (var current = start.Micro; periods is { } n ? values.Count < n : current <= end!.Micro; current += step)
                {
                    values.Add(new PdTimestamp(current));
                    Pd.CheckSize(values.Count);
                }
            }
            else
            {
                for (var k = periods!.Value - 1; k >= 0; k--)
                {
                    values.Add(new PdTimestamp(end!.Micro - (k * step)));
                }
            }
        }
        else
        {
            Func<PdTimestamp, PdTimestamp> roll = freq switch
            {
                "W" or "W-SUN" => static t => Weekday(t, 6),
                "W-MON" => static t => Weekday(t, 0),
                "MS" => static t => t.Parts.Day == 1 ? Midnight(t) : AddMonths(PdTimestamp.FromFields(t.Parts.Year, t.Parts.Month, 1), 1),
                "M" or "ME" => static t => MonthEnd(t.Parts.Year, t.Parts.Month) is var end && end.Micro >= Midnight(t).Micro ? end : MonthEnd(AddMonths(PdTimestamp.FromFields(t.Parts.Year, t.Parts.Month, 1), 1).Parts.Year, AddMonths(PdTimestamp.FromFields(t.Parts.Year, t.Parts.Month, 1), 1).Parts.Month),
                "YS" or "AS" => static t => t.Parts.Month == 1 && t.Parts.Day == 1 ? Midnight(t) : PdTimestamp.FromFields(t.Parts.Year + 1, 1, 1),
                "Y" or "YE" or "A" => static t => PdTimestamp.FromFields(t.Parts.Year, 12, 31),
                "QS" => static t => QuarterStart(t),
                "B" => static t => Business(t),
                _ => throw Pd.ValueError($"Invalid frequency: {freqText}"),
            };

            Func<PdTimestamp, PdTimestamp> next = freq switch
            {
                "W" or "W-SUN" or "W-MON" => static t => new PdTimestamp(t.Micro + (7 * PyDelta.MicrosecondsPerDay)),
                "MS" => static t => AddMonths(t, 1),
                "M" or "ME" => static t => AddMonths(PdTimestamp.FromFields(t.Parts.Year, t.Parts.Month, 1), 1) is var n ? MonthEnd(n.Parts.Year, n.Parts.Month) : t,
                "YS" or "AS" => static t => PdTimestamp.FromFields(t.Parts.Year + 1, 1, 1),
                "Y" or "YE" or "A" => static t => PdTimestamp.FromFields(t.Parts.Year + 1, 12, 31),
                "QS" => static t => AddMonths(t, 3),
                _ => static t => Business(new PdTimestamp(t.Micro + PyDelta.MicrosecondsPerDay)),
            };

            var current = roll(start ?? throw Pd.ValueError("date_range with this frequency needs a start date"));

            while (periods is { } n ? values.Count < n * multiple : current.Micro <= end!.Micro)
            {
                values.Add(current);
                Pd.CheckSize(values.Count);
                current = next(current);
            }

            if (multiple > 1)
            {
                values = [.. values.Where((_, i) => i % multiple == 0)];
            }
        }

        var freqName = freq switch { "H" => "h", "T" => "min", "S" => "s", "M" => "ME", "A" or "Y" => "YE-DEC", "YE" => "YE-DEC", "W" => "W-SUN", "AS" or "YS" => "YS-JAN", "QS" => "QS-JAN", _ => freq };
        return new PdIndex(new PdColumn(DType.DateTime, [.. values]), arguments.Given("name")) { Freq = (multiple > 1 ? multiple.ToString(CultureInfo.InvariantCulture) : string.Empty) + freqName };

        static PdTimestamp Midnight(PdTimestamp t) => new(t.Micro - (t.Micro % PyDelta.MicrosecondsPerDay));

        static PdTimestamp Weekday(PdTimestamp t, int weekday)
        {
            var midnight = Midnight(t);
            var current = PyDate.WeekdayOf(PyDate.OrdinalOf(t.Parts.Year, t.Parts.Month, t.Parts.Day));
            return new PdTimestamp(midnight.Micro + (((weekday - current + 7) % 7) * PyDelta.MicrosecondsPerDay));
        }

        static PdTimestamp AddMonths(PdTimestamp t, int months)
        {
            var p = t.Parts;
            var total = (p.Year * 12) + (p.Month - 1) + months;
            var (year, month) = (total / 12, (total % 12) + 1);
            return PdTimestamp.FromFields(year, month, Math.Min(p.Day, PyDate.DaysInMonth(year, month)), p.Hour, p.Minute, p.Second, p.Microsecond);
        }

        static PdTimestamp MonthEnd(int year, int month) => PdTimestamp.FromFields(year, month, PyDate.DaysInMonth(year, month));

        static PdTimestamp QuarterStart(PdTimestamp t)
        {
            var p = t.Parts;
            var month = (((p.Month - 1) / 3) * 3) + 1;
            var start = PdTimestamp.FromFields(p.Year, month, 1);
            return start.Micro >= Midnight(t).Micro ? start : AddMonths(start, 3);
        }

        static PdTimestamp Business(PdTimestamp t)
        {
            var current = Midnight(t);

            while (PyDate.WeekdayOf(PyDate.OrdinalOf(current.Parts.Year, current.Parts.Month, current.Parts.Day)) >= 5)
            {
                current = new PdTimestamp(current.Micro + PyDelta.MicrosecondsPerDay);
            }

            return current;
        }
    }

    private static PyObject AssertEqual(PdArgs arguments, bool frames)
    {
        var left = arguments.Required("left");
        var right = arguments.Required("right");
        var checkDtype = arguments.Bool("check_dtype", true);
        var exact = arguments.Bool("check_exact", false);
        var tolerance = arguments.Given("rtol") is { } rtol ? Pd.ToDouble(rtol) : 1e-5;

        PdSeries[] Columns(PyObject value) => value switch
        {
            PdFrame frame when frames => [.. Enumerable.Range(0, frame.Columns.Count).Select(frame.Column)],
            PdSeries series when !frames => [series],
            _ => throw new PyRaise(PyErrors.AssertionError($"{(frames ? "DataFrame" : "Series")} Expected type <class 'pandas.core.{(frames ? "frame.DataFrame" : "series.Series")}'>, found <class '{value.TypeName}'> instead")),
        };

        var a = Columns(left);
        var b = Columns(right);

        if (frames && !((PdFrame)left).Columns.SameAs(((PdFrame)right).Columns))
        {
            throw new PyRaise(PyErrors.AssertionError("DataFrame.columns are different"));
        }

        if (a.Length != b.Length || (a.Length > 0 && !a[0].Index.SameAs(b[0].Index)))
        {
            throw new PyRaise(PyErrors.AssertionError($"{(frames ? "DataFrame" : "Series")}.index are different"));
        }

        for (var c = 0; c < a.Length; c++)
        {
            if (checkDtype && a[c].Data.Type != b[c].Data.Type)
            {
                throw new PyRaise(PyErrors.AssertionError($"Attributes of {(frames ? $"DataFrame.iloc[:, {c}] (column name=\"{Pd.Text(a[c].Name)}\")" : "Series")} are different\n\nAttribute \"dtype\" are different\n[left]:  {Pd.Name(a[c].Data.Type)}\n[right]: {Pd.Name(b[c].Data.Type)}"));
            }

            if (!frames && arguments.Bool("check_names", true) && !Pd.Same(a[c].Name, b[c].Name))
            {
                throw new PyRaise(PyErrors.AssertionError($"Attributes of Series are different\n\nAttribute \"name\" are different\n[left]:  {a[c].Name.Repr()}\n[right]: {b[c].Name.Repr()}"));
            }

            for (var r = 0; r < a[c].Count; r++)
            {
                var x = a[c].Data.Values[r];
                var y = b[c].Data.Values[r];
                var equal = Pd.Same(x, y)
                    || (!exact && Pd.IsNumber(x) && Pd.IsNumber(y) && Math.Abs(Pd.ToDouble(x) - Pd.ToDouble(y)) <= tolerance * Math.Max(1, Math.Abs(Pd.ToDouble(y))));

                if (!equal)
                {
                    throw new PyRaise(PyErrors.AssertionError($"{(frames ? $"DataFrame.iloc[:, {c}] (column name=\"{Pd.Text(a[c].Name)}\")" : "Series")} values are different\n[index]: [{Pd.Text(a[c].Index[r])}]\n[left]:  [{x.Repr()}]\n[right]: [{y.Repr()}]"));
                }
            }
        }

        return PyNone.Instance;
    }
}

/// <summary><c>pd.Timestamp</c> as a type: callable, and the target of <c>isinstance</c>.</summary>
internal sealed class PdTimestampType : PyCallable, DatetimeModule.IPyClassLike
{
    private readonly PdRuntime _runtime;

    public PdTimestampType(PdRuntime runtime) => _runtime = runtime;

    public override string Name => "Timestamp";

    public override string TypeName => "type";

    public override string Repr() => "<class 'pandas._libs.tslibs.timestamps.Timestamp'>";

    public bool Matches(PyObject value) => value is PdTimestamp;

    public PyObject Construct(PyObject[] arguments, PyDict? keywords) => _runtime.Run(() =>
    {
        var bound = new PdArgs("Timestamp", arguments, keywords, ["ts_input", "year", "month", "day", "hour", "minute", "second", "microsecond", "tz", "unit", "freq"]);

        if (arguments.Length >= 3 || (bound.Given("year") is not null && arguments.Length <= 1 && bound.Given("ts_input") is null))
        {
            int Part(string name, int index, int fallback) =>
                index < arguments.Length ? Pd.ToInt(arguments[index], name) : bound.Given(name) is { } v ? Pd.ToInt(v, name) : fallback;

            return PdTimestamp.FromFields(Part("year", 0, 1970), Part("month", 1, 1), Part("day", 2, 1), Part("hour", 3, 0), Part("minute", 4, 0), Part("second", 5, 0), Part("microsecond", 6, 0));
        }

        var input = bound.Required("ts_input");

        if (input is PyStr { Value: "now" or "today" })
        {
            var now = _runtime.Machine.Modules["datetime"].GetAttribute("datetime") is { } type && Attributes.TryGet(_runtime.Machine, type, "now") is { } method
                ? _runtime.Call(method)
                : throw Pd.ValueError("the clock is not available");
            return PdTimestamp.From(now)!;
        }

        return PdTimestamp.Parse(input, null, bound.Str("unit", "ns")) ?? throw Pd.ValueError($"could not convert string to Timestamp: {input.Repr()}");
    });

    public override PyObject? GetAttribute(string name) => name switch
    {
        "now" or "today" => new PyBuiltinFunction(name, (_, _) => Construct([new PyStr("now")], null)),
        _ => base.GetAttribute(name),
    };
}

/// <summary>
/// <c>pd.options</c>: <c>pd.options.display.max_rows = 100</c> and its kin. The display
/// options feed the text renderer; the others are accepted and remembered.
/// </summary>
internal sealed class PdOptions : PyObject
{
    private readonly PdRuntime _runtime;
    private readonly string _prefix;

    public PdOptions(PdRuntime runtime, string prefix = "")
    {
        _runtime = runtime;
        _prefix = prefix;
    }

    public override string TypeName => "DictWrapper";

    public override string Repr() => $"<pandas options {_prefix}>";

    /// <summary>Expands a short option name (<c>max_rows</c>) to its full key.</summary>
    public string Resolve(string name) => name.Contains('.', StringComparison.Ordinal) || !Known.Contains("display." + name) ? name : "display." + name;

    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        "display.max_rows", "display.min_rows", "display.max_columns", "display.width", "display.precision",
        "display.max_colwidth", "display.float_format", "display.expand_frame_repr", "display.show_dimensions",
        "mode.copy_on_write", "mode.chained_assignment", "future.infer_string",
    };

    public static PyObject Default(string key) => key switch
    {
        "display.max_rows" => PyInt.From(60),
        "display.min_rows" => PyInt.From(10),
        "display.max_columns" => PyInt.From(20),
        "display.width" => PyInt.From(80),
        "display.precision" => PyInt.From(6),
        "display.max_colwidth" => PyInt.From(50),
        "display.expand_frame_repr" => PyBool.True,
        "display.show_dimensions" => new PyStr("truncate"),
        "mode.chained_assignment" => new PyStr("warn"),
        "mode.copy_on_write" => PyBool.True,
        _ when Known.Contains(key) => PyNone.Instance,
        _ => throw new PyRaise(new PyException(PyExceptionType.Registry["KeyError"], $"No such keys(s): '{key}'")),
    };

    public override PyObject? GetAttribute(string name)
    {
        var key = _prefix.Length == 0 ? name : $"{_prefix}.{name}";

        if (Known.Any(k => k.StartsWith(key + ".", StringComparison.Ordinal)))
        {
            return new PdOptions(_runtime, key);
        }

        return _runtime.Options.TryGetValue(key, out var value) ? value : Known.Contains(key) ? Default(key) : null;
    }

    public override bool SetAttribute(string name, PyObject value)
    {
        var key = _prefix.Length == 0 ? name : $"{_prefix}.{name}";
        _runtime.Options[key] = value;
        return true;
    }
}
