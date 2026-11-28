import Foundation

// MARK: - Providers

enum ProviderKind: String, Codable, CaseIterable, Identifiable {
    case gemini
    case groq
    case claudeCode
    case codex

    var id: String { rawValue }

    var displayName: String {
        switch self {
        case .gemini: return "Gemini"
        case .groq: return "Groq"
        case .claudeCode: return "Claude Code"
        case .codex: return "Codex"
        }
    }

    var subtitle: String {
        switch self {
        case .gemini: return "Google · free API keys"
        case .groq: return "Groq · free API keys"
        case .claudeCode: return "Your Claude plan, via the CLI"
        case .codex: return "Your ChatGPT plan, via the CLI"
        }
    }

    var symbol: String {
        switch self {
        case .gemini: return "sparkle"
        case .groq: return "bolt.fill"
        case .claudeCode: return "asterisk"
        case .codex: return "terminal.fill"
        }
    }

    var usesAPIKeys: Bool { self == .gemini || self == .groq }

    var keyURL: URL? {
        switch self {
        case .gemini: return URL(string: "https://aistudio.google.com/apikey")
        case .groq: return URL(string: "https://console.groq.com/keys")
        default: return nil
        }
    }

    var keyPlaceholder: String {
        switch self {
        case .gemini: return "AIza…"
        case .groq: return "gsk_…"
        default: return ""
        }
    }

    var cliName: String { self == .claudeCode ? "claude" : "codex" }
}

/// Non-secret info about an API key. The secret itself lives in the Keychain, keyed by `id`.
struct APIKeyMeta: Codable, Identifiable, Hashable {
    var id: UUID = UUID()
    var label: String
    var hint: String          // last 4 characters, for display
    var isEnabled: Bool = true
    var addedAt: Date = Date()
}

enum FallbackOrder: String, Codable, CaseIterable, Identifiable {
    /// Keep the chosen model and try the user's other keys before moving to the next model.
    case keysFirst
    /// Move to the next model on the same key before trying other keys.
    case modelsFirst

    var id: String { rawValue }
    var label: String {
        self == .keysFirst ? "Try my other keys first, then the next model" : "Switch to the next model first, then other keys"
    }
}

struct APIProviderSettings: Codable, Equatable {
    var keys: [APIKeyMeta] = []
    /// Priority order — first is the primary model, the rest are fallbacks.
    var models: [String] = []
    var fallbackOrder: FallbackOrder = .keysFirst
}

struct CLIProviderSettings: Codable, Equatable {
    /// Empty = use the CLI's own default model.
    var model: String = ""
    /// Optional absolute path if the CLI isn't found automatically.
    var binaryPath: String = ""
    /// Codex only: ask for low reasoning effort for snappier replies.
    var fastMode: Bool = true
}

// MARK: - Templates

enum OutputMode: String, Codable, CaseIterable, Identifiable {
    /// Replace the whole text field with the AI result.
    case replace
    /// Remove the trigger and insert the AI result after your text.
    case append

    var id: String { rawValue }
    var label: String { self == .replace ? "Replace text" : "Insert below" }
}

struct Template: Codable, Identifiable, Hashable {
    var id: UUID = UUID()
    var name: String
    var trigger: String
    var prompt: String
    var mode: OutputMode = .replace
    var isEnabled: Bool = true

    static let placeholder = Template(name: "", trigger: "", prompt: "")

    static let defaults: [Template] = [
        Template(name: "Fix grammar", trigger: "?fix",
                 prompt: "Fix spelling, grammar and punctuation. Keep the original meaning, tone and language."),
        Template(name: "Rewrite", trigger: "?rewrite",
                 prompt: "Rewrite this so it reads clearly and naturally. Keep the meaning and roughly the same length."),
        Template(name: "Make formal", trigger: "?formal",
                 prompt: "Rewrite this in a polite, professional tone suitable for work email."),
        Template(name: "Make shorter", trigger: "?short",
                 prompt: "Make this more concise without losing any important information."),
        Template(name: "Ask", trigger: "?ask",
                 prompt: "Answer the question or complete the request in the text. Be direct and concise."),
        Template(name: "Reply", trigger: "?reply",
                 prompt: "This is a message I received. Write a friendly, natural reply to it.",
                 mode: .append),
        Template(name: "To English", trigger: "?en",
                 prompt: "Translate this into natural English."),
    ]
}

