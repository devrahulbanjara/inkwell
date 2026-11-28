// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "Inkwell",
    platforms: [.macOS(.v14)],
    targets: [
        .executableTarget(
            name: "Inkwell",
            path: "Sources/Inkwell"
        )
    ]
)
