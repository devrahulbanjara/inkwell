using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Inkwell.Core;

namespace Inkwell.Providers;

public static class Prompts
{
    public const string System =
        "You are Inkwell, a writing assistant that works inside any text field on the user's computer.\n" +
        "Apply the instruction to the user's text.\n" +
        "Reply with ONLY the final text — no preamble, no explanation, no surrounding quotes, no code fences.\n" +
        "Keep the same language as the input unless the instruction says otherwise.";

    public static string User(Template template, string text) =>
        $"Instruction: {template.Prompt}\n\nText:\n{text}";
}

public sealed record AIResult(string Text, int InputTokens, int OutputTokens,
    ProviderKind Provider, string Model, string? KeyLabel, int Attempts);

// MARK: Quota / cooldown tracking

public sealed class QuotaTracker
{
    public sealed record Block(DateTime Until, bool Daily);

    /// "keyID|model" → cooldown
    private readonly Dictionary<string, Block> _blocks = new();
    /// keyID → reason the key was rejected
    public Dictionary<Guid, string> InvalidKeys { get; } = new();
    /// Last time each key succeeded
    public Dictionary<Guid, DateTime> LastSuccess { get; } = new();

    public event Action? Changed;

    private string Slot(Guid? key, string model) => $"{key?.ToString() ?? "cli"}|{model}";

    public DateTime? BlockedUntil(Guid? key, string model)
    {
        var s = Slot(key, model);
        if (!_blocks.TryGetValue(s, out var b)) return null;
        if (b.Until <= DateTime.Now) { _blocks.Remove(s); return null; }
        return b.Until;
    }

    public void SetBlock(Guid? key, string model, DateTime until, bool daily)
    {
        _blocks[Slot(key, model)] = new Block(until, daily);
        Changed?.Invoke();
    }

    public void MarkInvalid(Guid key, string reason) { InvalidKeys[key] = reason; Changed?.Invoke(); }
    public void ClearInvalid(Guid key) { InvalidKeys.Remove(key); Changed?.Invoke(); }

    public void MarkSuccess(Guid? key, string model)
    {
        _blocks.Remove(Slot(key, model));
        if (key.HasValue) { InvalidKeys.Remove(key.Value); LastSuccess[key.Value] = DateTime.Now; }
        Changed?.Invoke();
    }

    public void Forget(Guid key)
    {
        InvalidKeys.Remove(key);
        LastSuccess.Remove(key);
        var prefix = key.ToString();
        foreach (var k in _blocks.Keys.Where(k => k.StartsWith(prefix)).ToList())
            _blocks.Remove(k);
        Changed?.Invoke();
    }

    public enum KeyStatus { Ready, Invalid, Partial, Cooling }

    public KeyStatus GetKeyStatus(Guid key, List<string> models)
    {
        if (InvalidKeys.ContainsKey(key)) return KeyStatus.Invalid;
        var now = DateTime.Now;
        int active = models.Count(m =>
        {
            var s = Slot(key, m);
            return _blocks.TryGetValue(s, out var b) && b.Until > now;
        });
        if (active == 0) return KeyStatus.Ready;
        return active >= models.Count ? KeyStatus.Cooling : KeyStatus.Partial;
    }
}

// MARK: Router

/// <summary>
/// Picks the provider, rotates API keys, falls back across models, and turns every
/// failure into a clear, actionable message.
/// </summary>
public sealed class AIRouter
{
    private readonly SettingsStore _settings;
    public readonly QuotaTracker Quota;
    private readonly Dictionary<ProviderKind, int> _nextKeyIndex = new();

    public Action<string>? OnStatus { get; set; }

    private const double OverallBudget = 75;

    public AIRouter(SettingsStore settings, QuotaTracker quota)
    {
        _settings = settings;
        Quota = quota;
    }

    public async Task<AIResult> CompleteAsync(string system, string user, ProviderKind? provider = null)
    {
        var kind = provider ?? _settings.Prefs.ActiveProvider;
        try
        {
            return kind.UsesApiKeys()
                ? await CompleteWithKeysAsync(kind, system, user)
                : await CompleteWithCLIAsync(kind, system, user);
        }
        catch (AIException) { throw; }
        catch (Exception ex) { throw AIException.From(ex); }
    }

    // MARK: API-key providers

