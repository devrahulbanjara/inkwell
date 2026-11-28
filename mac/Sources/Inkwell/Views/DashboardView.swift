import AppKit
import Charts
import SwiftUI

struct DashboardView: View {
    @EnvironmentObject private var app: AppState
    @EnvironmentObject private var settings: SettingsStore
    @EnvironmentObject private var usage: UsageStore

    @StateObject private var rangeState = ViewState(StatsRange.week)
    @StateObject private var metricState = ViewState(ChartMetric.tokens)

    private var range: StatsRange { rangeState.value }
    private var metric: ChartMetric { metricState.value }

    enum ChartMetric: String, CaseIterable, Identifiable {
        case tokens = "Tokens", runs = "Runs"
        var id: String { rawValue }
    }

    var body: some View {
        ZStack(alignment: .top) {
            if case .provider(let kind, let addKey) = app.popoverPage {
                QuickProviderView(initial: kind, addKey: addKey) {
                    withAnimation(.smooth(duration: 0.35)) { app.popoverPage = .dashboard }
                }
                .id(app.popoverPage)
                .transition(.move(edge: .trailing).combined(with: .opacity))
            } else {
                dashboard
                    .transition(.move(edge: .leading).combined(with: .opacity))
            }
        }
        .padding(16)
        .frame(width: 340)
        .clipped()
    }

    private var dashboard: some View {
        let s = usage.summary(for: range)

        return VStack(alignment: .leading, spacing: 14) {
            header
            providerCard

            if !app.hasAccessibility {
                permissionBanner.transition(.move(edge: .top).combined(with: .opacity))
            }
            if let error = app.lastError {
                errorBanner(error).transition(.move(edge: .top).combined(with: .opacity))
            }

            Picker("Range", selection: $rangeState.value) {
                ForEach(StatsRange.allCases) { Text($0.rawValue).tag($0) }
            }
            .pickerStyle(.segmented)
            .labelsHidden()

            GlassGroup(spacing: 10) {
                LazyVGrid(columns: [GridItem(.flexible(), spacing: 10), GridItem(.flexible(), spacing: 10)], spacing: 10) {
                    StatCard(title: "Runs", value: "\(s.runs)", symbol: "bolt.fill", tint: .orange)
                    StatCard(title: "Tokens", value: s.totalTokens.compact, symbol: "number", tint: .purple,
                             detail: "\(s.inputTokens.compact) in · \(s.outputTokens.compact) out")
                    StatCard(title: "Words processed", value: s.words.compact, symbol: "text.alignleft", tint: .blue)
                    StatCard(title: "AI time", value: s.aiTime.short, symbol: "timer", tint: .green,
                             detail: s.runs > 0 ? String(format: "%.1fs avg", s.averageLatency) : nil)
                }
            }

            chart(s)

            if !s.topTemplates.isEmpty { topTemplates(s) }

            Divider()
            footer
        }
        .animation(.smooth(duration: 0.35), value: range)
        .animation(.smooth(duration: 0.35), value: metric)
        .animation(.spring(response: 0.4, dampingFraction: 0.82), value: app.lastError)
        .animation(.spring(response: 0.4, dampingFraction: 0.82), value: app.hasAccessibility)
        .animation(.smooth, value: usage.events.count)
    }

    // MARK: Sections

    private var header: some View {
        HStack(spacing: 10) {
            ZStack {
                RoundedRectangle(cornerRadius: 8, style: .continuous)
                    .fill(LinearGradient(colors: [.indigo, .purple], startPoint: .topLeading, endPoint: .bottomTrailing))
                Image(systemName: app.isWorking ? "sparkles" : "pencil.and.scribble")
                    .font(.system(size: 14, weight: .semibold))
                    .foregroundStyle(.white)
                    .contentTransition(.symbolEffect(.replace))
                    .symbolEffect(.pulse, isActive: app.isWorking)
            }
            .frame(width: 30, height: 30)

            VStack(alignment: .leading, spacing: 1) {
                Text("Inkwell").font(.headline)
                Text(statusText)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .contentTransition(.opacity)
                    .animation(.smooth, value: statusText)
            }
            Spacer()
            Toggle("", isOn: $settings.prefs.isEnabled)
                .toggleStyle(.switch)
                .controlSize(.small)
                .labelsHidden()
                .help("Turn triggers on or off")
        }
    }

