using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Pfx.Link;

// The tool's end of the live link (spec 3.12): an HTTP endpoint on 127.0.0.1 that the game's link mod polls with the
// engine's RestApi. A plain TcpListener with a small HTTP/1.1 reader, not HttpListener: HttpListener matches the Host
// header against its registered prefix and needs a URL reservation for anything but "localhost", while the game sends
// whatever host its base URL names. One request per connection ("Connection: close"); the body is text lines. Any path
// ending in the route is the link's: the mod keeps a second RestApi context under its own base URL ("/ev/") for event
// reports, so they do not queue behind its held poll.
public sealed class LinkServer : IDisposable
{
    public const string Route = "/ndzpfx";
    // A request head is a few hundred bytes; a link message a few kilobytes. Anything larger is not the link mod.
    private const int MaxHead = 16 * 1024;
    private const int MaxBody = 1 << 20;

    private readonly Func<string, CancellationToken, Task<string>> _exchange;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    // exchange: the request body in, the answer body out (it may wait: the link's long poll).
    public LinkServer(Func<string, CancellationToken, Task<string>> exchange)
    {
        _exchange = exchange;
    }

    public int Port { get; private set; }
    public bool Running => _listener != null;

    // Requests that were not the link's (wrong route, too large, unsupported encoding): shown in the link panel so a
    // second program on the same port is noticed.
    public event Action<string>? Rejected;

    // Throws SocketException when the port is taken.
    public void Start(int port)
    {
        Stop();
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _cts = new CancellationTokenSource();
        _ = AcceptLoop(listener, _cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _listener = null;
        _cts = null;
    }

    public void Dispose() => Stop();

    private async Task AcceptLoop(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException or InvalidOperationException)
            {
                return;
            }
            _ = Serve(client, ct);
        }
    }

    private async Task Serve(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var request = await HttpLite.Read(stream, MaxHead, MaxBody, ct);
                if (request == null)
                    return;
                if (request.Error != null)
                {
                    Rejected?.Invoke(request.Error);
                    await HttpLite.Answer(stream, request.ErrorStatus, request.Error, ct);
                    return;
                }
                if (!request.Path.EndsWith(Route, StringComparison.Ordinal))
                {
                    Rejected?.Invoke(request.Method + " " + request.Target);
                    await HttpLite.Answer(stream, 404, "", ct);
                    return;
                }
                string answer = await _exchange(request.Body, ct);
                await HttpLite.Answer(stream, 200, answer, ct);
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
            }
        }
    }
}

public sealed record HttpRequest(string Method, string Target, string Path, string Body, string? Error = null, int ErrorStatus = 400);

// The part of HTTP/1.1 the link needs: a request line, headers, an optional "Expect: 100-continue" (libcurl asks for it
// before larger POST bodies), a Content-Length body; a chunked body is refused with 411 (curl sends POST fields with a
// length). Answers are text/plain with a length and "Connection: close".
public static class HttpLite
{
    private static readonly byte[] HeadEnd = "\r\n\r\n"u8.ToArray();

    public static async Task<HttpRequest?> Read(Stream stream, int maxHead, int maxBody, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var head = new MemoryStream();
        int end = -1;
        while (end < 0)
        {
            int n = await stream.ReadAsync(buffer, ct);
            if (n == 0)
                return null;
            head.Write(buffer, 0, n);
            end = IndexOf(head.GetBuffer(), (int)head.Length, HeadEnd);
            if (end < 0 && head.Length > maxHead)
                return new HttpRequest("", "", "", "", "request head too large", 431);
        }
        byte[] all = head.ToArray();
        string headText = Encoding.ASCII.GetString(all, 0, end);
        var lines = headText.Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length < 3)
            return new HttpRequest("", lines[0], "", "", "not an HTTP request line");
        string method = first[0], target = first[1];
        int q = target.IndexOf('?');
        string path = q < 0 ? target : target[..q];
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon > 0)
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        if (headers.TryGetValue("Transfer-Encoding", out var te) && te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            return new HttpRequest(method, target, path, "", "chunked body", 411);
        int length = 0;
        if (headers.TryGetValue("Content-Length", out var cl) && (!int.TryParse(cl, out length) || length < 0 || length > maxBody))
            return new HttpRequest(method, target, path, "", "body too large", 413);
        if (length > 0 && headers.TryGetValue("Expect", out var expect) && expect.Equals("100-continue", StringComparison.OrdinalIgnoreCase))
            await stream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), ct);
        var body = new byte[length];
        int have = Math.Min(length, all.Length - end - HeadEnd.Length);
        Array.Copy(all, end + HeadEnd.Length, body, 0, have);
        while (have < length)
        {
            int n = await stream.ReadAsync(body.AsMemory(have, length - have), ct);
            if (n == 0)
                return null;
            have += n;
        }
        return new HttpRequest(method, target, path, Encoding.UTF8.GetString(body));
    }

    public static async Task Answer(Stream stream, int status, string body, CancellationToken ct)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        string reason = status switch { 200 => "OK", 404 => "Not Found", 411 => "Length Required", 413 => "Payload Too Large", 431 => "Request Header Fields Too Large", _ => "Bad Request" };
        string headText = $"HTTP/1.1 {status} {reason}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headText), ct);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    private static int IndexOf(byte[] data, int count, byte[] pattern)
    {
        for (int i = 0; i + pattern.Length <= count; i++)
        {
            int j = 0;
            while (j < pattern.Length && data[i + j] == pattern[j])
                j++;
            if (j == pattern.Length)
                return i;
        }
        return -1;
    }
}
