using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Serilog;
using Spana.Backend.Api;
using Spana.Backend.Core;

namespace Spana.Backend.Iptv;

public sealed class ProgramArt
{
    public string Title { get; set; } = "";
    public long Start { get; set; }
    public long End { get; set; }
    public string? Image { get; set; }
}

public sealed class ChannelArt
{
    public ProgramArt? Now { get; set; }
    public ProgramArt? Next { get; set; }
}

// Images for what is on now and next: the guide service for the Swedish channels it covers, the guide's own programme images otherwise.
public static class ChannelArtService
{
    private sealed record Broadcast(string Id, string Title, long Start, long End);
    private sealed record Schedule(DateTimeOffset At, List<Broadcast> Items);

    private static readonly TimeZoneInfo Stockholm = FindZone();
    private static readonly ConcurrentDictionary<string, string?> Slugs = new();          // channel key -> source slug (null = none)
    private static readonly ConcurrentDictionary<string, Schedule> Schedules = new();     // slug|date -> broadcasts
    private static readonly ConcurrentDictionary<string, string?> Images = new();         // broadcast id -> image url
    private static readonly ConcurrentDictionary<string, Lazy<Task>> InFlight = new();
    private static readonly SemaphoreSlim Gate = new(4, 4);
    private static readonly string CacheFile = Path.Combine(Storage.CacheRoot, "channel-art.json");
    private static int _loaded;

    private sealed class DiskCache
    {
        public Dictionary<string, string?> Slugs { get; set; } = new();
        public Dictionary<string, string?> Images { get; set; } = new();
    }

