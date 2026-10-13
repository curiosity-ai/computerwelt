using System.Text;
using Computerwelt.Emulation.Bash;
using Xunit;

namespace Computerwelt.Playwright.Tests;

/// <summary>
/// The two viewport settings: <see cref="PlaywrightOptions.DefaultViewport"/>, which a script may
/// override, and <see cref="PlaywrightOptions.Viewport"/>, which it may not.
/// </summary>
[Collection(BrowserCollection.Name)]
public sealed class ViewportTests : IDisposable
{
    private readonly BrowserFixture _browser;
    private readonly string _profiles = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        $".computerwelt-viewports-{Guid.NewGuid():N}");

    public ViewportTests(BrowserFixture browser) => _browser = browser;

    public void Dispose()
    {
        if (Directory.Exists(_profiles))
        {
            Directory.Delete(_profiles, recursive: true);
        }
    }

    private static bool Skipped =>
        Environment.GetEnvironmentVariable("COMPUTERWELT_SKIP_PLAYWRIGHT") == "1";

    private static async Task<string> SizesAsync(PlaywrightScope scope)
    {
        var bash = Bash.CreateBuilder().WithWorkingDirectory("/").WithPlaywright(scope).Build();

        await bash.FileSystem.WriteFileAsync(VPath.Parse("/run.py"), Encoding.UTF8.GetBytes("""
            from playwright.sync_api import sync_playwright

            with sync_playwright() as p:
                browser = p.chromium.launch()
                for page in [browser.new_page(), browser.new_page(viewport={'width': 800, 'height': 600})]:
                    size = page.viewport_size
                    print(f"{size['width']}x{size['height']}")
                page.set_viewport_size({'width': 1024, 'height': 768})
                print(page.evaluate("innerWidth + 'x' + innerHeight"))
            """));

        var result = await bash.ExecAsync("python /run.py");
        Assert.True(result.ExitCode == 0, result.Stderr.ToString());
        return result.Stdout.ToString();
    }

    [Fact]
    public async Task A_default_viewport_applies_only_where_the_script_chose_none()
    {
        if (Skipped)
        {
            return;
        }

        Assert.True(_browser.Unavailable is null, _browser.Unavailable);

        await using var session = await PlaywrightSession.CreateAsync(BrowserFixture.Options with { DefaultViewport = (1440, 900) });
        await using var scope = session.CreateScope();

        Assert.Equal("1440x900\n800x600\n1024x768\n", await SizesAsync(scope));
    }

    [Fact]
    public async Task A_pinned_viewport_wins_over_the_script_and_over_the_default()
    {
        if (Skipped)
        {
            return;
        }

        Assert.True(_browser.Unavailable is null, _browser.Unavailable);

        await using var session = await PlaywrightSession.CreateAsync(BrowserFixture.Options with { Viewport = (1200, 700), DefaultViewport = (1440, 900) });
        await using var scope = session.CreateScope();

        // A context is pinned; resizing a page afterwards is a page-level call and still works.
        Assert.Equal("1200x700\n1200x700\n1024x768\n", await SizesAsync(scope));
    }

    [Fact]
    public async Task A_context_that_keeps_a_profile_gets_the_default_too()
    {
        if (Skipped)
        {
            return;
        }

        Assert.True(_browser.Unavailable is null, _browser.Unavailable);

        await using var session = await PlaywrightSession.CreateAsync(BrowserFixture.Options with { DefaultViewport = (1440, 900) });
        await using var scope = session.CreateScope(new PlaywrightScopeOptions { ProfileDirectory = _profiles });

        Assert.Equal("1440x900\n800x600\n1024x768\n", await SizesAsync(scope));
    }
}
