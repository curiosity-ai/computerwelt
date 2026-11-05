using System.Text;
using Computerwelt.Emulation.Bash;
using Xunit;

namespace Computerwelt.Playwright.Tests;

/// <summary>
/// A tenant whose scope keeps its contexts between runs: the page one script opened is the page the
/// next one finds, until a script closes it.
/// </summary>
[Collection(BrowserCollection.Name)]
public sealed class KeepContextsTests : IDisposable
{
    private readonly BrowserFixture _browser;
    private readonly string _root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        $".computerwelt-keep-{Guid.NewGuid():N}");

    public KeepContextsTests(BrowserFixture browser)
    {
        _browser = browser;
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static bool Skipped =>
        Environment.GetEnvironmentVariable("COMPUTERWELT_SKIP_PLAYWRIGHT") == "1";

    private string Profiles => Path.Combine(_root, "profiles");

    private static async Task<ExecResult> RunAsync(PlaywrightScope scope, string program)
    {
        var bash = Bash.CreateBuilder().WithWorkingDirectory("/").WithPlaywright(scope).Build();

        await bash.FileSystem.WriteFileAsync(VPath.Parse("/run.py"), Encoding.UTF8.GetBytes(program));
        return await bash.ExecAsync("python /run.py");
    }

    private const string OPEN = """
        from playwright.sync_api import sync_playwright

        with sync_playwright() as p:
            browser = p.chromium.launch()
            context = browser.contexts[0] if browser.contexts else browser.new_context()
            page = context.pages[0] if context.pages else context.new_page()
            page.set_content('<h1>hello</h1>')
            page.evaluate("window.marker = 'still here'")
            print(len(browser.contexts), len(context.pages))
        """;

    private const string LOOK = """
        from playwright.sync_api import sync_playwright

        with sync_playwright() as p:
            browser = p.chromium.launch()
            context = browser.contexts[0] if browser.contexts else browser.new_context()
            page = context.pages[0] if context.pages else context.new_page()
            print(page.evaluate("window.marker"), len(browser.contexts), len(context.pages))
        """;

    private const string CLOSE = """
        from playwright.sync_api import sync_playwright

        with sync_playwright() as p:
            browser = p.chromium.launch()
            print(len(browser.contexts))
            browser.close()
        """;

    [Fact]
    public async Task A_page_stays_open_for_the_next_run_until_a_script_closes_the_browser()
    {
        if (Skipped)
        {
            return;
        }

        Assert.True(_browser.Unavailable is null, _browser.Unavailable);

        await using var session = await PlaywrightSession.CreateAsync(BrowserFixture.Options with { TemporaryDirectory = Path.Combine(_root, "temp") });
        await using var scope = session.CreateScope(new PlaywrightScopeOptions { ProfileDirectory = Profiles, KeepContextsBetweenRuns = true });

        var first = await RunAsync(scope, OPEN);
        Assert.True(first.ExitCode == 0, first.Stderr.ToString());
        Assert.Equal("1 1\n", first.Stdout.ToString());
        Assert.Single(scope.Contexts);
        Assert.Equal(0, scope.ActiveRuns);

        var second = await RunAsync(scope, LOOK);
        Assert.True(second.ExitCode == 0, second.Stderr.ToString());
        Assert.Equal("still here 1 1\n", second.Stdout.ToString());
        Assert.Single(scope.Contexts);

        var third = await RunAsync(scope, CLOSE);
        Assert.True(third.ExitCode == 0, third.Stderr.ToString());
        Assert.Equal("1\n", third.Stdout.ToString());
        Assert.Empty(scope.Contexts);

        var fourth = await RunAsync(scope, LOOK);
        Assert.Equal("None 1 1\n", fourth.Stdout.ToString());
    }

    [Fact]
    public async Task A_scope_that_does_not_keep_its_contexts_closes_them_with_the_run()
    {
        if (Skipped)
        {
            return;
        }

        Assert.True(_browser.Unavailable is null, _browser.Unavailable);

        await using var session = await PlaywrightSession.CreateAsync(BrowserFixture.Options with { TemporaryDirectory = Path.Combine(_root, "temp") });
        await using var scope = session.CreateScope(new PlaywrightScopeOptions { ProfileDirectory = Profiles });

        Assert.Equal(0, (await RunAsync(scope, OPEN)).ExitCode);
        Assert.Empty(scope.Contexts);
    }
}
