using System.Runtime.CompilerServices;
using Computerwelt.Emulation.Python;
using Computerwelt.Emulation.Python.Modules;
using Computerwelt.Emulation.Python.Runtime;
using Computerwelt.Playwright.Api;
using Computerwelt.Playwright.Interop;

namespace Computerwelt.Playwright;

/// <summary>
/// The <c>playwright</c> package, as the sandbox can import it.
/// </summary>
/// <remarks>
/// <para>
/// Registering this is the whole of the opt-in: a sandbox that does not register it has no
/// importable <c>playwright</c> and no route to a browser, exactly as a sandbox with no
/// filesystem has no <c>open</c>.
/// </para>
/// <para>
/// Three names are registered, because <c>from playwright.sync_api import sync_playwright</c>
/// imports the dotted name and <c>import playwright.sync_api</c> binds the package. The third
/// is <c>playwright.async_api</c>, which exists only to fail with a sentence explaining why —
/// this interpreter runs a program to completion synchronously, so the asynchronous API has
/// nothing to schedule against.
/// </para>
/// </remarks>
public sealed class PlaywrightLibrary
{
    // One bridge per run, keyed by the machine running it, so that `playwright` and
    // `playwright.sync_api` are two views of one thing: `p.stop()` reached through either
    // has to close the contexts opened through the other.
    private readonly ConditionalWeakTable<VirtualMachine, Bridge> _bridges = [];

    // And one module object per run, so that the name reached through the package and the
    // name imported directly are the same object — as they are in CPython.
    private readonly ConditionalWeakTable<VirtualMachine, PyObject> _modules = [];
    private readonly Func<PlaywrightScope> _scope;
    private readonly CancellationToken _cancellationToken;

    /// <summary>Builds the library over a host-owned browser session.</summary>
    /// <param name="session">The browser backend, which the host owns and disposes.</param>
    /// <param name="cancellationToken">
    /// Stops further driver calls once it is cancelled. It does not interrupt one already in
    /// flight — the driver has no way to abandon a call it has made — so a host that needs a
    /// hard bound sets <see cref="PlaywrightOptions.DefaultTimeout"/> as well.
    /// </param>
    public PlaywrightLibrary(PlaywrightSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        _scope = () => session.DefaultScope;
        _cancellationToken = cancellationToken;
    }

    /// <summary>Builds the library over one tenant's scope of a host-owned session.</summary>
    /// <param name="scope">The tenant's scope, which the host owns and disposes.</param>
    /// <param name="cancellationToken">Stops further driver calls once it is cancelled.</param>
    public PlaywrightLibrary(PlaywrightScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        _scope = () => scope;
        _cancellationToken = cancellationToken;
    }

    /// <summary>
    /// Builds the library over a scope the host resolves when a program first imports
    /// <c>playwright</c>, once per run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For a host whose sandbox outlives any one browser session: the shell is built once, while
    /// the browser may be switched on, reconfigured or switched off afterwards. The resolver runs
    /// at import time, so a run always gets the scope as it is now.
    /// </para>
    /// <para>
    /// A resolver that throws makes the import fail with an <c>ImportError</c> carrying its
    /// message — throw a <see cref="PlaywrightUnavailableException"/> to say why the browser is
    /// not available ("the browser is disabled", "every browser session is in use"). No host
    /// exception reaches the program either way.
    /// </para>
    /// </remarks>
    /// <param name="scope">Answers the scope for the run that is importing.</param>
    /// <param name="cancellationToken">Stops further driver calls once it is cancelled.</param>
    public PlaywrightLibrary(Func<PlaywrightScope> scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        _scope = scope;
        _cancellationToken = cancellationToken;
    }

    /// <summary>The modules to register, for <c>PythonRunner.Libraries</c> or <c>PythonOptions.Libraries</c>.</summary>
    public IReadOnlyList<PythonLibrary> Modules =>
    [
        PythonLibrary.FromFactory("playwright.sync_api", SyncApi),
        PythonLibrary.FromFactory("playwright", Package),
        PythonLibrary.FromFactory("playwright.async_api", static _ => throw new PyRaise(new PyException(
            PyExceptionType.ImportError,
            "playwright.async_api is not available: this interpreter runs a program to "
            + "completion synchronously, so there is no event loop for it to await on. "
            + "Use playwright.sync_api, which does the waiting for you."))),
    ];

    /// <summary>The modules to register, for a host that wants one expression.</summary>
    public static IReadOnlyList<PythonLibrary> For(
        PlaywrightSession session,
        CancellationToken cancellationToken = default) =>
        new PlaywrightLibrary(session, cancellationToken).Modules;

    /// <summary>The modules to register over one tenant's scope.</summary>
    public static IReadOnlyList<PythonLibrary> For(
        PlaywrightScope scope,
        CancellationToken cancellationToken = default) =>
        new PlaywrightLibrary(scope, cancellationToken).Modules;

    /// <summary>The modules to register over a scope resolved per run.</summary>
    public static IReadOnlyList<PythonLibrary> For(
        Func<PlaywrightScope> scope,
        CancellationToken cancellationToken = default) =>
        new PlaywrightLibrary(scope, cancellationToken).Modules;

    private PlaywrightScope ResolveScope()
    {
        PlaywrightScope scope;

        try
        {
            scope = _scope();
        }
        catch (Exception exception)
        {
            throw new PyRaise(new PyException(
                PyExceptionType.ImportError,
                exception is PlaywrightUnavailableException
                    ? $"playwright is not available: {exception.Message}"
                    : $"playwright is not available: {exception.GetType().Name}: {exception.Message}"));
        }

        if (scope is null || scope.IsDisposed)
        {
            throw new PyRaise(new PyException(
                PyExceptionType.ImportError,
                "playwright is not available: the browser session this sandbox was given has been closed"));
        }

        return scope;
    }

    private Bridge BridgeFor(PythonHostContext context) =>
        _bridges.GetValue(context.Machine, _ =>
        {
            var bridge = new Bridge(context, ResolveScope(), _cancellationToken);

            // The safety net. A program that used `with sync_playwright()` has already given
            // its contexts back by the time this runs and it does nothing; a program that
            // forgot, or that ended on an uncaught exception, gets them given back for it.
            // Without this the only thing releasing a shared browser's contexts would be the
            // script remembering to, and a script that forgot is the case the browser needs
            // protecting from.
            context.Machine.WhenRunCompleted(bridge.ReleaseAll);

            return bridge;
        });

    private PyObject SyncApi(PythonHostContext context) =>
        _modules.GetValue(context.Machine, _ => BuildSyncApi(context));

    private PyObject BuildSyncApi(PythonHostContext context)
    {
        var bridge = BridgeFor(context);
        var module = new PyModuleObject("playwright.sync_api");

        module.Add("sync_playwright", new PyBuiltinFunction("sync_playwright", (positional, keywords) =>
        {
            var arguments = new Arguments("sync_playwright", positional, keywords);
            arguments.Done(0);

            return new PyPlaywrightContextManager(bridge);
        }));

        module.Add("expect", PyExpect.Callable(bridge));

        // The exception classes, which are the only way a program can catch what the driver
        // reports: they are not builtins and not in any registry, so an unregistered sandbox
        // cannot even name them.
        module.Add("Error", Errors.Error);
        module.Add("TimeoutError", Errors.TimeoutError);

        return module;
    }

    private PyObject Package(PythonHostContext context)
    {
        var module = new PyModuleObject("playwright");

        // `import playwright.sync_api` binds `playwright`, and the program then reaches the
        // submodule through it — so the package has to carry the attribute.
        module.Add("sync_api", SyncApi(context));

        return module;
    }
}
