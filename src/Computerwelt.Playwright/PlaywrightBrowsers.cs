using System.Diagnostics;
using System.Globalization;
using Computerwelt.Playwright.Interop;

namespace Computerwelt.Playwright;

/// <summary>What a browser installation attempt did.</summary>
/// <param name="ExitCode">Zero when the driver reported success.</param>
/// <param name="StandardOutput">Everything the driver printed.</param>
/// <param name="StandardError">Everything the driver reported as an error.</param>
public readonly record struct PlaywrightInstallResult(int ExitCode, string StandardOutput, string StandardError)
{
    /// <summary>True when the driver reported success.</summary>
    public bool Succeeded => ExitCode == 0;

    /// <summary>The driver's own words, for a host that wants to log or surface them.</summary>
    public override string ToString() =>
        Succeeded
            ? StandardOutput
            : string.Format(
                CultureInfo.InvariantCulture,
                "playwright install failed with exit code {0}.{1}{2}{3}",
                ExitCode,
                Environment.NewLine,
                StandardOutput,
                StandardError);
}

/// <summary>One browser build Playwright would install, and whether it is on disk.</summary>
/// <param name="Title">What the driver calls it — <c>Chrome for Testing 151.0.7922.34 (playwright chromium v1234)</c>.</param>
/// <param name="Directory">Where it is, or would be, installed.</param>
/// <param name="IsInstalled">Whether the driver finished installing it there.</param>
public readonly record struct PlaywrightBrowserInstallation(string Title, string Directory, bool IsInstalled);

/// <summary>
/// Puts the browsers Playwright drives onto the host machine.
/// </summary>
/// <remarks>
/// <para>
/// Playwright's .NET package ships a driver and a command line but no browsers; they are
/// downloaded once, per machine, by <c>playwright install</c>. That command is normally run
/// from a shell — which is exactly what a host embedding this library does not want to
/// arrange — so it is wrapped here as an ordinary method: the driver's entry point is called
/// in-process and its output is returned rather than printed.
/// </para>
/// <para>
/// This is host setup and nothing in the sandbox can reach it. Call it once at start-up,
/// before <see cref="PlaywrightSession.CreateAsync"/>, or leave it out entirely if the
/// machine is provisioned some other way — a container image with the browsers baked in, or
/// a <c>PLAYWRIGHT_BROWSERS_PATH</c> pointing at a shared directory.
/// </para>
/// </remarks>
public static class PlaywrightBrowsers
{
    /// <summary>
    /// Installs the browsers, and optionally their operating-system dependencies.
    /// </summary>
    /// <param name="browsers">
    /// Which browsers to fetch. Empty installs every browser Playwright knows, which is what
    /// the bare <c>playwright install</c> does and is usually more than a host needs.
    /// </param>
    /// <param name="withDependencies">
    /// Also install the system packages the browsers need. Requires root on Linux and is
    /// ignored elsewhere, so it is off by default.
    /// </param>
    /// <param name="cancellationToken">Abandons the wait; the driver itself is not interruptible.</param>
    /// <remarks>
    /// The work is a synchronous call into Playwright's own command line, so it is moved off
    /// the caller's thread rather than pretending to be asynchronous in place — a download
    /// of a few hundred megabytes is not something to run on a request thread.
    /// </remarks>
    public static Task<PlaywrightInstallResult> InstallAsync(
        IEnumerable<PlaywrightBrowser>? browsers = null,
        bool withDependencies = false,
        CancellationToken cancellationToken = default)
    {
        var arguments = new List<string> { "install" };

        if (withDependencies)
        {
            arguments.Add("--with-deps");
        }

        if (browsers is not null)
        {
            foreach (var browser in browsers)
            {
                arguments.Add(PlaywrightOptions.NameOf(browser));
            }
        }

        return Task.Run(() => Install([.. arguments]), cancellationToken);
    }

    /// <summary>
    /// Installs the browsers a set of options names, so that a session built from the same
    /// options can launch every one of them.
    /// </summary>
    public static Task<PlaywrightInstallResult> InstallAsync(
        PlaywrightOptions options,
        bool withDependencies = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        return InstallAsync(options.Browsers, withDependencies, cancellationToken);
    }

