import AppKit
import SwiftUI

// MARK: - Add key → validate → choose models

@MainActor
final class AddKeyModel: ObservableObject {
    enum Step { case enterKey, chooseModels }
    @Published var step: Step = .enterKey
    @Published var key = ""
    @Published var label = ""
    @Published var validating = false
    @Published var error: String?
    @Published var remote: [RemoteModel] = []
    @Published var selection: [String] = []
    @Published var customID = ""
}

struct AddKeySheet: View {
    let kind: ProviderKind
    let onClose: () -> Void
    @EnvironmentObject private var settings: SettingsStore
    @EnvironmentObject private var quota: QuotaTracker
    @StateObject private var m = AddKeyModel()

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            HStack(spacing: 10) {
                ZStack {
                    Circle().fill(kind.tint.gradient)
                    Image(systemName: m.step == .enterKey ? "key.fill" : "cpu").foregroundStyle(.white)
                        .contentTransition(.symbolEffect(.replace))
                }
                .frame(width: 32, height: 32)
                VStack(alignment: .leading, spacing: 1) {
                    Text(m.step == .enterKey ? "Add a \(kind.displayName) key" : "Which models should this use?")
                        .font(.title3.weight(.semibold))
                    Text(m.step == .enterKey ? "It's checked with \(kind.displayName) before saving, and stored in your Keychain."
                                             : "Pick a primary model and optional fallbacks. Free models are listed first.")
                        .font(.caption).foregroundStyle(.secondary)
                }
            }

            Group {
                switch m.step {
                case .enterKey: enterKey
                case .chooseModels:
                    ModelPickerList(kind: kind, remote: m.remote, loading: false,
                                    selection: $m.selection, customID: $m.customID)
                }
            }
            .transition(.asymmetric(insertion: .move(edge: .trailing).combined(with: .opacity),
                                    removal: .move(edge: .leading).combined(with: .opacity)))

            if let e = m.error {
                Label(e, systemImage: "exclamationmark.triangle.fill")
                    .font(.caption).foregroundStyle(.red)
                    .transition(.opacity)
            }

            HStack {
                if m.step == .chooseModels {
                    Text("Key saved ✓").font(.caption).foregroundStyle(.green)
                }
                Spacer()
                Button(m.step == .enterKey ? "Cancel" : "Keep current models") { onClose() }
                    .keyboardShortcut(.cancelAction)
                if m.step == .enterKey {
                    Button { validate() } label: {
                        HStack(spacing: 6) {
                            if m.validating { ProgressView().controlSize(.small) }
                            Text(m.validating ? "Checking…" : "Continue")
                        }
                    }
                    .keyboardShortcut(.defaultAction)
                    .glassButton(prominent: true)
                    .disabled(m.key.trimmingCharacters(in: .whitespaces).isEmpty || m.validating)
                } else {
                    Button("Save models") {
                        if !m.selection.isEmpty { settings.updateAPI(kind) { $0.models = m.selection } }
                        onClose()
                    }
                    .keyboardShortcut(.defaultAction)
                    .glassButton(prominent: true)
                    .disabled(m.selection.isEmpty)
                }
            }
        }
        .padding(22)
        .frame(width: 560, height: m.step == .enterKey ? 300 : 560)
        .animation(.smooth(duration: 0.35), value: m.step)
        .animation(.smooth, value: m.error)
        .onAppear { m.selection = settings.api(kind).models }
    }

    private var enterKey: some View {
        VStack(alignment: .leading, spacing: 10) {
            SecureField("API key", text: $m.key, prompt: Text(kind.keyPlaceholder))
                .textFieldStyle(.roundedBorder)
                .font(.body.monospaced())
            TextField("Label", text: $m.label, prompt: Text("e.g. Personal, Project 2 (optional)"))
                .textFieldStyle(.roundedBorder)
            if let url = kind.keyURL {
                Link("Get a free key from \(kind == .gemini ? "Google AI Studio" : "GroqCloud") →", destination: url)
                    .font(.caption)
            }
        }
    }

    private func validate() {
        m.error = nil
        m.validating = true
        Task {
            defer { m.validating = false }
            do {
                m.remote = try await ProviderActions.addKey(kind, key: m.key, label: m.label, settings: settings, quota: quota)
                if m.selection.isEmpty { m.selection = ModelCatalog.defaultModelIDs(for: kind) }
                m.step = .chooseModels
            } catch {
                m.error = error.localizedDescription
            }
        }
    }
}

// MARK: - Change models (from the provider screen)

@MainActor
final class ModelsSheetModel: ObservableObject {
    @Published var selection: [String] = []
    @Published var remote: [RemoteModel] = []
    @Published var loading = false
    @Published var customID = ""
    @Published var loaded = false
}

struct ModelsSheet: View {
    let kind: ProviderKind
    let onClose: () -> Void
    @EnvironmentObject private var settings: SettingsStore
    @StateObject private var m = ModelsSheetModel()

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            VStack(alignment: .leading, spacing: 2) {
                Text("\(kind.displayName) models").font(.title3.weight(.semibold))
                Text("Tick models in the order you want them tried. #1 is primary; the rest are fallbacks.")
                    .font(.caption).foregroundStyle(.secondary)
            }
            ModelPickerList(kind: kind, remote: m.remote, loading: m.loading,
                            selection: $m.selection, customID: $m.customID)
            HStack {
                Button("Reset to recommended") {
                    withAnimation(.smooth) { m.selection = ModelCatalog.defaultModelIDs(for: kind) }
                }
                Spacer()
                Button("Cancel") { onClose() }.keyboardShortcut(.cancelAction)
                Button("Save") {
                    settings.updateAPI(kind) { $0.models = m.selection }
                    onClose()
                }
                .keyboardShortcut(.defaultAction)
                .glassButton(prominent: true)
                .disabled(m.selection.isEmpty)
            }
        }
        .padding(22)
        .frame(width: 560, height: 560)
        .onAppear {
            guard !m.loaded else { return }
            m.loaded = true
            m.selection = settings.api(kind).models
            guard let key = settings.firstSecret(kind) else { return }
            m.loading = true
            Task {
                m.remote = (try? await HTTPProviders.listModels(kind: kind, key: key)) ?? []
                m.loading = false
            }
        }
    }
}