    // MARK: Active provider (tap for keys/models, menu for a quick model switch)

    private var providerCard: some View {
        let kind = settings.prefs.activeProvider
        return HStack(spacing: 10) {
            ProviderLogo(kind: kind, size: 30)
            VStack(alignment: .leading, spacing: 1) {
                Text(kind.displayName).font(.callout.weight(.semibold))
                Text(providerDetail(kind))
                    .font(.caption2.monospaced())
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .truncationMode(.middle)
            }
            Spacer(minLength: 4)
            if let menu = modelMenu(kind) {
                menu
            }
            Image(systemName: "chevron.right")
                .font(.caption.weight(.semibold))
                .foregroundStyle(.tertiary)
        }
        .padding(.horizontal, 12).padding(.vertical, 9)
        .glassCard(cornerRadius: 14, tint: kind.tint, interactive: true)
        .contentShape(Rectangle())
        .onTapGesture {
            withAnimation(.smooth(duration: 0.35)) { app.popoverPage = .provider(kind, addKey: false) }
        }
        .help("Change provider, API keys and models")
        .animation(.smooth, value: kind)
    }

    private func providerDetail(_ kind: ProviderKind) -> String {
        if kind.usesAPIKeys {
            let keys = settings.api(kind).keys.filter(\.isEnabled).count
            guard keys > 0 else { return "No API key — tap to add one" }
            return "\(settings.effectiveModels(kind).first ?? "") · \(keys) key\(keys == 1 ? "" : "s")"
        }
        let m = settings.cli(kind).model
        return m.isEmpty ? "Default model" : m
    }

    /// Quick primary-model switch. Codex takes free-form model IDs, so it has no menu.
    private func modelMenu(_ kind: ProviderKind) -> AnyView? {
        let options: [ModelInfo]
        let current: String
        switch kind {
        case .gemini, .groq:
            guard settings.hasUsableKey(kind) else { return nil }
            options = ModelCatalog.models(for: kind)
            current = settings.effectiveModels(kind).first ?? ""
        case .claudeCode:
            options = ModelCatalog.claude
            current = settings.cli(kind).model
        case .codex:
            return nil
        }
        return AnyView(
            Menu {
                Section("Primary model") {
                    ForEach(options) { m in
                        Button {
                            withAnimation(.smooth) { settings.setPrimaryModel(kind, m.id) }
                        } label: {
                            if m.id == current { Label(m.name, systemImage: "checkmark") } else { Text(m.name) }
                        }
                    }
                }
            } label: {
                Image(systemName: "cpu")
            }
            .menuStyle(.borderlessButton)
            .menuIndicator(.hidden)
            .fixedSize()
            .help("Switch model")
        )
    }

    private var statusText: String {
        if app.isWorking { return "Working…" }
        if !app.hasAccessibility { return "Needs Accessibility permission" }
        if !settings.prefs.isEnabled { return "Paused" }
        return "Listening · \(settings.activeSummary)"
    }

    private var permissionBanner: some View {
        HStack(alignment: .top, spacing: 8) {
            Image(systemName: "hand.raised.fill").foregroundStyle(.orange)
            VStack(alignment: .leading, spacing: 6) {
                Text("Allow Inkwell in Accessibility so it can read and replace text.")
                    .font(.caption)
                Button("Open Privacy Settings") { app.openAccessibilitySettings() }
                    .controlSize(.small)
                    .glassButton(prominent: true)
            }
        }
        .padding(12)
        .frame(maxWidth: .infinity, alignment: .leading)
        .glassCard(cornerRadius: 14, tint: .orange)
    }

