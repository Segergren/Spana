using System.Globalization;
using System.Text.Json;

namespace Spana.Backend.Iptv;

// Xtream panels are loose with types: numbers arrive as strings, arrays as objects, and fields go missing.
internal static class J
{
    public static JsonElement? Prop(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;

    public static string? Str(this JsonElement e, string name)
    {
        var v = e.Prop(name);
        if (v == null) return null;
        return v.Value.ValueKind switch
        {
            JsonValueKind.String => NullIfEmpty(v.Value.GetString()),
            JsonValueKind.Number => v.Value.GetRawText(),
            JsonValueKind.True => "1",
            JsonValueKind.False => "0",
            JsonValueKind.Array => v.Value.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : null).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)),
            _ => null
        };
    }

    public static long Long(this JsonElement e, string name)
    {
        var s = e.Str(name);
        return s != null && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (long)d : 0;
    }

    public static double? Dbl(this JsonElement e, string name)
    {
        var s = e.Str(name);
        return s != null && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    public static bool Bool(this JsonElement e, string name)
    {
        var s = e.Str(name);
        return s == "1" || string.Equals(s, "true", StringComparison.OrdinalIgnoreCase);
    }

    public static IEnumerable<JsonElement> Items(this JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Array) return e.EnumerateArray();
        if (e.ValueKind == JsonValueKind.Object) return e.EnumerateObject().Select(p => p.Value);
        return [];
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
