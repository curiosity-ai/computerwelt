using Xunit;

namespace Computerwelt.Playwright.Tests;

/// <summary>
/// What a page reaches without a request route seeing it - a WebSocket, a worker's request - goes
/// past the same host list as everything else.
/// </summary>
/// <remarks>
/// A WebSocket handshake and a request a worker makes never reach a <c>context.route</c>
/// handler, so a request filter alone leaves a page free to reach any host the machine can. These
/// drive a real page against a real server and count what arrives.
/// </remarks>
[Collection(BrowserCollection.Name)]
public sealed class WebSocketPolicyTests
{
    private readonly BrowserFixture _browser;

    public WebSocketPolicyTests(BrowserFixture browser) => _browser = browser;

    private static bool Skipped =>
        Environment.GetEnvironmentVariable("COMPUTERWELT_SKIP_PLAYWRIGHT") == "1";

    // Opens a socket from the page's main world, from a frame or from a worker, sends one
    // message, and answers what came back - or how the attempt ended.
    private const string OPEN_SOCKET = """
        async ([url, from]) => {
            const attempt = `(url) => new Promise(done => {
                let socket;
                try { socket = new WebSocket(url); } catch (e) { return done('threw'); }
                const timer = setTimeout(() => done('timeout'), 3000);
                socket.onopen    = () => socket.send('ping');
                socket.onmessage = e => { clearTimeout(timer); done('reply:' + e.data); };
                socket.onerror   = () => { clearTimeout(timer); done('error'); };
                socket.onclose   = e => { clearTimeout(timer); done('closed:' + e.code); };
            })`;

            if (from === 'page') return await eval(attempt)(url);

            if (from === 'frame') {
                const frame = document.createElement('iframe');
                document.body.appendChild(frame);
                return await frame.contentWindow.eval(attempt)(url);
            }

            const source = `const attempt = ${attempt}; onmessage = async e => postMessage(await attempt(e.data));`;
            const worker = new Worker(URL.createObjectURL(new Blob([source], { type: 'text/javascript' })));
            return await new Promise(done => { worker.onmessage = e => done(e.data); worker.postMessage(url); });
        }
        """;

    // A worker fetches on its own behalf, which a request route does not see either.
    private const string WORKER_FETCH = """
        async (url) => {
            const source = `onmessage = async e => { try { const r = await fetch(e.data); postMessage('reached:' + await r.text()); } catch (x) { postMessage('failed'); } };`;
            const worker = new Worker(URL.createObjectURL(new Blob([source], { type: 'text/javascript' })));
            return await new Promise(done => { worker.onmessage = e => done(e.data); worker.postMessage(url); });
        }
        """;

    private async Task<string> OpenSocketAsync(LocalPage page, LocalWebSocketServer server, string[] allowedHosts, string from, string? profiles = null)
    {
        await using var scope = Session().CreateScope(new PlaywrightScopeOptions { AllowedHosts = allowedHosts, ProfileDirectory = profiles });

        var context = await scope.NewContextAsync("chromium", new Microsoft.Playwright.BrowserNewContextOptions());
        var tab     = await context.NewPageAsync();

        await tab.GotoAsync(page.Url);

        return await tab.EvaluateAsync<string>(OPEN_SOCKET, new[] { server.Url, from });
    }

    private PlaywrightSession Session()
    {
        Assert.True(_browser.Unavailable is null, _browser.Unavailable);
        return _browser.Session!;
    }

    [Theory]
    [InlineData("page")]
    [InlineData("frame")]
    [InlineData("worker")]
    public async Task A_socket_to_a_host_that_is_not_allowed_never_reaches_it(string from)
    {
        if (Skipped)
        {
            return;
        }

        using var page   = LocalPage.Start("socket test");
        using var server = LocalWebSocketServer.Start();

        // The page's own host is allowed and the socket server's port is not.
        var outcome = await OpenSocketAsync(page, server, [$"127.0.0.1:{page.Port}"], from);

        Assert.DoesNotContain("reply:", outcome, StringComparison.Ordinal);
        Assert.Equal(0, server.Handshakes);
    }

