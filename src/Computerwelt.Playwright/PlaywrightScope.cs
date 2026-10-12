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

    /// <summary>
    /// Host code told about every screenshot and PDF one of this tenant's scripts takes, with or
    /// without a <c>path=</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The bytes already went to the script and, when it named a path, into the sandbox's
    /// filesystem; this is for a host that also wants to keep them outside the run - attach them to
    /// a conversation, show them to a person - which a script cannot do by itself.
    /// </para>
    /// <para>
    /// Called on the interpreter's thread, synchronously, so keep it short: queue the work. An
    /// exception it throws is swallowed rather than handed to the script, because the script did
    /// nothing wrong.
    /// </para>
    /// </remarks>
    public Action<PlaywrightCapture>? OnCapture { get; init; }

    /// <summary>
    /// A directory this tenant's browser profiles live in, or null to open its contexts on the
    /// session's shared browser.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Set, every context the tenant opens is a <i>persistent</i> context: a browser process of its
    /// own, launched over a profile folder below this directory - <c>chromium-1</c> for the first
    /// context open at a time, <c>chromium-2</c> for a second open beside it, and so on. The next
    /// context to open takes the lowest folder free, so a tenant whose runs open one context each
    /// keeps one profile: its cookies, its local storage and its cache carry over from one run to
    /// the next, and nothing of it is in the shared browser another tenant also uses.
    /// </para>
    /// <para>
    /// The price is a browser process per open context instead of one per session, and a
    /// <c>storage_state</c> cannot be loaded into one - the profile is what carries it. The folders
    /// outlive the scope; removing them is the host's decision.
    /// </para>
    /// <para>
    /// Under a <see cref="PlaywrightOptions.Sandbox"/> the directory has to be one the browser may
    /// write to - below <see cref="PlaywrightOptions.TemporaryDirectory"/> or one of
    /// <see cref="PlaywrightSandboxOptions.ReadWritePaths"/> - and the ruleset is fixed when the
    /// session starts, so <see cref="PlaywrightSession.CreateScope"/> refuses one that is not.
    /// </para>
    /// </remarks>
    public string? ProfileDirectory { get; init; }
}

/// <summary>What a capture was.</summary>
public enum PlaywrightCaptureKind
{
    /// <summary>A screenshot of a page or of an element.</summary>
    Screenshot,

    /// <summary>A page printed to PDF.</summary>
    Pdf,
}

/// <summary>One screenshot or PDF a script took.</summary>
/// <param name="Kind">Screenshot or PDF.</param>
/// <param name="Content">The bytes the script got.</param>
/// <param name="ContentType"><c>image/png</c>, <c>image/jpeg</c> or <c>application/pdf</c>.</param>
/// <param name="Path">The virtual path the script wrote it to, or null when it only kept the bytes.</param>
/// <param name="PageUrl">The address of the page it was taken from.</param>
public sealed record PlaywrightCapture(PlaywrightCaptureKind Kind, byte[] Content, string ContentType, string? Path, string? PageUrl)
{
    /// <summary>The content type of a screenshot, read from its bytes - the driver writes PNG unless asked for JPEG.</summary>
    internal static string ImageType(byte[] bytes) =>
        bytes.Length > 2 && bytes[0] == 0xFF && bytes[1] == 0xD8 ? "image/jpeg" : "image/png";
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
    private readonly Dictionary<IBrowserContext, string> _profiles = [];
    private readonly HashSet<string> _profilesInUse = new(StringComparer.Ordinal);
    private bool _disposed;
    private long _lastActivityTicks = DateTimeOffset.UtcNow.UtcTicks;

    internal PlaywrightScope(PlaywrightSession session, PlaywrightScopeOptions options)
    {
        Session = session;
        Options = options;
        Policy = options.AllowedHosts is null
            ? session.Policy
            : new NavigationPolicy(session.Options with { AllowedHosts = options.AllowedHosts });

        if (options.ProfileDirectory is { } profiles)
        {
            ProfileDirectory = Path.GetFullPath(profiles);

            if (session.SandboxStatus.FileSystemConfined && !session.WritablePaths.Any(root => IsBeneath(ProfileDirectory, root)))
            {
                throw new ArgumentException(
                    $"the browser cannot write to '{ProfileDirectory}': the session's Landlock sandbox only lets it write to "
                    + $"[{string.Join(", ", session.WritablePaths)}]. Put PlaywrightScopeOptions.ProfileDirectory below "
                    + "PlaywrightOptions.TemporaryDirectory or one of PlaywrightSandboxOptions.ReadWritePaths - which "
                    + "has to exist when the session starts.",
                    nameof(options));
            }
        }
    }

