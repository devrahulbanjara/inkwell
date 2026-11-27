import SwiftUI

/// Local view state without the `@State` macro, which needs Xcode's SwiftUI macro plugin
/// (missing from Command Line Tools on the macOS 27 SDK). Use with `@StateObject`.
final class ViewState<Value>: ObservableObject {
    @Published var value: Value
    init(_ value: Value) { self.value = value }
}

extension ProviderKind {
    var tint: Color {
        switch self {
        case .gemini: return .blue
        case .groq: return .orange
        case .claudeCode: return Color(red: 0.85, green: 0.47, blue: 0.34)
        case .codex: return .teal
        }
    }
}
