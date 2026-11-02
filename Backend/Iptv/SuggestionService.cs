using Spana.Backend.Core;

namespace Spana.Backend.Iptv;

public sealed class ProgramView
{
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public long Start { get; set; }
    public long End { get; set; }
    public string? Image { get; set; }
}

public sealed class Suggestion
{
    public ProgramView Program { get; set; } = new();
    public CatalogItem Channel { get; set; } = new();
    public double Score { get; set; }
    public string Reason { get; set; } = "";
    public bool Live { get; set; }
    public List<string> Teams { get; set; } = new();
    public string Match { get; set; } = "habit";
    public MatchInfo? Info { get; set; }
}

public readonly record struct Relevance(double Score, string Reason, string Kind);

public static class SuggestionService
{
    private const double MinMinutes = 3;
    private const double HalfLifeDays = 30;
    private const double MinScore = 0.5;
    private const double TopTierBase = 2.0;

    private static readonly Dictionary<string, string> CountryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SWE"] = "Sweden", ["NOR"] = "Norway", ["DEN"] = "Denmark", ["FIN"] = "Finland", ["ENG"] = "England",
        ["ESP"] = "Spain", ["ITA"] = "Italy", ["GER"] = "Germany", ["FRA"] = "France", ["NED"] = "the Netherlands",
        ["POR"] = "Portugal", ["SCO"] = "Scotland", ["USA"] = "the USA", ["BEL"] = "Belgium", ["TUR"] = "Turkey",
        ["CAN"] = "Canada", ["MEX"] = "Mexico", ["BRA"] = "Brazil", ["ARG"] = "Argentina", ["AUS"] = "Australia", ["CZE"] = "Czechia",
        ["POL"] = "Poland", ["IND"] = "India", ["JPN"] = "Japan", ["KOR"] = "Korea"
    };

    internal sealed class Interest
    {
        public ProgramTitle.TeamKey Team = new();
        public string? Id;
        public string? Sport;
        public string Name = "";
        public double Weight;
        public string Source = "";
        // Every id and name this team was seen under, so forgetting it covers all of them.
        public HashSet<string> Keys = new();
    }

    internal sealed class Profile
    {
        public List<Interest> Teams = new();
        public Dictionary<string, (double weight, string source)> Shows = new();
        public Dictionary<string, (double weight, string name)> Leagues = new();
        public Dictionary<string, double> Countries = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, double> Sports = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, double> Channels = new();
        public Dictionary<string, double> EpgChannels = new(StringComparer.OrdinalIgnoreCase);
        public bool Empty => Teams.Count == 0 && Shows.Count == 0 && Leagues.Count == 0 && Channels.Count == 0;
    }

    private sealed record EpgMap(string Key, Dictionary<string, List<CatalogItem>> Map);
    private static volatile EpgMap? _epgMap;

    // Live streams grouped by guide channel id, rebuilt when the catalog changes.
    public static Dictionary<string, List<CatalogItem>> StreamsByEpg(Account a)
    {
        var index = CatalogService.GetIndex(a, MediaKind.Live);
        if (index == null) return new();
        string key = a.Id + ":" + index.GetHashCode() + ":" + GuideService.CatalogStamp(a);
        if (_epgMap is { } m && m.Key == key) return m.Map;
        var map = new Dictionary<string, List<CatalogItem>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in index.All)
        {
            if (string.IsNullOrEmpty(item.EpgId)) continue;
            if (!map.TryGetValue(item.EpgId, out var list)) map[item.EpgId] = list = new();
            list.Add(item);
        }
        _epgMap = new EpgMap(key, map);
        return map;
    }

    internal static Profile BuildProfile(string accountId)
    {
        var profile = new Profile();
        var now = DateTimeOffset.UtcNow;
        var leagueAcc = new Dictionary<string, (double minutes, double weight, string? name)>();
        var countryAcc = new Dictionary<string, (double minutes, double weight)>(StringComparer.OrdinalIgnoreCase);
        var sportAcc = new Dictionary<string, (double minutes, double weight)>(StringComparer.OrdinalIgnoreCase);
        var forgotten = LibraryService.Forgotten(accountId);
        foreach (var w in LibraryService.LiveWatches(accountId))
        {
            // Viewing from before an interest was removed in settings no longer counts towards it.
            bool Gone(string key) => forgotten.TryGetValue(key, out var at) && w.LastWatched <= at;

            double decay = Math.Pow(0.5, (now - w.LastWatched).TotalDays / HalfLifeDays);
            double weight = Math.Log(1 + Math.Min(w.Minutes, 240)) * decay;
            profile.Channels[w.ChannelId] = profile.Channels.GetValueOrDefault(w.ChannelId) + weight;
            if (w.EpgId != null) profile.EpgChannels[w.EpgId] = profile.EpgChannels.GetValueOrDefault(w.EpgId) + weight;
            if (w.Title == null) continue;

            var parsed = new ProgramTitle(w.Title);
            // Teams and shows need a real viewing; a quick zap past a match should not make you a fan.
            if (w.Minutes >= MinMinutes)
            {
                for (int i = 0; i < parsed.Teams.Count; i++)
                {
                    string? id = (i == 0 ? w.HomeId : w.AwayId) ?? MatchDataService.LeagueOf(parsed.Teams[i])?.TeamId;
                    string nameKey = "team:n:" + CatalogService.Normalize(parsed.TeamNames[i]);
                    string? idKey = id != null ? "team:" + id : null;
                    if (Gone(nameKey) || (idKey != null && Gone(idKey))) continue;
                    var existing = profile.Teams.FirstOrDefault(t => id != null && t.Id != null ? t.Id == id : t.Team.SameAs(parsed.Teams[i]));
                    if (existing == null)
                        profile.Teams.Add(existing = new Interest
                        {
                            Team = parsed.Teams[i], Id = id, Name = parsed.TeamNames[i], Weight = weight, Source = w.Title,
                            Sport = w.Sport ?? SportService.Classify(parsed.Category, CatalogService.Normalize(w.Title)) ?? (id?.StartsWith("fm:") == true ? "Football" : null)
                        });
                    else
                    {
                        if (weight > existing.Weight) existing.Source = w.Title;
                        existing.Id ??= id;
                        existing.Weight += weight;
                    }
                    existing.Keys.Add(nameKey);
                    if (idKey != null) existing.Keys.Add(idKey);
                }
                if (parsed.Teams.Count == 0 && parsed.Show.Length > 2 && !Gone("show:" + parsed.Show))
                {
                    var prev = profile.Shows.GetValueOrDefault(parsed.Show);
                    profile.Shows[parsed.Show] = (prev.weight + weight, w.Title);
                }
            }

            // Older viewing has no league stored, so it is resolved from what is known about the teams.
            string? league = MatchDataService.Canonical(w.League);
            string? leagueName = w.LeagueName;
            string? country = w.Country;
            string? sport = w.Sport ?? SportService.Classify(parsed.Category, CatalogService.Normalize(w.Title));
            if (league == null)
            {
                var ctx = MatchDataService.Context(parsed, w.Title, w.LastWatched.ToUnixTimeMilliseconds());
                league = ctx.League;
                leagueName = ctx.LeagueName;
                country ??= ctx.Country;
                sport ??= ctx.Sport;
            }
            leagueName = MatchDataService.NameOf(league) ?? leagueName;
            country ??= MatchDataService.CountryOf(league);

            // Leagues, countries and sports add up across viewings, so several short visits to Allsvenskan count.
            if (league != null && !Gone("league:" + league))
            {
                var prev = leagueAcc.GetValueOrDefault(league);
                leagueAcc[league] = (prev.minutes + w.Minutes, prev.weight + weight, prev.name ?? CleanLeague(leagueName ?? league));
            }
            if (country != null && !Gone("country:" + country))
            {
                var prev = countryAcc.GetValueOrDefault(country);
                countryAcc[country] = (prev.minutes + w.Minutes, prev.weight + weight);
            }
            if (sport != null && !Gone("sport:" + sport))
            {
                var prev = sportAcc.GetValueOrDefault(sport);
                sportAcc[sport] = (prev.minutes + w.Minutes, prev.weight + weight);
            }
        }

        foreach (var (k, v) in leagueAcc)
            if (v.minutes >= MinMinutes) profile.Leagues[k] = (v.weight, v.name ?? k);
        foreach (var (k, v) in countryAcc)
            if (v.minutes >= MinMinutes) profile.Countries[k] = v.weight;
        foreach (var (k, v) in sportAcc)
            if (v.minutes >= MinMinutes) profile.Sports[k] = v.weight;
        return profile;
    }

    public sealed class InterestView
    {
        public string Name { get; set; } = "";
        public string? Detail { get; set; }
        public List<string> Keys { get; set; } = new();
    }

    public sealed class InterestList
    {
        public List<InterestView> Teams { get; set; } = new();
        public List<InterestView> Leagues { get; set; } = new();
        public List<InterestView> Shows { get; set; } = new();
        public List<InterestView> Countries { get; set; } = new();
        public List<InterestView> Sports { get; set; } = new();
    }

    // What the suggestions think you follow, strongest first, for settings to show and remove one by one.
    public static InterestList Interests(string accountId)
    {
        var p = BuildProfile(accountId);
        string Country(string code)
        {
            string name = CountryNames.GetValueOrDefault(code, code.ToUpperInvariant());
            return name.StartsWith("the ") ? char.ToUpperInvariant(name[4]) + name[5..] : name;
        }
        return new InterestList
        {
            Teams = p.Teams.OrderByDescending(t => t.Weight)
                .Select(t => new InterestView { Name = t.Name, Detail = t.Sport, Keys = t.Keys.ToList() }).ToList(),
            Leagues = p.Leagues.OrderByDescending(l => l.Value.weight)
                .Select(l => new InterestView { Name = l.Value.name, Keys = ["league:" + l.Key] }).ToList(),
            Shows = p.Shows.OrderByDescending(s => s.Value.weight)
                .Select(s => new InterestView { Name = s.Value.source, Keys = ["show:" + s.Key] }).ToList(),
            Countries = p.Countries.OrderByDescending(c => c.Value)
                .Select(c => new InterestView { Name = Country(c.Key), Keys = ["country:" + c.Key] }).ToList(),
            Sports = p.Sports.OrderByDescending(s => s.Value)
                .Select(s => new InterestView { Name = s.Key, Keys = ["sport:" + s.Key] }).ToList(),
        };
    }

    private static string CleanLeague(string name)
    {
        int paren = name.IndexOf(" (", StringComparison.Ordinal);
        var s = paren > 0 ? name[..paren] : name;
        return s.Length > 0 && char.IsLower(s[0]) ? char.ToUpperInvariant(s[0]) + s[1..] : s;
    }

    // How much a programme matters to you: your teams first, then your leagues, the same country, and the big leagues of sports you watch.
    internal static Relevance Score(Profile profile, ProgramTitle parsed, MatchContext ctx, string? sport)
    {
        double score = 0, strongest = 0;
        string reason = "", kind = "habit";

        string? leagueKey = Leagues.Key(ctx.LeagueName);
        for (int ti = 0; ti < parsed.Teams.Count; ti++)
        {
            var team = parsed.Teams[ti];
            string? teamId = ti == 0 ? ctx.HomeId : ctx.AwayId;
            double best = 0;
            Interest? match = null;
            foreach (var interest in profile.Teams)
            {
                // A club you follow in one sport says nothing about a namesake in another sport.
                if (sport != null && interest.Sport != null && sport != interest.Sport) continue;
                // The scores source lists every match that day; a club missing from it is not the one playing here.
                if (ctx.NotOnFotMob && interest.Id?.StartsWith("fm:") == true) continue;
                // With ids on both sides only the exact club counts, so women's and youth teams do not match.
                double sim = teamId != null && interest.Id != null
                    ? (teamId == interest.Id ? 1 : 0)
                    : ProgramTitle.TeamSimilarity(team, interest.Team);
                double s = sim * interest.Weight;
                if (s > best) { best = s; match = interest; }
            }
            if (match == null) continue;
            bool exactId = teamId != null && match.Id != null;
            if (!exactId && ctx.League != null && profile.Leagues.Count > 0 && !profile.Leagues.ContainsKey(ctx.League) &&
                !Leagues.IsCup(leagueKey ?? ""))
                best *= 0.4;
            score += best * 3;
            if (best * 3 > strongest)
            {
                strongest = best * 3;
                reason = $"You watched {match.Source}";
                kind = "team";
            }
        }

        if (profile.Shows.TryGetValue(parsed.Show, out var show))
        {
            score += show.weight * 2;
            if (show.weight * 2 > strongest)
            {
                strongest = show.weight * 2;
                reason = $"You watch {show.source}";
                kind = "show";
            }
        }

        bool sportKnown = sport != null && profile.Sports.ContainsKey(sport);
        if (ctx.League != null && profile.Leagues.TryGetValue(ctx.League, out var league))
        {
            score += league.weight * 1.6;
            if (league.weight * 1.6 > strongest)
            {
                strongest = league.weight * 1.6;
                reason = $"You follow {league.name}";
                kind = "league";
            }
        }
        else if (ctx.Country != null && profile.Countries.TryGetValue(ctx.Country, out var cw) && sportKnown)
        {
            score += cw * 0.2;
            if (strongest == 0)
            {
                reason = $"More from {CountryNames.GetValueOrDefault(ctx.Country, ctx.Country)}";
                if (kind == "habit") kind = "country";
            }
        }

        if (Leagues.IsTopTier(leagueKey) && (sportKnown || (sport == null && profile.Sports.Count == 0)))
        {
            score += TopTierBase;
            if (strongest == 0 && reason.Length == 0) reason = $"Top league: {CleanLeague(ctx.LeagueName!)}";
            if (kind is "habit" or "country") kind = "top";
        }

        return new Relevance(score, reason, kind);
    }

    public static List<Suggestion> Get(Account a, int limit, string? excludeChannelId)
    {
        var guide = GuideService.Get(a);
        if (guide == null) return new();
        var profile = BuildProfile(a.Id);
        if (profile.Empty) return new();

        var streams = StreamsByEpg(a);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long horizon = now + TimeSpan.FromHours(10).Ticks / TimeSpan.TicksPerMillisecond;

        var best = new Dictionary<string, Suggestion>();
        foreach (var p in guide.All)
        {
            if (p.End <= now || p.Start > horizon) continue;
            if (!streams.TryGetValue(p.EpgId, out var channels)) continue;
            string title = SportService.ResolveTitle(p, channels);
            var parsed = title == p.Title ? p.Parsed : new ProgramTitle(title);

            // Ordinary programmes are only worth a look when they are a show you follow.
            bool followed = profile.Shows.ContainsKey(parsed.Show);
            bool sporty = parsed.Teams.Count == 2 || parsed.Category != null || SportService.GuideSaysSport(p.Category);
            if (!sporty && !followed) continue;
            string? sport = null;
            if (sporty)
            {
                var look = SportService.Inspect(p, title, now);
                // Studio shows, reruns and films only come up as a show you follow.
                if ((look.Skip || look.GuideOnly) && !followed) continue;
                parsed = look.Parsed;
                sport = look.Sport;
            }

            var ctx = MatchDataService.Context(parsed, title, p.Start);
            sport ??= SportService.Classify(parsed.Category, CatalogService.Normalize(title)) ?? ctx.Sport;
            var rel = Score(profile, parsed, ctx, sport);
            if (rel.Score < MinScore) continue;

            var info = parsed.Teams.Count == 2 ? MatchDataService.Lookup(parsed.Teams, parsed.TeamNames, p.Start, sport) : null;
            // Event channels keep long guide slots after the final whistle; a match over for 20 minutes is not a suggestion.
            if (info is { Finished: true } && now - (info.FinishedAt ?? 0) >= 20 * 60_000) continue;
            double score = rel.Score * MatchDataService.Quality(info);
            if (profile.EpgChannels.TryGetValue(p.EpgId, out var chw)) score += chw * 0.1;
            bool live = p.Start <= now;
            double hoursAway = live ? 0 : (p.Start - now) / 3_600_000.0;
            score *= live ? 1.0 : Math.Max(0.45, 0.95 - hoursAway * 0.06);

            var channel = PickChannel(channels, profile, excludeChannelId);
            if (channel == null) continue;

            string key = parsed.Show + "|" + p.Start / 60000;
            if (best.TryGetValue(key, out var existing) && existing.Score >= score) continue;
            best[key] = new Suggestion
            {
                Program = new ProgramView { Title = title, Description = p.Description, Start = p.Start, End = p.End, Image = p.Image },
                Channel = channel,
                Score = Math.Round(score, 3),
                Reason = rel.Reason.Length > 0 ? rel.Reason : $"On {channel.Name}",
                Live = live,
                Teams = parsed.TeamNames,
                Match = rel.Kind,
                Info = info
            };
        }

        var top = best.Values.OrderByDescending(s => s.Score).ThenBy(s => s.Program.Start).Take(limit).ToList();
        return top;
    }

    internal static CatalogItem? PickChannel(List<CatalogItem> channels, Profile profile, string? exclude)
    {
        CatalogItem? pick = null;
        double pickScore = double.MinValue;
        foreach (var c in channels)
        {
            if (ChannelFilter.Hidden(c)) continue;
            double s = profile.Channels.GetValueOrDefault(c.Id) * 10;
            // Quality and stream notes come out of the name when the catalog is loaded.
            if (c.Quality >= 720) s += 1;
            if (c.Tags is { } tags)
            {
                if (tags.Contains("offline")) s -= 5;
                if (tags.Contains("geo")) s -= 2;
                if (tags.Contains("part-time")) s -= 1;
                if (tags.Contains("50fps")) s -= 0.5;
            }
            if (c.Name.Contains("BETA", StringComparison.OrdinalIgnoreCase)) s -= 0.5;
            // Your own country's channel, with commentary you understand.
            if (!SportService.IsHome(c)) s -= 3;
            if (c.Id == exclude) s -= 100;
            if (s > pickScore) { pickScore = s; pick = c; }
        }
        return pick;
    }

    public static List<object> SearchPrograms(Account a, string normalizedQuery, string[] tokens, int limit)
    {
        var guide = GuideService.Get(a);
        if (guide == null || tokens.Length == 0) return new();
        var streams = StreamsByEpg(a);
        var profile = BuildProfile(a.Id);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long horizon = now + TimeSpan.FromHours(36).Ticks / TimeSpan.TicksPerMillisecond;

        var seen = new HashSet<string>();
        var results = new List<(Programme p, CatalogItem c)>();
        foreach (var p in guide.All)
        {
            if (p.End <= now || p.Start > horizon) continue;
            var title = p.NormalizedTitle;
            bool all = true;
            foreach (var t in tokens)
            {
                if (!ContainsWordPrefix(title, t)) { all = false; break; }
            }
            if (!all || !streams.TryGetValue(p.EpgId, out var channels)) continue;
            if (!seen.Add(title + "|" + p.Start / 60000)) continue;
            var channel = PickChannel(channels, profile, null);
            if (channel != null) results.Add((p, channel));
        }
        return results
            .OrderByDescending(r => r.p.Start <= now)
            .ThenBy(r => r.p.Start)
            .Take(limit)
            .Select(r => (object)new
            {
                program = new ProgramView { Title = r.p.Title, Description = r.p.Description, Start = r.p.Start, End = r.p.End, Image = r.p.Image },
                channel = r.c,
                live = r.p.Start <= now
            })
            .ToList();
    }

    private static bool ContainsWordPrefix(string text, string token)
    {
        int i = 0;
        while ((i = text.IndexOf(token, i, StringComparison.Ordinal)) >= 0)
        {
            if (i == 0 || text[i - 1] == ' ') return true;
            i++;
        }
        return false;
    }
}
