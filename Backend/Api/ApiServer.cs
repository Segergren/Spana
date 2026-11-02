using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.FileProviders;
using Serilog;
using Spana.Backend.App;
using Spana.Backend.Core;
using Spana.Backend.Iptv;
using Spana.Backend.Player;

namespace Spana.Backend.Api;

public static class ApiServer
{
    private const int StartPort = 44140;
    public static string Token { get; } = Environment.GetEnvironmentVariable("SPANA_TOKEN") ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    public static int Port { get; private set; }

    // Image URLs use a key that stays the same across launches so the webview can keep them cached.
    public static string ImageKey
    {
        get
        {
            var s = SettingsService.Current;
            if (s.ImageKey == null)
            {
                s.ImageKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
                SettingsService.Save();
            }
            return s.ImageKey;
        }
    }

    private static readonly IFileProvider Files = new ManifestEmbeddedFileProvider(Assembly.GetExecutingAssembly(), "Resources/wwwroot");

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html", [".js"] = "text/javascript", [".css"] = "text/css", [".json"] = "application/json",
        [".svg"] = "image/svg+xml", [".png"] = "image/png", [".ico"] = "image/x-icon", [".woff"] = "font/woff", [".woff2"] = "font/woff2"
    };

    public static string Start()
    {
        for (int port = StartPort; port < StartPort + 100; port++)
        {
            if (!IsPortFree(port)) continue;
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{port}/");
            try { listener.Start(); }
            catch (HttpListenerException) { continue; }
            Port = port;
            _ = Task.Run(() => AcceptLoop(listener));
            Log.Information("API server listening on {Port}", port);
            return $"http://localhost:{port}";
        }
        throw new IOException("No free port for the local server");
    }

    private static bool IsPortFree(int port)
    {
        try
        {
            using var probe = new TcpListener(IPAddress.Loopback, port);
            probe.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static async Task AcceptLoop(HttpListener listener)
    {
        while (listener.IsListening)
        {
            try
            {
                var ctx = await listener.GetContextAsync();
                _ = Task.Run(() => Handle(ctx));
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                break;
            }
        }
    }

    private static bool IsLocalOrigin(string? origin) =>
        origin == null || (Uri.TryCreate(origin, UriKind.Absolute, out var u) && (u.Host == "localhost" || u.Host == "127.0.0.1"));

    private static async Task Handle(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        string path = req.Url?.AbsolutePath ?? "/";
        string? origin = req.Headers["Origin"];
        bool keepOpen = false;

        try
        {
            if (!IsLocalOrigin(origin))
            {
                res.StatusCode = 403;
                return;
            }
            if (origin != null)
            {
                res.Headers["Access-Control-Allow-Origin"] = origin;
                res.Headers["Access-Control-Allow-Headers"] = "Content-Type, X-Spana-Token";
                res.Headers["Access-Control-Allow-Methods"] = "GET, POST, DELETE, OPTIONS";
            }
            if (req.HttpMethod == "OPTIONS")
            {
                res.StatusCode = 204;
                return;
            }

            bool isApi = path.StartsWith("/api/") || path == "/ws" || path == "/video" || path == "/img";
            if (!isApi)
            {
                await ServeStatic(path, req, res);
                return;
            }

            string? token = req.Headers["X-Spana-Token"] ?? req.QueryString["t"];
            bool imageKeyOk = path == "/img" && req.QueryString["k"] is { } k && k == ImageKey;
            if (token != Token && !imageKeyOk)
            {
                res.StatusCode = 401;
                return;
            }

            if (path == "/ws" && req.IsWebSocketRequest)
            {
                keepOpen = true;
                var ws = await ctx.AcceptWebSocketAsync(null);
                await MessageHub.RunAsync(ws.WebSocket);
                return;
            }
            if (path == "/video" && req.IsWebSocketRequest)
            {
                keepOpen = true;
                var ws = await ctx.AcceptWebSocketAsync(null);
                PlayerService.Frames.Attach(ws.WebSocket);
                var buf = new byte[256];
                try
                {
                    while (ws.WebSocket.State == System.Net.WebSockets.WebSocketState.Open)
                    {
                        var r = await ws.WebSocket.ReceiveAsync(buf, CancellationToken.None);
                        if (r.MessageType == System.Net.WebSockets.WebSocketMessageType.Close) break;
                    }
                }
                catch { }
                finally { PlayerService.Frames.Detach(ws.WebSocket); }
                return;
            }
            if (path == "/img")
            {
                await ServeImage(req, res);
                return;
            }

            await Routes.Dispatch(req, res, path);
        }
        catch (ApiError e)
        {
            await WriteJson(res, new { error = e.Message }, e.Status);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Request failed {Path}", path);
            try { await WriteJson(res, new { error = ex.Message }, 500); } catch { }
        }
        finally
        {
            if (!keepOpen)
            {
                try { res.Close(); } catch { }
            }
        }
    }

    private static async Task ServeImage(HttpListenerRequest req, HttpListenerResponse res)
    {
        string? url = req.QueryString["u"];
        if (string.IsNullOrEmpty(url) || !(url.StartsWith("http://") || url.StartsWith("https://")))
        {
            res.StatusCode = 400;
            return;
        }
        int.TryParse(req.QueryString["w"], out int width);
        var (file, type) = await ImageCache.GetAsync(url, width);
        if (file == null)
        {
            res.StatusCode = 404;
            return;
        }
        var info = new FileInfo(file);
        string etag = $"\"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}\"";
        res.Headers["ETag"] = etag;
        // Shown from the webview cache at once, then quietly checked against our disk copy.
        res.Headers["Cache-Control"] = "public, max-age=86400, stale-while-revalidate=31536000";
        if (req.Headers["If-None-Match"] == etag)
        {
            res.StatusCode = 304;
            return;
        }
        res.ContentType = type;
        await using var fs = File.OpenRead(file);
        res.ContentLength64 = fs.Length;
        await fs.CopyToAsync(res.OutputStream);
    }

    private static async Task ServeStatic(string path, HttpListenerRequest req, HttpListenerResponse res)
    {
        if (path.EndsWith('/')) path += "index.html";
        var file = Files.GetFileInfo(path);
        if (req.HttpMethod != "GET" || !file.Exists || file.IsDirectory || !ContentTypes.TryGetValue(Path.GetExtension(path), out var type))
        {
            res.StatusCode = 404;
            return;
        }
        string etag = $"\"{file.LastModified.UtcTicks:x}-{file.Length:x}\"";
        res.Headers["ETag"] = etag;
        if (req.Headers["If-None-Match"] == etag)
        {
            res.StatusCode = 304;
            return;
        }
        res.ContentType = type;
        // Bundle files carry a content hash in their name, so they never change and can be cached for good.
        res.Headers["Cache-Control"] = path.StartsWith("/assets/") ? "public, max-age=31536000, immutable" : "no-cache";
        res.ContentLength64 = file.Length;
        await using var stream = file.CreateReadStream();
        await stream.CopyToAsync(res.OutputStream);
    }

    public static async Task WriteJson(HttpListenerResponse res, object? value, int status = 200)
    {
        res.StatusCode = status;
        res.ContentType = "application/json; charset=utf-8";
        if (res.Headers["Cache-Control"] == null) res.Headers["Cache-Control"] = "no-store";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Storage.Json);
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
    }

    public static async Task<T> ReadJson<T>(HttpListenerRequest req) where T : new()
    {
        using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
        string body = await reader.ReadToEndAsync();
        return string.IsNullOrWhiteSpace(body) ? new T() : JsonSerializer.Deserialize<T>(body, Storage.Json) ?? new T();
    }
}

public sealed class ApiError(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}
