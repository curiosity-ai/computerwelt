using System.Text;
using Computerwelt.Emulation.Bash;
using Microsoft.Playwright;
using Xunit;

namespace Computerwelt.Playwright.Tests;

/// <summary>
/// Where a browser keeps what it writes: a tenant's profile folder, and the session's temporary
/// directory.
/// </summary>
/// <remarks>
/// Each test starts a session of its own, because the temporary directory is decided when the
/// driver starts. They work below the home directory, which neither the default sandbox grants
/// nor the host's temporary directory overlaps, so a confined browser can only have written
/// where the test said it could.
/// </remarks>
[Collection(BrowserCollection.Name)]
public sealed class ProfileTests : IDisposable
{
    private readonly BrowserFixture _browser;
    private readonly string _root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        $".computerwelt-profiles-{Guid.NewGuid():N}");

    public ProfileTests(BrowserFixture browser)
    {
        _browser = browser;
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static bool Skipped =>
        Environment.GetEnvironmentVariable("COMPUTERWELT_SKIP_PLAYWRIGHT") == "1";

    private string Temporary => Path.Combine(_root, "temp");

    private string Profiles => Path.Combine(_root, "profiles");

    private PlaywrightOptions Options(LocalPage page, PlaywrightSandboxOptions? sandbox = null) => BrowserFixture.Options with
    {
        AllowedHosts = [$"127.0.0.1:{page.Port}"],
        TemporaryDirectory = Temporary,
        Sandbox = sandbox,
    };

    private static async Task<ExecResult> RunAsync(PlaywrightScope scope, string program)
    {
        var bash = Bash.CreateBuilder().WithWorkingDirectory("/").WithPlaywright(scope).Build();

        await bash.FileSystem.WriteFileAsync(VPath.Parse("/run.py"), Encoding.UTF8.GetBytes(program));
        return await bash.ExecAsync("python /run.py");
    }

    private static string Remember(LocalPage page) => $$"""
        from playwright.sync_api import sync_playwright

        with sync_playwright() as p:
            page = p.chromium.launch().new_page()
            page.goto('{{page.Url}}')
            page.evaluate("localStorage.setItem('remembered', 'yes')")
        """;

    private static string Recall(LocalPage page) => $$"""
        from playwright.sync_api import sync_playwright

        with sync_playwright() as p:
            page = p.chromium.launch().new_page()
            page.goto('{{page.Url}}')
            print(page.evaluate("localStorage.getItem('remembered')"))
        """;

    [Fact]
    public async Task A_tenant_with_a_profile_directory_keeps_its_storage_from_one_run_to_the_next()
    {
        if (Skipped)
        {
            return;
        }

        Assert.True(_browser.Unavailable is null, _browser.Unavailable);

        using var page = LocalPage.Start("profile");
        await using var session = await PlaywrightSession.CreateAsync(Options(page));
        await using var scope = session.CreateScope(new PlaywrightScopeOptions { ProfileDirectory = Profiles });

        var first = await RunAsync(scope, Remember(page));
        Assert.True(first.ExitCode == 0, first.Stderr.ToString());
        Assert.Empty(scope.Contexts);

        var second = await RunAsync(scope, Recall(page));
        Assert.True(second.ExitCode == 0, second.Stderr.ToString());
        Assert.Equal("yes\n", second.Stdout.ToString());

        Assert.True(Directory.Exists(Path.Combine(Profiles, "chromium-1")));
        Assert.False(Directory.Exists(Path.Combine(Profiles, "chromium-2")));
    }

    [Fact]
    public async Task A_tenant_without_one_starts_every_run_from_a_blank_profile()
    {
        if (Skipped)
        {
            return;
        }

        Assert.True(_browser.Unavailable is null, _browser.Unavailable);

        using var page = LocalPage.Start("blank");
        await using var session = await PlaywrightSession.CreateAsync(Options(page));
        await using var scope = session.CreateScope();

        Assert.Equal(0, (await RunAsync(scope, Remember(page))).ExitCode);

        var second = await RunAsync(scope, Recall(page));
        Assert.Equal("None\n", second.Stdout.ToString());
    }

    [Fact]
    public async Task Two_contexts_open_at_once_get_a_profile_each_and_a_page_opens_none_on_its_own()
    {
        if (Skipped)
        {
            return;
        }

        Assert.True(_browser.Unavailable is null, _browser.Unavailable);

        using var page = LocalPage.Start("two");
        await using var session = await PlaywrightSession.CreateAsync(Options(page));
        await using var scope = session.CreateScope(new PlaywrightScopeOptions { ProfileDirectory = Profiles });

        var first = await scope.NewContextAsync("chromium", new BrowserNewContextOptions());
        var second = await scope.NewContextAsync("chromium", new BrowserNewContextOptions());

        Assert.Empty(scope.Pages);
        Assert.Equal(2, session.OpenContexts);
        Assert.True(Directory.Exists(Path.Combine(Profiles, "chromium-1")));
        Assert.True(Directory.Exists(Path.Combine(Profiles, "chromium-2")));

        await first.CloseAsync();
        Assert.Single(scope.Contexts);

        // The folder the closed context freed is the one the next context takes.
        var third = await scope.NewContextAsync("chromium", new BrowserNewContextOptions());
        Assert.Equal(2, scope.Contexts.Count);
        Assert.False(Directory.Exists(Path.Combine(Profiles, "chromium-3")));

        await second.CloseAsync();
        await third.CloseAsync();
        Assert.Equal(0, session.OpenContexts);
    }

    [Fact]
    public async Task A_profile_cannot_be_replaced_with_a_storage_state()
    {
        if (Skipped)
        {
            return;
        }

        Assert.True(_browser.Unavailable is null, _browser.Unavailable);

        using var page = LocalPage.Start("state");
        await using var session = await PlaywrightSession.CreateAsync(Options(page));
        await using var scope = session.CreateScope(new PlaywrightScopeOptions { ProfileDirectory = Profiles });

        var result = await RunAsync(scope, """
            from playwright.sync_api import sync_playwright, Error

            with sync_playwright() as p:
                try:
                    p.chromium.launch().new_context(storage_state={'cookies': [], 'origins': []})
                except Error as e:
                    print(e)
            """);

        Assert.True(result.ExitCode == 0, result.Stderr.ToString());
        Assert.Contains("keeps a profile of its own", result.Stdout.ToString(), StringComparison.Ordinal);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => scope.NewContextAsync("chromium", new BrowserNewContextOptions { StorageState = "{}" }));
        Assert.Equal(0, session.OpenContexts);
    }

    [Fact]
    public async Task The_shared_browser_keeps_its_temporary_profile_in_the_sessions_temporary_directory()
    {
        if (Skipped)
        {
            return;
        }

        Assert.True(_browser.Unavailable is null, _browser.Unavailable);

        using var page = LocalPage.Start("temp");
        await using var session = await PlaywrightSession.CreateAsync(Options(page));

        Assert.Equal(Temporary, session.TemporaryDirectory);

        var browser = await session.GetBrowserAsync("chromium");
        Assert.True(browser.IsConnected);

        // Playwright launches a browser with no profile of its own over a throw-away one, which it
        // creates in the driver's TMPDIR.
        Assert.Contains(
            Directory.GetDirectories(Temporary),
            directory => Path.GetFileName(directory).StartsWith("playwright_chromiumdev_profile-", StringComparison.Ordinal));

        // Handed to the driver, not set on the host.
        Assert.NotEqual(Temporary, Environment.GetEnvironmentVariable("TMPDIR"));
    }

    [Fact]
    public async Task A_confined_browser_writes_its_profiles_and_temporary_files_where_the_host_said_and_nowhere_else()
    {
        if (Skipped || Interop.LandlockConfinement.Unavailable() is not null)
        {
            return;
        }

        Assert.True(_browser.Unavailable is null, _browser.Unavailable);

        Directory.CreateDirectory(Profiles);

        using var page = LocalPage.Start("confined");
        await using var session = await PlaywrightSession.CreateAsync(Options(page, new PlaywrightSandboxOptions
        {
            Required = true,
            ReadWritePaths = [Profiles],
        }));

        Assert.True(session.SandboxStatus.FileSystemConfined, session.SandboxStatus.ToString());
        Assert.Null(session.SandboxStatus.Reason);

        // The host's temporary directory is no longer granted, so it is not where the profile could go.
        Assert.DoesNotContain(Path.GetFullPath(Path.GetTempPath()).TrimEnd('/'), session.WritablePaths);

        var outside = Path.Combine(_root, "elsewhere");
        Assert.Throws<ArgumentException>(() => session.CreateScope(new PlaywrightScopeOptions { ProfileDirectory = outside }));

        await using var scope = session.CreateScope(new PlaywrightScopeOptions { ProfileDirectory = Path.Combine(Profiles, "chat") });

        Assert.Equal(0, (await RunAsync(scope, Remember(page))).ExitCode);

        var second = await RunAsync(scope, Recall(page));
        Assert.True(second.ExitCode == 0, second.Stderr.ToString());
        Assert.Equal("yes\n", second.Stdout.ToString());
        Assert.True(Directory.Exists(Path.Combine(Profiles, "chat", "chromium-1")));
    }
}
