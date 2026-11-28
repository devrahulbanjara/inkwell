using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Inkwell.Core;
using Inkwell.Platform;
using Inkwell.Providers;

namespace Inkwell.Views;

/// <summary>Settings → AI Provider: pick a provider, manage keys, model order and fallbacks, check CLIs, test.</summary>
public sealed class ProvidersPage : StackPanel, IDisposable
{
    private readonly AppState _app;
    private readonly SettingsWindow _window;
    private ProviderKind _inspected;
    private string? _notice;
    private bool _testing;
    private (bool Ok, string Message)? _testResult;
    private bool _queued;
    private readonly TextBox _codexModel = new() { FontFamily = UI.MonoFont };
    private readonly TextBox _binaryPath = new() { FontFamily = UI.MonoFont, FontSize = 12.5 };

    private SettingsStore S => _app.Settings;

    public ProvidersPage(AppState app, SettingsWindow window)
    {
        _app = app;
        _window = window;
        _inspected = app.Settings.Prefs.ActiveProvider;
        Input.SetPlaceholder(_codexModel, @"Default from %USERPROFILE%\.codex\config.toml");
        _codexModel.LostFocus += (_, _) => CommitCodex();
        _codexModel.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitCodex(); };
        _binaryPath.LostFocus += (_, _) => CommitPath();
        _binaryPath.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitPath(); };

        S.Changed += Queue;
        app.Quota.Changed += Queue;
        app.CliStatus.PropertyChanged += OnCli;
        Build();
        _ = app.CliStatus.RefreshAllAsync(S);
    }

    private void OnCli(object? s, System.ComponentModel.PropertyChangedEventArgs e) => Queue();

    public void Dispose()
    {
        S.Changed -= Queue;
        _app.Quota.Changed -= Queue;
        _app.CliStatus.PropertyChanged -= OnCli;
    }

    private void Queue()
    {
        if (_queued) return;
        _queued = true;
        Dispatcher.BeginInvoke(() => { _queued = false; Build(); });
    }

    private void Build()
    {
        foreach (var tb in new[] { _codexModel, _binaryPath }) if (tb.Parent is Panel p) p.Children.Remove(tb);
        Children.Clear();
        var intro = UI.Text("Choose where Inkwell sends your text. Gemini and Groq are free with API keys; Claude Code and Codex use the plan you're already signed into.",
            14, "TextSecondaryBrush", wrap: true);
        intro.Margin = new Thickness(0, 0, 0, 16);
        Children.Add(intro);

        var grid = new UniformGrid { Columns = 2, Margin = new Thickness(-6, 0, -6, 18) };
        foreach (var k in ProviderKindExtensions.All) grid.Children.Add(ProviderCard(k));
        Children.Add(grid);

        Children.Add(DetailHeader());
        var detail = new StackPanel();
        if (_inspected.UsesApiKeys()) BuildApi(detail); else BuildCli(detail);
        Children.Add(detail);
        Children.Add(TestRow());
    }

    // MARK: Cards

    private UIElement ProviderCard(ProviderKind k)
    {
        bool active = S.Prefs.ActiveProvider == k, selected = _inspected == k;
        var tint = ThemeManager.Hex(k.TintHex());
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var logo = ProviderLogos.Create(k, 40);
        logo.VerticalAlignment = VerticalAlignment.Top;
        logo.Margin = new Thickness(0, 0, 14, 0);
        grid.Children.Add(logo);
        var text = new StackPanel();
        var name = new StackPanel { Orientation = Orientation.Horizontal };
        name.Children.Add(UI.Text(k.DisplayName(), 15, weight: FontWeights.SemiBold));
        if (active)
        {
            var check = UI.Glyph("", 14, ThemeManager.Hex("#22C55E"));
            check.Margin = new Thickness(8, 1, 0, 0);
            name.Children.Add(check);
        }
        text.Children.Add(name);
        text.Children.Add(UI.Text(k.Subtitle(), 12.5, "TextSecondaryBrush"));
        var status = UI.Text(StatusLine(k), 11.5, "TextTertiaryBrush", mono: true);
        status.Margin = new Thickness(0, 3, 0, 0);
        text.Children.Add(status);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var card = UI.Card(grid, new Thickness(16, 14, 16, 14));
        card.Margin = new Thickness(6);
        card.Cursor = Cursors.Hand;
        if (active) card.Background = new SolidColorBrush(Color.FromArgb(ThemeManager.IsDark ? (byte)0x22 : (byte)0x16, tint.R, tint.G, tint.B));
        if (selected)
        {
            card.BorderBrush = new SolidColorBrush(Color.FromArgb(0xE6, tint.R, tint.G, tint.B));
            card.BorderThickness = new Thickness(2);
            card.Padding = new Thickness(15, 13, 15, 13);
        }
        card.MouseEnter += (_, _) => { if (!active) card.SetResourceReference(Border.BackgroundProperty, "CardHoverBrush"); };
        card.MouseLeave += (_, _) => { if (!active) card.SetResourceReference(Border.BackgroundProperty, "CardBrush"); };
        card.MouseLeftButtonUp += (_, _) =>
        {
            if (_inspected == k) return;
            _inspected = k;
            _notice = null;
            _testResult = null;
            Build();
        };
        return card;
    }

    private string StatusLine(ProviderKind k)
    {
        if (k.UsesApiKeys())
        {
            int keys = S.Api(k).Keys.Count(x => x.IsEnabled);
            if (keys == 0) return "No keys yet";
            return $"{keys} key{(keys == 1 ? "" : "s")} · {S.EffectiveModels(k).FirstOrDefault() ?? "—"}";
        }
        if (_app.Quota.CliLimitSummary(k) is { } limit && limit.Contains("reached")) return limit;
        return _app.CliStatus.Status(k).State switch
        {
            CLIStatusState.Checking => "Checking…",
            CLIStatusState.NotInstalled => "Not installed",
            CLIStatusState.LoggedOut => "Not signed in",
            _ => $"Signed in · {(S.Cli(k).Model.Length == 0 ? "default model" : S.Cli(k).Model)}",
        };
    }

    private UIElement DetailHeader()
    {
        var k = _inspected;
        bool active = S.Prefs.ActiveProvider == k;
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new TextBlock { Text = k.DisplayName(), Style = UI.Style("SubtitleText"), VerticalAlignment = VerticalAlignment.Center });
        if (active)
        {
            var badge = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 2, 10, 3), Margin = new Thickness(12, 2, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            badge.SetResourceReference(Border.BackgroundProperty, "InfoSuccessBrush");
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(UI.Glyph("", 12, "InfoSuccessIconBrush"));
            var at = UI.Text("Active", 12, "InfoSuccessIconBrush", FontWeights.SemiBold);
            at.Margin = new Thickness(6, 0, 0, 0);
            sp.Children.Add(at);
            badge.Child = sp;
            title.Children.Add(badge);
        }
        grid.Children.Add(title);
        if (_notice is not null)
        {
            var n = UI.Text(_notice, 12.5, "InfoWarningIconBrush", wrap: true);
            n.VerticalAlignment = VerticalAlignment.Center;
            n.HorizontalAlignment = HorizontalAlignment.Right;
            n.Margin = new Thickness(12, 0, 12, 0);
            Grid.SetColumn(n, 1);
            grid.Children.Add(n);
        }
        if (!active)
        {
            var use = UI.Button($"Use {k.DisplayName()}", async () =>
            {
                _notice = null;
                _notice = await ProviderActions.ActivateAsync(k, S, _app.CliStatus);
                Build();
            }, primary: true);
            Grid.SetColumn(use, 2);
            grid.Children.Add(use);
        }
        return grid;
    }

    // MARK: Gemini / Groq

    private void BuildApi(StackPanel p)
    {
        var k = _inspected;
        var cfg = S.Api(k);
        p.Children.Add(SectionHeader("API keys", "Keys are tried in turn; a rate-limited key rests while the next one works.",
            UI.Button("Add key", () => AddKeyDialog.Show(_window, _app, k), primary: true, glyph: "")));

        if (cfg.Keys.Count == 0)
        {
            var empty = new StackPanel();
            empty.Children.Add(UI.Text($"No {k.DisplayName()} keys yet.", 14, weight: FontWeights.SemiBold));
            if (k.KeyUrl() is { } url)
            {
                var link = UI.Link($"Get a free {k.DisplayName()} API key →", () => Shell.Open(url), 14);
                link.Margin = new Thickness(0, 6, 0, 0);
                empty.Children.Add(link);
            }
            var card = UI.Card(empty);
            card.Margin = new Thickness(0, 0, 0, 12);
            p.Children.Add(card);
        }
        else
        {
            foreach (var meta in cfg.Keys.ToList()) p.Children.Add(KeyRow(k, meta, S.EffectiveModels(k)));
        }

        if (k == ProviderKind.Gemini)
        {
            var note = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 4, 0, 4) };
            note.Children.Add(UI.Glyph("", 13, "TextSecondaryBrush"));
            var t = UI.Text("Gemini limits are per Google Cloud project. Keys from the same project share one quota, so create extra keys in different projects.",
                12.5, "TextSecondaryBrush", wrap: true);
            t.Margin = new Thickness(8, 0, 0, 0);
            t.MaxWidth = 640;
            note.Children.Add(t);
            p.Children.Add(note);
        }

        p.Children.Add(SectionHeader("Models", "The first model is used normally; the rest are fallbacks when it's limited.",
            UI.Button("Choose models…", () => ModelsDialog.Show(_window, _app, k))));
        var models = S.EffectiveModels(k);
        for (int i = 0; i < models.Count; i++) p.Children.Add(ModelOrderRow(k, models[i], i));

        var fallback = new StackPanel();
        fallback.Children.Add(UI.Text("When a key or model hits its limit", 14, weight: FontWeights.SemiBold));
        foreach (var order in new[] { FallbackOrder.KeysFirst, FallbackOrder.ModelsFirst })
        {
            var rb = new RadioButton { Content = order.Label(), IsChecked = cfg.FallbackOrder == order, GroupName = "fallback" + k, FontSize = 14 };
            rb.Checked += (_, _) => { if (S.Api(k).FallbackOrder != order) S.UpdateApi(k, a => a.FallbackOrder = order); };
            fallback.Children.Add(rb);
        }
        var fcard = UI.Card(fallback);
        fcard.Margin = new Thickness(0, 14, 0, 0);
        p.Children.Add(fcard);
    }

    private UIElement KeyRow(ProviderKind k, APIKeyMeta meta, System.Collections.Generic.List<string> models)
    {
        var status = meta.IsEnabled ? _app.Quota.Status(meta.Id, models) : null;
        var (text, color, help) = QuotaTracker.Badge(status);
        if (status is { Kind: KeyStatusKind.Partial } st) text = $"Some models resting · {st.Until:t}";
        if (status is { Kind: KeyStatusKind.Cooling } sc) text = sc.Daily ? $"Daily limit · resets {LimitText.When(sc.Until!.Value)}" : $"Rate-limited · back {sc.Until:t}";

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var toggle = UI.Toggle(meta.IsEnabled, on =>
        {
            S.SetKeyEnabled(k, meta.Id, on);
            if (on) _app.Quota.ClearInvalid(meta.Id);
        });
        toggle.Margin = new Thickness(0, 0, 16, 0);
        grid.Children.Add(toggle);

        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(UI.Text(meta.Label, 14, weight: FontWeights.SemiBold));
        names.Children.Add(UI.Text("••••••••" + meta.Hint, 12, "TextSecondaryBrush", mono: true));
        Grid.SetColumn(names, 1);
        grid.Children.Add(names);

        var c = ThemeManager.Hex(color);
        var pill = new Border
        {
            CornerRadius = new CornerRadius(11), Padding = new Thickness(9, 3, 10, 4), VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb(0x24, c.R, c.G, c.B)), ToolTip = help,
        };
        var ps = new StackPanel { Orientation = Orientation.Horizontal };
        ps.Children.Add(new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = new SolidColorBrush(c), Margin = new Thickness(0, 1, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        ps.Children.Add(UI.Text(text, 12));
        pill.Child = ps;
        Grid.SetColumn(pill, 2);
        grid.Children.Add(pill);

        var del = UI.IconButton("", "Remove key", () => _window.Confirm("Remove this key?",
            $"“{meta.Label}” (••••{meta.Hint}) will be deleted from this PC.", "Remove", () =>
            {
                S.RemoveKey(k, meta.Id);
                _app.Quota.Forget(meta.Id);
            }));
        del.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(del, 3);
        grid.Children.Add(del);

        var card = UI.Card(grid, new Thickness(16, 10, 10, 10));
        card.Margin = new Thickness(0, 0, 0, 4);
        card.Opacity = meta.IsEnabled ? 1 : 0.6;
        return card;
    }

    private UIElement ModelOrderRow(ProviderKind k, string id, int index)
    {
        var info = ModelCatalog.Info(id, k);
        var tint = ThemeManager.Hex(k.TintHex());
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var badge = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 2, 9, 3), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        var bt = UI.Text(index == 0 ? "Primary" : $"Fallback {index}", 11.5, index == 0 ? "OnAccentBrush" : "TextSecondaryBrush", FontWeights.Bold);
        if (index == 0) { badge.Background = new SolidColorBrush(tint); bt.Foreground = Brushes.White; }
        else badge.SetResourceReference(Border.BackgroundProperty, "ControlBorderBrush");
        badge.Child = bt;
        grid.Children.Add(badge);
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(UI.Text(info?.Name ?? id, 14));
        names.Children.Add(UI.Text(id, 11.5, "TextTertiaryBrush", mono: true));
        Grid.SetColumn(names, 1);
        grid.Children.Add(names);
        TextBlock note;
        if (info is not null) note = UI.Text(info.Note, 12, "TextSecondaryBrush", wrap: true);
        else
        {
            var paid = ModelCatalog.IsKnownPaid(id, k);
            note = UI.Text(paid ? "Not on free tier" : "Custom model", 12, paid ? "InfoErrorIconBrush" : "TextSecondaryBrush");
        }
        note.MaxWidth = 280;
        note.TextAlignment = TextAlignment.Right;
        note.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(note, 2);
        grid.Children.Add(note);
        var card = UI.Card(grid, new Thickness(16, 9, 16, 9));
        card.Margin = new Thickness(0, 0, 0, 4);
        return card;
    }

    // MARK: Claude Code / Codex

    private void BuildCli(StackPanel p)
    {
        var k = _inspected;
        var status = _app.CliStatus.Status(k);
        p.Children.Add(QuickProviderControl.CliStatusCard(_app, k, status, compact: false, () => _ = _app.CliStatus.RefreshAsync(k, S.Cli(k))));

        if (_app.Quota.CliLimitSummary(k) is { } limit)
        {
            var reached = limit.Contains("reached", StringComparison.Ordinal);
            var bar = new InfoBar { Margin = new Thickness(0, 0, 0, 12), IsClosable = false };
            bar.Set(reached ? Severity.Warning : Severity.Info, reached ? "Plan limit reached" : "Plan usage",
                limit + (reached ? ". Inkwell will use it again automatically after the reset." : ""));
            p.Children.Add(bar);
        }

        p.Children.Add(SectionHeader("Model", k == ProviderKind.ClaudeCode
            ? "Uses your Claude plan's limits — Haiku is quickest and lightest."
            : "Leave empty to use the model from your Codex config.", null));

        if (k == ProviderKind.ClaudeCode)
        {
            var current = S.Cli(k).Model;
            var tint = ThemeManager.Hex(k.TintHex());
            foreach (var m in ModelCatalog.Claude)
            {
                bool selected = m.Id == current;
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition());
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.Children.Add(new RadioButton { IsChecked = selected, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
                var names = new StackPanel();
                var title = new StackPanel { Orientation = Orientation.Horizontal };
                title.Children.Add(UI.Text(m.Name, 14, weight: FontWeights.SemiBold));
                if (m.Recommended) title.Children.Add(UI.Tag("Recommended", "#16A34A"));
                names.Children.Add(title);
                names.Children.Add(UI.Text(m.Note, 12.5, "TextSecondaryBrush"));
                Grid.SetColumn(names, 1);
                grid.Children.Add(names);
                var flag = UI.Text($"--model {m.Id}", 11.5, "TextTertiaryBrush", mono: true);
                flag.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(flag, 2);
                grid.Children.Add(flag);
                var card = UI.Card(grid, new Thickness(14, 9, 16, 9));
                card.Margin = new Thickness(0, 0, 0, 4);
                card.Cursor = Cursors.Hand;
                if (selected) card.Background = new SolidColorBrush(Color.FromArgb(ThemeManager.IsDark ? (byte)0x22 : (byte)0x16, tint.R, tint.G, tint.B));
                var id = m.Id;
                card.MouseLeftButtonUp += (_, _) => S.UpdateCli(k, c => c.Model = id);
                p.Children.Add(card);
            }
        }
        else
        {
            var box = new StackPanel();
            box.Children.Add(UI.Text("Model ID (optional)", 13, "TextSecondaryBrush"));
            if (!_codexModel.IsKeyboardFocused) _codexModel.Text = S.Cli(k).Model;
            _codexModel.Margin = new Thickness(0, 6, 0, 12);
            box.Children.Add(_codexModel);
            box.Children.Add(UI.Toggle(S.Cli(k).FastMode, on => S.UpdateCli(k, c => c.FastMode = on), "Fast mode (low reasoning effort)"));
            p.Children.Add(UI.Card(box));
        }

        // Advanced: custom path
        var adv = new StackPanel();
        adv.Children.Add(UI.Text($"Path to {k.CliName()}", 13, "TextSecondaryBrush"));
        if (!_binaryPath.IsKeyboardFocused) _binaryPath.Text = S.Cli(k).BinaryPath;
        Input.SetPlaceholder(_binaryPath, k == ProviderKind.ClaudeCode ? @"Auto-detect (e.g. %USERPROFILE%\.local\bin\claude.exe)" : @"Auto-detect (e.g. %APPDATA%\npm\codex.cmd)");
        _binaryPath.Margin = new Thickness(0, 6, 0, 8);
        adv.Children.Add(_binaryPath);
        var note = new StackPanel { Orientation = Orientation.Horizontal };
        note.Children.Add(UI.Glyph("", 13, "TextSecondaryBrush"));
        var nt = UI.Text($@"Runs headless inside %USERPROFILE%\.inkwell so these sessions never mix with your normal {k.DisplayName()} history.", 12.5, "TextSecondaryBrush", wrap: true);
        nt.Margin = new Thickness(8, 0, 0, 0);
        note.Children.Add(nt);
        adv.Children.Add(note);
        var expander = new Expander
        {
            Header = UI.Text("Advanced", 14, weight: FontWeights.SemiBold),
            Content = new Border { Padding = new Thickness(0, 10, 0, 0), Child = adv },
            IsExpanded = S.Cli(k).BinaryPath.Length > 0 || status.State == CLIStatusState.NotInstalled,
        };
        expander.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        var ec = UI.Card(expander);
        ec.Margin = new Thickness(0, 12, 0, 0);
        p.Children.Add(ec);
    }

    private void CommitCodex()
    {
        var v = _codexModel.Text.Trim();
        if (v != S.Cli(ProviderKind.Codex).Model) S.UpdateCli(ProviderKind.Codex, c => c.Model = v);
    }

    private void CommitPath()
    {
        var k = _inspected;
        if (k.UsesApiKeys()) return;
        var v = _binaryPath.Text.Trim();
        if (v == S.Cli(k).BinaryPath) return;
        S.UpdateCli(k, c => c.BinaryPath = v);
        _ = _app.CliStatus.RefreshAsync(k, S.Cli(k));
    }

    // MARK: Test

    private UIElement TestRow()
    {
        var row = new StackPanel { Margin = new Thickness(0, 22, 0, 0) };
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        FrameworkElement icon = _testing ? UI.Spinner(14) : UI.Glyph("", 14);
        icon.Margin = new Thickness(0, 0, 8, 0);
        content.Children.Add(icon);
        content.Children.Add(new TextBlock { Text = _testing ? "Testing…" : "Send a test request", VerticalAlignment = VerticalAlignment.Center });
        var btn = new Button { Style = UI.Style("FluentButton"), Content = content, HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = !_testing };
        btn.Click += async (_, _) =>
        {
            var k = _inspected;
            _testing = true;
            _testResult = null;
            Build();
            var r = await ProviderActions.TestAsync(k, _app.Router);
            _testing = false;
            if (k == _inspected) _testResult = r;
            Build();
        };
        row.Children.Add(btn);
        if (_testResult is { } res)
        {
            var bar = new InfoBar { Margin = new Thickness(0, 10, 0, 0), IsClosable = false };
            if (res.Ok) bar.Set(Severity.Success, "It works", res.Message);
            else
            {
                var (t, d) = Messages.Split(res.Message);
                bar.Set(Messages.IsWarning(res.Message) ? Severity.Warning : Severity.Error, t, d);
            }
            row.Children.Add(bar);
        }
        return row;
    }

    private static UIElement SectionHeader(string title, string detail, UIElement? trailing)
    {
        var g = new Grid { Margin = new Thickness(0, 14, 0, 10) };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var s = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
        s.Children.Add(UI.Text(title, 15, weight: FontWeights.SemiBold));
        s.Children.Add(UI.Text(detail, 12.5, "TextSecondaryBrush", wrap: true));
        g.Children.Add(s);
        if (trailing is FrameworkElement fe)
        {
            fe.VerticalAlignment = VerticalAlignment.Bottom;
            fe.Margin = new Thickness(16, 0, 0, 0);
            Grid.SetColumn(fe, 1);
            g.Children.Add(fe);
        }
        return g;
    }
}
