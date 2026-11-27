import Foundation

/// Caches the install / sign-in state of the Claude Code and Codex CLIs for the Settings UI.
@MainActor
final class CLIStatusStore: ObservableObject {
    @Published private(set) var statuses: [ProviderKind: CLIStatus] = [:]

    func status(_ kind: ProviderKind) -> CLIStatus {
        statuses[kind] ?? .checking
    }

    func refresh(_ kind: ProviderKind, settings: CLIProviderSettings) async {
        statuses[kind] = .checking
        statuses[kind] = await CLIProviders.status(kind: kind, settings: settings)
    }

    func refreshAll(_ store: SettingsStore) async {
        let claudeSettings = store.cli(.claudeCode)
        let codexSettings = store.cli(.codex)
        statuses[.claudeCode] = .checking
        statuses[.codex] = .checking
        async let c = CLIProviders.status(kind: .claudeCode, settings: claudeSettings)
        async let x = CLIProviders.status(kind: .codex, settings: codexSettings)
        let (claude, codex) = await (c, x)
        statuses[.claudeCode] = claude
        statuses[.codex] = codex
    }
}
