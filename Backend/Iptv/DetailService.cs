using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Serilog;
using Spana.Backend.Core;

namespace Spana.Backend.Iptv;

public sealed class MetaInfo
{
    public string Id { get; set; } = "";
    public MediaKind Kind { get; set; }
    public string Name { get; set; } = "";
    public string? Poster { get; set; }
    public string? Backdrop { get; set; }
    public string? Plot { get; set; }
    public string? Cast { get; set; }
    public string? Director { get; set; }
    public string? Genre { get; set; }
    public string? Year { get; set; }
    public double? Rating { get; set; }
    public string? Runtime { get; set; }
    public string? Trailer { get; set; }
    public string? Ext { get; set; }
    public List<Season>? Seasons { get; set; }
}

public sealed class Season
{
    public int Number { get; set; }
    public string Name { get; set; } = "";
    public string? Cover { get; set; }
    public List<Episode> Episodes { get; set; } = new();
}

public sealed class Episode
{
    public string Id { get; set; } = "";
    public int Season { get; set; }
    public int Number { get; set; }
    public string Title { get; set; } = "";
    public string? Ext { get; set; }
    public string? Plot { get; set; }
    public string? Image { get; set; }
    public int DurationSecs { get; set; }
    public double? Rating { get; set; }
    public string? AirDate { get; set; }
}

public sealed class EpgEntry
{
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public long Start { get; set; }
    public long End { get; set; }
    public string? Image { get; set; }
    public List<string>? Teams { get; set; }
    public MatchInfo? Info { get; set; }
}

public static class DetailService
{
    private sealed record Cached<T>(T Value, DateTimeOffset At);
    private static readonly ConcurrentDictionary<string, Cached<MetaInfo>> Meta = new();
    private static readonly ConcurrentDictionary<string, Cached<List<EpgEntry>>> Epg = new();

    private static string MetaFile(Account a, MediaKind k, string id) =>
        Path.Combine(Storage.CacheRoot, a.Id, "meta", $"{k.ToString().ToLowerInvariant()}-{id}.json");

    public static async Task<MetaInfo> GetMetaAsync(Account a, MediaKind kind, string id, CancellationToken ct)
    {
        var ttl = kind == MediaKind.Series ? TimeSpan.FromHours(12) : TimeSpan.FromDays(7);
        string key = $"{a.Id}:{kind}:{id}";
        if (Meta.TryGetValue(key, out var mem) && DateTimeOffset.UtcNow - mem.At < ttl)
            return mem.Value;

        string file = MetaFile(a, kind, id);
        MetaInfo? stale = null;
        if (File.Exists(file))
        {
            try
            {
                stale = JsonSerializer.Deserialize<MetaInfo>(await File.ReadAllTextAsync(file, ct), Storage.Json);
                if (stale != null && DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(file) < ttl)
                {
                    Meta[key] = new(stale, new DateTimeOffset(File.GetLastWriteTimeUtc(file)));
                    return stale;
                }
            }
            catch { }
        }

        var basic = CatalogService.Find(a, kind, id);
        if (a.Kind != AccountKind.Xtream)
            return FromItem(kind, id, basic);

        try
        {
            var meta = kind == MediaKind.Series ? await FetchSeriesAsync(a, id, basic, ct) : await FetchMovieAsync(a, id, basic, ct);
            Meta[key] = new(meta, DateTimeOffset.UtcNow);
            Storage.Save(file, meta);
            return meta;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Log.Warning(ex, "Meta fetch failed for {Kind} {Id}", kind, id);
            return stale ?? FromItem(kind, id, basic);
        }
    }

    private static MetaInfo FromItem(MediaKind kind, string id, CatalogItem? item) => new()
    {
        Id = id,
        Kind = kind,
        Name = item?.Name ?? "",
        Poster = item?.Icon,
        Backdrop = item?.Backdrop,
        Plot = item?.Plot,
        Genre = item?.Genre,
        Year = item?.Year,
        Rating = item?.Rating,
        Ext = item?.Ext
    };

    private static async Task<MetaInfo> FetchMovieAsync(Account a, string id, CatalogItem? basic, CancellationToken ct)
    {
        using var doc = await XtreamClient.GetVodInfoAsync(a, id, ct);
        var root = doc.RootElement;
        var info = root.Prop("info") ?? default;
        var data = root.Prop("movie_data") ?? default;
        var meta = FromItem(MediaKind.Movie, id, basic);
        meta.Name = basic?.Name ?? info.Str("name") ?? data.Str("name") ?? meta.Name;
        meta.Poster = info.Str("movie_image") ?? info.Str("cover_big") ?? meta.Poster;
        meta.Backdrop = info.Str("backdrop_path") ?? meta.Backdrop;
        meta.Plot = info.Str("plot") ?? info.Str("description") ?? meta.Plot;
        meta.Cast = info.Str("cast") ?? info.Str("actors");
        meta.Director = info.Str("director");
        meta.Genre = info.Str("genre") ?? meta.Genre;
        meta.Year = XtreamClient.YearOf(info.Str("releasedate") ?? info.Str("release_date")) ?? meta.Year;
        meta.Rating = info.Dbl("rating") is > 0 and var r ? r : meta.Rating;
        meta.Runtime = info.Str("duration");
        meta.Trailer = info.Str("youtube_trailer");
        meta.Ext = data.Str("container_extension") ?? meta.Ext;
        return meta;
    }

