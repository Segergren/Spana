using System.Net;
using System.Text;
using System.Text.Json;
using Spana.Backend.Core;

namespace Spana.Backend.Iptv;

public static class XtreamClient
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(15)
    })
    { Timeout = TimeSpan.FromMinutes(3) };

    public static HttpClient Client => Http;

    // Accepts a get.php / player_api.php link or a plain server address.
    public static Account ParseAccountInput(string name, string input, string? username, string? password)
    {
        input = input.Trim();
        if (!input.Contains("://")) input = "http://" + input;
        var uri = new Uri(input);
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        string? qUser = query["username"];
        string? qPass = query["password"];
        string server = uri.GetLeftPart(UriPartial.Authority);

        bool looksXtream = qUser != null || !string.IsNullOrWhiteSpace(username) ||
                           uri.AbsolutePath.EndsWith("get.php") || uri.AbsolutePath.EndsWith("player_api.php");

        var account = new Account { Name = string.IsNullOrWhiteSpace(name) ? uri.Host : name.Trim() };
        if (looksXtream && (qUser ?? username) != null)
        {
            account.Kind = AccountKind.Xtream;
            account.Server = server;
            account.Username = (qUser ?? username ?? "").Trim();
            account.Password = (qPass ?? password ?? "").Trim();
        }
        else
        {
            account.Kind = AccountKind.M3u;
            account.M3uUrl = input;
        }
        return account;
    }

    private static string ApiUrl(Account a, string? action = null, string? extra = null)
    {
        var sb = new StringBuilder($"{a.Server}/player_api.php?username={Uri.EscapeDataString(a.Username)}&password={Uri.EscapeDataString(a.Password)}");
        if (action != null) sb.Append("&action=").Append(action);
        if (extra != null) sb.Append('&').Append(extra);
        return sb.ToString();
    }

    public static async Task<byte[]> GetBytesAsync(string url, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", SettingsService.Current.UserAgent);
        using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsByteArrayAsync(ct);
    }

    private static async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        var bytes = await GetBytesAsync(url, ct);
        return JsonDocument.Parse(bytes);
    }

    public static async Task<AccountInfo> AuthenticateAsync(Account a, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync(ApiUrl(a), ct);
        var user = doc.RootElement.Prop("user_info") ?? throw new InvalidOperationException("The server did not return account info. Check the address.");
        if (user.Long("auth") != 1) throw new InvalidOperationException("Wrong username or password.");
        long exp = user.Long("exp_date");
        return new AccountInfo
        {
            Status = user.Str("status") ?? "Unknown",
            ExpiresAt = exp > 0 ? exp : null,
            MaxConnections = (int)user.Long("max_connections"),
            ActiveConnections = (int)user.Long("active_cons"),
            IsTrial = user.Bool("is_trial"),
            CheckedAt = DateTimeOffset.UtcNow
        };
    }

    public static async Task<List<Category>> GetCategoriesAsync(Account a, MediaKind kind, CancellationToken ct)
    {
        string action = kind switch { MediaKind.Live => "get_live_categories", MediaKind.Movie => "get_vod_categories", _ => "get_series_categories" };
        using var doc = await GetJsonAsync(ApiUrl(a, action), ct);
        return doc.RootElement.Items()
            .Select(c => new Category { Id = c.Str("category_id") ?? "", Name = c.Str("category_name") ?? "Uncategorized" })
            .ToList();
    }

    public static async Task<List<CatalogItem>> GetItemsAsync(Account a, MediaKind kind, CancellationToken ct)
    {
        string action = kind switch { MediaKind.Live => "get_live_streams", MediaKind.Movie => "get_vod_streams", _ => "get_series" };
        using var doc = await GetJsonAsync(ApiUrl(a, action), ct);
        var list = new List<CatalogItem>();
        foreach (var e in doc.RootElement.Items())
        {
            var item = new CatalogItem
            {
                Id = (kind == MediaKind.Series ? e.Str("series_id") : e.Str("stream_id")) ?? "",
                Name = CleanName(e.Str("name") ?? e.Str("title") ?? ""),
                Icon = kind == MediaKind.Series ? e.Str("cover") : e.Str("stream_icon"),
                CategoryId = e.Str("category_id") ?? "",
                Num = (int)e.Long("num"),
                Rating = e.Dbl("rating"),
                Added = kind == MediaKind.Series ? e.Long("last_modified") : e.Long("added"),
                Ext = e.Str("container_extension"),
                EpgId = e.Str("epg_channel_id"),
                Archive = e.Long("tv_archive") == 1,
                Adult = e.Bool("is_adult"),
                Genre = e.Str("genre"),
                Plot = kind == MediaKind.Series ? Truncate(e.Str("plot"), 400) : null,
                Year = YearOf(e.Str("releaseDate") ?? e.Str("year")),
                Backdrop = e.Str("backdrop_path")
            };
            if (item.Rating == 0) item.Rating = null;
            if (item.Id.Length > 0) list.Add(item);
        }
        return list;
    }

    public static async Task<JsonDocument> GetSeriesInfoAsync(Account a, string seriesId, CancellationToken ct) =>
        await GetJsonAsync(ApiUrl(a, "get_series_info", "series_id=" + Uri.EscapeDataString(seriesId)), ct);

    public static async Task<JsonDocument> GetVodInfoAsync(Account a, string vodId, CancellationToken ct) =>
        await GetJsonAsync(ApiUrl(a, "get_vod_info", "vod_id=" + Uri.EscapeDataString(vodId)), ct);

    public static async Task<JsonDocument> GetShortEpgAsync(Account a, string streamId, int limit, CancellationToken ct) =>
        await GetJsonAsync(ApiUrl(a, "get_short_epg", $"stream_id={Uri.EscapeDataString(streamId)}&limit={limit}"), ct);

    public static string StreamUrl(Account a, MediaKind kind, string id, string? ext)
    {
        string u = Uri.EscapeDataString(a.Username), p = Uri.EscapeDataString(a.Password);
        return kind switch
        {
            MediaKind.Live => $"{a.Server}/live/{u}/{p}/{id}.ts",
            MediaKind.Movie => $"{a.Server}/movie/{u}/{p}/{id}.{ext ?? "mp4"}",
            _ => $"{a.Server}/series/{u}/{p}/{id}.{ext ?? "mp4"}"
        };
    }

    public static string? YearOf(string? date)
    {
        if (string.IsNullOrWhiteSpace(date) || date.Length < 4) return null;
        var y = date[..4];
        return y.All(char.IsDigit) && y != "0000" ? y : null;
    }

    private static string? Truncate(string? s, int max) => s == null || s.Length <= max ? s : s[..max] + "...";

    private static string CleanName(string name) => name.Replace("​", "").Trim();
}
