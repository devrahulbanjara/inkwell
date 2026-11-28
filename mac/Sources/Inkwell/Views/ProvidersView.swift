import AppKit
import SwiftUI

// MARK: - Screen state

@MainActor
final class ProvidersUI: ObservableObject {
    @Published var inspected: ProviderKind = .gemini
    @Published var didSync = false
    @Published var showAddKey = false
    @Published var showModels = false
    @Published var confirmDelete: APIKeyMeta?
    @Published var testing = false
    @Published var testResult: String?
    @Published var testOK = false
    @Published var notice: String?
}

// MARK: - AI Provider tab

struct ProvidersTab: View {
    @EnvironmentObject private var app: AppState
    @EnvironmentObject private var settings: SettingsStore
    @EnvironmentObject private var cli: CLIStatusStore
    @EnvironmentObject private var quota: QuotaTracker
    @StateObject private var ui = ProvidersUI()

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 18) {
                Text("Choose where Inkwell sends your text. Gemini and Groq are free with API keys; Claude Code and Codex use the plan you're already signed into.")
                    .font(.callout)
                    .foregroundStyle(.secondary)

                GlassGroup(spacing: 12) {
                    LazyVGrid(columns: [GridItem(.flexible(), spacing: 12), GridItem(.flexible(), spacing: 12)], spacing: 12) {
                        ForEach(ProviderKind.allCases) { kind in
                            ProviderCard(kind: kind,
                                         isActive: settings.prefs.activeProvider == kind,
                                         isSelected: ui.inspected == kind,
                                         status: statusLine(kind))
                                .onTapGesture {
                                    withAnimation(.smooth(duration: 0.3)) {
                                        ui.inspected = kind
                                        ui.testResult = nil
                                        ui.notice = nil
                                    }
                                }
                        }
                    }
                }

                detailHeader

                Group {
                    if ui.inspected.usesAPIKeys {
                        APIProviderDetail(kind: ui.inspected, ui: ui)
                    } else {
                        CLIProviderDetail(kind: ui.inspected, ui: ui)
                    }
                }
                .id(ui.inspected)
                .transition(.opacity.combined(with: .move(edge: .bottom)))

                testRow
            }
            .padding(22)
            .animation(.smooth(duration: 0.3), value: ui.inspected)
        }
        .onAppear {
            if !ui.didSync { ui.inspected = settings.prefs.activeProvider; ui.didSync = true }
        }
        .task { await cli.refreshAll(settings) }
        .sheet(isPresented: $ui.showAddKey) {
            AddKeySheet(kind: ui.inspected, onClose: { ui.showAddKey = false })
                .environmentObject(settings)
                .environmentObject(quota)
        }
        .sheet(isPresented: $ui.showModels) {
            ModelsSheet(kind: ui.inspected, onClose: { ui.showModels = false })
                .environmentObject(settings)
        }
    }

    private func statusLine(_ kind: ProviderKind) -> String {
        if kind.usesAPIKeys {
            let keys = settings.api(kind).keys.filter(\.isEnabled).count
            guard keys > 0 else { return "No keys yet" }
            let model = settings.api(kind).models.first ?? "—"
            return "\(keys) key\(keys == 1 ? "" : "s") · \(model)"
        }
        switch cli.status(kind).state {
        case .checking: return "Checking…"
        case .notInstalled: return "Not installed"
        case .loggedOut: return "Not signed in"
        case .ready:
            let m = settings.cli(kind).model
            return "Signed in · \(m.isEmpty ? "default model" : m)"
        }
    }

    // MARK: Header with "Make active"

    private var detailHeader: some View {
        let kind = ui.inspected
        let isActive = settings.prefs.activeProvider == kind
        return HStack(alignment: .center, spacing: 10) {
            Text(kind.displayName).font(.title3.weight(.semibold))
            if isActive {
                Label("Active", systemImage: "checkmark.seal.fill")
                    .font(.caption.weight(.semibold))
                    .foregroundStyle(.green)
                    .padding(.horizontal, 8).padding(.vertical, 3)
                    .glassCapsule(tint: .green)
            }
            Spacer()
            if let notice = ui.notice {
                Text(notice).font(.caption).foregroundStyle(.orange).lineLimit(2)
                    .transition(.opacity)
            }
            if !isActive {
                Button("Use \(kind.displayName)") { activate(kind) }
                    .glassButton(prominent: true)
            }
        }
    }

    private func activate(_ kind: ProviderKind) {
        ui.notice = nil
        Task {
            let notice = await ProviderActions.activate(kind, settings: settings, cli: cli)
            withAnimation { ui.notice = notice }
        }
    }

    // MARK: Test

    private var testRow: some View {
        HStack(spacing: 10) {
            Button {
                runTest()
            } label: {
                Label(ui.testing ? "Testing…" : "Send a test request", systemImage: "paperplane")
            }
            .glassButton()
            .disabled(ui.testing)
            if ui.testing { ProgressView().controlSize(.small) }
            if let r = ui.testResult {
                Image(systemName: ui.testOK ? "checkmark.circle.fill" : "xmark.octagon.fill")
                    .foregroundStyle(ui.testOK ? Color.green : Color.red)
                Text(r).font(.caption).foregroundStyle(.secondary).lineLimit(3).textSelection(.enabled)
            }
            Spacer()
        }
        .animation(.smooth, value: ui.testResult)
    }

    private func runTest() {
        let kind = ui.inspected
        ui.testing = true
        ui.testResult = nil
        Task {
            let r = await ProviderActions.test(kind, app: app)
            ui.testOK = r.ok
            ui.testResult = r.message
            ui.testing = false
        }
    }
}

