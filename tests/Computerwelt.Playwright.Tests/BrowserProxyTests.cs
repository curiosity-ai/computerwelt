using System.Net;
using Computerwelt.Playwright.Interop;
using Xunit;

namespace Computerwelt.Playwright.Tests;

/// <summary>
/// The proxy every page's traffic goes through, driven without a browser: who may use it, and
/// what it lets them reach.
/// </summary>
public sealed class BrowserProxyTests
{
    private static HttpClient ClientThrough(BrowserProxy proxy, NetworkCredential? credentials) =>
        new(new HttpClientHandler
        {
            Proxy    = new WebProxy(proxy.Server) { Credentials = credentials, BypassProxyOnLocal = false },
            UseProxy = true,
        });

    private static NavigationPolicy Policy(params string[] hosts) =>
        new(new PlaywrightOptions { AllowedHosts = hosts });

    [Fact]
    public async Task A_connection_without_credentials_reaches_nothing()
    {
        using var page = LocalPage.Start("proxied");
        await using var proxy = BrowserProxy.Start();

        using var client = ClientThrough(proxy, credentials: null);
        using var response = await client.GetAsync(page.Url);

        Assert.Equal(HttpStatusCode.ProxyAuthenticationRequired, response.StatusCode);
    }

    [Fact]
    public async Task A_registered_context_reaches_an_allowed_host()
    {
        using var page = LocalPage.Start("proxied");
        await using var proxy = BrowserProxy.Start();
        using var registration = proxy.Register(Policy($"127.0.0.1:{page.Port}"));

        using var client = ClientThrough(proxy, new NetworkCredential(registration.Username, registration.Password));
        var body = await client.GetStringAsync(page.Url);

        Assert.Contains("proxied", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_registered_context_is_refused_a_host_its_policy_does_not_allow()
    {
        using var page = LocalPage.Start("proxied");
        await using var proxy = BrowserProxy.Start();
        using var registration = proxy.Register(Policy("example.com"));

        using var client = ClientThrough(proxy, new NetworkCredential(registration.Username, registration.Password));

        // Reset rather than answered, so a browser reports a network error instead of a page.
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(page.Url));
    }

    [Fact]
    public async Task A_port_outside_the_sandboxs_tcp_ports_is_refused_even_for_an_allowed_host()
    {
        using var page = LocalPage.Start("proxied");
        await using var proxy = BrowserProxy.Start(allowedPorts: [1]);
        using var registration = proxy.Register(Policy($"127.0.0.1:{page.Port}"));

        using var client = ClientThrough(proxy, new NetworkCredential(registration.Username, registration.Password));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(page.Url));
    }

    [Fact]
    public async Task Each_context_is_held_to_its_own_policy()
    {
        using var page = LocalPage.Start("proxied");
        await using var proxy = BrowserProxy.Start();
        using var allowed = proxy.Register(Policy($"127.0.0.1:{page.Port}"));
        using var refused = proxy.Register(Policy("example.com"));

        using var first  = ClientThrough(proxy, new NetworkCredential(allowed.Username, allowed.Password));
        using var second = ClientThrough(proxy, new NetworkCredential(refused.Username, refused.Password));

        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync(page.Url)).StatusCode);
        await Assert.ThrowsAsync<HttpRequestException>(() => second.GetAsync(page.Url));
    }

    [Fact]
    public async Task Withdrawn_credentials_reach_nothing()
    {
        using var page = LocalPage.Start("proxied");
        await using var proxy = BrowserProxy.Start();
        var registration = proxy.Register(Policy($"127.0.0.1:{page.Port}"));

        registration.Dispose();

        using var client = ClientThrough(proxy, new NetworkCredential(registration.Username, registration.Password));
        using var response = await client.GetAsync(page.Url);

        Assert.Equal(HttpStatusCode.ProxyAuthenticationRequired, response.StatusCode);
    }

    [Fact]
    public async Task A_wrong_password_reaches_nothing()
    {
        using var page = LocalPage.Start("proxied");
        await using var proxy = BrowserProxy.Start();
        using var registration = proxy.Register(Policy($"127.0.0.1:{page.Port}"));

        using var client = ClientThrough(proxy, new NetworkCredential(registration.Username, "not-the-password"));
        using var response = await client.GetAsync(page.Url);

        Assert.Equal(HttpStatusCode.ProxyAuthenticationRequired, response.StatusCode);
    }
}
