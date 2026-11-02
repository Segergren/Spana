using Spana.Backend.Core;

namespace Spana.Backend.Iptv;

public sealed class SearchIndex
{
    private readonly MediaKind _kind;
    private readonly CatalogItem[] _items;
    private readonly string[] _names;
    private readonly string[][] _words;
    private readonly Dictionary<string, int> _byId;

    public SearchIndex(MediaKind kind, List<CatalogItem> items)
    {
        _kind = kind;
        _items = items.ToArray();
        _names = new string[_items.Length];
        _words = new string[_items.Length][];
        _byId = new Dictionary<string, int>(_items.Length, StringComparer.Ordinal);
        for (int i = 0; i < _items.Length; i++)
        {
            _names[i] = CatalogService.Normalize(StripTags(_items[i].Name));
            _words[i] = CatalogService.Normalize(_items[i].Name).Split(' ');
            _byId.TryAdd(_items[i].Id, i);
        }
    }

    public MediaKind Kind => _kind;

    public IReadOnlyList<CatalogItem> All => _items;

    public CatalogItem? Find(string id) => _byId.TryGetValue(id, out int i) ? _items[i] : null;

    public IEnumerable<(CatalogItem item, double score)> Search(string normalizedQuery, string[] tokens)
    {
        for (int i = 0; i < _items.Length; i++)
        {
            var item = _items[i];
            if (ChannelFilter.Hidden(item, _kind == MediaKind.Live)) continue;
            double score = Score(_names[i], _words[i], normalizedQuery, tokens);
            if (score <= 0) continue;
            if (item.Rating is double r) score += Math.Min(r, 10) * 0.2;
            yield return (item, score);
        }
    }

    private static double Score(string name, string[] words, string query, string[] tokens)
    {
        if (name == query) return 100;
        if (name.StartsWith(query, StringComparison.Ordinal)) return 85 - Math.Min(name.Length - query.Length, 20) * 0.5;

        int prefixHits = 0, containsHits = 0;
        foreach (var t in tokens)
        {
            bool prefix = false;
            foreach (var w in words)
            {
                if (w.StartsWith(t, StringComparison.Ordinal)) { prefix = true; break; }
            }
            if (prefix) prefixHits++;
            else if (t.Length >= 3 && name.Contains(t, StringComparison.Ordinal)) containsHits++;
            else return 0;
        }

        double score = 40 + prefixHits * 10 + containsHits * 4;
        if (name.Contains(query, StringComparison.Ordinal)) score += 10;
        return score - Math.Min(words.Length, 15) * 0.3;
    }

    // Drops provider prefixes like "SE:", "|SE|" or "[4K]" so they do not pollute matching.
    private static string StripTags(string name)
    {
        var s = name.Trim();
        if (s.Length > 4 && s[0] == '|')
        {
            int end = s.IndexOf('|', 1);
            if (end > 0 && end <= 6) s = s[(end + 1)..];
        }
        int colon = s.IndexOf(':');
        if (colon is > 0 and <= 4 && s[..colon].All(char.IsUpper)) s = s[(colon + 1)..];
        return s;
    }
}
