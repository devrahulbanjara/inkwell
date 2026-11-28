import Foundation

/// Curated free-tier models, verified against the official model and rate-limit pages (Nov 2026).
/// Free limits change often, so the notes are approximate and the app also loads
/// whatever models your key can see straight from the provider.
struct ModelInfo: Identifiable, Hashable {
    let id: String
    let name: String
    let note: String
    var isFree: Bool = true
    var recommended: Bool = false
    var preview: Bool = false
}

enum ModelCatalog {
    static func models(for kind: ProviderKind) -> [ModelInfo] {
        switch kind {
        case .gemini: return gemini
        case .groq: return groq
        case .claudeCode: return claude
        case .codex: return []
        }
    }

    /// Default priority list for a brand-new setup: primary first, then fallbacks.
    static func defaultModelIDs(for kind: ProviderKind) -> [String] {
        switch kind {
        case .gemini: return ["gemini-3.5-flash-lite", "gemini-3.1-flash-lite", "gemini-3.6-flash"]
        case .groq: return ["openai/gpt-oss-120b", "openai/gpt-oss-20b"]
        case .claudeCode: return ["haiku"]
        case .codex: return []
        }
    }

    /// Friendly name for any known model ID ("gemini-3.8-flash" → "Gemini 3.8 Flash").
    static func displayName(_ id: String) -> String {
        (gemini + groq + claude).first { $0.id == id }?.name ?? id
    }

    static func info(_ id: String, kind: ProviderKind) -> ModelInfo? {
        models(for: kind).first { $0.id == id }
    }

    /// Models known NOT to be on the free tier — flagged if they show up in a key's model list.
    static func isKnownPaid(_ id: String, kind: ProviderKind) -> Bool {
        switch kind {
        case .gemini:
            return id.contains("-pro") || id.hasPrefix("gemini-4") || id.hasPrefix("gemini-2.5")
        case .groq:
            return ["llama-3.3-70b-versatile", "llama-3.1-8b-instant", "minimaxai/minimax-m2.7"].contains(id)
        default:
            return false
        }
    }

    // MARK: Gemini — free tier is Flash / Flash-Lite only. Limits are per Google Cloud project.

    static let gemini: [ModelInfo] = [
        ModelInfo(id: "gemini-3.5-flash-lite", name: "Gemini 3.5 Flash-Lite",
                  note: "Stable · fastest · largest free quota (≈15/min, ≈500/day)", recommended: true),
        ModelInfo(id: "gemini-3.1-flash-lite", name: "Gemini 3.1 Flash-Lite",
                  note: "Stable · fast · large free quota (≈15/min, ≈500/day)"),
        ModelInfo(id: "gemini-3.6-flash", name: "Gemini 3.6 Flash",
                  note: "Stable · smarter · smaller free quota"),
        ModelInfo(id: "gemini-3.8-flash", name: "Gemini 3.8 Flash",
                  note: "Newest · smartest free model · tight quota (≈5/min, ≈20/day)"),
        ModelInfo(id: "gemini-3-flash-preview", name: "Gemini 3 Flash",
                  note: "Preview · may change or be retired", preview: true),
        ModelInfo(id: "gemini-flash-latest", name: "Gemini Flash (latest)",
                  note: "Alias · always points to Google's newest Flash"),
    ]

    // MARK: Groq — free plan models (≈30/min, ≈1,000/day, 8K tokens/min each).

    static let groq: [ModelInfo] = [
        ModelInfo(id: "openai/gpt-oss-120b", name: "GPT-OSS 120B",
                  note: "Production · best quality on Groq's free plan", recommended: true),
        ModelInfo(id: "openai/gpt-oss-20b", name: "GPT-OSS 20B",
                  note: "Production · very fast"),
        ModelInfo(id: "qwen/qwen3.8-27b", name: "Qwen 3.8 27B",
                  note: "Preview · strong multilingual", preview: true),
    ]

    // MARK: Claude Code — model aliases accepted by `claude --model`.

    static let claude: [ModelInfo] = [
        ModelInfo(id: "haiku", name: "Haiku", note: "Fastest · uses the least of your plan", recommended: true),
        ModelInfo(id: "sonnet", name: "Sonnet", note: "Smarter · a bit slower"),
        ModelInfo(id: "opus", name: "Opus", note: "Most capable · slowest, heaviest on limits"),
        ModelInfo(id: "fable", name: "Fable", note: "Needs Fable access on your plan"),
    ]

    /// Filters a provider's raw model list down to text-chat models worth offering.
    static func isChatModel(_ id: String, kind: ProviderKind) -> Bool {
        let lower = id.lowercased()
        let blocked = ["embedding", "embed", "image", "imagen", "veo", "tts", "audio", "live", "transcribe",
                       "whisper", "orpheus", "guard", "safeguard", "aqa", "robotics", "computer-use", "omni", "native"]
        if blocked.contains(where: { lower.contains($0) }) { return false }
        if kind == .gemini { return lower.hasPrefix("gemini") || lower.hasPrefix("gemma") }
        return true
    }
}
