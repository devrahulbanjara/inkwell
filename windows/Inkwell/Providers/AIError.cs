using System;
using System.ComponentModel;
using System.Globalization;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Inkwell.Providers;

/// <summary>
/// Every failure a provider can produce, classified so the router knows what to do next.
/// Mirrors the Mac app's AIError enum: <see cref="ShortKind"/> is the case name used in usage stats.
/// </summary>
public class AIException : Exception
{
    public string ShortKind { get; }
    /// <summary>Transient errors are worth one quick retry on the same key + model.</summary>
    public bool IsTransient { get; }
    /// <summary>Fatal errors stop immediately: trying another key or model cannot help.</summary>
    public bool IsFatal { get; }
    /// <summary>The raw provider message (without the friendly prefix), e.g. the reason a key was rejected.</summary>
    public string Detail { get; }

    protected AIException(string message, string shortKind, bool isTransient = false, bool isFatal = false, string? detail = null)
        : base(message)
    {
        ShortKind = shortKind;
        IsTransient = isTransient;
        IsFatal = isFatal;
        Detail = detail ?? message;
    }

    public static AIException NotConfigured(string msg) => new(msg, "not_configured", isFatal: true);
    public static AIException InvalidKey(string msg) => new($"API key rejected: {msg}", "invalid_key", detail: msg);
    public static AIException RateLimited(TimeSpan? retryAfter, bool daily, string message) => new RateLimitException(retryAfter, daily, message);
    public static AIException ModelUnavailable(string msg) => new($"Model unavailable: {msg}", "model_unavailable", detail: msg);
    public static AIException TooLargeForModel(string model) => new($"Text too long for {model}.", "too_large_for_model", detail: model);
    public static AIException InputTooLarge() => new("That text is too long for the model.", "too_large", isFatal: true);
    public static AIException Blocked(string reason) => new($"The model refused this text ({reason}).", "blocked", isFatal: true);
    public static AIException BadRequest(string msg) => new($"Request rejected: {msg}", "bad_request", isFatal: true, detail: msg);
    public static AIException Server(int code, string msg) => new($"The provider had a server error ({code}). Try again shortly.", "server", isTransient: true, detail: msg);
    /// <summary>The model is temporarily overloaded (Gemini 503 "high demand", Groq 498); another model may work right now.</summary>
    public static AIException Overloaded(string model) =>
        new($"{ModelCatalog.DisplayName(model)} is busy right now (high demand). Try again in a moment, or add a fallback model.", "server", isTransient: true, detail: model);
    public static AIException Offline() => new("You're offline.", "offline", isFatal: true);
    public static AIException Timeout() => new("The AI took too long to answer.", "timeout", isTransient: true);
    public static AIException EmptyResponse() => new("The model returned an empty answer.", "empty");
    public static AIException CliNotInstalled(string name) => new($"{name} isn't installed (or couldn't be found).", "cli", isFatal: true);
    public static AIException CliNotLoggedIn(string name) => new($"{name} isn't signed in. Sign in from a terminal first.", "cli", isFatal: true);
    public static AIException CliFailed(string msg) => new(msg, "cli");
    public static AIException AllExhausted(string msg) => new(msg, "exhausted");
    public static AIException Cancelled() => new("Cancelled.", "cancelled", isFatal: true);

    /// <summary>The usage-stats kind. "too_large_for_model" is reported as "too_large", like the Mac app.</summary>
    public string StatsKind => ShortKind == "too_large_for_model" ? "too_large" : ShortKind;

    public static AIException From(Exception error)
    {
        switch (error)
        {
            case AIException ae:
                return ae;
            // HttpClient reports its own timeout as a TaskCanceledException wrapping a TimeoutException.
            case TaskCanceledException tce when tce.InnerException is TimeoutException:
                return Timeout();
            case TimeoutException:
                return Timeout();
            case OperationCanceledException:
                return Cancelled();
            case HttpRequestException hre:
                switch (hre.HttpRequestError)
                {
                    case HttpRequestError.NameResolutionError:
                    case HttpRequestError.ConnectionError:
                        return Offline();
                }
                if (hre.InnerException is SocketException) return Offline();
                if (hre.StatusCode.HasValue) return Server((int)hre.StatusCode.Value, hre.Message);
                return Server(0, hre.Message);
            case JsonException:
                return Server(0, "Unexpected response format");
            case Win32Exception w32:
                return CliFailed(w32.Message);
            default:
                return CliFailed(error.Message);
        }
    }
}

/// <summary>Rate-limit specific exception with retry/daily info for the router.</summary>
public sealed class RateLimitException : AIException
{
    public TimeSpan? RetryAfter { get; }
    /// <summary>A long quota (per day, 5-hour, weekly…); backoff won't help until it resets.</summary>
    public bool Daily { get; }
    public string RawMessage { get; }
    /// <summary>Which limit was hit: "per-minute", "daily", "5-hour", "weekly", "extra usage"… (null = unknown).</summary>
    public string? Window { get; }
    /// <summary>When the limit resets, if the provider said so.</summary>
    public DateTime? ResetsAt { get; }

    public RateLimitException(TimeSpan? retryAfter, bool daily, string message, string? window = null, DateTime? resetsAt = null)
        : base(Describe(daily, window, resetsAt), daily ? "daily_limit" : "rate_limit", detail: message)
    {
        RetryAfter = retryAfter;
        Daily = daily;
        RawMessage = message;
        Window = window;
        ResetsAt = resetsAt;
    }

    private static string Describe(bool daily, string? window, DateTime? resetsAt)
    {
        var when = resetsAt is { } r ? $" Resets {LimitText.When(r)}." : "";
        if (window is not null && window != "per-minute")
            return $"{char.ToUpperInvariant(window[0])}{window[1..]} limit reached.{when}";
        return daily ? $"Daily free limit reached.{when}" : "Rate limited — too many requests.";
    }
}

/// <summary>Friendly reset times: "3:15 PM" today, "Mon 9:00 AM" this week, "Mar 20, 9:00 AM" later.</summary>
public static class LimitText
{
    public static string When(DateTime local)
    {
        var now = DateTime.Now;
        if (local.Date == now.Date) return local.ToString("t", CultureInfo.CurrentCulture);
        if (local.Date == now.Date.AddDays(1)) return "tomorrow " + local.ToString("t", CultureInfo.CurrentCulture);
        if (local - now < TimeSpan.FromDays(6)) return local.ToString("ddd ", CultureInfo.CurrentCulture) + local.ToString("t", CultureInfo.CurrentCulture);
        return local.ToString("MMM d, ", CultureInfo.CurrentCulture) + local.ToString("t", CultureInfo.CurrentCulture);
    }
}

/// <summary>Parses durations like "34s", "1.5s", "2m59.56s", "1h2m3s" (Gemini retryDelay / Groq reset headers).</summary>
public static class DurationParser
{
    private static readonly (string Unit, double Mult)[] Units = { ("h", 3600.0), ("ms", 0.001), ("m", 60.0), ("s", 1.0) };

    public static TimeSpan? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var plain)) return TimeSpan.FromSeconds(plain);

        double total = 0;
        bool matched = false;
        // "ms" is handled before "m" and "s" so "500ms" isn't misread.
        foreach (var (unit, mult) in Units)
        {
            var rx = new Regex(@"([0-9.]+)" + Regex.Escape(unit));
            foreach (Match m in rx.Matches(s))
            {
                if (double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                {
                    total += v * mult;
                    matched = true;
                }
            }
            s = rx.Replace(s, "");
        }
        return matched ? TimeSpan.FromSeconds(total) : null;
    }
}
