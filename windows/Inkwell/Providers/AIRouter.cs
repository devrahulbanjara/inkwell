using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Inkwell.Core;

namespace Inkwell.Providers;

public static class Prompts
{
    public const string System =
        "You are Inkwell, a writing assistant that works inside any text field on the user's PC.\n" +
        "Apply the instruction to the user's text.\n" +
        "Reply with ONLY the final text — no preamble, no explanation, no surrounding quotes, no code fences.\n" +
        "Keep the same language as the input unless the instruction says otherwise.";

    public static string User(Template template, string text) =>
        $"Instruction: {template.Prompt}\n\nText:\n{text}";
}

public sealed record AIResult(string Text, int InputTokens, int OutputTokens,
    ProviderKind Provider, string Model, string? KeyLabel, int Attempts);

// MARK: Quota / cooldown tracking (drives both routing and the key status badges)

public enum KeyStatusKind { Ready, Invalid, Partial, Cooling }

/// <summary>Ready · Invalid(reason) · Partial(until: some models resting) · Cooling(until, daily: every model resting).</summary>
public sealed record KeyStatus(KeyStatusKind Kind, DateTime? Until = null, bool Daily = false, string Reason = "")
{
    public static readonly KeyStatus Ready = new(KeyStatusKind.Ready);
}

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

    private static string Slot(Guid? key, string model) => $"{key?.ToString() ?? "cli"}|{model}";

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

    // MARK: CLI plan usage (Claude Code 5-hour / weekly windows, Codex usage limits)

    private readonly Dictionary<ProviderKind, CliLimitInfo> _cliLimits = new();

    /// <summary>Latest plan-usage reading for a CLI provider; null when nothing is known or it has reset.</summary>
    public CliLimitInfo? CliLimit(ProviderKind kind)
    {
        if (!_cliLimits.TryGetValue(kind, out var info)) return null;
        if (info.ResetsAt is { } r && r <= DateTime.Now) { _cliLimits.Remove(kind); return null; }
        return info;
    }

    public void SetCliLimit(ProviderKind kind, CliLimitInfo? info)
    {
        if (info is null) { if (_cliLimits.Remove(kind)) Changed?.Invoke(); return; }
        _cliLimits[kind] = info;
        Changed?.Invoke();
    }

    /// <summary>Short status for menus and cards: "Limit reached · resets 3:00 PM" or "82% of 5-hour limit used".</summary>
    public string? CliLimitSummary(ProviderKind kind)
    {
        var info = CliLimit(kind);
        if (info is null) return null;
        if (info.Rejected || BlockedUntil(null, kind.RawValue()) is not null)
            return info.ResetsAt is { } r ? $"{Cap(info.Window)} limit reached · resets {LimitText.When(r)}" : $"{Cap(info.Window)} limit reached";
        if (info.Utilization is { } u && u >= 0.5)
            return $"{Math.Round(u * 100)}% of {info.Window} limit used" + (info.ResetsAt is { } r2 ? $" · resets {LimitText.When(r2)}" : "");
        return null;
        static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
    }

    public void MarkInvalid(Guid key, string reason) { InvalidKeys[key] = reason; Changed?.Invoke(); }
    public void ClearInvalid(Guid key) { if (InvalidKeys.Remove(key)) Changed?.Invoke(); }

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
        foreach (var k in _blocks.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            _blocks.Remove(k);
        Changed?.Invoke();
    }

    public KeyStatus Status(Guid key, IReadOnlyList<string> models)
    {
        if (InvalidKeys.TryGetValue(key, out var reason)) return new KeyStatus(KeyStatusKind.Invalid, Reason: reason);
        var now = DateTime.Now;
        var active = models
            .Select(m => _blocks.TryGetValue(Slot(key, m), out var b) && b.Until > now ? b : null)
            .Where(b => b is not null)
            .Select(b => b!)
            .ToList();
        if (active.Count == 0) return KeyStatus.Ready;
        var soonest = active.Min(b => b.Until);
        if (active.Count >= models.Count) return new KeyStatus(KeyStatusKind.Cooling, soonest, active.All(b => b.Daily));
        return new KeyStatus(KeyStatusKind.Partial, soonest);
    }

    /// <summary>Badge for a key: short text, colour hex and a tooltip. Null status = the key is turned off.</summary>
    public static (string Text, string ColorHex, string Help) Badge(KeyStatus? status)
    {
        if (status is null) return ("Off", "#8A8A8A", "This key is turned off");
        string T(DateTime? d) => d?.ToString("t") ?? "";
        return status.Kind switch
        {
            KeyStatusKind.Ready => ("Ready", "#22C55E", "Ready to use"),
            KeyStatusKind.Invalid => ("Rejected", "#EF4444", status.Reason),
            KeyStatusKind.Partial => ("Partly resting", "#EAB308", $"Some models are rate-limited until {T(status.Until)}"),
            _ => status.Daily
                ? ($"Resets {T(status.Until)}", "#F97316", "Daily free limit reached")
                : ($"Back {T(status.Until)}", "#F97316", "Rate-limited for a moment"),
        };
    }
}

