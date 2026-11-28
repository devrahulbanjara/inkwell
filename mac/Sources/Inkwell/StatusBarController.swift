import AppKit
import SwiftUI

/// Owns the menu bar item. Left-click opens the dashboard popover; right-click (or ⌃-click)
/// opens a native menu with the controls people reach for most: pause, provider, model, keys and triggers.
@MainActor
final class StatusBarController: NSObject, NSMenuDelegate {
    private let app: AppState
    private let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
    private let popover = NSPopover()
    private var logoCache: [ProviderKind: NSImage] = [:]

    init(app: AppState) {
        self.app = app
        super.init()

        if let button = item.button {
            let icon = PassthroughHostingView(rootView: MenuBarIcon(app: app, settings: app.settings))
            icon.frame = NSRect(x: 0, y: 0, width: 24, height: 22)
            button.frame = icon.frame
            button.addSubview(icon)
            item.length = 24
            button.target = self
            button.action = #selector(clicked)
            button.sendAction(on: [.leftMouseUp, .rightMouseUp])
            button.setAccessibilityLabel("Inkwell")
        }

        let root = DashboardView()
            .environmentObject(app)
            .environmentObject(app.settings)
            .environmentObject(app.usage)
            .environmentObject(app.quota)
            .environmentObject(app.cliStatus)
        let host = NSHostingController(rootView: root)
        host.sizingOptions = .preferredContentSize
        popover.contentViewController = host
        popover.behavior = .transient
        popover.animates = true
    }

    // MARK: Clicks

    @objc private func clicked() {
        guard let event = NSApp.currentEvent else { return togglePopover() }
        let isRight = event.type == .rightMouseUp || event.modifierFlags.contains(.control)
        if isRight { showMenu() } else { togglePopover() }
    }

    private func togglePopover() {
        if popover.isShown {
            popover.performClose(nil)
        } else {
            showPopover()
        }
    }

    func showPopover(page: AppState.PopoverPage? = nil) {
        if let page { app.popoverPage = page }
        guard let button = item.button else { return }
        NSApp.activate(ignoringOtherApps: true)
        if !popover.isShown {
            popover.show(relativeTo: button.bounds, of: button, preferredEdge: .minY)
        }
        popover.contentViewController?.view.window?.makeKey()
    }

    func showMenu() {
        popover.performClose(nil)
        let menu = buildMenu()
        menu.delegate = self
        item.menu = menu
        item.button?.performClick(nil)
    }

    /// Screen-space frame of the menu bar icon (Cocoa coordinates).
    var iconFrame: NSRect? {
        guard let button = item.button, let window = button.window else { return nil }
        return window.convertToScreen(button.convert(button.bounds, to: nil))
    }

    func setAppearance(_ appearance: NSAppearance?) { popover.appearance = appearance }

    var popoverWindowNumber: Int? { popover.contentViewController?.view.window?.windowNumber }

    func closeAll() {
        item.menu?.cancelTracking()
        popover.performClose(nil)
    }

    func menuDidClose(_ menu: NSMenu) {
        item.menu = nil // so the next left-click opens the popover again
    }

    // MARK: Menu

    private var settings: SettingsStore { app.settings }

    private func buildMenu() -> NSMenu {
        let menu = NSMenu()
        menu.autoenablesItems = false
        let active = settings.prefs.activeProvider

        menu.addItem(header(statusLine))

        if !app.hasAccessibility {
            menu.addItem(action("Allow Accessibility Access…", symbol: "hand.raised") { [weak self] in
                self?.app.openAccessibilitySettings()
            })
        }

        let paused = !settings.prefs.isEnabled
        menu.addItem(action(paused ? "Resume Inkwell" : "Pause Inkwell",
                            symbol: paused ? "play.circle" : "pause.circle", key: "p") { [weak self] in
            self?.settings.prefs.isEnabled.toggle()
        })

        menu.addItem(.separator())

        let providerItem = submenu("Provider", image: logo(active))
        for kind in ProviderKind.allCases {
            let ready = isUsable(kind)
            let mi = action(kind.displayName + (ready ? "" : "  —  " + setupHint(kind))) { [weak self] in
                self?.activate(kind)
            }
            mi.image = logo(kind)
            mi.state = kind == active ? .on : .off
            providerItem.submenu?.addItem(mi)
        }
        menu.addItem(providerItem)

        if let modelItem = modelMenu(for: active) { menu.addItem(modelItem) }
        if active.usesAPIKeys { menu.addItem(keysMenu(for: active)) }

        menu.addItem(triggersMenu())

        menu.addItem(.separator())
        if let v = app.updates.availableVersion {
            menu.addItem(action("Update to Inkwell \(v)…", symbol: "arrow.down.circle") { [weak self] in
                self?.app.updates.install()
            })
        }
        menu.addItem(action("Open Dashboard", symbol: "chart.bar") { [weak self] in
            self?.showPopover(page: .dashboard)
        })
        menu.addItem(action("Settings…", symbol: "gearshape", key: ",") { [weak self] in
            guard let self else { return }
            SettingsWindow.shared.show(self.app)
        })
        menu.addItem(.separator())
        menu.addItem(action("Quit Inkwell", key: "q") { NSApp.terminate(nil) })
        return menu
    }

