using System.Runtime.InteropServices;

namespace Computerwelt.Playwright.Interop;

/// <summary>
/// Where the Playwright driver and its browsers live on the host.
/// </summary>
/// <remarks>
/// <para>
/// The .NET package keeps the same knowledge internal, so it is restated here, following the
/// package's own lookup: <c>PLAYWRIGHT_DRIVER_SEARCH_PATH</c>, then the application directory,
/// then the directory <c>Microsoft.Playwright.dll</c> was loaded from. Two things need it: the
/// sandbox, which has to grant the driver read and execute access to exactly these directories,
/// and the installer, which starts the driver itself so it can stream its output.
/// </para>
/// </remarks>
internal static class DriverLocation
{
    /// <summary>The directory holding <c>.playwright/node</c> and <c>.playwright/package</c>, or null when none was found.</summary>
    public static string? DriverDirectory()
    {
        var searchPath = Environment.GetEnvironmentVariable("PLAYWRIGHT_DRIVER_SEARCH_PATH");

        if (!string.IsNullOrEmpty(searchPath))
        {
            return HasDriver(searchPath) ? Path.Combine(searchPath, ".playwright") : null;
        }

        foreach (var candidate in Candidates())
        {
            if (HasDriver(candidate))
            {
                return Path.Combine(candidate, ".playwright");
            }
        }

        return null;
    }

    /// <summary>The node executable the driver runs on, or null when no driver was found.</summary>
    public static string? NodeExecutable()
    {
        var explicitPath = Environment.GetEnvironmentVariable("PLAYWRIGHT_NODEJS_PATH");

        if (!string.IsNullOrEmpty(explicitPath))
        {
            return explicitPath;
        }

        return DriverDirectory() is { } driver ? Path.Combine(driver, "node", PlatformId(), NodeName()) : null;
    }

    /// <summary>The driver's command-line entry point, or null when no driver was found.</summary>
    public static string? CliEntryPoint() =>
        DriverDirectory() is { } driver ? Path.Combine(driver, "package", "cli.js") : null;

    /// <summary>
    /// Where the browsers are installed: <c>PLAYWRIGHT_BROWSERS_PATH</c>, or the per-user cache
    /// Playwright falls back to.
    /// </summary>
    public static string BrowsersDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH");

        // "0" asks Playwright to keep the browsers beside the driver package.
        if (string.Equals(configured, "0", StringComparison.Ordinal) && DriverDirectory() is { } driver)
        {
            return Path.Combine(driver, "package", ".local-browsers");
        }

        if (!string.IsNullOrEmpty(configured))
        {
            return Path.GetFullPath(configured);
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ms-playwright");
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(home, "Library", "Caches", "ms-playwright");
        }

        var cache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");

        return Path.Combine(string.IsNullOrEmpty(cache) ? Path.Combine(home, ".cache") : cache, "ms-playwright");
    }

    private static IEnumerable<string> Candidates()
    {
        if (!string.IsNullOrEmpty(AppContext.BaseDirectory))
        {
            yield return AppContext.BaseDirectory;
        }

        var location = typeof(Microsoft.Playwright.IPlaywright).Assembly.Location;

        if (!string.IsNullOrEmpty(location) && Path.GetDirectoryName(location) is { } directory)
        {
            yield return directory;

            // Run straight out of the NuGet cache: lib/<tfm>/ sits two levels under the package.
            if (Directory.GetParent(directory)?.Parent is { } package)
            {
                yield return package.FullName;
            }
        }
    }

    private static bool HasDriver(string directory) =>
        File.Exists(Path.Combine(directory, ".playwright", "package", "cli.js"));

    private static string NodeName() => OperatingSystem.IsWindows() ? "node.exe" : "node";

    private static string PlatformId()
    {
        var arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

        if (OperatingSystem.IsWindows())
        {
            return "win32_x64";
        }

        if (OperatingSystem.IsMacOS())
        {
            return arm ? "darwin-arm64" : "darwin-x64";
        }

        return arm ? "linux-arm64" : "linux-x64";
    }
}
