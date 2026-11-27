import AppKit
import ApplicationServices
import ServiceManagement

enum InkwellError: LocalizedError {
    case noText
    case focusChanged

    var errorDescription: String? {
        switch self {
        case .noText: return "Couldn't read the text in this field."
        case .focusChanged: return "You switched fields, so the result was copied to your clipboard."
        }
    }
}

@MainActor
final class AppState: ObservableObject {
    let settings = SettingsStore()
    let usage = UsageStore()
    let quota = QuotaTracker()
    let cliStatus = CLIStatusStore()
    lazy var router = AIRouter(settings: settings, quota: quota)

    @Published var isWorking = false
    @Published var lastError: String?
    @Published var hasAccessibility = AXIsProcessTrusted()

    private let monitor = KeyMonitor()
    private var permissionTimer: Timer?

    func start() {
        monitor.onBufferChange = { [weak self] buffer in
            MainActor.assumeIsolated { self?.check(buffer) }
        }

        hasAccessibility = Self.isTrusted(prompt: true)
        if hasAccessibility { monitor.start() }

        // Pick up permission changes without needing a relaunch.
        permissionTimer = Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self else { return }
                let trusted = AXIsProcessTrusted()
                guard trusted != self.hasAccessibility else { return }
                self.hasAccessibility = trusted
                if trusted { self.monitor.start() } else { self.monitor.stop() }
            }
        }
    }

    // MARK: Trigger handling

    private func check(_ buffer: String) {
        guard settings.prefs.isEnabled, !isWorking else { return }
        guard let template = settings.activeTemplates.first(where: { buffer.hasSuffix($0.trigger) }) else { return }
        monitor.reset()
        Task { await run(template) }
    }

    private func run(_ template: Template) async {
        isWorking = true
        lastError = nil
        monitor.isPaused = true
        defer {
            isWorking = false
            monitor.isPaused = false
            monitor.reset()
        }

        let started = Date()
        var inputWords = 0
        let provider = settings.prefs.activeProvider
        var model = settings.activeSummary
        let hud = settings.prefs.showHUD
        router.onStatus = { message in if hud { HUDController.shared.working(message) } }

        do {
            // Let the target app finish inserting the trigger's last character.
            try await Task.sleep(for: .milliseconds(80))

            guard let captured = await TextBridge.capture(containing: template.trigger) else {
                throw InkwellError.noText
            }
            var text = captured.text
            if let r = text.range(of: template.trigger, options: .backwards) { text.removeSubrange(r) }
            let input = text.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !input.isEmpty else { throw InkwellError.noText }
            inputWords = input.split(whereSeparator: { $0.isWhitespace }).count

            if hud { HUDController.shared.working(template.name) }
            let result = try await router.complete(system: Prompts.system,
                                                   user: Prompts.user(template, text: input))
            model = result.model

            guard TextBridge.isSameElement(captured.element, TextBridge.focusedElement()) else {
                TextBridge.copyToClipboard(result.text)
                throw InkwellError.focusChanged
            }

            switch template.mode {
            case .replace:
                await TextBridge.replaceAll(with: result.text)
            case .append:
                await TextBridge.deleteBackward(template.trigger.count)
                await TextBridge.paste("\n\n" + result.text)
            }

            usage.record(UsageEvent(date: started, templateName: template.name, provider: provider.rawValue,
                                    model: model, inputTokens: result.inputTokens, outputTokens: result.outputTokens,
                                    latency: Date().timeIntervalSince(started), inputWords: inputWords, success: true,
                                    keyLabel: result.keyLabel))
            if hud { HUDController.shared.done("Done") }
            if settings.prefs.playSounds { NSSound(named: "Pop")?.play() }
        } catch {
            lastError = error.localizedDescription
            usage.record(UsageEvent(date: started, templateName: template.name, provider: provider.rawValue,
                                    model: model, inputTokens: 0, outputTokens: 0,
                                    latency: Date().timeIntervalSince(started), inputWords: inputWords, success: false,
                                    errorKind: (error as? AIError)?.shortKind ?? "capture"))
            if hud { HUDController.shared.failed(error.localizedDescription) }
            if settings.prefs.playSounds { NSSound.beep() }
        }
    }

    // MARK: Permissions & system

    static func isTrusted(prompt: Bool) -> Bool {
        let key = kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String
        return AXIsProcessTrustedWithOptions([key: prompt] as CFDictionary)
    }

    func openAccessibilitySettings() {
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility") {
            NSWorkspace.shared.open(url)
        }
    }

    var launchAtLogin: Bool {
        get { SMAppService.mainApp.status == .enabled }
        set {
            do {
                if newValue { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
            } catch {
                lastError = "Launch at login: \(error.localizedDescription)"
            }
            objectWillChange.send()
        }
    }
}