    /// <summary>The full path of <see cref="PlaywrightScopeOptions.ProfileDirectory"/>, or null when contexts use the shared browser.</summary>
    public string? ProfileDirectory { get; }

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
    /// <remarks>
    /// With a <see cref="PlaywrightScopeOptions.ProfileDirectory"/>, <paramref name="browser"/> only
    /// names the browser type: the context is launched over one of the tenant's profiles instead.
    /// </remarks>
    public Task<IBrowserContext> NewContextAsync(
        IBrowser browser,
        BrowserNewContextOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(browser);

        return OpenAsync(browser.BrowserType.Name, browser, options, cancellationToken);
    }

    /// <summary>
    /// Opens a context for this tenant in the browser of that name - the session's shared one, or
    /// one launched over a profile when the scope has a <see cref="PlaywrightScopeOptions.ProfileDirectory"/>.
    /// </summary>
    public Task<IBrowserContext> NewContextAsync(
        string browserName,
        BrowserNewContextOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(browserName);

        return OpenAsync(browserName, null, options, cancellationToken);
    }

    private async Task<IBrowserContext> OpenAsync(
        string browserName,
        IBrowser? browser,
        BrowserNewContextOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(_disposed, this);

        string? profile = null;

        lock (_contexts)
        {
            if (Options.MaxContexts is { } cap && _contexts.Count >= cap)
            {
                throw new InvalidOperationException(
                    $"this sandbox already has {_contexts.Count} browser contexts open, which is "
                    + "its PlaywrightScopeOptions.MaxContexts. Close one, or raise the cap.");
            }

            if (ProfileDirectory is { } root)
            {
                // Claimed before the launch, so two contexts opening at once never get one folder.
                profile = FreeProfile(root, browserName);
                _profilesInUse.Add(profile);
            }
        }

        IBrowserContext context;

        try
        {
            context = profile is null
                ? await Session.NewContextAsync(browser ?? await Session.GetBrowserAsync(browserName, cancellationToken).ConfigureAwait(false), options, Policy, cancellationToken).ConfigureAwait(false)
                : await Session.NewPersistentContextAsync(browserName, profile, options, Policy, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (profile is not null)
            {
                lock (_contexts)
                {
                    _profilesInUse.Remove(profile);
                }
            }

            throw;
        }

        if (profile is not null)
        {
            lock (_contexts)
            {
                _profiles[context] = profile;
            }
        }

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
            Forget(context);
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
            Forget(context);
            throw new ObjectDisposedException(nameof(PlaywrightScope));
        }

        Interlocked.Exchange(ref _lastActivityTicks, DateTimeOffset.UtcNow.UtcTicks);
        ContextOpened?.Invoke(this, context);

        return context;
    }

    /// <summary>Forgets a context the tenant closed, so it stops counting against either cap.</summary>
    public void Release(IBrowserContext context)
    {
        if (Forget(context))
        {
            ContextClosed?.Invoke(this, context);
        }
    }

    /// <summary>Stops counting a context and frees its profile folder; answers whether the scope had it open.</summary>
    private bool Forget(IBrowserContext context)
    {
        bool removed;

        lock (_contexts)
        {
            removed = _contexts.Remove(context);

            if (_profiles.Remove(context, out var profile))
            {
                _profilesInUse.Remove(profile);
            }
        }

        Session.Release(context);
        return removed;
    }

    /// <summary>The lowest-numbered profile folder no open context is using. Called under the lock.</summary>
    private string FreeProfile(string root, string browserName)
    {
        for (var slot = 1; ; slot++)
        {
            var candidate = Path.Combine(root, $"{browserName}-{slot}");

            if (!_profilesInUse.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private static bool IsBeneath(string path, string root)
    {
        var trimmed = root.TrimEnd(Path.DirectorySeparatorChar);

        return path.Equals(trimmed, StringComparison.Ordinal)
            || path.StartsWith(trimmed + Path.DirectorySeparatorChar, StringComparison.Ordinal);
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
