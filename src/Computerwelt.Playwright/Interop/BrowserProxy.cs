using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Computerwelt.Playwright.Interop;

/// <summary>
/// The proxy every page's traffic goes through, which is where <see cref="PlaywrightOptions.AllowedHosts"/>
/// is enforced for everything a request route cannot see.
/// </summary>
/// <remarks>
/// <para>
/// A <c>context.route</c> handler sees the requests of a page and its frames, and nothing else:
/// not a WebSocket handshake, not a request a worker makes. So the browser is pointed at this
/// proxy instead, with loopback included (<c>&lt;-loopback&gt;</c>), and the proxy decides for
/// each connection - a <c>CONNECT</c> tunnel, which is how TLS and WebSockets travel, or a
/// plain HTTP request - before anything leaves the machine.
/// </para>
/// <para>
/// Each context signs in to the proxy with credentials of its own (<see cref="Register"/>), which
/// is how one shared proxy applies each tenant's host list: a connection without valid
/// credentials is refused, so a context somebody opened without them reaches nothing.
/// </para>
/// <para>
/// A tunnel is opened for a host and a port and carries whatever the browser puts in it, so a
/// <c>CONNECT</c> is allowed when the host would be allowed over <c>http</c> or <c>https</c> on
/// that port. A WebSocket is decided by its host, like every other request.
/// </para>
/// <para>
/// Under a sandbox that confines TCP, the browser may connect to this proxy and nothing else, so
/// the ports the host granted (<see cref="PlaywrightSandboxOptions.AllowedTcpPorts"/>) are
/// enforced here, on the connections the proxy opens on the browser's behalf.
/// </para>
/// </remarks>
internal sealed class BrowserProxy : IAsyncDisposable
{
    private const int MAX_HEAD_BYTES = 64 * 1024;

