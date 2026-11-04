using System.Globalization;
using Spana.Backend.Core;

namespace Spana.Backend.Iptv;

// Another channel showing the same match or programme.
public sealed class Broadcast
{
    public CatalogItem Channel { get; set; } = new();
    public ProgramView Program { get; set; } = new();
    public string? Language { get; set; }
}

// Commentary language from the channel's country, ranked by what the viewer understands.
public static class Languages
{
    private static readonly Dictionary<string, string> ByCountry = new(StringComparer.OrdinalIgnoreCase)
    {
        ["se"] = "sv", ["no"] = "no", ["dk"] = "da", ["fi"] = "fi", ["is"] = "is",
        ["uk"] = "en", ["gb"] = "en", ["ie"] = "en", ["us"] = "en", ["ca"] = "en", ["au"] = "en", ["nz"] = "en", ["za"] = "en",
        ["de"] = "de", ["at"] = "de", ["ch"] = "de", ["fr"] = "fr", ["be"] = "fr", ["lu"] = "fr", ["nl"] = "nl",
        ["it"] = "it", ["es"] = "es", ["mx"] = "es", ["ar"] = "es", ["cl"] = "es", ["co"] = "es", ["pe"] = "es",
        ["pt"] = "pt", ["br"] = "pt", ["pl"] = "pl", ["ro"] = "ro", ["md"] = "ro", ["hu"] = "hu", ["cz"] = "cs", ["sk"] = "sk",
        ["hr"] = "hr", ["rs"] = "sr", ["ba"] = "bs", ["si"] = "sl", ["bg"] = "bg", ["gr"] = "el", ["cy"] = "el",
        ["tr"] = "tr", ["ru"] = "ru", ["ua"] = "uk", ["by"] = "ru", ["ee"] = "et", ["lv"] = "lv", ["lt"] = "lt", ["al"] = "sq",
        ["mk"] = "mk", ["il"] = "he", ["in"] = "hi", ["pk"] = "ur", ["ir"] = "fa", ["jp"] = "ja", ["kr"] = "ko", ["cn"] = "zh",
        ["me"] = "sr", ["xk"] = "sq", ["id"] = "id", ["th"] = "th", ["vn"] = "vi",
        ["sa"] = "ar", ["ae"] = "ar", ["qa"] = "ar", ["eg"] = "ar", ["ma"] = "ar", ["dz"] = "ar", ["tn"] = "ar", ["kw"] = "ar",
        ["jo"] = "ar", ["lb"] = "ar", ["iq"] = "ar", ["om"] = "ar", ["bh"] = "ar", ["sy"] = "ar", ["ly"] = "ar", ["ye"] = "ar"
    };

    /// <summary>The language the viewer picked, or the one of their region.</summary>
    public static string Preferred =>
        SettingsService.Current.ContentLanguage is { Length: > 0 } picked ? picked : Auto;

    public static string Auto { get; } = Of(ChannelNames.HomeCode) ?? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

    public static string? Of(string? country) => country != null && ByCountry.TryGetValue(country, out var l) ? l : null;

    public static string? Of(CatalogItem c) => Of(c.Country);

    /// <summary>0 for the viewer's language, 1 for English, 2 when unknown, 3 for anything else.</summary>
    public static int Rank(CatalogItem c)
    {
        string? lang = Of(c);
        if (lang == null) return 2;
        if (lang == Preferred) return 0;
        return lang == "en" ? 1 : 3;
    }

    public static string Name(string code)
    {
        try { return CultureInfo.GetCultureInfo(code).EnglishName; }
        catch (CultureNotFoundException) { return code; }
    }

    public static Broadcast Of(CatalogItem channel, ProgramView program) =>
        new() { Channel = channel, Program = program, Language = Of(channel) };
}
