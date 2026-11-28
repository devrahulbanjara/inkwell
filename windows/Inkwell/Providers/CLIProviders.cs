using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Inkwell.Core;
using Microsoft.Win32;

namespace Inkwell.Providers;

// MARK: Process runner

public sealed record ProcessOutput(int ExitCode, string Stdout, string Stderr, bool TimedOut);

public static class ProcessRunner
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    /// <summary>
    /// Runs a program without a shell (no quoting surprises), feeding <paramref name="stdin"/> as UTF-8,
    /// with a hard timeout that kills the whole process tree.
    /// npm's <c>.cmd</c> shims are run through node directly, because cmd.exe can't carry multi-line arguments.
    /// </summary>
    public static async Task<ProcessOutput> RunAsync(string executable, IReadOnlyList<string> args,
        string? stdin = null, IDictionary<string, string>? environment = null,
        string? cwd = null, int timeoutSeconds = 60)
    {
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
            StandardInputEncoding = Utf8,
        };
        if (cwd is not null) psi.WorkingDirectory = cwd;
        if (environment is not null)
        {
            psi.Environment.Clear();
            foreach (var (k, v) in environment)
                if (!string.IsNullOrEmpty(k)) psi.Environment[k] = v;
        }

        var ext = Path.GetExtension(executable).ToLowerInvariant();
        if (ext is ".cmd" or ".bat")
        {
            if (NpmShim.Resolve(executable) is { } shim)
            {
                psi.FileName = shim.Node;
                psi.ArgumentList.Add(shim.Script);
                foreach (var a in args) psi.ArgumentList.Add(a);
            }
            else
            {
                // Last resort: cmd.exe. It can't take newlines, so flatten them.
                psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
                var line = string.Join(" ", new[] { executable }.Concat(args)
                    .Select(a => CmdQuote(a.Replace("\r", " ").Replace("\n", " "))));
                psi.Arguments = $"/d /s /c \"{line}\"";
            }
        }
        else if (ext == ".ps1")
        {
            psi.FileName = "powershell.exe";
            foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", executable })
                psi.ArgumentList.Add(a);
            foreach (var a in args) psi.ArgumentList.Add(a);
        }
        else
        {
            psi.FileName = executable;
            foreach (var a in args) psi.ArgumentList.Add(a);
        }

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            throw AIException.CliFailed($"Couldn't start {Path.GetFileName(executable)}: {ex.Message}");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        var stdinTask = Task.Run(async () =>
        {
            try
            {
                if (stdin is not null) await process.StandardInput.WriteAsync(stdin);
                process.StandardInput.Close();
            }
            catch (IOException) { /* the program exited without reading stdin */ }
            catch (ObjectDisposedException) { }
        });

        bool timedOut = false;
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
        {
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                timedOut = true;
                try { process.Kill(entireProcessTree: true); } catch { }
            }
        }
        if (timedOut)
        {
            using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await process.WaitForExitAsync(grace.Token); } catch (OperationCanceledException) { }
        }

        string stdout = "", stderr = "";
        try { stdout = await stdoutTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        try { stderr = await stderrTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        try { await stdinTask.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }

        int exit;
        try { exit = process.HasExited ? process.ExitCode : -1; } catch { exit = -1; }
        return new ProcessOutput(exit, stdout.Replace("\r\n", "\n"), stderr.Replace("\r\n", "\n"), timedOut);
    }

    private static string CmdQuote(string a)
    {
        if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"', '&', '|', '<', '>', '^', '(', ')', '%', '!' }) < 0) return a;
        return "\"" + a.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>Reads npm's cmd-shim (…\npm\codex.cmd) to find the node script it launches.</summary>
    private static class NpmShim
    {
        private static readonly Regex ScriptRx = new(@"""%(?:~dp0|dp0)%\\?(?<path>[^""]+?\.(?:c|m)?js)""", RegexOptions.IgnoreCase);

        public static (string Node, string Script)? Resolve(string shimPath)
        {
            try
            {
                var text = File.ReadAllText(shimPath);
                var m = ScriptRx.Match(text);
                if (!m.Success) return null;
                var dir = Path.GetDirectoryName(shimPath) ?? "";
                var script = Path.GetFullPath(Path.Combine(dir, m.Groups["path"].Value.TrimStart('\\')));
                if (!File.Exists(script)) return null;
                var localNode = Path.Combine(dir, "node.exe");
                var node = File.Exists(localNode) ? localNode : CLILocator.Locate("node", "", exeOnly: true);
                return node is null ? null : (node, script);
            }
            catch
            {
                return null;
            }
        }
    }
}

// MARK: Finding the CLIs (apps launched at login don't see PATH changes made afterwards)

public static class CLILocator
{
    /// <summary>
    /// PATH from the live registry (so installs made after Inkwell started are found), plus the usual
    /// per-user install folders for Claude Code, npm, nvm, Volta, Scoop, Chocolatey, Bun and Cargo.
    /// </summary>
    public static string GetSearchPath()
    {
        var parts = new List<string>();
        void AddAll(string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            foreach (var p in value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                parts.Add(Environment.ExpandEnvironmentVariables(p));
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey("Environment");
            AddAll(key?.GetValue("Path") as string);
        }
        catch { }
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment");
            AddAll(key?.GetValue("Path") as string);
        }
        catch { }
        AddAll(Environment.GetEnvironmentVariable("PATH"));

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        parts.AddRange(new[]
        {
            Path.Combine(home, ".local", "bin"),
            Path.Combine(home, ".claude", "local"),
            Path.Combine(appData, "npm"),
            Path.Combine(localAppData, "Volta", "bin"),
            Path.Combine(localAppData, "pnpm"),
            Path.Combine(home, "scoop", "shims"),
            @"C:\ProgramData\chocolatey\bin",
            Path.Combine(home, ".bun", "bin"),
            Path.Combine(home, ".cargo", "bin"),
            Path.Combine(programFiles, "nodejs"),
        });

        // nvm-windows: the active version's symlink, then every installed version.
        AddAll(Environment.GetEnvironmentVariable("NVM_SYMLINK"));
        var nvmHome = Environment.GetEnvironmentVariable("NVM_HOME") ?? Path.Combine(appData, "nvm");
        try
        {
            if (Directory.Exists(nvmHome))
                parts.AddRange(Directory.GetDirectories(nvmHome, "v*").OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase));
        }
        catch { }

        return string.Join(Path.PathSeparator, parts.Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase));
    }

    public static string? Locate(string name, string overridePath, bool exeOnly = false)
    {
        var trimmed = Environment.ExpandEnvironmentVariables(overridePath.Trim().Trim('"'));
        if (trimmed.StartsWith("~", StringComparison.Ordinal))
            trimmed = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + trimmed[1..];
        if (!string.IsNullOrEmpty(trimmed) && File.Exists(trimmed)) return trimmed;

        var exts = exeOnly ? new[] { ".exe" } : new[] { ".exe", ".cmd", ".bat", ".ps1" };
        foreach (var dir in GetSearchPath().Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in exts)
            {
                try
                {
                    var candidate = Path.Combine(dir, name + ext);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { /* malformed PATH entry */ }
            }
        }
        return null;
    }

    public static Dictionary<string, string> GetEnvironment()
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            var k = e.Key?.ToString();
            if (!string.IsNullOrEmpty(k)) env[k] = e.Value?.ToString() ?? "";
        }
        env["PATH"] = GetSearchPath();
        env["NO_COLOR"] = "1";
        env["TERM"] = "dumb";
        return env;
    }

    /// <summary>
    /// Dedicated folder %USERPROFILE%\.inkwell that the CLIs always run in,
    /// so headless Claude Code / Codex never pick up a project's files or config.
    /// </summary>
    public static string WorkDir
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".inkwell");
            try
            {
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(dir, "README.txt"),
                        "Inkwell runs Claude Code and Codex in headless mode from this folder.\r\nSafe to leave empty.\r\n");
                }
            }
            catch { }
            return dir;
        }
    }
}

