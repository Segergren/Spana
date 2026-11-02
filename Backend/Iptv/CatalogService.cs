using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Serilog;
using Spana.Backend.App;
using Spana.Backend.Core;

namespace Spana.Backend.Iptv;

public sealed class CatalogSnapshot
{
    public DateTimeOffset FetchedAt { get; set; }
    public List<Category> Categories { get; set; } = new();
    public List<CatalogItem> Items { get; set; } = new();
}

public sealed class CatalogStatus
{
    public MediaKind Kind { get; set; }
    public bool Loading { get; set; }
    public int Count { get; set; }
    public DateTimeOffset? FetchedAt { get; set; }
    public string? Error { get; set; }
}

public static class CatalogService
{
    private static readonly MediaKind[] Kinds = [MediaKind.Live, MediaKind.Movie, MediaKind.Series];
    private static readonly ConcurrentDictionary<string, CatalogSnapshot> Snapshots = new();
    private static readonly ConcurrentDictionary<string, SearchIndex> Indexes = new();
    private static readonly ConcurrentDictionary<string, Task> Refreshing = new();
    private static readonly ConcurrentDictionary<string, CatalogStatus> Statuses = new();
    private static readonly SemaphoreSlim DiskGate = new(1, 1);

    private static string Key(Account a, MediaKind k) => $"{a.Id}:{k}";
    private static string CacheDir(Account a) => Path.Combine(Storage.CacheRoot, a.Id);
    private static string CacheFile(Account a, MediaKind k) => Path.Combine(CacheDir(a), k.ToString().ToLowerInvariant() + ".json");

    private static TimeSpan Ttl(MediaKind k) =>
        TimeSpan.FromHours(k == MediaKind.Live ? SettingsService.Current.LiveCacheHours : SettingsService.Current.VodCacheHours);

    public static IEnumerable<CatalogStatus> GetStatus(Account a) =>
        EnabledKinds.Select(k => Statuses.GetOrAdd(Key(a, k), _ => new CatalogStatus { Kind = k }));

    // Loads every catalog from disk right away and refreshes stale ones in the background.
    private static IEnumerable<MediaKind> EnabledKinds =>
        SettingsService.Current.ShowVod ? Kinds : [MediaKind.Live];

    public static void Warm(Account a)
    {
        foreach (var k in EnabledKinds)
            _ = Task.Run(() => GetAsync(a, k));
    }

