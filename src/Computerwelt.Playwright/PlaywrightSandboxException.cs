namespace Computerwelt.Playwright;

/// <summary>
/// Raised when a browser sandbox the host required could not be put in place.
/// </summary>
/// <remarks>
/// Only ever raised when <see cref="PlaywrightSandboxOptions.Required"/> is set: a host that did
/// not insist gets an unconfined session and a <see cref="PlaywrightSession.SandboxStatus"/> saying
/// why, rather than no session.
/// </remarks>
public sealed class PlaywrightSandboxException : Exception
{
    /// <summary>Creates the exception.</summary>
    public PlaywrightSandboxException()
    {
    }

    /// <summary>Creates the exception with what went wrong.</summary>
    public PlaywrightSandboxException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with what went wrong and why.</summary>
    public PlaywrightSandboxException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