// MARK: CLI Status

public sealed record CLIStatus(CLIStatusState State, string? Path = null, string? Version = null, string Detail = "")
{
    public static readonly CLIStatus Checking = new(CLIStatusState.Checking);
}

public enum CLIStatusState { Checking, NotInstalled, LoggedOut, Ready }

// MARK: CLI Providers

public static class CLIProviders
{
    public static async Task<CLIStatus> CheckStatusAsync(ProviderKind kind, CLIProviderSettings settings)
    {
        var name = kind.CliName();
        var exe = CLILocator.Locate(name, settings.BinaryPath);
        if (exe is null)
            return new CLIStatus(CLIStatusState.NotInstalled, Detail: $"{name} was not found on this PC.");

        var env = CLILocator.GetEnvironment();
        string? versionText = null;
        try
        {
            var version = await ProcessRunner.RunAsync(exe, new[] { "--version" },
                environment: env, cwd: CLILocator.WorkDir, timeoutSeconds: 10);
            versionText = version.Stdout.Trim().Split('\n').FirstOrDefault()?.Trim();
        }
        catch { }

        var args = kind == ProviderKind.ClaudeCode
            ? new[] { "auth", "status" }
            : new[] { "login", "status" };

        try
        {
            var output = await ProcessRunner.RunAsync(exe, args,
                environment: env, cwd: CLILocator.WorkDir, timeoutSeconds: 15);

            if (output.ExitCode != 0 || output.TimedOut)
                return new CLIStatus(CLIStatusState.LoggedOut, exe, versionText, "Not signed in.");

            var detail = "Signed in";
            if (kind == ProviderKind.ClaudeCode)
            {
                try
                {
                    using var doc = JsonDocument.Parse(output.Stdout);
                    var root = doc.RootElement;
                    var method = root.TryGetProperty("authMethod", out var am) ? am.GetString() ?? "" : "";
                    if (method == "none")
                        return new CLIStatus(CLIStatusState.LoggedOut, exe, versionText, "Not signed in.");

                    var friendlyMap = new Dictionary<string, string>
                    {
                        ["claude.ai"] = "Claude subscription",
                        ["oauth_token"] = "OAuth token",
                        ["api_key"] = "Anthropic API key",
                        ["api_key_helper"] = "API key helper",
                        ["third_party"] = "Cloud provider"
                    };
                    var friendly = friendlyMap.TryGetValue(method, out var f) ? f : method;
                    string? who = root.TryGetProperty("email", out var email)
                        ? email.GetString()
                        : (root.TryGetProperty("account", out var acct) && acct.TryGetProperty("email", out var ae)
                            ? ae.GetString() : null);
                    detail = $"Signed in · {friendly}" + (who is not null ? $" · {who}" : "");
                }
                catch { /* not JSON, use generic detail */ }
            }
            else
            {
                var line = (output.Stdout + "\n" + output.Stderr).Split('\n')
                    .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
                if (line is not null) detail = line.Trim();
            }

            return new CLIStatus(CLIStatusState.Ready, exe, versionText, detail);
        }
        catch
        {
            return new CLIStatus(CLIStatusState.LoggedOut, exe, versionText, "Couldn't check sign-in status.");
        }
    }

