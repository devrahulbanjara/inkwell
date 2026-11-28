import Foundation

// MARK: - Process runner

struct ProcessOutput {
    let status: Int32
    let stdout: String
    let stderr: String
    let timedOut: Bool
}

enum ProcessRunner {
    private final class Collector: @unchecked Sendable {
        var out = Data()
        var err = Data()
        var timedOut = false
    }

    /// Runs a program without a shell (no quoting issues), feeding `stdin`, with a hard timeout.
    static func run(_ executable: URL, args: [String], stdin: String? = nil,
                    environment: [String: String], cwd: URL?, timeout: TimeInterval) async throws -> ProcessOutput {
        try await withCheckedThrowingContinuation { (cont: CheckedContinuation<ProcessOutput, Error>) in
            DispatchQueue.global(qos: .userInitiated).async {
                let p = Process()
                p.executableURL = executable
                p.arguments = args
                p.environment = environment
                if let cwd { p.currentDirectoryURL = cwd }
                let outPipe = Pipe(), errPipe = Pipe(), inPipe = Pipe()
                p.standardOutput = outPipe
                p.standardError = errPipe
                p.standardInput = inPipe

                do { try p.run() } catch {
                    cont.resume(throwing: AIError.cliFailed("Couldn't start \(executable.lastPathComponent): \(error.localizedDescription)"))
                    return
                }

                let c = Collector()
                let group = DispatchGroup()
                group.enter()
                DispatchQueue.global().async { c.out = outPipe.fileHandleForReading.readDataToEndOfFile(); group.leave() }
                group.enter()
                DispatchQueue.global().async { c.err = errPipe.fileHandleForReading.readDataToEndOfFile(); group.leave() }
                DispatchQueue.global().async {
                    if let stdin { try? inPipe.fileHandleForWriting.write(contentsOf: Data(stdin.utf8)) }
                    try? inPipe.fileHandleForWriting.close()
                }

                let killer = DispatchWorkItem {
                    if p.isRunning { c.timedOut = true; p.terminate() }
                }
                DispatchQueue.global().asyncAfter(deadline: .now() + timeout, execute: killer)
                p.waitUntilExit()
                killer.cancel()
                group.wait()

                cont.resume(returning: ProcessOutput(
                    status: p.terminationStatus,
                    stdout: String(decoding: c.out, as: UTF8.self),
                    stderr: String(decoding: c.err, as: UTF8.self),
                    timedOut: c.timedOut))
            }
        }
    }
}

// MARK: - Finding the CLIs (GUI apps don't inherit your shell PATH)

actor CLILocator {
    static let shared = CLILocator()
    private var cachedShellPath: String?

    /// PATH from the user's interactive login shell, so Homebrew/npm/nvm installs are visible.
    func shellPath() async -> String {
        if let cachedShellPath { return cachedShellPath }
        let shell = ProcessInfo.processInfo.environment["SHELL"] ?? "/bin/zsh"
        var path = ""
        if let out = try? await ProcessRunner.run(URL(fileURLWithPath: shell),
                                                  args: ["-ilc", "printf '__INKWELL_PATH__%s' \"$PATH\""],
                                                  environment: ProcessInfo.processInfo.environment,
                                                  cwd: CLILocator.workDir, timeout: 6),
           let r = out.stdout.range(of: "__INKWELL_PATH__") {
            path = String(out.stdout[r.upperBound...]).trimmingCharacters(in: .whitespacesAndNewlines)
        }
        let home = FileManager.default.homeDirectoryForCurrentUser.path
        let extras = ["\(home)/.local/bin", "\(home)/.claude/local", "\(home)/.homebrew/bin", "/opt/homebrew/bin",
                      "/usr/local/bin", "\(home)/.npm-global/bin", "\(home)/.bun/bin", "\(home)/.volta/bin",
                      "\(home)/.cargo/bin", "/usr/bin", "/bin", "/usr/sbin", "/sbin"]
        var parts = path.split(separator: ":").map(String.init)
        for e in extras where !parts.contains(e) { parts.append(e) }
        // nvm installs: add every node version's bin folder.
        let nvm = "\(home)/.nvm/versions/node"
        if let versions = try? FileManager.default.contentsOfDirectory(atPath: nvm) {
            for v in versions.sorted().reversed() { parts.append("\(nvm)/\(v)/bin") }
        }
        let joined = parts.joined(separator: ":")
        cachedShellPath = joined
        return joined
    }

    func locate(_ name: String, override: String) async -> URL? {
        let trimmed = (override as NSString).expandingTildeInPath.trimmingCharacters(in: .whitespaces)
        if !trimmed.isEmpty, FileManager.default.isExecutableFile(atPath: trimmed) {
            return URL(fileURLWithPath: trimmed)
        }
        for dir in await shellPath().split(separator: ":") {
            let candidate = "\(dir)/\(name)"
            if FileManager.default.isExecutableFile(atPath: candidate) { return URL(fileURLWithPath: candidate) }
        }
        return nil
    }

    func environment() async -> [String: String] {
        var env = ProcessInfo.processInfo.environment
        env["PATH"] = await shellPath()
        env["NO_COLOR"] = "1"
        env["TERM"] = "dumb"
        return env
    }

    /// Dedicated folder `~/.inkwell` that the CLIs (and the PATH-probing shell) always run in,
    /// so headless Claude Code / Codex never pick up a project's files or config.
    static var workDir: URL {
        let dir = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".inkwell", isDirectory: true)
        if !FileManager.default.fileExists(atPath: dir.path) {
            try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
            let readme = "Inkwell runs Claude Code and Codex in headless mode from this folder.\nSafe to leave empty.\n"
            try? readme.write(to: dir.appendingPathComponent("README.txt"), atomically: true, encoding: .utf8)
        }
        return dir
    }
}