    private async Task<AIResult> CompleteWithKeysAsync(ProviderKind kind, string system, string user)
    {
        var cfg = _settings.Api(kind);
        var keys = cfg.Keys.Where(k => k.IsEnabled && !string.IsNullOrEmpty(_settings.Secret(k.Id))).ToList();
        if (keys.Count == 0)
            throw AIException.NotConfigured($"Add a {kind.DisplayName()} API key in Settings → AI Provider.");

        var models = cfg.Models.Count == 0 ? ModelCatalog.DefaultModelIDs(kind) : new List<string>(cfg.Models);

        // Round-robin starting key
        var start = (_nextKeyIndex.GetValueOrDefault(kind) % keys.Count);
        var rotated = keys.Skip(start).Concat(keys.Take(start)).ToList();

        var plan = new List<(APIKeyMeta key, string model)>();
        switch (cfg.FallbackOrder)
        {
            case FallbackOrder.KeysFirst:
                foreach (var m in models)
                    foreach (var k in rotated)
                        plan.Add((k, m));
                break;
            case FallbackOrder.ModelsFirst:
                foreach (var k in rotated)
                    foreach (var m in models)
                        plan.Add((k, m));
                break;
        }

        var began = DateTime.Now;
        int attempts = 0;
        var skippedModels = new HashSet<string>();
        AIException? lastError = null;
        DateTime? soonest = null;
        bool announcedFallback = false;

        foreach (var (key, model) in plan)
        {
            if (skippedModels.Contains(model)) continue;
            if (Quota.InvalidKeys.ContainsKey(key.Id)) continue;
            if (Quota.BlockedUntil(key.Id, model) is { } until)
            {
                soonest = soonest.HasValue ? (until < soonest.Value ? until : soonest.Value) : until;
                continue;
            }
            if ((DateTime.Now - began).TotalSeconds > OverallBudget) throw AIException.Timeout();

            if (attempts > 0 && !announcedFallback)
            {
                OnStatus?.Invoke("Trying backup…");
                announcedFallback = true;
            }

            try
            {
                attempts++;
                var secret = _settings.Secret(key.Id);
                var raw = await WithTransientRetry(() =>
                    HTTPProviders.CompleteAsync(kind, secret, model, system, user));
                var text = Clean(raw.Text);
                if (string.IsNullOrEmpty(text)) throw AIException.EmptyResponse();
                Quota.MarkSuccess(key.Id, model);
                _nextKeyIndex[kind] = keys.IndexOf(key) + 1;
                return new AIResult(text, raw.InputTokens, raw.OutputTokens, kind, model, key.Label, attempts);
            }
            catch (Exception ex)
            {
                var e = AIException.From(ex);
                lastError = e;
                if (e.IsFatal) throw;

                if (e is RateLimitException rle)
                {
                    var untilDt = rle.Daily
                        ? DailyReset(kind, rle.RetryAfter)
                        : DateTime.Now.Add(Clamp(rle.RetryAfter ?? TimeSpan.FromSeconds(30), 2, 600));
                    Quota.SetBlock(key.Id, model, untilDt, rle.Daily);
                    soonest = soonest.HasValue ? (untilDt < soonest.Value ? untilDt : soonest.Value) : untilDt;
                }
                else if (e.ShortKind == "invalid_key")
                {
                    Quota.MarkInvalid(key.Id, e.Message);
                }
                else if (e.ShortKind == "model_unavailable")
                {
                    Quota.SetBlock(key.Id, model, DateTime.Now.AddDays(1), true);
                    if (cfg.Keys.Count == 1) skippedModels.Add(model);
                }
                else if (e.ShortKind is "too_large" or "server" or "timeout" or "empty")
                {
                    skippedModels.Add(model);
                }
            }
        }

        throw ExhaustedError(kind, lastError, soonest, attempts);
    }

    // MARK: CLI providers