    [Theory]
    [InlineData("page")]
    [InlineData("frame")]
    [InlineData("worker")]
    public async Task A_socket_to_an_allowed_host_connects_and_carries_messages(string from)
    {
        if (Skipped)
        {
            return;
        }

        using var page   = LocalPage.Start("socket test");
        using var server = LocalWebSocketServer.Start();

        var outcome = await OpenSocketAsync(page, server, [$"127.0.0.1:{page.Port}", $"127.0.0.1:{server.Port}"], from);

        Assert.Equal("reply:echo:ping", outcome);
        Assert.Equal(1, server.Handshakes);
    }
    [Fact]
    public async Task A_socket_from_a_context_that_keeps_a_profile_is_held_to_the_host_list_too()
    {
        if (Skipped)
        {
            return;
        }

        using var page   = LocalPage.Start("socket test");
        using var server = LocalWebSocketServer.Start();

        var profiles = Path.Combine(Path.GetTempPath(), $"computerwelt-ws-{Guid.NewGuid():N}");

        try
        {
            var outcome = await OpenSocketAsync(page, server, [$"127.0.0.1:{page.Port}"], "page", profiles);

            Assert.DoesNotContain("reply:", outcome, StringComparison.Ordinal);
            Assert.Equal(0, server.Handshakes);
        }
        finally
        {
            if (Directory.Exists(profiles))
            {
                Directory.Delete(profiles, recursive: true);
            }
        }
    }

    [Fact]
    public async Task A_worker_cannot_fetch_from_a_host_that_is_not_allowed()
    {
        if (Skipped)
        {
            return;
        }

        using var page   = LocalPage.Start("worker fetch");
        using var server = LocalWebSocketServer.Start();

        await using var scope = Session().CreateScope(new PlaywrightScopeOptions { AllowedHosts = [$"127.0.0.1:{page.Port}"] });

        var context = await scope.NewContextAsync("chromium", new Microsoft.Playwright.BrowserNewContextOptions());
        var tab     = await context.NewPageAsync();

        await tab.GotoAsync(page.Url);

        Assert.Equal("failed", await tab.EvaluateAsync<string>(WORKER_FETCH, server.HttpUrl));
        Assert.Equal(0, server.Requests);
    }

    [Fact]
    public async Task A_worker_can_fetch_from_an_allowed_host()
    {
        if (Skipped)
        {
            return;
        }

        using var page   = LocalPage.Start("worker fetch");
        using var server = LocalWebSocketServer.Start();

        await using var scope = Session().CreateScope(new PlaywrightScopeOptions { AllowedHosts = [$"127.0.0.1:{page.Port}", $"127.0.0.1:{server.Port}"] });

        var context = await scope.NewContextAsync("chromium", new Microsoft.Playwright.BrowserNewContextOptions());
        var tab     = await context.NewPageAsync();

        await tab.GotoAsync(page.Url);

        Assert.Equal("reached:reached", await tab.EvaluateAsync<string>(WORKER_FETCH, server.HttpUrl));
        Assert.Equal(1, server.Requests);
    }

    [Fact]
    public async Task A_context_cannot_be_opened_with_a_proxy_of_its_own()
    {
        if (Skipped)
        {
            return;
        }

        await using var scope = Session().CreateScope(new PlaywrightScopeOptions { AllowedHosts = ["127.0.0.1"] });

        var options = new Microsoft.Playwright.BrowserNewContextOptions { Proxy = new Microsoft.Playwright.Proxy { Server = "http://127.0.0.1:1" } };

        await Assert.ThrowsAsync<ArgumentException>(() => scope.NewContextAsync("chromium", options));
        Assert.Empty(scope.Contexts);
    }
}
