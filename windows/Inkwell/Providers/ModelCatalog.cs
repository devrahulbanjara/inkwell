using System;
using System.Collections.Generic;
using System.Linq;
using Inkwell.Core;

namespace Inkwell.Providers;

public sealed record ModelInfo(
    string Id,
    string Name,
    string Note,
    bool IsFree = true,
    bool Recommended = false,
    bool Preview = false);

public static class ModelCatalog
{
    public static IReadOnlyList<ModelInfo> Models(ProviderKind kind) => kind switch
    {
        ProviderKind.Gemini => Gemini,
        ProviderKind.Groq => Groq,
        ProviderKind.ClaudeCode => Claude,
        _ => Array.Empty<ModelInfo>()
    };

    public static List<string> DefaultModelIDs(ProviderKind kind) => kind switch
    {
        ProviderKind.Gemini => new() { "gemini-3.5-flash-lite", "gemini-3.1-flash-lite", "gemini-3.6-flash" },
        ProviderKind.Groq => new() { "openai/gpt-oss-120b", "openai/gpt-oss-20b" },
        ProviderKind.ClaudeCode => new() { "haiku" },
        _ => new()
    };

    /// <summary>Friendly name for any model ID ("gemini-3.8-flash" → "Gemini 3.8 Flash").</summary>
    public static string DisplayName(string id) =>
        Gemini.Concat(Groq).Concat(Claude).FirstOrDefault(m => m.Id == id)?.Name ?? id;

    public static ModelInfo? Info(string id, ProviderKind kind) =>
        Models(kind).FirstOrDefault(m => m.Id == id);

    public static bool IsKnownPaid(string id, ProviderKind kind) => kind switch
    {
        ProviderKind.Gemini => id.Contains("-pro", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("gemini-4", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("gemini-2.5", StringComparison.OrdinalIgnoreCase),
        ProviderKind.Groq => id is "llama-3.3-70b-versatile" or "llama-3.1-8b-instant" or "minimaxai/minimax-m2.7",
        _ => false
    };

    public static readonly IReadOnlyList<ModelInfo> Gemini = new[]
    {
        new ModelInfo("gemini-3.5-flash-lite", "Gemini 3.5 Flash-Lite",
            "Stable · fastest · largest free quota (≈15/min, ≈500/day)", Recommended: true),
        new ModelInfo("gemini-3.1-flash-lite", "Gemini 3.1 Flash-Lite",
            "Stable · fast · large free quota (≈15/min, ≈500/day)"),
        new ModelInfo("gemini-3.6-flash", "Gemini 3.6 Flash",
            "Stable · smarter · smaller free quota"),
        new ModelInfo("gemini-3.8-flash", "Gemini 3.8 Flash",
            "Newest · smartest free model · tight quota (≈5/min, ≈20/day)"),
        new ModelInfo("gemini-3-flash-preview", "Gemini 3 Flash",
            "Preview · may change or be retired", Preview: true),
        new ModelInfo("gemini-flash-latest", "Gemini Flash (latest)",
            "Alias · always points to Google's newest Flash"),
    };

    public static readonly IReadOnlyList<ModelInfo> Groq = new[]
    {
        new ModelInfo("openai/gpt-oss-120b", "GPT-OSS 120B",
            "Production · best quality on Groq's free plan", Recommended: true),
        new ModelInfo("openai/gpt-oss-20b", "GPT-OSS 20B",
            "Production · very fast"),
        new ModelInfo("qwen/qwen3.8-27b", "Qwen 3.8 27B",
            "Preview · strong multilingual", Preview: true),
    };

    public static readonly IReadOnlyList<ModelInfo> Claude = new[]
    {
        new ModelInfo("haiku", "Haiku", "Fastest · uses the least of your plan", Recommended: true),
        new ModelInfo("sonnet", "Sonnet", "Smarter · a bit slower"),
        new ModelInfo("opus", "Opus", "Most capable · slowest, heaviest on limits"),
        new ModelInfo("fable", "Fable", "Needs Fable access on your plan"),
    };

    private static readonly string[] BlockedFragments =
    {
        "embedding", "embed", "image", "imagen", "veo", "tts", "audio", "live", "transcribe",
        "whisper", "orpheus", "guard", "safeguard", "aqa", "robotics", "computer-use", "omni", "native"
    };

    public static bool IsChatModel(string id, ProviderKind kind)
    {
        var lower = id.ToLowerInvariant();
        if (BlockedFragments.Any(b => lower.Contains(b, StringComparison.Ordinal))) return false;
        if (kind == ProviderKind.Gemini) return lower.StartsWith("gemini") || lower.StartsWith("gemma");
        return true;
    }
}
