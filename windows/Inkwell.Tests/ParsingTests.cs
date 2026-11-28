using System.Net.Http;
using System.Net.Http.Headers;
using Inkwell.Core;
using Inkwell.Providers;

namespace Inkwell.Tests;

/// <summary>Limit messages in the exact formats Claude Code, Codex, Gemini and Groq print (researched Nov 2026).</summary>
public class LimitParsingTests
{
    // MARK: Claude Code

    [Fact]
    public void ClaudeStreamJson_WeeklyLimit_UsesRateLimitEvent()
    {
        var resetsAt = DateTimeOffset.UtcNow.AddDays(2).ToUnixTimeSeconds();
        var stdout = string.Join("\n",
            "{\"type\":\"system\",\"subtype\":\"init\"}",
            $"{{\"type\":\"rate_limit_event\",\"rate_limit_info\":{{\"status\":\"rejected\",\"resetsAt\":{resetsAt},\"rateLimitType\":\"seven_day\",\"overageStatus\":\"rejected\",\"isUsingOverage\":false}}}}",
            "{\"type\":\"result\",\"subtype\":\"success\",\"terminal_reason\":\"api_error\",\"is_error\":true,\"api_error_status\":429,\"api_error\":\"usage_limit_reached\",\"result\":\"You've hit your weekly limit · resets 11am (UTC)\"}");
        var ex = Assert.IsType<RateLimitException>(Record.Exception(() => CLIProviders.ParseClaude(new ProcessOutput(1, stdout, "", false))));
        Assert.Equal("weekly", ex.Window);
        Assert.True(ex.Daily);
        Assert.NotNull(ex.ResetsAt);
        Assert.InRange((ex.ResetsAt!.Value - DateTimeOffset.FromUnixTimeSeconds(resetsAt).LocalDateTime).TotalSeconds, -1, 1);
    }

    [Fact]
    public void ClaudeJson_SubtypeSuccessButIsError_IsStillALimit()
    {
        // Claude Code 2.1.x marks usage-limit failures "subtype":"success" — is_error + api_error is the real signal.
        var stdout = "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":true,\"api_error_status\":429,\"api_error\":\"usage_limit_reached\",\"result\":\"5-hour limit reached ∙ resets 2am\"}";
        var ex = Assert.IsType<RateLimitException>(Record.Exception(() => CLIProviders.ParseClaude(new ProcessOutput(1, stdout, "", false))));
        Assert.Equal("5-hour", ex.Window);
        Assert.NotNull(ex.ResetsAt);
        Assert.Equal(2, ex.ResetsAt!.Value.Hour);
        Assert.True(ex.ResetsAt > DateTime.Now);
    }

