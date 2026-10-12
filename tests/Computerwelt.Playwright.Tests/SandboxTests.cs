using System.Text;
using Computerwelt.Emulation.Bash;
using Microsoft.Playwright;
using Xunit;

namespace Computerwelt.Playwright.Tests;

/// <summary>
/// The Landlock sandbox around the driver and its browsers.
/// </summary>
/// <remarks>
/// <para>
/// These start a session of their own, because the sandbox is decided when the driver starts.
/// They prove the two things worth proving: that a confined browser still works, and that it
/// really cannot read a file outside what it was granted — tested with a <c>file:</c> URL, the
/// one route a browser has to the disk, which the navigation policy is told to allow here so
/// that the kernel is the only thing left in the way.
/// </para>
/// <para>
/// On a host without Landlock — not Linux, an old kernel, an LSM list without it — the tests
/// that need it assert only that the session said so and started anyway.
/// </para>
/// </remarks>
[Collection(BrowserCollection.Name)]
public sealed class SandboxTests
{
    private readonly BrowserFixture _browser;

    public SandboxTests(BrowserFixture browser) => _browser = browser;

    private static bool Skipped =>
        Environment.GetEnvironmentVariable("COMPUTERWELT_SKIP_PLAYWRIGHT") == "1";

    private static PlaywrightOptions Options(PlaywrightSandboxOptions sandbox) => BrowserFixture.Options with
    {
        AllowedHosts = ["*"],
        AllowedSchemes = ["http", "https", "about", "data", "blob", "file"],
        Sandbox = sandbox,
    };

    [Fact]
    public async Task A_confined_browser_still_renders_and_the_status_says_it_is_confined()
    {
        if (Skipped)
        {
            return;
        }

        Assert.True(_browser.Unavailable is null, _browser.Unavailable);

        await using var session = await PlaywrightSession.CreateAsync(Options(new PlaywrightSandboxOptions()));

        if (Interop.LandlockConfinement.Unavailable() is { } reason)
        {
            Assert.False(session.SandboxStatus.FileSystemConfined);
            Assert.Equal(reason, session.SandboxStatus.Reason);
            return;
        }

        Assert.True(session.SandboxStatus.FileSystemConfined, session.SandboxStatus.ToString());

        var bash = Bash.CreateBuilder().WithWorkingDirectory("/").WithPlaywright(session).Build();

        await bash.FileSystem.WriteFileAsync(VPath.Parse("/run.py"), Encoding.UTF8.GetBytes("""
            from playwright.sync_api import sync_playwright

            with sync_playwright() as p:
                page = p.chromium.launch().new_page()
                page.goto('data:text/html,<h1>confined</h1>')
                page.screenshot(path='/shot.png')
                print(page.text_content('h1'))
            """));

        var result = await bash.ExecAsync("python /run.py && ls /shot.png");

        Assert.True(result.ExitCode == 0, result.Stderr.ToString());
        Assert.Equal("confined\n/shot.png\n", result.Stdout.ToString());
    }

