import Foundation

struct RawResult {
    var text: String
    var inputTokens: Int
    var outputTokens: Int
    /// Plan-usage reading a CLI reported alongside the reply (Claude Code), if any.
    var limit: CLILimitInfo? = nil
}

struct RemoteModel: Identifiable, Hashable {
    let id: String
    let displayName: String
}

/// One-shot HTTP calls to Gemini and Groq. No retries here — the router decides
/// what to retry, which key to rotate to and which model to fall back to.
enum HTTPProviders {
    static let session: URLSession = {
        let cfg = URLSessionConfiguration.ephemeral
        cfg.timeoutIntervalForRequest = 25
        cfg.timeoutIntervalForResource = 40
        cfg.waitsForConnectivity = false
        return URLSession(configuration: cfg)
    }()

    // MARK: - Completion

    static func complete(kind: ProviderKind, key: String, model: String, system: String, user: String) async throws -> RawResult {
        switch kind {
        case .gemini: return try await gemini(key: key, model: model, system: system, user: user)
        case .groq:
            do {
                return try await groq(key: key, model: model, system: system, user: user, tuned: true)
            } catch AIError.badRequest(let msg) where msg.lowercased().contains("reason") || msg.lowercased().contains("property") {
                // A model that doesn't accept our reasoning tweaks — retry plainly.
                return try await groq(key: key, model: model, system: system, user: user, tuned: false)
            }
        default:
            throw AIError.notConfigured("Not an API provider")
        }
    }

    // MARK: Gemini

    private struct GeminiResponse: Decodable {
        struct Candidate: Decodable {
            struct Content: Decodable {
                struct Part: Decodable { let text: String?; let thought: Bool? }
                let parts: [Part]?
            }
            let content: Content?
            let finishReason: String?
        }
        struct Usage: Decodable {
            let promptTokenCount: Int?
            let candidatesTokenCount: Int?
            let thoughtsTokenCount: Int?
        }
        struct Feedback: Decodable { let blockReason: String? }
        let candidates: [Candidate]?
        let usageMetadata: Usage?
        let promptFeedback: Feedback?
    }

    private static func gemini(key: String, model: String, system: String, user: String) async throws -> RawResult {
        let path = model.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? model
        guard let url = URL(string: "https://generativelanguage.googleapis.com/v1beta/models/\(path):generateContent") else {
            throw AIError.modelUnavailable(model)
        }
        var req = URLRequest(url: url)
        req.httpMethod = "POST"
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        req.setValue(key, forHTTPHeaderField: "x-goog-api-key")
        req.httpBody = try JSONSerialization.data(withJSONObject: [
            "system_instruction": ["parts": [["text": system]]],
            "contents": [["role": "user", "parts": [["text": user]]]],
            "generationConfig": ["temperature": 0.3],
        ] as [String: Any])

        let data = try await send(req, kind: .gemini, model: model)
        let r = try JSONDecoder().decode(GeminiResponse.self, from: data)
        if let reason = r.promptFeedback?.blockReason { throw AIError.blocked(reason.lowercased()) }
        let candidate = r.candidates?.first
        if let fr = candidate?.finishReason, ["SAFETY", "PROHIBITED_CONTENT", "BLOCKLIST", "SPII"].contains(fr) {
            throw AIError.blocked(fr.lowercased())
        }
        let text = (candidate?.content?.parts ?? [])
            .filter { $0.thought != true }
            .compactMap(\.text)
            .joined()
        let u = r.usageMetadata
        return RawResult(text: text,
                         inputTokens: u?.promptTokenCount ?? 0,
                         outputTokens: (u?.candidatesTokenCount ?? 0) + (u?.thoughtsTokenCount ?? 0))
    }

    // MARK: Groq (OpenAI-compatible)

    private struct ChatResponse: Decodable {
        struct Choice: Decodable {
            struct Message: Decodable { let content: String? }
            let message: Message
            let finish_reason: String?
        }
        struct Usage: Decodable { let prompt_tokens: Int?; let completion_tokens: Int? }
        let choices: [Choice]
        let usage: Usage?
    }

