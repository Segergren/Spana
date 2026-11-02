using System.Collections.Concurrent;
using System.Globalization;
using System.Xml;
using Serilog;
using Spana.Backend.App;
using Spana.Backend.Core;

namespace Spana.Backend.Iptv;

public sealed class Programme
{
    public string EpgId { get; init; } = "";
    public long Start { get; init; }
    public long End { get; init; }
    public string Title { get; init; } = "";
    public string? Description { get; init; }
    // XMLTV categories joined with ", " ("Sports, Ice Hockey").
    public string? Category { get; init; }
    public string? Image { get; init; }

    private ProgramTitle? _parsed;
    public ProgramTitle Parsed => _parsed ??= new ProgramTitle(Title);
    private string? _norm;
    public string NormalizedTitle => _norm ??= CatalogService.Normalize(Title);
}

public sealed class Guide
{
    public DateTimeOffset FetchedAt { get; init; }
    public Dictionary<string, List<Programme>> ByChannel { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public List<Programme> All { get; init; } = new();
    // Channel logos from the guide, by guide channel id.
    public Dictionary<string, string> Logos { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    // Xtream: the provider's own guide, the public fallback merged into it, and the channel list that was matched.
    internal Guide? Provider { get; init; }
    internal Guide? Fallback { get; init; }
    internal object? Matched { get; init; }

    public Programme? Current(string? epgId, long now)
    {
        if (epgId == null || !ByChannel.TryGetValue(epgId, out var list)) return null;
        foreach (var p in list)
            if (p.Start <= now && p.End > now) return p;
        return null;
    }

    public IEnumerable<Programme> Upcoming(string? epgId, long now) =>
        epgId != null && ByChannel.TryGetValue(epgId, out var list) ? list.Where(p => p.End > now) : [];
}

// Full XMLTV guide per account, kept on disk and refreshed in the background.
public static class GuideService
{
    private static readonly ConcurrentDictionary<string, Guide> Guides = new();
    private static readonly ConcurrentDictionary<string, Lazy<Task>> Refreshing = new();
    private static readonly ConcurrentDictionary<string, Lazy<Task>> Merging = new();
    private static readonly ConcurrentDictionary<string, DateTimeOffset> RetryAt = new();
    private static readonly ConcurrentDictionary<string, object> LoadLocks = new();
    private static readonly ConcurrentDictionary<string, DateTimeOffset> MergeTried = new();
    private static readonly ConcurrentDictionary<string, (Guide guide, object index)> Applied = new();
    private static readonly ConcurrentDictionary<string, int> Stamps = new();
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(4);

    private static string FilePath(Account a) => Path.Combine(Storage.CacheRoot, a.Id, "xmltv.xml");

    // Changes whenever guide ids or logos were written into the channel list.
    public static int CatalogStamp(Account a) => Stamps.GetValueOrDefault(a.Id);

    public static Guide? Get(Account a)
    {
        if (!Guides.TryGetValue(a.Id, out var guide))
        {
            // Requests arriving together wait for one load instead of each parsing every file.
            lock (LoadLocks.GetOrAdd(a.Id, _ => new object()))
            {
                if (!Guides.TryGetValue(a.Id, out guide))
                {
                    guide = LoadFromDisk(a);
                    if (guide != null) Guides[a.Id] = guide;
                    // A playlist guide is matched against the channel list; wait for it instead of downloading again.
                    else if (a.Kind == AccountKind.M3u && PlaylistGuide.HasFiles(a) && CatalogService.GetIndex(a, MediaKind.Live) == null) return null;
                }
            }
        }
        long horizon = DateTimeOffset.UtcNow.AddHours(6).ToUnixTimeMilliseconds();
        bool stale = guide == null || DateTimeOffset.UtcNow - guide.FetchedAt > Ttl ||
                     (guide.All.Count > 0 && guide.All[^1].End < horizon);
        if (stale && DateTimeOffset.UtcNow >= RetryAt.GetValueOrDefault(a.Id)) StartRefresh(a);
        if (guide != null && Track(a, guide)) Notify(a);
        return guide;
    }

    public static void Warm(Account a) => _ = Task.Run(() => Get(a));

    public static void Refresh(Account a) => StartRefresh(a);

    private static void StartRefresh(Account a)
    {
        var job = new Lazy<Task>(() => Task.Run(() => RefreshAsync(a)));
        if (Refreshing.TryAdd(a.Id, job)) _ = job.Value;
    }

    public static void Forget(Account a) => Guides.TryRemove(a.Id, out _);

    private static void Notify(Account a)
    {
        _ = MessageHub.Send("guideUpdated", new { accountId = a.Id });
        FeedCache.Invalidate();
    }

    private static async Task RefreshAsync(Account a)
    {
        bool ok = false;
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (a.Kind == AccountKind.M3u)
            {
                using var pcts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                var built = await PlaylistGuide.RefreshAsync(a, pcts.Token);
                if (built == null) return;
                ok = true;
                Guides[a.Id] = built;
                Log.Information("Guide refreshed: {Count} programmes on {Channels} channels in {Ms} ms", built.All.Count, built.ByChannel.Count, sw.ElapsedMilliseconds);
                Track(a, built);
                Notify(a);
                return;
            }
            string url = $"{a.Server}/xmltv.php?username={Uri.EscapeDataString(a.Username)}&password={Uri.EscapeDataString(a.Password)}";
            Guide guide;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                var bytes = await XtreamClient.GetBytesAsync(url, cts.Token);
                string file = FilePath(a);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                await File.WriteAllBytesAsync(file + ".tmp", bytes);
                File.Move(file + ".tmp", file, true);
                guide = Parse(file, DateTimeOffset.UtcNow);
                ok = true;
            }
            catch (Exception ex) when (!Guides.ContainsKey(a.Id))
            {
                // Without any provider guide the public fallback still helps; the provider is asked again in half an hour.
                Log.Warning("Guide refresh failed: {Message}", ex.Message);
                guide = new Guide { FetchedAt = DateTimeOffset.UtcNow - Ttl + TimeSpan.FromMinutes(30) };
            }
            // The previous fallback stays until it is matched again, so those channels never go blank in between.
            var previous = Guides.GetValueOrDefault(a.Id)?.Fallback;
            Guides[a.Id] = previous == null ? guide : Merge(guide, previous, null);
            Log.Information("Guide refreshed: {Count} programmes on {Channels} channels in {Ms} ms", guide.All.Count, guide.ByChannel.Count, sw.ElapsedMilliseconds);
            Track(a, Guides[a.Id]);
            Notify(a);
        }
        catch (Exception ex)
        {
            Log.Warning("Guide refresh failed: {Message}", ex.Message);
        }
        finally
        {
            // A failed refresh waits half an hour instead of being retried on every request.
            if (ok) RetryAt.TryRemove(a.Id, out _);
            else RetryAt[a.Id] = DateTimeOffset.UtcNow.AddMinutes(30);
            Refreshing.TryRemove(a.Id, out _);
        }
    }