// MARK: - Status

struct CLIStatus: Equatable {
    enum State: Equatable { case checking, notInstalled, loggedOut, ready }
    var state: State
    var path: String? = nil
    var version: String? = nil
    var detail: String = ""

    static let checking = CLIStatus(state: .checking)
}

enum CLIProviders {
    static func status(kind: ProviderKind, settings: CLIProviderSettings) async -> CLIStatus {
        let name = kind.cliName
        guard let exe = await CLILocator.shared.locate(name, override: settings.binaryPath) else {
            return CLIStatus(state: .notInstalled, detail: "`\(name)` was not found on this Mac.")
        }
        let env = await CLILocator.shared.environment()
        let version = try? await ProcessRunner.run(exe, args: ["--version"], environment: env,
                                                   cwd: CLILocator.workDir, timeout: 10)
        let versionText = version?.stdout.trimmingCharacters(in: .whitespacesAndNewlines)
            .components(separatedBy: "\n").first

        let args = kind == .claudeCode ? ["auth", "status"] : ["login", "status"]
        guard let out = try? await ProcessRunner.run(exe, args: args, environment: env,
                                                     cwd: CLILocator.workDir, timeout: 15) else {
            return CLIStatus(state: .loggedOut, path: exe.path, version: versionText, detail: "Couldn't check sign-in status.")
        }
        // Both CLIs exit 0 when signed in.
        guard out.status == 0, !out.timedOut else {
            return CLIStatus(state: .loggedOut, path: exe.path, version: versionText, detail: "Not signed in.")
        }
        var detail = "Signed in"
        if kind == .claudeCode,
           let obj = try? JSONSerialization.jsonObject(with: Data(out.stdout.utf8)) as? [String: Any] {
            let method = obj["authMethod"] as? String ?? ""
            if method == "none" {
                return CLIStatus(state: .loggedOut, path: exe.path, version: versionText, detail: "Not signed in.")
            }
            let friendly = ["claude.ai": "Claude subscription", "oauth_token": "OAuth token",
                            "api_key": "Anthropic API key", "api_key_helper": "API key helper",
                            "third_party": "Cloud provider"][method] ?? method
            let who = (obj["email"] as? String) ?? (obj["account"] as? [String: Any])?["email"] as? String
            detail = "Signed in · \(friendly)" + (who.map { " · \($0)" } ?? "")
        } else {
            let line = (out.stdout + "\n" + out.stderr).split(separator: "\n").first { !$0.isEmpty }
            if let line { detail = String(line) }
        }
        return CLIStatus(state: .ready, path: exe.path, version: versionText, detail: detail)
    }

    // MARK: Completion

    static func complete(kind: ProviderKind, settings: CLIProviderSettings, system: String, user: String) async throws -> RawResult {
        let name = kind.displayName
        guard let exe = await CLILocator.shared.locate(kind.cliName, override: settings.binaryPath) else {
            throw AIError.cliNotInstalled(name)
        }
        let env = await CLILocator.shared.environment()
        let model = settings.model.trimmingCharacters(in: .whitespaces)

        if kind == .claudeCode {
            let full = ["-p", "--output-format", "json", "--system-prompt", system,
                        "--tools", "", "--strict-mcp-config", "--disable-slash-commands", "--no-session-persistence"]
                + (model.isEmpty ? [] : ["--model", model])
            var out = try await ProcessRunner.run(exe, args: full, stdin: user, environment: env,
                                                  cwd: CLILocator.workDir, timeout: 90)
            if isUnknownFlag(out) { // older Claude Code: fall back to the minimal flag set
                let minimal = ["-p", "--output-format", "json", "--system-prompt", system] + (model.isEmpty ? [] : ["--model", model])
                out = try await ProcessRunner.run(exe, args: minimal, stdin: user, environment: env,
                                                  cwd: CLILocator.workDir, timeout: 90)
            }
            return try parseClaude(out)
        } else {
            let prompt = """
            \(system)
            Do not run commands, read files or use tools — just reply with the text.

            \(user)
            """
            let base = ["exec", "--json", "--skip-git-repo-check", "--sandbox", "read-only"]
            let modelArgs = model.isEmpty ? [] : ["-m", model]
            let fast = settings.fastMode ? ["-c", "model_reasoning_effort=\"low\""] : []
            var out = try await ProcessRunner.run(exe, args: base + ["--ephemeral"] + modelArgs + fast + ["-"],
                                                  stdin: prompt, environment: env,
                                                  cwd: CLILocator.workDir, timeout: 120)
            if isUnknownFlag(out) {
                out = try await ProcessRunner.run(exe, args: ["exec", "--json", "--skip-git-repo-check"] + modelArgs + ["-"],
                                                  stdin: prompt, environment: env,
                                                  cwd: CLILocator.workDir, timeout: 120)
            }
            return try parseCodex(out)
        }
    }

