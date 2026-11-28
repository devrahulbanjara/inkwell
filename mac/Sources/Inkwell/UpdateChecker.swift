import AppKit
import Foundation

/// Looks for a newer GitHub release at launch and every few hours, and updates in one click:
/// - Installed with Homebrew → `brew update` + `brew upgrade --cask` in the background, then relaunch.
/// - Otherwise → opens the new DMG.
/// Ad-hoc signed builds lose their Accessibility grant on every update, so the stale entry is reset first;
/// the new version then asks for access cleanly instead of showing a switch that's on but doesn't work.
@MainActor
final class UpdateChecker: ObservableObject {
    static let repo = "devrahulbanjara/inkwell"
    static let cask = "devrahulbanjara/inkwell/inkwell"

    /// The newer version ("0.1.2"), or nil when Inkwell is up to date.
    @Published private(set) var availableVersion: String?
    /// Progress or error text while installing.
    @Published private(set) var status: String?
    @Published private(set) var isInstalling = false
    /// The dashboard banner was closed for this version (the menu keeps offering it).
    @Published var dismissed = false

    private var dmgURL: URL?
    private var pageURL = URL(string: "https://github.com/\(UpdateChecker.repo)/releases/latest")!
    private var timer: Timer?

    static var currentVersion: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0.0.0"
    }

    func start() {
        Task { @MainActor [weak self] in
            try? await Task.sleep(for: .seconds(8)) // don't slow down login
            await self?.check()
        }
        timer = Timer.scheduledTimer(withTimeInterval: 6 * 3600, repeats: true) { [weak self] _ in
            Task { @MainActor in await self?.check() }
        }
    }

    /// Returns true when an update is available.
    @discardableResult
    func check() async -> Bool {
        var req = URLRequest(url: URL(string: "https://api.github.com/repos/\(Self.repo)/releases/latest")!)
        req.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        req.setValue("Inkwell-Mac-Updater", forHTTPHeaderField: "User-Agent")
        req.timeoutInterval = 30
        guard let response = try? await URLSession.shared.data(for: req),
              (response.1 as? HTTPURLResponse)?.statusCode == 200,
              let obj = try? JSONSerialization.jsonObject(with: response.0) as? [String: Any],
              let tag = obj["tag_name"] as? String,
              (obj["draft"] as? Bool) != true else { return availableVersion != nil }

        let latest = tag.trimmingCharacters(in: CharacterSet(charactersIn: "vV"))
        if let page = (obj["html_url"] as? String).flatMap({ URL(string: $0) }) { pageURL = page }
        let assets = obj["assets"] as? [[String: Any]] ?? []
        let dmgs = assets.compactMap { a -> (String, URL)? in
            guard let name = a["name"] as? String, name.hasSuffix(".dmg"),
                  let url = (a["browser_download_url"] as? String).flatMap({ URL(string: $0) }) else { return nil }
            return (name, url)
        }
        dmgURL = dmgs.first { $0.0 == "Inkwell.dmg" }?.1 ?? dmgs.first?.1

        // A release without a Mac build (e.g. a Windows-only fix) isn't an update for this app.
        if dmgURL != nil, Self.isNewer(latest, than: Self.currentVersion) {
            if latest != availableVersion { dismissed = false }
            availableVersion = latest
            return true
        }
        availableVersion = nil
        return false
    }

    func install() {
        guard !isInstalling else { return }
        guard let brew = Self.brewPath(), Self.installedWithHomebrew(brew: brew) else {
            NSWorkspace.shared.open(dmgURL ?? pageURL)
            return
        }
        isInstalling = true
        status = "Updating with Homebrew…"
        Task { @MainActor in
            var env = ProcessInfo.processInfo.environment
            env["HOMEBREW_NO_INSTALL_CLEANUP"] = "1"
            env["HOMEBREW_NO_ENV_HINTS"] = "1"
            env["NONINTERACTIVE"] = "1"
            let brewURL = URL(fileURLWithPath: brew)
            _ = try? await ProcessRunner.run(brewURL, args: ["update", "--quiet"], environment: env, cwd: nil, timeout: 300)
            let out = try? await ProcessRunner.run(brewURL, args: ["upgrade", "--cask", Self.cask], environment: env, cwd: nil, timeout: 600)
            if out?.status == 0 {
                status = "Restarting…"
                relaunch()
            } else {
                isInstalling = false
                status = "Homebrew couldn't update Inkwell, so the download page was opened."
                NSWorkspace.shared.open(dmgURL ?? pageURL)
            }
        }
    }

    /// Resets the stale Accessibility entry, then reopens the freshly installed app once this one has quit.
    private func relaunch() {
        let path = Bundle.main.bundlePath
        let id = Bundle.main.bundleIdentifier ?? "io.github.inkwell"
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/bin/sh")
        p.arguments = ["-c", "/usr/bin/tccutil reset Accessibility '\(id)' >/dev/null 2>&1; sleep 1; /usr/bin/open '\(path)'"]
        try? p.run()
        NSApp.terminate(nil)
    }

    private static func brewPath() -> String? {
        ["/opt/homebrew/bin/brew", "/usr/local/bin/brew"].first { FileManager.default.isExecutableFile(atPath: $0) }
    }

    private static func installedWithHomebrew(brew: String) -> Bool {
        let prefix = URL(fileURLWithPath: brew).deletingLastPathComponent().deletingLastPathComponent()
        return FileManager.default.fileExists(atPath: prefix.appendingPathComponent("Caskroom/inkwell").path)
    }

    static func isNewer(_ a: String, than b: String) -> Bool {
        func parts(_ s: String) -> [Int] {
            s.split(separator: ".").map { piece in Int(String(piece.prefix(while: { ch in ch.isNumber }))) ?? 0 }
        }
        let pa = parts(a), pb = parts(b)
        for i in 0..<max(pa.count, pb.count) {
            let x = i < pa.count ? pa[i] : 0
            let y = i < pb.count ? pb[i] : 0
            if x != y { return x > y }
        }
        return false
    }
}
