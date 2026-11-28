using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Inkwell.Core;
using Microsoft.Win32;

namespace Inkwell.Providers;

// MARK: Process runner

public sealed record ProcessOutput(int ExitCode, string Stdout, string Stderr, bool TimedOut);

public static class ProcessRunner
{
    /// <summary>
    /// Runs a program without a shell, feeding stdin, with a hard timeout.
    /// Handles .cmd/.bat by wrapping in cmd.exe /d /s /c.
    /// </summary>
    public static async Task<ProcessOutput> RunAsync(string executable, string[] args,
        string? stdin = null, Dictionary<string, string>? environment = null,
        string? cwd = null, int timeoutSeconds = 60)
    {
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        // Handle .cmd / .bat wrappers
        var ext = Path.GetExtension(executable).ToLowerInvariant();
        if (ext is ".cmd" or ".bat")
        {
            psi.FileName = "cmd.exe";
            var quoted = $"\"{executable}\" {string.Join(" ", args.Select(QuoteArg))}";
            psi.Arguments = $"/d /s /c \"{quoted}\"";
        }
        else
        {
            psi.FileName = executable;
            psi.Arguments = string.Join(" ", args.Select(QuoteArg));
        }

        if (cwd is not null) psi.WorkingDirectory = cwd;
        if (environment is not null)
        {
            foreach (var (k, v) in environment)
                psi.Environment[k] = v;
        }

        using var process = new Process { StartInfo = psi };
        process.Start();

        // Write stdin
        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin);
            process.StandardInput.Close();
        }
        else
        {
            process.StandardInput.Close();
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        bool timedOut = false;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            try { process.Kill(entireProcessTree: true); } catch { }
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        return new ProcessOutput(process.ExitCode, stdout, stderr, timedOut);
    }

    private static string QuoteArg(string a) =>
        a.Contains(' ') || a.Contains('"') ? $"\"{a.Replace("\"", "\\\"")}\"" : a;
}

// MARK: Finding the CLIs on Windows

public static class CLILocator
{
    private static string? _cachedPath;

    /// <summary>
    /// Builds a comprehensive PATH for finding CLI tools on Windows.
    /// Checks system PATH, registry, plus common npm/nvm/scoop/chocolatey install locations.
    /// </summary>
    public static string GetSearchPath()
    {
        if (_cachedPath is not null) return _cachedPath;

        var parts = new List<string>();

        // Current PATH
        var envPath = Environment.GetEnvironmentVariable("PATH") ?? "";
        parts.AddRange(envPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));

        // Registry PATH values (HKCU and HKLM)
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Environment");
            var regPath = key?.GetValue("Path") as string;
            if (!string.IsNullOrEmpty(regPath))
                parts.AddRange(regPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        }
        catch { }
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment");
            var regPath = key?.GetValue("Path") as string;
            if (!string.IsNullOrEmpty(regPath))
                parts.AddRange(regPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        }
        catch { }

        // Extra directories: npm, nvm, scoop, chocolatey, volta, bun, cargo, .claude
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var extras = new[]
        {
            Path.Combine(home, ".local", "bin"),
            Path.Combine(home, ".claude", "local"),
            Path.Combine(appData, "npm"),
            Path.Combine(localAppData, "Volta", "bin"),
            Path.Combine(home, "scoop", "shims"),
            @"C:\ProgramData\chocolatey\bin",
            Path.Combine(home, ".bun", "bin"),
            Path.Combine(home, ".cargo", "bin")
        };

        foreach (var e in extras)
            if (!parts.Contains(e, StringComparer.OrdinalIgnoreCase))
                parts.Add(e);

        // nvm-windows installs
        var nvmHome = Environment.GetEnvironmentVariable("NVM_HOME");
        if (!string.IsNullOrEmpty(nvmHome))
        {
            var symlink = Environment.GetEnvironmentVariable("NVM_SYMLINK");
            if (!string.IsNullOrEmpty(symlink)) parts.Add(symlink);
        }

