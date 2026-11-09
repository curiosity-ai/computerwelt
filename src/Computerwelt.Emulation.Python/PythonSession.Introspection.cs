using System.Text.RegularExpressions;
using Computerwelt.Emulation.Python.Builtins;
using Computerwelt.Emulation.Python.Modules;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python;

public sealed partial class PythonSession
{
    // A dotted name ending at the caret: `np.lin`, `df.groupby(...).s` is not one (it needs a call).
    private static readonly Regex Trailing = new(@"[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_0-9]*)*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The variables the cells have defined, for a variable inspector.</summary>
    /// <remarks>Modules, and names the session itself defines (<c>Out</c>, <c>_</c>, <c>_1</c>), are left out, as an IPython inspector does.</remarks>
    public IReadOnlyList<SessionVariable> Variables()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var running = _machine.Enter();
        var variables = new List<SessionVariable>();

        foreach (var (key, value) in _machine.Globals.Entries)
        {
            var name = key.Display();

            if (name.StartsWith('_') || name == "Out" || value is PyModuleObject)
            {
                continue;
            }

            variables.Add(new SessionVariable(name, value.TypeName, Summarise(value)));
        }

        variables.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));

        return variables;
    }

    /// <summary>Names that complete the text before <paramref name="cursor"/>.</summary>
    /// <remarks>
    /// Resolution is by attribute lookup on a dotted name only — <c>x.y.</c> completes, <c>f().</c> does not —
    /// so asking for completions never calls a function the person wrote.
    /// </remarks>
    /// <returns>The candidates, sorted, and the offset where the word being completed starts.</returns>
    public Completions Complete(string source, int cursor)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(source);

        cursor = Math.Clamp(cursor, 0, source.Length);

        var line = source[..cursor];
        var lineStart = line.LastIndexOf('\n') + 1;
        var match = Trailing.Match(line[lineStart..]);

        if (!match.Success)
        {
            return new Completions([], cursor);
        }

        using var running = _machine.Enter();

        // `math.sq` -> resolve `math`, complete `sq`; `math.` -> complete everything; `va` -> everything in scope.
        var parts = match.Value.Split('.');
        var prefix = parts[^1];
        var start = cursor - prefix.Length;
        IEnumerable<string> names;

        if (parts.Length == 1)
        {
            names = _machine.Globals.Entries.Select(static e => e.Key.Display())
                .Concat(_machine.Builtins.Entries.Select(static e => e.Key.Display()))
                .Concat(Keywords);
        }
        else
        {
            var target = ResolveName(parts[..^1]);

            if (target is null)
            {
                return new Completions([], start);
            }

            names = Introspection.Dir(_machine, target).Items.Select(static n => n.Display());
        }

        var hidePrivate = !prefix.StartsWith('_');
        var result = names
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && (!hidePrivate || !n.StartsWith('_')))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Take(200)
            .ToList();

        return new Completions(result, start);
    }

    private PyObject? ResolveName(string[] path)
    {
        if (path.Length == 0)
        {
            return null;
        }

        var key = new PyStr(path[0]);
        PyObject? current = _machine.Globals.TryGetValue(key, out var global)
            ? global
            : _machine.Builtins.TryGetValue(key, out var builtin) ? builtin : null;

        for (var i = 1; current is not null && i < path.Length; i++)
        {
            current = Attributes.TryGet(_machine, current, path[i]);
        }

        return current;
    }

    private static string Summarise(PyObject value)
    {
        try
        {
            var text = value.Repr();

            return text.Length > 80 ? text[..77] + "..." : text;
        }
        catch (PyRaise)
        {
            // A repr that raises is shown as its type, not as a failed inspector.
            return $"<{value.TypeName}>";
        }
    }

    private static readonly string[] Keywords =
    [
        "False", "None", "True", "and", "as", "assert", "async", "await", "break", "class", "continue", "def",
        "del", "elif", "else", "except", "finally", "for", "from", "global", "if", "import", "in", "is",
        "lambda", "nonlocal", "not", "or", "pass", "raise", "return", "try", "while", "with", "yield",
    ];
}

/// <summary>One variable of a notebook session.</summary>
/// <param name="Name">The name.</param>
/// <param name="Type">The Python type name.</param>
/// <param name="Summary">A short repr.</param>
public sealed record SessionVariable(string Name, string Type, string Summary);

/// <summary>The result of <see cref="PythonSession.Complete"/>.</summary>
/// <param name="Items">The candidate names.</param>
/// <param name="Start">Where in the source the word being completed starts.</param>
public sealed record Completions(IReadOnlyList<string> Items, int Start);
