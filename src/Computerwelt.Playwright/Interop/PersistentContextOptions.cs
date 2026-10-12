using System.Reflection;
using Microsoft.Playwright;

namespace Computerwelt.Playwright.Interop;

/// <summary>
/// Turns the options of an ordinary context into the options of a persistent one.
/// </summary>
/// <remarks>
/// The two types carry the same context options under the same names - the persistent one adds
/// the launch options on top - so they are copied by name, which keeps up with whatever a later
/// Playwright adds. An option the persistent type has no counterpart for is refused by name
/// rather than dropped: a script that asked for it would otherwise get a context that silently
/// is not what it asked for.
/// </remarks>
internal static class PersistentContextOptions
{
    private static readonly (PropertyInfo From, PropertyInfo? To)[] Properties =
        [.. typeof(BrowserNewContextOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(static p => p.CanRead)
            .Select(static p => (p, typeof(BrowserTypeLaunchPersistentContextOptions).GetProperty(p.Name, BindingFlags.Public | BindingFlags.Instance)))];

    /// <summary>The persistent-context options carrying everything <paramref name="options"/> set.</summary>
    /// <exception cref="NotSupportedException">
    /// <paramref name="options"/> sets something a persistent context cannot have - a
    /// <c>StorageState</c>, whose job the profile already does.
    /// </exception>
    public static BrowserTypeLaunchPersistentContextOptions From(BrowserNewContextOptions options)
    {
        var result = new BrowserTypeLaunchPersistentContextOptions();

        foreach (var (from, to) in Properties)
        {
            if (from.GetValue(options) is not { } value)
            {
                continue;
            }

            if (to is null || !to.CanWrite || !to.PropertyType.IsAssignableFrom(from.PropertyType))
            {
                throw new NotSupportedException(from.Name);
            }

            to.SetValue(result, value);
        }

        return result;
    }
}
