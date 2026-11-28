using System;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace Inkwell.Providers;

/// <summary>
/// Every failure a provider can produce, classified so the router knows what to do next.
/// </summary>
public class AIException : Exception
{
    public string ShortKind { get; }
    public bool IsTransient { get; }
    public bool IsFatal { get; }

    protected AIException(string message, string shortKind, bool isTransient = false, bool isFatal = false)
        : base(message)
    {
        ShortKind = shortKind;
        IsTransient = isTransient;
        IsFatal = isFatal;
    }

    // Factory methods matching Swift's AIError enum
    public static AIException NotConfigured(string msg) => new(msg, "not_configured", isFatal: true);
    public static AIException InvalidKey(string msg) => new($"API key rejected: {msg}", "invalid_key");
    public static AIException RateLimited(TimeSpan? retryAfter, bool daily, string message)
        => new RateLimitException(retryAfter, daily, message);
    public static AIException ModelUnavailable(string msg) => new($"Model unavailable: {msg}", "model_unavailable");
    public static AIException TooLargeForModel(string model) => new($"Text too long for {model}.", "too_large");
    public static AIException InputTooLarge() => new("That text is too long for the model.", "too_large", isFatal: true);
    public static AIException Blocked(string reason) => new($"The model refused this text ({reason}).", "blocked", isFatal: true);
    public static AIException BadRequest(string msg) => new($"Request rejected: {msg}", "bad_request", isFatal: true);
    public static AIException Server(int code, string msg) => new($"The provider had a server error ({code}). Try again shortly.", "server", isTransient: true);
    public static AIException Offline() => new("You're offline.", "offline", isFatal: true);
    public static AIException Timeout() => new("The AI took too long to answer.", "timeout", isTransient: true);
    public static AIException EmptyResponse() => new("The model returned an empty answer.", "empty");
    public static AIException CliNotInstalled(string name) => new($"{name} isn't installed (or couldn't be found).", "cli", isFatal: true);
    public static AIException CliNotLoggedIn(string name) => new($"{name} isn't signed in. Sign in from Terminal first.", "cli", isFatal: true);
    public static AIException CliFailed(string msg) => new(msg, "cli");
    public static AIException AllExhausted(string msg) => new(msg, "exhausted");
    public static AIException Cancelled() => new("Cancelled.", "cancelled", isFatal: true);

    public static AIException From(Exception error)
    {
        if (error is AIException ae) return ae;
        if (error is OperationCanceledException) return Cancelled();
        if (error is HttpRequestException hre)
        {
            if (hre.StatusCode.HasValue) return Server((int)hre.StatusCode.Value, hre.Message);
            var msg = hre.Message.ToLowerInvariant();
            if (msg.Contains("timeout") || msg.Contains("timed out")) return Timeout();
            if (msg.Contains("network") || msg.Contains("connect") || msg.Contains("dns")
                || msg.Contains("host") || msg.Contains("refused"))
                return Offline();
            return Server(0, hre.Message);
        }
        if (error is TaskCanceledException) return Timeout();
        return CliFailed(error.Message);
    }
}

/// <summary>Rate-limit specific exception with retry/daily info for the router.</summary>
public sealed class RateLimitException : AIException
{
    public TimeSpan? RetryAfter { get; }
    public bool Daily { get; }
    public string RawMessage { get; }

    public RateLimitException(TimeSpan? retryAfter, bool daily, string message)
        : base(daily ? "Daily free limit reached." : "Rate limited — too many requests.",
               daily ? "daily_limit" : "rate_limit")
    {
        RetryAfter = retryAfter;
        Daily = daily;
        RawMessage = message;
    }
}

/// <summary>Parses durations like "34s", "1.5s", "2m59.56s", "1h2m3s" (Gemini retryDelay / Groq reset headers).</summary>
public static class DurationParser
{
    public static TimeSpan? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        if (double.TryParse(s, out var plain)) return TimeSpan.FromSeconds(plain);

        double total = 0;
        bool matched = false;
        // Order matters: "ms" before "m" and "s"
        foreach (var (unit, mult) in new[] { ("h", 3600.0), ("ms", 0.001), ("m", 60.0), ("s", 1.0) })
        {
            var rx = new Regex(@"([0-9.]+)" + Regex.Escape(unit));
            foreach (Match m in rx.Matches(s))
            {
                if (double.TryParse(m.Groups[1].Value, out var v))
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