    private static TimeZoneInfo FindZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm"); }
        catch { return TimeZoneInfo.Local; }
    }

    private static string Today(long ms) =>
        TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(ms), Stockholm).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static async Task<ChannelArt> GetAsync(Account account, CatalogItem channel)
    {
        var art = await FromTvNuAsync(channel);
        FromGuide(account, channel, art);
        ImageCache.Prefetch([art.Now?.Image, art.Next?.Image]);
        return art;
    }

    private static async Task<ChannelArt> FromTvNuAsync(CatalogItem channel)
    {
        Load();
        if (!MatchDataService.Enabled || !MatchDataService.OnTvNu(channel)) return new ChannelArt();
        string key = SportService.ChannelKey(channel.Name);
        string? slug = await ResolveSlugAsync(key);
        if (slug == null) return new ChannelArt();

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var schedule = await ScheduleAsync(slug, Today(now));
        if (schedule == null) return new ChannelArt();

        var current = schedule.Items.FirstOrDefault(b => b.Start <= now && b.End > now);
        var next = schedule.Items.FirstOrDefault(b => b.Start >= (current?.End ?? now));
        if (next == null && current != null)
            next = (await ScheduleAsync(slug, Today(current.End + 60_000)))?.Items.FirstOrDefault(b => b.Start >= current.End);

        return new ChannelArt
        {
            Now = current == null ? null : new ProgramArt { Title = current.Title, Start = current.Start, End = current.End, Image = await ImageAsync(current.Id) },
            Next = next == null ? null : new ProgramArt { Title = next.Title, Start = next.Start, End = next.End, Image = await ImageAsync(next.Id) }
        };
    }

    // Fills what the source data left open from the account's guide: whole slots, or just the image of a slot that overlaps.
    private static void FromGuide(Account account, CatalogItem channel, ChannelArt art)
    {
        if (channel.EpgId == null || (art.Now?.Image != null && art.Next?.Image != null)) return;
        var guide = GuideService.Get(account);
        if (guide == null || !guide.ByChannel.TryGetValue(channel.EpgId, out var list)) return;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        int i = list.FindIndex(p => p.End > now);
        if (i < 0) return;
        var cur = list[i].Start <= now ? list[i] : null;
        var next = cur != null ? list.ElementAtOrDefault(i + 1) : list[i];
        var channels = SuggestionService.StreamsByEpg(account).GetValueOrDefault(channel.EpgId) ?? [channel];
        art.Now = Fill(art.Now, cur, channels);
        art.Next = Fill(art.Next, next, channels);
    }

    private static ProgramArt? Fill(ProgramArt? slot, Programme? p, List<CatalogItem> channels)
    {
        if (p == null) return slot;
        if (slot == null) return new ProgramArt { Title = SportService.ResolveTitle(p, channels), Start = p.Start, End = p.End, Image = p.Image };
        if (slot.Image == null && p.Start < slot.End && p.End > slot.Start) slot.Image = p.Image;
        return slot;
    }

    // Source ids are lower case names with dashes ("kanal-5", "tv4-fotboll"); names seen in the source data are used first.
    private static async Task<string?> ResolveSlugAsync(string key)
    {
        if (Slugs.TryGetValue(key, out var known)) return known;
        var fromData = MatchDataService.TvNuChannelSlug(key);
        var candidates = new List<string>();
        if (fromData != null) candidates.Add(fromData);
        string guess = key.Replace(' ', '-');
        if (!candidates.Contains(guess)) candidates.Add(guess);

        foreach (var slug in candidates)
        {
            if (await ScheduleAsync(slug, Today(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())) != null)
            {
                Slugs[key] = slug;
                Save();
                return slug;
            }
        }
        Slugs[key] = null;
        Save();
        return null;
    }

    private static async Task<Schedule?> ScheduleAsync(string slug, string date)
    {
        string cacheKey = slug + "|" + date;
        if (Schedules.TryGetValue(cacheKey, out var s) && DateTimeOffset.UtcNow - s.At < TimeSpan.FromMinutes(30)) return s;
        await Once(cacheKey, async () =>
        {
            try
            {
                using var doc = await GetJson($"{SpanaApi.Base}/channels/{Uri.EscapeDataString(slug)}/schedule?date={date}");
                var items = new List<Broadcast>();
                foreach (var b in (doc.RootElement.Prop("data")?.Prop("broadcasts") ?? default).Items())
                {
                    var times = b.Prop("broadcast");
                    string? id = times?.Str("id") ?? b.Str("id");
                    long start = times?.Long("startTime") ?? 0, end = times?.Long("endTime") ?? 0;
                    if (id != null && start > 0 && end > start) items.Add(new Broadcast(id, b.Str("title") ?? "", start, end));
                }
                Schedules[cacheKey] = new Schedule(DateTimeOffset.UtcNow, items.OrderBy(i => i.Start).ToList());
            }
            catch (HttpRequestException)
            {
                // Unknown channel id; remembered as missing by the caller.
            }
            catch (Exception ex)
            {
                Log.Debug("guide schedule failed for {Slug}: {Message}", slug, ex.Message);
            }
        });
        return Schedules.TryGetValue(cacheKey, out s) ? s : null;
    }

    private static async Task<string?> ImageAsync(string broadcastId)
    {
        if (Images.TryGetValue(broadcastId, out var url)) return url;
        await Once("img|" + broadcastId, async () =>
        {
            try
            {
                using var doc = await GetJson($"{SpanaApi.Base}/programmes/{Uri.EscapeDataString(broadcastId)}");
                var data = doc.RootElement.Prop("data") ?? doc.RootElement;
                string? image = data.Str("imageLandscape") ?? data.Str("imagePortrait") ?? data.Str("imageSquare");
                Images[broadcastId] = image != null ? SpanaApi.Image(image + "?width=640&height=360&forceFit=1&quality=85") : null;
                Save();
            }
            catch (Exception ex)
            {
                Log.Debug("guide details failed for {Id}: {Message}", broadcastId, ex.Message);
            }
        });
        return Images.GetValueOrDefault(broadcastId);
    }

    private static async Task Once(string key, Func<Task> work)
    {
        var task = InFlight.GetOrAdd(key, _ => new Lazy<Task>(async () =>
        {
            await Gate.WaitAsync();
            try { await work(); }
            finally { Gate.Release(); }
        }));
        try { await task.Value; }
        finally { InFlight.TryRemove(key, out _); }
    }

    private static async Task<JsonDocument> GetJson(string url)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", SettingsService.Current.UserAgent);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var res = await XtreamClient.Client.SendAsync(req, cts.Token);
        res.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await res.Content.ReadAsByteArrayAsync(cts.Token));
    }

    private static void Load()
    {
        if (Interlocked.Exchange(ref _loaded, 1) == 1) return;
        try
        {
            if (!File.Exists(CacheFile)) return;
            var cache = JsonSerializer.Deserialize<DiskCache>(File.ReadAllText(CacheFile), Storage.Json);
            if (cache == null) return;
            foreach (var (k, v) in cache.Slugs) Slugs[k] = v;
            foreach (var (k, v) in cache.Images) Images[k] = v;
        }
        catch (Exception ex)
        {
            Log.Warning("Could not read channel art cache: {Message}", ex.Message);
        }
    }

    private static int _saveScheduled;

    private static void Save()
    {
        if (Interlocked.Exchange(ref _saveScheduled, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(3000);
            Interlocked.Exchange(ref _saveScheduled, 0);
            try
            {
                // Images only for recent broadcasts are worth keeping; the file stays small.
                var images = Images.TakeLast(3000).ToDictionary(k => k.Key, v => v.Value);
                Storage.Save(CacheFile, new DiskCache { Slugs = Slugs.ToDictionary(k => k.Key, v => v.Value), Images = images });
            }
            catch (Exception ex)
            {
                Log.Debug("Could not save channel art cache: {Message}", ex.Message);
            }
        });
    }
}
