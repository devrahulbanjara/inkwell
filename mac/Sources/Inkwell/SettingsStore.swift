import Foundation
import SwiftUI

@MainActor
final class SettingsStore: ObservableObject {
    @Published var prefs: Preferences {
        didSet { save() }
    }

    /// keyID → secret. Mirrored to a single Keychain item so macOS asks for access at most once.
    @Published private var secrets: [String: String] = [:]

    private static let defaultsKey = "inkwell.preferences.v2"
    private static let keychainAccount = "api-keys-v2"

    /// Ephemeral stores never read or write UserDefaults or the Keychain (used for screenshots).
    let ephemeral: Bool

    init(ephemeral: Bool = false) {
        self.ephemeral = ephemeral
        if ephemeral {
            prefs = Preferences()
            return
        }
        if let data = UserDefaults.standard.data(forKey: Self.defaultsKey),
           let decoded = try? JSONDecoder().decode(Preferences.self, from: data) {
            prefs = decoded
        } else if let old = UserDefaults.standard.data(forKey: "inkwell.preferences"),
                  let decoded = try? JSONDecoder().decode(Preferences.self, from: old) {
            prefs = decoded // carries templates & toggles over from v1
        } else {
            prefs = Preferences()
        }
        if let json = Keychain.read(Self.keychainAccount),
           let dict = try? JSONDecoder().decode([String: String].self, from: Data(json.utf8)) {
            secrets = dict
        }
    }

    private func save() {
        guard !ephemeral else { return }
        if let data = try? JSONEncoder().encode(prefs) {
            UserDefaults.standard.set(data, forKey: Self.defaultsKey)
        }
    }

    private func saveSecrets() {
        guard !ephemeral else { return }
        if let data = try? JSONEncoder().encode(secrets), let json = String(data: data, encoding: .utf8) {
            Keychain.write(json, account: Self.keychainAccount)
        }
    }

    // MARK: API provider settings

    func api(_ kind: ProviderKind) -> APIProviderSettings {
        kind == .groq ? prefs.groq : prefs.gemini
    }

    func updateAPI(_ kind: ProviderKind, _ change: (inout APIProviderSettings) -> Void) {
        if kind == .groq { change(&prefs.groq) } else { change(&prefs.gemini) }
    }

    func cli(_ kind: ProviderKind) -> CLIProviderSettings {
        kind == .codex ? prefs.codex : prefs.claude
    }

    func updateCLI(_ kind: ProviderKind, _ change: (inout CLIProviderSettings) -> Void) {
        if kind == .codex { change(&prefs.codex) } else { change(&prefs.claude) }
    }

    func cliBinding<T>(_ kind: ProviderKind, _ path: WritableKeyPath<CLIProviderSettings, T>) -> Binding<T> {
        Binding(get: { self.cli(kind)[keyPath: path] },
                set: { v in self.updateCLI(kind) { $0[keyPath: path] = v } })
    }

    // MARK: Keys

    func secret(for id: UUID) -> String {
        (secrets[id.uuidString] ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
    }

    /// Returns nil if this exact key is already saved for the provider.
    @discardableResult
    func addKey(_ kind: ProviderKind, secret raw: String, label: String) -> APIKeyMeta? {
        let secret = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !secret.isEmpty else { return nil }
        if api(kind).keys.contains(where: { self.secret(for: $0.id) == secret }) { return nil }
        let meta = APIKeyMeta(label: label.isEmpty ? "Key \(api(kind).keys.count + 1)" : label,
                              hint: String(secret.suffix(4)))
        secrets[meta.id.uuidString] = secret
        saveSecrets()
        updateAPI(kind) { $0.keys.append(meta) }
        return meta
    }

    func removeKey(_ kind: ProviderKind, id: UUID) {
        secrets[id.uuidString] = nil
        saveSecrets()
        updateAPI(kind) { $0.keys.removeAll { $0.id == id } }
    }

    func setKeyEnabled(_ kind: ProviderKind, id: UUID, _ enabled: Bool) {
        updateAPI(kind) { s in
            if let i = s.keys.firstIndex(where: { $0.id == id }) { s.keys[i].isEnabled = enabled }
        }
    }

    func hasUsableKey(_ kind: ProviderKind) -> Bool {
        api(kind).keys.contains { $0.isEnabled && !secret(for: $0.id).isEmpty }
    }

    func firstSecret(_ kind: ProviderKind) -> String? {
        api(kind).keys.lazy.map { self.secret(for: $0.id) }.first { !$0.isEmpty }
    }

    // MARK: Model order (API providers)

    /// The models actually used: the user's list, or the recommended defaults if they never chose.
    func effectiveModels(_ kind: ProviderKind) -> [String] {
        let m = api(kind).models
        return m.isEmpty ? ModelCatalog.defaultModelIDs(for: kind) : m
    }

    /// Makes `id` the primary model. For CLI providers it simply sets the model.
    func setPrimaryModel(_ kind: ProviderKind, _ id: String) {
        guard kind.usesAPIKeys else { updateCLI(kind) { $0.model = id }; return }
        var models = effectiveModels(kind)
        models.removeAll { $0 == id }
        models.insert(id, at: 0)
        updateAPI(kind) { $0.models = models }
    }

    /// Adds `id` as the last fallback, or removes it. The last remaining model can't be removed.
    func toggleModel(_ kind: ProviderKind, _ id: String) {
        var models = effectiveModels(kind)
        if let i = models.firstIndex(of: id) {
            guard models.count > 1 else { return }
            models.remove(at: i)
        } else {
            models.append(id)
        }
        updateAPI(kind) { $0.models = models }
    }

    func moveModelUp(_ kind: ProviderKind, _ id: String) {
        var models = effectiveModels(kind)
        guard let i = models.firstIndex(of: id), i > 0 else { return }
        models.swapAt(i, i - 1)
        updateAPI(kind) { $0.models = models }
    }

    /// Enabled templates, longest trigger first so "?formal" wins over "?f".
    var activeTemplates: [Template] {
        prefs.templates
            .filter { $0.isEnabled && !$0.trigger.trimmingCharacters(in: .whitespaces).isEmpty }
            .sorted { $0.trigger.count > $1.trigger.count }
    }

    /// Short label for the menu bar dashboard, e.g. "Gemini · gemini-3.5-flash-lite".
    var activeSummary: String {
        let k = prefs.activeProvider
        let model: String
        if k.usesAPIKeys {
            model = api(k).models.first ?? ModelCatalog.defaultModelIDs(for: k).first ?? ""
        } else {
            let m = cli(k).model
            model = m.isEmpty ? "default model" : m
        }
        return "\(k.displayName) · \(model)"
    }
}
