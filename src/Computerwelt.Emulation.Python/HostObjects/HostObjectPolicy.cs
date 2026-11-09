using System.Reflection;

namespace Computerwelt.Emulation.Python.HostObjects;

/// <summary>
/// What a program may reach through a host object: the policy that keeps a reflection bridge a
/// list somebody wrote rather than the whole of the host's object model.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="PyHostObject"/> answers attribute lookups by reflection, so without a policy
/// every public member of every object it returned — and of everything those returned — would
/// be one <c>.</c> away from a sandboxed program, including <c>GetType()</c> and from there the
/// whole runtime. The policy narrows that three ways: only members <i>declared by</i> an exposed
/// type are visible (so <see cref="object"/>'s and the framework's are not), a value whose type
/// is not exposed crosses as text rather than as an object, and named members are hidden outright.
/// </para>
/// <para>
/// A host adds vocabulary with this, never authority: it names the types a script may use, and
/// the script still runs under the sandbox's limits.
/// </para>
/// </remarks>
public sealed class HostObjectPolicy
{
    private readonly Func<Type, bool> _exposes;
    private readonly HashSet<string> _hidden;

    /// <summary>Creates a policy from a predicate over types.</summary>
    /// <param name="exposes">Whether members declared by a type may be reached.</param>
    /// <param name="hiddenMembers">Member names that are never reachable, on any type — <c>Internals</c>, say.</param>
    /// <param name="textTypes">Value types that cross as text and are read back from text: an id, a path.</param>
    public HostObjectPolicy(Func<Type, bool> exposes, IEnumerable<string>? hiddenMembers = null, Func<Type, bool>? textTypes = null)
    {
        ArgumentNullException.ThrowIfNull(exposes);

        _exposes = exposes;
        _hidden = new HashSet<string>(hiddenMembers ?? [], StringComparer.Ordinal);
        IsTextType = textTypes ?? (static _ => false);
    }

    /// <summary>A policy exposing every type declared in the given assemblies.</summary>
    public static HostObjectPolicy ForAssemblies(IEnumerable<Assembly> assemblies, IEnumerable<string>? hiddenMembers = null, Func<Type, bool>? textTypes = null)
    {
        ArgumentNullException.ThrowIfNull(assemblies);

        var set = new HashSet<Assembly>(assemblies);

        return new HostObjectPolicy(type => set.Contains(type.Assembly), hiddenMembers, textTypes);
    }

    /// <summary>The most items an enumerable result may hold when it is turned into a list.</summary>
    public int MaxItems { get; init; } = 1_000_000;

    /// <summary>Whether a value of this type crosses as text (and is parsed from text going the other way).</summary>
    public Func<Type, bool> IsTextType { get; }

    /// <summary>Whether members declared by <paramref name="type"/> may be reached.</summary>
    public bool Exposes(Type type) => _exposes(type);

    /// <summary>Whether a member name is hidden.</summary>
    public bool IsHidden(string name) => _hidden.Contains(name);
}
