using System.Text.RegularExpressions;
using Computerwelt.Emulation.Python.Builtins;
using Computerwelt.Emulation.Python.HostObjects;
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
    /// <para>
    /// Completion never calls anything the person wrote. A dotted name (<c>x.y.</c>) is resolved by attribute lookup;
    /// a call in the chain (<c>Q().Start</c>) is followed only through host objects, whose declared types say what a
    /// call returns (<see cref="HostShape"/>) — so <c>Q().StartAt("_User").</c> completes the members of what
    /// <c>StartAt</c> is declared to return, and a Python function call (<c>f().</c>) completes nothing.
    /// </para>
    /// </remarks>
    /// <returns>The candidates, sorted, and the offset where the word being completed starts.</returns>
    public Completions Complete(string source, int cursor)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(source);

        cursor = Math.Clamp(cursor, 0, source.Length);

        var line = source[..cursor];
        var lineStart = line.LastIndexOf('\n') + 1;

        if (!TryReadChain(line[lineStart..], out var segments, out var prefix))
        {
            return new Completions([], cursor);
        }

        var start = cursor - prefix.Length;

        using var running = _machine.Enter();

        IEnumerable<string> names;

        if (segments.Count == 0)
        {
            // A bare word: everything in scope.
            names = _machine.Globals.Entries.Select(static e => e.Key.Display())
                .Concat(_machine.Builtins.Entries.Select(static e => e.Key.Display()))
                .Concat(Keywords);
        }
        else
        {
            var target = Resolve(segments);

            if (target is null)
            {
                return new Completions([], start);
            }

            names = target is HostShape shape
                ? shape.Names
                : target is PyHostObject or IPyHasShape ? ((IPyHasShape)target).Shape.Names : Introspection.Dir(_machine, (PyObject)target).Items.Select(static n => n.Display());
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

    private sealed record Segment(string Name, int Calls);

    // The expression ending at the caret: names joined by dots, each optionally followed by call parentheses.
    // `a.b(1).c` -> [a, b+1 call] with prefix `c`; `va` -> [] with prefix `va`. Returns false for anything else (a literal, an operator).
    private static bool TryReadChain(string line, out List<Segment> segments, out string prefix)
    {
        segments = [];
        prefix = string.Empty;

        var i = line.Length;

        while (i > 0)
        {
            var c = line[i - 1];

            if (char.IsLetterOrDigit(c) || c == '_' || c == '.')
            {
                i--;
            }
            else if (c == ')')
            {
                var depth = 0;
                var j = i - 1;

                for (; j >= 0; j--)
                {
                    if (line[j] == ')')
                    {
                        depth++;
                    }
                    else if (line[j] == '(' && --depth == 0)
                    {
                        break;
                    }
                }

                if (j < 0)
                {
                    return false;
                }

                i = j;
            }
            else
            {
                break;
            }
        }

        var chain = line[i..];

        if (chain.Length > 0 && !(char.IsLetter(chain[0]) || chain[0] == '_'))
        {
            return false;
        }

        // Split at the dots that are not inside parentheses.
        var parts = new List<string>();
        var depthNow = 0;
        var from = 0;

        for (var k = 0; k < chain.Length; k++)
        {
            switch (chain[k])
            {
                case '(': depthNow++; break;
                case ')': depthNow--; break;
                case '.' when depthNow == 0:
                    parts.Add(chain[from..k]);
                    from = k + 1;
                    break;
            }
        }

        var last = chain[from..];

        if (last.Contains('(', StringComparison.Ordinal) || last.Contains(')', StringComparison.Ordinal))
        {
            // The caret is right after a call: there is no word to complete.
            return false;
        }

        prefix = last;

        foreach (var part in parts)
        {
            var open = part.IndexOf('(', StringComparison.Ordinal);
            var name = open < 0 ? part : part[..open];

            if (name.Length == 0)
            {
                return false;
            }

            var calls = 0;

            for (var k = open < 0 ? part.Length : open; k < part.Length; k++)
            {
                if (part[k] == '(' && depthAt(part, k) == 0)
                {
                    calls++;
                }
            }

            segments.Add(new Segment(name, calls));
        }

        return true;

        static int depthAt(string text, int index)
        {
            var d = 0;

            for (var k = 0; k < index; k++)
            {
                d += text[k] == '(' ? 1 : text[k] == ')' ? -1 : 0;
            }

            return d;
        }
    }

    // Walks the chain without running anything: a PyObject while it is a plain value, a HostShape once it is a host expression.
    private object? Resolve(List<Segment> segments)
    {
        var first = new PyStr(segments[0].Name);
        object? current = _machine.Globals.TryGetValue(first, out var global)
            ? global
            : _machine.Builtins.TryGetValue(first, out var builtin) ? builtin : null;

        for (var i = 0; current is not null; i++)
        {
            for (var c = 0; c < segments[i].Calls && current is not null; c++)
            {
                current = current switch
                {
                    HostShape shape => shape.Call(),
                    IPyHasShape host and PyHostMethod => host.Shape.Call(),
                    _ => null,
                };
            }

            if (i == segments.Count - 1)
            {
                return current;
            }

            current = current switch
            {
                HostShape shape => shape.Member(segments[i + 1].Name),
                IPyHasShape host => host.Shape.Member(segments[i + 1].Name),
                PyObject value => segments[i].Calls == 0 ? Attributes.TryGet(_machine, value, segments[i + 1].Name) : null,
                _ => null,
            };
        }

        return null;
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
