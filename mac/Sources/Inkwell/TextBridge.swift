import AppKit
import ApplicationServices
import os

/// Reads and writes text in whatever app currently has keyboard focus.
/// Strategy: read via the Accessibility API when the app supports it; otherwise fall back
/// to ⌘A / ⌘C. Writing always goes through the pasteboard (⌘V) so the target app's
/// Undo works, and the user's clipboard is restored afterwards.
@MainActor
enum TextBridge {
    struct Captured {
        let text: String
        let element: AXUIElement?
        let viaClipboard: Bool
    }

    // MARK: Reading

    private static let log = Logger(subsystem: "io.github.inkwell", category: "capture")

    static func capture(containing trigger: String) async -> Captured? {
        let app = NSWorkspace.shared.frontmostApplication
        let appID = app?.bundleIdentifier ?? "unknown"
        enableAccessibility(for: app)

        // Browsers (Brave, Chrome, Edge, Arc…) update the text they report to Accessibility a moment
        // after the keystroke lands, and build their accessibility tree lazily. So read a few times
        // before giving up on Accessibility.
        var element: AXUIElement?
        var sawValue = false
        for attempt in 0..<8 {
            element = focusedElement()
            if let element, let value = stringValue(of: element) {
                sawValue = true
                if value.contains(trigger) {
                    log.info("Read \(value.count) chars via Accessibility in \(appID, privacy: .public) (attempt \(attempt + 1))")
                    return Captured(text: value, element: element, viaClipboard: false)
                }
            }
            if attempt < 7 { try? await Task.sleep(for: .milliseconds(50)) }
        }
        log.info("Accessibility read missed the trigger in \(appID, privacy: .public): element=\(element != nil, privacy: .public) value=\(sawValue, privacy: .public) role=\(element.flatMap(role(of:)) ?? "none", privacy: .public)")

        guard let copied = await copyAllViaClipboard() else {
            log.error("Clipboard fallback copied nothing in \(appID, privacy: .public)")
            return nil
        }
        // ⌘A leaves everything selected; collapse it so the cursor goes back to the end either way.
        press(.right)
        try? await Task.sleep(for: .milliseconds(30))
        guard copied.contains(trigger) else {
            log.error("Clipboard fallback got \(copied.count) chars without the trigger in \(appID, privacy: .public)")
            return nil
        }
        log.info("Read \(copied.count) chars via clipboard in \(appID, privacy: .public)")
        return Captured(text: copied, element: element, viaClipboard: true)
    }

    static func focusedElement() -> AXUIElement? {
        let system = AXUIElementCreateSystemWide()
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(system, kAXFocusedUIElementAttribute as CFString, &value) == .success,
              let value, CFGetTypeID(value) == AXUIElementGetTypeID() else { return nil }
        return unsafeBitCast(value, to: AXUIElement.self)
    }

    static func isSameElement(_ a: AXUIElement?, _ b: AXUIElement?) -> Bool {
        guard let a, let b else { return true } // can't tell — assume yes
        return CFEqual(a, b)
    }

