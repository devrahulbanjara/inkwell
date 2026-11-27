import SwiftUI

/// Recognisable brand-style marks for each provider, drawn in code so the app stays dependency- and asset-free.
/// Gemini: gradient four-point sparkle · Groq: white "g" on orange · Claude: terracotta starburst · Codex: OpenAI-style knot.
struct ProviderLogo: View {
    let kind: ProviderKind
    var size: CGFloat = 28

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: size * 0.28, style: .continuous)
        ZStack {
            shape.fill(background)
            mark
        }
        .frame(width: size, height: size)
        .overlay(shape.strokeBorder(Color.primary.opacity(0.10), lineWidth: 0.5))
        .accessibilityLabel(kind.displayName)
    }

    private var background: Color {
        switch kind {
        case .gemini: return .white
        case .groq: return Color(red: 0.96, green: 0.31, blue: 0.21)        // #F55036
        case .claudeCode: return Color(red: 0.97, green: 0.95, blue: 0.91)   // warm cream
        case .codex: return .black
        }
    }

    @ViewBuilder
    private var mark: some View {
        switch kind {
        case .gemini:
            GeminiSparkle()
                .fill(LinearGradient(colors: [Color(red: 0.26, green: 0.52, blue: 0.96),
                                              Color(red: 0.61, green: 0.45, blue: 0.80),
                                              Color(red: 0.85, green: 0.40, blue: 0.44)],
                                     startPoint: .topLeading, endPoint: .bottomTrailing))
                .padding(size * 0.16)
        case .groq:
            Text("g")
                .font(.system(size: size * 0.74, weight: .bold, design: .rounded))
                .foregroundStyle(.white)
                .offset(y: -size * 0.08)
        case .claudeCode:
            ClaudeBurst(color: Color(red: 0.85, green: 0.47, blue: 0.34))
                .padding(size * 0.14)
        case .codex:
            OpenAIKnot(color: .white)
                .padding(size * 0.15)
        }
    }
}

/// Four-point star with concave sides.
private struct GeminiSparkle: Shape {
    func path(in r: CGRect) -> Path {
        // Control points pulled slightly toward each corner give the sparkle its soft, full sides.
        let k: CGFloat = 0.06
        func ctl(_ dx: CGFloat, _ dy: CGFloat) -> CGPoint {
            CGPoint(x: r.midX + dx * r.width * k, y: r.midY + dy * r.height * k)
        }
        var p = Path()
        p.move(to: CGPoint(x: r.midX, y: r.minY))
        p.addQuadCurve(to: CGPoint(x: r.maxX, y: r.midY), control: ctl(1, -1))
        p.addQuadCurve(to: CGPoint(x: r.midX, y: r.maxY), control: ctl(1, 1))
        p.addQuadCurve(to: CGPoint(x: r.minX, y: r.midY), control: ctl(-1, 1))
        p.addQuadCurve(to: CGPoint(x: r.midX, y: r.minY), control: ctl(-1, -1))
        p.closeSubpath()
        return p
    }
}

/// Irregular radiating rays, like the Claude spark.
private struct ClaudeBurst: View {
    let color: Color
    private static let lengths: [CGFloat] = [1.0, 0.78, 0.92, 0.70, 0.96, 0.82, 0.88, 0.74, 1.0, 0.80, 0.90, 0.72]

    var body: some View {
        Canvas { ctx, size in
            let s = min(size.width, size.height)
            let c = CGPoint(x: size.width / 2, y: size.height / 2)
            let radius = s / 2
            for (i, len) in Self.lengths.enumerated() {
                let angle = (Double(i) * 30 + (i.isMultiple(of: 2) ? 0 : 7)) * .pi / 180
                let end = CGPoint(x: c.x + cos(angle) * radius * len, y: c.y + sin(angle) * radius * len)
                var ray = Path()
                ray.move(to: c)
                ray.addLine(to: end)
                ctx.stroke(ray, with: .color(color), style: StrokeStyle(lineWidth: s * 0.11, lineCap: .round))
            }
        }
    }
}

/// Six interlocking rounded links, approximating the OpenAI blossom.
private struct OpenAIKnot: View {
    let color: Color

    var body: some View {
        Canvas { ctx, size in
            let s = min(size.width, size.height)
            let c = CGPoint(x: size.width / 2, y: size.height / 2)
            let w = s * 0.30, h = s * 0.52
            for i in 0..<6 {
                var t = CGAffineTransform(translationX: c.x, y: c.y)
                t = t.rotated(by: CGFloat(i) * .pi / 3)
                let rect = CGRect(x: s * 0.06, y: -h * 0.78, width: w, height: h)
                let link = Path(roundedRect: rect, cornerRadius: w / 2).applying(t)
                ctx.stroke(link, with: .color(color), lineWidth: s * 0.055)
            }
        }
    }
}
