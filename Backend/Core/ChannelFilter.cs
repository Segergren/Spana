using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Spana.Backend.Core;

// Channels kept out of every list: adult content when that is hidden, and names matching the viewer's patterns ("AlienPlay: *").
public static class ChannelFilter
{
    private sealed record Compiled(List<string> Source, Regex? Regex, string Stamp);

    private static Compiled _compiled = new([], null, "none");

    public static bool Hidden(CatalogItem item, bool live = true)
    {
        var s = SettingsService.Current;
        if (s.HideAdult && item.Adult) return true;
        return live && Get(s.HiddenChannels).Regex?.IsMatch(item.Name) == true;
    }

    public static bool Active => Get(SettingsService.Current.HiddenChannels).Regex != null;

    // Changes whenever the patterns do, for cache keys.
    public static string Stamp => Get(SettingsService.Current.HiddenChannels).Stamp;

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
