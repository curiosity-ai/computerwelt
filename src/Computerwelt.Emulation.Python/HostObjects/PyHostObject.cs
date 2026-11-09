using System.Collections;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.HostObjects;

/// <summary>
/// A host (C#) object a program reaches by attribute: properties are read, methods are called,
/// what comes back is converted, and which members exist at all is decided by a
/// <see cref="HostObjectPolicy"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Names.</b> A member is found by its own name or in <c>snake_case</c> — <c>q.start_at(...)</c>
/// is <c>Q().StartAt(...)</c> — so code can be written either way.
/// </para>
/// <para>
/// <b>Arguments.</b> Each overload is tried in turn and the first one every argument converts to is
/// called: <c>None</c> is <see langword="null"/>, ints and floats follow the parameter type, a
/// <c>str</c> becomes a string, an enum member, or a value the policy marks as text (an id) through
/// its <c>Parse</c>, lists and dicts become arrays, lists and dictionaries. Keywords name parameters.
/// Generic methods and delegates are not callable from Python.
/// </para>
/// <para>
/// <b>Async.</b> A method returning a <see cref="Task"/> is awaited before the program sees the
/// result, by blocking the calling thread. That is the one place this bridge blocks, and it is
/// sound only because the interpreter runs a cell on a thread of its own with nothing to deadlock
/// against; a host that drives a session from a thread with a synchronisation context must move
/// the cell to the pool first (<c>Task.Run</c>), as Mosaik's notebook kernel does.
/// </para>
/// <para>
/// <b>Results.</b> Numbers, strings, bools and byte arrays are the Python ones; an enum is its name;
/// a dictionary is a dict; any other enumerable is a list (up to <see cref="HostObjectPolicy.MaxItems"/>);
/// a text type is its string; an object of an exposed type is another host object; anything else is
/// its text, so a type the policy does not name never becomes reachable.
/// </para>
/// </remarks>
public sealed class PyHostObject : PyObject, IPyAttributeNames, IPyHasShape
{
    private readonly HostObjectPolicy _policy;
    private readonly MemberTable _members;

    private PyHostObject(object target, HostObjectPolicy policy)
    {
        Target = target;
        _policy = policy;
        _members = MemberTable.For(target.GetType(), policy);
    }

    /// <summary>The wrapped object.</summary>
    public object Target { get; }

    /// <inheritdoc />
    public override string TypeName => Target.GetType().Name;

    /// <summary>Wraps a host value for a program: converted when it is a plain value, a host object when the policy exposes its type.</summary>
    public static PyObject Wrap(object? value, HostObjectPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        return ToPython(value, policy);
    }

    /// <inheritdoc />
    public HostShape Shape => HostShape.OfType(Target.GetType(), _policy)!;

    /// <summary>The names of the members a program can reach, for <c>dir()</c> and completion.</summary>
    public IEnumerable<string> AttributeNames => _members.Names;

    /// <inheritdoc />
    public override string Repr()
    {
        string text;

        try
        {
            text = Target.ToString() ?? string.Empty;
        }
        catch (Exception)
        {
            // A ToString that throws is shown as the type, not as a failed repr.
            text = string.Empty;
        }

        return text.Length == 0 || text == Target.GetType().ToString() ? $"<{TypeName}>" : $"<{TypeName}: {text}>";
    }

    /// <inheritdoc />
    public override bool PyEquals(PyObject other) => other is PyHostObject host && Equals(host.Target, Target);

    /// <inheritdoc />
    public override BigInteger PyHash() => Target.GetHashCode();

    /// <inheritdoc />
    public override PyObject? GetAttribute(string name)
    {
        var entry = _members.Find(name);

        if (entry is null)
        {
            return null;
        }

        if (entry.Property is { } property)
        {
            return Guard(() => ToPython(property.GetValue(Target), _policy));
        }

        if (entry.Field is { } field)
        {
            return Guard(() => ToPython(field.GetValue(Target), _policy));
        }

        return new PyHostMethod(entry.Name, entry, HostShape.OfMethods(entry.Methods, _policy), (arguments, keywords) => Invoke(entry, arguments, keywords));
    }

