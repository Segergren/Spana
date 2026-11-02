using System.Net;
using System.Text.Json;
using Spana.Backend.App;
using Spana.Backend.Cast;
using Spana.Backend.Core;
using Spana.Backend.Iptv;
using Spana.Backend.Player;

namespace Spana.Backend.Api;

internal static class Routes
{
    private sealed class AddAccountBody
    {
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
        public string? Username { get; set; }
        public string? Password { get; set; }
    }

    private sealed class PlayerBody
    {
        public string Action { get; set; } = "";
        public PlayRequest? Item { get; set; }
        public long Ms { get; set; }
        public int Value { get; set; }
        public float Rate { get; set; }
        public bool Enabled { get; set; }
    }

    private sealed class ForgetBody
    {
        public List<string> Keys { get; set; } = new();
    }

    private sealed class CastBody
    {
        public string Action { get; set; } = "";
        public string? Id { get; set; }
    }

    public static async Task Dispatch(HttpListenerRequest req, HttpListenerResponse res, string path)
    {
        var parts = path.Trim('/').Split('/').Skip(1).Select(Uri.UnescapeDataString).ToArray();
        string method = req.HttpMethod;
        string head = parts.ElementAtOrDefault(0) ?? "";

        switch (head)
        {
            case "bootstrap" when method == "GET":
                await ApiServer.WriteJson(res, Bootstrap());
                return;

            case "accounts" when method == "POST" && parts.Length == 1:
                await ApiServer.WriteJson(res, await AddAccount(await ApiServer.ReadJson<AddAccountBody>(req)));
                return;

            case "accounts" when method == "POST" && parts.ElementAtOrDefault(2) == "activate":
                Activate(parts[1]);
                await ApiServer.WriteJson(res, Bootstrap());
                return;

            case "accounts" when method == "DELETE" && parts.Length == 2:
                RemoveAccount(parts[1]);
                await ApiServer.WriteJson(res, Bootstrap());
                return;

            case "settings" when method == "POST":
                UpdateSettings(await ApiServer.ReadJson<Dictionary<string, JsonElement>>(req));
                await ApiServer.WriteJson(res, Bootstrap());
                return;

            case "catalog" when method == "GET" && parts.Length == 2:
                await Catalog(req, res, ParseKind(parts[1]));
                return;

            case "refresh" when method == "POST":
                CatalogService.RefreshAll(RequireAccount());
                GuideService.Refresh(RequireAccount());
                await ApiServer.WriteJson(res, new { ok = true });
                return;

            case "hidden-preview" when method == "GET":
                await ApiServer.WriteJson(res, HiddenPreview(req.QueryString.GetValues("p") ?? []));
                return;

            case "search" when method == "GET":
                await ApiServer.WriteJson(res, Search(req.QueryString["q"] ?? "", int.TryParse(req.QueryString["limit"], out var l) ? l : 40));
                return;

            case "meta" when method == "GET" && parts.Length == 3:
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await ApiServer.WriteJson(res, await DetailService.GetMetaAsync(RequireAccount(), ParseKind(parts[1]), parts[2], cts.Token));
                    return;
                }

            case "channel-art" when method == "GET" && parts.Length == 2:
                {
                    var account = RequireAccount();
                    var channel = CatalogService.Find(account, MediaKind.Live, parts[1]) ?? throw new ApiError(404, "Unknown channel");
                    await ApiServer.WriteJson(res, await ChannelArtService.GetAsync(account, channel));
                    return;
                }

            case "guide" when method == "GET" && parts.ElementAtOrDefault(1) == "now":
                await ApiServer.WriteJson(res, GuideNow(RequireAccount()));
                return;

            case "sport" when method == "GET":
                await FeedCache.WaitUntilReady(RequireAccount());
                await ApiServer.WriteJson(res, FeedCache.Sport(RequireAccount()));
                return;

            case "suggestions" when method == "GET":
                {
                    int sl = int.TryParse(req.QueryString["limit"], out var l2) ? l2 : 20;
                    await FeedCache.WaitUntilReady(RequireAccount());
                    var cached = FeedCache.Suggestions(RequireAccount());
                    await ApiServer.WriteJson(res, cached.Take(sl));
                }
                return;

            case "interests" when method == "GET":
                await ApiServer.WriteJson(res, SuggestionService.Interests(RequireAccount().Id));
                return;

            case "interests" when method == "POST" && parts.ElementAtOrDefault(1) == "forget":
                {
                    var account = RequireAccount();
                    LibraryService.Forget(account.Id, (await ApiServer.ReadJson<ForgetBody>(req)).Keys);
                    await ApiServer.WriteJson(res, SuggestionService.Interests(account.Id));
                }
                return;

            case "watchprofile" when method == "DELETE":
                LibraryService.ClearLiveWatches(RequireAccount().Id);
                await ApiServer.WriteJson(res, new { ok = true });
                return;

            case "epg" when method == "GET" && parts.Length == 2:
                {
                    var account = RequireAccount();
                    var fromGuide = GuideEntries(account, parts[1]);
                    if (fromGuide != null)
                    {
                        await ApiServer.WriteJson(res, fromGuide);
                        return;
                    }
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    List<EpgEntry> epg;
                    try { epg = await DetailService.GetEpgAsync(account, parts[1], cts.Token); }
                    catch (Exception) when (!cts.IsCancellationRequested) { epg = new(); }
                    await ApiServer.WriteJson(res, epg);
                    return;
                }

            case "library" when method == "GET":
                await ApiServer.WriteJson(res, LibraryService.Get(RequireAccount().Id));
                return;

            case "favorite" when method == "POST":
                {
                    var entry = await ApiServer.ReadJson<LibraryEntry>(req);
                    bool added = LibraryService.ToggleFavorite(RequireAccount().Id, entry);
                    await ApiServer.WriteJson(res, new { favorite = added });
                    return;
                }

            case "history" when method == "DELETE" && parts.Length == 2:
                LibraryService.RemoveHistory(RequireAccount().Id, parts[1]);
                await ApiServer.WriteJson(res, new { ok = true });
                return;

            case "cache" when method == "POST" && parts.ElementAtOrDefault(1) == "clear":
                ImageCache.Clear();
                await ApiServer.WriteJson(res, new { ok = true });
                return;

            case "cache" when method == "GET":
                await ApiServer.WriteJson(res, new { imageBytes = ImageCache.SizeBytes() });
                return;

            case "player" when method == "GET":
                await ApiServer.WriteJson(res, PlayerService.GetState());
                return;

            case "player" when method == "POST":
                Player(await ApiServer.ReadJson<PlayerBody>(req));
                await ApiServer.WriteJson(res, new { ok = true });
                return;

            case "update" when method == "GET":
                await ApiServer.WriteJson(res, UpdateService.GetState());
                return;

            case "update" when method == "POST":
                UpdateService.Apply();
                await ApiServer.WriteJson(res, new { ok = true });
                return;

            case "cast" when method == "GET":
                CastService.Scan();
                await ApiServer.WriteJson(res, CastService.GetState());
                return;

            case "cast" when method == "POST":
                await Cast(await ApiServer.ReadJson<CastBody>(req));
                await ApiServer.WriteJson(res, CastService.GetState());
                return;
        }

