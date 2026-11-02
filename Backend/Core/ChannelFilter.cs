using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Spana.Backend.Iptv;

namespace Spana.Backend.Core;

// Channels kept out of every list: adult content when that is hidden, and names matching the viewer's patterns ("AlienPlay: *").
public static class ChannelFilter
{
    private sealed record Compiled(List<string> Source, Regex? Regex, string Stamp);

    private sealed record GuideIds(Guide Guide, long Bucket, HashSet<string> Ids);

    private static Compiled _compiled = new([], null, "none");
    private static GuideIds? _guideIds;

    // Channels that carry their event in the name pass needsGuide: false, since they never have a guide.
    public static bool Hidden(CatalogItem item, bool live = true, bool needsGuide = true)
    {
        var s = SettingsService.Current;
        if (s.HideAdult && item.Adult) return true;
        if (!live) return false;
        if (needsGuide && s.HideNoGuide && !HasGuide(item)) return true;
        return Get(s.HiddenChannels).Regex?.IsMatch(item.Name) == true;
    }

    public static bool Active => Get(SettingsService.Current.HiddenChannels).Regex != null || NoGuideActive;

    // Changes whenever the patterns, or the guide while channels without one are hidden, do; for cache keys.
    public static string Stamp
    {
        get
        {
            string stamp = Get(SettingsService.Current.HiddenChannels).Stamp;
            if (!SettingsService.Current.HideNoGuide) return stamp;
            var guide = SettingsService.ActiveAccount is { } a ? GuideService.Peek(a) : null;
            return $"{stamp}-g{guide?.FetchedAt.ToUnixTimeMilliseconds() ?? 0}-{guide?.ByChannel.Count ?? 0}";
        }
    }

    private static bool NoGuideActive =>
        SettingsService.Current.HideNoGuide && SettingsService.ActiveAccount is { } a && GuideService.Peek(a) != null;

    // True until a guide is loaded, so nothing disappears while it is on its way.
    public static bool HasGuide(CatalogItem item)
    {
        var ids = GuidedIds();
        return ids == null || (item.EpgId != null && ids.Contains(item.EpgId));
    }

    // Guide ids with something on now or later, rebuilt for each new guide and every ten minutes.
    private static HashSet<string>? GuidedIds()
    {
        if (SettingsService.ActiveAccount is not { } a || GuideService.Peek(a) is not { } guide) return null;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long bucket = now / 600_000;
        var c = _guideIds;
        if (c != null && c.Guide == guide && c.Bucket == bucket) return c.Ids;
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, list) in guide.ByChannel)
            if (list.Exists(p => p.End > now)) ids.Add(key);
        _guideIds = new GuideIds(guide, bucket, ids);
        return ids;
    }

    public static List<string> Clean(IEnumerable<string?> patterns) =>
        patterns.Select(p => p?.Trim() ?? "").Where(p => p.Length > 0 && p.Length <= 200)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(500).ToList();

    private static Compiled Get(List<string> patterns)
    {
        var c = _compiled;
        if (ReferenceEquals(c.Source, patterns)) return c;
        c = Compile(patterns);
        _compiled = c;
        return c;
    }

    public static Regex ToRegex(IEnumerable<string> patterns)
    {
        var parts = patterns.Select(p => Regex.Escape(p).Replace(@"\*", ".*").Replace(@"\?", "."));
        return new Regex($"^(?:{string.Join('|', parts)})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static Compiled Compile(List<string> patterns)
    {
        if (patterns.Count == 0) return new(patterns, null, "none");
        var regex = ToRegex(patterns);
        string stamp = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', patterns))))[..12];
        return new(patterns, regex, stamp);
    }
}