// MARK: - Provider card

private struct ProviderCard: View {
    let kind: ProviderKind
    let isActive: Bool
    let isSelected: Bool
    let status: String

    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            ProviderLogo(kind: kind, size: 34)

            VStack(alignment: .leading, spacing: 3) {
                HStack(spacing: 6) {
                    Text(kind.displayName).font(.headline)
                    if isActive {
                        Image(systemName: "checkmark.circle.fill").foregroundStyle(.green).font(.caption)
                            .transition(.scale.combined(with: .opacity))
                    }
                }
                Text(kind.subtitle).font(.caption).foregroundStyle(.secondary)
                Text(status).font(.caption2.monospaced()).foregroundStyle(.tertiary).lineLimit(1)
                    .contentTransition(.opacity)
            }
            Spacer(minLength: 0)
        }
        .padding(14)
        .frame(maxWidth: .infinity, alignment: .leading)
        .glassCard(cornerRadius: 18, tint: isActive ? kind.tint : nil, interactive: true)
        .overlay(
            RoundedRectangle(cornerRadius: 18, style: .continuous)
                .strokeBorder(isSelected ? kind.tint.opacity(0.9) : .clear, lineWidth: 2)
        )
        .scaleEffect(isSelected ? 1.0 : 0.985)
        .contentShape(RoundedRectangle(cornerRadius: 18, style: .continuous))
        .animation(.spring(response: 0.35, dampingFraction: 0.8), value: isSelected)
        .animation(.spring(response: 0.35, dampingFraction: 0.8), value: isActive)
    }
}

// MARK: - Gemini / Groq detail

private struct APIProviderDetail: View {
    let kind: ProviderKind
    @ObservedObject var ui: ProvidersUI
    @EnvironmentObject private var settings: SettingsStore
    @EnvironmentObject private var quota: QuotaTracker

