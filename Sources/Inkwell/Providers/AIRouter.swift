import Foundation

enum Prompts {
    static let system = """
    You are Inkwell, a writing assistant that works inside any text field on the user's Mac.
    Apply the instruction to the user's text.
    Reply with ONLY the final text — no preamble, no explanation, no surrounding quotes, no code fences.
    Keep the same language as the input unless the instruction says otherwise.
    """

    static func user(_ template: Template, text: String) -> String {
        "Instruction: \(template.prompt)\n\nText:\n\(text)"
    }
}

struct AIResult {
    let text: String
    let inputTokens: Int
    let outputTokens: Int
    let provider: ProviderKind
    let model: String
    let keyLabel: String?
    let attempts: Int
}

// MARK: - Quota / cooldown tracking (drives both routing and the key status badges in Settings)

@MainActor
final class QuotaTracker: ObservableObject {
    struct Block: Equatable {
        let until: Date
        let daily: Bool
    }

    /// "keyID|model" → cooldown
    @Published private(set) var blocks: [String: Block] = [:]
    /// keyID → reason the key was rejected
    @Published private(set) var invalidKeys: [UUID: String] = [:]
    /// Last time each key succeeded.
    @Published private(set) var lastSuccess: [UUID: Date] = [:]

    private func slot(_ key: UUID?, _ model: String) -> String { "\(key?.uuidString ?? "cli")|\(model)" }

    func blockedUntil(key: UUID?, model: String) -> Date? {
        guard let b = blocks[slot(key, model)] else { return nil }
        if b.until <= Date() { blocks[slot(key, model)] = nil; return nil }
        return b.until
    }

    func block(key: UUID?, model: String, until: Date, daily: Bool) {
        blocks[slot(key, model)] = Block(until: until, daily: daily)
    }

    func markInvalid(_ key: UUID, reason: String) { invalidKeys[key] = reason }
    func clearInvalid(_ key: UUID) { invalidKeys[key] = nil }

    func markSuccess(key: UUID?, model: String) {
        blocks[slot(key, model)] = nil
        if let key { invalidKeys[key] = nil; lastSuccess[key] = Date() }
    }

    func forget(key: UUID) {
        invalidKeys[key] = nil
        lastSuccess[key] = nil
        blocks = blocks.filter { !$0.key.hasPrefix(key.uuidString) }
    }

    enum KeyStatus: Equatable {
        case ready
        case invalid(String)
        case partial(Date)      // some models cooling down
        case cooling(Date, daily: Bool) // every model cooling down
    }

    func status(of key: UUID, models: [String]) -> KeyStatus {
        if let r = invalidKeys[key] { return .invalid(r) }
        let now = Date()
        let active = models.compactMap { m -> Block? in
            guard let b = blocks[slot(key, m)], b.until > now else { return nil }
            return b
        }
        guard !active.isEmpty else { return .ready }
        let soonest = active.map(\.until).min()!
        if active.count >= models.count { return .cooling(soonest, daily: active.allSatisfy(\.daily)) }
        return .partial(soonest)
    }
}

// MARK: - Router

/// Picks the provider, rotates API keys, falls back across models, and turns every
/// failure into a clear, actionable message. Never throws an untyped error.
@MainActor
final class AIRouter {
    private let settings: SettingsStore
    let quota: QuotaTracker
    private var nextKeyIndex: [ProviderKind: Int] = [:]

    /// Called with short status updates ("Switching to backup key…") for the floating HUD.
    var onStatus: ((String) -> Void)?

    private let overallBudget: TimeInterval = 75

    init(settings: SettingsStore, quota: QuotaTracker) {
        self.settings = settings
        self.quota = quota
    }

    func complete(system: String, user: String, provider: ProviderKind? = nil) async throws -> AIResult {
        let kind = provider ?? settings.prefs.activeProvider
        do {
            if kind.usesAPIKeys {
                return try await completeWithKeys(kind: kind, system: system, user: user)
            } else {
                return try await completeWithCLI(kind: kind, system: system, user: user)
            }
        } catch {
            throw AIError.from(error)
        }
    }