    private static func stringValue(of element: AXUIElement) -> String? {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, kAXValueAttribute as CFString, &value) == .success else { return nil }
        return value as? String
    }

    private static func role(of element: AXUIElement) -> String? {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, kAXRoleAttribute as CFString, &value) == .success else { return nil }
        return value as? String
    }

    /// Chromium browsers and Electron apps only expose web-page text to Accessibility when asked.
    /// `AXManualAccessibility` is the quiet way to ask. Browsers that ignore it get `AXEnhancedUserInterface`,
    /// the flag VoiceOver sets; it's only sent to known browsers because it can slow window animations.
    static func enableAccessibility(for app: NSRunningApplication?) {
        guard let app, app.processIdentifier != ProcessInfo.processInfo.processIdentifier else { return }
        let element = AXUIElementCreateApplication(app.processIdentifier)
        AXUIElementSetAttributeValue(element, "AXManualAccessibility" as CFString, kCFBooleanTrue)
        if let id = app.bundleIdentifier, chromiumBrowsers.contains(id) {
            AXUIElementSetAttributeValue(element, "AXEnhancedUserInterface" as CFString, kCFBooleanTrue)
        }
    }

    private static let chromiumBrowsers: Set<String> = [
        "com.google.Chrome", "com.google.Chrome.beta", "com.google.Chrome.dev", "com.google.Chrome.canary",
        "com.brave.Browser", "com.brave.Browser.beta", "com.brave.Browser.nightly",
        "com.microsoft.edgemac", "com.microsoft.edgemac.Beta", "company.thebrowser.Browser",
        "com.operasoftware.Opera", "com.vivaldi.Vivaldi", "org.chromium.Chromium", "ai.perplexity.comet",
    ]

    private static var appObserver: NSObjectProtocol?

    /// Ask each app to turn on its accessibility tree as soon as it comes to the front,
    /// so it's already built by the time a trigger is typed.
    static func watchFrontmostApp() {
        guard appObserver == nil else { return }
        enableAccessibility(for: NSWorkspace.shared.frontmostApplication)
        appObserver = NSWorkspace.shared.notificationCenter.addObserver(
            forName: NSWorkspace.didActivateApplicationNotification, object: nil, queue: .main
        ) { note in
            let app = note.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication
            MainActor.assumeIsolated { enableAccessibility(for: app) }
        }
    }

    private static func copyAllViaClipboard() async -> String? {
        let pb = NSPasteboard.general
        let saved = snapshot(pb)
        let before = pb.changeCount
        press(.a, command: true)
        try? await Task.sleep(for: .milliseconds(40))
        press(.c, command: true)
        for _ in 0..<25 where pb.changeCount == before {
            try? await Task.sleep(for: .milliseconds(20))
        }
        let text = pb.changeCount != before ? pb.string(forType: .string) : nil
        restore(saved, to: pb)
        return text
    }

    // MARK: Writing

    static func replaceAll(with text: String) async {
        press(.a, command: true)
        try? await Task.sleep(for: .milliseconds(40))
        await paste(text)
    }

    static func deleteBackward(_ count: Int) async {
        for _ in 0..<count { press(.delete) }
        try? await Task.sleep(for: .milliseconds(30))
    }

    static func paste(_ text: String) async {
        let pb = NSPasteboard.general
        let saved = snapshot(pb)
        pb.clearContents()
        pb.setString(text, forType: .string)
        // Ask clipboard managers (Maccy, Raycast, Paste…) to ignore this temporary entry.
        pb.setData(Data(), forType: NSPasteboard.PasteboardType("org.nspasteboard.TransientType"))
        press(.v, command: true)
        try? await Task.sleep(for: .milliseconds(250))
        restore(saved, to: pb)
    }

    static func copyToClipboard(_ text: String) {
        let pb = NSPasteboard.general
        pb.clearContents()
        pb.setString(text, forType: .string)
    }

    // MARK: Keystrokes

    enum Key: CGKeyCode {
        case a = 0x00, c = 0x08, v = 0x09, delete = 0x33, right = 0x7C
    }

    static func press(_ key: Key, command: Bool = false) {
        let source = CGEventSource(stateID: .combinedSessionState)
        let down = CGEvent(keyboardEventSource: source, virtualKey: key.rawValue, keyDown: true)
        let up = CGEvent(keyboardEventSource: source, virtualKey: key.rawValue, keyDown: false)
        let flags: CGEventFlags = command ? .maskCommand : []
        down?.flags = flags
        up?.flags = flags
        down?.post(tap: .cghidEventTap)
        up?.post(tap: .cghidEventTap)
    }

    // MARK: Clipboard snapshot

    private typealias Snapshot = [[NSPasteboard.PasteboardType: Data]]

    private static func snapshot(_ pb: NSPasteboard) -> Snapshot {
        (pb.pasteboardItems ?? []).map { item in
            var dict: [NSPasteboard.PasteboardType: Data] = [:]
            for type in item.types {
                if let data = item.data(forType: type) { dict[type] = data }
            }
            return dict
        }
    }

    private static func restore(_ snap: Snapshot, to pb: NSPasteboard) {
        pb.clearContents()
        guard !snap.isEmpty else { return }
        let items = snap.map { dict -> NSPasteboardItem in
            let item = NSPasteboardItem()
            for (type, data) in dict { item.setData(data, forType: type) }
            return item
        }
        pb.writeObjects(items)
    }
}
