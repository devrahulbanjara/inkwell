import SwiftUI

/// Liquid Glass on macOS 26+ (Tahoe, Golden Gate…), graceful material fallback on 14–15.
extension View {
    @ViewBuilder
    func glassCard(cornerRadius: CGFloat = 14, tint: Color? = nil, interactive: Bool = false) -> some View {
        let shape = RoundedRectangle(cornerRadius: cornerRadius, style: .continuous)
        if #available(macOS 26.0, *) {
            let base: Glass = tint.map { Glass.regular.tint($0.opacity(0.22)) } ?? .regular
            self.glassEffect(interactive ? base.interactive() : base, in: shape)
        } else {
            self
                .background(.regularMaterial, in: shape)
                .background(tint.map { $0.opacity(0.10) } ?? .clear, in: shape)
                .overlay(shape.strokeBorder(Color.primary.opacity(0.06)))
        }
    }

    @ViewBuilder
    func glassCapsule(tint: Color? = nil) -> some View {
        if #available(macOS 26.0, *) {
            self.glassEffect(tint.map { Glass.regular.tint($0.opacity(0.25)) } ?? .regular, in: Capsule())
        } else {
            self.background(.thickMaterial, in: Capsule())
                .overlay(Capsule().strokeBorder(Color.primary.opacity(0.08)))
                .shadow(color: .black.opacity(0.18), radius: 12, y: 4)
        }
    }

    @ViewBuilder
    func glassButton(prominent: Bool = false) -> some View {
        if #available(macOS 26.0, *) {
            if prominent { self.buttonStyle(.glassProminent) } else { self.buttonStyle(.glass) }
        } else {
            if prominent { self.buttonStyle(.borderedProminent) } else { self.buttonStyle(.bordered) }
        }
    }
}

/// Groups glass shapes so they blend and morph together on macOS 26+.
struct GlassGroup<Content: View>: View {
    var spacing: CGFloat = 10
    @ViewBuilder var content: Content

    var body: some View {
        if #available(macOS 26.0, *) {
            GlassEffectContainer(spacing: spacing) { content }
        } else {
            content
        }
    }
}
