using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Inkwell.Core;
using Inkwell.Platform;
using Inkwell.Providers;
using Inkwell.Views;

namespace Inkwell;

/// <summary>
/// Notification-area app (no taskbar button, like the Mac app has no Dock icon). Left-click the tray icon for the
/// dashboard flyout, right-click for quick controls. One instance per user; launching again opens the flyout.
/// </summary>
public partial class App : Application
{
    private const string MutexName = @"Local\Inkwell.SingleInstance.v1";
    private const string ShowEventName = @"Local\Inkwell.ShowDashboard.v1";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private AppState? _app;
    private TrayIcon? _tray;
    private FlyoutWindow? _flyout;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnCrash;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log(a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) => { Log(a.Exception); a.SetObserved(); };

#if DEBUG
        // README screenshots with sample data (never touches real settings): Inkwell.exe --screenshots <dir>
        var shotIdx = Array.FindIndex(e.Args, a => a.Equals("--screenshots", StringComparison.OrdinalIgnoreCase));
        if (shotIdx >= 0 && shotIdx + 1 < e.Args.Length)
        {
            ThemeManager.Initialize();
            _ = Screenshots.RunAsync(e.Args[shotIdx + 1]);
            return;
        }
#endif

        // Single instance: a second launch just asks the first one to show its dashboard.
        _mutex = new Mutex(true, MutexName, out var isFirst);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        if (!isFirst)
        {
            _showEvent.Set();
            Shutdown();
            return;
        }
        var waiter = new Thread(() =>
        {
            while (_showEvent.WaitOne())
                Dispatcher.BeginInvoke(() => _flyout?.ShowFlyout(_tray?.IconRect()));
        }) { IsBackground = true, Name = "Inkwell show listener" };
        waiter.Start();

        ThemeManager.Initialize();

        _app = new AppState();
        _tray = new TrayIcon();
        TextBridge.OwnerHandle = _tray.Handle;
        _flyout = new FlyoutWindow(_app);
        _flyout.OpenSettingsRequested += () => SettingsWindow.Open(_app);
        _flyout.OpenSettingsPageRequested += p => SettingsWindow.Open(_app, p);

        _tray.LeftClick += () => _flyout.Toggle(_tray.IconRect());
        _tray.RightClick += ShowMenu;
        _app.PropertyChanged += (_, _) => UpdateTray();
        _app.Settings.Changed += UpdateTray;
        UpdateTray();

        _app.Start();

