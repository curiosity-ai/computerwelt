using System.Runtime.InteropServices;
using FS = Sandbox.Landlock.FileSystem;
using Kernel = Sandbox.Landlock;
using Net = Sandbox.Landlock.Network;

namespace Computerwelt.Playwright.Interop;

/// <summary>
/// Starts something on a thread confined by a Landlock ruleset, so the processes it starts
/// inherit the ruleset and the host process does not.
/// </summary>
/// <remarks>
/// <para>
/// Landlock is irrevocable and per thread: once <c>landlock_restrict_self</c> returns, the
/// calling thread and every process it forks afterwards are confined for life. So the ruleset is
/// enforced on a thread created for the purpose, the start runs there, and the thread is then
/// allowed to end. A start that returns a task keeps running on the thread pool afterwards —
/// only what it forked from the confined thread is confined, which is exactly the driver.
/// </para>
/// <para>
/// The thread itself also runs the start's synchronous part — resolving the driver path, loading
/// whatever assembly that touches — so the application and runtime directories are readable to
/// it. That is a read grant for the browser as well, and an acceptable one: they hold code, not
/// data.
/// </para>
/// </remarks>
internal static class LandlockConfinement
{
    private static readonly FS[] ReadOnly = [FS.READ_FILE, FS.READ_DIR, FS.EXECUTE];

    // A device is read and written in place; nothing is created under /dev outside /dev/shm.
    private static readonly FS[] ReadWriteInPlace = [FS.READ_FILE, FS.READ_DIR, FS.WRITE_FILE];

    private static readonly FS[] ReadWrite =
    [
        FS.READ_FILE, FS.READ_DIR, FS.EXECUTE,
        FS.WRITE_FILE, FS.TRUNCATE, FS.MAKE_REG, FS.MAKE_DIR, FS.MAKE_SYM,
        FS.REMOVE_FILE, FS.REMOVE_DIR, FS.REFER,
    ];

    // Every right a rule grants has to be one the ruleset handles, or the kernel refuses the rule.
    // FileSystem.CORE does not handle sockets, FIFOs or device ioctls, so those are left out of the
    // grants above - and are therefore not restricted at all.

    /// <summary>The locations a dynamically linked browser loads its libraries, fonts and certificates from.</summary>
    internal static readonly string[] SystemPaths =
    [
        "/usr", "/bin", "/sbin", "/lib", "/lib32", "/lib64", "/libx32", "/etc", "/opt",
        "/proc", "/sys", "/var/lib/fontconfig", "/var/cache/fontconfig", "/nix/store",
    ];

    // Not /run: container runtimes mount secrets under /run/secrets. And /proc is safe to read
    // because Landlock also scopes ptrace - a confined browser cannot open the host process's
    // /proc/<pid>/environ or /proc/<pid>/mem, which are guarded by the same access check.

    /// <summary>The kernel's Landlock ABI version, or zero when it has none.</summary>
    public static int AbiVersion()
    {
        if (!OperatingSystem.IsLinux())
        {
            return 0;
        }

        try
        {
            return Math.Max(0, Kernel.GetAbiVersion());
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return 0;
        }
    }

