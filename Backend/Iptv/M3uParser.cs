using System.Text;
using System.Text.RegularExpressions;
using Spana.Backend.Core;

namespace Spana.Backend.Iptv;

public static partial class M3uParser
{
    [GeneratedRegex("([a-zA-Z0-9-]+)=\"([^\"]*)\"")]
    private static partial Regex AttrRegex();

    public sealed record Result(Dictionary<MediaKind, List<Category>> Categories, Dictionary<MediaKind, List<CatalogItem>> Items, List<string> GuideUrls);

    public static Result Parse(byte[] data)
    {
        var categories = new Dictionary<MediaKind, Dictionary<string, Category>>
        {
            [MediaKind.Live] = new(), [MediaKind.Movie] = new(), [MediaKind.Series] = new()
        };
        var items = new Dictionary<MediaKind, List<CatalogItem>>
        {
            [MediaKind.Live] = new(), [MediaKind.Movie] = new(), [MediaKind.Series] = new()
        };

        using var reader = new StringReader(Encoding.UTF8.GetString(data));
        string? line;
        Dictionary<string, string>? attrs = null;
        string? title = null, userAgent = null, referrer = null, extGroup = null;
        int num = 0;
        var guideUrls = new List<string>();
        while ((line = reader.ReadLine()) != null)
        {
            line = line.Trim();
            if (line.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase))
            {
                var header = AttrRegex().Matches(line).ToDictionary(m => m.Groups[1].Value.ToLowerInvariant(), m => m.Groups[2].Value);
                string? urls = header.GetValueOrDefault("x-tvg-url") ?? header.GetValueOrDefault("url-tvg");
                if (urls != null) guideUrls.AddRange(urls.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
            else if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
            {
                attrs = new Dictionary<string, string>();
                foreach (Match m in AttrRegex().Matches(line)) attrs[m.Groups[1].Value.ToLowerInvariant()] = m.Groups[2].Value;
                title = Title(line);
                userAgent = attrs.GetValueOrDefault("http-user-agent") ?? attrs.GetValueOrDefault("user-agent") ?? userAgent;
                referrer = attrs.GetValueOrDefault("http-referrer") ?? attrs.GetValueOrDefault("referer") ?? attrs.GetValueOrDefault("referrer") ?? referrer;
            }
            else if (line.StartsWith("#EXTVLCOPT:", StringComparison.OrdinalIgnoreCase))
            {
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string key = line[11..eq].Trim().ToLowerInvariant();
                string value = line[(eq + 1)..].Trim();
                if (key is "http-user-agent") userAgent = value;
                else if (key is "http-referrer" or "http-referer") referrer = value;
            }
            else if (line.StartsWith("#EXTGRP:", StringComparison.OrdinalIgnoreCase))
            {
                extGroup = line[8..].Trim();
            }
            else if (line.Length > 0 && !line.StartsWith('#') && attrs != null)
            {
                string url = line;
                // Kodi style headers: "http://host/live.m3u8|User-Agent=VLC&Referer=https://site/".
                int bar = url.IndexOf('|');
                if (bar > 0 && url.IndexOf('=', bar) > bar)
                {
                    foreach (var pair in url[(bar + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
                    {
                        int eq = pair.IndexOf('=');
                        if (eq <= 0) continue;
                        string key = pair[..eq].Trim().ToLowerInvariant();
                        string value = Uri.UnescapeDataString(pair[(eq + 1)..].Trim());
                        if (key == "user-agent") userAgent = value;
                        else if (key is "referer" or "referrer") referrer = value;
                    }
                    url = url[..bar];
                }

                // Series episodes in M3U have no grouping by show, so they are listed as movies.
                var kind = url.Contains("/movie/") || url.Contains("/series/") ? MediaKind.Movie : MediaKind.Live;
                string group = attrs.GetValueOrDefault("group-title") is { Length: > 0 } g ? g : extGroup is { Length: > 0 } eg ? eg : "Uncategorized";
                var cats = categories[kind];
                if (!cats.TryGetValue(group, out var cat))
                    cats[group] = cat = new Category { Id = "m" + cats.Count, Name = group };

                string? tvgName = attrs.GetValueOrDefault("tvg-name") is { Length: > 0 } tn ? tn : null;
                var info = ChannelNames.Parse(title is { Length: > 0 } ? title : tvgName ?? url);
                int? quality = info.Quality ?? (tvgName != null ? ChannelNames.Parse(tvgName).Quality : null) ?? ChannelNames.QualityFromUrl(url);
                string? epgId = attrs.GetValueOrDefault("tvg-id") is { Length: > 0 } tvgId ? tvgId : null;
                items[kind].Add(new CatalogItem
                {
                    Id = "u" + (++num),
                    Name = info.Name,
                    Icon = attrs.GetValueOrDefault("tvg-logo") is { Length: > 0 } logo ? logo : null,
                    CategoryId = cat.Id,
                    Num = num,
                    // Channels without a guide id are matched to guides by name.
                    EpgId = epgId ?? (kind == MediaKind.Live ? "n:" + CatalogService.Normalize(info.Name) : null),
                    Url = url,
                    Ext = Path.GetExtension(new Uri(url, UriKind.RelativeOrAbsolute).IsAbsoluteUri ? new Uri(url).AbsolutePath : url).TrimStart('.'),
                    Quality = kind == MediaKind.Live ? quality : null,
                    Country = kind == MediaKind.Live ? info.Country ?? ChannelNames.CountryOf(epgId) : null,
                    Tags = kind == MediaKind.Live ? info.Tags : null,
                    UserAgent = userAgent,
                    Referrer = referrer
                });
                attrs = null;
                userAgent = referrer = null;
            }
        }

        return new Result(categories.ToDictionary(k => k.Key, v => v.Value.Values.ToList()), items, guideUrls);
    }

    // The title follows the first comma after the attributes, so "Murder, She Wrote" survives and quoted values may hold commas.
    private static string? Title(string line)
    {
        bool quoted = false;
        for (int i = 8; i < line.Length; i++)
        {
            if (line[i] == '"') quoted = !quoted;
            else if (line[i] == ',' && !quoted) return line[(i + 1)..].Trim();
        }
        int comma = line.LastIndexOf(',');
        return comma >= 0 ? line[(comma + 1)..].Trim() : null;
    }
}
