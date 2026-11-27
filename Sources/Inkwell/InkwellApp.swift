import AppKit
import SwiftUI

@main
struct InkwellApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate

    var body: some Scene {
        // The menu bar item is an AppKit status item (see StatusBarController) so it can
        // show the dashboard on left-click and a quick-controls menu on right-click.
        Settings { EmptyView() }
    }
}

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private(set) lazy var app = AppState() // lazy so screenshot mode never loads real settings
    private var statusBar: StatusBarController?

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.accessory) // no Dock icon, even when run with `swift run`
        if Screenshots.runIfRequested() { return }
        statusBar = StatusBarController(app: app)
        app.start()
    }
}

struct MenuBarIcon: View {
    @ObservedObject var app: AppState
    @ObservedObject var settings: SettingsStore

    var body: some View {
        Image(systemName: symbol)
            .font(.system(size: 14, weight: .medium))
            .contentTransition(.symbolEffect(.replace))
            .symbolEffect(.pulse, isActive: app.isWorking)
            .frame(width: 22, height: 22)
    }

    private var symbol: String {
        if app.isWorking { return "sparkles" }
        if !settings.prefs.isEnabled || !app.hasAccessibility { return "pencil.slash" }
        return "pencil.and.scribble"
    }
}

/// Plain AppKit window for Settings — reliable for menu-bar-only apps.
@MainActor
final class SettingsWindow: ObservableObject {
    static let shared = SettingsWindow()
    @Published var tab: SettingsTab = .providers
    private var window: NSWindow?

    func show(_ app: AppState, tab: SettingsTab? = nil) {
        if let tab { self.tab = tab }
        if window == nil {
            let root = SettingsView(window: self)
                .environmentObject(app)
                .environmentObject(app.settings)
                .environmentObject(app.usage)
                .environmentObject(app.quota)
                .environmentObject(app.cliStatus)
            let w = NSWindow(contentViewController: NSHostingController(rootView: root))
            w.title = "Inkwell Settings"
            w.styleMask = [.titled, .closable, .miniaturizable]
            w.isReleasedWhenClosed = false
            w.center()
            window = w
        }
        NSApp.activate(ignoringOtherApps: true)
        window?.makeKeyAndOrderFront(nil)
    }

    func close() { window?.orderOut(nil) }
    var windowNumber: Int? { window?.windowNumber }
}