    private var statusLine: String {
        if app.isWorking { return "Working…" }
        if !app.hasAccessibility { return "Needs Accessibility access" }
        if !settings.prefs.isEnabled { return "Paused" }
        return "Listening · \(settings.prefs.activeProvider.displayName)"
    }

    private func modelMenu(for kind: ProviderKind) -> NSMenuItem? {
        let item = submenu("Model", symbol: "cpu")
        guard let sub = item.submenu else { return nil }
        switch kind {
        case .gemini, .groq:
            let order = settings.effectiveModels(kind)
            let catalog = ModelCatalog.models(for: kind)
            let custom = order.filter { id in !catalog.contains { $0.id == id } }
            for m in catalog {
                sub.addItem(modelItem(kind, id: m.id, title: m.name, order: order))
            }
            for id in custom {
                sub.addItem(modelItem(kind, id: id, title: id, order: order))
            }
            sub.addItem(.separator())
            let note = NSMenuItem(title: "✓ primary   – fallback", action: nil, keyEquivalent: "")
            note.isEnabled = false
            sub.addItem(note)
        case .claudeCode:
            let current = settings.cli(kind).model
            for m in ModelCatalog.claude {
                let mi = action(m.name) { [weak self] in self?.settings.updateCLI(kind) { $0.model = m.id } }
                mi.state = m.id == current ? .on : .off
                mi.toolTip = m.note
                sub.addItem(mi)
            }
        case .codex:
            let m = settings.cli(kind).model
            let mi = NSMenuItem(title: m.isEmpty ? "Default from Codex config" : m, action: nil, keyEquivalent: "")
            mi.state = .on
            mi.isEnabled = false
            sub.addItem(mi)
        }
        if let limit = app.quota.cliLimitSummary(kind) {
            sub.addItem(.separator())
            let note = NSMenuItem(title: limit, action: nil, keyEquivalent: "")
            note.isEnabled = false
            sub.addItem(note)
        }
        sub.addItem(.separator())
        sub.addItem(action("Manage Models…") { [weak self] in
            self?.showPopover(page: .provider(kind, addKey: false))
        })
        return item
    }

    private func modelItem(_ kind: ProviderKind, id: String, title: String, order: [String]) -> NSMenuItem {
        let mi = action(title) { [weak self] in self?.settings.setPrimaryModel(kind, id) }
        if order.first == id { mi.state = .on } else if order.contains(id) { mi.state = .mixed }
        mi.toolTip = id
        return mi
    }

    private func keysMenu(for kind: ProviderKind) -> NSMenuItem {
        let item = submenu("API Keys", symbol: "key")
        guard let sub = item.submenu else { return item }
        let cfg = settings.api(kind)
        let models = settings.effectiveModels(kind)
        if cfg.keys.isEmpty {
            let none = NSMenuItem(title: "No \(kind.displayName) keys yet", action: nil, keyEquivalent: "")
            none.isEnabled = false
            sub.addItem(none)
        }
        for meta in cfg.keys {
            let badge = QuickProviderView.badge(meta.isEnabled ? app.quota.status(of: meta.id, models: models) : nil)
            let mi = action("\(meta.label)  ••\(meta.hint)  ·  \(badge.text)") { [weak self] in
                guard let self else { return }
                let enable = !meta.isEnabled
                self.settings.setKeyEnabled(kind, id: meta.id, enable)
                if enable { self.app.quota.clearInvalid(meta.id) }
            }
            mi.state = meta.isEnabled ? .on : .off
            mi.toolTip = (meta.isEnabled ? "Click to turn off. " : "Click to turn on. ") + badge.help
            sub.addItem(mi)
        }
        sub.addItem(.separator())
        sub.addItem(action("Add Key…", symbol: "plus") { [weak self] in
            self?.showPopover(page: .provider(kind, addKey: true))
        })
        if let url = kind.keyURL {
            sub.addItem(action("Get a Free \(kind.displayName) Key", symbol: "arrow.up.forward.square") {
                NSWorkspace.shared.open(url)
            })
        }
        return item
    }