    private static async Task<MetaInfo> FetchSeriesAsync(Account a, string id, CatalogItem? basic, CancellationToken ct)
    {
        using var doc = await XtreamClient.GetSeriesInfoAsync(a, id, ct);
        var root = doc.RootElement;
        var info = root.Prop("info") ?? default;
        var meta = FromItem(MediaKind.Series, id, basic);
        meta.Name = basic?.Name ?? info.Str("name") ?? meta.Name;
        meta.Poster = info.Str("cover") ?? meta.Poster;
        meta.Backdrop = info.Str("backdrop_path") ?? meta.Backdrop;
        meta.Plot = info.Str("plot") ?? meta.Plot;
        meta.Cast = info.Str("cast");
        meta.Director = info.Str("director");
        meta.Genre = info.Str("genre") ?? meta.Genre;
        meta.Year = XtreamClient.YearOf(info.Str("releaseDate")) ?? meta.Year;
        meta.Rating = info.Dbl("rating") is > 0 and var r ? r : meta.Rating;
        meta.Runtime = info.Str("episode_run_time") is { } rt ? rt + " min" : null;
        meta.Trailer = info.Str("youtube_trailer");

        var seasonMeta = new Dictionary<int, (string name, string? cover)>();
        foreach (var s in (root.Prop("seasons") ?? default).Items())
        {
            int n = (int)s.Long("season_number");
            seasonMeta[n] = (s.Str("name") ?? $"Season {n}", s.Str("cover_big") ?? s.Str("cover"));
        }

        var seasons = new Dictionary<int, Season>();
        var episodes = root.Prop("episodes");
        if (episodes is { } eps)
        {
            foreach (var group in eps.Items())
            {
                foreach (var e in group.Items())
                {
                    var ei = e.Prop("info") ?? default;
                    int sn = (int)e.Long("season");
                    if (!seasons.TryGetValue(sn, out var season))
                    {
                        var sm = seasonMeta.GetValueOrDefault(sn, ($"Season {sn}", null));
                        seasons[sn] = season = new Season { Number = sn, Name = sm.name, Cover = sm.cover };
                    }
                    season.Episodes.Add(new Episode
                    {
                        Id = e.Str("id") ?? "",
                        Season = sn,
                        Number = (int)e.Long("episode_num"),
                        Title = CleanEpisodeTitle(e.Str("title") ?? "", meta.Name),
                        Ext = e.Str("container_extension"),
                        Plot = ei.Str("plot"),
                        Image = ei.Str("movie_image"),
                        DurationSecs = (int)ei.Long("duration_secs"),
                        Rating = ei.Dbl("rating") is > 0 and var er ? er : null,
                        AirDate = ei.Str("releaseDate") ?? ei.Str("air_date")
                    });
                }
            }
        }
        foreach (var s in seasons.Values) s.Episodes.Sort((x, y) => x.Number.CompareTo(y.Number));
        meta.Seasons = seasons.Values.OrderBy(s => s.Number == 0 ? int.MaxValue : s.Number).ToList();
        return meta;
    }

    // "Show.Name - S01E01 - Title" becomes "Title"
    private static string CleanEpisodeTitle(string title, string show)
    {
        var m = System.Text.RegularExpressions.Regex.Match(title, @"S\d{1,2}\s*E\d{1,3}\s*-?\s*(.*)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success && m.Groups[1].Value.Trim().Length > 0) return m.Groups[1].Value.Trim();
        return title;
    }

    public static async Task<List<EpgEntry>> GetEpgAsync(Account a, string streamId, CancellationToken ct)
    {
        string key = $"{a.Id}:{streamId}";
        if (Epg.TryGetValue(key, out var c) && DateTimeOffset.UtcNow - c.At < TimeSpan.FromMinutes(10)
            && (c.Value.Count == 0 || c.Value[0].End > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
            return c.Value;
        if (a.Kind != AccountKind.Xtream) return new();

        using var doc = await XtreamClient.GetShortEpgAsync(a, streamId, 8, ct);
        var list = new List<EpgEntry>();
        foreach (var e in (doc.RootElement.Prop("epg_listings") ?? default).Items())
        {
            long start = e.Long("start_timestamp"), end = e.Long("stop_timestamp");
            if (start == 0 || end == 0) continue;
            list.Add(new EpgEntry
            {
                Title = B64(e.Str("title")) ?? "",
                Description = B64(e.Str("description")),
                Start = start * 1000,
                End = end * 1000
            });
        }
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        list = list.Where(x => x.End > now).OrderBy(x => x.Start).ToList();
        Epg[key] = new(list, DateTimeOffset.UtcNow);
        return list;
    }

    private static string? B64(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(s)).Trim(); }
        catch { return s; }
    }
}
