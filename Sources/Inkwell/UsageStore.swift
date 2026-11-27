import Foundation

@MainActor
final class UsageStore: ObservableObject {
    @Published private(set) var events: [UsageEvent] = []

    let folder: URL
    private let fileURL: URL

    init() {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        folder = base.appendingPathComponent("Inkwell", isDirectory: true)
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        fileURL = folder.appendingPathComponent("usage.json")
        load()
    }

    func record(_ event: UsageEvent) {
        events.append(event)
        save()
    }

    func reset() {
        events = []
        save()
    }

    private func load() {
        guard let data = try? Data(contentsOf: fileURL) else { return }
        let dec = JSONDecoder()
        dec.dateDecodingStrategy = .iso8601
        events = ((try? dec.decode([UsageEvent].self, from: data)) ?? []).sorted { $0.date < $1.date }
    }

    private func save() {
        let enc = JSONEncoder()
        enc.dateEncodingStrategy = .iso8601
        if let data = try? enc.encode(events) {
            try? data.write(to: fileURL, options: .atomic)
        }
    }

    func summary(for range: StatsRange, now: Date = .now) -> UsageSummary {
        let cal = Calendar.current
        let today = cal.startOfDay(for: now)
        let start: Date
        if let days = range.days {
            start = cal.date(byAdding: .day, value: -(days - 1), to: today) ?? today
        } else {
            start = cal.startOfDay(for: events.first?.date ?? now)
        }

        let inRange = events.filter { $0.date >= start }
        let ok = inRange.filter(\.success)

        var s = UsageSummary()
        s.runs = ok.count
        s.failures = inRange.count - ok.count
        s.inputTokens = ok.reduce(0) { $0 + $1.inputTokens }
        s.outputTokens = ok.reduce(0) { $0 + $1.outputTokens }
        s.words = ok.reduce(0) { $0 + $1.inputWords }
        s.aiTime = ok.reduce(0) { $0 + $1.latency }

        var runsPerDay: [Date: Int] = [:]
        var tokensPerDay: [Date: Int] = [:]
        for e in ok {
            let d = cal.startOfDay(for: e.date)
            runsPerDay[d, default: 0] += 1
            tokensPerDay[d, default: 0] += e.inputTokens + e.outputTokens
        }
        var buckets: [DayBucket] = []
        var day = start
        while day <= today {
            buckets.append(DayBucket(day: day, runs: runsPerDay[day] ?? 0, tokens: tokensPerDay[day] ?? 0))
            guard let next = cal.date(byAdding: .day, value: 1, to: day) else { break }
            day = next
        }
        s.daily = buckets

        s.topTemplates = Dictionary(grouping: ok, by: \.templateName)
            .map { TemplateCount(name: $0.key, count: $0.value.count) }
            .sorted { $0.count > $1.count }
            .prefix(4)
            .map { $0 }
        return s
    }
}