    private func triggersMenu() -> NSMenuItem {
        let item = submenu("Triggers", symbol: "text.cursor")
        guard let sub = item.submenu else { return item }
        for t in settings.prefs.templates {
            let mi = action("\(t.trigger)   \(t.name)") { [weak self] in
                guard let self, let i = self.settings.prefs.templates.firstIndex(where: { $0.id == t.id }) else { return }
                self.settings.prefs.templates[i].isEnabled.toggle()
            }
            mi.state = t.isEnabled ? .on : .off
            mi.toolTip = t.prompt
            sub.addItem(mi)
        }
        sub.addItem(.separator())
        sub.addItem(action("Edit Triggers…") { [weak self] in
            guard let self else { return }
            SettingsWindow.shared.show(self.app, tab: .templates)
        })
        return item
    }

    // MARK: Provider helpers

    private func isUsable(_ kind: ProviderKind) -> Bool {
        kind.usesAPIKeys ? settings.hasUsableKey(kind) : app.cliStatus.status(kind).state == .ready
    }

    private func setupHint(_ kind: ProviderKind) -> String {
        if kind.usesAPIKeys { return "add a key" }
        switch app.cliStatus.status(kind).state {
        case .notInstalled: return "not installed"
        case .loggedOut: return "not signed in"
        default: return "set up"
        }
    }

    private func activate(_ kind: ProviderKind) {
        if kind.usesAPIKeys && !settings.hasUsableKey(kind) {
            showPopover(page: .provider(kind, addKey: true))
            return
        }
        Task {
            if await ProviderActions.activate(kind, settings: settings, cli: app.cliStatus) != nil {
                showPopover(page: .provider(kind, addKey: false))
            }
        }
    }

    private func logo(_ kind: ProviderKind) -> NSImage? {
        if let cached = logoCache[kind] { return cached }
        let renderer = ImageRenderer(content: ProviderLogo(kind: kind, size: 16))
        renderer.scale = NSScreen.main?.backingScaleFactor ?? 2
        guard let image = renderer.nsImage else { return nil }
        logoCache[kind] = image
        return image
    }

    // MARK: Menu item factories

    private func header(_ title: String) -> NSMenuItem {
        let mi = NSMenuItem(title: title, action: nil, keyEquivalent: "")
        mi.isEnabled = false
        return mi
    }

    private func submenu(_ title: String, symbol: String? = nil, image: NSImage? = nil) -> NSMenuItem {
        let mi = NSMenuItem(title: title, action: nil, keyEquivalent: "")
        mi.image = image ?? symbol.flatMap { NSImage(systemSymbolName: $0, accessibilityDescription: nil) }
        mi.submenu = NSMenu(title: title)
        mi.submenu?.autoenablesItems = false
        return mi
    }

    private func action(_ title: String, symbol: String? = nil, key: String = "",
                        _ handler: @escaping () -> Void) -> NSMenuItem {
        let mi = ClosureMenuItem(title: title, keyEquivalent: key, handler: handler)
        if let symbol { mi.image = NSImage(systemSymbolName: symbol, accessibilityDescription: nil) }
        return mi
    }
}

/// NSMenuItem that runs a closure, avoiding a selector per item.
private final class ClosureMenuItem: NSMenuItem {
    private let handler: () -> Void

    init(title: String, keyEquivalent: String, handler: @escaping () -> Void) {
        self.handler = handler
        super.init(title: title, action: #selector(run), keyEquivalent: keyEquivalent)
        target = self
    }

    required init(coder: NSCoder) { fatalError("not used") }

    @objc private func run() { handler() }
}

/// Hosts the animated SwiftUI icon inside the status bar button without swallowing its clicks.
private final class PassthroughHostingView<Content: View>: NSHostingView<Content> {
    override func hitTest(_ point: NSPoint) -> NSView? { nil }
}
