using Computerwelt.Playwright.Interop;
using Microsoft.Playwright;

namespace Computerwelt.Playwright;

/// <summary>
/// The browser backend a sandbox borrows.
/// </summary>
/// <remarks>
/// <para>
/// The session is host-owned and lives outside the sandbox: it starts the Playwright driver,
/// launches browsers and holds them, and every script that imports <c>playwright</c> is
/// handed a view onto what is already running. Nothing a script does starts a process, and
/// nothing a script does can outlive <see cref="DisposeAsync"/>.
/// </para>
/// <para>
/// One session is meant to be shared. Browsers are expensive and a browser <i>context</i> is
/// the unit Playwright isolates on — separate cookies, storage and cache — so two tenants
/// sharing this session still share nothing observable, exactly as two <c>Bash</c> instances
/// over one <see cref="PlaywrightSession"/> would.
/// </para>
/// </remarks>
public sealed class PlaywrightSession : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, IBrowser> _browsers = new(StringComparer.Ordinal);
    private readonly List<IBrowserContext> _contexts = [];
    private readonly List<PlaywrightScope> _scopes = [];
    private IPlaywright? _playwright;
    private PlaywrightScope? _defaultScope;
    private bool _disposed;

    private PlaywrightSession(IPlaywright playwright, PlaywrightOptions options, PlaywrightSandboxStatus sandboxStatus)
    {
        _playwright = playwright;
        Options = options;
        Policy = new NavigationPolicy(options);
        SandboxStatus = sandboxStatus;
    }

    /// <summary>The host's configuration, which a script cannot widen.</summary>
    public PlaywrightOptions Options { get; }

    /// <summary>What the browser is allowed to reach, for a tenant whose scope does not say otherwise.</summary>
    public NavigationPolicy Policy { get; }

    /// <summary>
    /// Whether the driver and its browsers run confined by <see cref="PlaywrightOptions.Sandbox"/>,
    /// and if not, why not.
    /// </summary>
    public PlaywrightSandboxStatus SandboxStatus { get; }

    /// <summary>
    /// The scope a sandbox registered with the session itself uses: the session's own host list
    /// and no cap beyond the session's.
    /// </summary>
    public PlaywrightScope DefaultScope
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            lock (_scopes)
            {
                return _defaultScope ??= Track(new PlaywrightScope(this, new PlaywrightScopeOptions { Name = "default" }));
            }
        }
    }

    /// <summary>The scopes open on this session, the default one included once it was used.</summary>
    public IReadOnlyList<PlaywrightScope> Scopes
    {
        get
        {
            lock (_scopes)
            {
                return [.. _scopes];
            }
        }
    }

    /// <summary>How many browser contexts are open across every script using this session.</summary>
    public int OpenContexts
    {
        get
        {
            lock (_contexts)
            {
                return _contexts.Count;
            }
        }
    }

    /// <summary>
    /// Starts the Playwright driver and returns a session over it.
    /// </summary>
    /// <remarks>
    /// No browser is launched here. The first script that asks for one pays for it, and a
    /// session nobody uses costs a driver process and nothing more.
    /// <para>
    /// With <see cref="PlaywrightOptions.Sandbox"/> set, the driver is started from a thread
    /// confined by a Landlock ruleset, so the driver and every browser it launches inherit it —
    /// see <see cref="PlaywrightSandboxOptions"/>. What was actually enforced is in
    /// <see cref="SandboxStatus"/>.
    /// </para>
    /// </remarks>
    /// <param name="options">The host's configuration, or null for the defaults — which reach no hosts at all.</param>
    /// <param name="cancellationToken">Abandons the wait for the driver to come up.</param>
    /// <exception cref="PlaywrightSandboxException">The sandbox is required and could not be enforced.</exception>
    public static async Task<PlaywrightSession> CreateAsync(
        PlaywrightOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        options ??= new PlaywrightOptions();

        if (options.Sandbox is not { } sandbox)
        {
            var playwright = await Microsoft.Playwright.Playwright.CreateAsync().ConfigureAwait(false);

            return new PlaywrightSession(playwright, options, PlaywrightSandboxStatus.NotRequested);
        }

        // The driver is forked inside CreateAsync's synchronous part, so it is that part that has
        // to run on the confined thread; the rest of the start-up awaits on the thread pool.
        var (starting, status) = await LandlockConfinement.RunAsync(sandbox, Microsoft.Playwright.Playwright.CreateAsync).ConfigureAwait(false);

        try
        {
            return new PlaywrightSession(await starting.ConfigureAwait(false), options, status);
        }
        catch (PlaywrightException exception) when (status.FileSystemConfined)
        {
            throw new PlaywrightSandboxException(
                "the Playwright driver did not start inside the Landlock sandbox. If it starts without "
                + "PlaywrightOptions.Sandbox, the ruleset is denying a path it needs - add it to "
                + $"PlaywrightSandboxOptions.ReadOnlyPaths or ReadWritePaths. {exception.Message}",
                exception);
        }
    }

    /// <summary>
    /// Opens a scope: one tenant's share of this session, with its own host list and cap.
    /// </summary>
    /// <remarks>
    /// Scopes cost nothing until they open a context. Dispose one when its tenant is done, which
    /// closes whatever it left open; the session disposes any still open when it is disposed.
    /// </remarks>
    public PlaywrightScope CreateScope(PlaywrightScopeOptions? options = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_scopes)
        {
            return Track(new PlaywrightScope(this, options ?? new PlaywrightScopeOptions()));
        }
    }

    private PlaywrightScope Track(PlaywrightScope scope)
    {
        _scopes.Add(scope);
        return scope;
    }

    /// <summary>Forgets a scope that was disposed.</summary>
    internal void Forget(PlaywrightScope scope)
    {
        lock (_scopes)
        {
            _scopes.Remove(scope);

            if (ReferenceEquals(_defaultScope, scope))
            {
                _defaultScope = null;
            }
        }
    }

    /// <summary>
    /// The device profiles Playwright ships, for <c>p.devices['iPhone 13']</c>.
    /// </summary>
    /// <remarks>
    /// A table of constants the driver already has, so it costs nothing and needs no browser.
    /// </remarks>
    public IReadOnlyDictionary<string, BrowserNewContextOptions> Devices =>
        _playwright?.Devices ?? throw new ObjectDisposedException(nameof(PlaywrightSession));

    /// <summary>The names a script may pass to <c>sync_playwright()</c>'s browser types.</summary>
    public IEnumerable<string> BrowserNames => Options.Browsers.Select(PlaywrightOptions.NameOf);

    /// <summary>Whether <paramref name="name"/> is a browser this session may launch.</summary>
    public bool Allows(string name) => BrowserNames.Contains(name, StringComparer.Ordinal);

    /// <summary>
    /// The browser of that name, launched on first use and shared thereafter.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The host did not list this browser, so it is not launchable at all — which is
    /// reported rather than silently substituted.
    /// </exception>
    public async Task<IBrowser> GetBrowserAsync(string name, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!Allows(name))
        {
            throw new InvalidOperationException(
                $"'{name}' is not available: this sandbox was configured with "
                + $"[{string.Join(", ", BrowserNames)}]. Add it to PlaywrightOptions.Browsers.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_browsers.TryGetValue(name, out var existing) && existing.IsConnected)
            {
                return existing;
            }

            var playwright = _playwright ?? throw new ObjectDisposedException(nameof(PlaywrightSession));

            var type = name switch
            {
                "firefox" => playwright.Firefox,
                "webkit" => playwright.Webkit,
                _ => playwright.Chromium,
            };

            var browser = await type.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = Options.Headless,
                Args = Options.LaunchArguments.Count > 0 ? Options.LaunchArguments : null,
                Channel = Options.Channel,
                ExecutablePath = Options.ExecutablePath,
                Timeout = Options.LaunchTimeout,
            }).ConfigureAwait(false);

            _browsers[name] = browser;
            return browser;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Opens a browser context under the session's caps and its navigation policy.
    /// </summary>
    /// <remarks>
    /// Every context the sandbox uses comes through here, which is what makes the two
    /// guarantees hold for all of them: the cap on how many may exist, and the request
    /// filter that refuses anything the host did not allow — including the requests a page
    /// makes on its own, which no check on <c>page.goto</c> would ever see.
    /// </remarks>
    public Task<IBrowserContext> NewContextAsync(
        IBrowser browser,
        BrowserNewContextOptions options,
        CancellationToken cancellationToken = default) =>
        NewContextAsync(browser, options, Policy, cancellationToken);

    /// <summary>
    /// Opens a context under the session's caps and <paramref name="policy"/>, which is how a
    /// <see cref="PlaywrightScope"/> gets its own host list onto the request filter.
    /// </summary>
    internal async Task<IBrowserContext> NewContextAsync(
        IBrowser browser,
        BrowserNewContextOptions options,
        NavigationPolicy policy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(browser);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(policy);
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_contexts)
        {
            // Checked before the context exists, so the cap is a bound on what is open
            // rather than on what was asked for.
            if (_contexts.Count >= Options.MaxContexts)
            {
                throw new InvalidOperationException(
                    $"this sandbox already has {_contexts.Count} browser contexts open, which is "
                    + "PlaywrightOptions.MaxContexts. Close one, or raise the cap.");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (Options.Viewport is { } viewport)
        {
            options.ViewportSize = new ViewportSize { Width = viewport.Width, Height = viewport.Height };
        }

        var context = await browser.NewContextAsync(options).ConfigureAwait(false);

        context.SetDefaultTimeout(Options.DefaultTimeout);
        context.SetDefaultNavigationTimeout(Options.DefaultTimeout);

        // The backstop the whole policy rests on: `page.goto` is checked for the sake of a
        // good error message, but *this* is what a redirect, an iframe, an image or a
        // `fetch()` the page issues has to get past.
        await context.RouteAsync("**/*", route =>
        {
            if (policy.IsAllowed(route.Request.Url))
            {
                _ = route.ContinueAsync();
            }
            else
            {
                _ = route.AbortAsync("blockedbyclient");
            }
        }).ConfigureAwait(false);

        context.Close += OnContextClosed;

        lock (_contexts)
        {
            _contexts.Add(context);
        }

        return context;
    }

    /// <summary>Forgets a context the sandbox closed, so it stops counting against the cap.</summary>
    public void Release(IBrowserContext context)
    {
        lock (_contexts)
        {
            _contexts.Remove(context);
        }
    }

    private void OnContextClosed(object? sender, IBrowserContext context) => Release(context);

    /// <summary>
    /// Closes every context and browser this session opened, then stops the driver.
    /// </summary>
    /// <remarks>
    /// A script has no way to leave anything running past this point, which is the reason
    /// the session — and not the script — owns all of it.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var scope in Scopes)
        {
            await scope.DisposeAsync().ConfigureAwait(false);
        }

        IBrowserContext[] contexts;

        lock (_contexts)
        {
            contexts = [.. _contexts];
            _contexts.Clear();
        }

        foreach (var context in contexts)
        {
            // A context whose browser already went away throws on close, and a failure to
            // tidy up must not hide the reason we are tidying up.
            try
            {
                await context.CloseAsync().ConfigureAwait(false);
            }
            catch (PlaywrightException)
            {
            }
        }

        foreach (var browser in _browsers.Values)
        {
            try
            {
                await browser.CloseAsync().ConfigureAwait(false);
            }
            catch (PlaywrightException)
            {
            }
        }

        _browsers.Clear();
        _playwright?.Dispose();
        _playwright = null;
        _gate.Dispose();
    }
}
