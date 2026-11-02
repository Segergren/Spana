using System.Globalization;
using System.Text.RegularExpressions;

namespace Spana.Backend.Iptv;

// Events named in channel names ("NFL 01ⓧ: Vikings vs Steelers (Sunday 9/28) 9:30AM ET"); only dated ones close to today count.
public static partial class EventChannels
{
    public readonly record struct Parsed(string Title, string Label, long Start);

    [GeneratedRegex(@"\b(?:mon|tue|wed|thu|fri|sat|sun)[a-z]*,?\s+(?<d>\d{1,2})(?:st|nd|rd|th)?\s+(?<mon>jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\.?(?:\s+(?<y>20\d\d))?", RegexOptions.IgnoreCase)]
    private static partial Regex DayMonth();

    [GeneratedRegex(@"\(\s*(?:mon|tue|wed|thu|fri|sat|sun)[a-z]*\s+(?<mo>\d{1,2})/(?<d>\d{1,2})\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex MonthSlashDay();

    [GeneratedRegex(@"(?<!\d)(?<h>\d{1,2})(?::(?<m>\d{2}))?(?::\d{2})?\s*(?<ap>am|pm)?(?:\s*(?<tz>et|est|edt|uk|bst|gmt|cet|cest))?\b", RegexOptions.IgnoreCase)]
    private static partial Regex Clock();

    [GeneratedRegex(@"no (?:scheduled )?event|no event scheduled|event scheduled", RegexOptions.IgnoreCase)]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"^[A-Z]{2}:\s+")]
    private static partial Regex CountryTag();

    private static readonly string[] Months = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];

    private static readonly TimeZoneInfo Eastern = Zone("America/New_York", "Eastern Standard Time");
    private static readonly TimeZoneInfo London = Zone("Europe/London", "GMT Standard Time");
    private static readonly TimeZoneInfo Central = Zone("Europe/Stockholm", "W. Europe Standard Time");

    private static TimeZoneInfo Zone(params string[] ids)
    {
        foreach (var id in ids)
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { }
        return TimeZoneInfo.Utc;
    }

    // The event in a channel name, when it is dated between yesterday and the day after tomorrow.
    public static Parsed? Parse(string name, DateTimeOffset now)
    {
        if (Placeholder().IsMatch(name)) return null;
        // "NFL 01ⓧ: ..." or "Matchroom Event 23 | ...": the channel's own label comes first.
        string body = CountryTag().Replace(name, "");
        int mark = body.IndexOf('ⓧ');
        int bar = body.IndexOf(" | ", StringComparison.Ordinal);
        // Cleaned names have lost the ⓧ: "US: NFL 01: Vikings vs Steelers (Sunday 9/28) 9:30AM ET".
        int colon = mark < 0 ? body.IndexOf(": ", StringComparison.Ordinal) : -1;
        if (colon > 24 || (bar > 0 && bar < colon)) colon = -1;
        string label = mark > 0 ? body[..mark] : colon > 0 ? body[..colon] : bar > 0 ? body[..bar] : "";
        string rest = mark > 0 ? body[(mark + 1)..].TrimStart(':', '|', ' ') : colon > 0 ? body[(colon + 2)..] : bar > 0 ? body[(bar + 3)..] : body;

        int day, month, year;
        bool hasYear;
        int afterDate;
        var dm = DayMonth().Match(rest);
        var md = MonthSlashDay().Match(rest);
        if (dm.Success)
        {
            day = int.Parse(dm.Groups["d"].Value);
            month = Array.IndexOf(Months, dm.Groups["mon"].Value[..3].ToLowerInvariant()) + 1;
            hasYear = dm.Groups["y"].Success;
            year = hasYear ? int.Parse(dm.Groups["y"].Value) : now.Year;
            afterDate = dm.Index + dm.Length;
        }
        else if (md.Success)
        {
            month = int.Parse(md.Groups["mo"].Value);
            day = int.Parse(md.Groups["d"].Value);
            // "(Sat 25/10)" is day first.
            if (month > 12 && day <= 12) (month, day) = (day, month);
            hasYear = false;
            year = now.Year;
            afterDate = md.Index + md.Length;
        }
        else return null;

        // The clock follows the date; "1:00PM UK/8:00AM ET" takes the first one with its zone.
        var clock = Clock().Matches(rest[afterDate..]).FirstOrDefault(c => c.Groups["m"].Success || c.Groups["ap"].Success);
        if (clock == null) return null;
        int hour = int.Parse(clock.Groups["h"].Value);
        int minute = clock.Groups["m"].Success ? int.Parse(clock.Groups["m"].Value) : 0;
        string ap = clock.Groups["ap"].Value.ToLowerInvariant();
        if (ap == "pm" && hour < 12) hour += 12;
        if (ap == "am" && hour == 12) hour = 0;
        if (hour > 23 || minute > 59 || month is < 1 or > 12 || day is < 1 or > 31) return null;
        var zone = clock.Groups["tz"].Value.ToLowerInvariant() switch
        {
            "et" or "est" or "edt" => Eastern,
            "cet" or "cest" => Central,
            _ => London
        };

        long? best = null;
        foreach (int y in hasYear ? [year] : new[] { year - 1, year, year + 1 })
        {
            if (day > DateTime.DaysInMonth(y, month)) continue;
            var local = new DateTime(y, month, day, hour, minute, 0, DateTimeKind.Unspecified);
            long ms = new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUnixTimeMilliseconds();
            if (best == null || Math.Abs(ms - now.ToUnixTimeMilliseconds()) < Math.Abs(best.Value - now.ToUnixTimeMilliseconds())) best = ms;
        }
        if (best == null) return null;
        var today = new DateTimeOffset(now.Date, now.Offset);
        if (best < today.AddDays(-1).ToUnixTimeMilliseconds() || best >= today.AddDays(3).ToUnixTimeMilliseconds()) return null;

        string text = rest[..(dm.Success ? dm.Index : md.Index)];
        return new Parsed(Describe(text), CatalogService.Normalize(label), best.Value);
    }

    // "Romania, Liga I | FC Botosani vs. FC Farul Constanta |" becomes "Romania, Liga I: FC Botosani vs. FC Farul Constanta".
    private static string Describe(string text)
    {
        var parts = text.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        int match = parts.FindIndex(p => p.Contains(" vs", StringComparison.OrdinalIgnoreCase) || p.Contains(" v ", StringComparison.OrdinalIgnoreCase));
        if (match > 0) return $"{parts[match - 1]}: {parts[match]}";
        if (match == 0) return parts[0];
        // "MotoGP: Germany Grand Prix"; a dash would read as two teams.
        return string.Join(": ", parts.Take(2));
    }
}