    private static func groq(key: String, model: String, system: String, user: String, tuned: Bool) async throws -> RawResult {
        var req = URLRequest(url: URL(string: "https://api.groq.com/openai/v1/chat/completions")!)
        req.httpMethod = "POST"
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        req.setValue("Bearer \(key)", forHTTPHeaderField: "Authorization")
        var body: [String: Any] = [
            "model": model,
            "temperature": 0.3,
            "messages": [["role": "system", "content": system], ["role": "user", "content": user]],
        ]
        if tuned {
            // Reasoning models: keep thinking short and out of the reply.
            if model.contains("gpt-oss") {
                body["reasoning_effort"] = "low"
                body["include_reasoning"] = false
            } else if model.contains("qwen") {
                body["reasoning_format"] = "hidden"
            }
        }
        req.httpBody = try JSONSerialization.data(withJSONObject: body)

        let data = try await send(req, kind: .groq, model: model)
        let r = try JSONDecoder().decode(ChatResponse.self, from: data)
        if r.choices.first?.finish_reason == "content_filter" { throw AIError.blocked("content filter") }
        return RawResult(text: r.choices.first?.message.content ?? "",
                         inputTokens: r.usage?.prompt_tokens ?? 0,
                         outputTokens: r.usage?.completion_tokens ?? 0)
    }

    // MARK: - Model lists (also used to validate a key)

    static func listModels(kind: ProviderKind, key: String) async throws -> [RemoteModel] {
        switch kind {
        case .gemini:
            var req = URLRequest(url: URL(string: "https://generativelanguage.googleapis.com/v1beta/models?pageSize=1000")!)
            req.setValue(key, forHTTPHeaderField: "x-goog-api-key")
            let data = try await send(req, kind: .gemini, model: "")
            struct List: Decodable {
                struct M: Decodable { let name: String; let displayName: String?; let supportedGenerationMethods: [String]? }
                let models: [M]?
            }
            let list = try JSONDecoder().decode(List.self, from: data)
            return (list.models ?? [])
                .filter { $0.supportedGenerationMethods?.contains("generateContent") ?? true }
                .map { RemoteModel(id: $0.name.replacingOccurrences(of: "models/", with: ""), displayName: $0.displayName ?? $0.name) }
                .filter { ModelCatalog.isChatModel($0.id, kind: .gemini) }
        case .groq:
            var req = URLRequest(url: URL(string: "https://api.groq.com/openai/v1/models")!)
            req.setValue("Bearer \(key)", forHTTPHeaderField: "Authorization")
            let data = try await send(req, kind: .groq, model: "")
            struct List: Decodable {
                struct M: Decodable { let id: String; let active: Bool? }
                let data: [M]
            }
            let list = try JSONDecoder().decode(List.self, from: data)
            return list.data
                .filter { $0.active ?? true }
                .map { RemoteModel(id: $0.id, displayName: $0.id) }
                .filter { ModelCatalog.isChatModel($0.id, kind: .groq) }
        default:
            return []
        }
    }

    // MARK: - Transport + error classification

    private static func send(_ req: URLRequest, kind: ProviderKind, model: String) async throws -> Data {
        let result: (Data, URLResponse)
        do {
            result = try await session.data(for: req)
        } catch {
            throw AIError.from(error)
        }
        let (data, response) = result
        guard let http = response as? HTTPURLResponse else { throw AIError.server(0, "No HTTP response") }
        guard (200..<300).contains(http.statusCode) else {
            throw classify(status: http.statusCode, data: data, headers: http, kind: kind, model: model)
        }
        return data
    }

    /// Reads both error shapes: `{"error":{"code":429,"status":"RESOURCE_EXHAUSTED","message":…,"details":[…]}}`
    /// (Gemini) and `{"error":{"message":…,"type":…,"code":"rate_limit_exceeded"}}` (Groq / OpenAI style).
    private struct ErrorInfo {
        var message = ""
        var code = ""
        var status = ""
        var retryDelay: TimeInterval?
        var quotaIDs: [String] = []
    }