    var body: some View {
        let cfg = settings.api(kind)
        VStack(alignment: .leading, spacing: 14) {
            sectionHeader("API keys", detail: "Keys are tried in turn; a rate-limited key rests while the next one works.") {
                Button { ui.showAddKey = true } label: { Label("Add key", systemImage: "plus") }
                    .glassButton(prominent: true)
            }

            if cfg.keys.isEmpty {
                VStack(alignment: .leading, spacing: 8) {
                    Text("No \(kind.displayName) keys yet.").font(.callout.weight(.medium))
                    if let url = kind.keyURL {
                        Link("Get a free \(kind.displayName) API key →", destination: url).font(.callout)
                    }
                }
                .padding(14)
                .frame(maxWidth: .infinity, alignment: .leading)
                .glassCard(cornerRadius: 14)
            } else {
                VStack(spacing: 8) {
                    ForEach(cfg.keys) { meta in
                        KeyRow(kind: kind, meta: meta, status: quota.status(of: meta.id, models: cfg.models)) {
                            ui.confirmDelete = meta
                        }
                        .transition(.move(edge: .top).combined(with: .opacity))
                    }
                }
                .animation(.smooth, value: cfg.keys)
            }

            if kind == .gemini {
                Label("Gemini limits are per Google Cloud project. Keys from the same project share one quota, so create extra keys in different projects.",
                      systemImage: "info.circle")
                    .font(.caption).foregroundStyle(.secondary)
            }

            sectionHeader("Models", detail: "The first model is used normally; the rest are fallbacks when it's limited.") {
                Button("Choose models…") { ui.showModels = true }.glassButton()
            }

            VStack(spacing: 6) {
                ForEach(Array(cfg.models.enumerated()), id: \.element) { index, id in
                    ModelOrderRow(kind: kind, id: id, index: index)
                }
            }

            VStack(alignment: .leading, spacing: 6) {
                Text("When a key or model hits its limit").font(.subheadline.weight(.semibold))
                Picker("", selection: Binding(get: { settings.api(kind).fallbackOrder },
                                              set: { v in settings.updateAPI(kind) { $0.fallbackOrder = v } })) {
                    ForEach(FallbackOrder.allCases) { Text($0.label).tag($0) }
                }
                .pickerStyle(.radioGroup)
                .labelsHidden()
            }
            .padding(14)
            .frame(maxWidth: .infinity, alignment: .leading)
            .glassCard(cornerRadius: 14)
        }
        .confirmationDialog("Remove this key?",
                            isPresented: Binding(get: { ui.confirmDelete != nil }, set: { if !$0 { ui.confirmDelete = nil } }),
                            presenting: ui.confirmDelete) { meta in
            Button("Remove “\(meta.label)”", role: .destructive) {
                withAnimation(.smooth) {
                    settings.removeKey(kind, id: meta.id)
                    quota.forget(key: meta.id)
                }
            }
        }
    }
}

private struct KeyRow: View {
    let kind: ProviderKind
    let meta: APIKeyMeta
    let status: QuotaTracker.KeyStatus
    let onDelete: () -> Void
    @EnvironmentObject private var settings: SettingsStore
    @EnvironmentObject private var quota: QuotaTracker

    var body: some View {
        HStack(spacing: 12) {
            Toggle("", isOn: Binding(get: { meta.isEnabled },
                                     set: { v in
                                         settings.setKeyEnabled(kind, id: meta.id, v)
                                         if v { quota.clearInvalid(meta.id) }
                                     }))
                .toggleStyle(.switch)
                .controlSize(.mini)
                .labelsHidden()

            VStack(alignment: .leading, spacing: 2) {
                Text(meta.label).font(.callout.weight(.medium))
                Text("••••••••\(meta.hint)").font(.caption.monospaced()).foregroundStyle(.secondary)
            }
            Spacer()
            StatusBadge(status: meta.isEnabled ? status : nil)
            Button(role: .destructive, action: onDelete) { Image(systemName: "trash") }
                .buttonStyle(.borderless)
                .help("Remove key")
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
        .glassCard(cornerRadius: 14)
        .opacity(meta.isEnabled ? 1 : 0.6)
    }
}

private struct StatusBadge: View {
    let status: QuotaTracker.KeyStatus?

    var body: some View {
        let (text, color): (String, Color) = {
            guard let status else { return ("Off", .secondary) }
            switch status {
            case .ready: return ("Ready", .green)
            case .invalid: return ("Rejected", .red)
            case .partial(let d): return ("Some models resting · \(d.formatted(date: .omitted, time: .shortened))", .yellow)
            case .cooling(let d, let daily):
                return (daily ? "Daily limit · resets \(d.formatted(date: .omitted, time: .shortened))"
                              : "Rate-limited · back \(d.formatted(date: .omitted, time: .shortened))", .orange)
            }
        }()
        HStack(spacing: 5) {
            Circle().fill(color).frame(width: 7, height: 7)
            Text(text).font(.caption)
        }
        .padding(.horizontal, 9).padding(.vertical, 4)
        .glassCapsule(tint: color == .secondary ? nil : color)
        .help(helpText)
        .animation(.smooth, value: text)
    }

