using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace GrowLinkAgent.CoreTests;

/// <summary>A local HTTP server that answers the agent's climate requests as a test scripts it.</summary>
public sealed class TestServer : IDisposable
{
    public sealed record Received(string Method, string Path, string? Authorization, string Body);
    public sealed record Reply(int Status, string Body = "{}", int? RetryAfterSeconds = null);

    private readonly HttpListener _listener = new();
    private readonly ConcurrentQueue<Reply> _script = new();
    private Task? _loop;

    public int Port { get; }
    public string BaseUrl => $"http://127.0.0.1:{Port}/functions/v1/climate";
    public ConcurrentQueue<Received> Requests { get; } = new();
    /// <summary>The answer when the script is empty.</summary>
    public Reply Default { get; set; } = new(201, """{"status":"created","import_id":"00000000-0000-4000-8000-000000000001"}""");

    public TestServer(int? port = null)
    {
        Port = port ?? FreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
    }

    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public TestServer Start()
    {
        _listener.Start();
        _loop = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { break; }
                using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync();
                Requests.Enqueue(new Received(ctx.Request.HttpMethod, ctx.Request.Url!.AbsolutePath, ctx.Request.Headers["Authorization"], body));
                var reply = _script.TryDequeue(out var r) ? r : Default;
                ctx.Response.StatusCode = reply.Status;
                if (reply.RetryAfterSeconds is { } ra) ctx.Response.Headers["Retry-After"] = ra.ToString();
                var bytes = Encoding.UTF8.GetBytes(reply.Body);
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        });
        return this;
    }

    public void Script(params Reply[] replies)
    {
        foreach (var r in replies) _script.Enqueue(r);
    }

    public void Dispose()
    {
        try { _listener.Stop(); _listener.Close(); } catch { }
    }
}