    // MARK: API-key providers (Gemini, Groq)

    private func completeWithKeys(kind: ProviderKind, system: String, user: String) async throws -> AIResult {
        let cfg = settings.api(kind)
        let keys = cfg.keys.filter { $0.isEnabled && !settings.secret(for: $0.id).isEmpty }
        guard !keys.isEmpty else {
            throw AIError.notConfigured("Add a \(kind.displayName) API key in Settings → AI Provider.")
        }
        let models = cfg.models.isEmpty ? ModelCatalog.defaultModelIDs(for: kind) : cfg.models

        // Round-robin the starting key so load spreads across keys over time.
        let start = (nextKeyIndex[kind] ?? 0) % keys.count
        let rotated = Array(keys[start...] + keys[..<start])

        var plan: [(key: APIKeyMeta, model: String)] = []
        switch cfg.fallbackOrder {
        case .keysFirst:
            for m in models { for k in rotated { plan.append((k, m)) } }
        case .modelsFirst:
            for k in rotated { for m in models { plan.append((k, m)) } }
        }

        let began = Date()
        var attempts = 0
        var skippedModels = Set<String>()
        var lastError: AIError?
        var soonest: Date?
        var announcedFallback = false

        for (key, model) in plan {
            if skippedModels.contains(model) { continue }
            if quota.invalidKeys[key.id] != nil { continue }
            if let until = quota.blockedUntil(key: key.id, model: model) {
                soonest = min(soonest ?? until, until)
                continue
            }
            if Date().timeIntervalSince(began) > overallBudget { throw AIError.timeout }

            if attempts > 0 && !announcedFallback {
                onStatus?("Trying backup…")
                announcedFallback = true
            }

            do {
                attempts += 1
                let secret = settings.secret(for: key.id)
                let raw = try await withTransientRetry {
                    try await HTTPProviders.complete(kind: kind, key: secret, model: model, system: system, user: user)
                }
                let text = Self.clean(raw.text)
                guard !text.isEmpty else { throw AIError.emptyResponse }
                quota.markSuccess(key: key.id, model: model)
                if let i = keys.firstIndex(where: { $0.id == key.id }) { nextKeyIndex[kind] = i + 1 }
                return AIResult(text: text, inputTokens: raw.inputTokens, outputTokens: raw.outputTokens,
                                provider: kind, model: model, keyLabel: key.label, attempts: attempts)
            } catch {
                let e = AIError.from(error)
                lastError = e
                if e.isFatal { throw e }
                switch e {
                case .rateLimited(let after, let daily, _):
                    let until = daily ? Self.dailyReset(kind: kind, retryAfter: after)
                                      : Date().addingTimeInterval(min(max(after ?? 30, 2), 600))
                    quota.block(key: key.id, model: model, until: until, daily: daily)
                    soonest = min(soonest ?? until, until)
                case .invalidKey(let reason):
                    quota.markInvalid(key.id, reason: reason)
                case .modelUnavailable:
                    // Not available on this key (e.g. no free quota) — park it for a day on this key only.
                    quota.block(key: key.id, model: model, until: Date().addingTimeInterval(86_400), daily: true)
                    if cfg.keys.count == 1 { skippedModels.insert(model) }
                case .tooLargeForModel, .server, .timeout, .emptyResponse:
                    skippedModels.insert(model)
                default:
                    break
                }
            }
        }

        throw Self.exhaustedError(kind: kind, last: lastError, soonest: soonest, attempts: attempts)
    }

    // MARK: CLI providers (Claude Code, Codex)

