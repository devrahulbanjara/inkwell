using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Inkwell.Providers;

namespace Inkwell.Core;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProviderKind
{
    Gemini,
    Groq,
    ClaudeCode,
    Codex
}

public static class ProviderKindExtensions
{
    public static readonly ProviderKind[] All =
    {
        ProviderKind.Gemini,
        ProviderKind.Groq,
        ProviderKind.ClaudeCode,
        ProviderKind.Codex
    };

    public static string DisplayName(this ProviderKind kind) => kind switch
    {
        ProviderKind.Gemini => "Gemini",
        ProviderKind.Groq => "Groq",
        ProviderKind.ClaudeCode => "Claude Code",
        ProviderKind.Codex => "Codex",
        _ => kind.ToString()
    };

    public static string Subtitle(this ProviderKind kind) => kind switch
    {
        ProviderKind.Gemini => "Google · free API keys",
        ProviderKind.Groq => "Groq · free API keys",
        ProviderKind.ClaudeCode => "Your Claude plan, via the CLI",
        ProviderKind.Codex => "Your ChatGPT plan, via the CLI",
        _ => ""
    };

    public static bool UsesApiKeys(this ProviderKind kind) => kind is ProviderKind.Gemini or ProviderKind.Groq;

    public static string? KeyUrl(this ProviderKind kind) => kind switch
    {
        ProviderKind.Gemini => "https://aistudio.google.com/apikey",
        ProviderKind.Groq => "https://console.groq.com/keys",
        _ => null
    };

    public static string KeyPlaceholder(this ProviderKind kind) => kind switch
    {
        ProviderKind.Gemini => "AIza...",
        ProviderKind.Groq => "gsk_...",
        _ => ""
    };

    public static string CliName(this ProviderKind kind) => kind == ProviderKind.ClaudeCode ? "claude" : "codex";

    public static string TintHex(this ProviderKind kind) => kind switch
    {
        ProviderKind.Gemini => "#2563EB",
        ProviderKind.Groq => "#F55036",
        ProviderKind.ClaudeCode => "#D97757",
        ProviderKind.Codex => "#0D9488",
        _ => "#6366F1"
    };
}

