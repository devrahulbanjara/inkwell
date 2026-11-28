using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Inkwell.Core;

namespace Inkwell.Providers;

public sealed record RawResult(string Text, int InputTokens, int OutputTokens)
{
    /// <summary>Plan-usage reading a CLI reported alongside the reply, if any.</summary>
    public CliLimitInfo? Limit { get; init; }
}

public sealed record RemoteModel(string Id, string DisplayName);

/// <summary>
/// One-shot HTTP calls to Gemini and Groq. No retries here — the router decides
/// what to retry, which key to rotate to and which model to fall back to.
/// </summary>
public static class HTTPProviders
{
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(25) // per request, like the Mac app; the router has a 75 s overall budget
    };

    // MARK: Completion

    public static async Task<RawResult> CompleteAsync(ProviderKind kind, string key, string model, string system, string user)
    {
        return kind switch
        {
            ProviderKind.Gemini => await GeminiAsync(key, model, system, user),
            ProviderKind.Groq => await GroqAsync(key, model, system, user, tuned: true),
            _ => throw AIException.NotConfigured("Not an API provider")
        };
    }

    // MARK: Gemini

    private static async Task<RawResult> GeminiAsync(string key, string model, string system, string user)
    {
        var path = Uri.EscapeDataString(model);
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{path}:generateContent";

        var body = JsonSerializer.Serialize(new
        {
            system_instruction = new { parts = new[] { new { text = system } } },
            contents = new[] { new { role = "user", parts = new[] { new { text = user } } } },
            generationConfig = new { temperature = 0.3 }
        });

        var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        req.Headers.Add("x-goog-api-key", key);

        var data = await SendAsync(req, ProviderKind.Gemini, model);
        using var doc = JsonDocument.Parse(data);
        var root = doc.RootElement;

        // Check prompt feedback
        if (root.TryGetProperty("promptFeedback", out var pf) &&
            pf.TryGetProperty("blockReason", out var br))
            throw AIException.Blocked(br.GetString()?.ToLowerInvariant() ?? "unknown");

        // Parse candidate
        string text = "";
        int inputTokens = 0, outputTokens = 0;

        if (root.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
        {
            var candidate = candidates[0];
            if (candidate.TryGetProperty("finishReason", out var fr))
            {
                var reason = fr.GetString() ?? "";
                if (reason is "SAFETY" or "PROHIBITED_CONTENT" or "BLOCKLIST" or "SPII")
                    throw AIException.Blocked(reason.ToLowerInvariant());
            }
            if (candidate.TryGetProperty("content", out var content) &&
                content.TryGetProperty("parts", out var parts))
            {
                foreach (var part in parts.EnumerateArray())
                {
                    if (part.TryGetProperty("thought", out var th) && th.GetBoolean()) continue;
                    if (part.TryGetProperty("text", out var t)) text += t.GetString();
                }
            }
        }

        if (root.TryGetProperty("usageMetadata", out var usage))
        {
            inputTokens = usage.TryGetProperty("promptTokenCount", out var pt) ? pt.GetInt32() : 0;
            outputTokens = (usage.TryGetProperty("candidatesTokenCount", out var ct) ? ct.GetInt32() : 0)
                         + (usage.TryGetProperty("thoughtsTokenCount", out var tt) ? tt.GetInt32() : 0);
        }

        return new RawResult(text, inputTokens, outputTokens);
    }

    // MARK: Groq (OpenAI-compatible)

    private static async Task<RawResult> GroqAsync(string key, string model, string system, string user, bool tuned)
    {
        try
        {
            return await GroqInternalAsync(key, model, system, user, tuned);
        }
        catch (AIException ex) when (tuned && ex.ShortKind == "bad_request" &&
            (ex.Message.Contains("reason", StringComparison.OrdinalIgnoreCase)
             || ex.Message.Contains("property", StringComparison.OrdinalIgnoreCase)))
        {
            return await GroqInternalAsync(key, model, system, user, false);
        }
    }

    private static async Task<RawResult> GroqInternalAsync(string key, string model, string system, string user, bool tuned)
    {
        var bodyDict = new Dictionary<string, object>
        {
            ["model"] = model,
            ["temperature"] = 0.3,
            ["messages"] = new[]
            {
                new Dictionary<string, string> { ["role"] = "system", ["content"] = system },
                new Dictionary<string, string> { ["role"] = "user", ["content"] = user }
            }
        };

        if (tuned)
        {
            if (model.Contains("gpt-oss"))
            {
                bodyDict["reasoning_effort"] = "low";
                bodyDict["include_reasoning"] = false;
            }
            else if (model.Contains("qwen"))
            {
                bodyDict["reasoning_format"] = "hidden";
            }
        }

        var req = new HttpRequestMessage(HttpMethod.Post, "https://api.groq.com/openai/v1/chat/completions");
        req.Content = new StringContent(JsonSerializer.Serialize(bodyDict), Encoding.UTF8, "application/json");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        var data = await SendAsync(req, ProviderKind.Groq, model);
        using var doc = JsonDocument.Parse(data);
        var root = doc.RootElement;

        string text = "";
        int inputTokens = 0, outputTokens = 0;

        if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            var choice = choices[0];
            if (choice.TryGetProperty("finish_reason", out var fr) && fr.GetString() == "content_filter")
                throw AIException.Blocked("content filter");
            if (choice.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var c))
                text = c.GetString() ?? "";
        }

        if (root.TryGetProperty("usage", out var usage))
        {
            inputTokens = usage.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt32() : 0;
            outputTokens = usage.TryGetProperty("completion_tokens", out var ct) ? ct.GetInt32() : 0;
        }

        return new RawResult(text, inputTokens, outputTokens);
    }

    // MARK: Model lists (also used to validate a key)

    public static async Task<List<RemoteModel>> ListModelsAsync(ProviderKind kind, string key)
    {
        switch (kind)
        {
            case ProviderKind.Gemini:
            {
                var req = new HttpRequestMessage(HttpMethod.Get,
                    "https://generativelanguage.googleapis.com/v1beta/models?pageSize=1000");
                req.Headers.Add("x-goog-api-key", key);
                var data = await SendAsync(req, ProviderKind.Gemini, "");
                using var doc = JsonDocument.Parse(data);
                var models = new List<RemoteModel>();
                if (doc.RootElement.TryGetProperty("models", out var arr))
                {
                    foreach (var m in arr.EnumerateArray())
                    {
                        var methods = m.TryGetProperty("supportedGenerationMethods", out var sg)
                            ? sg.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
                            : new List<string>();
                        if (methods.Count > 0 && !methods.Contains("generateContent")) continue;
                        var name = m.GetProperty("name").GetString() ?? "";
                        var id = name.Replace("models/", "");
                        var display = m.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? name : name;
                        if (ModelCatalog.IsChatModel(id, ProviderKind.Gemini))
                            models.Add(new RemoteModel(id, display));
                    }
                }
                return models;
            }
            case ProviderKind.Groq:
            {
                var req = new HttpRequestMessage(HttpMethod.Get, "https://api.groq.com/openai/v1/models");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                var data = await SendAsync(req, ProviderKind.Groq, "");
                using var doc = JsonDocument.Parse(data);
                var models = new List<RemoteModel>();
                if (doc.RootElement.TryGetProperty("data", out var arr))
                {
                    foreach (var m in arr.EnumerateArray())
                    {
                        var active = !m.TryGetProperty("active", out var a) || a.GetBoolean();
                        if (!active) continue;
                        var id = m.GetProperty("id").GetString() ?? "";
                        if (ModelCatalog.IsChatModel(id, ProviderKind.Groq))
                            models.Add(new RemoteModel(id, id));
                    }
                }
                return models;
            }
            default:
                return new();
        }
    }

    // MARK: Transport + error classification

    private static async Task<string> SendAsync(HttpRequestMessage req, ProviderKind kind, string model)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await Client.SendAsync(req);
        }
        catch (Exception ex)
        {
            throw AIException.From(ex);
        }

        var body = await resp.Content.ReadAsStringAsync();
        if (resp.IsSuccessStatusCode) return body;

        throw Classify((int)resp.StatusCode, body, resp.Headers, kind, model);
    }

    /// <summary>
    /// Gemini: QuotaFailure.quotaId says PerDay / PerMinute; daily quotas reset at midnight Pacific (its retryDelay
    /// can say "1s" even then, so it's ignored for daily limits). Groq: "(RPD)"/"(TPD)" = daily, "(RPM)"/"(TPM)" =
    /// per minute, and the message says "Please try again in 6m50.8s".
    /// </summary>
    private static AIException RateLimit(ProviderKind kind, string msg, string lower, List<string> quotaIDs,
        TimeSpan? headerRetry, TimeSpan? retryDelay)
    {
        var daily = quotaIDs.Any(q => q.Contains("perday", StringComparison.OrdinalIgnoreCase))
            || lower.Contains("per day") || lower.Contains("(rpd)") || lower.Contains("(tpd)") || lower.Contains("(asd)")
            || lower.Contains("quota_exceeded") || lower.Contains("daily");

        TimeSpan? wait = headerRetry;
        var m = System.Text.RegularExpressions.Regex.Match(msg, @"(?:try again in|retry in)\s+([0-9hms.]+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (wait is null && m.Success) wait = DurationParser.Parse(m.Groups[1].Value);
        if (wait is null && !(daily && kind == ProviderKind.Gemini)) wait = retryDelay;
        if (daily && kind == ProviderKind.Gemini) wait = null; // AIRouter.DailyReset → next midnight Pacific

        var resets = wait is { TotalSeconds: > 0 } w ? DateTime.Now.Add(w) : (DateTime?)null;
        if (daily && kind == ProviderKind.Gemini) resets = AIRouter.DailyReset(kind, null);
        return new RateLimitException(wait, daily, msg, daily ? "daily" : "per-minute", daily ? resets : null);
    }

    internal static AIException Classify(int status, string body, HttpResponseHeaders headers,
        ProviderKind kind, string model)
    {
        var msg = "";
        var code = "";
        var errorStatus = "";
        TimeSpan? retryDelay = null;
        var quotaIDs = new List<string>();

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            JsonElement err;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
            {
                var first = root[0];
                err = first.TryGetProperty("error", out var e) ? e : first;
            }
            else
            {
                err = root.TryGetProperty("error", out var e) ? e : root;
            }
            msg = err.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            code = err.TryGetProperty("code", out var c) ? c.ToString() : "";
            errorStatus = err.TryGetProperty("status", out var s) ? s.GetString() ?? "" :
                (err.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "");

            if (err.TryGetProperty("details", out var details))
            {
                foreach (var d in details.EnumerateArray())
                {
                    if (d.TryGetProperty("retryDelay", out var rd))
                        retryDelay = DurationParser.Parse(rd.GetString());
                    if (d.TryGetProperty("violations", out var violations))
                    {
                        foreach (var v in violations.EnumerateArray())
                        {
                            if (v.TryGetProperty("quotaId", out var qi)) quotaIDs.Add(qi.GetString() ?? "");
                            if (v.TryGetProperty("quotaMetric", out var qm)) quotaIDs.Add(qm.GetString() ?? "");
                        }
                    }
                }
            }
        }
        catch { msg = body.Length > 300 ? body[..300] : body; }

        if (string.IsNullOrEmpty(msg)) msg = $"HTTP {status}";
        var lower = $"{msg} {code} {errorStatus}".ToLowerInvariant();

        var headerRetry = headers.TryGetValues("retry-after", out var retryValues)
            ? DurationParser.Parse(retryValues.FirstOrDefault())
            : null;

        return status switch
        {
            401 or 403 => AIException.InvalidKey(msg),
            404 => AIException.ModelUnavailable(model),
            413 => kind == ProviderKind.Groq ? AIException.TooLargeForModel(model) : AIException.InputTooLarge(),
            429 when lower.Contains("limit: 0") || lower.Contains("limit 0")
                => AIException.ModelUnavailable($"{model} (no free quota)"),
            429 when kind == ProviderKind.Groq && lower.Contains("request too large")
                => AIException.TooLargeForModel(model),
            429 => RateLimit(kind, msg, lower, quotaIDs, headerRetry, retryDelay),
            400 or 422 when lower.Contains("api key not valid") || lower.Contains("api_key_invalid") || lower.Contains("invalid api key")
                => AIException.InvalidKey(msg),
            400 or 422 when lower.Contains("decommissioned") || lower.Contains("model_not_found")
                || (lower.Contains("model") && (lower.Contains("not found") || lower.Contains("not supported") || lower.Contains("does not exist")))
                => AIException.ModelUnavailable(model),
            400 or 422 when lower.Contains("context") || lower.Contains("too long") || lower.Contains("too large") || lower.Contains("exceeds")
                => AIException.InputTooLarge(),
            400 or 422 => AIException.BadRequest(msg),
            408 or 504 => AIException.Timeout(),
            503 when lower.Contains("high demand") || lower.Contains("overloaded") || lower.Contains("unavailable")
                => AIException.Overloaded(model),
            498 or 500 or 502 or 503 => AIException.Server(status, msg),
            _ => status >= 500 ? AIException.Server(status, msg) : AIException.BadRequest(msg)
        };
    }
}