    /// <inheritdoc />
    public override bool SetAttribute(string name, PyObject value)
    {
        var entry = _members.Find(name);

        if (entry?.Property is { CanWrite: true } property && property.SetMethod is { IsPublic: true })
        {
            var converted = Convert(value, property.PropertyType) ?? throw new PyRaise(PyErrors.TypeError(
                $"cannot assign {value.TypeName} to {Target.GetType().Name}.{entry.Name} ({property.PropertyType.Name})"));

            Guard(() => { property.SetValue(Target, converted.Value); return PyNone.Instance; });

            return true;
        }

        return false;
    }

    /// <inheritdoc />
    public override int? Length() => Target is ICollection collection ? collection.Count : null;

    /// <inheritdoc />
    public override IEnumerable<PyObject>? Iterate() =>
        Target is IEnumerable and not string ? Enumerate((IEnumerable)Target) : null;

    private IEnumerable<PyObject> Enumerate(IEnumerable source)
    {
        var count = 0;

        foreach (var item in source)
        {
            if (++count > _policy.MaxItems)
            {
                throw new PyRaise(PyErrors.Create("MemoryError", $"more than {_policy.MaxItems} items"));
            }

            yield return ToPython(item, _policy);
        }
    }

    private PyObject Invoke(Entry entry, PyObject[] arguments, PyDict? keywords)
    {
        var failures = new List<string>();

        foreach (var method in entry.Methods)
        {
            if (!TryBind(method, arguments, keywords, out var bound, out var why))
            {
                failures.Add($"{Signature(method)}: {why}");
                continue;
            }

            return Guard(() => ToPython(Await(method.Invoke(Target, bound)), _policy));
        }

        throw new PyRaise(PyErrors.TypeError(
            $"{Target.GetType().Name}.{entry.Name}() has no overload for these arguments:\n  " + string.Join("\n  ", failures)));
    }

    private bool TryBind(MethodInfo method, PyObject[] positional, PyDict? keywords, out object?[] bound, out string why)
    {
        var parameters = method.GetParameters();
        var acceptsParams = parameters.Length > 0 && parameters[^1].GetCustomAttribute<ParamArrayAttribute>() is not null;
        var fixedCount = acceptsParams ? parameters.Length - 1 : parameters.Length;

        bound = new object?[parameters.Length];
        why = string.Empty;

        if (positional.Length > fixedCount && !acceptsParams)
        {
            why = $"takes {fixedCount} arguments, {positional.Length} given";
            return false;
        }

        var supplied = new bool[parameters.Length];

        for (var i = 0; i < Math.Min(positional.Length, fixedCount); i++)
        {
            if (Convert(positional[i], parameters[i].ParameterType) is not { } converted)
            {
                why = $"argument {i + 1} ({positional[i].TypeName}) is not a {parameters[i].ParameterType.Name}";
                return false;
            }

            bound[i] = converted.Value;
            supplied[i] = true;
        }

        if (acceptsParams)
        {
            var element = parameters[^1].ParameterType.GetElementType()!;
            var rest = positional.Skip(fixedCount).ToArray();
            var array = Array.CreateInstance(element, rest.Length);

            for (var i = 0; i < rest.Length; i++)
            {
                if (Convert(rest[i], element) is not { } converted)
                {
                    why = $"argument {fixedCount + i + 1} ({rest[i].TypeName}) is not a {element.Name}";
                    return false;
                }

                array.SetValue(converted.Value, i);
            }

            bound[^1] = array;
            supplied[^1] = true;
        }

        if (keywords is not null)
        {
            foreach (var (key, value) in keywords.Entries)
            {
                var name = key.Display();
                var index = Array.FindIndex(parameters, p => string.Equals(p.Name, name, StringComparison.Ordinal));

                if (index < 0 || supplied[index])
                {
                    why = index < 0 ? $"no parameter named '{name}'" : $"'{name}' given twice";
                    return false;
                }

                if (Convert(value, parameters[index].ParameterType) is not { } converted)
                {
                    why = $"'{name}' ({value.TypeName}) is not a {parameters[index].ParameterType.Name}";
                    return false;
                }

                bound[index] = converted.Value;
                supplied[index] = true;
            }
        }

        for (var i = 0; i < parameters.Length; i++)
        {
            if (supplied[i])
            {
                continue;
            }

            if (parameters[i].HasDefaultValue)
            {
                bound[i] = parameters[i].DefaultValue is DBNull or null ? DefaultOf(parameters[i].ParameterType) : parameters[i].DefaultValue;
            }
            else
            {
                why = $"missing '{parameters[i].Name}'";
                return false;
            }
        }

        return true;
    }