public sealed class APIKeyMeta
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Label { get; set; } = "";
    public string Hint { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FallbackOrder
{
    KeysFirst,
    ModelsFirst
}

public static class FallbackOrderExtensions
{
    public static string Label(this FallbackOrder order) => order == FallbackOrder.KeysFirst
        ? "Try my other keys first, then the next model"
        : "Switch to the next model first, then other keys";

    public static string ShortLabel(this FallbackOrder order) => order == FallbackOrder.KeysFirst
        ? "Try other keys first"
        : "Try next model first";
}

public sealed class APIProviderSettings
{
    public List<APIKeyMeta> Keys { get; set; } = new();
    public List<string> Models { get; set; } = new();
    public FallbackOrder FallbackOrder { get; set; } = FallbackOrder.KeysFirst;
}

public sealed class CLIProviderSettings
{
    public string Model { get; set; } = "";
    public string BinaryPath { get; set; } = "";
    public bool FastMode { get; set; } = true;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OutputMode
{
    Replace,
    Append
}

public static class OutputModeExtensions
{
    public static string Label(this OutputMode mode) => mode == OutputMode.Replace ? "Replace text" : "Insert below";
}

public sealed class Template
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Trigger { get; set; } = "";
    public string Prompt { get; set; } = "";
    public OutputMode Mode { get; set; } = OutputMode.Replace;
    public bool IsEnabled { get; set; } = true;

    public Template Clone() => new()
    {
        Id = Id,
        Name = Name,
        Trigger = Trigger,
        Prompt = Prompt,
        Mode = Mode,
        IsEnabled = IsEnabled
    };

    public static List<Template> CreateDefaults() => new()
    {
        new Template
        {
            Name = "Fix grammar",
            Trigger = "?fix",
            Prompt = "Fix spelling, grammar and punctuation. Keep the original meaning, tone and language."
        },
        new Template
        {
            Name = "Rewrite",
            Trigger = "?rewrite",
            Prompt = "Rewrite this so it reads clearly and naturally. Keep the meaning and roughly the same length."
        },
        new Template
        {
            Name = "Make formal",
            Trigger = "?formal",
            Prompt = "Rewrite this in a polite, professional tone suitable for work email."
        },
        new Template
        {
            Name = "Make shorter",
            Trigger = "?short",
            Prompt = "Make this more concise without losing any important information."
        },
        new Template
        {
            Name = "Ask",
            Trigger = "?ask",
            Prompt = "Answer the question or complete the request in the text. Be direct and concise."
        },
        new Template
        {
            Name = "Reply",
            Trigger = "?reply",
            Prompt = "This is a message I received. Write a friendly, natural reply to it.",
            Mode = OutputMode.Append
        },
        new Template
        {
            Name = "To English",
            Trigger = "?en",
            Prompt = "Translate this into natural English."
        }
    };
}

public sealed class Preferences
{
    public bool IsEnabled { get; set; } = true;
    public ProviderKind ActiveProvider { get; set; } = ProviderKind.Gemini;
    public APIProviderSettings Gemini { get; set; } = new() { Models = ModelCatalog.DefaultModelIDs(ProviderKind.Gemini) };
    public APIProviderSettings Groq { get; set; } = new() { Models = ModelCatalog.DefaultModelIDs(ProviderKind.Groq) };
    public CLIProviderSettings Claude { get; set; } = new() { Model = "haiku" };
    public CLIProviderSettings Codex { get; set; } = new() { Model = "" };
    public List<Template> Templates { get; set; } = Template.CreateDefaults();
    public bool PlaySounds { get; set; } = true;
    public bool ShowHud { get; set; } = true;
}

public sealed class UsageEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset Date { get; set; }
    public string TemplateName { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public double Latency { get; set; }
    public int InputWords { get; set; }
    public bool Success { get; set; }
    public string? KeyLabel { get; set; }
    public string? ErrorKind { get; set; }
}

public enum StatsRange
{
    Week,
    Month,
    All
}

public static class StatsRangeExtensions
{
    public static string Label(this StatsRange range) => range switch
    {
        StatsRange.Week => "7 days",
        StatsRange.Month => "30 days",
        StatsRange.All => "All time",
        _ => ""
    };

    public static int? Days(this StatsRange range) => range switch
    {
        StatsRange.Week => 7,
        StatsRange.Month => 30,
        StatsRange.All => null,
        _ => null
    };
}

public sealed record DayBucket(DateTime Day, int Runs, int Tokens);

public sealed record TemplateCount(string Name, int Count);

public sealed class UsageSummary
{
    public int Runs { get; set; }
    public int Failures { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int Words { get; set; }
    public double AiTime { get; set; }
    public List<DayBucket> Daily { get; set; } = new();
    public List<TemplateCount> TopTemplates { get; set; } = new();

    public int TotalTokens => InputTokens + OutputTokens;
    public double AverageLatency => Runs == 0 ? 0 : AiTime / Runs;
}

public static class FormatHelpers
{
    public static string Compact(this int value)
    {
        double n = value;
        if (n >= 1_000_000) return $"{n / 1_000_000:0.0}M";
        if (n >= 10_000) return $"{n / 1_000:0}K";
        if (n >= 1_000) return $"{n / 1_000:0.0}K";
        return value.ToString();
    }

    public static string ShortDuration(this double seconds)
    {
        var s = (int)Math.Round(seconds);
        if (s < 60) return $"{s}s";
        if (s < 3600) return $"{s / 60}m {s % 60}s";
        return $"{s / 3600}h {(s % 3600) / 60}m";
    }
}
