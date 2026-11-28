import AppKit
import SwiftUI

/// The floating "AI is working" pill. A colourful ring spins clockwise around it while the AI works, the label
/// shimmers ("Fixing grammar…"), then it turns green with a check, or red with a clear two-line message.
/// It floats just above the field you're typing in and never takes focus, so typing is never interrupted.
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
    /// Field (or caret) to float above, in Cocoa screen coordinates. Nil = top-centre of the screen.
    private var anchorRect: CGRect?

    private static let size = NSSize(width: 520, height: 150)

    func working(_ title: String) { show(.working(title)) }
    func done(_ title: String) { show(.done(title), autoHide: 1.1) }
    func failed(_ message: String) { show(.failed(message), autoHide: min(7, 3 + Double(message.count) * 0.03)) }

    /// Sets where the pill should appear (the field being edited). Call before `working`.
    func anchor(to rect: CGRect?) { anchorRect = rect }

    private func show(_ phase: HUDModel.Phase, autoHide: Double? = nil) {
        hideTask?.cancel()
        let panel = ensurePanel()
        position(panel)
        withAnimation(.spring(response: 0.42, dampingFraction: 0.72)) { model.phase = phase }
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
                if self.model.phase == .hidden {
                    panel.orderOut(nil)
                    self.anchorRect = nil
                }
            }
        })
    }

    private func ensurePanel() -> NSPanel {
        if let panel { return panel }
        let p = NSPanel(contentRect: NSRect(origin: .zero, size: Self.size),
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

    /// Centres the pill just above the field (or just below it when there's no room above);
    /// without a field, top-centre of the screen under the mouse, just under the menu bar.
    private func position(_ panel: NSPanel) {
        let size = panel.frame.size
        let pillHalf: CGFloat = 26
        let gap: CGFloat = 6
        if let a = anchorRect, let screen = NSScreen.screens.first(where: { $0.frame.intersects(a) }) ?? NSScreen.main {
            let visible = screen.visibleFrame
            var midY = a.maxY + gap + pillHalf
            if midY + pillHalf > visible.maxY { midY = a.minY - gap - pillHalf }
            let x = min(max(a.midX - size.width / 2, visible.minX - 60), visible.maxX - size.width + 60)
            panel.setFrameOrigin(NSPoint(x: x, y: midY - size.height / 2))
            return
        }
        let mouse = NSEvent.mouseLocation
        let screen = NSScreen.screens.first { $0.frame.contains(mouse) } ?? NSScreen.main
        guard let visible = screen?.visibleFrame else { return }
        panel.setFrameOrigin(NSPoint(x: visible.midX - size.width / 2, y: visible.maxY - 6 - pillHalf * 2 - (size.height / 2 - pillHalf)))
    }
}

/// "All Gemini keys hit today's free limit. Next available…" → bold title + detail line.
enum MessageText {
    static func split(_ message: String) -> (title: String, detail: String) {
        let m = message.trimmingCharacters(in: .whitespacesAndNewlines)
        if let r = m.range(of: ". "), m.distance(from: m.startIndex, to: r.lowerBound) < 70 {
            return (String(m[..<r.lowerBound]), String(m[r.upperBound...]).trimmingCharacters(in: .whitespaces))
        }
        if m.count <= 60 { return (m.hasSuffix(".") ? String(m.dropLast()) : m, "") }
        return ("Couldn't finish", m)
    }

    /// Limits and cooldowns read as warnings (orange); everything else as errors (red).
    static func isWarning(_ message: String) -> Bool {
        let l = message.lowercased()
        return l.contains("limit") || l.contains("cooling down") || l.contains("rate-limited") || l.contains("switched fields")
    }
}

struct HUDView: View {
    @ObservedObject var model: HUDModel

    private static let rainbow: [Color] = [
        Color(red: 0.66, green: 0.33, blue: 0.97), Color(red: 0.39, green: 0.40, blue: 0.95),
        Color(red: 0.23, green: 0.51, blue: 0.96), Color(red: 0.13, green: 0.83, blue: 0.93),
        Color(red: 0.96, green: 0.45, blue: 0.71), Color(red: 0.66, green: 0.33, blue: 0.97),
    ]

    var body: some View {
        ZStack {
            if model.phase != .hidden {
                pill
                    .transition(.scale(scale: 0.7).combined(with: .opacity).combined(with: .offset(y: 10)))
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private var isWorking: Bool { if case .working = model.phase { return true } else { return false } }

    private var shape: RoundedRectangle { RoundedRectangle(cornerRadius: 22, style: .continuous) }

    private var pill: some View {
        TimelineView(.animation(paused: !isWorking)) { context in
            let t = context.date.timeIntervalSinceReferenceDate
            let angle = Angle.degrees((t.truncatingRemainder(dividingBy: 2.4) / 2.4) * 360) // clockwise
            let shimmer = (t.truncatingRemainder(dividingBy: 1.8) / 1.8) * 1.6 - 0.3
            pillBody(angle: angle, shimmer: shimmer)
        }
    }

    /// Split out of `pill` so the type-checker handles the long modifier chain quickly.
    private func pillBody(angle: Angle, shimmer: Double) -> some View {
        content(shimmer: shimmer)
            .padding(.horizontal, 16)
            .padding(.vertical, 11)
            .frame(maxWidth: 420, alignment: .leading)
            .fixedSize(horizontal: detail == nil, vertical: true)
            .background(.thickMaterial, in: shape)
            .overlay(shape.strokeBorder(ring(angle), lineWidth: 2))
            .background(glow(angle))
            .shadow(color: .black.opacity(0.18), radius: 12, y: 4)
    }

    private func glow(_ angle: Angle) -> some View {
        shape.fill(AngularGradient(colors: Self.rainbow, center: .center, angle: angle))
            .blur(radius: 16)
            .opacity(isWorking ? 0.55 : 0)
    }

    private func content(shimmer: Double) -> some View {
            HStack(alignment: titleAlignment, spacing: 10) {
                icon
                    .font(.system(size: 16, weight: .semibold))
                    .frame(width: 20, height: 20)
                VStack(alignment: .leading, spacing: 2) {
                    title(shimmer: shimmer)
                    if let detail {
                        Text(detail)
                            .font(.system(size: 12))
                            .foregroundStyle(.secondary)
                            .lineLimit(3)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }
            }
    }

    private var titleAlignment: VerticalAlignment { detail == nil ? .center : .top }

    private func ring(_ angle: Angle) -> AnyShapeStyle {
        switch model.phase {
        case .working: return AnyShapeStyle(AngularGradient(colors: Self.rainbow, center: .center, angle: angle))
        case .done: return AnyShapeStyle(LinearGradient(colors: [.green.opacity(0.8), .green], startPoint: .top, endPoint: .bottom))
        case .failed(let m):
            let c: Color = MessageText.isWarning(m) ? .orange : .red
            return AnyShapeStyle(LinearGradient(colors: [c.opacity(0.8), c], startPoint: .top, endPoint: .bottom))
        case .hidden: return AnyShapeStyle(Color.clear)
        }
    }

    @ViewBuilder private func title(shimmer: Double) -> some View {
        let text = Text(titleText)
            .font(.system(size: 13.5, weight: .semibold, design: .rounded))
            .lineLimit(1)
        if isWorking {
            text.foregroundStyle(LinearGradient(stops: [
                .init(color: .primary, location: 0),
                .init(color: .primary, location: max(0, min(1, shimmer - 0.15))),
                .init(color: .purple, location: max(0, min(1, shimmer))),
                .init(color: .primary, location: max(0, min(1, shimmer + 0.15))),
                .init(color: .primary, location: 1),
            ], startPoint: .leading, endPoint: .trailing))
        } else {
            text.foregroundStyle(.primary)
        }
    }

    @ViewBuilder private var icon: some View {
        switch model.phase {
        case .working:
            Image(systemName: "sparkles")
                .foregroundStyle(LinearGradient(colors: [.purple, .indigo, .cyan], startPoint: .topLeading, endPoint: .bottomTrailing))
                .symbolEffect(.variableColor.iterative.reversing, options: .repeating)
        case .done:
            Image(systemName: "checkmark.circle.fill")
                .foregroundStyle(.white, .green)
                .symbolEffect(.bounce, value: model.phase)
        case .failed(let m):
            Image(systemName: MessageText.isWarning(m) ? "exclamationmark.triangle.fill" : "exclamationmark.circle.fill")
                .foregroundStyle(MessageText.isWarning(m) ? Color.orange : Color.red)
                .symbolEffect(.bounce, value: model.phase)
        case .hidden:
            EmptyView()
        }
    }

    private var titleText: String {
        switch model.phase {
        case .working(let t): return t.hasSuffix("…") ? t : "\(t)…"
        case .done(let t): return t
        case .failed(let m): return MessageText.split(m).title
        case .hidden: return ""
        }
    }

    private var detail: String? {
        guard case .failed(let m) = model.phase else { return nil }
        let d = MessageText.split(m).detail
        return d.isEmpty ? nil : d
    }
}
