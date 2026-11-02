using System.Net;

namespace Spana.Backend.Core;

// Match data and its art come from Spana's API at spana.tv, which fetches and
// caches the public sports and guide sources for every client.
public static class SpanaApi
{
    public const string Base = "https://spana.tv/api";

    // Rewrites a source art URL to the Spana art proxy.
    public static string Image(string url) => $"{Base}/art?u={Uri.EscapeDataString(url)}";
}
