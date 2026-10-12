namespace Computerwelt.Playwright;

/// <summary>
/// Thrown by a host's scope resolver to say why a sandbox cannot have a browser right now.
/// </summary>
/// <remarks>
/// The message becomes the <c>ImportError</c> the program sees on <c>import playwright</c>, so
/// write it for the program's author: what is unavailable and what would make it available.
/// </remarks>
public sealed class PlaywrightUnavailableException : Exception
{
    /// <summary>Creates the exception.</summary>
    public PlaywrightUnavailableException()
    {
    }

    /// <summary>Creates the exception with the reason the program will read.</summary>
    public PlaywrightUnavailableException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with the reason the program will read, and its cause.</summary>
    public PlaywrightUnavailableException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