    [Fact]
    public void ClaudeLegacyEpochSuffix()
    {
        var epoch = DateTimeOffset.UtcNow.AddHours(3).ToUnixTimeSeconds();
        var (window, reset) = LimitParser.ParseClaude($"Claude AI usage limit reached|{epoch}");
        Assert.Null(window);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(epoch).LocalDateTime, reset!.Value, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void ClaudeResetWithTimeZone()
    {
        var (window, reset) = LimitParser.ParseClaude("You've hit your weekly limit · resets 11am (UTC)");
        Assert.Equal("weekly", window);
        var utc = reset!.Value.ToUniversalTime();
        Assert.Equal(11, utc.Hour);
        Assert.Equal(0, utc.Minute);
        Assert.True(reset > DateTime.Now);
    }

    [Fact]
    public void ClaudeResetWithWeekday()
    {
        var (window, reset) = LimitParser.ParseClaude("Weekly limit reached ∙ resets Mon 9am");
        Assert.Equal("weekly", window);
        Assert.Equal(DayOfWeek.Monday, reset!.Value.DayOfWeek);
        Assert.Equal(9, reset.Value.Hour);
    }

    [Fact]
    public void ClaudeSuccess_ReadsTextTokensAndUsageReading()
    {
        var stdout = string.Join("\n",
            "{\"type\":\"rate_limit_event\",\"rate_limit_info\":{\"status\":\"allowed_warning\",\"utilization\":0.86,\"resetsAt\":1900000000,\"rateLimitType\":\"five_hour\"}}",
            "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"Hello there.\",\"usage\":{\"input_tokens\":10,\"cache_read_input_tokens\":5,\"output_tokens\":3}}");
        var r = CLIProviders.ParseClaude(new ProcessOutput(0, stdout, "", false));
        Assert.Equal("Hello there.", r.Text);
        Assert.Equal(15, r.InputTokens);
        Assert.Equal(3, r.OutputTokens);
        Assert.Equal("5-hour", r.Limit!.Window);
        Assert.False(r.Limit.Rejected);
        Assert.Equal(0.86, r.Limit.Utilization);
    }

    [Fact]
    public void ClaudeExitOneWithNothing_IsFriendly()
    {
        var ex = Assert.IsAssignableFrom<AIException>(Record.Exception(() => CLIProviders.ParseClaude(new ProcessOutput(1, "", "", false))));
        Assert.Contains("limit", ex.Message);
    }

    [Fact]
    public void ClaudeNotLoggedIn()
    {
        var stdout = "{\"type\":\"result\",\"subtype\":\"error\",\"is_error\":true,\"result\":\"Invalid API key · Please run /login\"}";
        var ex = Assert.IsAssignableFrom<AIException>(Record.Exception(() => CLIProviders.ParseClaude(new ProcessOutput(1, stdout, "", false))));
        Assert.True(ex.IsFatal);
        Assert.Contains("isn't signed in", ex.Message);
    }

    // MARK: Codex

    [Fact]
    public void CodexUsageLimit_TimeToday()
    {
        var at = DateTime.Now.AddHours(2);
        var msg = $"You’ve hit your usage limit. Upgrade to Pro (https://chatgpt.com/explore/pro), visit https://chatgpt.com/settings/usage to purchase more credits or try again at {at:h:mm tt}.";
        var stdout = "{\"type\":\"thread.started\"}\n{\"type\":\"turn.failed\",\"error\":{\"message\":\"" + msg.Replace("\"", "\\\"") + "\"}}";
        var ex = Assert.IsType<RateLimitException>(Record.Exception(() => CLIProviders.ParseCodex(new ProcessOutput(1, stdout, "", false))));
        Assert.Equal("5-hour", ex.Window); // < 5 h away
        Assert.Equal(at.Hour, ex.ResetsAt!.Value.Hour);
        Assert.Equal(at.Minute, ex.ResetsAt.Value.Minute);
    }

    [Fact]
    public void CodexUsageLimit_DateFormat_IsWeekly()
    {
        var (window, reset) = LimitParser.ParseCodex("You've hit your usage limit. Try again at Mar 20th, 2027 3:36 PM.");
        Assert.Equal("weekly", window);
        Assert.Equal(new DateTime(2027, 3, 20, 15, 36, 0), reset);
    }

    [Fact]
    public void CodexTryAgainLater_HasNoTime()
    {
        var (_, reset) = LimitParser.ParseCodex("You've hit your usage limit. Try again later.");
        Assert.Null(reset);
    }

    [Fact]
    public void CodexSuccess()
    {
        var stdout = string.Join("\n",
            "{\"type\":\"thread.started\",\"thread_id\":\"x\"}",
            "{\"type\":\"item.completed\",\"item\":{\"id\":\"i\",\"type\":\"agent_message\",\"text\":\"Done!\"}}",
            "{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":100,\"output_tokens\":7,\"reasoning_output_tokens\":3}}");
        var r = CLIProviders.ParseCodex(new ProcessOutput(0, stdout, "", false));
        Assert.Equal("Done!", r.Text);
        Assert.Equal(100, r.InputTokens);
        Assert.Equal(10, r.OutputTokens);
    }

    // MARK: Gemini / Groq HTTP

    private static HttpResponseHeaders Headers(string? retryAfter = null)
    {
        var resp = new HttpResponseMessage();
        if (retryAfter is not null) resp.Headers.TryAddWithoutValidation("retry-after", retryAfter);
        return resp.Headers;
    }

    [Fact]
    public void GeminiDailyQuota_ResetsAtMidnightPacific_IgnoresRetryDelay()
    {
        var body = """
        {"error":{"code":429,"message":"You exceeded your current quota. Quota exceeded for metric: generativelanguage.googleapis.com/generate_content_free_tier_requests, limit: 500","status":"RESOURCE_EXHAUSTED",
        "details":[{"@type":"type.googleapis.com/google.rpc.QuotaFailure","violations":[{"quotaMetric":"generativelanguage.googleapis.com/generate_content_free_tier_requests","quotaId":"GenerateRequestsPerDayPerProjectPerModel-FreeTier"}]},
                   {"@type":"type.googleapis.com/google.rpc.RetryInfo","retryDelay":"1s"}]}}
        """;
        var ex = Assert.IsType<RateLimitException>(HTTPProviders.Classify(429, body, Headers(), ProviderKind.Gemini, "gemini-3.5-flash-lite"));
        Assert.True(ex.Daily);
        Assert.Equal("daily", ex.Window);
        var pacific = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        var resetPt = TimeZoneInfo.ConvertTime(ex.ResetsAt!.Value, TimeZoneInfo.Local, pacific);
        Assert.Equal(0, resetPt.Hour);
        Assert.True(ex.ResetsAt > DateTime.Now.AddMinutes(1));
    }

    [Fact]
    public void GeminiPerMinute_UsesRetryDelay()
    {
        var body = """
        {"error":{"code":429,"message":"Quota exceeded. Please retry in 34.2s.","status":"RESOURCE_EXHAUSTED",
        "details":[{"@type":"type.googleapis.com/google.rpc.QuotaFailure","violations":[{"quotaId":"GenerateRequestsPerMinutePerProjectPerModel-FreeTier"}]},
                   {"@type":"type.googleapis.com/google.rpc.RetryInfo","retryDelay":"34s"}]}}
        """;
        var ex = Assert.IsType<RateLimitException>(HTTPProviders.Classify(429, body, Headers(), ProviderKind.Gemini, "m"));
        Assert.False(ex.Daily);
        Assert.Equal("per-minute", ex.Window);
        Assert.InRange(ex.RetryAfter!.Value.TotalSeconds, 34, 35);
    }

    [Fact]
    public void GeminiLimitZero_MeansModelNotOnFreeTier()
    {
        var body = """{"error":{"code":429,"message":"Quota exceeded for metric: x, limit: 0","status":"RESOURCE_EXHAUSTED"}}""";
        var ex = HTTPProviders.Classify(429, body, Headers(), ProviderKind.Gemini, "gemini-4-pro");
        Assert.Equal("model_unavailable", ex.ShortKind);
    }

    [Fact]
    public void GroqDailyTokens_UsesMessageDuration()
    {
        var body = """{"error":{"message":"Rate limit reached for model `openai/gpt-oss-120b` in organization `org_1` service tier `on_demand` on tokens per day (TPD): Limit 200000, Used 193487, Requested 7464. Please try again in 6m50.832s.","type":"tokens","code":"rate_limit_exceeded"}}""";
        var ex = Assert.IsType<RateLimitException>(HTTPProviders.Classify(429, body, Headers(), ProviderKind.Groq, "openai/gpt-oss-120b"));
        Assert.True(ex.Daily);
        Assert.InRange(ex.RetryAfter!.Value.TotalSeconds, 410, 411);
    }

    [Fact]
    public void GroqRetryAfterHeaderWins()
    {
        var body = """{"error":{"message":"Rate limit reached ... on requests per minute (RPM): Limit 30. Please try again in 2s.","code":"rate_limit_exceeded"}}""";
        var ex = Assert.IsType<RateLimitException>(HTTPProviders.Classify(429, body, Headers("7"), ProviderKind.Groq, "m"));
        Assert.False(ex.Daily);
        Assert.Equal(7, ex.RetryAfter!.Value.TotalSeconds);
    }

    [Fact]
    public void GeminiHighDemand_IsFriendlyAndTransient()
    {
        var body = """{"error":{"code":503,"message":"This model is currently experiencing high demand. Spikes in demand are usually temporary. Please try again later.","status":"UNAVAILABLE"}}""";
        var ex = HTTPProviders.Classify(503, body, Headers(), ProviderKind.Gemini, "gemini-3.8-flash");
        Assert.True(ex.IsTransient);
        Assert.Equal("server", ex.ShortKind); // so the router falls back to the next model
        Assert.StartsWith("Gemini 3.8 Flash is busy right now", ex.Message);
    }

    [Fact]
    public void GroqFlexCapacityIsTransient()
    {
        var ex = HTTPProviders.Classify(498, """{"error":{"message":"Flex tier capacity exceeded"}}""", Headers(), ProviderKind.Groq, "m");
        Assert.True(ex.IsTransient);
    }

    [Theory]
    [InlineData(401, "invalid_key")]
    [InlineData(403, "invalid_key")]
    [InlineData(404, "model_unavailable")]
    [InlineData(503, "server")]
    [InlineData(504, "timeout")]
    public void StatusCodes(int status, string kind) =>
        Assert.Equal(kind, HTTPProviders.Classify(status, "{}", Headers(), ProviderKind.Gemini, "m").ShortKind);

    [Fact]
    public void HttpClientTimeout_IsTimeoutNotCancelled()
    {
        var e = AIException.From(new TaskCanceledException("x", new TimeoutException()));
        Assert.Equal("timeout", e.ShortKind);
    }
}

public class TextTests
{
    [Theory]
    [InlineData("34s", 34)]
    [InlineData("1.5s", 1.5)]
    [InlineData("2m59.56s", 179.56)]
    [InlineData("1h2m3s", 3723)]
    [InlineData("500ms", 0.5)]
    [InlineData("7", 7)]
    public void Durations(string raw, double seconds) =>
        Assert.Equal(seconds, DurationParser.Parse(raw)!.Value.TotalSeconds, 3);

