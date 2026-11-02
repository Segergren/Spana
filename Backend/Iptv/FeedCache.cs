using Serilog;
using Spana.Backend.Api;
using Spana.Backend.Core;

namespace Spana.Backend.Iptv;

// Keeps the sport feed and suggestions computed ahead of time and warms the image cache for everything they show.
public static class FeedCache
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(60);
    private static long _version;
    private static int _scheduled;
    private static readonly object Gate = new();
    private static (string key, long version, DateTimeOffset at, List<SportEvent> value)? _sport;
    private static (string key, long version, DateTimeOffset at, List<Suggestion> value)? _suggestions;

    // Called whenever the inputs change: guide, match data, viewing history, settings or the active account.
    public static void Invalidate()
    {
        Interlocked.Increment(ref _version);
        if (Interlocked.Exchange(ref _scheduled, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500);
            Interlocked.Exchange(ref _scheduled, 0);
            Warm();
        });
    }

    // The first calculation waits until match data and channels are loaded, so it never shows a half-built order.
    public static async Task WaitUntilReady(Account a)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(8);
        await Task.WhenAny(MatchDataService.Ready, Task.Delay(TimeSpan.FromSeconds(8)));
        while (CatalogService.GetIndex(a, MediaKind.Live) == null && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(100);
    }

    public static void Warm()
    {
        if (SettingsService.ActiveAccount is not { } account) return;
        try
        {
            WaitUntilReady(account).GetAwaiter().GetResult();
            var sport = Sport(account);
            var suggestions = Suggestions(account);
            Prefetch(account, sport, suggestions);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Feed warm-up failed");
        }
    }

    private static bool Fresh(string key, long version, DateTimeOffset at, string wantKey) =>
        key == wantKey && version == Interlocked.Read(ref _version) && DateTimeOffset.UtcNow - at < MaxAge;

    public static List<SportEvent> Sport(Account a)
    {
        lock (Gate)
        {
            if (_sport is { } c && Fresh(c.key, c.version, c.at, a.Id)) return c.value;
        }
        long version = Interlocked.Read(ref _version);
        var value = SportService.Get(a);
        lock (Gate) _sport = (a.Id, version, DateTimeOffset.UtcNow, value);
        return value;
    }

    public static List<Suggestion> Suggestions(Account a)
    {
        lock (Gate)
        {
            if (_suggestions is { } c && Fresh(c.key, c.version, c.at, a.Id)) return c.value;
        }
        long version = Interlocked.Read(ref _version);
        var value = SuggestionService.Get(a, 24, null);
        lock (Gate) _suggestions = (a.Id, version, DateTimeOffset.UtcNow, value);
        return value;
    }

    private static void Prefetch(Account a, List<SportEvent> sport, List<Suggestion> suggestions)
    {
        var urls = new List<string?>();
        long soon = DateTimeOffset.UtcNow.AddHours(48).ToUnixTimeMilliseconds();
        foreach (var s in suggestions)
            urls.AddRange([s.Info?.Image, s.Info?.HomeLogo, s.Info?.AwayLogo, s.Channel.IconAlt ?? s.Channel.Icon]);
        foreach (var e in sport.Where(e => e.Program.Start < soon).OrderByDescending(e => e.Relevance))
            urls.AddRange([e.Info?.Image, e.Info?.HomeLogo, e.Info?.AwayLogo, e.Channel.IconAlt ?? e.Channel.Icon]);

        var lib = LibraryService.Get(a.Id);
        foreach (var entry in lib.Favorites.Concat(lib.History).Where(x => x.Kind == MediaKind.Live))
        {
            var item = CatalogService.Find(a, MediaKind.Live, entry.Id);
            urls.Add(item?.IconAlt ?? entry.Icon);
            urls.Add(item?.Icon);
        }
        ImageCache.Prefetch(urls);
    }
}
