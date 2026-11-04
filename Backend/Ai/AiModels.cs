using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Spana.Backend.Ai;

public sealed class AiModel
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Reasoning { get; set; } = new();
}

public static partial class AiModels
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(6);
    private static readonly ConcurrentDictionary<string, (DateTimeOffset at, List<AiModel> list)> Cache = new();
    private static readonly string[] Levels = ["low", "medium", "high"];

    // Chat models only, without pro, codex or dated snapshots.
    [GeneratedRegex(@"^(gpt-([5-9]|\d{2})(\.\d+)?(-(mini|nano|luna|sol|terra|astra))?|o\d(-mini)?)$")]
    private static partial Regex OpenAiChat();

    public static AiModel? Find(string provider, string model) =>
        Cache.TryGetValue(provider, out var c) ? c.list.FirstOrDefault(m => m.Id == model) : null;

    public static void Forget(string provider) => Cache.TryRemove(provider, out _);

    public static async Task<List<AiModel>> List(string provider, string key, CancellationToken ct)
    {
        if (Cache.TryGetValue(provider, out var c) && DateTimeOffset.UtcNow - c.at < MaxAge) return c.list;
        var list = provider switch
        {
            "claude" => Claude(await AiClient.GetJson(provider, key, "https://api.anthropic.com/v1/models?limit=100", ct)),
            "openai" => OpenAi(await AiClient.GetJson(provider, key, "https://api.openai.com/v1/models", ct)),
            "deepseek" => DeepSeek(await AiClient.GetJson(provider, key, "https://api.deepseek.com/models", ct)),
            _ => new List<AiModel>()
        };
        Cache[provider] = (DateTimeOffset.UtcNow, list);
        return list;
    }

    private static IEnumerable<JsonNode> Data(JsonNode doc) => (doc["data"] as JsonArray ?? new JsonArray()).OfType<JsonNode>();

    private static bool Supported(JsonNode? node) => node is JsonObject o && o["supported"] is JsonValue v && v.TryGetValue(out bool b) && b;

    private static List<AiModel> Claude(JsonNode doc) =>
        Data(doc)
            .Where(m => m["lifecycle"]?.GetValue<string>() is null or "active")
            .Select(m =>
            {
                var caps = m["capabilities"];
                var reasoning = new List<string>();
                if (Supported(caps?["thinking"]?["types"]?["disabled"])) reasoning.Add("off");
                if (caps?["effort"] is JsonObject effort && Supported(effort)) reasoning.AddRange(Levels.Where(l => Supported(effort[l])));
                string id = m["id"]!.GetValue<string>();
                return new AiModel { Id = id, Name = m["display_name"]?.GetValue<string>() ?? id, Reasoning = reasoning };
            })
            .ToList();

    // OpenAI lists only ids, not reasoning levels.
    private static List<AiModel> OpenAi(JsonNode doc) =>
        Data(doc)
            .Select(m => (id: m["id"]!.GetValue<string>(), created: m["created"]?.GetValue<long>() ?? 0))
            .Where(m => OpenAiChat().IsMatch(m.id))
            .OrderByDescending(m => m.created)
            .Select(m => new AiModel { Id = m.id, Name = OpenAiName(m.id), Reasoning = [.. Levels] })
            .ToList();

    // "gpt-6.1-terra" reads as "GPT 6.1 Terra", "o4-mini" as "o4 Mini".
    private static string OpenAiName(string id) =>
        string.Join(' ', id.Split('-').Select(w => w == "gpt" ? "GPT" : w.Length > 0 && char.IsLetter(w[0]) && !OSeries().IsMatch(w) ? char.ToUpperInvariant(w[0]) + w[1..] : w));

    [GeneratedRegex(@"^o\d$")]
    private static partial Regex OSeries();

    private static List<AiModel> DeepSeek(JsonNode doc) =>
        Data(doc)
            .Select(m =>
            {
                string id = m["id"]!.GetValue<string>();
                var levels = (m["effort"]?["supported_levels"] as JsonArray ?? new JsonArray())
                    .Select(l => l?.GetValue<string>()).Where(l => l != null && Levels.Contains(l)).Select(l => l!);
                var reasoning = levels.Any() ? ["off", .. levels] : AiClient.ReasoningLevels("deepseek").ToList();
                return new AiModel { Id = id, Name = (m["name"]?.GetValue<string>() ?? id).Replace('-', ' '), Reasoning = reasoning };
            })
            .ToList();
}
