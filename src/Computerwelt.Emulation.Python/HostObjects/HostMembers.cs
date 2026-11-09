using System.Reflection;

namespace Computerwelt.Emulation.Python.HostObjects;

internal sealed class Entry(string name)
{
    public string Name { get; } = name;

    /// <summary>The name as a program is shown it, per the policy's style.</summary>
    public string Shown { get; set; } = name;

    public PropertyInfo? Property { get; set; }

    public FieldInfo? Field { get; set; }

    public List<MethodInfo> Methods { get; } = [];
}

internal sealed class MemberTable
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<HostObjectPolicy, Dictionary<Type, MemberTable>> Cache = new();

    private readonly Dictionary<string, Entry> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Entry> _byKey = new(StringComparer.Ordinal);

/// <summary>The table of a type under a policy, built once.</summary>
    public static MemberTable For(Type type, HostObjectPolicy policy)
    {
        var tables = Cache.GetOrCreateValue(policy);

        lock (tables)
        {
            if (!tables.TryGetValue(type, out var table))
            {
                tables[type] = table = new MemberTable(type, policy);
            }

            return table;
        }
    }

    private MemberTable(Type type, HostObjectPolicy policy)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;

        bool Reachable(MemberInfo member) =>
            member.DeclaringType is { } owner && policy.Exposes(owner) && !policy.IsHidden(member.Name);

        // Interface members count too: a value typed as an interface answers what the interface declares.
        var types = new List<Type> { type };

        types.AddRange(type.GetInterfaces());

        foreach (var t in types)
        {
            foreach (var property in t.GetProperties(flags).Where(static p => p.GetIndexParameters().Length == 0 && p.GetMethod is { IsPublic: true }))
            {
                if (Reachable(property))
                {
                    Get(property.Name).Property ??= property;
                }
            }

            foreach (var field in t.GetFields(flags))
            {
                if (Reachable(field))
                {
                    Get(field.Name).Field ??= field;
                }
            }

            foreach (var method in t.GetMethods(flags).Where(static m => !m.IsSpecialName && !m.ContainsGenericParameters && !m.GetParameters().Any(static p => IsUnsupported(p.ParameterType))))
            {
                if (Reachable(method) && !Get(method.Name).Methods.Any(existing => SameParameters(existing, method)))
                {
                    Get(method.Name).Methods.Add(method);
                }
            }
        }

        foreach (var entry in _byName.Values)
        {
            // Fewest parameters first: `Q()` before `Q(userUID)`, and the cheapest binding wins on a tie.
            entry.Shown = policy.NameStyle == HostNameStyle.Pythonic ? HostNames.ToPython(entry.Name) : entry.Name;
            entry.Methods.Sort(static (a, b) => a.GetParameters().Length.CompareTo(b.GetParameters().Length));
            _byKey[Key(entry.Name)] = entry;
        }
    }

    public IEnumerable<string> Names => _byName.Values.Select(static e => e.Shown).Order(StringComparer.Ordinal);

    public Entry? Find(string name) =>
        _byName.TryGetValue(name, out var exact) ? exact : _byKey.GetValueOrDefault(Key(name));

    private Entry Get(string name)
    {
        if (!_byName.TryGetValue(name, out var entry))
        {
            _byName[name] = entry = new Entry(name);
        }

        return entry;
    }

    // `start_at`, `StartAt` and `startat` are the same member.
    private static string Key(string name) => HostNames.Key(name);

    private static bool IsUnsupported(Type type) => typeof(Delegate).IsAssignableFrom(type) || type.IsByRef || type.IsPointer;

    private static bool SameParameters(MethodInfo a, MethodInfo b) =>
        a.GetParameters().Select(static p => p.ParameterType).SequenceEqual(b.GetParameters().Select(static p => p.ParameterType));
}
