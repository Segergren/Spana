using Spana.Backend.Iptv;

namespace Spana.Backend.Core;

public static class LibraryService
{
    private const int MaxHistory = 200;
    private static readonly object Gate = new();
    private static Library _library = new();
    private static string? _accountId;

    private static string PathFor(string accountId) => Path.Combine(Storage.Root, "library", accountId + ".json");

    private static Library ForAccount(string accountId)
    {
        if (_accountId != accountId)
        {
            _library = Storage.Load<Library>(PathFor(accountId));
            _accountId = accountId;
        }
        return _library;
    }

    public static string KeyFor(MediaKind kind, string id) => $"{kind}:{id}".ToLowerInvariant();

    public static Library Get(string accountId)
    {
        lock (Gate)
        {
            var lib = ForAccount(accountId);
            return new Library { Favorites = lib.Favorites.ToList(), History = lib.History.ToList(), LiveWatches = lib.LiveWatches.ToList() };
        }
    }

    public static bool ToggleFavorite(string accountId, LibraryEntry entry)
    {
        lock (Gate)
        {
            var lib = ForAccount(accountId);
            entry.Key = KeyFor(entry.Kind, entry.Id);
            int removed = lib.Favorites.RemoveAll(f => f.Key == entry.Key);
            if (removed == 0)
            {
                entry.UpdatedAt = DateTimeOffset.UtcNow;
                lib.Favorites.Insert(0, entry);
            }
            Storage.Save(PathFor(accountId), lib);
            FeedCache.Invalidate();
            return removed == 0;
        }
    }

    public static void RecordPlayback(string accountId, LibraryEntry entry)
    {
        lock (Gate)
        {
            var lib = ForAccount(accountId);
            entry.Key = KeyFor(entry.Kind, entry.Id);
            var existing = lib.History.FirstOrDefault(h => h.Key == entry.Key);
            if (existing != null)
            {
                lib.History.Remove(existing);
                if (entry.PositionMs == 0 && entry.DurationMs == 0)
                {
                    entry.PositionMs = existing.PositionMs;
                    entry.DurationMs = existing.DurationMs;
                }
            }
            entry.UpdatedAt = DateTimeOffset.UtcNow;
            lib.History.Insert(0, entry);
            if (lib.History.Count > MaxHistory)
                lib.History.RemoveRange(MaxHistory, lib.History.Count - MaxHistory);
            Storage.Save(PathFor(accountId), lib);
        }
    }

    public static void UpdateProgress(string accountId, MediaKind kind, string id, long positionMs, long durationMs, bool persist)
    {
        lock (Gate)
        {
            var lib = ForAccount(accountId);
            var entry = lib.History.FirstOrDefault(h => h.Key == KeyFor(kind, id));
            if (entry == null) return;
            entry.PositionMs = positionMs;
            entry.DurationMs = durationMs;
            entry.UpdatedAt = DateTimeOffset.UtcNow;
            if (persist) Storage.Save(PathFor(accountId), lib);
        }
    }

    public static void RemoveHistory(string accountId, string key)
    {
        lock (Gate)
        {
            var lib = ForAccount(accountId);
            lib.History.RemoveAll(h => h.Key == key);
            Storage.Save(PathFor(accountId), lib);
        }
    }

    public static void AddLiveWatch(string accountId, string channelId, string channelName, string? epgId, string? title, double minutes,
        MatchContext? ctx = null, string? sport = null)
    {
        lock (Gate)
        {
            var lib = ForAccount(accountId);
            var w = lib.LiveWatches.FirstOrDefault(x => x.ChannelId == channelId && x.Title == title);
            if (w == null)
            {
                w = new LiveWatch { ChannelId = channelId, ChannelName = channelName, EpgId = epgId, Title = title };
                lib.LiveWatches.Add(w);
            }
            w.Minutes += minutes;
            w.LastWatched = DateTimeOffset.UtcNow;
            if (ctx is { } c)
            {
                w.League ??= c.League;
                w.LeagueName ??= c.LeagueName;
                w.Country ??= c.Country;
                w.HomeId ??= c.HomeId;
                w.AwayId ??= c.AwayId;
            }
            w.Sport ??= sport ?? ctx?.Sport;
            if (w.Minutes >= 3 && w.Minutes - minutes < 3) FeedCache.Invalidate();
            if (lib.LiveWatches.Count > 600)
                lib.LiveWatches = lib.LiveWatches.OrderByDescending(x => x.LastWatched).Take(500).ToList();
            Storage.Save(PathFor(accountId), lib);
        }
    }

    public static List<LiveWatch> LiveWatches(string accountId)
    {
        lock (Gate)
        {
            return ForAccount(accountId).LiveWatches.ToList();
        }
    }

    public static void ClearLiveWatches(string accountId)
    {
        lock (Gate)
        {
            var lib = ForAccount(accountId);
            lib.LiveWatches.Clear();
            Storage.Save(PathFor(accountId), lib);
            FeedCache.Invalidate();
        }
    }

    public static LibraryEntry? FindHistory(string accountId, MediaKind kind, string id)
    {
        lock (Gate)
        {
            return ForAccount(accountId).History.FirstOrDefault(h => h.Key == KeyFor(kind, id));
        }
    }
}