    public static Dictionary<string, string> CategoryNames(Account a, MediaKind kind) =>
        Snapshots.TryGetValue(Key(a, kind), out var snap)
            ? snap.Categories.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First().Name)
            : new();

    public static async Task<CatalogSnapshot> GetAsync(Account a, MediaKind kind, bool force = false)
    {
        string key = Key(a, kind);
        if (!Snapshots.TryGetValue(key, out var snap))
        {
            snap = await LoadFromDiskAsync(a, kind);
            if (snap != null) SetSnapshot(a, kind, snap);
        }

        bool stale = snap == null || DateTimeOffset.UtcNow - snap.FetchedAt > Ttl(kind);
        if (force || stale)
        {
            var refresh = Refreshing.GetOrAdd(key, _ => Task.Run(() => RefreshAsync(a, kind)));
            if (snap == null)
            {
                await refresh;
                Snapshots.TryGetValue(key, out snap);
            }
        }
        return snap ?? new CatalogSnapshot();
    }

    public static SearchIndex? GetIndex(Account a, MediaKind kind) => Indexes.GetValueOrDefault(Key(a, kind));

    public static CatalogItem? Find(Account a, MediaKind kind, string id) =>
        Indexes.GetValueOrDefault(Key(a, kind))?.Find(id);

    public static void RefreshAll(Account a)
    {
        foreach (var k in EnabledKinds)
        {
            string key = Key(a, k);
            Refreshing.GetOrAdd(key, _ => Task.Run(() => RefreshAsync(a, k)));
        }
    }

    public static void Forget(Account a)
    {
        foreach (var k in Kinds)
        {
            Snapshots.TryRemove(Key(a, k), out _);
            Indexes.TryRemove(Key(a, k), out _);
            Statuses.TryRemove(Key(a, k), out _);
        }
        try { Directory.Delete(CacheDir(a), true); } catch { }
    }

    private static void SetSnapshot(Account a, MediaKind kind, CatalogSnapshot snap)
    {
        // Provider names become what people call the channel; lists saved before that get the same treatment.
        if (kind == MediaKind.Live)
        {
            foreach (var item in snap.Items)
            {
                ChannelNames.Apply(item);
                if (a.Kind == AccountKind.M3u && string.IsNullOrEmpty(item.EpgId)) item.EpgId = "n:" + Normalize(item.Name);
            }
            if (a.Kind == AccountKind.M3u) GuideService.Forget(a);
        }
        foreach (var c in snap.Categories)
            c.Count = 0;
        var byId = snap.Categories.ToDictionary(c => c.Id, c => c, StringComparer.Ordinal);
        foreach (var item in snap.Items)
            if (byId.TryGetValue(item.CategoryId, out var c)) c.Count++;

        Snapshots[Key(a, kind)] = snap;
        Indexes[Key(a, kind)] = new SearchIndex(kind, snap.Items);
        var status = Statuses.GetOrAdd(Key(a, kind), _ => new CatalogStatus { Kind = kind });
        status.Count = snap.Items.Count;
        status.FetchedAt = snap.FetchedAt;
    }

    private static async Task RefreshAsync(Account a, MediaKind kind)
    {
        string key = Key(a, kind);
        var status = Statuses.GetOrAdd(key, _ => new CatalogStatus { Kind = kind });
        status.Loading = true;
        status.Error = null;
        Broadcast(a);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            CatalogSnapshot snap;
            if (a.Kind == AccountKind.Xtream)
            {
                var cats = XtreamClient.GetCategoriesAsync(a, kind, cts.Token);
                var items = XtreamClient.GetItemsAsync(a, kind, cts.Token);
                snap = new CatalogSnapshot { FetchedAt = DateTimeOffset.UtcNow, Categories = await cats, Items = await items };
            }
            else
            {
                var parsed = await M3uCache.GetAsync(a, cts.Token);
                snap = new CatalogSnapshot
                {
                    FetchedAt = DateTimeOffset.UtcNow,
                    Categories = parsed.Categories[kind],
                    Items = parsed.Items[kind]
                };
            }

            // Orphaned items keep a home so the category view never hides them.
            var known = snap.Categories.Select(c => c.Id).ToHashSet();
            if (snap.Items.Any(i => !known.Contains(i.CategoryId)))
            {
                snap.Categories.Add(new Category { Id = "_other", Name = "Other" });
                foreach (var i in snap.Items.Where(i => !known.Contains(i.CategoryId))) i.CategoryId = "_other";
            }

            SetSnapshot(a, kind, snap);
            await SaveToDiskAsync(a, kind, snap);
            Log.Information("Refreshed {Kind} catalog for {Account}: {Count} items in {Ms} ms", kind, a.Name, snap.Items.Count, sw.ElapsedMilliseconds);
            FeedCache.Invalidate();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to refresh {Kind} catalog", kind);
            status.Error = ex.Message;
        }
        finally
        {
            status.Loading = false;
            Refreshing.TryRemove(key, out _);
            Broadcast(a);
        }
    }

    private static void Broadcast(Account a) =>
        _ = MessageHub.Send("catalogStatus", new { accountId = a.Id, catalogs = GetStatus(a) });

    private static async Task<CatalogSnapshot?> LoadFromDiskAsync(Account a, MediaKind kind)
    {
        string file = CacheFile(a, kind);
        if (!File.Exists(file)) return null;
        try
        {
            await using var fs = File.OpenRead(file);
            return await JsonSerializer.DeserializeAsync<CatalogSnapshot>(fs, Storage.Json);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Discarding unreadable catalog cache {File}", file);
            return null;
        }
    }

    private static async Task SaveToDiskAsync(Account a, MediaKind kind, CatalogSnapshot snap)
    {
        await DiskGate.WaitAsync();
        try
        {
            Directory.CreateDirectory(CacheDir(a));
            string file = CacheFile(a, kind), tmp = file + ".tmp";
            await using (var fs = File.Create(tmp))
                await JsonSerializer.SerializeAsync(fs, snap, Storage.Json);
            File.Move(tmp, file, true);
        }
        finally
        {
            DiskGate.Release();
        }
    }

    public static string Normalize(string s)
    {
        var formD = s.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(formD.Length);
        foreach (char c in formD)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}

internal static class M3uCache
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static (string url, DateTimeOffset at, M3uParser.Result result)? _last;

    public static async Task<M3uParser.Result> GetAsync(Account a, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            if (_last is { } l && l.url == a.M3uUrl && DateTimeOffset.UtcNow - l.at < TimeSpan.FromMinutes(5))
                return l.result;
            var result = M3uParser.Parse(await XtreamClient.GetBytesAsync(a.M3uUrl, ct));
            _last = (a.M3uUrl, DateTimeOffset.UtcNow, result);
            return result;
        }
        finally
        {
            Gate.Release();
        }
    }
}
