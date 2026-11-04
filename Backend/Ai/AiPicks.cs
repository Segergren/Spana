using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Serilog;
using Spana.Backend.Core;
using Spana.Backend.Iptv;

namespace Spana.Backend.Ai;

public sealed class VodPick
{
    public string Kind { get; set; } = "movie";
    public CatalogItem Item { get; set; } = new();
    public string Why { get; set; } = "";
}

public sealed class PicksFile
{
    public string AccountId { get; set; } = "";
    public bool WithVod { get; set; }
    public DateTimeOffset GeneratedAt { get; set; }
    public List<Suggestion> Live { get; set; } = new();
    public List<VodPick> Vod { get; set; } = new();
}

public static partial class AiPicks
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(3);
    private static readonly TimeSpan RetryAfterError = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RetryWhileWaiting = TimeSpan.FromMinutes(1);
    private const int MaxPicks = 10;
    private const int MaxTvCandidates = 260;
    private const int MaxSportCandidates = 140;
    private const long Horizon = 12 * 3_600_000L;
    private const int MaxPerChannel = 6;

    [GeneratedRegex(@"(s\d{1,2}\s?e\d{1,3}|avsnitt \d+|del \d+|episode \d+|ep \d+|part \d+).*$")]
    private static partial Regex EpisodeMarker();

    // OpenAI appends source links and Claude wraps quotes in <cite> tags.
    [GeneratedRegex(@"\s*\(?\[[^\]]*\]\([^)]*\)\)?|\s*\(https?://[^)]*\)")]
    private static partial Regex Citation();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex Tag();

    private sealed class Run
    {
        public Task? Task;
        public string? Error;
        public DateTimeOffset FailedAt;
        public DateTimeOffset IdleUntil;
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Run> Runs = new();
    private static PicksFile? _picks;
    // Only from this session; the saved country is written when a key is saved.
    private static string? _country;

    private static Run RunFor(Account a) => Runs.TryGetValue(a.Id, out var r) ? r : Runs[a.Id] = new Run();

    private static string FilePath(Account a) => Path.Combine(Storage.CacheRoot, a.Id, "ai-picks.json");

    public static bool Enabled(out string provider, out string key)
    {
        var ai = SettingsService.Current.Ai;
        provider = ai.Provider;
        key = ai.Keys.GetValueOrDefault(provider) ?? "";
        return provider.Length > 0 && key.Length > 0;
    }

    public static void SettingsChanged()
    {
        lock (Gate)
            foreach (var run in Runs.Values)
                run.Error = null;
    }

    public static object Get(Account a)
    {
        if (!Enabled(out _, out _)) return new { status = "off" };
        var picks = Load(a);
        bool running;
        string? error;
        lock (Gate)
        {
            var run = RunFor(a);
            var at = DateTimeOffset.UtcNow;
            if (Stale(a, picks) && run.Task == null && at >= run.IdleUntil && (run.Error == null || at - run.FailedAt > RetryAfterError))
                Start(a, run);
            running = run.Task != null;
            error = run.Error;
        }
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bool usable = picks != null && picks.AccountId == a.Id;
        return new
        {
            status = running ? "loading" : error != null ? "error" : GuideService.Peek(a) == null && !usable ? "waiting" : "ready",
            error,
            generatedAt = usable ? picks!.GeneratedAt : (DateTimeOffset?)null,
            live = usable ? Current(a, picks!.Live, now) : [],
            vod = usable && SettingsService.Current.ShowVod ? picks!.Vod : []
        };
    }

    public static void Refresh(Account a)
    {
        lock (Gate)
        {
            var run = RunFor(a);
            run.Error = null;
            run.IdleUntil = default;
            if (run.Task == null) Start(a, run);
        }
    }

    private static PicksFile? Load(Account a)
    {
        lock (Gate)
        {
            if (_picks?.AccountId == a.Id) return _picks;
            var loaded = File.Exists(FilePath(a)) ? Storage.Load<PicksFile>(FilePath(a)) : null;
            _picks = loaded?.AccountId == a.Id ? loaded : null;
            return _picks;
        }
    }

    private static bool Stale(Account a, PicksFile? p)
    {
        var s = SettingsService.Current;
        // Switching provider or model waits for the next refresh.
        if (p == null || p.AccountId != a.Id || (s.ShowVod && !p.WithVod && VodReady(a))) return true;
        var age = DateTimeOffset.UtcNow - p.GeneratedAt;
        if (age > MaxAge) return true;
        // Most picks are over, so there is likely something newer to show.
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return age > TimeSpan.FromMinutes(45) && p.Live.Count(x => x.Program.End > now) < 3;
    }

    // Live state and scores come from the current sport feed.
    private static List<Suggestion> Current(Account a, List<Suggestion> picks, long now)
    {
        var sport = new Dictionary<string, SportEvent>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in FeedCache.Sport(a))
        {
            sport.TryAdd(e.Channel.EpgId + "|" + e.Program.Start, e);
            foreach (var alt in e.Alternatives ?? []) sport.TryAdd(alt.Channel.EpgId + "|" + alt.Program.Start, e);
        }
        var list = new List<Suggestion>();
        foreach (var p in picks)
        {
            // A pick made on another channel follows the match to the one in the viewer's language.
            var e = sport.GetValueOrDefault(p.Channel.EpgId + "|" + p.Program.Start);
            var channel = e?.Channel ?? p.Channel;
            var program = e == null || e.Channel.Id == p.Channel.Id ? p.Program
                : p.Teams.Count == 2 ? e.Program
                : new ProgramView { Title = p.Program.Title, Description = e.Program.Description, Start = e.Program.Start, End = e.Program.End, Image = e.Program.Image, Replay = e.Program.Replay };
            if (program.End <= now || ChannelFilter.Hidden(channel) || Foreign(channel, e?.Mine ?? false)) continue;
            if (e != null) program.Replay = e.Program.Replay;
            var info = e?.Info ?? p.Info;
            if (info is { Finished: true } && now - (info.FinishedAt ?? 0) >= 20 * 60_000) continue;
            if (list.Any(x => x.Channel.Id == channel.Id && x.Program.Start == program.Start)) continue;
            list.Add(new Suggestion
            {
                Program = program, Channel = channel, Reason = p.Reason, Teams = e?.Teams ?? p.Teams, Match = p.Match, Info = info,
                Live = program.Start <= now, Language = Languages.Of(channel), Alternatives = e?.Alternatives
            });
        }
        // What is on now leads; later games keep the AI's order after it.
        return list.OrderByDescending(x => x.Program.Start <= now + 30 * 60_000).ToList();
    }

    private static void Start(Account a, Run run)
    {
        run.Task = Task.Run(async () =>
        {
            try
            {
                var result = await Generate(a);
                if (result == null)
                {
                    // Nothing to pick from yet; polling should not start a new attempt every few seconds.
                    lock (Gate) run.IdleUntil = DateTimeOffset.UtcNow + RetryWhileWaiting;
                    return;
                }
                Storage.Save(FilePath(a), result);
                lock (Gate)
                {
                    if (_picks == null || _picks.AccountId == a.Id) _picks = result;
                    run.Error = null;
                }
            }
            catch (Exception ex)
            {
                if (ex is not AiException) Log.Warning(ex, "AI picks failed");
                lock (Gate)
                {
                    run.Error = ex is AiException ? ex.Message : ex is TaskCanceledException ? "The request timed out." : "Could not reach the AI provider.";
                    run.FailedAt = DateTimeOffset.UtcNow;
                }
            }
            finally
            {
                lock (Gate) run.Task = null;
            }
        });
    }

    private sealed class Entry
    {
        public string Id = "";
        public ProgramView Program = new();
        public CatalogItem Channel = new();
        public List<string> Teams = new();
        public MatchInfo? Info;
        public string? Label;
        public string? Notes;
    }

    private static async Task<PicksFile?> Generate(Account a)
    {
        if (!Enabled(out string provider, out string key)) return null;
        var settings = SettingsService.Current;
        await FeedCache.WaitUntilReady(a);
        if (GuideService.Get(a) == null) return null;

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        string? country = settings.Ai.Country ?? (_country ??= await AiClient.LookupCountry(cts.Token));

        var tv = TvCandidates(a);
        if (tv.Count == 0) return null;
        var vod = settings.ShowVod ? VodCandidates(a) : new();
        bool withVod = vod.Count > 0;
        bool search = AiClient.CanSearch(provider);
        string model = AiClient.ModelFor(settings.Ai, provider);

        string system = SystemPrompt(country, search, vod.Count > 0), user = UserPrompt(a, country, tv, vod);
        Log.Information("AI picks request: {Tv} guide entries, {Vod} library titles, {Chars} characters", tv.Count, vod.Count, system.Length + user.Length);
        string reply = await AiClient.Complete(provider, key, model, AiClient.ReasoningFor(provider, model, settings.Ai.Reasoning), system, user, search, cts.Token);
        var json = AiClient.ParseJson(reply) ?? throw new AiException("The answer could not be read. Try again or pick another model.");

        var byId = tv.ToDictionary(e => e.Id);
        var live = new List<Suggestion>();
        foreach (var (id, why, title) in Picks(json, "live"))
        {
            if (live.Count == MaxPicks) break;
            if (!byId.Remove(id, out var e)) continue;
            var program = title == null || e.Teams.Count == 2 ? e.Program : new ProgramView
            {
                Title = title, Description = e.Program.Description, Start = e.Program.Start, End = e.Program.End, Image = e.Program.Image,
                Replay = e.Program.Replay
            };
            live.Add(new Suggestion
            {
                Program = program, Channel = e.Channel, Reason = why, Teams = e.Teams, Info = e.Info, Match = "pick",
                Live = e.Program.Start <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
        }
        var vodById = vod.ToDictionary(v => v.id);
        var vodPicks = new List<VodPick>();
        foreach (var (id, why, _) in Picks(json, "vod"))
            if (vodPicks.Count < MaxPicks && vodById.Remove(id, out var v))
                vodPicks.Add(new VodPick { Kind = v.kind, Item = v.item, Why = why });

        Log.Information("AI picks from {Provider}: {Live} on TV, {Vod} in the library", provider, live.Count, vodPicks.Count);
        return new PicksFile
        {
            AccountId = a.Id, WithVod = withVod, GeneratedAt = DateTimeOffset.UtcNow, Live = live, Vod = vodPicks
        };
    }

    private static IEnumerable<(string id, string why, string? title)> Picks(JsonElement json, string name)
    {
        if (json.ValueKind != JsonValueKind.Object || !json.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array) yield break;
        foreach (var p in list.EnumerateArray())
        {
            if (p.ValueKind != JsonValueKind.Object) continue;
            string? id = p.TryGetProperty("id", out var i) ? i.ToString() : null;
            string why = p.TryGetProperty("why", out var w) && w.ValueKind == JsonValueKind.String ? Tag().Replace(Citation().Replace(w.GetString()!, ""), "").Trim() : "";
            string? title = p.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String && t.GetString()!.Trim() is { Length: > 2 and < 60 } tt ? tt : null;
            if (id != null) yield return (id, why, title);
        }
    }

    private static List<Entry> TvCandidates(Account a)
    {
        var list = new List<Entry>();
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var sport = FeedCache.Sport(a)
            .Where(e => e.Program.End > now + 10 * 60_000 && e.Program.Start < now + Horizon &&
                        !(e.Info is { Finished: true }))
            .OrderByDescending(e => e.Tier).ThenByDescending(e => e.Prominence).ThenByDescending(e => e.Relevance)
            .Take(MaxSportCandidates);
        var sportTitles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in sport)
        {
            if (Foreign(e.Channel, e.Mine)) continue;
            if (!taken.Add(e.Channel.EpgId + "|" + e.Program.Start)) continue;
            foreach (var alt in e.Alternatives ?? []) taken.Add(alt.Channel.EpgId + "|" + alt.Program.Start);
            // Event channels repeat "Football: Allsvenskan" all day.
            if (e.Teams.Count < 2 && !sportTitles.Add(CatalogService.Normalize(e.Program.Title))) continue;
            string label = e.League is { Length: > 0 } l ? $"{e.Sport}, {l}" : e.Sport;
            list.Add(new Entry
            {
                Id = "s" + list.Count, Program = e.Program, Channel = e.Channel, Teams = e.Teams, Info = e.Info, Label = label,
                Notes = SportNotes(e)
            });
        }

        var guide = GuideService.Get(a);
        if (guide == null) return list;
        var streams = SuggestionService.StreamsByEpg(a);
        var profile = SuggestionService.BuildProfile(a.Id);
        var titles = new HashSet<string>(StringComparer.Ordinal);
        var perChannel = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var others = new List<(Entry entry, double rank)>();
        foreach (var p in guide.All)
        {
            if (p.End <= now + 10 * 60_000 || p.Start >= now + Horizon || p.End - p.Start < 15 * 60_000) continue;
            if (!streams.TryGetValue(p.EpgId, out var channels) || !channels.Any(SportService.IsHome)) continue;
            if (taken.Contains(p.EpgId + "|" + p.Start)) continue;
            // Repeats and 24/7 channels would crowd out everything else.
            if (perChannel.GetValueOrDefault(p.EpgId) >= MaxPerChannel) continue;
            if (!titles.Add(EpisodeMarker().Replace(p.NormalizedTitle, "").Trim())) continue;
            perChannel[p.EpgId] = perChannel.GetValueOrDefault(p.EpgId) + 1;
            var channel = SuggestionService.PickChannel(channels, profile, null);
            if (channel == null || !SportService.IsHome(channel)) continue;
            string title = SportService.ResolveTitle(p, channels);
            double rank = profile.EpgChannels.GetValueOrDefault(p.EpgId) * 100 - channel.Num * 0.01;
            others.Add((new Entry
            {
                Program = new ProgramView { Title = title, Description = p.Description, Start = p.Start, End = p.End, Image = p.Image, Replay = p.Repeat == true },
                Channel = channel, Label = p.Category
            }, rank));
        }
        // The provider's order puts the main channels first.
        foreach (var (entry, _) in others.OrderByDescending(o => o.rank).Take(MaxTvCandidates))
        {
            entry.Id = "t" + list.Count;
            list.Add(entry);
        }
        return list;
    }

    // Only in a language the viewer does not speak; still worth it for their own team.
    private static bool Foreign(CatalogItem channel, bool mine) => Languages.Rank(channel) >= 3 && !mine;

    private static string SportNotes(SportEvent e)
    {
        var notes = new List<string>();
        if (e.Mine) notes.Add("their team");
        else if (e.Followed) notes.Add("league they follow");
        if (e.Info?.Kickoff is long kick && Math.Abs(kick - e.Program.Start) < 4 * 3_600_000L)
            notes.Add("live match, kick-off " + DateTimeOffset.FromUnixTimeMilliseconds(kick).ToLocalTime().ToString("HH:mm"));
        else if (e.Teams.Count == 2 && e.Info == null)
            notes.Add("not in today's match data, may be a replay");
        else if (e.Teams.Count < 2)
            notes.Add("no specific match named");
        if (e.Prominence >= 3) notes.Add("major league");
        if (e.Program.Replay) notes.Add("replay");
        if (Languages.Rank(e.Channel) == 2) notes.Add("commentary language unknown");
        return string.Join(", ", notes);
    }

    private static bool VodReady(Account a) =>
        CatalogService.GetIndex(a, MediaKind.Movie)?.All.Count > 0 || CatalogService.GetIndex(a, MediaKind.Series)?.All.Count > 0;

    private static List<(string id, string kind, CatalogItem item)> VodCandidates(Account a)
    {
        var list = new List<(string, string, CatalogItem)>();
        foreach (var (kind, mk, max, prefix) in new[] { ("movie", MediaKind.Movie, 220, "m"), ("series", MediaKind.Series, 120, "v") })
        {
            var all = CatalogService.GetIndex(a, mk)?.All;
            if (all == null) continue;
            var recent = all.Where(i => !ChannelFilter.Hidden(i, false)).OrderByDescending(i => i.Added).Take(2000).ToList();
            var chosen = recent.Take(max * 2 / 3)
                .Concat(recent.Where(i => i.Rating >= 7).OrderByDescending(i => i.Rating))
                .DistinctBy(i => CatalogService.Normalize(i.Name))
                .Take(max);
            foreach (var item in chosen) list.Add((prefix + list.Count, kind, item));
        }
        return list;
    }

    private static string SystemPrompt(string? country, bool search, bool vod)
    {
        string where = country ?? "the viewer's country";
        var sb = new StringBuilder();
        sb.AppendLine("You choose what is worth watching today for one TV viewer, using their own TV guide.");
        if (search)
            sb.AppendLine($"Always start with one or two web searches for what people in {where} care about today: big matches and what is at stake, major events, premieres. The guide alone does not say what matters today, so do not skip this.");
        sb.AppendLine("Pick 8 to 10 entries from the TV guide, most worth watching first. Weigh the viewer's interests, but include broadly important events too, and do not fill the list with one competition.");
        sb.AppendLine("The notes column says what Spana knows. Skip possible replays and slots with no specific match named, and pick a game only once even if several channels show it.");
        if (vod)
            sb.AppendLine("Also pick up to 10 movies or series from the library list that suit the viewer's taste. Judge by what you know about each title, not by the rating in the list.");
        sb.AppendLine("For each pick write \"why\": one concrete sentence in English, at most 50 characters. Do not mention the channel, the time or that it is live; the card shows those. For a match the card also shows the teams, so say what is at stake. Otherwise name the people in it, and for movies and series say what it is about instead of repeating the title.");
        sb.AppendLine("When a TV title does not say what is on and the description names it, add \"title\" with a short name such as \"Lakers - Heat\".");
        sb.AppendLine("Only use ids from the lists. Reply with JSON only and no other text, in this shape:");
        sb.Append(vod
            ? "{\"live\":[{\"id\":\"s0\",\"why\":\"...\",\"title\":\"...\"}],\"vod\":[{\"id\":\"m0\",\"why\":\"...\"}]}"
            : "{\"live\":[{\"id\":\"s0\",\"why\":\"...\",\"title\":\"...\"}]}");
        return sb.ToString();
    }

    private static string UserPrompt(Account a, string? country, List<Entry> tv, List<(string id, string kind, CatalogItem item)> vod)
    {
        var now = DateTime.Now;
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine($"It is {now.ToString("dddd d MMMM yyyy, HH:mm", inv)} local time{(country != null ? $" and the viewer lives in {country}" : "")}.");

        var interests = SuggestionService.Interests(a.Id);
        void Line(string name, IEnumerable<string> items)
        {
            var list = items.ToList();
            if (list.Count > 0) sb.AppendLine($"{name}: {string.Join(", ", list)}");
        }
        sb.AppendLine();
        sb.AppendLine("What they watch on live TV:");
        Line("Teams", interests.Teams.Take(8).Select(t => t.Name));
        Line("Leagues", interests.Leagues.Take(6).Select(t => t.Name));
        Line("Shows", interests.Shows.Take(8).Select(t => t.Name));
        Line("Sports", interests.Sports.Take(5).Select(t => t.Name));

        sb.AppendLine();
        sb.AppendLine("TV guide, on now and the next 12 hours (id | time | channel | category | title | notes | description):");
        foreach (var e in tv)
        {
            var start = DateTimeOffset.FromUnixTimeMilliseconds(e.Program.Start).ToLocalTime();
            var end = DateTimeOffset.FromUnixTimeMilliseconds(e.Program.End).ToLocalTime();
            string when = start <= DateTimeOffset.Now ? $"now until {end:HH:mm}" : start.Date == now.Date ? $"{start:HH:mm}-{end:HH:mm}" : $"tomorrow {start:HH:mm}";
            string? desc = e.Program.Description == e.Program.Title ? null : e.Program.Description;
            sb.AppendLine($"{e.Id} | {when} | {e.Channel.Name} | {e.Label} | {e.Program.Title} | {e.Notes} | {Clip(desc, 110)}");
        }

        if (vod.Count > 0)
        {
            var lib = LibraryService.Get(a.Id);
            var taste = lib.History.Concat(lib.Favorites)
                .Where(h => h.Kind is MediaKind.Movie or MediaKind.Series or MediaKind.Episode)
                .Select(h => h.SeriesName ?? h.Name).Distinct().Take(25).ToList();
            sb.AppendLine();
            sb.AppendLine(taste.Count > 0 ? $"Movies and series they watched: {string.Join(", ", taste)}" : "They have not watched any movies or series yet.");
            sb.AppendLine();
            sb.AppendLine("Library, recently added (id | type | title | year | genre | rating):");
            foreach (var (id, kind, item) in vod)
                sb.AppendLine($"{id} | {kind} | {item.Name} | {item.Year} | {Clip(item.Genre, 40)} | {item.Rating?.ToString("0.0", inv)}");
        }
        return sb.ToString();
    }

    private static string Clip(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = Tag().Replace(s, "").ReplaceLineEndings(" ").Replace('|', '/').Trim();
        if (s.Length == 0) return "";
        return s.Length <= max ? s : s[..max].TrimEnd() + "...";
    }
}