    private static readonly TimeSpan HeadTimeout = TimeSpan.FromSeconds(30);

    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Proxy-Authorization", "Proxy-Connection", "Connection", "Keep-Alive",
    };

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly ConcurrentDictionary<string, Registration> _registrations = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _accepting;
    private readonly HashSet<int>? _allowedPorts;

    private BrowserProxy(IReadOnlyCollection<int>? allowedPorts)
    {
        _allowedPorts = allowedPorts is null ? null : [.. allowedPorts];
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accepting = Task.Run(AcceptAsync);
    }

    /// <summary>The loopback port the proxy listens on.</summary>
    public int Port { get; }

    /// <summary>The address a browser is pointed at.</summary>
    public string Server => $"http://127.0.0.1:{Port}";

    /// <summary>Starts a proxy on a free loopback port.</summary>
    /// <param name="allowedPorts">The only ports it may connect to on a page's behalf, or null for any.</param>
    public static BrowserProxy Start(IReadOnlyCollection<int>? allowedPorts = null) => new(allowedPorts);

    /// <summary>
    /// Credentials whose connections are decided by <paramref name="policy"/>, valid until the
    /// returned registration is disposed.
    /// </summary>
    public Registration Register(NavigationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var registration = new Registration(this, policy, "cw-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)), Convert.ToHexString(RandomNumberGenerator.GetBytes(24)));

        _registrations[registration.Username] = registration;

        return registration;
    }

    private async Task AcceptAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            TcpClient client;

            try
            {
                client = await _listener.AcceptTcpClientAsync(_stopping.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                await ServeConnectionAsync(client).ConfigureAwait(false);
            }
            catch (RefusedException)
            {
                // A reset, not an answer: a browser shows a proxy's answer to a plain HTTP request
                // as though the server had sent it, while a reset fails the request as a network
                // error - which is what a refusal is everywhere else in this package.
                client.LingerState = new LingerOption(true, 0);
            }
            catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // The browser or the server hung up; there is nobody left to tell.
            }
        }
    }

    private async Task ServeConnectionAsync(TcpClient client)
    {
        var browser = client.GetStream();

        using var headTimeout = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        headTimeout.CancelAfter(HeadTimeout);

        var (head, rest) = await ReadHeadAsync(browser, headTimeout.Token).ConfigureAwait(false);

        if (head is null)
        {
            return;
        }

        var lines = head.Split("\r\n");
        var requestLine = lines[0].Split(' ');

        if (requestLine.Length != 3)
        {
            await RespondAsync(browser, 400, "Bad Request", "malformed request line").ConfigureAwait(false);
            return;
        }

        var headers = lines.Skip(1)
            .Where(static l => l.Length > 0)
            .Select(static l => (Name: l[..Math.Max(l.IndexOf(':'), 0)].Trim(), Value: l[(l.IndexOf(':') + 1)..].Trim()))
            .Where(static h => h.Name.Length > 0)
            .ToList();

        if (Authenticate(headers) is not { } registration)
        {
            await RespondAsync(browser, 407, "Proxy Authentication Required", "this browser context has no access to the network",
                "Proxy-Authenticate: Basic realm=\"computerwelt\"").ConfigureAwait(false);
            return;
        }

        var (method, target) = (requestLine[0], requestLine[1]);

        if (string.Equals(method, "CONNECT", StringComparison.OrdinalIgnoreCase))
        {
            await TunnelAsync(browser, rest, target, registration.Policy).ConfigureAwait(false);
        }
        else
        {
            await ForwardAsync(browser, rest, method, target, requestLine[2], headers, registration.Policy).ConfigureAwait(false);
        }
    }

    private Registration? Authenticate(List<(string Name, string Value)> headers)
    {
        foreach (var (name, value) in headers)
        {
            if (!string.Equals(name, "Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
             || !value.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string decoded;

            try
            {
                decoded = Encoding.UTF8.GetString(Convert.FromBase64String(value[6..].Trim()));
            }
            catch (FormatException)
            {
                return null;
            }

            var colon = decoded.IndexOf(':', StringComparison.Ordinal);

            if (colon < 0 || !_registrations.TryGetValue(decoded[..colon], out var registration))
            {
                return null;
            }

            var given = Encoding.UTF8.GetBytes(decoded[(colon + 1)..]);
            var expected = Encoding.UTF8.GetBytes(registration.Password);

            return CryptographicOperations.FixedTimeEquals(given, expected) ? registration : null;
        }

        return null;
    }

    private async Task TunnelAsync(NetworkStream browser, byte[] rest, string authority, NavigationPolicy policy)
    {
        if (!Uri.TryCreate("http://" + authority, UriKind.Absolute, out var parsed) || parsed.Authority.Length == 0)
        {
            await RespondAsync(browser, 400, "Bad Request", "malformed CONNECT target").ConfigureAwait(false);
            return;
        }

        var host = parsed.Host;
        var port = parsed.Port;

        if (!policy.IsAllowed(UrlFor("https", host, port)) && !policy.IsAllowed(UrlFor("http", host, port)))
        {
            await RespondAsync(browser, 403, "Forbidden", $"{host}:{port} is not among the hosts this sandbox may reach (AllowedHosts)").ConfigureAwait(false);
            return;
        }

        if (!AllowsPort(port))
        {
            await RespondAsync(browser, 403, "Forbidden", $"port {port} is not among the TCP ports this sandbox may connect to (AllowedTcpPorts)").ConfigureAwait(false);
            return;
        }

        using var server = await ConnectAsync(browser, parsed.DnsSafeHost, port).ConfigureAwait(false);

        if (server is null)
        {
            return;
        }

        await browser.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"), _stopping.Token).ConfigureAwait(false);

        var upstream = server.GetStream();

        if (rest.Length > 0)
        {
            await upstream.WriteAsync(rest, _stopping.Token).ConfigureAwait(false);
        }

        await PipeAsync(browser, upstream).ConfigureAwait(false);
    }

    private async Task ForwardAsync(
        NetworkStream                    browser,
        byte[]                           rest,
        string                           method,
        string                           target,
        string                           version,
        List<(string Name, string Value)> headers,
        NavigationPolicy                 policy)
    {
        if (!Uri.TryCreate(target, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "ws"))
        {
            await RespondAsync(browser, 400, "Bad Request", "a proxy request names an absolute http URL").ConfigureAwait(false);
            return;
        }

        if (!policy.IsAllowed(UrlFor("http", url.Host, url.Port)) || !AllowsPort(url.Port))
        {
            throw new RefusedException();
        }

        using var server = await ConnectAsync(browser, url.DnsSafeHost, url.Port).ConfigureAwait(false);

        if (server is null)
        {
            return;
        }

        // One request per connection: the browser may send its next request on the same
        // connection to a different host, which would then not be checked. An upgrade keeps
        // its own Connection header, because that is what asks for the upgrade.
        var upgrading = headers.Any(static h => string.Equals(h.Name, "Upgrade", StringComparison.OrdinalIgnoreCase));
        var request = new StringBuilder();

        request.Append(method).Append(' ').Append(url.PathAndQuery).Append(' ').Append(version).Append("\r\n");

        foreach (var (name, value) in headers)
        {
            if (string.Equals(name, "Connection", StringComparison.OrdinalIgnoreCase) && upgrading)
            {
                request.Append(name).Append(": ").Append(value).Append("\r\n");
            }
            else if (!HopByHopHeaders.Contains(name))
            {
                request.Append(name).Append(": ").Append(value).Append("\r\n");
            }
        }

        if (!upgrading)
        {
            request.Append("Connection: close\r\n");
        }

        request.Append("\r\n");

        var upstream = server.GetStream();

        await upstream.WriteAsync(Encoding.ASCII.GetBytes(request.ToString()), _stopping.Token).ConfigureAwait(false);

        if (rest.Length > 0)
        {
            await upstream.WriteAsync(rest, _stopping.Token).ConfigureAwait(false);
        }

        await PipeAsync(browser, upstream).ConfigureAwait(false);
    }

    private bool AllowsPort(int port) => _allowedPorts is null || _allowedPorts.Contains(port);

    private async Task<TcpClient?> ConnectAsync(NetworkStream browser, string host, int port)
    {
        var server = new TcpClient();

        try
        {
            await server.ConnectAsync(host, port, _stopping.Token).ConfigureAwait(false);
            return server;
        }
        catch (SocketException exception)
        {
            server.Dispose();
            await RespondAsync(browser, 502, "Bad Gateway", $"{host}:{port} could not be reached: {exception.SocketErrorCode}").ConfigureAwait(false);
            return null;
        }
    }

    private async Task PipeAsync(NetworkStream browser, NetworkStream server)
    {
        using var done = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);

        var up   = CopyAsync(browser, server, done.Token);
        var down = CopyAsync(server, browser, done.Token);

        // Either side closing ends the exchange, and the other side is cut with it.
        await Task.WhenAny(up, down).ConfigureAwait(false);
        await done.CancelAsync().ConfigureAwait(false);

        try
        {
            await Task.WhenAll(up, down).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private static async Task CopyAsync(NetworkStream from, NetworkStream to, CancellationToken cancellationToken)
    {
        try
        {
            await from.CopyToAsync(to, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                to.Socket.Shutdown(SocketShutdown.Send);
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
            {
            }
        }
    }

    // Reads up to the blank line ending a request head, and hands back whatever arrived after it.
    private static async Task<(string? Head, byte[] Remainder)> ReadHeadAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MAX_HEAD_BYTES];
        var length = 0;

        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                return (null, []);
            }

            var searchFrom = Math.Max(0, length - 3);
            length += read;

            var end = buffer.AsSpan(searchFrom, length - searchFrom).IndexOf("\r\n\r\n"u8);

            if (end >= 0)
            {
                end += searchFrom;
                return (Encoding.ASCII.GetString(buffer, 0, end), buffer[(end + 4)..length]);
            }
        }

        return (null, []);
    }

    private async Task RespondAsync(NetworkStream stream, int status, string reason, string message, string? extraHeader = null)
    {
        var body = Encoding.UTF8.GetBytes(message);
        var head = new StringBuilder()
            .Append("HTTP/1.1 ").Append(status.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(reason).Append("\r\n")
            .Append("Content-Type: text/plain; charset=utf-8\r\n")
            .Append("Content-Length: ").Append(body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n")
            .Append("Connection: close\r\n");

        if (extraHeader is not null)
        {
            head.Append(extraHeader).Append("\r\n");
        }

        head.Append("\r\n");

        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), _stopping.Token).ConfigureAwait(false);
        await stream.WriteAsync(body, _stopping.Token).ConfigureAwait(false);
    }

    private static string UrlFor(string scheme, string host, int port) =>
        new UriBuilder(scheme, host, port, "/").Uri.ToString();

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        _listener.Stop();

        try
        {
            await _accepting.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _registrations.Clear();
        _stopping.Dispose();
    }

    // A plain HTTP request the proxy refuses; the connection is reset rather than answered.
    private sealed class RefusedException : Exception;

    /// <summary>One context's credentials, and the policy its connections are decided by.</summary>
    internal sealed class Registration : IDisposable
    {
        private readonly BrowserProxy _proxy;

        internal Registration(BrowserProxy proxy, NavigationPolicy policy, string username, string password)
        {
            _proxy = proxy;
            Policy = policy;
            Username = username;
            Password = password;
        }

        public NavigationPolicy Policy { get; }

        public string Username { get; }

        public string Password { get; }

        /// <summary>What a context is opened with, so its traffic signs in as this registration.</summary>
        public Microsoft.Playwright.Proxy ToProxy() => new()
        {
            Server = _proxy.Server,
            Bypass = "<-loopback>",
            Username = Username,
            Password = Password,
        };

        /// <summary>Withdraws the credentials; a connection still open keeps going, a new one is refused.</summary>
        public void Dispose() => _proxy._registrations.TryRemove(Username, out _);
    }
}