        throw new ApiError(404, "Not found");
    }

    private static MediaKind ParseKind(string s) => s.ToLowerInvariant() switch
    {
        "live" => MediaKind.Live,
        "movie" => MediaKind.Movie,
        "series" => MediaKind.Series,
        "episode" => MediaKind.Episode,
        _ => throw new ApiError(400, "Unknown kind " + s)
    };

    private static Account RequireAccount() => SettingsService.ActiveAccount ?? throw new ApiError(409, "No account configured");

    private static object Bootstrap()
    {
        var s = SettingsService.Current;
        var active = SettingsService.ActiveAccount;
        return new
        {
            version = UpdateService.GetState().Current is { Length: > 0 } v ? v : typeof(Routes).Assembly.GetName().Version?.ToString(3),
            activeAccountId = active?.Id,
            accounts = s.Accounts.Select(a => new
            {
                a.Id, a.Name, a.Kind, server = a.Kind == AccountKind.Xtream ? a.Server : a.M3uUrl, a.Username, a.Info
            }),
            settings = new
            {
                s.UserAgent, s.NetworkCachingMs, s.Volume, s.HardwareDecoding, s.AudioDelayMs, s.LiveCacheHours, s.VodCacheHours,
                s.HideAdult, s.HiddenChannels, s.HideNoGuide, s.ShowVod, s.MatchData, s.PreferredAudioLanguage, s.PreferredSubtitleLanguage, s.UpdateChannel
            },
            catalogs = active != null ? CatalogService.GetStatus(active) : []
        };
    }

    private static async Task<object> AddAccount(AddAccountBody body)
    {
        if (string.IsNullOrWhiteSpace(body.Url)) throw new ApiError(400, "Enter a server address or playlist link.");
        Account account;
        try { account = XtreamClient.ParseAccountInput(body.Name, body.Url, body.Username, body.Password); }
        catch (UriFormatException) { throw new ApiError(400, "That does not look like a valid address."); }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            if (account.Kind == AccountKind.Xtream)
                account.Info = await XtreamClient.AuthenticateAsync(account, cts.Token);
            else
                M3uParser.Parse(await XtreamClient.GetBytesAsync(account.M3uUrl, cts.Token));
        }
        catch (InvalidOperationException e) { throw new ApiError(400, e.Message); }
        catch (Exception e) { throw new ApiError(400, "Could not reach the server: " + e.Message); }

        var settings = SettingsService.Current;
        var duplicate = settings.Accounts.FirstOrDefault(a => a.Server == account.Server && a.Username == account.Username && a.M3uUrl == account.M3uUrl);
        if (duplicate != null)
        {
            duplicate.Password = account.Password;
            duplicate.Info = account.Info;
            account = duplicate;
        }
        else
        {
            settings.Accounts.Add(account);
        }
        settings.ActiveAccountId = account.Id;
        SettingsService.Save();
        CatalogService.Warm(account);
        GuideService.Warm(account);
        return Bootstrap();
    }

    private static void Activate(string id)
    {
        var account = SettingsService.Current.Accounts.FirstOrDefault(a => a.Id == id) ?? throw new ApiError(404, "Unknown account");
        PlayerService.Stop();
        SettingsService.Current.ActiveAccountId = id;
        SettingsService.Save();
        CatalogService.Warm(account);
        GuideService.Warm(account);
        FeedCache.Invalidate();
        _ = RefreshAccountInfo(account);
    }

    public static async Task RefreshAccountInfo(Account account)
    {
        if (account.Kind != AccountKind.Xtream) return;
        try
        {
            account.Info = await XtreamClient.AuthenticateAsync(account);
            SettingsService.Save();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning("Account check failed: {Message}", ex.Message);
        }
    }

    private static void RemoveAccount(string id)
    {
        var s = SettingsService.Current;
        var account = s.Accounts.FirstOrDefault(a => a.Id == id);
        if (account == null) return;
        if (SettingsService.ActiveAccount?.Id == id) PlayerService.Stop();
        s.Accounts.Remove(account);
        CatalogService.Forget(account);
        GuideService.Forget(account);
        if (s.ActiveAccountId == id) s.ActiveAccountId = s.Accounts.FirstOrDefault()?.Id;
        SettingsService.Save();
    }

    private static void UpdateSettings(Dictionary<string, JsonElement> patch)
    {
        var s = SettingsService.Current;
        foreach (var (key, v) in patch)
        {
            switch (key)
            {
                case "userAgent": s.UserAgent = v.GetString() ?? s.UserAgent; break;
                case "networkCachingMs": s.NetworkCachingMs = Math.Clamp(v.GetInt32(), 200, 20000); break;
                case "hardwareDecoding": s.HardwareDecoding = v.GetBoolean(); break;
                case "audioDelayMs":
                    s.AudioDelayMs = Math.Clamp(v.GetInt32(), -500, 1000);
                    PlayerService.ApplyAudioDelay();
                    break;
                case "liveCacheHours": s.LiveCacheHours = Math.Clamp(v.GetInt32(), 1, 168); break;
                case "vodCacheHours": s.VodCacheHours = Math.Clamp(v.GetInt32(), 1, 720); break;
                case "hideAdult": s.HideAdult = v.GetBoolean(); break;
                case "hideNoGuide": s.HideNoGuide = v.GetBoolean(); break;
                case "hiddenChannels":
                    s.HiddenChannels = ChannelFilter.Clean(v.EnumerateArray().Select(e => e.GetString()));
                    break;
                case "matchData": s.MatchData = v.GetBoolean(); break;
                case "showVod":
                    s.ShowVod = v.GetBoolean();
                    if (SettingsService.ActiveAccount is { } acc) CatalogService.Warm(acc);
                    break;
                case "preferredAudioLanguage": s.PreferredAudioLanguage = v.GetString() ?? ""; break;
                case "preferredSubtitleLanguage": s.PreferredSubtitleLanguage = v.GetString() ?? ""; break;
                case "updateChannel":
                    s.UpdateChannel = v.GetString() == "beta" ? "beta" : "stable";
                    UpdateService.ChannelChanged();
                    break;
            }
        }
        SettingsService.Save();
        FeedCache.Invalidate();
    }

    private static async Task Catalog(HttpListenerRequest req, HttpListenerResponse res, MediaKind kind)
    {
        var account = RequireAccount();
        var snap = await CatalogService.GetAsync(account, kind);
        bool hideAdult = SettingsService.Current.HideAdult;
        if (kind == MediaKind.Live) MatchDataService.ApplyLogos(snap.Items);
        // The leading version changes whenever cached items get new names or fields.
        string etag = $"\"v2-{account.Id}-{snap.FetchedAt.ToUnixTimeMilliseconds()}-{hideAdult}-{ChannelFilter.Stamp}-{snap.Items.Count(i => i.IconAlt != null)}-{GuideService.CatalogStamp(account)}\"";
        if (req.Headers["If-None-Match"] == etag)
        {
            res.StatusCode = 304;
            return;
        }
        res.Headers["ETag"] = etag;
        res.Headers["Cache-Control"] = "no-cache";

        if (kind == MediaKind.Live) MatchDataService.ApplyLogos(snap.Items);
        bool live = kind == MediaKind.Live;
        var items = snap.Items.Where(i => !ChannelFilter.Hidden(i, live)).ToList();
        var categories = snap.Categories.Where(c => c.Count > 0).ToList();
        if (hideAdult) categories = categories.Where(c => !LooksAdult(c.Name)).ToList();
        if (live && ChannelFilter.Active)
        {
            // Categories whose every channel is hidden go too, and counts match what is left.
            var counts = items.GroupBy(i => i.CategoryId).ToDictionary(g => g.Key, g => g.Count());
            categories = categories.Where(c => counts.ContainsKey(c.Id))
                .Select(c => new Category { Id = c.Id, Name = c.Name, Count = counts[c.Id] }).ToList();
        }
        var allowed = categories.Select(c => c.Id).ToHashSet();
        await ApiServer.WriteJson(res, new
        {
            fetchedAt = snap.FetchedAt,
            home = ChannelNames.HomeCode,
            categories,
            items = items.Where(i => allowed.Contains(i.CategoryId))
        });
    }

    // What each pattern would hide, counted on the full channel list so hidden channels still show up here.
    private static object HiddenPreview(string[] raw)
    {
        var patterns = ChannelFilter.Clean(raw);
        var account = SettingsService.ActiveAccount;
        var all = account != null ? CatalogService.GetIndex(account, MediaKind.Live)?.All : null;
        bool hideAdult = SettingsService.Current.HideAdult;
        var channels = (all ?? []).Where(c => !(hideAdult && c.Adult)).ToList();
        var hidden = new HashSet<string>();
        bool guideLoaded = account != null && GuideService.Peek(account) != null;
        var noGuide = guideLoaded ? channels.Where(c => !ChannelFilter.HasGuide(c)).ToList() : [];
        if (SettingsService.Current.HideNoGuide) foreach (var c in noGuide) hidden.Add(c.Id);
        var results = patterns.Select(p =>
        {
            var regex = ChannelFilter.ToRegex([p]);
            var hits = channels.Where(c => regex.IsMatch(c.Name)).ToList();
            foreach (var c in hits) hidden.Add(c.Id);
            var names = hits.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            return new { pattern = p, count = hits.Count, names = names.Take(60), more = Math.Max(0, names.Count - 60) };
        }).ToList();
        return new { loaded = all != null, guideLoaded, noGuide = noGuide.Count, total = hidden.Count, patterns = results };
    }

    private static bool LooksAdult(string name)
    {
        var n = name.ToLowerInvariant();
        return n.Contains("adult") || n.Contains("xxx") || n.Contains("18+") || n.Contains("for adults");
    }

    private static object Search(string q, int limit)
    {
        var account = RequireAccount();
        string norm = CatalogService.Normalize(q);
        var tokens = norm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return new { live = Array.Empty<object>(), movie = Array.Empty<object>(), series = Array.Empty<object>(), programs = Array.Empty<object>() };
        List<CatalogItem> Top(MediaKind k) =>
            CatalogService.GetIndex(account, k)?.Search(norm, tokens)
                .OrderByDescending(x => x.score).ThenByDescending(x => x.item.Added)
                .Take(limit).Select(x => x.item).ToList() ?? new();

        return new
        {
            live = Top(MediaKind.Live),
            movie = Top(MediaKind.Movie),
            series = Top(MediaKind.Series),
            programs = SuggestionService.SearchPrograms(account, norm, tokens, 30)
        };
    }

    private static Dictionary<string, object> GuideNow(Account account)
    {
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var guide = GuideService.Get(account);
        if (guide == null) return result;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var streams = SuggestionService.StreamsByEpg(account);
        foreach (var (epgId, list) in guide.ByChannel)
        {
            int i = list.FindIndex(p => p.End > now);
            if (i < 0) continue;
            var cur = list[i].Start <= now ? list[i] : null;
            var next = cur != null ? list.ElementAtOrDefault(i + 1) : list[i];
            var channels = streams.GetValueOrDefault(epgId);
            string? Title(Programme? p) => p == null ? null : channels != null ? SportService.ResolveTitle(p, channels) : p.Title;
            result[epgId] = new
            {
                title = Title(cur),
                start = cur?.Start,
                end = cur?.End,
                image = cur?.Image,
                next = Title(next),
                nextStart = next?.Start
            };
        }
        return result;
    }

    private static List<EpgEntry>? GuideEntries(Account account, string streamId)
    {
        var channel = CatalogService.Find(account, MediaKind.Live, streamId);
        var guide = GuideService.Get(account);
        if (channel?.EpgId == null || guide == null) return null;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var channels = SuggestionService.StreamsByEpg(account).GetValueOrDefault(channel.EpgId) ?? [channel];
        var list = guide.Upcoming(channel.EpgId, now).Take(12).Select(p =>
        {
            // Generic entries get the real matchup from the match data, and matchups get scores, logos and images.
            string title = SportService.ResolveTitle(p, channels);
            // Studio shows and reruns stay plain; games named only in the description still get their teams.
            var look = SportService.Inspect(p, title, now);
            var parsed = look.Parsed;
            string? sport = look.Skip ? null : look.Sport;
            var info = !look.Skip && parsed.Teams.Count == 2 ? MatchDataService.Lookup(parsed.Teams, parsed.TeamNames, p.Start, sport) : null;
            bool match = parsed.Teams.Count == 2 && (sport != null || info != null);
            return new EpgEntry
            {
                Title = title,
                Description = info?.Description ?? p.Description,
                Start = p.Start,
                End = p.End,
                Image = p.Image,
                Teams = match ? parsed.TeamNames : null,
                Info = info
            };
        }).ToList();
        return list.Count > 0 ? list : null;
    }

    private static Task Cast(CastBody body)
    {
        try
        {
            switch (body.Action)
            {
                case "select": CastService.Select(string.IsNullOrEmpty(body.Id) ? null : body.Id); break;
                case "dismissError": CastService.DismissError(); break;
                default: throw new ApiError(400, "Unknown cast action");
            }
            return Task.CompletedTask;
        }
        catch (CastException ex)
        {
            throw new ApiError(400, ex.Message);
        }
    }

    private static void Player(PlayerBody body)
    {
        switch (body.Action)
        {
            case "play" when body.Item != null:
                Task.Run(() =>
                {
                    try { PlayerService.Play(body.Item); }
                    catch (Exception ex) { Serilog.Log.Error(ex, "Play failed"); }
                });
                break;
            case "toggle": PlayerService.TogglePause(); break;
            case "goLive": PlayerService.GoLive(); break;
            case "stop": PlayerService.Stop(); break;
            case "seek": PlayerService.Seek(body.Ms); break;
            case "seekBy": PlayerService.SeekRelative(body.Ms); break;
            case "volume": PlayerService.SetVolume(body.Value); break;
            case "mute": PlayerService.ToggleMute(); break;
            case "rate": PlayerService.SetRate(body.Rate); break;
            case "audio": PlayerService.SetAudioTrack(body.Value); break;
            case "subtitle": PlayerService.SetSubtitle(body.Value); break;
            case "fullscreen": Program.SetFullscreen(body.Enabled); break;
            default: throw new ApiError(400, "Unknown player action");
        }
    }
}