    private var helpText: String {
        if case .invalid(let r) = status { return r }
        return ""
    }
}

private struct ModelOrderRow: View {
    let kind: ProviderKind
    let id: String
    let index: Int

    var body: some View {
        let info = ModelCatalog.info(id, kind: kind)
        HStack(spacing: 10) {
            Text(index == 0 ? "Primary" : "Fallback \(index)")
                .font(.caption2.weight(.bold))
                .foregroundStyle(index == 0 ? Color.white : Color.secondary)
                .padding(.horizontal, 8).padding(.vertical, 3)
                .background(index == 0 ? AnyShapeStyle(kind.tint.gradient) : AnyShapeStyle(Color.primary.opacity(0.08)),
                            in: Capsule())
                .frame(width: 86, alignment: .leading)
            VStack(alignment: .leading, spacing: 1) {
                Text(info?.name ?? id).font(.callout)
                Text(id).font(.caption2.monospaced()).foregroundStyle(.tertiary)
            }
            Spacer()
            if let note = info?.note {
                Text(note).font(.caption2).foregroundStyle(.secondary).multilineTextAlignment(.trailing).frame(maxWidth: 230, alignment: .trailing)
            } else if !(ModelCatalog.info(id, kind: kind)?.isFree ?? false) {
                Text(ModelCatalog.isKnownPaid(id, kind: kind) ? "Not on free tier" : "Custom model")
                    .font(.caption2).foregroundStyle(ModelCatalog.isKnownPaid(id, kind: kind) ? Color.red : Color.secondary)
            }
        }
        .padding(.horizontal, 12).padding(.vertical, 8)
        .glassCard(cornerRadius: 12)
    }
}

// MARK: - Claude Code / Codex detail

private struct CLIProviderDetail: View {
    let kind: ProviderKind
    @ObservedObject var ui: ProvidersUI
    @EnvironmentObject private var settings: SettingsStore
    @EnvironmentObject private var cli: CLIStatusStore

    private var installCommand: String {
        kind == .claudeCode ? "curl -fsSL https://claude.ai/install.sh | bash" : "npm install -g @openai/codex"
    }
    private var loginCommand: String { kind == .claudeCode ? "claude auth login" : "codex login" }

    var body: some View {
        let status = cli.status(kind)
        VStack(alignment: .leading, spacing: 14) {
            statusCard(status)

            sectionHeader("Model", detail: kind == .claudeCode ? "Uses your Claude plan's limits — Haiku is quickest and lightest." : "Leave empty to use the model from your Codex config.") { EmptyView() }

            if kind == .claudeCode {
                VStack(spacing: 6) {
                    ForEach(ModelCatalog.claude) { m in
                        let selected = settings.cli(kind).model == m.id
                        HStack(spacing: 10) {
                            Image(systemName: selected ? "largecircle.fill.circle" : "circle")
                                .foregroundStyle(selected ? kind.tint : Color.secondary)
                                .contentTransition(.symbolEffect(.replace))
                            VStack(alignment: .leading, spacing: 1) {
                                HStack(spacing: 6) {
                                    Text(m.name).font(.callout.weight(.medium))
                                    if m.recommended { RecommendedTag() }
                                }
                                Text(m.note).font(.caption).foregroundStyle(.secondary)
                            }
                            Spacer()
                            Text("--model \(m.id)").font(.caption2.monospaced()).foregroundStyle(.tertiary)
                        }
                        .padding(.horizontal, 12).padding(.vertical, 9)
                        .glassCard(cornerRadius: 12, tint: selected ? kind.tint : nil, interactive: true)
                        .contentShape(Rectangle())
                        .onTapGesture { withAnimation(.smooth) { settings.updateCLI(kind) { $0.model = m.id } } }
                    }
                }
            } else {
                VStack(alignment: .leading, spacing: 10) {
                    TextField("Model ID (optional)", text: settings.cliBinding(kind, \.model), prompt: Text("Default from ~/.codex/config.toml"))
                        .textFieldStyle(.roundedBorder)
                        .font(.body.monospaced())
                    Toggle("Fast mode (low reasoning effort)", isOn: settings.cliBinding(kind, \.fastMode))
                }
                .padding(14)
                .glassCard(cornerRadius: 14)
            }

            DisclosureGroup("Advanced") {
                VStack(alignment: .leading, spacing: 8) {
                    TextField("Path to \(kind.cliName)", text: settings.cliBinding(kind, \.binaryPath),
                              prompt: Text("Auto-detect (e.g. ~/.local/bin/\(kind.cliName))"))
                        .textFieldStyle(.roundedBorder)
                        .font(.caption.monospaced())
                    Label("Runs headless inside ~/.inkwell so these sessions never mix with your normal \(kind.displayName) history.",
                          systemImage: "folder")
                        .font(.caption).foregroundStyle(.secondary)
                }
                .padding(.top, 6)
            }
            .font(.callout)
        }
    }

