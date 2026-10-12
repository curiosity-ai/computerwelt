using Microsoft.Playwright;

namespace Computerwelt.Playwright;

/// <summary>
/// What one tenant of a shared <see cref="PlaywrightSession"/> is allowed, beyond what the
/// session allows everyone.
/// </summary>
public sealed record PlaywrightScopeOptions
{
    /// <summary>A name for logs and for the host's own bookkeeping.</summary>
    public string? Name { get; init; }

    /// <summary>
    /// The hosts this tenant's pages may reach, in the same pattern syntax as
    /// <see cref="PlaywrightOptions.AllowedHosts"/>, or null to use the session's list.
    /// </summary>
    /// <remarks>
    /// It <b>replaces</b> the session's list for this tenant rather than adding to it, so a host
    /// that wants "the workspace's list plus this tenant's" passes the union. Replacing is the
    /// only reading under which a scope can also be <i>narrower</i> than its session.
    /// </remarks>
    public IReadOnlyList<string>? AllowedHosts { get; init; }

    /// <summary>
    /// How many browser contexts this tenant may have open at a time, or null for no limit
    /// beyond the session's <see cref="PlaywrightOptions.MaxContexts"/>.
    /// </summary>
    public int? MaxContexts { get; init; }

    /// <summary>
    /// Host code run on every context this tenant opens, after the navigation policy is installed
    /// and before the script gets the context.
    /// </summary>
    /// <remarks>
    /// The place for what the host decides about a tenant's profile and the script must not be
    /// able to skip: an init script that signs the page in, extra headers, cookies, a geolocation.
    /// A failure here fails the script's <c>new_context()</c>.
    /// </remarks>
    public Func<IBrowserContext, CancellationToken, Task>? ConfigureContext { get; init; }
}

/// <summary>
/// One tenant's share of a <see cref="PlaywrightSession"/>: its own navigation policy, its own
/// cap, and a list of the contexts and pages it has open that the host can see.
/// </summary>
/// <remarks>
/// <para>
/// A session is expensive — a driver process and a browser — and a context is cheap, so one
/// session is shared by many tenants and each tenant gets a scope. Registering a sandbox with
/// <c>builder.WithPlaywright(scope)</c>
/// routes every context its scripts open through the scope, so the scope's host list is what
/// their pages are filtered by.
/// </para>
/// <para>
/// The scope is also the host's window onto what a tenant's browser is doing:
/// <see cref="Pages"/> lists the pages it has open right now, whichever run opened them, so a host
/// can screenshot them for a live view without the script's cooperation.
/// </para>
/// <para>
/// Disposing a scope closes whatever it still has open. The session and its browser keep running
/// for every other tenant.
/// </para>
/// </remarks>
public sealed class PlaywrightScope : IAsyncDisposable
{
    private readonly List<IBrowserContext> _contexts = [];
    private bool _disposed;
    private long _lastActivityTicks = DateTimeOffset.UtcNow.UtcTicks;

    internal PlaywrightScope(PlaywrightSession session, PlaywrightScopeOptions options)
    {
        Session = session;
        Options = options;
        Policy = options.AllowedHosts is null
            ? session.Policy
            : new NavigationPolicy(session.Options with { AllowedHosts = options.AllowedHosts });
    }

    /// <summary>The shared session this scope borrows from.</summary>
    public PlaywrightSession Session { get; }

    /// <summary>What the host decided for this tenant.</summary>
    public PlaywrightScopeOptions Options { get; }

    /// <summary>What this tenant's pages may reach.</summary>
    public NavigationPolicy Policy { get; }

    /// <summary>Whether the scope has been disposed, after which it opens nothing.</summary>
    public bool IsDisposed => _disposed;

    /// <summary>When this tenant last opened a context, for a host's idle sweep.</summary>
    public DateTimeOffset LastActivity => new(Interlocked.Read(ref _lastActivityTicks), TimeSpan.Zero);

    /// <summary>The contexts this tenant has open right now.</summary>
    public IReadOnlyList<IBrowserContext> Contexts
    {
        get
        {
            lock (_contexts)
            {
                return [.. _contexts];
            }
        }
    }

    /// <summary>
    /// The pages this tenant has open right now, oldest context first.
    /// </summary>
    /// <remarks>
    /// A snapshot: a page may close between reading this and using it, and a host using it for a
    /// screenshot should expect a <see cref="PlaywrightException"/> for that page.
    /// </remarks>
    public IReadOnlyList<IPage> Pages =>
        [.. Contexts.SelectMany(static c => c.Pages).Where(static p => !p.IsClosed)];

    /// <summary>Raised when this tenant opens a context, after <see cref="PlaywrightScopeOptions.ConfigureContext"/> ran.</summary>
    public event EventHandler<IBrowserContext>? ContextOpened;

    /// <summary>Raised when one of this tenant's contexts closes, however it closed.</summary>
    public event EventHandler<IBrowserContext>? ContextClosed;

    /// <summary>
    /// Opens a context for this tenant, under the session's caps, this scope's cap and this
    /// scope's navigation policy.
    /// </summary>
    public async Task<IBrowserContext> NewContextAsync(
        IBrowser browser,
        BrowserNewContextOptions options,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_contexts)
        {
            if (Options.MaxContexts is { } cap && _contexts.Count >= cap)
            {
                throw new InvalidOperationException(
                    $"this sandbox already has {_contexts.Count} browser contexts open, which is "
                    + "its PlaywrightScopeOptions.MaxContexts. Close one, or raise the cap.");
            }
        }

        var context = await Session.NewContextAsync(browser, options, Policy, cancellationToken).ConfigureAwait(false);

        try
        {
            if (Options.ConfigureContext is { } configure)
            {
                await configure(context, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            await CloseQuietlyAsync(context).ConfigureAwait(false);
            Session.Release(context);
            throw;
        }

        context.Close += OnContextClosed;

        bool disposedMeanwhile;

        lock (_contexts)
        {
            disposedMeanwhile = _disposed;

            if (!disposedMeanwhile)
            {
                _contexts.Add(context);
            }
        }

        if (disposedMeanwhile)
        {
            await CloseQuietlyAsync(context).ConfigureAwait(false);
            Session.Release(context);
            throw new ObjectDisposedException(nameof(PlaywrightScope));
        }

        Interlocked.Exchange(ref _lastActivityTicks, DateTimeOffset.UtcNow.UtcTicks);
        ContextOpened?.Invoke(this, context);

        return context;
    }

    /// <summary>Forgets a context the tenant closed, so it stops counting against either cap.</summary>
    public void Release(IBrowserContext context)
    {
        bool removed;

        lock (_contexts)
        {
            removed = _contexts.Remove(context);
        }

        Session.Release(context);

        if (removed)
        {
            ContextClosed?.Invoke(this, context);
        }
    }

    private void OnContextClosed(object? sender, IBrowserContext context) => Release(context);

    /// <summary>
    /// Closes every context this tenant still has open. The session's browser keeps running.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        IBrowserContext[] contexts;

        lock (_contexts)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            contexts = [.. _contexts];
        }

        foreach (var context in contexts)
        {
            await CloseQuietlyAsync(context).ConfigureAwait(false);
            Release(context);
        }

        Session.Forget(this);
    }

    private static async Task CloseQuietlyAsync(IBrowserContext context)
    {
        // A context whose browser already went away throws on close, and a failure to tidy up
        // must not hide the reason we are tidying up.
        try
        {
            await context.CloseAsync().ConfigureAwait(false);
        }
        catch (PlaywrightException)
        {
        }
    }
}
