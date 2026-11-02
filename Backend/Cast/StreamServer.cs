using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Serilog;

namespace Spana.Backend.Cast;

// Relays VLC's loopback MPEG-TS output to the TV with the headers DLNA renderers look for. Listens on the LAN,
// but only the current session's random token is served.
internal static partial class StreamServer
{
    // Flags: sn-increasing (no fixed end), streaming and background transfer, connection stalling, DLNA 1.5.
    private const string DlnaFeatures = "DLNA.ORG_OP=00;DLNA.ORG_CI=0;DLNA.ORG_FLAGS=05700000000000000000000000000000";
    public const string DlnaProtocolInfo = "http-get:*:video/mp2t:" + DlnaFeatures;

    private sealed record Session(string Token, string Upstream, CancellationTokenSource Ended);

    private static TcpListener? _listener;
    private static volatile Session? _session;
    private static readonly HttpClient Relay = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private static int _relays;
    public static int Port { get; private set; }
    public static long LastRequestTicks;
    public static int ActiveRelays => Volatile.Read(ref _relays);

    [GeneratedRegex(@"^/dlna/([0-9a-f]+)/stream\.ts$")]
    private static partial Regex PathPattern();

    public static void Serve(string token, string upstream)
    {
        EnsureStarted();
        Interlocked.Exchange(ref LastRequestTicks, 0);
        Interlocked.Exchange(ref _session, new Session(token, upstream, new CancellationTokenSource()))?.Ended.Cancel();
    }

    public static void Clear() => Interlocked.Exchange(ref _session, null)?.Ended.Cancel();

    private static void EnsureStarted()
    {
        if (_listener != null) return;
        var listener = new TcpListener(IPAddress.Any, 0);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _listener = listener;
        Log.Information("Cast stream server listening on {Port}", Port);
        _ = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(); }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) { continue; }
                _ = Task.Run(() => Handle(client));
            }
        });
    }

    private static async Task Handle(TcpClient client)
    {
        using var _ = client;
        try
        {
            var stream = client.GetStream();
            var buffer = new byte[8192];
            var head = new MemoryStream();
            while (head.ToArray().AsSpan().IndexOf("\r\n\r\n"u8) < 0)
            {
                using var idle = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                int n = await stream.ReadAsync(buffer, idle.Token);
                if (n == 0 || head.Length > 65536) return;
                head.Write(buffer, 0, n);
            }
            var request = Encoding.ASCII.GetString(head.ToArray()).Split("\r\n")[0].Split(' ');
            string method = request[0];
            string path = request.Length > 1 ? request[1].Split('?')[0] : "";
            var match = PathPattern().Match(path);
            var session = _session;
            if ((method != "GET" && method != "HEAD") || !match.Success || session == null || match.Groups[1].Value != session.Token)
            {
                await Write(stream, "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                return;
            }
            Interlocked.Exchange(ref LastRequestTicks, Environment.TickCount64);
            await RelayStream(stream, method, session);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or HttpRequestException)
        {
        }
    }

    // Runs until the TV disconnects or the session changes.
    private static async Task RelayStream(NetworkStream stream, string method, Session session)
    {
        const string headers = "HTTP/1.1 200 OK\r\nContent-Type: video/mp2t\r\ntransferMode.dlna.org: Streaming\r\n" +
            $"contentFeatures.dlna.org: {DlnaFeatures}\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n";
        if (method == "HEAD")
        {
            await Write(stream, headers);
            return;
        }

        var ct = session.Ended.Token;
        HttpResponseMessage? upstream = null;
        for (int attempt = 0; upstream == null; attempt++)
        {
            try
            {
                var res = await Relay.GetAsync(session.Upstream, HttpCompletionOption.ResponseHeadersRead, ct);
                if (res.IsSuccessStatusCode) upstream = res;
                else res.Dispose();
            }
            catch (HttpRequestException) when (attempt < 30) { }
            if (upstream == null) await Task.Delay(500, ct);
        }
        Interlocked.Increment(ref _relays);
        try
        {
            using (upstream)
            {
                await Write(stream, headers);
                await using var source = await upstream.Content.ReadAsStreamAsync(ct);
                await source.CopyToAsync(stream, ct);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _relays);
        }
    }

    private static Task Write(NetworkStream stream, string text) => stream.WriteAsync(Encoding.ASCII.GetBytes(text)).AsTask();
}
