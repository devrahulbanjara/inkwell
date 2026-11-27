import AppKit
import SwiftUI

// MARK: - Shared provider actions (used by the popover and the Settings window)

struct ProviderActionError: LocalizedError {
    let message: String
    var errorDescription: String? { message }
}

@MainActor
enum ProviderActions {
    /// Makes `kind` active if it's usable. Returns a short reason when it isn't.
    static func activate(_ kind: ProviderKind, settings: SettingsStore, cli: CLIStatusStore) async -> String? {
        if kind.usesAPIKeys {
            guard settings.hasUsableKey(kind) else { return "Add a \(kind.displayName) key first." }
        } else {
            await cli.refresh(kind, settings: settings.cli(kind))
            switch cli.status(kind).state {
            case .ready: break
            case .notInstalled: return "Install \(kind.displayName) first."
            default: return "Sign in to \(kind.displayName) first."
            }
        }
        withAnimation(.smooth) { settings.prefs.activeProvider = kind }
        return nil
    }

    /// Validates a key with the provider, then saves it. Returns the models the key can access.
    static func addKey(_ kind: ProviderKind, key raw: String, label: String,
                       settings: SettingsStore, quota: QuotaTracker) async throws -> [RemoteModel] {
        let key = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        let models: [RemoteModel]
        do {
            models = try await HTTPProviders.listModels(kind: kind, key: key)
        } catch {
            let e = AIError.from(error)
            switch e {
            case .invalidKey(let msg): throw ProviderActionError(message: "\(kind.displayName) rejected this key: \(msg)")
            case .offline: throw ProviderActionError(message: "You're offline — connect and try again.")
            case .rateLimited: throw ProviderActionError(message: "Rate-limited while checking. Wait a moment and try again.")
            default: throw ProviderActionError(message: e.localizedDescription)
            }
        }
        guard let meta = settings.addKey(kind, secret: key, label: label.trimmingCharacters(in: .whitespaces)) else {
            throw ProviderActionError(message: "That key is already added.")
        }
        quota.clearInvalid(meta.id)
        // First key for an API provider while the active one can't run → switch to it.
        let active = settings.prefs.activeProvider
        if active.usesAPIKeys, active != kind, !settings.hasUsableKey(active) {
            withAnimation(.smooth) { settings.prefs.activeProvider = kind }
        }
        return models
    }

    static func test(_ kind: ProviderKind, app: AppState) async -> (ok: Bool, message: String) {
        let start = Date()
        do {
            let r = try await app.router.complete(system: "Reply with exactly the word OK.", user: "ping", provider: kind)
            let time = String(format: "%.1fs", Date().timeIntervalSince(start))
            return (true, "Works — \(r.model)\(r.keyLabel.map { " · \($0)" } ?? "") · \(time)")
        } catch {
            return (false, error.localizedDescription)
        }
    }
}

// MARK: - Popover page

@MainActor
final class QuickProviderUI: ObservableObject {
    @Published var inspected: ProviderKind
    @Published var addingKey = false
    @Published var newKey = ""
    @Published var newLabel = ""
    @Published var validating = false
    @Published var keyError: String?
    @Published var confirmRemove: UUID?
    @Published var notice: String?
    @Published var testing = false
    @Published var testResult: String?
    @Published var testOK = false
    @Published var customModel = ""

    init(_ kind: ProviderKind, addKey: Bool) { inspected = kind; addingKey = addKey && kind.usesAPIKeys }

    func resetTransient() {
        addingKey = false; newKey = ""; newLabel = ""; keyError = nil
        confirmRemove = nil; notice = nil; testResult = nil; customModel = ""
    }
}

/// Provider, key and model controls that live right in the menu bar popover.
struct QuickProviderView: View {
    let onBack: () -> Void
    @EnvironmentObject private var app: AppState
    @EnvironmentObject private var settings: SettingsStore
    @EnvironmentObject private var quota: QuotaTracker
    @EnvironmentObject private var cli: CLIStatusStore
    @StateObject private var ui: QuickProviderUI

    init(initial: ProviderKind, addKey: Bool = false, onBack: @escaping () -> Void) {
        self.onBack = onBack
        _ui = StateObject(wrappedValue: QuickProviderUI(initial, addKey: addKey))
    }

