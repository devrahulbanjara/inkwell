import AppKit
import SwiftUI

/// A small floating glass pill shown at the top of the screen while the AI works —
/// like a tiny Dynamic Island. It never takes focus, so typing is never interrupted.
@MainActor
final class HUDModel: ObservableObject {
    enum Phase: Equatable {
        case hidden
        case working(String)
        case done(String)
        case failed(String)
    }
    @Published var phase: Phase = .hidden
}

@MainActor
final class HUDController {
    static let shared = HUDController()

    private let model = HUDModel()
    private var panel: NSPanel?
    private var hideTask: Task<Void, Never>?

    func working(_ title: String) { show(.working(title)) }
    func done(_ title: String) { show(.done(title), autoHide: 0.9) }
    func failed(_ message: String) { show(.failed(message), autoHide: 2.2) }

    private func show(_ phase: HUDModel.Phase, autoHide: Double? = nil) {
        hideTask?.cancel()
        let panel = ensurePanel()
        position(panel)
        withAnimation(.spring(response: 0.38, dampingFraction: 0.78)) { model.phase = phase }
        if !panel.isVisible {
            panel.alphaValue = 0
            panel.orderFrontRegardless()
            NSAnimationContext.runAnimationGroup { ctx in
                ctx.duration = 0.18
                panel.animator().alphaValue = 1
            }
        }
        if let autoHide {
            hideTask = Task { [weak self] in
                try? await Task.sleep(for: .seconds(autoHide))
                guard !Task.isCancelled else { return }
                self?.hide()
            }
        }
    }

    func hide() {
        guard let panel else { return }
        withAnimation(.smooth(duration: 0.25)) { model.phase = .hidden }
        NSAnimationContext.runAnimationGroup({ ctx in
            ctx.duration = 0.25
            panel.animator().alphaValue = 0
        }, completionHandler: {
            Task { @MainActor in
                if self.model.phase == .hidden { panel.orderOut(nil) }
            }
        })
    }

    private func ensurePanel() -> NSPanel {
        if let panel { return panel }
        let p = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 360, height: 70),
                        styleMask: [.nonactivatingPanel, .borderless],
                        backing: .buffered, defer: true)
        p.isFloatingPanel = true
        p.level = .statusBar
        p.backgroundColor = .clear
        p.isOpaque = false
        p.hasShadow = false
        p.ignoresMouseEvents = true
        p.hidesOnDeactivate = false
        p.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        let host = NSHostingView(rootView: HUDView(model: model))
        host.frame = p.contentRect(forFrameRect: p.frame)
        host.autoresizingMask = [.width, .height]
        p.contentView = host
        panel = p
        return p
    }

    /// Top-centre of the screen the mouse is on, just under the menu bar.
    private func position(_ panel: NSPanel) {
        let mouse = NSEvent.mouseLocation
        let screen = NSScreen.screens.first { $0.frame.contains(mouse) } ?? NSScreen.main
        guard let visible = screen?.visibleFrame else { return }
        let size = panel.frame.size
        panel.setFrameOrigin(NSPoint(x: visible.midX - size.width / 2, y: visible.maxY - size.height - 6))
    }
}

private struct HUDView: View {
    @ObservedObject var model: HUDModel

    var body: some View {
        ZStack {
            if model.phase != .hidden {
                pill
                    .transition(.scale(scale: 0.6, anchor: .top).combined(with: .opacity))
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
        .padding(.top, 8)
    }

    private var pill: some View {
        HStack(spacing: 9) {
            icon
                .font(.system(size: 14, weight: .semibold))
                .frame(width: 18)
            Text(title)
                .font(.system(size: 13, weight: .medium, design: .rounded))
                .lineLimit(1)
                .contentTransition(.interpolate)
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 10)
        .glassCapsule(tint: tint)
        .fixedSize()
    }

    @ViewBuilder private var icon: some View {
        switch model.phase {
        case .working:
            Image(systemName: "sparkles")
                .foregroundStyle(LinearGradient(colors: [.purple, .indigo, .cyan], startPoint: .leading, endPoint: .trailing))
                .symbolEffect(.variableColor.iterative.reversing, options: .repeating)
        case .done:
            Image(systemName: "checkmark.circle.fill")
                .foregroundStyle(.green)
                .symbolEffect(.bounce, value: model.phase)
        case .failed:
            Image(systemName: "exclamationmark.triangle.fill")
                .foregroundStyle(.orange)
        case .hidden:
            EmptyView()
        }
    }

    private var title: String {
        switch model.phase {
        case .working(let t): return "\(t)…"
        case .done(let t): return t
        case .failed(let m): return m
        case .hidden: return ""
        }
    }

    private var tint: Color? {
        switch model.phase {
        case .working: return .purple
        case .failed: return .orange
        default: return nil
        }
    }
}
