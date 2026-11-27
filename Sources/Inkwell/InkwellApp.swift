import AppKit
import SwiftUI

@main
struct InkwellApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate

    var body: some Scene {
        MenuBarExtra {
            DashboardView()
                .environmentObject(delegate.app)
                .environmentObject(delegate.app.settings)
                .environmentObject(delegate.app.usage)
                .environmentObject(delegate.app.quota)
                .environmentObject(delegate.app.cliStatus)
        } label: {
            MenuBarIcon(app: delegate.app, settings: delegate.app.settings)
        }
        .menuBarExtraStyle(.window)
    }
}

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    let app = AppState()

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.accessory) // no Dock icon, even when run with `swift run`
        app.start()
    }
}

struct MenuBarIcon: View {
    @ObservedObject var app: AppState
    @ObservedObject var settings: SettingsStore

    var body: some View {
        Image(systemName: symbol)
            .contentTransition(.symbolEffect(.replace))
            .symbolEffect(.pulse, isActive: app.isWorking)
    }

    private var symbol: String {
        if app.isWorking { return "sparkles" }
        if !settings.prefs.isEnabled || !app.hasAccessibility { return "pencil.slash" }
        return "pencil.and.scribble"
    }
}

/// Plain AppKit window for Settings — reliable for menu-bar-only apps.
@MainActor
final class SettingsWindow {
    static let shared = SettingsWindow()
    private var window: NSWindow?

    func show(_ app: AppState) {
        if window == nil {
            let root = SettingsView()
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
}
