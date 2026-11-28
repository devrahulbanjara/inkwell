import Foundation

/// Latest plan-usage reading a CLI reported (Claude Code's `rate_limit_event`).
struct CLILimitInfo: Equatable {
    var window: String          // "5-hour", "weekly", "weekly Opus", "extra usage", "usage"
    var utilization: Double?    // 0…1
    var resetsAt: Date?
    var rejected: Bool
}

/// Friendly reset times: "3:15 PM" today, "tomorrow 9:00 AM", "Mon 9:00 AM" this week, "Mar 20, 9:00 AM" later.
enum LimitText {
    static func when(_ date: Date) -> String {
        let cal = Calendar.current
        let time = date.formatted(date: .omitted, time: .shortened)
        if cal.isDateInToday(date) { return time }
        if cal.isDateInTomorrow(date) { return "tomorrow \(time)" }
        if date.timeIntervalSinceNow < 6 * 86_400 {
            return date.formatted(.dateTime.weekday(.abbreviated)) + " " + time
        }
        return date.formatted(.dateTime.month(.abbreviated).day()) + ", " + time
    }
}

/// Reads "which limit, and when does it reset" out of what Claude Code and Codex print when a plan limit
/// (5-hour session, weekly, Opus weekly, extra usage…) is reached. Formats researched Nov 2026:
/// - Claude Code: `rate_limit_event` JSON (`rateLimitType`, `resetsAt` in epoch seconds), or text like
///   "You've hit your weekly limit · resets 11am (UTC)", "5-hour limit reached ∙ resets 2am",
///   "Claude AI usage limit reached|1755615600".
/// - Codex: "You've hit your usage limit. … try again at 3:15 PM." or "… try again at Mar 20th, 2027 3:36 PM."
enum LimitParser {
    static func looksLikeLimit(_ text: String) -> Bool {
        let l = normalize(text).lowercased()
        let needles = ["usage limit", "rate limit", "limit reached", "hit your limit", "hit your weekly", "hit your session",
                       "too many requests", "quota", "429", "usage_limit_reached"]
        if needles.contains(where: { l.contains($0) }) { return true }
        return l.range(of: #"\b(5-hour|weekly|session|opus|sonnet) limit\b"#, options: .regularExpression) != nil
    }

    static func claudeWindow(_ type: String?) -> String {
        switch type ?? "" {
        case "five_hour": return "5-hour"
        case "seven_day": return "weekly"
        case "seven_day_opus": return "weekly Opus"
        case "seven_day_sonnet": return "weekly Sonnet"
        case "overage": return "extra usage"
        case "": return "usage"
        case let t where t.hasPrefix("seven_day"): return "weekly"
        case let t: return t.replacingOccurrences(of: "_", with: " ")
        }
    }

    /// Epoch seconds (current CLI) or milliseconds (some wrappers).
    static func fromEpoch(_ value: Double) -> Date? {
        guard value > 0 else { return nil }
        return Date(timeIntervalSince1970: value > 1e12 ? value / 1000 : value)
    }

    static func parseClaude(_ text: String) -> (window: String?, resetsAt: Date?) {
        let t = normalize(text)
        let window = windowFromText(t)
        if let m = firstMatch(#"\|(\d{10,13})\s*$"#, in: t), let v = Double(m[1]) {
            return (window, fromEpoch(v))
        }
        if let m = firstMatch(#"resets?\s+(?:at\s+|on\s+)?([^()\n·∙.]+?)\s*(?:\(([^)]+)\))?\s*(?:[.·∙\n]|$)"#, in: t) {
            return (window, parseWhen(m[1], timeZone: m[2].isEmpty ? nil : m[2]))
        }
        return (window, nil)
    }

    static func parseCodex(_ text: String) -> (window: String?, resetsAt: Date?) {
        let t = normalize(text)
        var reset: Date?
        if let m = firstMatch(#"try again (?:at|on)\s+(.+?)\s*(?:\.\s*$|\.\s|$)"#, in: t) {
            reset = parseWhen(m[1], timeZone: nil)
        } else if let m = firstMatch(#"try again in\s+([0-9a-z ,]+)"#, in: t), let span = parseRelative(m[1]) {
            reset = Date().addingTimeInterval(span)
        }
        var window = windowFromText(t)
        if window == nil, let reset {
            window = reset.timeIntervalSinceNow <= 5.2 * 3600 ? "5-hour" : "weekly"
        }
        return (window, reset)
    }

    private static func windowFromText(_ t: String) -> String? {
        let l = t.lowercased()
        if l.contains("weekly") || l.contains("week") {
            return l.contains("opus") ? "weekly Opus" : (l.contains("sonnet") ? "weekly Sonnet" : "weekly")
        }
        if l.contains("5-hour") || l.contains("5 hour") || l.contains("five_hour") || l.contains("session") { return "5-hour" }
        if l.contains("monthly") || l.contains("month") { return "monthly" }
        if l.contains("daily") || l.contains("per day") || l.contains("(rpd)") || l.contains("(tpd)") { return "daily" }
        return nil
    }

    /// "3:15 PM", "11am", "Mon 9am", "Mar 20th, 2027 3:36 PM", optionally in an IANA time zone ("UTC", "Europe/Berlin").
    static func parseWhen(_ raw: String, timeZone: String?) -> Date? {
        let s = raw.trimmingCharacters(in: CharacterSet(charactersIn: " .,"))
        let pattern = #"^(?:(mon|tue|wed|thu|fri|sat|sun)[a-z]*,?\s+)?(?:(jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\.?\s+(\d{1,2})(?:st|nd|rd|th)?,?\s+(?:(\d{4}),?\s+)?)?(?:at\s+)?(\d{1,2})(?::(\d{2}))?\s*(am|pm|a\.m\.|p\.m\.)?$"#
        guard let m = firstMatch(pattern, in: s) else { return nil }

        var cal = Calendar(identifier: .gregorian)
        cal.timeZone = zone(timeZone)
        let now = Date()
        guard var hour = Int(m[5]) else { return nil }
        let minute = Int(m[6]) ?? 0
        if !m[7].isEmpty {
            let pm = m[7].lowercased().hasPrefix("p")
            if hour == 12 { hour = pm ? 12 : 0 } else if pm { hour += 12 }
        }
        guard hour < 24, minute < 60 else { return nil }

        if !m[2].isEmpty {
            let months = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"]
            guard let month = months.firstIndex(where: { m[2].lowercased().hasPrefix($0) }), let day = Int(m[3]) else { return nil }
            var comps = DateComponents()
            comps.year = Int(m[4]) ?? cal.component(.year, from: now)
            comps.month = month + 1
            comps.day = day
            comps.hour = hour
            comps.minute = minute
            guard var date = cal.date(from: comps) else { return nil }
            if m[4].isEmpty, date < now.addingTimeInterval(-86_400), let next = cal.date(byAdding: .year, value: 1, to: date) { date = next }
            return date
        }

        guard var date = cal.date(bySettingHour: hour, minute: minute, second: 0, of: now) else { return nil }
        if !m[1].isEmpty {
            let days = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"]
            if let want = days.firstIndex(where: { m[1].lowercased().hasPrefix($0) }) {
                let have = cal.component(.weekday, from: now) - 1
                let add = (want - have + 7) % 7
                date = cal.date(byAdding: .day, value: add, to: date) ?? date
                if date <= now { date = cal.date(byAdding: .day, value: 7, to: date) ?? date }
            }
        } else if date <= now {
            date = cal.date(byAdding: .day, value: 1, to: date) ?? date
        }
        return date
    }

    private static func zone(_ name: String?) -> TimeZone {
        guard let n = name?.trimmingCharacters(in: .whitespaces), !n.isEmpty else { return .current }
        if n.uppercased() == "UTC" || n.uppercased() == "GMT" { return TimeZone(identifier: "UTC") ?? .current }
        return TimeZone(identifier: n) ?? TimeZone(abbreviation: n) ?? .current
    }

    /// "2 days 3 hours 4 minutes" → seconds.
    private static func parseRelative(_ s: String) -> TimeInterval? {
        guard let re = try? NSRegularExpression(pattern: #"(\d+)\s*(day|hour|hr|minute|min|second|sec)s?"#, options: [.caseInsensitive]) else { return nil }
        var total = 0.0
        var any = false
        for match in re.matches(in: s, range: NSRange(s.startIndex..., in: s)) {
            guard let vr = Range(match.range(at: 1), in: s), let ur = Range(match.range(at: 2), in: s), let v = Double(s[vr]) else { continue }
            let u = s[ur].lowercased()
            total += u.hasPrefix("day") ? v * 86_400 : u.hasPrefix("h") ? v * 3600 : u.hasPrefix("min") ? v * 60 : v
            any = true
        }
        return any ? total : nil
    }

    private static func normalize(_ s: String) -> String {
        s.replacingOccurrences(of: "\u{2019}", with: "'").replacingOccurrences(of: "\u{2018}", with: "'")
    }

    /// Capture groups of the first match (index 0 = whole match); missing groups are "".
    private static func firstMatch(_ pattern: String, in text: String) -> [String]? {
        guard let re = try? NSRegularExpression(pattern: pattern, options: [.caseInsensitive, .anchorsMatchLines]),
              let m = re.firstMatch(in: text, range: NSRange(text.startIndex..., in: text)) else { return nil }
        return (0..<m.numberOfRanges).map { i in
            Range(m.range(at: i), in: text).map { String(text[$0]) } ?? ""
        }
    }
}

/// Turns a template name into what's happening right now: "Fix grammar" → "Fixing grammar",
/// "Make formal" → "Making it formal", "To English" → "Translating to English", "Summarize" → "Summarizing".
enum WorkingLabels {
    private static let known: [String: String] = [
        "fix grammar": "Fixing grammar", "rewrite": "Rewriting", "make formal": "Making it formal",
        "make shorter": "Making it shorter", "ask": "Thinking", "reply": "Writing a reply", "to english": "Translating to English",
    ]
    private static let irregular: [String: String] = [
        "be": "being", "see": "seeing", "run": "running", "get": "getting", "put": "putting", "set": "setting",
        "cut": "cutting", "plan": "planning", "stop": "stopping", "trim": "trimming", "swap": "swapping",
        "map": "mapping", "tag": "tagging", "chat": "chatting", "jot": "jotting",
    ]
    private static let notVerbs: Set<String> = ["to", "in", "into", "as", "more", "less", "the", "a", "an", "my", "new", "untitled"]

    static func label(for name: String) -> String {
        let n = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !n.isEmpty else { return "Working" }
        if let k = known[n.lowercased()] { return k }
        let words = n.split(separator: " ").map(String.init)
        let first = words[0]
        let rest = words.dropFirst().joined(separator: " ")
        if first.lowercased() == "to", words.count > 1 { return "Translating to \(rest)" }
        if first.lowercased() == "make", words.count > 1 { return "Making it \(rest.lowercased())" }
        if notVerbs.contains(first.lowercased()) || first.lowercased().hasSuffix("ing") || !first.allSatisfy(\.isLetter) { return n }
        let g = gerund(first.lowercased())
        let capitalized = String(g.prefix(1)).uppercased() + String(g.dropFirst())
        return rest.isEmpty ? capitalized : "\(capitalized) \(rest)"
    }

    private static func gerund(_ v: String) -> String {
        if let g = irregular[v] { return g }
        if v.hasSuffix("ie") { return String(v.dropLast(2)) + "ying" }
        if v.hasSuffix("ee") || v.hasSuffix("ye") || v.hasSuffix("oe") { return v + "ing" }
        if v.hasSuffix("e"), v.count > 2 { return String(v.dropLast()) + "ing" }
        return v + "ing"
    }
}
