using System.Reflection;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.HostObjects;

/// <summary>An object that has a static shape — the types it is declared as — which completion can read without running anything.</summary>
public interface IPyHasShape
{
    /// <summary>The shape.</summary>
    HostShape Shape { get; }
}

/// <summary>
/// What a host expression <i>would</i> be, worked out from declared types alone: the member names of a
/// value, what a property gives, what a method returns.
/// </summary>
/// <remarks>
/// This is what lets completion follow a fluent chain — <c>Q().StartAt("_User").</c> — without calling
/// anything: <c>Q</c> declares that it returns an <c>IQuery</c>, <c>StartAt</c> declares what it returns, and the
/// names offered are the members of that type under the same <see cref="HostObjectPolicy"/> that governs the
/// program. A task's result type is looked through, since a program sees the awaited value.
/// </remarks>
public sealed class HostShape
{
    private readonly IReadOnlyList<Type> _types;
    private readonly IReadOnlyList<MethodInfo>? _overloads;
    private readonly HostObjectPolicy _policy;

    private HostShape(IReadOnlyList<Type> types, IReadOnlyList<MethodInfo>? overloads, HostObjectPolicy policy)
    {
        _types = types;
        _overloads = overloads;
        _policy = policy;
    }

    /// <summary>The shape of a value of a type, or null when the policy exposes nothing about it.</summary>
    public static HostShape? OfType(Type type, HostObjectPolicy policy)
    {
        var unwrapped = Unwrap(type);

        return unwrapped is not null && policy.Exposes(unwrapped) ? new HostShape([unwrapped], null, policy) : null;
    }

    internal static HostShape OfMethods(IReadOnlyList<MethodInfo> overloads, HostObjectPolicy policy) => new([], overloads, policy);

    /// <summary>The member names a program can reach on a value of this shape.</summary>
    public IEnumerable<string> Names =>
        _types.SelectMany(type => MemberTable.For(type, _policy).Names).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);

    /// <summary>The shape of a member: what a property or field holds, or the method itself — null when there is no such member or nothing is known of it.</summary>
    public HostShape? Member(string name)
    {
        var shapes = new List<HostShape>();

        foreach (var type in _types)
        {
            if (MemberTable.For(type, _policy).Find(name) is not { } entry)
            {
                continue;
            }

            if (entry.Methods.Count > 0)
            {
                shapes.Add(OfMethods(entry.Methods, _policy));
            }
            else if ((entry.Property?.PropertyType ?? entry.Field?.FieldType) is { } memberType && OfType(memberType, _policy) is { } held)
            {
                shapes.Add(held);
            }
        }

        return Merge(shapes);
    }

    /// <summary>What calling a method of this shape returns, or null when this is not a method or its result is not exposed.</summary>
    public HostShape? Call() =>
        _overloads is null ? null : Merge(_overloads.Select(method => OfType(method.ReturnType, _policy)).OfType<HostShape>().ToList());

    private HostShape? Merge(List<HostShape> shapes)
    {
        if (shapes.Count == 0)
        {
            return null;
        }

        return shapes.Count == 1
            ? shapes[0]
            : new HostShape([.. shapes.SelectMany(static s => s._types).Distinct()], shapes.SelectMany(static s => s._overloads ?? []).ToList() is { Count: > 0 } all ? all : null, _policy);
    }

    private static Type? Unwrap(Type type)
    {
        if (Nullable.GetUnderlyingType(type) is { } inner)
        {
            return inner;
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() is var d && (d == typeof(Task<>) || d == typeof(ValueTask<>)))
        {
            return Unwrap(type.GetGenericArguments()[0]);
        }

        return type == typeof(void) || type == typeof(Task) || type == typeof(ValueTask) ? null : type;
    }
}

/// <summary>A method of a host object. Callable like any builtin; also knows what it returns, for completion.</summary>
public sealed class PyHostMethod : PyBuiltinFunction, IPyHasShape
{
    internal PyHostMethod(string name, Entry entry, HostShape shape, Func<PyObject[], PyDict?, PyObject> call)
        : base(name, call)
    {
        Entry = entry;
        Shape = shape;
    }

    internal Entry Entry { get; }

    /// <inheritdoc />
    public HostShape Shape { get; }

    /// <inheritdoc />
    public override string Repr() => $"<host method {Name}>";
}