    // Keeps the Xtream fallback and the guide's ids and logos in step with the channel list. True when the channel list changed.
    private static bool Track(Account a, Guide guide)
    {
        var index = CatalogService.GetIndex(a, MediaKind.Live);
        if (index == null) return false;
        if (a.Kind == AccountKind.Xtream && guide.Matched != index) StartFallback(a, guide.Provider ?? guide);
        if (Applied.TryGetValue(a.Id, out var last) && last.guide == guide && last.index == index) return false;
        Applied[a.Id] = (guide, index);
        return Apply(a, guide, index.All);
    }

    // Channels without a logo get the guide's; Xtream channels without a guide id get the name key the fallback found them under.
    private static bool Apply(Account a, Guide guide, IReadOnlyList<CatalogItem> channels)
    {
        bool changed = false;
        foreach (var item in channels)
        {
            string? key = item.EpgId;
            if (key == null && a.Kind == AccountKind.Xtream && guide.Fallback != null)
            {
                string named = "n:" + CatalogService.Normalize(item.Name);
                if (guide.Fallback.ByChannel.ContainsKey(named))
                {
                    item.EpgId = key = named;
                    changed = true;
                }
            }
            if (key != null && item.Icon == null && guide.Logos.TryGetValue(key, out var logo))
            {
                item.Icon = logo;
                changed = true;
            }
        }
        if (changed) Stamps.AddOrUpdate(a.Id, 1, (_, v) => v + 1);
        return changed;
    }