    private func completeWithCLI(kind: ProviderKind, system: String, user: String) async throws -> AIResult {
        let cfg = kind == .claudeCode ? settings.prefs.claude : settings.prefs.codex
        let model = cfg.model.trimmingCharacters(in: .whitespaces)
        if let until = quota.blockedUntil(key: nil, model: kind.rawValue) {
            throw AIError.allExhausted("\(kind.displayName) hit its usage limit. Try again after \(until.formatted(date: .omitted, time: .shortened)).")
        }
        do {
            let raw = try await withTransientRetry {
                try await CLIProviders.complete(kind: kind, settings: cfg, system: system, user: user)
            }
            let text = Self.clean(raw.text)
            guard !text.isEmpty else { throw AIError.emptyResponse }
            quota.markSuccess(key: nil, model: kind.rawValue)
            return AIResult(text: text, inputTokens: raw.inputTokens, outputTokens: raw.outputTokens,
                            provider: kind, model: model.isEmpty ? "default" : model, keyLabel: nil, attempts: 1)
        } catch {
            let e = AIError.from(error)
            if case .rateLimited(let after, let daily, let message) = e {
                let until = Date().addingTimeInterval(after ?? (daily ? 3600 : 60))
                quota.block(key: nil, model: kind.rawValue, until: until, daily: daily)
                throw AIError.allExhausted("\(kind.displayName) is rate-limited: \(message.prefix(140))")
            }
            throw e
        }
    }

    // MARK: Helpers

    /// One quick retry with jittered backoff for transient errors (5xx, timeouts).
    private func withTransientRetry<T>(_ op: @escaping () async throws -> T) async throws -> T {
        do {
            return try await op()
        } catch {
            let e = AIError.from(error)
            guard e.isTransient else { throw e }
            let delay = 0.6 + Double.random(in: 0...0.6)
            try await Task.sleep(for: .seconds(delay))
            return try await op()
        }
    }

    /// Gemini's daily quotas reset at midnight Pacific time; Groq tells us via retry-after.
    static func dailyReset(kind: ProviderKind, retryAfter: TimeInterval?) -> Date {
        if let retryAfter, retryAfter > 0 { return Date().addingTimeInterval(retryAfter) }
        if kind == .gemini, let pacific = TimeZone(identifier: "America/Los_Angeles") {
            var cal = Calendar(identifier: .gregorian)
            cal.timeZone = pacific
            let startOfToday = cal.startOfDay(for: Date())
            return cal.date(byAdding: .day, value: 1, to: startOfToday) ?? Date().addingTimeInterval(86_400)
        }
        return Date().addingTimeInterval(3600)
    }

    private static func exhaustedError(kind: ProviderKind, last: AIError?, soonest: Date?, attempts: Int) -> AIError {
        let when = soonest.map { " Next available around \($0.formatted(date: .omitted, time: .shortened))." } ?? ""
        switch last {
        case .some(.invalidKey):
            return .allExhausted("Your \(kind.displayName) keys were rejected. Check them in Settings.\(when)")
        case .some(.rateLimited(_, true, _)):
            return .allExhausted("All \(kind.displayName) keys hit today's free limit.\(when) Add another key or switch provider.")
        case .some(.rateLimited):
            return .allExhausted("All \(kind.displayName) keys are rate-limited right now.\(when)")
        case .some(let e):
            return attempts == 1 ? e : .allExhausted("Every \(kind.displayName) key and model failed. Last error: \(e.localizedDescription)")
        case .none:
            return .allExhausted("All \(kind.displayName) keys are cooling down.\(when)")
        }
    }

    /// Strips code fences, wrapping quotes and leaked <think> blocks some models add despite instructions.
    static func clean(_ s: String) -> String {
        var t = s
        while let open = t.range(of: "<think>"), let close = t.range(of: "</think>", range: open.upperBound..<t.endIndex) {
            t.removeSubrange(open.lowerBound..<close.upperBound)
        }
        t = t.trimmingCharacters(in: .whitespacesAndNewlines)
        if t.hasPrefix("```") {
            var lines = t.components(separatedBy: "\n")
            lines.removeFirst()
            if lines.last?.trimmingCharacters(in: .whitespaces).hasPrefix("```") == true { lines.removeLast() }
            t = lines.joined(separator: "\n").trimmingCharacters(in: .whitespacesAndNewlines)
        }
        if t.count > 1, t.hasPrefix("\""), t.hasSuffix("\""), !t.dropFirst().dropLast().contains("\"") {
            t = String(t.dropFirst().dropLast())
        }
        return t
    }
}