    /// <summary>
    /// Runs Playwright's command line in-process with <paramref name="arguments"/>.
    /// </summary>
    /// <remarks>
    /// The escape hatch for the parts of the tool this class does not wrap —
    /// <c>install-deps</c>, <c>uninstall</c>, <c>--dry-run</c>. It is the same entry point
    /// the <c>playwright</c> executable has.
    /// </remarks>
    public static PlaywrightInstallResult Install(params string[] arguments)
    {
        var result = new Microsoft.Playwright.Program().RunWithResult(arguments);

        return new PlaywrightInstallResult(result.ExitCode, result.StandardOutput, result.StandardError);
    }
    /// <summary>
    /// Installs the browsers, handing every line the driver prints to <paramref name="output"/> as
    /// it is printed.
    /// </summary>
    /// <remarks>
    /// The overload for a host that shows the download to somebody: a browser is a few hundred
    /// megabytes, and the driver reports its progress as it goes. Where the browsers go is the
    /// driver's own decision — <c>PLAYWRIGHT_BROWSERS_PATH</c> when the host process has it set,
    /// the per-user cache otherwise — and so is skipping a build that is already there, which
    /// makes this cheap to call again: an up-to-date machine downloads nothing.
    /// </remarks>
    /// <param name="browsers">Which browsers to fetch; null or empty fetches every default one.</param>
    /// <param name="output">Receives each line of the driver's output, standard error included.</param>
    /// <param name="withDependencies">Also install the system packages the browsers need (root only, Linux only).</param>
    /// <param name="cancellationToken">Stops the download, killing the driver.</param>
    public static async Task<PlaywrightInstallResult> InstallAsync(
        IEnumerable<PlaywrightBrowser>? browsers,
        Action<string>? output,
        bool withDependencies = false,
        CancellationToken cancellationToken = default)
    {
        var arguments = new List<string> { "install" };

        if (withDependencies)
        {
            arguments.Add("--with-deps");
        }

        if (browsers is not null)
        {
            arguments.AddRange(browsers.Select(PlaywrightOptions.NameOf));
        }

        return await RunDriverAsync(arguments, output, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// What the driver would install for <paramref name="browsers"/>, and which of it is on disk.
    /// </summary>
    /// <remarks>
    /// Asks the driver itself (<c>playwright install --dry-run</c>) rather than guessing at folder
    /// names, because the build a browser resolves to is decided by the version of the driver this
    /// process carries: after an upgrade the folder from the previous build is still there and is
    /// not the one that will be launched.
    /// </remarks>
    public static async Task<IReadOnlyList<PlaywrightBrowserInstallation>> GetInstallationsAsync(
        IEnumerable<PlaywrightBrowser>? browsers = null,
        CancellationToken cancellationToken = default)
    {
        var arguments = new List<string> { "install", "--dry-run" };

        if (browsers is not null)
        {
            arguments.AddRange(browsers.Select(PlaywrightOptions.NameOf));
        }

        var lines = new List<string>();
        var result = await RunDriverAsync(arguments, line => lines.Add(line), cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new InvalidOperationException(result.ToString());
        }

        return ParseDryRun(lines);
    }

    /// <summary>Whether every browser build <paramref name="browsers"/> needs is on disk.</summary>
    public static async Task<bool> IsInstalledAsync(
        IEnumerable<PlaywrightBrowser>? browsers = null,
        CancellationToken cancellationToken = default)
    {
        var installations = await GetInstallationsAsync(browsers, cancellationToken).ConfigureAwait(false);

        // FFmpeg rides along with Chromium for video recording, which this sandbox refuses, so a
        // machine without it can still launch everything a script is allowed to use.
        return installations.Count > 0
            && installations.Where(static i => !i.Title.StartsWith("FFmpeg", StringComparison.Ordinal)).All(static i => i.IsInstalled);
    }

    internal static IReadOnlyList<PlaywrightBrowserInstallation> ParseDryRun(IEnumerable<string> lines)
    {
        var installations = new List<PlaywrightBrowserInstallation>();
        string? title = null;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();

            if (line.Length == 0)
            {
                continue;
            }

            if (!char.IsWhiteSpace(line[0]))
            {
                title = line.Trim();
                continue;
            }

            const string Marker = "Install location:";
            var trimmed = line.Trim();

            if (title is not null && trimmed.StartsWith(Marker, StringComparison.Ordinal))
            {
                var directory = trimmed[Marker.Length..].Trim();
                var installed = System.IO.Directory.Exists(directory)
                    && File.Exists(Path.Combine(directory, "INSTALLATION_COMPLETE"));

                installations.Add(new PlaywrightBrowserInstallation(title, directory, installed));
                title = null;
            }
        }

        return installations;
    }

    private static async Task<PlaywrightInstallResult> RunDriverAsync(
        IReadOnlyList<string> arguments,
        Action<string>? output,
        CancellationToken cancellationToken)
    {
        var node = DriverLocation.NodeExecutable();
        var cli = DriverLocation.CliEntryPoint();

        if (node is null || cli is null || !File.Exists(cli))
        {
            return new PlaywrightInstallResult(
                1,
                string.Empty,
                "the Playwright driver was not found next to Microsoft.Playwright.dll; build the host project so its .playwright folder is copied to the output");
        }

        var startInfo = new ProcessStartInfo(node)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add(cli);

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // What the .NET package sets for its own driver runs, so the driver reports the same
        // language and version in its messages.
        startInfo.Environment["PW_LANG_NAME"] = "csharp";
        startInfo.Environment["PW_LANG_NAME_VERSION"] = $"{Environment.Version.Major}.{Environment.Version.Minor}";
        startInfo.Environment["PW_CLI_DISPLAY_VERSION"] = typeof(Microsoft.Playwright.IPlaywright).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        using var process = new Process { StartInfo = startInfo };
        var stdout = new System.Text.StringBuilder();
        var stderr = new System.Text.StringBuilder();
        var gate = new object();

        process.OutputDataReceived += (_, e) => Receive(e.Data, stdout);
        process.ErrorDataReceived += (_, e) => Receive(e.Data, stderr);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // It ended on its own between the cancellation and the kill.
            }

            throw;
        }

        // WaitForExitAsync returns once the process ends; the last lines may still be in flight.
        process.WaitForExit();

        return new PlaywrightInstallResult(process.ExitCode, stdout.ToString(), stderr.ToString());

        void Receive(string? line, System.Text.StringBuilder into)
        {
            if (line is null)
            {
                return;
            }

            lock (gate)
            {
                into.AppendLine(line);
                output?.Invoke(line);
            }
        }
    }
}