        _cachedPath = string.Join(Path.PathSeparator, parts.Distinct(StringComparer.OrdinalIgnoreCase));
        return _cachedPath;
    }

    public static string? Locate(string name, string overridePath)
    {
        var trimmed = overridePath.Trim();
        if (!string.IsNullOrEmpty(trimmed) && File.Exists(trimmed)) return trimmed;

        var exts = new[] { ".exe", ".cmd", ".bat", "" };
        foreach (var dir in GetSearchPath().Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in exts)
            {
                var candidate = Path.Combine(dir, name + ext);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    public static Dictionary<string, string> GetEnvironment()
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            env[e.Key?.ToString() ?? ""] = e.Value?.ToString() ?? "";
        env["PATH"] = GetSearchPath();
        env["NO_COLOR"] = "1";
        env["TERM"] = "dumb";
        return env;
    }

    /// <summary>
    /// Dedicated folder ~/.inkwell that the CLIs always run in,
    /// so headless Claude Code / Codex never pick up a project's files or config.
    /// </summary>
    public static string WorkDir
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".inkwell");
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "README.txt"),
                    "Inkwell runs Claude Code and Codex in headless mode from this folder.\nSafe to leave empty.\n");
            }
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
        var exe = CLILocator.Locate(kind.CliName(), settings.BinaryPath);
        if (exe is null) throw AIException.CliNotInstalled(name);

        var env = CLILocator.GetEnvironment();
        var model = settings.Model.Trim();

        if (kind == ProviderKind.ClaudeCode)
        {
            var args = new List<string>
            {
                "-p", "--output-format", "json", "--system-prompt", system,
                "--tools", "", "--strict-mcp-config", "--disable-slash-commands", "--no-session-persistence"
            };
            if (!string.IsNullOrEmpty(model)) { args.Add("--model"); args.Add(model); }

            var output = await ProcessRunner.RunAsync(exe, args.ToArray(), stdin: user,
                environment: env, cwd: CLILocator.WorkDir, timeoutSeconds: 90);

            if (IsUnknownFlag(output))
            {
                var minimal = new List<string> { "-p", "--output-format", "json", "--system-prompt", system };
                if (!string.IsNullOrEmpty(model)) { minimal.Add("--model"); minimal.Add(model); }
                output = await ProcessRunner.RunAsync(exe, minimal.ToArray(), stdin: user,
                    environment: env, cwd: CLILocator.WorkDir, timeoutSeconds: 90);
            }

            return ParseClaude(output);
        }
        else
        {
            var prompt = $"{system}\nDo not run commands, read files or use tools — just reply with the text.\n\n{user}";
            var args = new List<string> { "exec", "--json", "--skip-git-repo-check", "--sandbox", "read-only", "--ephemeral" };
            if (!string.IsNullOrEmpty(model)) { args.Add("-m"); args.Add(model); }
            if (settings.FastMode) { args.Add("-c"); args.Add("model_reasoning_effort=\"low\""); }
            args.Add("-");

            var output = await ProcessRunner.RunAsync(exe, args.ToArray(), stdin: prompt,
                environment: env, cwd: CLILocator.WorkDir, timeoutSeconds: 120);

            if (IsUnknownFlag(output))
            {
                var fallback = new List<string> { "exec", "--json", "--skip-git-repo-check" };
                if (!string.IsNullOrEmpty(model)) { fallback.Add("-m"); fallback.Add(model); }
                fallback.Add("-");
                output = await ProcessRunner.RunAsync(exe, fallback.ToArray(), stdin: prompt,
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

    private static RawResult ParseClaude(ProcessOutput output)
    {
        if (output.TimedOut) throw AIException.Timeout();
        var jsonLine = output.Stdout.Split('\n').LastOrDefault(l => l.TrimStart().StartsWith("{")) ?? output.Stdout;
        try
        {
            using var doc = JsonDocument.Parse(jsonLine);
            var root = doc.RootElement;
            var result = root.TryGetProperty("result", out var r) ? r.GetString() ?? "" : "";
            var isError = root.TryGetProperty("is_error", out var ie) && ie.GetBoolean();
            if (isError || output.ExitCode != 0)
                throw ClassifyCLI(string.IsNullOrEmpty(result) ? output.Stderr : result, "Claude Code");

            int inputTokens = 0, outputTokens = 0;
            if (root.TryGetProperty("usage", out var usage))
            {
                inputTokens = (usage.TryGetProperty("input_tokens", out var it) ? it.GetInt32() : 0)
                            + (usage.TryGetProperty("cache_read_input_tokens", out var cr) ? cr.GetInt32() : 0)
                            + (usage.TryGetProperty("cache_creation_input_tokens", out var cc) ? cc.GetInt32() : 0);
                outputTokens = usage.TryGetProperty("output_tokens", out var ot) ? ot.GetInt32() : 0;
            }
            return new RawResult(result, inputTokens, outputTokens);
        }
        catch (AIException) { throw; }
        catch
        {
            throw ClassifyCLI(string.IsNullOrEmpty(output.Stderr) ? output.Stdout : output.Stderr, "Claude Code");
        }
    }

    private static RawResult ParseCodex(ProcessOutput output)
    {
        if (output.TimedOut) throw AIException.Timeout();

        string text = "";
        int input = 0, outputTokens = 0;
        string? failure = null;

        foreach (var line in output.Stdout.Split('\n'))
        {
            if (!line.TrimStart().StartsWith("{")) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeProp)) continue;
                var type = typeProp.GetString();

                switch (type)
                {
                    case "item.completed":
                        if (root.TryGetProperty("item", out var item)
                            && item.TryGetProperty("type", out var it) && it.GetString() == "agent_message"
                            && item.TryGetProperty("text", out var t))
                            text = t.GetString() ?? "";
                        break;
                    case "turn.completed":
                        if (root.TryGetProperty("usage", out var u))
                        {
                            input += u.TryGetProperty("input_tokens", out var itp) ? itp.GetInt32() : 0;
                            outputTokens += (u.TryGetProperty("output_tokens", out var otp) ? otp.GetInt32() : 0)
                                          + (u.TryGetProperty("reasoning_output_tokens", out var rot) ? rot.GetInt32() : 0);
                        }
                        break;
                    case "turn.failed":
                    case "error":
                        failure = root.TryGetProperty("error", out var err)
                            ? (err.TryGetProperty("message", out var em) ? em.GetString() : "Codex failed.")
                            : (root.TryGetProperty("message", out var mm) ? mm.GetString() : "Codex failed.");
                        break;
                }
            }
            catch { continue; }
        }

        if (failure is not null && string.IsNullOrEmpty(text))
            throw ClassifyCLI(failure, "Codex");
        if (string.IsNullOrEmpty(text) && output.ExitCode != 0)
            throw ClassifyCLI(string.IsNullOrEmpty(output.Stderr) ? output.Stdout : output.Stderr, "Codex");

        return new RawResult(text, input, outputTokens);
    }

    private static AIException ClassifyCLI(string message, string name)
    {
        var m = message.Trim();
        var lower = m.ToLowerInvariant();

        if (lower.Contains("not logged in") || lower.Contains("please log in") || lower.Contains("/login")
            || lower.Contains("authentication") || lower.Contains("unauthorized") || lower.Contains("401")
            || lower.Contains("invalid api key") || lower.Contains("codex login"))
            return AIException.CliNotLoggedIn(name);

        if (lower.Contains("usage limit") || lower.Contains("rate limit") || lower.Contains("limit reached")
            || lower.Contains("429") || lower.Contains("quota") || lower.Contains("too many requests"))
            return AIException.RateLimited(null, lower.Contains("usage limit"), m);

        if (lower.Contains("model") && (lower.Contains("not found") || lower.Contains("invalid")
            || lower.Contains("not available") || lower.Contains("not supported")))
            return AIException.ModelUnavailable(m);

        if (lower.Contains("overloaded") || lower.Contains("529") || lower.Contains("503"))
            return AIException.Server(503, m);

        var shortMsg = m.Length > 220 ? m[..220] + "…" : m;
        return AIException.CliFailed(string.IsNullOrEmpty(shortMsg) ? $"{name} failed without an error message." : $"{name}: {shortMsg}");
    }
}