    private static func parseError(_ data: Data) -> ErrorInfo {
        var info = ErrorInfo()
        guard let obj = try? JSONSerialization.jsonObject(with: data) else {
            info.message = String(data: data.prefix(300), encoding: .utf8) ?? ""
            return info
        }
        let root = (obj as? [[String: Any]])?.first ?? (obj as? [String: Any]) ?? [:]
        let err = root["error"] as? [String: Any] ?? root
        info.message = err["message"] as? String ?? ""
        if let c = err["code"] { info.code = "\(c)" }
        info.status = err["status"] as? String ?? (err["type"] as? String ?? "")
        for detail in err["details"] as? [[String: Any]] ?? [] {
            if let d = detail["retryDelay"] as? String { info.retryDelay = DurationParser.seconds(d) }
            for v in detail["violations"] as? [[String: Any]] ?? [] {
                if let q = v["quotaId"] as? String { info.quotaIDs.append(q) }
                if let q = v["quotaMetric"] as? String { info.quotaIDs.append(q) }
            }
        }
        return info
    }

    private static func classify(status: Int, data: Data, headers: HTTPURLResponse, kind: ProviderKind, model: String) -> AIError {
        let e = parseError(data)
        let msg = e.message.isEmpty ? HTTPURLResponse.localizedString(forStatusCode: status) : e.message
        let lower = (msg + " " + e.code + " " + e.status).lowercased()
        let headerRetry = DurationParser.seconds(headers.value(forHTTPHeaderField: "retry-after"))

        switch status {
        case 401:
            return .invalidKey(msg)
        case 403:
            // Gemini 403s cover leaked/blocked keys, billing holds and "free tier not available here".
            return .invalidKey(msg)
        case 404:
            return .modelUnavailable(model)
        case 413:
            return kind == .groq ? .tooLargeForModel(model) : .inputTooLarge
        case 429:
            // "limit: 0" = this model has no free quota for this key/project at all.
            if lower.contains("limit: 0") || lower.contains("limit 0") { return .modelUnavailable("\(model) (no free quota)") }
            if kind == .groq && lower.contains("request too large") { return .tooLargeForModel(model) }
            let daily = e.quotaIDs.contains { $0.lowercased().contains("perday") }
                || lower.contains("per day") || lower.contains("(rpd)") || lower.contains("(tpd)")
                || lower.contains("quota_exceeded") || lower.contains("daily")
            // Gemini's daily quota resets at midnight Pacific; its retryDelay can say "1s" even then, so ignore it.
            if daily && kind == .gemini { return .rateLimited(retryAfter: nil, daily: true, message: msg) }
            // Groq says "Please try again in 6m50.832s" in the message; the retry-after header wins when present.
            var messageDelay: TimeInterval?
            if let r = msg.range(of: #"(?:try again in|retry in)\s+[0-9hms.]+"#, options: [.regularExpression, .caseInsensitive]) {
                messageDelay = DurationParser.seconds(String(msg[r]).components(separatedBy: " ").last)
            }
            return .rateLimited(retryAfter: headerRetry ?? messageDelay ?? e.retryDelay, daily: daily, message: msg)
        case 400, 422:
            if lower.contains("api key not valid") || lower.contains("api_key_invalid") || lower.contains("invalid api key") {
                return .invalidKey(msg)
            }
            if lower.contains("decommissioned") || lower.contains("model_not_found")
                || (lower.contains("model") && (lower.contains("not found") || lower.contains("not supported") || lower.contains("does not exist"))) {
                return .modelUnavailable(model)
            }
            if lower.contains("context") || lower.contains("too long") || lower.contains("too large") || lower.contains("exceeds") {
                return .inputTooLarge
            }
            return .badRequest(msg)
        case 408, 504:
            return .timeout
        case 503 where lower.contains("high demand") || lower.contains("overloaded") || lower.contains("unavailable"):
            return .overloaded(model)
        case 498, 500, 502, 503:
            // 498 = Groq flex capacity exceeded; 503 = overloaded.
            return .server(status, msg)
        default:
            return status >= 500 ? .server(status, msg) : .badRequest(msg)
        }
    }
}