    private func errorBanner(_ message: String) -> some View {
        HStack(spacing: 8) {
            Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.red)
            Text(message).font(.caption).lineLimit(3)
            Spacer(minLength: 0)
            Button { app.lastError = nil } label: { Image(systemName: "xmark") }
                .buttonStyle(.borderless)
                .font(.caption)
        }
        .padding(12)
        .glassCard(cornerRadius: 14, tint: .red)
    }

    private func chart(_ s: UsageSummary) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                Text("Activity").font(.subheadline.weight(.semibold))
                Spacer()
                Picker("Metric", selection: $metricState.value) {
                    ForEach(ChartMetric.allCases) { Text($0.rawValue).tag($0) }
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .controlSize(.small)
                .frame(width: 120)
            }

            Chart(s.daily) { b in
                BarMark(
                    x: .value("Day", b.day, unit: .day),
                    y: .value(metric.rawValue, metric == .tokens ? b.tokens : b.runs)
                )
                .foregroundStyle(LinearGradient(colors: [.purple, .indigo], startPoint: .top, endPoint: .bottom))
                .cornerRadius(3)
            }
            .chartXAxis {
                AxisMarks(values: .automatic(desiredCount: range == .week ? 7 : 4)) { _ in
                    AxisValueLabel(format: axisFormat)
                }
            }
            .chartYAxis {
                AxisMarks(position: .leading, values: .automatic(desiredCount: 3)) { _ in
                    AxisGridLine()
                    AxisValueLabel()
                }
            }
            .frame(height: 110)
            .overlay {
                if s.runs == 0 {
                    Text("No activity yet — type ?fix after any text")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .padding(6)
                        .glassCapsule()
                        .transition(.opacity)
                }
            }
        }
        .padding(12)
        .glassCard(cornerRadius: 16)
    }

    private var axisFormat: Date.FormatStyle {
        range == .week
            ? Date.FormatStyle.dateTime.weekday(.abbreviated)
            : Date.FormatStyle.dateTime.day().month(.abbreviated)
    }

    private func topTemplates(_ s: UsageSummary) -> some View {
        let maxCount = max(s.topTemplates.first?.count ?? 1, 1)
        return VStack(alignment: .leading, spacing: 6) {
            Text("Top templates").font(.subheadline.weight(.semibold))
            ForEach(s.topTemplates) { t in
                HStack(spacing: 8) {
                    Text(t.name).font(.caption).frame(width: 96, alignment: .leading).lineLimit(1)
                    GeometryReader { geo in
                        Capsule()
                            .fill(LinearGradient(colors: [.indigo, .purple], startPoint: .leading, endPoint: .trailing))
                            .frame(width: max(4, geo.size.width * CGFloat(t.count) / CGFloat(maxCount)))
                    }
                    .frame(height: 6)
                    Text("\(t.count)").font(.caption.monospacedDigit()).foregroundStyle(.secondary)
                        .frame(width: 32, alignment: .trailing)
                }
            }
        }
        .padding(12)
        .glassCard(cornerRadius: 16)
    }

    private var footer: some View {
        HStack {
            Button { SettingsWindow.shared.show(app) } label: {
                Label("Settings", systemImage: "gearshape")
            }
            .glassButton()
            Spacer()
            Button("Quit") { NSApp.terminate(nil) }
                .keyboardShortcut("q")
                .glassButton()
        }
        .font(.callout)
    }
}

struct StatCard: View {
    let title: String
    let value: String
    let symbol: String
    let tint: Color
    var detail: String? = nil

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack(spacing: 5) {
                Image(systemName: symbol).font(.caption2.weight(.bold)).foregroundStyle(tint)
                Text(title).font(.caption).foregroundStyle(.secondary)
            }
            Text(value)
                .font(.system(.title2, design: .rounded).weight(.semibold))
                .monospacedDigit()
                .contentTransition(.numericText())
            if let detail {
                Text(detail).font(.caption2).foregroundStyle(.tertiary)
            }
        }
        .frame(maxWidth: .infinity, minHeight: 64, alignment: .leading)
        .padding(12)
        .glassCard(cornerRadius: 16, tint: tint, interactive: true)
    }
}
