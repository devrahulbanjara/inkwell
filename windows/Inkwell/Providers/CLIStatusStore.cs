using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Inkwell.Core;

namespace Inkwell.Providers;

/// <summary>
/// Caches the install / sign-in state of the Claude Code and Codex CLIs for the Settings UI.
/// </summary>
public sealed class CLIStatusStore : INotifyPropertyChanged
{
    private readonly Dictionary<ProviderKind, CLIStatus> _statuses = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public CLIStatus Status(ProviderKind kind) =>
        _statuses.TryGetValue(kind, out var s) ? s : CLIStatus.Checking;

    public bool Frozen { get; set; }

    public void Set(ProviderKind kind, CLIStatus status)
    {
        _statuses[kind] = status;
        OnPropertyChanged(nameof(CLIStatus));
    }

    public async Task RefreshAsync(ProviderKind kind, CLIProviderSettings settings)
    {
        if (Frozen) return;
        _statuses[kind] = CLIStatus.Checking;
        OnPropertyChanged(nameof(CLIStatus));
        _statuses[kind] = await CLIProviders.CheckStatusAsync(kind, settings);
        OnPropertyChanged(nameof(CLIStatus));
    }

    public async Task RefreshAllAsync(SettingsStore store)
    {
        if (Frozen) return;
        _statuses[ProviderKind.ClaudeCode] = CLIStatus.Checking;
        _statuses[ProviderKind.Codex] = CLIStatus.Checking;
        OnPropertyChanged(nameof(CLIStatus));

        var claudeTask = CLIProviders.CheckStatusAsync(ProviderKind.ClaudeCode, store.Cli(ProviderKind.ClaudeCode));
        var codexTask = CLIProviders.CheckStatusAsync(ProviderKind.Codex, store.Cli(ProviderKind.Codex));
        _statuses[ProviderKind.ClaudeCode] = await claudeTask;
        _statuses[ProviderKind.Codex] = await codexTask;
        OnPropertyChanged(nameof(CLIStatus));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