    private var kind: ProviderKind { ui.inspected }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            header
            providerStrip
            ScrollView {
                VStack(alignment: .leading, spacing: 14) {
                    activationRow
                    Group {
                        if kind.usesAPIKeys { apiDetail } else { cliDetail }
                    }
                    .id(kind)
                    .transition(.opacity.combined(with: .move(edge: .trailing)))
                    testRow
                }
                .padding(2)
            }
            .frame(height: 420)
        }
        .animation(.smooth(duration: 0.3), value: kind)
        .animation(.smooth(duration: 0.25), value: ui.addingKey)
        .animation(.smooth(duration: 0.25), value: ui.notice)
        .task { await cli.refreshAll(settings) }
    }

    // MARK: Header & provider chooser

    private var header: some View {
        HStack(spacing: 8) {
            Button(action: onBack) {
                Image(systemName: "chevron.left").font(.system(size: 13, weight: .semibold))
                    .frame(width: 26, height: 26)
            }
            .buttonStyle(.borderless)
            .keyboardShortcut(.cancelAction)
            .help("Back to dashboard")
            Text("AI Provider").font(.headline)
            Spacer()
            Button { SettingsWindow.shared.show(app, tab: .providers) } label: {
                Image(systemName: "gearshape")
            }
            .buttonStyle(.borderless)
            .help("Open full settings")
        }
    }

    private var providerStrip: some View {
        GlassGroup(spacing: 6) {
            HStack(spacing: 6) {
                ForEach(ProviderKind.allCases) { k in
                    let selected = k == kind
                    let active = settings.prefs.activeProvider == k
                    VStack(spacing: 5) {
                        ProviderLogo(kind: k, size: 26)
                            .overlay(alignment: .topTrailing) {
                                if active {
                                    Image(systemName: "checkmark.circle.fill")
                                        .font(.system(size: 11, weight: .bold))
                                        .foregroundStyle(.white, .green)
                                        .offset(x: 5, y: -5)
                                        .transition(.scale.combined(with: .opacity))
                                }
                            }
                        Text(k.displayName)
                            .font(.caption2.weight(selected ? .semibold : .regular))
                            .lineLimit(1)
                            .minimumScaleFactor(0.8)
                    }
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 8)
                    .glassCard(cornerRadius: 12, tint: selected ? k.tint : nil, interactive: true)
                    .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous)
                        .strokeBorder(selected ? k.tint.opacity(0.85) : .clear, lineWidth: 1.5))
                    .contentShape(Rectangle())
                    .onTapGesture {
                        withAnimation(.smooth(duration: 0.3)) { ui.inspected = k; ui.resetTransient() }
                    }
                    .help(k.subtitle)
                    .animation(.spring(response: 0.35, dampingFraction: 0.8), value: active)
                }
            }
        }
    }

    @ViewBuilder
    private var activationRow: some View {
        if settings.prefs.activeProvider == kind {
            HStack(spacing: 6) {
                Image(systemName: "checkmark.seal.fill").foregroundStyle(.green)
                Text("\(kind.displayName) is active").font(.callout.weight(.medium))
                Spacer()
            }
        } else {
            HStack(spacing: 8) {
                Button {
                    Task { ui.notice = await ProviderActions.activate(kind, settings: settings, cli: cli) }
                } label: {
                    Label("Use \(kind.displayName)", systemImage: "checkmark.circle")
                }
                .glassButton(prominent: true)
                .controlSize(.small)
                if let n = ui.notice {
                    Text(n).font(.caption).foregroundStyle(.orange).lineLimit(2).transition(.opacity)
                }
                Spacer(minLength: 0)
            }
        }
    }

    // MARK: Gemini / Groq

    private var apiDetail: some View {
        let cfg = settings.api(kind)
        return VStack(alignment: .leading, spacing: 10) {
            sectionTitle("API keys") {
                Button {
                    ui.addingKey.toggle(); ui.keyError = nil
                } label: {
                    Image(systemName: ui.addingKey ? "xmark" : "plus")
                        .contentTransition(.symbolEffect(.replace))
                }
                .buttonStyle(.borderless)
                .help(ui.addingKey ? "Cancel" : "Add a key")
            }

            if ui.addingKey || cfg.keys.isEmpty {
                addKeyForm.transition(.move(edge: .top).combined(with: .opacity))
            }

            ForEach(cfg.keys) { meta in
                keyRow(meta, models: settings.effectiveModels(kind))
                    .transition(.move(edge: .top).combined(with: .opacity))
            }

            sectionTitle("Models") { EmptyView() }
            Text("#1 is used first; the rest are fallbacks when it's limited. Tap to add or remove.")
                .font(.caption2).foregroundStyle(.secondary)
            modelList

            HStack {
                Text("When limited").font(.caption).foregroundStyle(.secondary)
                Spacer()
                Picker("", selection: Binding(get: { settings.api(kind).fallbackOrder },
                                              set: { v in settings.updateAPI(kind) { $0.fallbackOrder = v } })) {
                    Text("Try other keys first").tag(FallbackOrder.keysFirst)
                    Text("Try next model first").tag(FallbackOrder.modelsFirst)
                }
                .labelsHidden()
                .pickerStyle(.menu)
                .controlSize(.small)
                .fixedSize()
            }
        }
        .animation(.smooth, value: cfg.keys)
        .animation(.smooth(duration: 0.25), value: cfg.models)
    }

    private var addKeyForm: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 6) {
                SecureField("API key", text: $ui.newKey, prompt: Text(kind.keyPlaceholder))
                    .textFieldStyle(.roundedBorder)
                    .font(.callout.monospaced())
                    .onSubmit(addKey)
                Button {
                    if let s = NSPasteboard.general.string(forType: .string) {
                        ui.newKey = s.trimmingCharacters(in: .whitespacesAndNewlines)
                    }
                } label: { Image(systemName: "doc.on.clipboard") }
                    .buttonStyle(.borderless)
                    .help("Paste key from clipboard")
            }
            TextField("Label", text: $ui.newLabel, prompt: Text("Label (optional)"))
                .textFieldStyle(.roundedBorder)
                .font(.callout)
                .onSubmit(addKey)
            if let e = ui.keyError {
                Label(e, systemImage: "exclamationmark.triangle.fill")
                    .font(.caption).foregroundStyle(.red).lineLimit(3)
            }
            HStack {
                if let url = kind.keyURL {
                    Link("Get a free key →", destination: url).font(.caption)
                }
                Spacer()
                Button(action: addKey) {
                    HStack(spacing: 5) {
                        if ui.validating { ProgressView().controlSize(.mini) }
                        Text(ui.validating ? "Checking…" : "Add key")
                    }
                }
                .glassButton(prominent: true)
                .controlSize(.small)
                .disabled(ui.newKey.trimmingCharacters(in: .whitespaces).isEmpty || ui.validating)
            }
        }
        .padding(10)
        .glassCard(cornerRadius: 12)
    }

    private func addKey() {
        guard !ui.validating, !ui.newKey.trimmingCharacters(in: .whitespaces).isEmpty else { return }
        ui.validating = true
        ui.keyError = nil
        let k = kind
        Task {
            defer { ui.validating = false }
            do {
                _ = try await ProviderActions.addKey(k, key: ui.newKey, label: ui.newLabel, settings: settings, quota: quota)
                withAnimation(.smooth) { ui.addingKey = false; ui.newKey = ""; ui.newLabel = "" }
            } catch {
                withAnimation(.smooth) { ui.keyError = error.localizedDescription }
            }
        }
    }

    private func keyRow(_ meta: APIKeyMeta, models: [String]) -> some View {
        let badge = Self.badge(meta.isEnabled ? quota.status(of: meta.id, models: models) : nil)
        return HStack(spacing: 8) {
            Toggle("", isOn: Binding(get: { meta.isEnabled },
                                     set: { v in
                                         settings.setKeyEnabled(kind, id: meta.id, v)
                                         if v { quota.clearInvalid(meta.id) }
                                     }))
                .toggleStyle(.switch)
                .controlSize(.mini)
                .labelsHidden()
            VStack(alignment: .leading, spacing: 1) {
                Text(meta.label).font(.callout.weight(.medium)).lineLimit(1)
                Text("••••\(meta.hint)").font(.caption2.monospaced()).foregroundStyle(.secondary)
            }
            Spacer(minLength: 4)
            if ui.confirmRemove == meta.id {
                Button("Remove", role: .destructive) {
                    withAnimation(.smooth) {
                        settings.removeKey(kind, id: meta.id)
                        quota.forget(key: meta.id)
                        ui.confirmRemove = nil
                    }
                }
                .controlSize(.small)
                Button { ui.confirmRemove = nil } label: { Image(systemName: "xmark") }
                    .buttonStyle(.borderless)
            } else {
                HStack(spacing: 4) {
                    Circle().fill(badge.color).frame(width: 7, height: 7)
                    Text(badge.text).font(.caption2).foregroundStyle(.secondary).lineLimit(1)
                }
                .help(badge.help)
                Button { withAnimation(.smooth) { ui.confirmRemove = meta.id } } label: {
                    Image(systemName: "trash")
                }
                .buttonStyle(.borderless)
                .help("Remove key")
            }
        }
        .padding(.horizontal, 10).padding(.vertical, 7)
        .glassCard(cornerRadius: 12)
        .opacity(meta.isEnabled ? 1 : 0.6)
    }

    static func badge(_ status: QuotaTracker.KeyStatus?) -> (text: String, color: Color, help: String) {
        guard let status else { return ("Off", .secondary, "This key is turned off") }
        let t: (Date) -> String = { $0.formatted(date: .omitted, time: .shortened) }
        switch status {
        case .ready: return ("Ready", .green, "Ready to use")
        case .invalid(let r): return ("Rejected", .red, r)
        case .partial(let d): return ("Partly resting", .yellow, "Some models are rate-limited until \(t(d))")
        case .cooling(let d, let daily):
            return daily ? ("Resets \(t(d))", .orange, "Daily free limit reached")
                         : ("Back \(t(d))", .orange, "Rate-limited for a moment")
        }
    }

    private var modelList: some View {
        let selected = settings.effectiveModels(kind)
        let catalog = ModelCatalog.models(for: kind)
        let unselected = catalog.filter { !selected.contains($0.id) }
        return VStack(spacing: 5) {
            ForEach(Array(selected.enumerated()), id: \.element) { index, id in
                modelRow(id: id, index: index, info: ModelCatalog.info(id, kind: kind))
            }
            ForEach(unselected) { info in
                modelRow(id: info.id, index: nil, info: info)
            }
            HStack(spacing: 6) {
                TextField("Other model ID", text: $ui.customModel)
                    .textFieldStyle(.roundedBorder)
                    .font(.caption.monospaced())
                    .onSubmit(addCustomModel)
                Button("Add", action: addCustomModel)
                    .controlSize(.small)
                    .disabled(ui.customModel.trimmingCharacters(in: .whitespaces).isEmpty)
            }
            .padding(.top, 2)
        }
    }

    private func addCustomModel() {
        let id = ui.customModel.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !id.isEmpty else { return }
        if !settings.effectiveModels(kind).contains(id) { settings.toggleModel(kind, id) }
        ui.customModel = ""
    }

    private func modelRow(id: String, index: Int?, info: ModelInfo?) -> some View {
        let selected = index != nil
        return HStack(spacing: 8) {
            ZStack {
                Circle().fill(selected ? AnyShapeStyle(kind.tint.gradient) : AnyShapeStyle(Color.primary.opacity(0.08)))
                if let index {
                    Text("\(index + 1)").font(.caption2.weight(.bold)).foregroundStyle(.white)
                }
            }
            .frame(width: 18, height: 18)
            VStack(alignment: .leading, spacing: 0) {
                HStack(spacing: 4) {
                    Text(info?.name ?? id).font(.caption.weight(.medium)).lineLimit(1)
                    if info?.recommended == true { miniTag("Best", .green) }
                    if info?.preview == true { miniTag("Preview", .purple) }
                    if info == nil { miniTag(ModelCatalog.isKnownPaid(id, kind: kind) ? "Not free" : "Custom",
                                             ModelCatalog.isKnownPaid(id, kind: kind) ? .red : .secondary) }
                }
                Text(id).font(.caption2.monospaced()).foregroundStyle(.tertiary).lineLimit(1).truncationMode(.middle)
            }
            Spacer(minLength: 2)
            if let index, index > 0 {
                Button { settings.moveModelUp(kind, id) } label: { Image(systemName: "chevron.up") }
                    .buttonStyle(.borderless)
                    .help("Move up")
                Button { settings.setPrimaryModel(kind, id) } label: { Image(systemName: "star") }
                    .buttonStyle(.borderless)
                    .help("Make primary")
            }
        }
        .padding(.horizontal, 9).padding(.vertical, 6)
        .glassCard(cornerRadius: 10, tint: selected ? kind.tint : nil, interactive: true)
        .opacity(selected ? 1 : 0.75)
        .contentShape(Rectangle())
        .onTapGesture { settings.toggleModel(kind, id) }
    }

    private func miniTag(_ text: String, _ color: Color) -> some View {
        Text(text)
            .font(.system(size: 9, weight: .semibold))
            .foregroundStyle(color)
            .padding(.horizontal, 5).padding(.vertical, 1)
            .background(color.opacity(0.14), in: Capsule())
    }

    // MARK: Claude Code / Codex

    private var cliDetail: some View {
        let status = cli.status(kind)
        return VStack(alignment: .leading, spacing: 10) {
            HStack(alignment: .top, spacing: 8) {
                Group {
                    switch status.state {
                    case .checking: ProgressView().controlSize(.small)
                    case .ready: Image(systemName: "checkmark.circle.fill").foregroundStyle(.green)
                    case .loggedOut: Image(systemName: "person.crop.circle.badge.exclamationmark").foregroundStyle(.orange)
                    case .notInstalled: Image(systemName: "xmark.circle.fill").foregroundStyle(.red)
                    }
                }
                .frame(width: 18)
                VStack(alignment: .leading, spacing: 5) {
                    switch status.state {
                    case .checking:
                        Text("Checking \(kind.displayName)…").font(.callout)
                    case .ready:
                        Text(status.detail).font(.callout.weight(.medium)).lineLimit(2)
                    case .loggedOut:
                        Text("Installed, but not signed in").font(.callout.weight(.medium))
                        Text("Run this in Terminal, then check again:").font(.caption).foregroundStyle(.secondary)
                        CommandBox(command: kind == .claudeCode ? "claude auth login" : "codex login")
                    case .notInstalled:
                        Text("\(kind.displayName) isn't installed").font(.callout.weight(.medium))
                        Text("Install it in Terminal, sign in, then check again:").font(.caption).foregroundStyle(.secondary)
                        CommandBox(command: kind == .claudeCode ? "curl -fsSL https://claude.ai/install.sh | bash"
                                                                : "npm install -g @openai/codex")
                    }
                }
                Spacer(minLength: 0)
                Button { Task { await cli.refresh(kind, settings: settings.cli(kind)) } } label: {
                    Image(systemName: "arrow.clockwise")
                }
                .buttonStyle(.borderless)
                .disabled(status.state == .checking)
                .help("Check again")
            }
            .padding(10)
            .glassCard(cornerRadius: 12, tint: status.state == .ready ? .green : (status.state == .checking ? nil : .orange))

            sectionTitle("Model") { EmptyView() }
            if kind == .claudeCode {
                VStack(spacing: 5) {
                    ForEach(ModelCatalog.claude) { m in
                        let selected = settings.cli(kind).model == m.id
                        HStack(spacing: 8) {
                            Image(systemName: selected ? "largecircle.fill.circle" : "circle")
                                .foregroundStyle(selected ? kind.tint : Color.secondary)
                                .contentTransition(.symbolEffect(.replace))
                            VStack(alignment: .leading, spacing: 0) {
                                HStack(spacing: 4) {
                                    Text(m.name).font(.caption.weight(.medium))
                                    if m.recommended { miniTag("Best", .green) }
                                }
                                Text(m.note).font(.caption2).foregroundStyle(.secondary).lineLimit(2)
                            }
                            Spacer(minLength: 0)
                        }
                        .padding(.horizontal, 9).padding(.vertical, 6)
                        .glassCard(cornerRadius: 10, tint: selected ? kind.tint : nil, interactive: true)
                        .contentShape(Rectangle())
                        .onTapGesture { withAnimation(.smooth) { settings.updateCLI(kind) { $0.model = m.id } } }
                    }
                }
            } else {
                TextField("Model ID", text: settings.cliBinding(kind, \.model), prompt: Text("Default from Codex config"))
                    .textFieldStyle(.roundedBorder)
                    .font(.caption.monospaced())
                Toggle("Fast mode (low reasoning effort)", isOn: settings.cliBinding(kind, \.fastMode))
                    .font(.caption)
                    .toggleStyle(.switch)
                    .controlSize(.mini)
            }
        }
    }

    // MARK: Test

    private var testRow: some View {
        VStack(alignment: .leading, spacing: 6) {
            Button {
                ui.testing = true
                ui.testResult = nil
                let k = kind
                Task {
                    let r = await ProviderActions.test(k, app: app)
                    ui.testOK = r.ok
                    ui.testResult = r.message
                    ui.testing = false
                }
            } label: {
                HStack(spacing: 5) {
                    if ui.testing { ProgressView().controlSize(.mini) } else { Image(systemName: "paperplane") }
                    Text(ui.testing ? "Testing…" : "Send a test request")
                }
            }
            .glassButton()
            .controlSize(.small)
            .disabled(ui.testing)

            if let r = ui.testResult {
                Label(r, systemImage: ui.testOK ? "checkmark.circle.fill" : "xmark.octagon.fill")
                    .font(.caption)
                    .foregroundStyle(ui.testOK ? Color.green : Color.red)
                    .lineLimit(4)
                    .textSelection(.enabled)
                    .transition(.opacity)
            }
        }
        .padding(.top, 4)
        .animation(.smooth, value: ui.testResult)
    }

    private func sectionTitle<T: View>(_ title: String, @ViewBuilder trailing: () -> T) -> some View {
        HStack {
            Text(title.uppercased()).font(.caption2.weight(.bold)).foregroundStyle(.secondary)
            Spacer()
            trailing()
        }
        .padding(.top, 2)
    }
}