    /// <summary>Why Landlock cannot be used here, or null when it can.</summary>
    public static string? Unavailable()
    {
        if (!OperatingSystem.IsLinux())
        {
            return $"Landlock is a Linux feature and this host runs {RuntimeInformation.OSDescription}";
        }

        if (RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
        {
            return $"Landlock is not supported on {RuntimeInformation.ProcessArchitecture}";
        }

        return AbiVersion() > 0 ? null : "this kernel has no Landlock support (it needs 5.13 or later, with landlock among the enabled LSMs)";
    }

    /// <summary>
    /// The paths a ruleset built from <paramref name="options"/> grants, split by access, with the
    /// ones that do not exist dropped.
    /// </summary>
    public static (IReadOnlyList<string> ReadOnly, IReadOnlyList<string> ReadWrite) Grants(PlaywrightSandboxOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var readOnly = new List<string>();
        var readWrite = new List<string>();

        if (options.IncludeSystemPaths)
        {
            readOnly.AddRange(SystemPaths);
        }

        // What the confined thread and the driver are run from.
        readOnly.Add(AppContext.BaseDirectory);
        readOnly.Add(RuntimeEnvironment.GetRuntimeDirectory());

        if (DriverLocation.DriverDirectory() is { } driver)
        {
            readOnly.Add(driver);
        }

        if (DriverLocation.NodeExecutable() is { } node)
        {
            readOnly.Add(node);
        }

        readOnly.Add(DriverLocation.BrowsersDirectory());
        readOnly.AddRange(options.ReadOnlyPaths);

        if (options.AllowTemporaryDirectory)
        {
            readWrite.Add(Path.GetTempPath());
        }

        // Chromium's shared memory, which it creates and removes as files.
        readWrite.Add("/dev/shm");
        readWrite.AddRange(options.ReadWritePaths);

        return (Existing(readOnly), Existing(readWrite));
    }

    /// <summary>
    /// Runs <paramref name="start"/> on a thread confined by the ruleset <paramref name="options"/>
    /// describes, and reports what was enforced.
    /// </summary>
    /// <exception cref="PlaywrightSandboxException">
    /// The ruleset could not be enforced and <see cref="PlaywrightSandboxOptions.Required"/> is set.
    /// Without it the start runs unconfined instead, and the status says why.
    /// </exception>
    public static async Task<(T Result, PlaywrightSandboxStatus Status)> RunAsync<T>(
        PlaywrightSandboxOptions options,
        Func<T> start)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(start);

        if (Unavailable() is { } reason)
        {
            return Unconfined(options, start, reason);
        }

        var abi = AbiVersion();
        var confineNetwork = options.AllowedTcpPorts is not null && abi >= 4;
        var completion = new TaskCompletionSource<(T, PlaywrightSandboxStatus)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (readOnly, readWrite) = Grants(options);

        var thread = new Thread(() =>
        {
            Kernel ruleset;

            try
            {
                ruleset = Build(readOnly, readWrite, options.AllowedTcpPorts, confineNetwork);
                ruleset.Enforce(disableDenyLogging: true, enableChildDenyLogging: false, disabledNestedDomainsLogging: true);
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
            {
                // Nothing is confined yet, so falling back is still possible - this thread is
                // not restricted and the start may run on it as it would anywhere else.
                try
                {
                    completion.TrySetResult(Unconfined(options, start, $"the Landlock ruleset could not be enforced: {exception.Message}"));
                }
                catch (Exception failure)
                {
                    completion.TrySetException(failure);
                }

                return;
            }

            try
            {
                var networkNote = options.AllowedTcpPorts is not null && !confineNetwork
                    ? $"AllowedTcpPorts needs Landlock ABI 4 and this kernel has {abi}, so TCP is not confined"
                    : null;

                completion.TrySetResult((start(), new PlaywrightSandboxStatus(true, true, confineNetwork, abi, networkNote)));
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "Computerwelt.Playwright landlock",
        };

        thread.Start();

        return await completion.Task.ConfigureAwait(false);
    }

    private static (T, PlaywrightSandboxStatus) Unconfined<T>(PlaywrightSandboxOptions options, Func<T> start, string reason)
    {
        if (options.Required)
        {
            throw new PlaywrightSandboxException(
                $"the browser sandbox is required (PlaywrightSandboxOptions.Required) but {reason}.");
        }

        return (start(), new PlaywrightSandboxStatus(true, false, false, AbiVersion(), reason));
    }

    private static Kernel Build(IReadOnlyList<string> readOnly, IReadOnlyList<string> readWrite, IReadOnlyList<int>? ports, bool confineNetwork)
    {
        var ruleset = confineNetwork
            ? Kernel.CreateRuleset([FS.CORE], [Net.CONNECT_TCP], [])
            : Kernel.CreateRuleset(FS.CORE);

        foreach (var path in readOnly)
        {
            ruleset.AddPathBeneathRule(path, For(path, ReadOnly));
        }

        if (Directory.Exists("/dev"))
        {
            ruleset.AddPathBeneathRule("/dev", ReadWriteInPlace);
        }

        foreach (var path in readWrite)
        {
            ruleset.AddPathBeneathRule(path, For(path, ReadWrite));
        }

        if (confineNetwork && ports is not null)
        {
            foreach (var port in ports.Distinct())
            {
                ruleset.AddPortRule(port, Net.CONNECT_TCP);
            }
        }

        return ruleset;
    }

    // The kernel refuses a rule on a regular file that grants a directory-only right (EINVAL), so a
    // file keeps only the rights that mean something for a file.
    private static FS[] For(string path, FS[] rights) =>
        Directory.Exists(path)
            ? rights
            : [.. rights.Where(static r => r is FS.EXECUTE or FS.READ_FILE or FS.WRITE_FILE or FS.TRUNCATE)];

    private static List<string> Existing(IEnumerable<string> paths) =>
        [.. paths
            .Where(static p => !string.IsNullOrWhiteSpace(p))
            .Select(static p => Path.GetFullPath(p))
            .Where(static p => Directory.Exists(p) || File.Exists(p))
            .Distinct(StringComparer.Ordinal)];
}