    private static object? DefaultOf(Type type) => type.IsValueType ? Activator.CreateInstance(type) : null;

    // The one blocking wait; see the remarks on the class.
    private static object? Await(object? result)
    {
        switch (result)
        {
            case Task task:
                task.GetAwaiter().GetResult();

                var type = task.GetType();

                return type.IsGenericType && type.GetProperty("Result") is { } property && property.PropertyType.Name != "VoidTaskResult"
                    ? property.GetValue(task)
                    : null;

            case null:
                return null;
        }

        var resultType = result.GetType();

        if (resultType == typeof(ValueTask))
        {
            ((ValueTask)result).AsTask().GetAwaiter().GetResult();
            return null;
        }

        if (resultType.IsGenericType && resultType.GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            return Await(resultType.GetMethod("AsTask")!.Invoke(result, null));
        }

        return result;
    }

    // A host exception leaving the sandbox is the sandbox leaking, so each failure becomes the Python one; a cancellation
    // stays a cancellation, because that is how the session stops a cell and no `except` in the program may swallow it.
    private static PyObject Guard(Func<PyObject> action)
    {
        try
        {
            return action();
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            throw Translate(tie.InnerException);
        }
        catch (AggregateException aggregate) when (aggregate.InnerExceptions.Count == 1)
        {
            throw Translate(aggregate.InnerExceptions[0]);
        }
        catch (Exception e) when (e is not PyRaise and not OperationCanceledException)
        {
            throw Translate(e);
        }
    }

    private static Exception Translate(Exception exception) => exception switch
    {
        PyRaise or OperationCanceledException => exception,
        TargetInvocationException { InnerException: { } inner } => Translate(inner),
        AggregateException { InnerExceptions.Count: 1 } aggregate => Translate(aggregate.InnerExceptions[0]),
        ArgumentException or FormatException => new PyRaise(PyErrors.ValueError(exception.Message)),
        KeyNotFoundException => new PyRaise(PyErrors.Create("KeyError", exception.Message)),
        UnauthorizedAccessException => new PyRaise(PyErrors.Create("PermissionError", exception.Message)),
        NotSupportedException or NotImplementedException => new PyRaise(PyErrors.Create("NotImplementedError", exception.Message)),
        _ => new PyRaise(PyErrors.RuntimeError($"{exception.GetType().Name}: {exception.Message}")),
    };

    private static string Signature(MethodInfo method) =>
        method.Name + "(" + string.Join(", ", method.GetParameters().Select(static p => $"{p.ParameterType.Name} {p.Name}")) + ")";

    // ---- host -> Python

    internal static PyObject ToPython(object? value, HostObjectPolicy policy)
    {
        switch (value)
        {
            case null: return PyNone.Instance;
            case PyObject already: return already;
            case bool b: return PyBool.Of(b);
            case string s: return new PyStr(s);
            case char c: return new PyStr(c.ToString());
            case byte[] bytes: return new PyBytes(bytes);
            case float f: return new PyFloat(f);
            case double d: return new PyFloat(d);
            case decimal m: return new PyFloat((double)m);
            case Enum e: return new PyStr(e.ToString());
            case sbyte or byte or short or ushort or int or uint or long or ulong or nint or nuint:
                return PyInt.From(System.Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture) is var l && value is ulong u && u > long.MaxValue ? (BigInteger)u : l);
        }

