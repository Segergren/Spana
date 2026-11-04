using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Serilog;
using Spana.Backend.Core;

namespace Spana.Backend.Ai;

public sealed class AiException(string message) : Exception(message);

public static partial class AiClient
{
    public static readonly string[] Providers = ["claude", "openai", "deepseek"];

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(15)
    })
    { Timeout = TimeSpan.FromMinutes(5) };

    public static string DefaultModel(string provider) => provider switch
    {
        "claude" => "claude-sonnet-5-5",
        "openai" => "gpt-6.1-sol",
        "deepseek" => "deepseek-flash",
        _ => ""
    };

    public static string NameOf(string provider) => provider switch
    {
        "claude" => "Claude",
        "openai" => "OpenAI",
        "deepseek" => "DeepSeek",
        _ => provider
    };

    public static bool CanSearch(string provider) => provider is "claude" or "openai";

    // Below medium Claude skips the web search; DeepSeek has no medium.
    public static string DefaultReasoning(string provider) => provider == "deepseek" ? "low" : "medium";

    public static string[] ReasoningLevels(string provider) =>
        provider == "deepseek" ? ["off", "low", "high"] : ["low", "medium", "high"];

    public static string ReasoningFor(string provider, string model, string? wanted)
    {
        var known = AiModels.Find(provider, model)?.Reasoning;
        IReadOnlyList<string> levels = known is { Count: > 0 } ? known : ReasoningLevels(provider);
        if (!string.IsNullOrEmpty(wanted) && levels.Contains(wanted)) return wanted;
        string fallback = DefaultReasoning(provider);
        return levels.Contains(fallback) ? fallback : levels.FirstOrDefault(l => l != "off") ?? levels[0];
    }

    public static string ModelFor(AiSettings s, string provider) =>
        s.Models.TryGetValue(provider, out var m) && !string.IsNullOrWhiteSpace(m) ? m.Trim() : DefaultModel(provider);

    // Listing the models checks the key without spending tokens.
    public static async Task Verify(string provider, string key, CancellationToken ct)
    {
        AiModels.Forget(provider);
        await AiModels.List(provider, key, ct);
    }

    internal static async Task<JsonNode> GetJson(string provider, string key, string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        Authorize(req, provider, key);
        using var res = await Http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) throw await Failure(provider, res, ct);
        return JsonNode.Parse(await res.Content.ReadAsStringAsync(ct)) ?? new JsonObject();
    }

    public static async Task<string> Complete(string provider, string key, string model, string reasoning, string system, string user, bool search,
        CancellationToken ct) =>
        provider switch
        {
            "claude" => await Claude(key, model, reasoning, system, user, search, ct),
            "openai" => await OpenAi(key, model, reasoning, system, user, search, ct),
            "deepseek" => await DeepSeek(key, model, reasoning, system, user, ct),
            _ => throw new AiException("Unknown provider")
        };

    private static void Authorize(HttpRequestMessage req, string provider, string key)
    {
        if (provider == "claude")
        {
            req.Headers.Add("x-api-key", key);
            req.Headers.Add("anthropic-version", "2023-06-01");
        }
        else
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
    }

    [GeneratedRegex(@"^claude-(opus|sonnet|haiku|fable|mythos)-(\d+)(?:-(\d))?(?!\d)")]
    private static partial Regex ClaudeModel();

    private static async Task<string> Claude(string key, string model, string reasoning, string system, string user, bool search, CancellationToken ct)
    {
        var m = ClaudeModel().Match(model);
        string family = m.Success ? m.Groups[1].Value : "";
        int major = m.Success ? int.Parse(m.Groups[2].Value) : 0;
        int minor = m.Success && m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
        bool current = major > 4 || (major == 4 && minor >= 6);
        bool fallbacks = (family is "opus" or "fable" && major >= 5) || (family == "sonnet" && (major > 5 || (major == 5 && minor >= 5)));

        var messages = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = user } };
        for (int turn = 0; turn < 5; turn++)
        {
            var body = new JsonObject
            {
                ["model"] = model,
                ["max_tokens"] = 16000,
                ["system"] = system,
                ["messages"] = messages.DeepClone()
            };
            if (reasoning == "off") body["thinking"] = new JsonObject { ["type"] = "disabled" };
            else if (current) body["output_config"] = new JsonObject { ["effort"] = reasoning };
            if (search)
                body["tools"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = current && family != "haiku" ? "web_search_20260209" : "web_search_20250305",
                        ["name"] = "web_search",
                        ["max_uses"] = 3
                    }
                };
            if (fallbacks) body["fallbacks"] = "default";

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages") { Content = Json(body) };
            Authorize(req, "claude", key);
            if (fallbacks) req.Headers.Add("anthropic-beta", "server-side-fallback-2026-07-01");
            using var res = await Http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) throw await Failure("claude", res, ct);

            var doc = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct))!;
            string? stop = doc["stop_reason"]?.GetValue<string>();
            var content = doc["content"] as JsonArray ?? new JsonArray();
            // Long web searches pause the turn; sending it back lets Claude continue.
            if (stop == "pause_turn")
            {
                messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = content.DeepClone() });
                continue;
            }
            Log.Information("Claude answered with {Searches} web searches", doc["usage"]?["server_tool_use"]?["web_search_requests"]?.GetValue<int>() ?? 0);
            if (stop == "refusal") throw new AiException("Claude declined the request.");
            if (stop == "max_tokens") throw new AiException("The answer was cut off. Try another model.");
            // Citations split the text into blocks; joined as is, the JSON stays whole.
            return string.Concat(content.Where(b => b?["type"]?.GetValue<string>() == "text").Select(b => b!["text"]?.GetValue<string>()));
        }
        throw new AiException("Claude did not finish searching.");
    }

    [GeneratedRegex(@"^(gpt-([5-9]|\d{2})|o\d)")]
    private static partial Regex ReasoningModel();

    private static async Task<string> OpenAi(string key, string model, string reasoning, string system, string user, bool search, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["instructions"] = system,
            ["input"] = user
        };
        if (search) body["tools"] = new JsonArray { new JsonObject { ["type"] = "web_search" } };
        bool reasons = AiModels.Find("openai", model)?.Reasoning is { } known ? known.Count > 0 : ReasoningModel().IsMatch(model);
        if (reasons) body["reasoning"] = new JsonObject { ["effort"] = reasoning };

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses") { Content = Json(body) };
        Authorize(req, "openai", key);
        using var res = await Http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) throw await Failure("openai", res, ct);

        var doc = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct))!;
        if (doc["status"]?.GetValue<string>() == "incomplete") throw new AiException("The answer was cut off. Try another model.");
        var output = doc["output"] as JsonArray ?? new JsonArray();
        Log.Information("OpenAI answered with {Searches} web searches", output.Count(i => i?["type"]?.GetValue<string>() == "web_search_call"));
        var sb = new StringBuilder();
        foreach (var item in output)
        {
            if (item?["type"]?.GetValue<string>() != "message") continue;
            foreach (var part in item["content"] as JsonArray ?? new JsonArray())
            {
                if (part?["type"]?.GetValue<string>() == "refusal") throw new AiException("OpenAI declined the request.");
                if (part?["type"]?.GetValue<string>() == "output_text") sb.Append(part["text"]?.GetValue<string>());
            }
        }
        return sb.ToString();
    }

    private static async Task<string> DeepSeek(string key, string model, string reasoning, string system, string user, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = user }
            },
            ["response_format"] = new JsonObject { ["type"] = "json_object" },
            ["max_tokens"] = 32000
        };
        if (reasoning == "off") body["thinking"] = new JsonObject { ["type"] = "disabled" };
        else body["reasoning_effort"] = reasoning;
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.deepseek.com/chat/completions") { Content = Json(body) };
        Authorize(req, "deepseek", key);
        using var res = await Http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) throw await Failure("deepseek", res, ct);

        var doc = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct))!;
        var choice = doc["choices"]?[0];
        if (choice?["finish_reason"]?.GetValue<string>() == "length") throw new AiException("The answer was cut off. Try another model.");
        return choice?["message"]?["content"]?.GetValue<string>() ?? "";
    }

    private static StringContent Json(JsonNode body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");

    private static async Task<AiException> Failure(string provider, HttpResponseMessage res, CancellationToken ct)
    {
        string text = "";
        try { text = await res.Content.ReadAsStringAsync(ct); } catch (Exception) { }
        string? detail = null;
        try { detail = JsonNode.Parse(text)?["error"]?["message"]?.GetValue<string>(); } catch (Exception) { }
        Log.Warning("{Provider} answered {Status}: {Body}", provider, (int)res.StatusCode, text.Length > 500 ? text[..500] : text);
        string name = NameOf(provider);
        return (int)res.StatusCode switch
        {
            401 or 403 => new AiException($"{name} did not accept the API key."),
            402 => new AiException($"The {name} account is out of credit."),
            429 => new AiException($"{name} is rate limiting the key or it is out of credit." + (detail != null ? " " + detail : "")),
            _ => new AiException($"{name} answered {(int)res.StatusCode}" + (detail != null ? ": " + detail : "."))
        };
    }

    public static JsonElement? ParseJson(string text)
    {
        int end = text.LastIndexOf('}');
        for (int start = text.IndexOf('{'); start >= 0 && start < end; start = text.IndexOf('{', start + 1))
        {
            try
            {
                using var doc = JsonDocument.Parse(text[start..(end + 1)]);
                return doc.RootElement.Clone();
            }
            catch (JsonException) { }
        }
        return null;
    }

    // Only the country name is kept.
    public static async Task<string?> LookupCountry(CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://ipapi.co/json/");
            req.Headers.UserAgent.ParseAdd("Spana");
            using var res = await Http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            return doc.RootElement.TryGetProperty("country_name", out var c) && c.GetString() is { Length: > 0 } name ? name : null;
        }
        catch (Exception ex)
        {
            Log.Warning("Country lookup failed: {Message}", ex.Message);
            return null;
        }
    }
}
