import Foundation

/// Every failure a provider can produce, classified so the router knows what to do next.
enum AIError: LocalizedError, Equatable {
    case notConfigured(String)
    case invalidKey(String)
    /// `daily` = a per-day quota; backoff won't help until it resets.
    case rateLimited(retryAfter: TimeInterval?, daily: Bool, message: String)
    /// A plan limit on Claude Code / Codex (5-hour session, weekly, Opus weekly…). `window` is e.g. "5-hour", "weekly".
    case usageLimit(window: String?, resetsAt: Date?, message: String)
    /// Model doesn't exist, was retired, or isn't on this key's free tier.
    case modelUnavailable(String)
    /// The model is temporarily overloaded (Gemini 503 "high demand"); another model may work right now.
    case overloaded(String)
    /// The request is too big for this model's per-minute token limit; a different model may accept it.
    case tooLargeForModel(String)
    case inputTooLarge
    case blocked(String)
    case badRequest(String)
    case server(Int, String)
    case offline
    case timeout
    case emptyResponse
    case cliNotInstalled(String)
    case cliNotLoggedIn(String)
    case cliFailed(String)
    case allExhausted(String)
    case cancelled

    var errorDescription: String? {
        switch self {
        case .notConfigured(let m): return m
        case .invalidKey(let m): return "API key rejected: \(m)"
        case .rateLimited(_, let daily, _): return daily ? "Daily free limit reached." : "Rate limited — too many requests."
        case .usageLimit(let window, let resetsAt, _):
            let what = (window.map { "\($0.prefix(1).uppercased())\($0.dropFirst()) limit" }) ?? "Usage limit"
            return "\(what) reached." + (resetsAt.map { " Resets \(LimitText.when($0))." } ?? "")
        case .modelUnavailable(let m): return "Model unavailable: \(m)"
        case .overloaded(let m): return "\(ModelCatalog.displayName(m)) is busy right now (high demand). Try again in a moment, or add a fallback model."
        case .tooLargeForModel(let m): return "Text too long for \(m)."
        case .inputTooLarge: return "That text is too long for the model."
        case .blocked(let r): return "The model refused this text (\(r))."
        case .badRequest(let m): return "Request rejected: \(m)"
        case .server(let code, _): return "The provider had a server error (\(code)). Try again shortly."
        case .offline: return "You're offline."
        case .timeout: return "The AI took too long to answer."
        case .emptyResponse: return "The model returned an empty answer."
        case .cliNotInstalled(let name): return "\(name) isn't installed (or couldn't be found)."
        case .cliNotLoggedIn(let name): return "\(name) isn't signed in. Sign in from Terminal first."
        case .cliFailed(let m): return m
        case .allExhausted(let m): return m
        case .cancelled: return "Cancelled."
        }
    }

    var shortKind: String {
        switch self {
        case .notConfigured: return "not_configured"
        case .invalidKey: return "invalid_key"
        case .rateLimited(_, let daily, _): return daily ? "daily_limit" : "rate_limit"
        case .usageLimit: return "usage_limit"
        case .modelUnavailable: return "model_unavailable"
        case .overloaded: return "server"
        case .tooLargeForModel, .inputTooLarge: return "too_large"
        case .blocked: return "blocked"
        case .badRequest: return "bad_request"
        case .server: return "server"
        case .offline: return "offline"
        case .timeout: return "timeout"
        case .emptyResponse: return "empty"
        case .cliNotInstalled, .cliNotLoggedIn, .cliFailed: return "cli"
        case .allExhausted: return "exhausted"
        case .cancelled: return "cancelled"
        }
    }

    /// Transient errors worth one quick retry on the same key + model.
    var isTransient: Bool {
        switch self {
        case .server, .timeout, .overloaded: return true
        default: return false
        }
    }

    /// Errors where trying another key/model cannot help — stop immediately.
    var isFatal: Bool {
        switch self {
        case .inputTooLarge, .blocked, .badRequest, .offline, .cancelled, .notConfigured,
             .cliNotInstalled, .cliNotLoggedIn:
            return true
        default:
            return false
        }
    }

    static func from(_ error: Error) -> AIError {
        if let e = error as? AIError { return e }
        if error is CancellationError { return .cancelled }
        if let u = error as? URLError {
            switch u.code {
            case .timedOut: return .timeout
            case .cancelled: return .cancelled
            case .notConnectedToInternet, .networkConnectionLost, .cannotFindHost, .cannotConnectToHost,
                 .dnsLookupFailed, .dataNotAllowed, .internationalRoamingOff:
                return .offline
            default: return .server(0, u.localizedDescription)
            }
        }
        if error is DecodingError { return .server(0, "Unexpected response format") }
        return .cliFailed(error.localizedDescription)
    }
}

/// Parses durations like "34s", "1.5s", "2m59.56s", "1h2m3s" (Gemini retryDelay / Groq reset headers).
enum DurationParser {
    static func seconds(_ raw: String?) -> TimeInterval? {
        guard var s = raw?.trimmingCharacters(in: .whitespaces), !s.isEmpty else { return nil }
        if let plain = Double(s) { return plain }
        var total: Double = 0
        var matched = false
        for (unit, mult) in [("h", 3600.0), ("ms", 0.001), ("m", 60.0), ("s", 1.0)] {
            // Handle "ms" before "m"/"s" so "500ms" isn't misread.
            while let r = s.range(of: #"[0-9.]+\#(unit)"#, options: .regularExpression) {
                let num = s[r].dropLast(unit.count)
                if let v = Double(num) { total += v * mult; matched = true }
                s.removeSubrange(r)
            }
        }
        return matched ? total : nil
    }
}
