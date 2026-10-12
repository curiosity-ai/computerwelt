using System.Text;
using Computerwelt.Emulation.Bash;
using Xunit;

namespace Computerwelt.Playwright.Tests;

/// <summary>
/// One tenant's share of a shared session: its own host list, its own cap, the host's view of
/// what it has open, and the resolver that lets a sandbox built once find a browser later.
/// </summary>
[Collection(BrowserCollection.Name)]
public sealed class ScopeTests
{
    private readonly BrowserFixture _browser;

    public ScopeTests(BrowserFixture browser) => _browser = browser;

    private static bool Skipped =>
        Environment.GetEnvironmentVariable("COMPUTERWELT_SKIP_PLAYWRIGHT") == "1";

    private static async Task<ExecResult> RunAsync(Bash bash, string program)
    {
        await bash.FileSystem.WriteFileAsync(VPath.Parse("/run.py"), Encoding.UTF8.GetBytes(program));
        return await bash.ExecAsync("python /run.py");
    }

    private PlaywrightSession Session()
    {
        Assert.True(_browser.Unavailable is null, _browser.Unavailable);
        return _browser.Session!;
    }

    [Fact]
    public async Task A_scopes_host_list_replaces_the_sessions_for_that_tenant_only()
    {
        if (Skipped)
        {
            return;
        }

        var session = Session();

        await using var scope = session.CreateScope(new PlaywrightScopeOptions { AllowedHosts = ["*.example.com"] });

        Assert.True(scope.Policy.IsAllowed("https://docs.example.com/"));
        Assert.False(session.Policy.IsAllowed("https://docs.example.com/"));
        Assert.False(scope.Policy.IsAllowed("https://example.org/"));
    }

    [Fact]
    public async Task A_scope_with_no_host_list_uses_the_sessions()
    {
        if (Skipped)
        {
            return;
        }

        await using var scope = Session().CreateScope();

        Assert.Same(scope.Session.Policy, scope.Policy);
    }

    [Fact]
    public async Task A_shell_registered_with_a_scope_runs_its_contexts_through_it()
    {
        if (Skipped)
        {
            return;
        }

        await using var scope = Session().CreateScope(new PlaywrightScopeOptions
        {
            // The host's say over the tenant's profile, which the script cannot skip.
            ConfigureContext = (context, _) => context.AddInitScriptAsync("window.__host = 'configured';"),
        });

        var opened = 0;
        var pagesWhileOpen = -1;

        scope.ContextOpened += (_, context) =>
        {
            opened++;
            context.Page += (_, _) => pagesWhileOpen = scope.Pages.Count;
        };

        var bash = Bash.CreateBuilder().WithWorkingDirectory("/").WithPlaywright(scope).Build();

        var result = await RunAsync(bash, """
            from playwright.sync_api import sync_playwright

            with sync_playwright() as p:
                page = p.chromium.launch().new_page()
                page.set_content('<p>hi</p>')
                print(page.evaluate('window.__host'))
            """);

        Assert.True(result.ExitCode == 0, result.Stderr.ToString());
        Assert.Equal("configured\n", result.Stdout.ToString());
        Assert.Equal(1, opened);
        Assert.Equal(1, pagesWhileOpen);

        // The run ended and gave its context back, through the scope.
        Assert.Empty(scope.Contexts);
        Assert.Empty(scope.Pages);
    }

    [Fact]
    public async Task A_scopes_own_cap_is_enforced_below_the_sessions()
    {
        if (Skipped)
        {
            return;
        }

        await using var scope = Session().CreateScope(new PlaywrightScopeOptions { MaxContexts = 1 });

        var bash = Bash.CreateBuilder().WithWorkingDirectory("/").WithPlaywright(scope).Build();

        var result = await RunAsync(bash, """
            from playwright.sync_api import sync_playwright, Error

            with sync_playwright() as p:
                browser = p.chromium.launch()
                browser.new_context()
                try:
                    browser.new_context()
                    print('opened two')
                except Error as e:
                    print('refused' if 'MaxContexts' in str(e) else str(e))
            """);

        Assert.True(result.ExitCode == 0, result.Stderr.ToString());
        Assert.Equal("refused\n", result.Stdout.ToString());
    }

