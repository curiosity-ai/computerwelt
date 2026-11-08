using System.Net.WebSockets;
using System.Text;

namespace Computerwelt.Playwright.Tests;

/// <summary>A WebSocket echo server on a free loopback port that counts every request and handshake that reaches it.</summary>
internal sealed class LocalWebSocketServer : IDisposable
{
    private readonly System.Net.HttpListener _listener = new();
    private int _handshakes;
    private int _requests;

    private LocalWebSocketServer(int port)
    {
        Port = port;
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();

        _ = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                System.Net.HttpListenerContext request;

                try
                {
                    request = await _listener.GetContextAsync();
                }
                catch (Exception exception) when (exception is System.Net.HttpListenerException or ObjectDisposedException)
                {
                    return;
                }

                Interlocked.Increment(ref _requests);

                if (!request.Request.IsWebSocketRequest)
                {
                    // A plain request is answered, so a fetch that got through says so.
                    var body = Encoding.UTF8.GetBytes("reached");
                    request.Response.AddHeader("Access-Control-Allow-Origin", "*");
                    await request.Response.OutputStream.WriteAsync(body);
                    request.Response.Close();
                    continue;
                }

                _ = Task.Run(() => EchoAsync(request));
            }
        });
    }

    public int Port { get; }

    public string Url => $"ws://127.0.0.1:{Port}/";

    public string HttpUrl => $"http://127.0.0.1:{Port}/";

    /// <summary>How many requests of any kind reached the server.</summary>
    public int Requests => Volatile.Read(ref _requests);

    /// <summary>How many WebSocket handshakes reached the server.</summary>
    public int Handshakes => Volatile.Read(ref _handshakes);

    public static LocalWebSocketServer Start()
    {
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        return new LocalWebSocketServer(port);
    }

    private async Task EchoAsync(System.Net.HttpListenerContext request)
    {
        Interlocked.Increment(ref _handshakes);

        try
        {
            var context = await request.AcceptWebSocketAsync(subProtocol: null);
            var socket  = context.WebSocket;
            var buffer  = new byte[1024];

            while (socket.State == WebSocketState.Open)
            {
                var received = await socket.ReceiveAsync(buffer, CancellationToken.None);

                if (received.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    return;
                }

                var echo = Encoding.UTF8.GetBytes("echo:" + Encoding.UTF8.GetString(buffer, 0, received.Count));
                await socket.SendAsync(echo, WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }
        catch (Exception exception) when (exception is WebSocketException or System.Net.HttpListenerException or ObjectDisposedException)
        {
            // The page went away mid-conversation; the handshake was already counted.
        }
    }

    public void Dispose() => _listener.Close();
}