        if (e.Args.Contains("--updated", StringComparer.OrdinalIgnoreCase))
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            t.Tick += (_, _) => { t.Stop(); HudController.Done($"Updated to Inkwell {UpdateChecker.CurrentVersionText}"); };
            t.Start();
        }

        // Testing aid: --open flyout | provider:<kind> | settings:<page> | hud-working | hud-done | hud-failed
        var openIdx = Array.FindIndex(e.Args, a => a.Equals("--open", StringComparison.OrdinalIgnoreCase));
        if (openIdx >= 0 && openIdx + 1 < e.Args.Length)
        {
            var what = e.Args[openIdx + 1].ToLowerInvariant();
            Dispatcher.BeginInvoke(() => OpenForTesting(what), DispatcherPriority.ApplicationIdle);
            return;
        }

        // Started by hand (not at login): show where Inkwell lives — new tray icons are often tucked away.
        if (!e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase))
            Dispatcher.BeginInvoke(() => _flyout.ShowFlyout(_tray.IconRect()), DispatcherPriority.ApplicationIdle);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _app?.Stop();
        _tray?.Dispose();
        try { _mutex?.ReleaseMutex(); } catch { }
        base.OnExit(e);
    }

    private void UpdateTray()
    {
        if (_app is null || _tray is null) return;
        var state = _app.IsWorking ? TrayState.Working : !_app.Settings.Prefs.IsEnabled ? TrayState.Paused : TrayState.Normal;
        _tray.Update(state, $"Inkwell — {_app.StatusText}");
    }

    // MARK: Right-click menu (same items as the Mac menu bar menu)

    private void ShowMenu()
    {
        if (_app is null || _tray is null || _flyout is null) return;
        _flyout.HideFlyout();
        var s = _app.Settings;
        var active = s.Prefs.ActiveProvider;
        int px = (int)Math.Round(16 * Win32.GetDpiForSystem() / 96.0);

        using var menu = new NativeMenu();
        menu.Header(MenuStatus());
        var paused = !s.Prefs.IsEnabled;
        menu.Item(paused ? "Resume Inkwell" : "Pause Inkwell", () => s.SetEnabled(paused));
        menu.Separator();

        var providers = menu.Submenu("Provider", ProviderLogos.Render(active, px));
        foreach (var k in ProviderKindExtensions.All)
        {
            var kind = k;
            var ready = IsUsable(k);
            providers.Item(k.DisplayName() + (ready ? "" : "  —  " + SetupHint(k)), () => Activate(kind),
                isChecked: k == active, radio: true, icon: ProviderLogos.Render(k, px));
        }

        BuildModelMenu(menu.Submenu("Model"), active);
        if (active.UsesApiKeys()) BuildKeysMenu(menu.Submenu("API Keys"), active);

        var triggers = menu.Submenu("Triggers");
        foreach (var t in s.Prefs.Templates.ToList())
        {
            var id = t.Id;
            triggers.Item($"{t.Trigger}\t{t.Name}", () =>
            {
                var cur = s.Prefs.Templates.FirstOrDefault(x => x.Id == id);
                if (cur is not null) s.Update(_ => cur.IsEnabled = !cur.IsEnabled);
            }, isChecked: t.IsEnabled);
        }
        triggers.Separator();
        triggers.Item("Edit Triggers…", () => SettingsWindow.Open(_app, SettingsPage.Templates));

        menu.Separator();
        if (_app.Updates.AvailableVersion is { } v)
            menu.Item($"Update to Inkwell {v}", () => _ = _app.Updates.InstallAsync());
        menu.Item("Open Dashboard", () => _flyout.ShowFlyout(_tray.IconRect()));
        menu.Item("Settings…", () => SettingsWindow.Open(_app));
        menu.Separator();
        menu.Item("Quit Inkwell", () => Shutdown());

        Win32.GetCursorPos(out var pt);
        menu.Show(_tray.Handle, pt.X, pt.Y, alignBottomRight: true);
    }

    private string MenuStatus()
    {
        var s = _app!.Settings;
        if (_app.IsWorking) return "Working…";
        if (!s.Prefs.IsEnabled) return "Paused";
        return $"Listening · {s.Prefs.ActiveProvider.DisplayName()}";
    }

    private void BuildModelMenu(NativeMenu sub, ProviderKind kind)
    {
        var s = _app!.Settings;
        switch (kind)
        {
            case ProviderKind.Gemini:
            case ProviderKind.Groq:
                var order = s.EffectiveModels(kind);
                var catalog = ModelCatalog.Models(kind);
                foreach (var m in catalog) ModelItem(m.Id, m.Name);
                foreach (var id in order.Where(id => catalog.All(c => c.Id != id))) ModelItem(id, id);
                sub.Separator();
                sub.Header("✓ primary · numbers show fallback order");
                void ModelItem(string id, string title)
                {
                    var i = order.IndexOf(id);
                    var label = i > 0 ? $"{title}   (fallback {i})" : title;
                    sub.Item(label, () => s.SetPrimaryModel(kind, id), isChecked: i == 0);
                }
                break;
            case ProviderKind.ClaudeCode:
                var current = s.Cli(kind).Model;
                foreach (var m in ModelCatalog.Claude)
                {
                    var id = m.Id;
                    sub.Item(m.Name, () => s.UpdateCli(kind, c => c.Model = id), isChecked: m.Id == current, radio: true);
                }
                break;
            default:
                var cm = s.Cli(kind).Model;
                sub.Item(cm.Length == 0 ? "Default from Codex config" : cm, null, isChecked: true, enabled: false);
                break;
        }
        if (_app.Quota.CliLimitSummary(kind) is { } limit) { sub.Separator(); sub.Header(limit); }
        sub.Separator();
        sub.Item("Manage Models…", () => _flyout!.ShowFlyout(_tray!.IconRect(), (kind, false)));
    }

    private void BuildKeysMenu(NativeMenu sub, ProviderKind kind)
    {
        var s = _app!.Settings;
        var cfg = s.Api(kind);
        var models = s.EffectiveModels(kind);
        if (cfg.Keys.Count == 0) sub.Header($"No {kind.DisplayName()} keys yet");
        foreach (var meta in cfg.Keys.ToList())
        {
            var (badge, _, _) = QuotaTracker.Badge(meta.IsEnabled ? _app.Quota.Status(meta.Id, models) : null);
            var id = meta.Id;
            var enable = !meta.IsEnabled;
            sub.Item($"{meta.Label}  ••{meta.Hint}  ·  {badge}", () =>
            {
                s.SetKeyEnabled(kind, id, enable);
                if (enable) _app.Quota.ClearInvalid(id);
            }, isChecked: meta.IsEnabled);
        }
        sub.Separator();
        sub.Item("Add Key…", () => _flyout!.ShowFlyout(_tray!.IconRect(), (kind, true)));
        if (kind.KeyUrl() is { } url) sub.Item($"Get a Free {kind.DisplayName()} Key", () => Shell.Open(url));
    }

    private bool IsUsable(ProviderKind k) =>
        k.UsesApiKeys() ? _app!.Settings.HasUsableKey(k) : _app!.CliStatus.Status(k).State == CLIStatusState.Ready;

    private string SetupHint(ProviderKind k)
    {
        if (k.UsesApiKeys()) return "add a key";
        return _app!.CliStatus.Status(k).State switch
        {
            CLIStatusState.NotInstalled => "not installed",
            CLIStatusState.LoggedOut => "not signed in",
            CLIStatusState.Checking => "checking…",
            _ => "set up",
        };
    }

    private async void Activate(ProviderKind kind)
    {
        if (kind.UsesApiKeys() && !_app!.Settings.HasUsableKey(kind))
        {
            _flyout!.ShowFlyout(_tray!.IconRect(), (kind, true));
            return;
        }
        if (await ProviderActions.ActivateAsync(kind, _app!.Settings, _app.CliStatus) is not null)
            _flyout!.ShowFlyout(_tray!.IconRect(), (kind, false));
    }

    private void OpenForTesting(string what)
    {
        var parts = what.Split(':');
        switch (parts[0])
        {
            case "flyout": _flyout!.ShowFlyout(_tray!.IconRect()); break;
            case "provider":
                var kind = Enum.TryParse<ProviderKind>(parts.ElementAtOrDefault(1) ?? "", true, out var k) ? k : ProviderKind.Gemini;
                _flyout!.ShowFlyout(_tray!.IconRect(), (kind, false));
                break;
            case "settings":
                var page = Enum.TryParse<SettingsPage>(parts.ElementAtOrDefault(1) ?? "", true, out var p) ? p : SettingsPage.Providers;
                SettingsWindow.Open(_app!, page);
                break;
            case "hud-working": HudController.Working("Fixing grammar"); break;
            case "hud-done": HudController.Done("Done"); break;
            case "hud-failed": HudController.Failed("Claude Code's 5-hour limit is reached. Resets 3:00 PM. Switch to Gemini to keep going."); break;
        }
    }

    // MARK: Crash safety

    private void OnCrash(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception);
        e.Handled = true; // a background app should keep running
    }

    private static void Log(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Inkwell");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "errors.log"), $"[{DateTime.Now:O}] {ex}\r\n\r\n");
        }
        catch { }
    }
}