    // MARK: Completion

    public static async Task<RawResult> CompleteAsync(ProviderKind kind, CLIProviderSettings settings,
        string system, string user)
    {
        var name = kind.DisplayName();
        var exe = CLILocator.Locate(kind.CliName(), settings.BinaryPath) ?? throw AIException.CliNotInstalled(name);
        var env = CLILocator.GetEnvironment();
        var model = settings.Model.Trim();

        if (kind == ProviderKind.ClaudeCode)
        {
            // stream-json also carries rate_limit_event lines: which plan window (5-hour, weekly…) and when it resets.
            var args = new List<string>
            {
                "-p", "--output-format", "stream-json", "--verbose", "--system-prompt", system,
                "--tools", "", "--strict-mcp-config", "--disable-slash-commands", "--no-session-persistence"
            };
            if (model.Length > 0) { args.Add("--model"); args.Add(model); }

            var output = await ProcessRunner.RunAsync(exe, args, stdin: user,
                environment: env, cwd: CLILocator.WorkDir, timeoutSeconds: 90);

            if (IsUnknownFlag(output)) // older Claude Code: fall back to the minimal flag set
            {
                var minimal = new List<string> { "-p", "--output-format", "json", "--system-prompt", system };
                if (model.Length > 0) { minimal.Add("--model"); minimal.Add(model); }
                output = await ProcessRunner.RunAsync(exe, minimal, stdin: user,
                    environment: env, cwd: CLILocator.WorkDir, timeoutSeconds: 90);
            }
            return ParseClaude(output);
        }
        else
        {
            var prompt = $"{system}\nDo not run commands, read files or use tools — just reply with the text.\n\n{user}";
            var args = new List<string> { "exec", "--json", "--skip-git-repo-check", "--sandbox", "read-only", "--ephemeral" };
            if (model.Length > 0) { args.Add("-m"); args.Add(model); }
            if (settings.FastMode) { args.Add("-c"); args.Add("model_reasoning_effort=\"low\""); }
            args.Add("-");

            var output = await ProcessRunner.RunAsync(exe, args, stdin: prompt,
                environment: env, cwd: CLILocator.WorkDir, timeoutSeconds: 120);

            if (IsUnknownFlag(output))
            {
                var fallback = new List<string> { "exec", "--json", "--skip-git-repo-check" };
                if (model.Length > 0) { fallback.Add("-m"); fallback.Add(model); }
                fallback.Add("-");
                output = await ProcessRunner.RunAsync(exe, fallback, stdin: prompt,
                    environment: env, cwd: CLILocator.WorkDir, timeoutSeconds: 120);
            }
            return ParseCodex(output);
        }
    }