        var type = value.GetType();

        if (policy.IsTextType(type) || value is Guid or DateTime or DateTimeOffset or TimeSpan or Type)
        {
            return new PyStr(value is Type t ? t.Name : value.ToString() ?? string.Empty);
        }

        if (value is IDictionary dictionary)
        {
            var result = new PyDict();

            foreach (DictionaryEntry entry in dictionary)
            {
                result.Set(ToPython(entry.Key, policy), ToPython(entry.Value, policy));
            }

            return result;
        }

        if (policy.Exposes(type))
        {
            // An exposed collection stays an object (it may be lazy, or a fluent query that is also enumerable); iterating it gives items.
            return new PyHostObject(value, policy);
        }

        if (value is IEnumerable sequence)
        {
            var items = new List<PyObject>();

            foreach (var item in sequence)
            {
                if (items.Count >= policy.MaxItems)
                {
                    throw new PyRaise(PyErrors.Create("MemoryError", $"more than {policy.MaxItems} items"));
                }

                items.Add(ToPython(item, policy));
            }

            return new PyList(items);
        }

        // A type the policy does not name never becomes an object a program can reach into: it is what it prints as.
        return new PyStr(value.ToString() ?? type.Name);
    }

    // ---- Python -> host

    /// <summary>Converts to <paramref name="target"/>, or returns null when the value cannot be one. The boxed result may itself be null.</summary>
    private (object? Value, bool Ok)? Convert(PyObject value, Type target) =>
        TryConvert(value, target, _policy, out var converted) ? (converted, true) : null;

    private static bool TryConvert(PyObject value, Type target, HostObjectPolicy policy, out object? converted)
    {
        converted = null;

        var underlying = Nullable.GetUnderlyingType(target);

        if (value is PyNone)
        {
            return !target.IsValueType || underlying is not null;
        }

        target = underlying ?? target;

        if (value is PyHostObject host)
        {
            converted = host.Target;
            return target.IsInstanceOfType(host.Target);
        }

        if (target == typeof(object))
        {
            converted = value switch
            {
                PyBool b => b.IsTruthy(),
                PyInt i => i.Value >= long.MinValue && i.Value <= long.MaxValue ? (long)i.Value : i.Value,
                PyFloat f => f.Value,
                PyStr s => s.Value,
                PyBytes bytes => bytes.Value,
                PyList or PyTuple => ConvertSequence(value, typeof(List<object?>), policy),
                PyDict => ConvertDictionary((PyDict)value, typeof(Dictionary<string, object?>), policy),
                _ => null,
            };

            return converted is not null;
        }

        switch (value)
        {
            case PyBool flag when target == typeof(bool):
                converted = flag.IsTruthy();
                return true;

            case PyInt integer when target.IsPrimitive || target == typeof(decimal) || target == typeof(BigInteger):
                return TryNumber(integer.Value, target, out converted);

            case PyFloat number when target == typeof(double) || target == typeof(float) || target == typeof(decimal):
                converted = System.Convert.ChangeType(number.Value, target, System.Globalization.CultureInfo.InvariantCulture);
                return true;

            case PyStr text:
                return TryFromText(text.Value, target, policy, out converted);

            case PyBytes bytes when target == typeof(byte[]):
                converted = bytes.Value;
                return true;

            case PyList or PyTuple when IsSequenceTarget(target):
                converted = ConvertSequence(value, target, policy);
                return converted is not null;

            case PyDict dict when IsDictionaryTarget(target):
                converted = ConvertDictionary(dict, target, policy);
                return converted is not null;
        }

        return false;
    }

    private static bool TryNumber(BigInteger value, Type target, out object? converted)
    {
        converted = null;

        try
        {
            converted = target == typeof(BigInteger)
                ? value
                : target == typeof(double) ? (double)value
                : target == typeof(float) ? (float)value
                : target == typeof(decimal) ? (decimal)value
                : target == typeof(bool) || target == typeof(char) ? null
                : System.Convert.ChangeType((object)(value >= long.MinValue && value <= long.MaxValue ? (long)value : (object)(ulong)value), target, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception e) when (e is OverflowException or InvalidCastException)
        {
            // The value does not fit the parameter: this overload is not the one meant.
            return false;
        }

        return converted is not null;
    }

    private static bool TryFromText(string text, Type target, HostObjectPolicy policy, out object? converted)
    {
        converted = null;

        if (target == typeof(string))
        {
            converted = text;
            return true;
        }

        if (target.IsEnum)
        {
            return Enum.TryParse(target, text, ignoreCase: true, out converted);
        }

        if (target == typeof(Guid))
        {
            var ok = Guid.TryParse(text, out var guid);
            converted = guid;
            return ok;
        }

        if (!policy.IsTextType(target) && !policy.Exposes(target))
        {
            return false;
        }

        // An id or a path, read back from the text it crossed as: the type's own Parse, an implicit conversion, or a constructor.
        try
        {
            if (target.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, [typeof(string)]) is { } parse)
            {
                converted = parse.Invoke(null, [text]);
                return true;
            }

            if (target.GetMethod("op_Implicit", BindingFlags.Public | BindingFlags.Static, [typeof(string)]) is { } implicitly && implicitly.ReturnType == target)
            {
                converted = implicitly.Invoke(null, [text]);
                return true;
            }

            if (target.GetConstructor([typeof(string)]) is { } constructor)
            {
                converted = constructor.Invoke([text]);
                return true;
            }
        }
        catch (TargetInvocationException)
        {
            // The text is not one of these (a malformed id): this overload is not the one meant.
            return false;
        }

        return false;
    }

    private static bool IsSequenceTarget(Type target) =>
        target.IsArray || (target.IsGenericType && target.GetGenericTypeDefinition() is var d
            && (d == typeof(List<>) || d == typeof(IEnumerable<>) || d == typeof(IList<>) || d == typeof(IReadOnlyList<>) || d == typeof(ICollection<>) || d == typeof(IReadOnlyCollection<>)));

    private static bool IsDictionaryTarget(Type target) =>
        target.IsGenericType && target.GetGenericTypeDefinition() is var d
        && (d == typeof(Dictionary<,>) || d == typeof(IDictionary<,>) || d == typeof(IReadOnlyDictionary<,>));

    private static object? ConvertSequence(PyObject value, Type target, HostObjectPolicy policy)
    {
        var items = value is PyList list ? list.Items : ((PyTuple)value).Items.ToList();
        var element = target.IsArray ? target.GetElementType()! : target.GetGenericArguments()[0];
        var converted = new List<object?>(items.Count);

        foreach (var item in items)
        {
            if (!TryConvert(item, element, policy, out var one))
            {
                return null;
            }

            converted.Add(one);
        }

        var array = Array.CreateInstance(element, converted.Count);

        for (var i = 0; i < converted.Count; i++)
        {
            array.SetValue(converted[i], i);
        }

        if (target.IsArray || target.IsAssignableFrom(array.GetType()))
        {
            return array;
        }

        return Activator.CreateInstance(typeof(List<>).MakeGenericType(element), array);
    }

    private static object? ConvertDictionary(PyDict value, Type target, HostObjectPolicy policy)
    {
        var arguments = target.GetGenericArguments();
        var result = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(arguments))!;

        foreach (var (key, item) in value.Entries)
        {
            if (!TryConvert(key, arguments[0], policy, out var k) || k is null || !TryConvert(item, arguments[1], policy, out var v))
            {
                return null;
            }

            result.Add(k, v);
        }

        return result;
    }
}

/// <summary>An object that can list the names a program may look up on it, for <c>dir()</c> and completion.</summary>
public interface IPyAttributeNames
{
    /// <summary>The attribute names.</summary>
    IEnumerable<string> AttributeNames { get; }
}