    private static void StartFallback(Account a, Guide provider)
    {
        if (Merging.ContainsKey(a.Id) || (MergeTried.TryGetValue(a.Id, out var at) && DateTimeOffset.UtcNow - at < TimeSpan.FromMinutes(10))) return;
        var job = new Lazy<Task>(() => Task.Run(() => FallbackAsync(a, provider)));
        if (!Merging.TryAdd(a.Id, job)) return;
        MergeTried[a.Id] = DateTimeOffset.UtcNow;
        _ = job.Value;
    }

    private static async Task FallbackAsync(Account a, Guide provider)
    {
        bool failed = false;
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var index = CatalogService.GetIndex(a, MediaKind.Live);
            if (index == null) return;
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var extra = await PlaylistGuide.FallbackAsync(a, provider, index.All, cts.Token);
            var current = Guides.GetValueOrDefault(a.Id);
            // A newer provider guide came in meanwhile; it gets its own pass.
            if (current != provider && current?.Provider != provider) return;
            var merged = Merge(provider, extra, index);
            Guides[a.Id] = merged;
            Log.Information("Guide fallback: {Count} programmes on {Channels} channels in {Ms} ms", extra?.All.Count ?? 0, extra?.ByChannel.Count ?? 0, sw.ElapsedMilliseconds);
            Track(a, merged);
            Notify(a);
        }
        catch (Exception ex)
        {
            failed = true;
            Log.Warning("Guide fallback failed: {Message}", ex.Message);
        }
        finally
        {
            if (!failed) MergeTried.TryRemove(a.Id, out _);
            Merging.TryRemove(a.Id, out _);
        }
    }

    // The provider's programmes always win; the fallback only fills channels the provider has nothing for.
    private static Guide Merge(Guide provider, Guide? extra, object? matched)
    {
        if (extra == null || extra.ByChannel.Count == 0)
            return new Guide { FetchedAt = provider.FetchedAt, ByChannel = provider.ByChannel, All = provider.All, Logos = provider.Logos, Provider = provider, Matched = matched };
        var guide = new Guide
        {
            FetchedAt = provider.FetchedAt,
            ByChannel = new(provider.ByChannel, StringComparer.OrdinalIgnoreCase),
            All = new List<Programme>(provider.All.Count + extra.All.Count),
            Logos = new(provider.Logos, StringComparer.OrdinalIgnoreCase),
            Provider = provider,
            Fallback = extra,
            Matched = matched
        };
        guide.All.AddRange(provider.All);
        foreach (var (key, list) in extra.ByChannel)
            if (guide.ByChannel.TryAdd(key, list)) guide.All.AddRange(list);
        foreach (var (key, logo) in extra.Logos) guide.Logos.TryAdd(key, logo);
        guide.All.Sort((x, y) => x.End.CompareTo(y.End));
        return guide;
    }

    private static Guide? LoadFromDisk(Account a)
    {
        if (a.Kind == AccountKind.M3u)
        {
            try { return PlaylistGuide.Load(a); }
            catch (Exception ex)
            {
                Log.Warning(ex, "Discarding unreadable playlist guide");
                return null;
            }
        }
        string file = FilePath(a);
        if (!File.Exists(file)) return null;
        try
        {
            return Parse(file, new DateTimeOffset(File.GetLastWriteTimeUtc(file)));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Discarding unreadable guide cache");
            return null;
        }
    }

    private static Guide Parse(string file, DateTimeOffset fetchedAt)
    {
        var guide = new Guide { FetchedAt = fetchedAt };
        long cutoff = DateTimeOffset.UtcNow.AddHours(-12).ToUnixTimeMilliseconds();
        // Reruns repeat titles, descriptions and images; one copy of each is kept.
        var pool = new Dictionary<string, string>(StringComparer.Ordinal);
        string? Share(string? s) => s == null ? null : pool.TryGetValue(s, out var shared) ? shared : pool[s] = s;
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreWhitespace = true, IgnoreComments = true };
        using var reader = XmlReader.Create(file, settings);
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element) continue;
            if (reader.Name == "channel")
            {
                if (reader.GetAttribute("id") is { } id && ChannelIcon(reader) is { } icon) guide.Logos.TryAdd(id, icon);
                continue;
            }
            if (reader.Name != "programme") continue;
            string? channel = reader.GetAttribute("channel");
            long start = ParseTime(reader.GetAttribute("start"));
            long end = ParseTime(reader.GetAttribute("stop"));
            if (channel == null || start == 0 || end <= start || end < cutoff) continue;

            var (title, desc, category, image) = ReadDetails(reader);
            if (string.IsNullOrWhiteSpace(title)) continue;

            var p = new Programme
            {
                EpgId = channel, Start = start, End = end, Title = Share(title.Trim())!, Description = Share(desc?.Trim()), Category = category,
                Image = Share(PlaylistGuide.WebUrl(image))
            };
            if (!guide.ByChannel.TryGetValue(channel, out var list)) guide.ByChannel[channel] = list = new();
            list.Add(p);
            guide.All.Add(p);
        }
        foreach (var list in guide.ByChannel.Values) list.Sort((x, y) => x.Start.CompareTo(y.Start));
        guide.All.Sort((x, y) => x.End.CompareTo(y.End));
        return guide;
    }

    private static string? ChannelIcon(XmlReader reader)
    {
        using var sub = reader.ReadSubtree();
        while (sub.Read())
            if (sub.NodeType == XmlNodeType.Element && sub.Name == "icon")
                return PlaylistGuide.WebUrl(sub.GetAttribute("src"));
        return null;
    }

    // Reads title, description, categories and image of a <programme>; the reader must be on that element.
    internal static (string? title, string? desc, string? category, string? icon) ReadDetails(XmlReader reader)
    {
        string? title = null, desc = null, category = null, icon = null;
        using var sub = reader.ReadSubtree();
        sub.Read();
        while (!sub.EOF)
        {
            if (sub.NodeType != XmlNodeType.Element) { sub.Read(); continue; }
            if (sub.Name == "title" && title == null) title = sub.ReadElementContentAsString();
            else if (sub.Name == "desc" && desc == null) desc = sub.ReadElementContentAsString();
            else if (sub.Name == "category")
            {
                string c = sub.ReadElementContentAsString().Trim();
                if (c.Length > 0 && (category == null || !category.Contains(c, StringComparison.OrdinalIgnoreCase)))
                    category = category == null ? c : category + ", " + c;
            }
            else if (sub.Name == "icon")
            {
                icon ??= sub.GetAttribute("src");
                sub.Read();
            }
            else sub.Read();
        }
        return (title, desc, category == null ? null : string.Intern(category), icon);
    }

    // XMLTV times look like "20261101134500 +0000"
    internal static long ParseTime(string? s)
    {
        if (string.IsNullOrEmpty(s) || s.Length < 14) return 0;
        if (!DateTime.TryParseExact(s[..14], "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)) return 0;
        var offset = TimeSpan.Zero;
        var rest = s[14..].Trim();
        if (rest.Length == 5 && (rest[0] == '+' || rest[0] == '-') && int.TryParse(rest[1..3], out int h) && int.TryParse(rest[3..5], out int m))
            offset = new TimeSpan(rest[0] == '-' ? -h : h, rest[0] == '-' ? -m : m, 0);
        return new DateTimeOffset(dt, offset).ToUnixTimeMilliseconds();
    }
}