    private static bool IsUnknownFlag(ProcessOutput output)
    {
        if (output.ExitCode == 0) return false;
        var e = output.Stderr.ToLowerInvariant();
        return e.Contains("unknown option") || e.Contains("unexpected argument") || e.Contains("unrecognized");
    }

    private static IEnumerable<JsonElement> JsonLines(string stdout)
    {
        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("{", StringComparison.Ordinal)) continue;
            JsonDocument? doc = null;
            try { doc = JsonDocument.Parse(line); } catch { }
            if (doc is null) continue;
            using (doc) yield return doc.RootElement.Clone();
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    internal static RawResult ParseClaude(ProcessOutput output)
    {
        if (output.TimedOut) throw AIException.Timeout();

        JsonElement? result = null;
        CliLimitInfo? limit = null;
        foreach (var obj in JsonLines(output.Stdout))
        {
            var type = Str(obj, "type");
            if (type == "result") result = obj;
            else if (type == "rate_limit_event" && obj.TryGetProperty("rate_limit_info", out var info))
            {
                double? util = info.TryGetProperty("utilization", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetDouble() : null;
                DateTime? resets = info.TryGetProperty("resetsAt", out var r) && r.ValueKind == JsonValueKind.Number ? LimitParser.FromEpoch(r.GetDouble()) : null;
                var status = Str(info, "status") ?? "";
                var reading = new CliLimitInfo(LimitParser.ClaudeWindow(Str(info, "rateLimitType")), util, resets, status == "rejected");
                // A rejection is the most useful reading; otherwise keep the latest.
                if (limit is null || !limit.Rejected || reading.Rejected) limit = reading;
            }
        }

        if (result is { } res)
        {
            var text = Str(res, "result") ?? "";
            var isError = res.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True;
            if (isError || output.ExitCode != 0)
            {
                var apiError = Str(res, "api_error") ?? "";
                var status = Int(res, "api_error_status");
                if (limit is { Rejected: true } || apiError == "usage_limit_reached" || apiError == "rate_limit" || status == 429)
                    throw ClaudeLimit(text.Length > 0 ? text : output.Stderr, limit);
                throw ClassifyCLI(text.Length > 0 ? text : (output.Stderr.Length > 0 ? output.Stderr : apiError), "Claude Code");
            }

            int input = 0, outTokens = 0;
            if (res.TryGetProperty("usage", out var usage))
            {
                input = Int(usage, "input_tokens") + Int(usage, "cache_read_input_tokens") + Int(usage, "cache_creation_input_tokens");
                outTokens = Int(usage, "output_tokens");
            }
            return new RawResult(text, input, outTokens) { Limit = limit };
        }

        if (limit is { Rejected: true }) throw ClaudeLimit(output.Stderr, limit);
        var raw = output.Stderr.Trim().Length > 0 ? output.Stderr : output.Stdout;
        if (raw.Trim().Length == 0 && output.ExitCode != 0)
            throw AIException.CliFailed("Claude Code stopped without replying. If you've used up your plan's limit, it resets on its own — check /usage in Claude Code.");
        throw ClassifyCLI(raw, "Claude Code");
    }

    private static AIException ClaudeLimit(string message, CliLimitInfo? info)
    {
        var (window, resets) = LimitParser.ParseClaude(message);
        var w = info?.Window is { } iw && iw != "usage" ? iw : window ?? "usage";
        var at = info?.ResetsAt ?? resets;
        var daily = w != "per-minute";
        var msg = message.Trim().Length > 0 ? message.Trim() : $"Claude Code {w} limit reached";
        return new RateLimitException(at.HasValue ? at.Value - DateTime.Now : null, daily, msg, w, at);
    }

    internal static RawResult ParseCodex(ProcessOutput output)
    {
        if (output.TimedOut) throw AIException.Timeout();

        string text = "";
        int input = 0, outTokens = 0;
        string? failure = null;

        foreach (var obj in JsonLines(output.Stdout))
        {
            switch (Str(obj, "type"))
            {
                case "item.completed":
                    if (obj.TryGetProperty("item", out var item) && Str(item, "type") == "agent_message")
                        text = Str(item, "text") ?? text;
                    break;
                case "turn.completed":
                    if (obj.TryGetProperty("usage", out var u))
                    {
                        input += Int(u, "input_tokens");
                        outTokens += Int(u, "output_tokens") + Int(u, "reasoning_output_tokens");
                    }
                    break;
                case "turn.failed":
                case "error":
                    failure = (obj.TryGetProperty("error", out var err) ? Str(err, "message") : null)
                              ?? Str(obj, "message") ?? "Codex failed.";
                    break;
            }
        }

        if (failure is not null && text.Length == 0) throw ClassifyCLI(failure, "Codex");
        if (text.Length == 0 && output.ExitCode != 0)
            throw ClassifyCLI(output.Stderr.Length > 0 ? output.Stderr : output.Stdout, "Codex");
        return new RawResult(text, input, outTokens);
    }

    private static AIException ClassifyCLI(string message, string name)
    {
        var m = message.Trim();
        var lower = m.ToLowerInvariant();

        if (LimitParser.LooksLikeLimit(m) && !lower.Contains("not logged in"))
        {
            var (window, resets) = name == "Codex" ? LimitParser.ParseCodex(m) : LimitParser.ParseClaude(m);
            // "rate_limit_exceeded" without a plan window is a short per-minute API limit, not plan usage.
            var planLimit = window is not null || resets is not null || lower.Contains("usage limit") || lower.Contains("hit your");
            return new RateLimitException(resets.HasValue ? resets.Value - DateTime.Now : null,
                planLimit, m, planLimit ? window ?? "usage" : "per-minute", resets);
        }

        if (lower.Contains("not logged in") || lower.Contains("please log in") || lower.Contains("/login")
            || lower.Contains("authentication") || lower.Contains("unauthorized") || lower.Contains("401")
            || lower.Contains("invalid api key") || lower.Contains("codex login"))
            return AIException.CliNotLoggedIn(name);

        if (lower.Contains("model") && (lower.Contains("not found") || lower.Contains("invalid")
            || lower.Contains("not available") || lower.Contains("not supported")))
            return AIException.ModelUnavailable(m);

        if (lower.Contains("overloaded") || lower.Contains("529") || lower.Contains("503"))
            return AIException.Server(503, m);

        var shortMsg = m.Length > 220 ? m[..220] + "…" : m;
        return AIException.CliFailed(shortMsg.Length == 0 ? $"{name} failed without an error message." : $"{name}: {shortMsg}");
    }
}
