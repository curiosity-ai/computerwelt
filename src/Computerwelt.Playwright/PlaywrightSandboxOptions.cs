namespace Computerwelt.Playwright;

/// <summary>
/// Confines the browser processes a <see cref="PlaywrightSession"/> starts with the Linux
/// kernel's <see href="https://landlock.io">Landlock</see> security module.
/// </summary>
/// <remarks>
/// <para>
/// The navigation policy decides what a page may <i>ask</i> for; this decides what the browser
/// process can reach if it is ever made to ask for something else — a renderer exploit, a
/// <c>file:</c> URL a host allowed by mistake, a driver bug. Everything outside the paths named
/// here is denied to the driver and to every browser it launches, by the kernel, for the life of
/// those processes.
/// </para>
/// <para>
/// Landlock restricts the calling thread and is inherited by the processes it starts, so the
/// session enforces the ruleset on a throw-away thread and starts the Playwright driver from
/// there. The host process itself is never restricted.
/// </para>
/// <para>
/// Only Linux has Landlock (kernel 5.13 and later). Elsewhere, or on a kernel built without it,
/// the session starts unconfined and says so in <see cref="PlaywrightSession.SandboxStatus"/> —
/// unless <see cref="Required"/> is set, in which case it refuses to start at all.
/// </para>
/// </remarks>
public sealed record PlaywrightSandboxOptions
{
    /// <summary>
    /// Whether a session that cannot be confined refuses to start rather than starting without
    /// the sandbox.
    /// </summary>
    public bool Required { get; init; }

    /// <summary>
    /// Whether the usual system locations are readable: <c>/usr</c>, <c>/lib</c>, <c>/etc</c>,
    /// <c>/proc</c>, <c>/sys</c> and the rest of what a dynamically linked browser loads its
    /// libraries, fonts and certificates from.
    /// </summary>
    /// <remarks>
    /// On by default, because a browser cannot start without them. Turn it off only to replace
    /// them with an exact list in <see cref="ReadOnlyPaths"/>.
    /// </remarks>
    public bool IncludeSystemPaths { get; init; } = true;

    /// <summary>
    /// Whether the operating system's temporary directory is writable.
    /// </summary>
    /// <remarks>
    /// The driver creates each browser profile under it — the directory comes from the host
    /// process's <c>TMPDIR</c>, which the driver inherits — so a browser cannot start without
    /// it. Turn it off only when <see cref="ReadWritePaths"/> names wherever <c>TMPDIR</c>
    /// points.
    /// </remarks>
    public bool AllowTemporaryDirectory { get; init; } = true;

    /// <summary>Extra files and directories the browser may read, and execute from.</summary>
    public IReadOnlyList<string> ReadOnlyPaths { get; init; } = [];

    /// <summary>Extra files and directories the browser may read, write, create and delete in.</summary>
    public IReadOnlyList<string> ReadWritePaths { get; init; } = [];

    /// <summary>
    /// The TCP ports the browser may connect to, or null to leave TCP unrestricted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Needs Landlock ABI 4 (kernel 6.7); on an older kernel the list is not enforced and
    /// <see cref="PlaywrightSandboxStatus.NetworkConfined"/> says so. It is a backstop under
    /// <see cref="PlaywrightOptions.AllowedHosts"/>, not a replacement: a port names no host.
    /// </para>
    /// <para>
    /// The driver talks to the host and to the browser over pipes, so neither needs a port of
    /// its own. Name every port a page should reach — usually <c>80</c> and <c>443</c>, plus
    /// whatever local service the browser is meant to see — and <c>53</c> if name resolution
    /// may fall back to TCP.
    /// </para>
    /// </remarks>
    public IReadOnlyList<int>? AllowedTcpPorts { get; init; }
}

/// <summary>What the sandbox actually did for a session.</summary>
/// <param name="Requested">Whether the host asked for a sandbox at all.</param>
/// <param name="FileSystemConfined">Whether the driver and its browsers run under a Landlock filesystem ruleset.</param>
/// <param name="NetworkConfined">Whether <see cref="PlaywrightSandboxOptions.AllowedTcpPorts"/> is enforced.</param>
/// <param name="AbiVersion">The kernel's Landlock ABI version, or zero when it has none.</param>
/// <param name="Reason">Why the sandbox is not (fully) in force, or null when it is.</param>
public sealed record PlaywrightSandboxStatus(
    bool Requested,
    bool FileSystemConfined,
    bool NetworkConfined,
    int AbiVersion,
    string? Reason)
{
    /// <summary>The status of a session whose host asked for no sandbox.</summary>
    public static PlaywrightSandboxStatus NotRequested { get; } = new(false, false, false, 0, "no sandbox was requested");

    /// <summary>One line for a log or an admin page.</summary>
    public override string ToString() =>
        !Requested
            ? "not sandboxed (none requested)"
            : FileSystemConfined
                ? $"sandboxed with Landlock ABI {AbiVersion}" + (NetworkConfined ? ", TCP ports confined" : string.Empty) + (Reason is null ? string.Empty : $" ({Reason})")
                : $"not sandboxed: {Reason}";
}