    private static func isUnknownFlag(_ out: ProcessOutput) -> Bool {
        guard out.status != 0 else { return false }
        let e = out.stderr.lowercased()
        return e.contains("unknown option") || e.contains("unexpected argument") || e.contains("unrecognized")
    }

    private static func parseClaude(_ out: ProcessOutput) throws -> RawResult {
        if out.timedOut { throw AIError.timeout }
        let jsonLine = out.stdout.split(separator: "\n").last { $0.hasPrefix("{") }.map(String.init) ?? out.stdout
        guard let obj = try? JSONSerialization.jsonObject(with: Data(jsonLine.utf8)) as? [String: Any] else {
            throw classifyCLI(out.stderr.isEmpty ? out.stdout : out.stderr, name: "Claude Code")
        }
        let result = obj["result"] as? String ?? ""
        if (obj["is_error"] as? Bool) == true || out.status != 0 {
            throw classifyCLI(result.isEmpty ? out.stderr : result, name: "Claude Code")
        }
        let usage = obj["usage"] as? [String: Any] ?? [:]
        let input = (usage["input_tokens"] as? Int ?? 0)
            + (usage["cache_read_input_tokens"] as? Int ?? 0)
            + (usage["cache_creation_input_tokens"] as? Int ?? 0)
        return RawResult(text: result, inputTokens: input, outputTokens: usage["output_tokens"] as? Int ?? 0)
    }

    private static func parseCodex(_ out: ProcessOutput) throws -> RawResult {
        if out.timedOut { throw AIError.timeout }
        var text = ""
        var input = 0, output = 0
        var failure: String?
        for line in out.stdout.split(separator: "\n") {
            guard let obj = try? JSONSerialization.jsonObject(with: Data(line.utf8)) as? [String: Any],
                  let type = obj["type"] as? String else { continue }
            switch type {
            case "item.completed":
                if let item = obj["item"] as? [String: Any], item["type"] as? String == "agent_message",
                   let t = item["text"] as? String { text = t }
            case "turn.completed":
                if let u = obj["usage"] as? [String: Any] {
                    input += u["input_tokens"] as? Int ?? 0
                    output += (u["output_tokens"] as? Int ?? 0) + (u["reasoning_output_tokens"] as? Int ?? 0)
                }
            case "turn.failed", "error":
                let err = obj["error"] as? [String: Any]
                failure = (err?["message"] as? String) ?? (obj["message"] as? String) ?? "Codex failed."
            default:
                break
            }
        }
        if let failure, text.isEmpty { throw classifyCLI(failure, name: "Codex") }
        if text.isEmpty && out.status != 0 { throw classifyCLI(out.stderr.isEmpty ? out.stdout : out.stderr, name: "Codex") }
        return RawResult(text: text, inputTokens: input, outputTokens: output)
    }

    private static func classifyCLI(_ message: String, name: String) -> AIError {
        let m = message.trimmingCharacters(in: .whitespacesAndNewlines)
        let lower = m.lowercased()
        if lower.contains("not logged in") || lower.contains("please log in") || lower.contains("/login")
            || lower.contains("authentication") || lower.contains("unauthorized") || lower.contains("401")
            || lower.contains("invalid api key") || lower.contains("codex login") {
            return .cliNotLoggedIn(name)
        }
        if lower.contains("usage limit") || lower.contains("rate limit") || lower.contains("limit reached")
            || lower.contains("429") || lower.contains("quota") || lower.contains("too many requests") {
            return .rateLimited(retryAfter: nil, daily: lower.contains("usage limit"), message: m)
        }
        if lower.contains("model") && (lower.contains("not found") || lower.contains("invalid") || lower.contains("not available") || lower.contains("not supported")) {
            return .modelUnavailable(m)
        }
        if lower.contains("overloaded") || lower.contains("529") || lower.contains("503") {
            return .server(503, m)
        }
        let short = m.count > 220 ? String(m.prefix(220)) + "…" : m
        return .cliFailed(short.isEmpty ? "\(name) failed without an error message." : "\(name): \(short)")
    }
}