// MARK: Router

/// <summary>
/// Picks the provider, rotates API keys, falls back across models, and turns every
/// failure into a clear, actionable message. Never throws anything but <see cref="AIException"/>.
/// </summary>
public sealed class AIRouter
{
    private readonly SettingsStore _settings;
    public QuotaTracker Quota { get; }
    private readonly Dictionary<ProviderKind, int> _nextKeyIndex = new();

    /// <summary>Called with short status updates ("Trying backup…") for the floating HUD.</summary>
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
#if DEBUG
        // Debug builds only: INKWELL_FAKE_AI=1 answers locally so the capture → replace pipeline can be tested without keys.
        if (Environment.GetEnvironmentVariable("INKWELL_FAKE_AI") == "1")
        {
            await Task.Delay(1200);
            var text = user[(user.IndexOf("Text:\n", StringComparison.Ordinal) + 6)..];
            return new AIResult("FIXED: " + text.ToUpperInvariant(), 10, 10, kind, "fake", null, 1);
        }
#endif
        try
        {
            return kind.UsesApiKeys()
                ? await CompleteWithKeysAsync(kind, system, user)
                : await CompleteWithCLIAsync(kind, system, user);
        }
        catch (Exception ex)
        {
            throw AIException.From(ex);
        }
    }

    // MARK: API-key providers (Gemini, Groq)

    private async Task<AIResult> CompleteWithKeysAsync(ProviderKind kind, string system, string user)
    {
        var cfg = _settings.Api(kind);
        var keys = cfg.Keys.Where(k => k.IsEnabled && !string.IsNullOrEmpty(_settings.Secret(k.Id))).ToList();
        if (keys.Count == 0)
            throw AIException.NotConfigured($"Add a {kind.DisplayName()} API key in Settings → AI Provider.");

        var models = cfg.Models.Count == 0 ? ModelCatalog.DefaultModelIDs(kind) : new List<string>(cfg.Models);

        // Round-robin the starting key so load spreads across keys over time.
        var start = _nextKeyIndex.GetValueOrDefault(kind) % keys.Count;
        var rotated = keys.Skip(start).Concat(keys.Take(start)).ToList();

        var plan = new List<(APIKeyMeta Key, string Model)>();
        if (cfg.FallbackOrder == FallbackOrder.KeysFirst)
        {
            foreach (var m in models) foreach (var k in rotated) plan.Add((k, m));
        }
        else
        {
            foreach (var k in rotated) foreach (var m in models) plan.Add((k, m));
        }

        var began = DateTime.Now;
        int attempts = 0;
        var skippedModels = new HashSet<string>();
        AIException? lastError = null;
        DateTime? soonest = null;
        bool announcedFallback = false;

        void NoteSoonest(DateTime until) => soonest = soonest is null || until < soonest ? until : soonest;

        foreach (var (key, model) in plan)
        {
            if (skippedModels.Contains(model)) continue;
            if (Quota.InvalidKeys.ContainsKey(key.Id)) continue;
            if (Quota.BlockedUntil(key.Id, model) is { } blocked)
            {
                NoteSoonest(blocked);
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
                var raw = await WithTransientRetry(() => HTTPProviders.CompleteAsync(kind, secret, model, system, user));
                var text = Clean(raw.Text);
                if (string.IsNullOrEmpty(text)) throw AIException.EmptyResponse();
                Quota.MarkSuccess(key.Id, model);
                var idx = keys.FindIndex(k => k.Id == key.Id);
                if (idx >= 0) _nextKeyIndex[kind] = idx + 1;
                return new AIResult(text, raw.InputTokens, raw.OutputTokens, kind, model, key.Label, attempts);
            }
            catch (Exception ex)
            {
                var e = AIException.From(ex);
                lastError = e;
                if (e.IsFatal) throw e;

                switch (e)
                {
                    case RateLimitException rle:
                        var until = rle.Daily
                            ? DailyReset(kind, rle.RetryAfter)
                            : DateTime.Now.AddSeconds(Math.Clamp((rle.RetryAfter ?? TimeSpan.FromSeconds(30)).TotalSeconds, 2, 600));
                        Quota.SetBlock(key.Id, model, until, rle.Daily);
                        NoteSoonest(until);
                        break;
                    case { ShortKind: "invalid_key" }:
                        Quota.MarkInvalid(key.Id, e.Detail);
                        break;
                    case { ShortKind: "model_unavailable" }:
                        // Not available on this key (e.g. no free quota) — park it for a day on this key only.
                        Quota.SetBlock(key.Id, model, DateTime.Now.AddDays(1), true);
                        if (cfg.Keys.Count == 1) skippedModels.Add(model);
                        break;
                    case { ShortKind: "too_large_for_model" or "server" or "timeout" or "empty" }:
                        skippedModels.Add(model);
                        break;
                }
            }
        }

        throw ExhaustedError(kind, lastError, soonest, attempts);
    }

    // MARK: CLI providers (Claude Code, Codex)

    private async Task<AIResult> CompleteWithCLIAsync(ProviderKind kind, string system, string user)
    {
        var cfg = _settings.Cli(kind);
        var model = cfg.Model.Trim();
        var slot = kind.RawValue();
        // While a plan limit is known to be in force, fail instantly instead of waiting on the CLI.
        if (Quota.BlockedUntil(null, slot) is { } until)
            throw AIException.AllExhausted(CliLimitMessage(kind, Quota.CliLimit(kind)?.Window, until));

        try
        {
            var raw = await WithTransientRetry(() => CLIProviders.CompleteAsync(kind, cfg, system, user));
            var text = Clean(raw.Text);
            if (string.IsNullOrEmpty(text)) throw AIException.EmptyResponse();
            Quota.MarkSuccess(null, slot);
            Quota.SetCliLimit(kind, raw.Limit);
            return new AIResult(text, raw.InputTokens, raw.OutputTokens, kind,
                string.IsNullOrEmpty(model) ? "default" : model, null, 1);
        }
        catch (Exception ex)
        {
            var e = AIException.From(ex);
            if (e is RateLimitException rle)
            {
                // Plan windows: wait for the reset the CLI told us about; unknown → retry in an hour (a minute if per-minute).
                var blockUntil = rle.ResetsAt is { } r && r > DateTime.Now
                    ? r
                    : DateTime.Now.Add(rle.RetryAfter is { TotalSeconds: > 0 } ra ? ra : (rle.Daily ? TimeSpan.FromHours(1) : TimeSpan.FromMinutes(1)));
                Quota.SetBlock(null, slot, blockUntil, rle.Daily);
                Quota.SetCliLimit(kind, new CliLimitInfo(rle.Window ?? "usage", 1, rle.ResetsAt ?? blockUntil, true));
                throw AIException.AllExhausted(CliLimitMessage(kind, rle.Window, rle.ResetsAt ?? (rle.Daily ? null : blockUntil)));
            }
            throw e;
        }
    }

    // MARK: Helpers

    /// <summary>"Claude Code's 5-hour limit is reached. Resets 3:00 PM. Switch to Gemini to keep going."</summary>
    private string CliLimitMessage(ProviderKind kind, string? window, DateTime? resets)
    {
        var what = window is null or "usage" ? "usage limit" : $"{window} limit";
        var when = resets is { } r ? $" Resets {LimitText.When(r)}." : " It resets on its own.";
        return $"{kind.DisplayName()}'s {what} is reached.{when}{SuggestAlternative(kind)}";
    }

    /// <summary>Points to another provider that's ready right now, if there is one.</summary>
    private string SuggestAlternative(ProviderKind current)
    {
        foreach (var k in ProviderKindExtensions.All)
        {
            if (k == current) continue;
            if (k.UsesApiKeys() && _settings.HasUsableKey(k)) return $" Switch to {k.DisplayName()} to keep going.";
        }
        return "";
    }

    /// <summary>One quick retry with jittered backoff for transient errors (5xx, timeouts).</summary>
    private static async Task<T> WithTransientRetry<T>(Func<Task<T>> op)
    {
        try { return await op(); }
        catch (Exception ex)
        {
            var e = AIException.From(ex);
            if (!e.IsTransient) throw e;
            await Task.Delay(600 + Random.Shared.Next(600));
            return await op();
        }
    }

    /// <summary>Gemini's daily quotas reset at midnight Pacific time; Groq tells us via retry-after.</summary>
    public static DateTime DailyReset(ProviderKind kind, TimeSpan? retryAfter)
    {
        if (retryAfter is { TotalSeconds: > 0 } r) return DateTime.Now.Add(r);
        if (kind == ProviderKind.Gemini)
        {
            try
            {
                var pacific = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
                var nowPacific = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pacific);
                var midnight = DateTime.SpecifyKind(nowPacific.Date.AddDays(1), DateTimeKind.Unspecified);
                return TimeZoneInfo.ConvertTimeToUtc(midnight, pacific).ToLocalTime();
            }
            catch { /* time zone database unavailable */ }
            return DateTime.Now.AddDays(1);
        }
        return DateTime.Now.AddHours(1);
    }

    private static AIException ExhaustedError(ProviderKind kind, AIException? last, DateTime? soonest, int attempts)
    {
        var when = soonest.HasValue ? $" Next available around {LimitText.When(soonest.Value)}." : "";
        var name = kind.DisplayName();
        return last switch
        {
            null => AIException.AllExhausted($"All {name} keys are cooling down.{when}"),
            { ShortKind: "invalid_key" } => AIException.AllExhausted($"Your {name} keys were rejected. Check them in Settings.{when}"),
            RateLimitException { Daily: true } => AIException.AllExhausted($"All {name} keys hit today's free limit.{when} Add another key or switch provider."),
            RateLimitException => AIException.AllExhausted($"All {name} keys are rate-limited right now.{when}"),
            _ when attempts == 1 => last,
            _ => AIException.AllExhausted($"Every {name} key and model failed. Last error: {last.Message}"),
        };
    }

    /// <summary>Strips code fences, wrapping quotes and leaked &lt;think&gt; blocks some models add despite instructions.</summary>
    public static string Clean(string s)
    {
        var t = s.Replace("\r\n", "\n");
        while (true)
        {
            var open = t.IndexOf("<think>", StringComparison.Ordinal);
            if (open < 0) break;
            var close = t.IndexOf("</think>", open + 7, StringComparison.Ordinal);
            if (close < 0) break;
            t = t[..open] + t[(close + "</think>".Length)..];
        }

        t = t.Trim();

        if (t.StartsWith("```", StringComparison.Ordinal))
        {
            var lines = t.Split('\n').ToList();
            lines.RemoveAt(0);
            if (lines.Count > 0 && lines[^1].Trim().StartsWith("```", StringComparison.Ordinal)) lines.RemoveAt(lines.Count - 1);
            t = string.Join('\n', lines).Trim();
        }

        if (t.Length > 1 && t[0] == '"' && t[^1] == '"' && !t[1..^1].Contains('"'))
            t = t[1..^1];

        return t;
    }
}

