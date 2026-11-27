import AppKit
import ApplicationServices

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

    static func capture(containing trigger: String) async -> Captured? {
        enableElectronAccessibility()
        let element = focusedElement()
        if let element, let value = stringValue(of: element), value.contains(trigger) {
            return Captured(text: value, element: element, viaClipboard: false)
        }
        if let copied = await copyAllViaClipboard(), copied.contains(trigger) {
            press(.right) // collapse the selection, cursor to the end
            try? await Task.sleep(for: .milliseconds(30))
            return Captured(text: copied, element: element, viaClipboard: true)
        }
        return nil
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

    /// Electron/Chromium apps only expose their text to Accessibility when asked.
    private static func enableElectronAccessibility() {
        guard let pid = NSWorkspace.shared.frontmostApplication?.processIdentifier else { return }
        let app = AXUIElementCreateApplication(pid)
        AXUIElementSetAttributeValue(app, "AXManualAccessibility" as CFString, kCFBooleanTrue)
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