// MARK: - Preferences (stored as one JSON blob in UserDefaults)

struct Preferences: Codable {
    var isEnabled = true
    var activeProvider: ProviderKind = .gemini
    var gemini = APIProviderSettings(models: ModelCatalog.defaultModelIDs(for: .gemini))
    var groq = APIProviderSettings(models: ModelCatalog.defaultModelIDs(for: .groq))
    var claude = CLIProviderSettings(model: "haiku")
    var codex = CLIProviderSettings(model: "")
    var templates: [Template] = Template.defaults
    var playSounds = true
    var showHUD = true

    init() {}

    // Tolerant decoding so new fields never wipe old settings after an update.
    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        let d = Preferences()
        isEnabled = (try? c.decodeIfPresent(Bool.self, forKey: .isEnabled)) ?? d.isEnabled
        activeProvider = (try? c.decodeIfPresent(ProviderKind.self, forKey: .activeProvider)) ?? d.activeProvider
        gemini = (try? c.decodeIfPresent(APIProviderSettings.self, forKey: .gemini)) ?? d.gemini
        groq = (try? c.decodeIfPresent(APIProviderSettings.self, forKey: .groq)) ?? d.groq
        claude = (try? c.decodeIfPresent(CLIProviderSettings.self, forKey: .claude)) ?? d.claude
        codex = (try? c.decodeIfPresent(CLIProviderSettings.self, forKey: .codex)) ?? d.codex
        templates = (try? c.decodeIfPresent([Template].self, forKey: .templates)) ?? d.templates
        playSounds = (try? c.decodeIfPresent(Bool.self, forKey: .playSounds)) ?? d.playSounds
        showHUD = (try? c.decodeIfPresent(Bool.self, forKey: .showHUD)) ?? d.showHUD
    }
}

// MARK: - Usage

struct UsageEvent: Codable, Identifiable {
    var id: UUID = UUID()
    var date: Date
    var templateName: String
    var provider: String
    var model: String
    var inputTokens: Int
    var outputTokens: Int
    var latency: TimeInterval
    var inputWords: Int
    var success: Bool
    var keyLabel: String? = nil
    var errorKind: String? = nil
}

enum StatsRange: String, CaseIterable, Identifiable {
    case week = "7 days"
    case month = "30 days"
    case all = "All time"

    var id: String { rawValue }

    var days: Int? {
        switch self {
        case .week: return 7
        case .month: return 30
        case .all: return nil
        }
    }
}

struct DayBucket: Identifiable {
    let day: Date
    let runs: Int
    let tokens: Int
    var id: Date { day }
}

struct TemplateCount: Identifiable {
    let name: String
    let count: Int
    var id: String { name }
}

struct UsageSummary {
    var runs = 0
    var failures = 0
    var inputTokens = 0
    var outputTokens = 0
    var words = 0
    var aiTime: TimeInterval = 0
    var daily: [DayBucket] = []
    var topTemplates: [TemplateCount] = []

    var totalTokens: Int { inputTokens + outputTokens }
    var averageLatency: TimeInterval { runs == 0 ? 0 : aiTime / Double(runs) }
}

// MARK: - Formatting helpers

extension Int {
    var compact: String {
        let n = Double(self)
        if n >= 1_000_000 { return String(format: "%.1fM", n / 1_000_000) }
        if n >= 10_000 { return String(format: "%.0fK", n / 1_000) }
        if n >= 1_000 { return String(format: "%.1fK", n / 1_000) }
        return "\(self)"
    }
}

extension TimeInterval {
    var short: String {
        let s = Int(self.rounded())
        if s < 60 { return "\(s)s" }
        if s < 3600 { return "\(s / 60)m \(s % 60)s" }
        return "\(s / 3600)h \((s % 3600) / 60)m"
    }
}
