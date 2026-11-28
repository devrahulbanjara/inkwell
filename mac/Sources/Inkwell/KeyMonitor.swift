import AppKit

/// Watches keystrokes system-wide (needs Accessibility permission) and keeps a short
/// rolling buffer of the last characters typed, so we can spot a trigger like "?fix".
/// Password fields are never seen: macOS enables Secure Input for them.
final class KeyMonitor {
    private var keyMonitor: Any?
    private var mouseMonitor: Any?
    private(set) var buffer = ""
    private let maxLength = 64

    var isPaused = false
    var onBufferChange: ((String) -> Void)?

    var isRunning: Bool { keyMonitor != nil }

    func start() {
        guard keyMonitor == nil else { return }
        keyMonitor = NSEvent.addGlobalMonitorForEvents(matching: .keyDown) { [weak self] event in
            self?.handle(event)
        }
        mouseMonitor = NSEvent.addGlobalMonitorForEvents(matching: [.leftMouseDown, .rightMouseDown]) { [weak self] _ in
            self?.reset() // cursor probably moved
        }
    }

    func stop() {
        if let m = keyMonitor { NSEvent.removeMonitor(m) }
        if let m = mouseMonitor { NSEvent.removeMonitor(m) }
        keyMonitor = nil
        mouseMonitor = nil
        reset()
    }

    func reset() { buffer = "" }

    private func handle(_ event: NSEvent) {
        guard !isPaused else { return }
        let flags = event.modifierFlags.intersection(.deviceIndependentFlagsMask)
        if flags.contains(.command) || flags.contains(.control) {
            reset()
            return
        }
        switch event.keyCode {
        case 51: // delete
            if flags.contains(.option) { reset() } else if !buffer.isEmpty { buffer.removeLast() }
            return
        case 36, 76, 48, 53, 115, 116, 117, 119, 121, 123, 124, 125, 126:
            // return, enter, tab, esc, home, page up, fwd delete, end, page down, arrows
            reset()
            return
        default:
            break
        }
        guard let chars = event.characters, !chars.isEmpty else { return }
        buffer.append(chars)
        if buffer.count > maxLength { buffer = String(buffer.suffix(maxLength)) }
        onBufferChange?(buffer)
    }
}