    @ViewBuilder
    private func statusCard(_ status: CLIStatus) -> some View {
        HStack(alignment: .top, spacing: 12) {
            Group {
                switch status.state {
                case .checking: ProgressView().controlSize(.small)
                case .ready: Image(systemName: "checkmark.circle.fill").foregroundStyle(.green)
                case .loggedOut: Image(systemName: "person.crop.circle.badge.exclamationmark").foregroundStyle(.orange)
                case .notInstalled: Image(systemName: "xmark.circle.fill").foregroundStyle(.red)
                }
            }
            .font(.title2)
            .frame(width: 28)

            VStack(alignment: .leading, spacing: 6) {
                switch status.state {
                case .checking:
                    Text("Checking \(kind.displayName)…").font(.callout.weight(.medium))
                case .ready:
                    Text(status.detail).font(.callout.weight(.medium))
                    Text([status.version, status.path].compactMap { $0 }.joined(separator: " · "))
                        .font(.caption.monospaced()).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
                case .loggedOut:
                    Text("Installed, but not signed in").font(.callout.weight(.medium))
                    Text("Run this in Terminal, finish signing in, then press Check again:").font(.caption).foregroundStyle(.secondary)
                    CommandBox(command: loginCommand)
                case .notInstalled:
                    Text("\(kind.displayName) isn't installed").font(.callout.weight(.medium))
                    Text("Install it in Terminal, sign in, then press Check again. Already installed? Set its path under Advanced.")
                        .font(.caption).foregroundStyle(.secondary)
                    CommandBox(command: installCommand)
                }
            }
            Spacer()
            Button("Check again") {
                Task { await cli.refresh(kind, settings: settings.cli(kind)) }
            }
            .glassButton()
            .disabled(status.state == .checking)
        }
        .padding(14)
        .glassCard(cornerRadius: 16, tint: statusTint(status.state))
        .animation(.smooth, value: status)
    }
}

private func statusTint(_ state: CLIStatus.State) -> Color? {
    switch state {
    case .ready: return .green
    case .checking: return nil
    case .loggedOut, .notInstalled: return .orange
    }
}

struct CommandBox: View {
    let command: String
    var body: some View {
        HStack(spacing: 8) {
            Text(command).font(.caption.monospaced()).textSelection(.enabled).lineLimit(1)
            Spacer(minLength: 4)
            Button {
                NSPasteboard.general.clearContents()
                NSPasteboard.general.setString(command, forType: .string)
            } label: { Image(systemName: "doc.on.doc") }
                .buttonStyle(.borderless)
                .help("Copy")
        }
        .padding(.horizontal, 10).padding(.vertical, 6)
        .background(Color.primary.opacity(0.06), in: RoundedRectangle(cornerRadius: 8, style: .continuous))
    }
}

struct RecommendedTag: View {
    var body: some View {
        Text("Recommended")
            .font(.caption2.weight(.semibold))
            .foregroundStyle(.green)
            .padding(.horizontal, 6).padding(.vertical, 1)
            .background(Color.green.opacity(0.14), in: Capsule())
    }
}

@ViewBuilder
private func sectionHeader<Trailing: View>(_ title: String, detail: String, @ViewBuilder trailing: () -> Trailing) -> some View {
    HStack(alignment: .bottom) {
        VStack(alignment: .leading, spacing: 2) {
            Text(title).font(.headline)
            Text(detail).font(.caption).foregroundStyle(.secondary)
        }
        Spacer()
        trailing()
    }
}
