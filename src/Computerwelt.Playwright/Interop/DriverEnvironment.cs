using System.Reflection;

namespace Computerwelt.Playwright.Interop;

/// <summary>
/// Hands the Playwright driver environment variables of its own, without setting them on the
/// host process.
/// </summary>
/// <remarks>
/// <para>
/// The driver inherits the host's environment, and the only way the .NET package offers to add
/// to it is <c>Microsoft.Playwright.Helpers.Driver.EnvironmentVariables</c> — an internal,
/// process-wide dictionary copied into the driver's start info. Setting <c>TMPDIR</c> on the
/// host instead would move every temporary file the host writes, which is not this library's
/// decision to make.
/// </para>
/// <para>
/// So the variables are put into that dictionary for exactly as long as the driver takes to
/// start — its process is started synchronously, inside <c>Playwright.CreateAsync</c> — and
/// taken out again, under a lock so two sessions starting at once do not see each other's.
/// The dictionary is internal, so it is reached by reflection, and a package version that
/// moved it is reported rather than ignored: <see cref="Apply"/> answers false.
/// </para>
/// </remarks>
internal static class DriverEnvironment
{
    private static readonly object Gate = new();

    private static readonly Dictionary<string, string>? Variables =
        typeof(Microsoft.Playwright.IPlaywright).Assembly
            .GetType("Microsoft.Playwright.Helpers.Driver")
            ?.GetProperty("EnvironmentVariables", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(null) as Dictionary<string, string>;

    /// <summary>Whether this version of the Playwright package lets the driver be given variables.</summary>
    public static bool IsSupported => Variables is not null;

    /// <summary>
    /// The variables that make the driver, and every browser it launches, keep their temporary
    /// files in <paramref name="directory"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, string> TemporaryDirectory(string directory) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TMPDIR"] = directory,
            ["TMP"] = directory,
            ["TEMP"] = directory,
        };

    /// <summary>
    /// Runs <paramref name="start"/> with <paramref name="variables"/> added to the driver's
    /// environment, and answers whether they were.
    /// </summary>
    /// <remarks>
    /// <paramref name="start"/> runs either way — on a package version this cannot reach, it
    /// simply starts with the host's environment.
    /// </remarks>
    public static (T Result, bool Applied) Apply<T>(IReadOnlyDictionary<string, string> variables, Func<T> start)
    {
        if (Variables is not { } target || variables.Count == 0)
        {
            return (start(), variables.Count == 0);
        }

        lock (Gate)
        {
            var previous = new Dictionary<string, string?>(StringComparer.Ordinal);

            lock (target)
            {
                foreach (var (name, value) in variables)
                {
                    previous[name] = target.TryGetValue(name, out var old) ? old : null;
                    target[name] = value;
                }
            }

            try
            {
                return (start(), true);
            }
            finally
            {
                lock (target)
                {
                    foreach (var (name, old) in previous)
                    {
                        if (old is null)
                        {
                            target.Remove(name);
                        }
                        else
                        {
                            target[name] = old;
                        }
                    }
                }
            }
        }
    }
}