    private async Task<AIResult> CompleteWithCLIAsync(ProviderKind kind, string system, string user)
    {
        var cfg = kind == ProviderKind.ClaudeCode ? _settings.Prefs.Claude : _settings.Prefs.Codex;
        var model = cfg.Model.Trim();
        if (Quota.BlockedUntil(null, kind.ToString()) is { } until)
            throw AIException.AllExhausted($"{kind.DisplayName()} hit its usage limit. Try again after {until:t}.");

        try
        {
            var raw = await WithTransientRetry(() =>
                CLIProviders.CompleteAsync(kind, cfg, system, user));
            var text = Clean(raw.Text);
            if (string.IsNullOrEmpty(text)) throw AIException.EmptyResponse();
            Quota.MarkSuccess(null, kind.ToString());
            return new AIResult(text, raw.InputTokens, raw.OutputTokens, kind,
                string.IsNullOrEmpty(model) ? "default" : model, null, 1);
        }
        catch (Exception ex)
        {
            var e = AIException.From(ex);
            if (e is RateLimitException rle)
            {
                var untilDt = DateTime.Now.Add(rle.RetryAfter ?? (rle.Daily ? TimeSpan.FromHours(1) : TimeSpan.FromMinutes(1)));
                Quota.SetBlock(null, kind.ToString(), untilDt, rle.Daily);
                throw AIException.AllExhausted($"{kind.DisplayName()} is rate-limited: {rle.RawMessage[..Math.Min(140, rle.RawMessage.Length)]}");
            }
            throw;
        }
    }

    // MARK: Helpers

    private static async Task<T> WithTransientRetry<T>(Func<Task<T>> op)
    {
        try { return await op(); }
        catch (Exception ex)
        {
            var e = AIException.From(ex);
            if (!e.IsTransient) throw e;
            var delay = 600 + Random.Shared.Next(600);
            await Task.Delay(delay);
            return await op();
        }
    }

    public static DateTime DailyReset(ProviderKind kind, TimeSpan? retryAfter)
    {
        if (retryAfter.HasValue && retryAfter.Value.TotalSeconds > 0)
            return DateTime.Now.Add(retryAfter.Value);
        if (kind == ProviderKind.Gemini)
        {
            try
            {
                var pacific = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
                var nowPacific = TimeZoneInfo.ConvertTime(DateTime.Now, pacific);
                var midnight = nowPacific.Date.AddDays(1);
                return TimeZoneInfo.ConvertTime(midnight, pacific, TimeZoneInfo.Local);
            }
            catch { }
        }
        return DateTime.Now.AddHours(1);
    }

    private static AIException ExhaustedError(ProviderKind kind, AIException? last, DateTime? soonest, int attempts)
    {
        var when = soonest.HasValue ? $" Next available around {soonest.Value:t}." : "";
        return last?.ShortKind switch
        {
            "invalid_key" => AIException.AllExhausted($"Your {kind.DisplayName()} keys were rejected. Check them in Settings.{when}"),
            "daily_limit" => AIException.AllExhausted($"All {kind.DisplayName()} keys hit today's free limit.{when} Add another key or switch provider."),
            "rate_limit" => AIException.AllExhausted($"All {kind.DisplayName()} keys are rate-limited right now.{when}"),
            _ when last is not null && attempts == 1 => last,
            _ when last is not null => AIException.AllExhausted($"Every {kind.DisplayName()} key and model failed. Last error: {last.Message}"),
            _ => AIException.AllExhausted($"All {kind.DisplayName()} keys are cooling down.{when}")
        };
    }

    /// <summary>Strips code fences, wrapping quotes and leaked &lt;think&gt; blocks.</summary>
    public static string Clean(string s)
    {
        var t = s;
        // Remove <think>...</think> blocks
        while (true)
        {
            var open = t.IndexOf("<think>", StringComparison.Ordinal);
            if (open < 0) break;
            var close = t.IndexOf("</think>", open, StringComparison.Ordinal);
            if (close < 0) break;
            t = t[..open] + t[(close + "</think>".Length)..];
        }

        t = t.Trim();

        // Strip code fences
        if (t.StartsWith("```"))
        {
            var lines = t.Split('\n').ToList();
            lines.RemoveAt(0);
            if (lines.Count > 0 && lines[^1].Trim().StartsWith("```")) lines.RemoveAt(lines.Count - 1);
            t = string.Join('\n', lines).Trim();
        }

        // Strip wrapping quotes
        if (t.Length > 1 && t[0] == '"' && t[^1] == '"' && !t[1..^1].Contains('"'))
            t = t[1..^1];

        return t;
    }

    private static TimeSpan Clamp(TimeSpan value, double minSec, double maxSec)
    {
        if (value.TotalSeconds < minSec) return TimeSpan.FromSeconds(minSec);
        if (value.TotalSeconds > maxSec) return TimeSpan.FromSeconds(maxSec);
        return value;
    }
}
