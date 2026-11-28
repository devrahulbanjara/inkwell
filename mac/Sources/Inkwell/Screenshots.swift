import AppKit
import SwiftUI

/// Renders the app's main screens to PNGs with sample data, for the README.
/// Run with `INKWELL_SCREENSHOTS=/path/to/dir swift run`. Uses throwaway stores, so it never
/// touches your real settings, Keychain or usage stats.
@MainActor
enum Screenshots {
    static func runIfRequested() -> Bool {
        guard let dir = ProcessInfo.processInfo.environment["INKWELL_SCREENSHOTS"], !dir.isEmpty else { return false }
        let out = URL(fileURLWithPath: dir, isDirectory: true)
        try? FileManager.default.createDirectory(at: out, withIntermediateDirectories: true)
        let app = demoState()
        let bar = StatusBarController(app: app)
        keepAlive = bar

        Task { @MainActor in
            try? await Task.sleep(for: .milliseconds(800))
            for dark in [false, true] {
                NSApp.appearance = NSAppearance(named: dark ? .darkAqua : .aqua)
                bar.setAppearance(NSAppearance(named: dark ? .darkAqua : .aqua))
                let mode = dark ? "dark" : "light"

                app.popoverPage = .dashboard
                bar.showPopover(page: .dashboard)
                await settle()
                capture(window(bar.popoverWindowNumber), to: out.appendingPathComponent("dashboard-\(mode).png"))

                withAnimation(nil) { app.popoverPage = .provider(.gemini, addKey: false) }
                await settle()
                capture(window(bar.popoverWindowNumber), to: out.appendingPathComponent("providers-\(mode).png"))
                bar.closeAll()
                app.popoverPage = .dashboard

                SettingsWindow.shared.show(app, tab: .providers)
                await settle()
                capture(window(SettingsWindow.shared.windowNumber), to: out.appendingPathComponent("settings-\(mode).png"))
                SettingsWindow.shared.tab = .templates
                await settle()
                capture(window(SettingsWindow.shared.windowNumber), to: out.appendingPathComponent("templates-\(mode).png"))
                SettingsWindow.shared.close()

                HUDController.shared.working("Fixing grammar")
                await settle()
                if let hud = ourWindows().min(by: { $0.bounds.width * $0.bounds.height < $1.bounds.width * $1.bounds.height }) {
                    capture(hud, to: out.appendingPathComponent("hud-\(mode).png"))
                }
                HUDController.shared.hide()

                // The right-click menu: capture the menu bar region around the icon plus the open menu.
                let menuShot = out.appendingPathComponent("menu-\(mode).png")
                let timer = Timer(timeInterval: 1.0, repeats: false) { _ in
                    MainActor.assumeIsolated {
                        captureMenu(iconFrame: bar.iconFrame, to: menuShot)
                        bar.closeAll()
                    }
                }
                RunLoop.main.add(timer, forMode: .common)
                bar.showMenu() // blocks in menu tracking until the timer closes it
                try? await Task.sleep(for: .milliseconds(400))
            }
            NSApp.terminate(nil)
        }
        return true
    }

    private static var keepAlive: StatusBarController?

    private static func settle() async { try? await Task.sleep(for: .milliseconds(1400)) }

    struct WindowInfo { let id: CGWindowID; let bounds: CGRect; let layer: Int }

    /// On-screen windows owned by this process (IDs and bounds don't need Screen Recording access).
    private static func ourWindows() -> [WindowInfo] {
        let pid = ProcessInfo.processInfo.processIdentifier
        let list = CGWindowListCopyWindowInfo([.optionOnScreenOnly], kCGNullWindowID) as? [[String: Any]] ?? []
        return list.compactMap { w in
            guard (w[kCGWindowOwnerPID as String] as? Int32) == pid,
                  let id = w[kCGWindowNumber as String] as? CGWindowID,
                  let b = w[kCGWindowBounds as String] as? [String: CGFloat],
                  let layer = w[kCGWindowLayer as String] as? Int else { return nil }
            let rect = CGRect(x: b["X"] ?? 0, y: b["Y"] ?? 0, width: b["Width"] ?? 0, height: b["Height"] ?? 0)
            guard rect.width > 60, rect.height > 30 else { return nil } // skip the status item itself
            return WindowInfo(id: id, bounds: rect, layer: layer)
        }
    }

