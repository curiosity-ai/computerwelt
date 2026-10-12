using System.Text;

namespace Computerwelt.Playwright.Tests;

/// <summary>A one-page HTTP server on a free loopback port.</summary>
internal sealed class LocalPage : IDisposable
{
    private readonly System.Net.HttpListener _listener = new();

    private LocalPage(int port, string heading)
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

                var body = Encoding.UTF8.GetBytes($"<h1>{heading}</h1>");
                request.Response.ContentType = "text/html";
                await request.Response.OutputStream.WriteAsync(body);
                request.Response.Close();
            }
        });
    }

    public int Port { get; }

    public string Url => $"http://127.0.0.1:{Port}/";

    public static LocalPage Start(string heading)
    {
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        return new LocalPage(port, heading);
    }

    public void Dispose() => _listener.Close();
}