    [Fact]
    public async Task A_confined_browser_reads_what_it_was_granted_and_nothing_else()
    {
        if (Skipped || Interop.LandlockConfinement.Unavailable() is not null)
        {
            return;
        }

        Assert.True(_browser.Unavailable is null, _browser.Unavailable);

        // Under the home directory, which nothing in the default grant list covers.
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), $".computerwelt-landlock-{Guid.NewGuid():N}");
        var granted = Path.Combine(root, "granted");
        var denied = Path.Combine(root, "denied");

        Directory.CreateDirectory(granted);
        Directory.CreateDirectory(denied);
        await File.WriteAllTextAsync(Path.Combine(granted, "page.html"), "<h1>granted</h1>");
        await File.WriteAllTextAsync(Path.Combine(denied, "page.html"), "<h1>secret</h1>");

        try
        {
            // Sanity check, unconfined: the denied file is perfectly readable to a browser that is
            // not sandboxed, so what the confined one fails to read is the kernel's doing.
            var plain = await _browser.Session!.GetBrowserAsync("chromium");
            var plainPage = await plain.NewPageAsync();

            await plainPage.GotoAsync(new Uri(Path.Combine(denied, "page.html")).AbsoluteUri);
            Assert.Equal("secret", await plainPage.TextContentAsync("h1"));
            await plainPage.CloseAsync();

            await using var session = await PlaywrightSession.CreateAsync(Options(new PlaywrightSandboxOptions
            {
                ReadOnlyPaths = [granted],
            }));

            Assert.True(session.SandboxStatus.FileSystemConfined, session.SandboxStatus.ToString());

            var browser = await session.GetBrowserAsync("chromium");
            var context = await session.NewContextAsync(browser, new BrowserNewContextOptions());
            var page = await context.NewPageAsync();

            await page.GotoAsync(new Uri(Path.Combine(granted, "page.html")).AbsoluteUri);
            Assert.Equal("granted", await page.TextContentAsync("h1"));

            var refused = await Assert.ThrowsAsync<PlaywrightException>(
                () => page.GotoAsync(new Uri(Path.Combine(denied, "page.html")).AbsoluteUri));

            Assert.Contains("ERR_ACCESS_DENIED", refused.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task A_confined_browser_connects_only_to_the_tcp_ports_it_was_granted()
    {
        if (Skipped || Interop.LandlockConfinement.AbiVersion() < 4)
        {
            return;
        }

        Assert.True(_browser.Unavailable is null, _browser.Unavailable);

        using var granted = LocalPage.Start("granted");
        using var denied = LocalPage.Start("denied");

        await using var session = await PlaywrightSession.CreateAsync(Options(new PlaywrightSandboxOptions
        {
            AllowedTcpPorts = [granted.Port],
        }));

        Assert.True(session.SandboxStatus.NetworkConfined, session.SandboxStatus.ToString());

        var browser = await session.GetBrowserAsync("chromium");
        var context = await session.NewContextAsync(browser, new BrowserNewContextOptions());
        var page = await context.NewPageAsync();

        await page.GotoAsync(granted.Url);
        Assert.Equal("granted", await page.TextContentAsync("h1"));

        await Assert.ThrowsAsync<PlaywrightException>(() => page.GotoAsync(denied.Url));
    }

    /// <summary>A one-page HTTP server on a free loopback port.</summary>
    private sealed class LocalPage : IDisposable
    {
        private readonly System.Net.HttpListener _listener = new();

        private LocalPage(int port, string heading)
        {
            Port = port;
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();

            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    System.Net.HttpListenerContext request;

                    try
                    {
                        request = await _listener.GetContextAsync();
                    }
                    catch (Exception exception) when (exception is System.Net.HttpListenerException or ObjectDisposedException)
                    {
                        return;
                    }

                    var body = Encoding.UTF8.GetBytes($"<h1>{heading}</h1>");
                    request.Response.ContentType = "text/html";
                    await request.Response.OutputStream.WriteAsync(body);
                    request.Response.Close();
                }
            });
        }

        public int Port { get; }

        public string Url => $"http://127.0.0.1:{Port}/";

        public static LocalPage Start(string heading)
        {
            var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            probe.Start();
            var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            return new LocalPage(port, heading);
        }

        public void Dispose() => _listener.Close();
    }

    [Fact]
    public async Task A_required_sandbox_reports_exactly_what_was_enforced()
    {
        if (Skipped)
        {
            return;
        }

        Assert.True(_browser.Unavailable is null, _browser.Unavailable);

        var options = Options(new PlaywrightSandboxOptions { Required = true });

        if (Interop.LandlockConfinement.Unavailable() is not null)
        {
            await Assert.ThrowsAsync<PlaywrightSandboxException>(() => PlaywrightSession.CreateAsync(options));
            return;
        }

        await using var session = await PlaywrightSession.CreateAsync(options);

        Assert.True(session.SandboxStatus.Requested);
        Assert.True(session.SandboxStatus.FileSystemConfined);
        Assert.False(session.SandboxStatus.NetworkConfined);
        Assert.True(session.SandboxStatus.AbiVersion >= 1);
    }

    [Fact]
    public async Task An_unsandboxed_session_says_none_was_requested()
    {
        if (Skipped)
        {
            return;
        }

        Assert.True(_browser.Unavailable is null, _browser.Unavailable);

        Assert.Same(PlaywrightSandboxStatus.NotRequested, _browser.Session!.SandboxStatus);
        await Task.CompletedTask;
    }

    [Fact]
    public void The_grants_name_the_driver_and_the_browsers_and_never_the_home_directory()
    {
        var (readOnly, readWrite) = Interop.LandlockConfinement.Grants(new PlaywrightSandboxOptions());
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (Interop.DriverLocation.DriverDirectory() is { } driver)
        {
            Assert.Contains(Path.GetFullPath(driver), readOnly);
        }

        Assert.DoesNotContain(Path.GetFullPath(home), readOnly);
        Assert.DoesNotContain(Path.GetFullPath(home), readWrite);
        Assert.DoesNotContain("/run", readOnly);
    }
}
