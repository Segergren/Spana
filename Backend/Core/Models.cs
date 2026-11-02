using System.Text.Json.Serialization;

namespace Spana.Backend.Core;

public enum AccountKind
{
    Xtream,
    M3u
}

public sealed class Account
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public AccountKind Kind { get; set; } = AccountKind.Xtream;
    public string Server { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string M3uUrl { get; set; } = "";
    public AccountInfo? Info { get; set; }
}

public sealed class AccountInfo
{
    public string Status { get; set; } = "";
    public long? ExpiresAt { get; set; }
    public int MaxConnections { get; set; }
    public int ActiveConnections { get; set; }
    public bool IsTrial { get; set; }
    public DateTimeOffset CheckedAt { get; set; }
}

public sealed class Settings
{
    public List<Account> Accounts { get; set; } = new();
    public string? ActiveAccountId { get; set; }
    public string UserAgent { get; set; } = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/138.0.0.0 Safari/537.36";
    public int NetworkCachingMs { get; set; } = 1500;
    public int Volume { get; set; } = 80;
    public bool HardwareDecoding { get; set; } = true;
    public int AudioDelayMs { get; set; }
    public int LiveCacheHours { get; set; } = 6;
    public int VodCacheHours { get; set; } = 24;
    public bool HideAdult { get; set; } = true;
    // Channel names to hide, with * and ? wildcards.
    public List<string> HiddenChannels { get; set; } = new();
    public bool HideNoGuide { get; set; }
    public bool ShowVod { get; set; }
    public bool MatchData { get; set; } = true;
    public string PreferredAudioLanguage { get; set; } = "";
    public string PreferredSubtitleLanguage { get; set; } = "";
    // "stable" or "beta"
    public string UpdateChannel { get; set; } = "stable";
    public WindowState? Window { get; set; }
    public string? ImageKey { get; set; }
    // Audio track position the viewer picked per channel or title, so it is used again next time.
    public Dictionary<string, int> AudioChoices { get; set; } = new();
}

public sealed class WindowState
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Maximized { get; set; }
}

public enum MediaKind
{
    Live,
    Movie,
    Series,
    Episode
}

public sealed class Category
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Count { get; set; }
}

public sealed class CatalogItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Icon { get; set; }
    public string? IconAlt { get; set; }
    public string CategoryId { get; set; } = "";
    public int Num { get; set; }
    public double? Rating { get; set; }
    public long Added { get; set; }
    public string? Ext { get; set; }
    public string? EpgId { get; set; }
    public bool Archive { get; set; }
    public bool Adult { get; set; }
    public string? Url { get; set; }
    public string? Year { get; set; }
    public string? Genre { get; set; }
    public string? Plot { get; set; }
    public string? Backdrop { get; set; }
    // Lines of video the name or playlist promises: 1080 for "FHD" or "(1080p)".
    public int? Quality { get; set; }
    public string? Country { get; set; }
    // Stream notes taken out of the name: geo, part-time, backup, offline, hevc, 50fps, 5.1, heading.
    public List<string>? Tags { get; set; }
    public string? UserAgent { get; set; }
    public string? Referrer { get; set; }
}

public sealed class LibraryEntry
{
    public string Key { get; set; } = "";
    public MediaKind Kind { get; set; }
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Icon { get; set; }
    public string? SeriesId { get; set; }
    public string? SeriesName { get; set; }
    public string? Subtitle { get; set; }
    public string? Ext { get; set; }
    public long PositionMs { get; set; }
    public long DurationMs { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class LiveWatch
{
    public string ChannelId { get; set; } = "";
    public string ChannelName { get; set; } = "";
    public string? EpgId { get; set; }
    public string? Title { get; set; }
    public string? League { get; set; }
    public string? LeagueName { get; set; }
    public string? Country { get; set; }
    public string? Sport { get; set; }
    public string? HomeId { get; set; }
    public string? AwayId { get; set; }
    public double Minutes { get; set; }
    public DateTimeOffset LastWatched { get; set; }
}

public sealed class Library
{
    public List<LibraryEntry> Favorites { get; set; } = new();
    public List<LibraryEntry> History { get; set; } = new();
    public List<LiveWatch> LiveWatches { get; set; } = new();
    // Interests removed in settings ("team:fm:123", "league:allsvenskan"), with when; viewing before then no longer counts.
    public Dictionary<string, DateTimeOffset> Forgotten { get; set; } = new();
}
