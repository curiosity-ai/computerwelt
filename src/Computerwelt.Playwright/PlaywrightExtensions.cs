using Computerwelt.Emulation.Bash;
using Computerwelt.Emulation.Python;

namespace Computerwelt.Playwright;

/// <summary>
/// Adds the browser to a sandbox, at each of the three places one is configured.
/// </summary>
/// <remarks>
/// The shape mirrors how Python itself is added: <c>builder.WithPython()</c> joins the two
/// interpreters, and <c>builder.WithPlaywright(session)</c> joins a browser to them. Nothing
/// here changes what the sandbox may reach except by adding the module — the limits, the
/// filesystem and the allowlist are all decided elsewhere and read from there.
/// </remarks>
public static class PlaywrightExtensions
{
    /// <summary>
    /// Makes <c>playwright</c> importable from a bare <see cref="PythonRunner"/>.
    /// </summary>
    /// <remarks>
    /// For a host running Python on its own. Over a joined session, prefer
    /// <see cref="WithPlaywright(BashBuilder, PlaywrightSession, PythonOptions?)"/> so that
    /// the shell's <c>python</c> command gets it too.
    /// </remarks>
    public static PythonRunner WithPlaywright(
        this PythonRunner runner,
        PlaywrightSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(session);

        runner.Libraries.AddRange(PlaywrightLibrary.For(session, cancellationToken));
        return runner;
    }

    /// <summary>
    /// Returns options whose <c>python</c> command can import <c>playwright</c>.
    /// </summary>
    /// <remarks>
    /// <see cref="PythonOptions"/> is a record and this returns a new one, so a host that
    /// keeps a template of options is not surprised by one of them growing a browser.
    /// </remarks>
    public static PythonOptions WithPlaywright(
        this PythonOptions options,
        PlaywrightSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(session);

        return options with
        {
            Libraries = [.. options.Libraries, .. PlaywrightLibrary.For(session, cancellationToken)],
        };
    }

    /// <summary>
    /// Builds a shell whose <c>python</c> command can drive the host's browser.
    /// </summary>
    /// <remarks>
    /// The one-line form: <c>new BashBuilder().WithPlaywright(session).Build()</c> gives a
    /// session where <c>python -c "from playwright.sync_api import sync_playwright"</c> works
    /// against the same virtual filesystem every other command sees — so a screenshot the
    /// script takes is a file the next <c>ls</c> lists.
    /// </remarks>
    public static BashBuilder WithPlaywright(
        this BashBuilder builder,
        PlaywrightSession session,
        PythonOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(session);

        return builder.WithPython((options ?? new PythonOptions()).WithPlaywright(session));
    }
    /// <summary>
    /// Makes <c>playwright</c> importable from a bare <see cref="PythonRunner"/>, over one
    /// tenant's scope of a shared session.
    /// </summary>
    public static PythonRunner WithPlaywright(
        this PythonRunner runner,
        PlaywrightScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(scope);

        runner.Libraries.AddRange(PlaywrightLibrary.For(scope, cancellationToken));
        return runner;
    }

    /// <summary>Returns options whose <c>python</c> command can import <c>playwright</c> over one tenant's scope.</summary>
    public static PythonOptions WithPlaywright(
        this PythonOptions options,
        PlaywrightScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(scope);

        return options with
        {
            Libraries = [.. options.Libraries, .. PlaywrightLibrary.For(scope, cancellationToken)],
        };
    }

    /// <summary>
    /// Returns options whose <c>python</c> command can import <c>playwright</c> over a scope the
    /// host resolves at import time.
    /// </summary>
    /// <remarks>
    /// See <see cref="PlaywrightLibrary(Func{PlaywrightScope}, CancellationToken)"/>: for a sandbox
    /// built once whose browser may come and go afterwards.
    /// </remarks>
    public static PythonOptions WithPlaywright(
        this PythonOptions options,
        Func<PlaywrightScope> scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(scope);

        return options with
        {
            Libraries = [.. options.Libraries, .. PlaywrightLibrary.For(scope, cancellationToken)],
        };
    }

    /// <summary>
    /// Builds a shell whose <c>python</c> command drives the host's browser as one tenant of it:
    /// the scope's host list filters every page, and the host sees the pages it opens.
    /// </summary>
    public static BashBuilder WithPlaywright(
        this BashBuilder builder,
        PlaywrightScope scope,
        PythonOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(scope);

        return builder.WithPython((options ?? new PythonOptions()).WithPlaywright(scope));
    }

    /// <summary>
    /// Returns options whose <c>python</c> command can import <c>playwright</c> over a scope the
    /// host resolves asynchronously at import time.
    /// </summary>
    public static PythonOptions WithPlaywright(
        this PythonOptions options,
        Func<CancellationToken, Task<PlaywrightScope>> scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(scope);

        return options with
        {
            Libraries = [.. options.Libraries, .. PlaywrightLibrary.For(scope, cancellationToken)],
        };
    }

    /// <summary>
    /// Builds a shell whose <c>python</c> command drives the browser through a scope the host
    /// resolves each time a program imports <c>playwright</c>.
    /// </summary>
    public static BashBuilder WithPlaywright(
        this BashBuilder builder,
        Func<PlaywrightScope> scope,
        PythonOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(scope);

        return builder.WithPython((options ?? new PythonOptions()).WithPlaywright(scope));
    }
}
