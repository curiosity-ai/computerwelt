using System.Reflection;

namespace Computerwelt.Emulation.Python.Modules;

/// <summary>
/// The standard modules that are written in Python rather than C#.
/// </summary>
/// <remarks>
/// <para>
/// <c>functools</c>, <c>textwrap</c>, <c>contextlib</c> and their kind are far easier to get
/// exactly right as Python than as object-model plumbing, and they gain nothing from being
/// host code: a module compiled this way runs inside the sandbox, under the run's limits,
/// with exactly the reach any other program has. They ship as embedded <c>.py</c> files in
/// <c>Modules/Source/</c> and are compiled on first import, per run — so a module-level
/// variable is one tenant's, never shared.
/// </para>
/// <para>
/// A module only appears here by having a file, which keeps the importable set a list
/// somebody wrote.
/// </para>
/// </remarks>
internal static class BuiltInSources
{
    private const string Prefix = "Computerwelt.Emulation.Python.Modules.Source.";

    /// <summary>Returns the library for <paramref name="name"/>, or null when there is none.</summary>
    public static PythonSourceLibrary? Find(string name)
    {
        using var stream = typeof(BuiltInSources).Assembly.GetManifestResourceStream($"{Prefix}{name}.py");

        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return new PythonSourceLibrary(name, reader.ReadToEnd());
    }
}