    [Theory]
    [InlineData("Fix grammar", "Fixing grammar")]
    [InlineData("Rewrite", "Rewriting")]
    [InlineData("Make formal", "Making it formal")]
    [InlineData("Make shorter", "Making it shorter")]
    [InlineData("To English", "Translating to English")]
    [InlineData("To Spanish", "Translating to Spanish")]
    [InlineData("Ask", "Thinking")]
    [InlineData("Reply", "Writing a reply")]
    [InlineData("Summarize", "Summarizing")]
    [InlineData("Proofread email", "Proofreading email")]
    [InlineData("Simplify", "Simplifying")]
    [InlineData("Make casual", "Making it casual")]
    [InlineData("Translate to French", "Translating to French")]
    [InlineData("Shorten", "Shortening")]
    [InlineData("", "Working")]
    public void WorkingLabels_(string name, string expected) => Assert.Equal(expected, WorkingLabels.For(name));

    [Theory]
    [InlineData("<think>hmm</think>Hello", "Hello")]
    [InlineData("```\nHello\n```", "Hello")]
    [InlineData("\"Hello\"", "Hello")]
    [InlineData("\"He said \"hi\"\"", "\"He said \"hi\"\"")]
    [InlineData("  Hello  \r\n", "Hello")]
    public void Clean(string raw, string expected) => Assert.Equal(expected, AIRouter.Clean(raw));

    [Theory]
    [InlineData(999, "999")]
    [InlineData(1500, "1.5K")]
    [InlineData(25000, "25K")]
    [InlineData(2_500_000, "2.5M")]
    public void Compact(int n, string expected) => Assert.Equal(expected, n.Compact());
}