// MARK: - Shared picker

struct ModelPickerList: View {
    let kind: ProviderKind
    let remote: [RemoteModel]
    let loading: Bool
    @Binding var selection: [String]
    @Binding var customID: String

    private var catalog: [ModelInfo] { ModelCatalog.models(for: kind) }

    private var extras: [RemoteModel] {
        let known = Set(catalog.map(\.id))
        return remote.filter { !known.contains($0.id) }.sorted { $0.id < $1.id }
    }

    private var customSelected: [String] {
        let known = Set(catalog.map(\.id)).union(remote.map(\.id))
        return selection.filter { !known.contains($0) }
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 8) {
                Text("FREE MODELS").font(.caption2.weight(.bold)).foregroundStyle(.secondary)
                ForEach(catalog) { info in
                    row(id: info.id, title: info.name, note: info.note,
                        badge: info.recommended ? .recommended : (info.preview ? .preview : nil),
                        available: remote.isEmpty || remote.contains { $0.id == info.id } || info.id.hasSuffix("-latest"))
                }

                if loading {
                    HStack(spacing: 8) {
                        ProgressView().controlSize(.small)
                        Text("Loading models your key can access…").font(.caption).foregroundStyle(.secondary)
                    }
                    .padding(.top, 6)
                }

                if !extras.isEmpty {
                    Text("ALSO AVAILABLE ON YOUR KEY").font(.caption2.weight(.bold)).foregroundStyle(.secondary).padding(.top, 10)
                    Text("Not verified as free. Paid-only models usually fail with a quota error on free keys.")
                        .font(.caption2).foregroundStyle(.tertiary)
                    ForEach(extras) { r in
                        row(id: r.id, title: r.displayName, note: nil,
                            badge: ModelCatalog.isKnownPaid(r.id, kind: kind) ? .paid : .unverified, available: true)
                    }
                }

                ForEach(customSelected, id: \.self) { id in
                    row(id: id, title: id, note: "Custom model ID", badge: .unverified, available: true)
                }

                HStack {
                    TextField("Add a model ID manually", text: $customID)
                        .textFieldStyle(.roundedBorder)
                        .font(.caption.monospaced())
                        .onSubmit(addCustom)
                    Button("Add", action: addCustom)
                        .disabled(customID.trimmingCharacters(in: .whitespaces).isEmpty)
                }
                .padding(.top, 8)
            }
            .padding(2)
        }
        .animation(.smooth(duration: 0.25), value: selection)
    }

    private enum Badge { case recommended, preview, paid, unverified }

    private func row(id: String, title: String, note: String?, badge: Badge?, available: Bool) -> some View {
        let index = selection.firstIndex(of: id)
        let selected = index != nil
        return HStack(spacing: 10) {
            ZStack {
                Circle()
                    .fill(selected ? AnyShapeStyle(kind.tint.gradient) : AnyShapeStyle(Color.primary.opacity(0.08)))
                if let index {
                    Text("\(index + 1)").font(.caption.weight(.bold)).foregroundStyle(.white)
                        .transition(.scale.combined(with: .opacity))
                }
            }
            .frame(width: 22, height: 22)

            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 6) {
                    Text(title).font(.callout.weight(.medium)).lineLimit(1)
                    switch badge {
                    case .recommended: RecommendedTag()
                    case .preview: tag("Preview", .purple)
                    case .paid: tag("Not free", .red)
                    case .unverified: tag("Unverified", .secondary)
                    case .none: EmptyView()
                    }
                    if !available { tag("Not on this key", .orange) }
                }
                Text(id).font(.caption2.monospaced()).foregroundStyle(.tertiary)
                if let note { Text(note).font(.caption).foregroundStyle(.secondary) }
            }
            Spacer()
            if let index {
                HStack(spacing: 2) {
                    Button { move(id, by: -1) } label: { Image(systemName: "chevron.up") }
                        .disabled(index == 0)
                    Button { move(id, by: 1) } label: { Image(systemName: "chevron.down") }
                        .disabled(index == selection.count - 1)
                }
                .buttonStyle(.borderless)
                .transition(.opacity)
            }
        }
        .padding(.horizontal, 12).padding(.vertical, 8)
        .glassCard(cornerRadius: 12, tint: selected ? kind.tint : nil, interactive: true)
        .contentShape(Rectangle())
        .onTapGesture { toggle(id) }
    }

    private func tag(_ text: String, _ color: Color) -> some View {
        Text(text)
            .font(.caption2.weight(.semibold))
            .foregroundStyle(color)
            .padding(.horizontal, 6).padding(.vertical, 1)
            .background(color.opacity(0.14), in: Capsule())
    }

    private func toggle(_ id: String) {
        if let i = selection.firstIndex(of: id) { selection.remove(at: i) } else { selection.append(id) }
    }

    private func move(_ id: String, by delta: Int) {
        guard let i = selection.firstIndex(of: id) else { return }
        let j = i + delta
        guard selection.indices.contains(j) else { return }
        selection.swapAt(i, j)
    }

    private func addCustom() {
        let id = customID.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !id.isEmpty else { return }
        if !selection.contains(id) { selection.append(id) }
        customID = ""
    }
}