    [Fact]
    public async Task Disposing_a_scope_closes_what_it_left_open_and_leaves_the_browser_running()
    {
        if (Skipped)
        {
            return;
        }

        var session = Session();
        var scope = session.CreateScope();
        var browser = await session.GetBrowserAsync("chromium");
        var context = await scope.NewContextAsync(browser, new Microsoft.Playwright.BrowserNewContextOptions());

        await context.NewPageAsync();
        Assert.Single(scope.Pages);

        await scope.DisposeAsync();

        Assert.True(scope.IsDisposed);
        Assert.Empty(scope.Contexts);
        Assert.DoesNotContain(scope, session.Scopes);
        Assert.True(browser.IsConnected);
    }

    [Fact]
    public async Task A_resolver_that_refuses_becomes_an_import_error_with_its_reason()
    {
        if (Skipped)
        {
            return;
        }

        var bash = Bash.CreateBuilder()
            .WithWorkingDirectory("/")
            .WithPlaywright(() => throw new PlaywrightUnavailableException("the browser is switched off in this workspace"))
            .Build();

        var result = await RunAsync(bash, """
            try:
                from playwright.sync_api import sync_playwright
                print('imported')
            except ImportError as e:
                print(str(e))
            """);

        Assert.True(result.ExitCode == 0, result.Stderr.ToString());
        Assert.Equal("playwright is not available: the browser is switched off in this workspace\n", result.Stdout.ToString());
    }

    [Fact]
    public async Task A_resolver_is_asked_on_each_run_so_a_shell_built_before_the_browser_finds_it()
    {
        if (Skipped)
        {
            return;
        }

        PlaywrightScope? current = null;

        var bash = Bash.CreateBuilder()
            .WithWorkingDirectory("/")
            .WithPlaywright(() => current ?? throw new PlaywrightUnavailableException("not yet"))
            .Build();

        const string Program = """
            try:
                from playwright.sync_api import sync_playwright
                with sync_playwright() as p:
                    page = p.chromium.launch().new_page()
                    page.set_content('<h1>ok</h1>')
                    print(page.text_content('h1'))
            except ImportError as e:
                print(str(e))
            """;

        var before = await RunAsync(bash, Program);
        Assert.Equal("playwright is not available: not yet\n", before.Stdout.ToString());

        await using var scope = Session().CreateScope();
        current = scope;

        var after = await RunAsync(bash, Program);
        Assert.True(after.ExitCode == 0, after.Stderr.ToString());
        Assert.Equal("ok\n", after.Stdout.ToString());
    }

    [Fact]
    public async Task The_installer_reports_the_browser_the_fixture_launched_as_installed()
    {
        if (Skipped)
        {
            return;
        }

        Session();

        var installations = await PlaywrightBrowsers.GetInstallationsAsync([PlaywrightBrowser.Chromium]);

        Assert.Contains(installations, i => i.Title.StartsWith("Chrome", StringComparison.Ordinal) && i.IsInstalled);
        Assert.True(await PlaywrightBrowsers.IsInstalledAsync([PlaywrightBrowser.Chromium]));
    }

    [Fact]
    public void A_dry_run_is_read_into_one_entry_per_build()
    {
        var installations = PlaywrightBrowsers.ParseDryRun(
        [
            "Chrome for Testing 151.0.7922.34 (playwright chromium v1234)",
            "  Install location:    /nowhere/chromium-1234",
            "  Download url:        https://cdn.playwright.dev/builds/cft/151.0.7922.34/linux64/chrome-linux64.zip",
            "",
            "FFmpeg (playwright ffmpeg v1011)",
            "  Install location:    /nowhere/ffmpeg-1011",
            "  Download url:        https://cdn.playwright.dev/builds/ffmpeg/1011/ffmpeg-linux.zip",
        ]);

        Assert.Equal(2, installations.Count);
        Assert.Equal("/nowhere/chromium-1234", installations[0].Directory);
        Assert.StartsWith("FFmpeg", installations[1].Title, StringComparison.Ordinal);
        Assert.All(installations, i => Assert.False(i.IsInstalled));
    }
}