    private static func window(_ number: Int?) -> WindowInfo? {
        guard let number else { return nil }
        return WindowInfo(id: CGWindowID(number), bounds: .zero, layer: 0)
    }

    private static func capture(_ window: WindowInfo?, to url: URL) {
        guard let window else { print("screenshot: no window for \(url.lastPathComponent)"); return }
        run(["-x", "-l", String(window.id), url.path])
    }

    private static func captureMenu(iconFrame: NSRect?, to url: URL) {
        guard let screen = NSScreen.main, let icon = iconFrame else { return }
        let menus = ourWindows().filter { $0.layer > 0 && $0.layer != NSWindow.Level.statusBar.rawValue }
        guard let menu = menus.max(by: { $0.bounds.height < $1.bounds.height }) else {
            print("screenshot: menu window not found"); return
        }
        // Cocoa (bottom-left) → screen capture (top-left) coordinates.
        let iconTop = CGRect(x: icon.minX, y: screen.frame.height - icon.maxY, width: icon.width, height: icon.height)
        let region = menu.bounds.union(iconTop).insetBy(dx: -14, dy: -10)
        let r = region.intersection(CGRect(origin: .zero, size: screen.frame.size))
        run(["-x", "-R\(Int(r.minX)),\(Int(r.minY)),\(Int(r.width)),\(Int(r.height))", url.path])
    }

    private static func run(_ args: [String]) {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/sbin/screencapture")
        p.arguments = args
        try? p.run()
        p.waitUntilExit()
    }

    private static func demoState() -> AppState {
        let settings = SettingsStore(ephemeral: true)
        settings.addKey(.gemini, secret: "AIzaSyDemoKeyNotReal0000000000000000x4Km", label: "Personal")
        settings.addKey(.gemini, secret: "AIzaSyDemoKeyNotReal00000000000000work7Q", label: "Side project")
        settings.addKey(.groq, secret: "gsk_DemoKeyNotReal000000000000000000x9Fd", label: "Main")
        settings.prefs.activeProvider = .gemini

        var rng = SeededRandom(seed: 42)
        var events: [UsageEvent] = []
        let templates = ["Fix grammar", "Fix grammar", "Fix grammar", "Rewrite", "Make formal", "Ask", "Reply", "Make shorter"]
        let cal = Calendar.current
        for dayOffset in 0..<30 {
            guard let day = cal.date(byAdding: .day, value: -dayOffset, to: Date()) else { continue }
            let runs = Int(rng.next() % 9) + (dayOffset % 7 < 5 ? 4 : 1)
            for _ in 0..<runs {
                let words = Int(rng.next() % 90) + 12
                let input = words * 2 + 140
                events.append(UsageEvent(date: day.addingTimeInterval(-Double(rng.next() % 30_000)),
                                         templateName: templates[Int(rng.next() % UInt64(templates.count))],
                                         provider: "gemini", model: "gemini-3.5-flash-lite",
                                         inputTokens: input, outputTokens: Int(Double(input) * 0.6),
                                         latency: 0.7 + Double(rng.next() % 900) / 1000, inputWords: words, success: true,
                                         keyLabel: "Personal"))
            }
        }
        let app = AppState(settings: settings, usage: UsageStore(ephemeral: true, events: events.sorted { $0.date < $1.date }))
        app.hasAccessibility = true
        app.cliStatus.frozen = true
        app.cliStatus.set(.claudeCode, CLIStatus(state: .ready, path: "~/.local/bin/claude", version: "2.4.1",
                                                 detail: "Signed in with Claude.ai"))
        app.cliStatus.set(.codex, CLIStatus(state: .loggedOut, path: "/opt/homebrew/bin/codex"))
        return app
    }
}

private struct SeededRandom {
    var state: UInt64
    init(seed: UInt64) { state = seed }
    mutating func next() -> UInt64 {
        state = state &* 6364136223846793005 &+ 1442695040888963407
        return state >> 33
    }
}