// MARK: Shared provider actions (used by the flyout, the tray menu and the Settings window)

public static class ProviderActions
{
    /// <summary>Makes <paramref name="kind"/> active if it's usable. Returns a short reason when it isn't.</summary>
    public static async Task<string?> ActivateAsync(ProviderKind kind, SettingsStore settings, CLIStatusStore cli)
    {
        if (kind.UsesApiKeys())
        {
            if (!settings.HasUsableKey(kind)) return $"Add a {kind.DisplayName()} key first.";
        }
        else
        {
            await cli.RefreshAsync(kind, settings.Cli(kind));
            switch (cli.Status(kind).State)
            {
                case CLIStatusState.Ready: break;
                case CLIStatusState.NotInstalled: return $"Install {kind.DisplayName()} first.";
                default: return $"Sign in to {kind.DisplayName()} first.";
            }
        }
        settings.SetActiveProvider(kind);
        return null;
    }

    /// <summary>Validates a key with the provider, then saves it. Returns the models the key can access.</summary>
    public static async Task<List<RemoteModel>> AddKeyAsync(ProviderKind kind, string raw, string label,
        SettingsStore settings, QuotaTracker quota)
    {
        var key = raw.Trim();
        List<RemoteModel> models;
        try
        {
            models = await HTTPProviders.ListModelsAsync(kind, key);
        }
        catch (Exception ex)
        {
            var e = AIException.From(ex);
            throw new InvalidOperationException(e switch
            {
                { ShortKind: "invalid_key" } => $"{kind.DisplayName()} rejected this key: {e.Detail}",
                { ShortKind: "offline" } => "You're offline — connect and try again.",
                RateLimitException => "Rate-limited while checking. Wait a moment and try again.",
                _ => e.Message,
            });
        }
        var meta = settings.AddKey(kind, key, label.Trim())
            ?? throw new InvalidOperationException("That key is already added.");
        quota.ClearInvalid(meta.Id);
        // First key for an API provider while the active one can't run → switch to it.
        var active = settings.Prefs.ActiveProvider;
        if (active.UsesApiKeys() && active != kind && !settings.HasUsableKey(active))
            settings.SetActiveProvider(kind);
        return models;
    }

    public static async Task<(bool Ok, string Message)> TestAsync(ProviderKind kind, AIRouter router)
    {
        var start = DateTime.Now;
        try
        {
            var r = await router.CompleteAsync("Reply with exactly the word OK.", "ping", kind);
            var time = (DateTime.Now - start).TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
            return (true, $"Works — {r.Model}{(r.KeyLabel is null ? "" : $" · {r.KeyLabel}")} · {time}s");
        }
        catch (Exception ex)
        {
            return (false, AIException.From(ex).Message);
        }
    }
}
