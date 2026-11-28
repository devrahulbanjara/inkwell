using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Inkwell.Core;
using Inkwell.Platform;
using Inkwell.Providers;

namespace Inkwell.Views;

/// <summary>Provider, key and model controls right in the tray flyout (the Mac popover's provider page).</summary>
public partial class QuickProviderControl : UserControl
{
    private readonly AppState _app;
    private ProviderKind _kind;
    private bool _addingKey;
    private bool _validating;
    private string? _keyError;
    private Guid? _confirmRemove;
    private string? _notice;
    private bool _testing;
    private (bool Ok, string Message)? _testResult;
    private bool _rebuildQueued;
    private Button? _addKeyButton, _customAddButton;

    // Inputs live across rebuilds so typing is never lost.
    private readonly PasswordBox _newKey = new() { FontFamily = UI.MonoFont, FontSize = 13 };
    private readonly TextBox _newLabel = new() { FontSize = 13 };
    private readonly TextBox _customModel = new() { FontFamily = UI.MonoFont, FontSize = 12 };
    private readonly TextBox _codexModel = new() { FontFamily = UI.MonoFont, FontSize = 12 };

    public event Action? BackRequested;
    public event Action? OpenSettingsRequested;
    public event Action<bool>? MenuOpening;

    private SettingsStore S => _app.Settings;

    public QuickProviderControl(AppState app, ProviderKind kind, bool addKey)
    {
        InitializeComponent();
        _app = app;
        _kind = kind;
        _addingKey = addKey && kind.UsesApiKeys();

        Input.SetPlaceholder(_newLabel, "Label (optional)");
        Input.SetPlaceholder(_customModel, "Other model ID");
        Input.SetPlaceholder(_codexModel, "Default from Codex config");
        _newKey.KeyDown += (_, e) => { if (e.Key == Key.Enter) AddKey(); };
        _newLabel.KeyDown += (_, e) => { if (e.Key == Key.Enter) AddKey(); };
        _customModel.KeyDown += (_, e) => { if (e.Key == Key.Enter) AddCustomModel(); };
        _customModel.TextChanged += (_, _) => { if (_customAddButton is not null) _customAddButton.IsEnabled = _customModel.Text.Trim().Length > 0; };
        _newKey.PasswordChanged += (_, _) => { if (_addKeyButton is not null) _addKeyButton.IsEnabled = !_validating && _newKey.Password.Trim().Length > 0; };
        _codexModel.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitCodexModel(); };
        _codexModel.LostFocus += (_, _) => CommitCodexModel();

        S.Changed += QueueRebuild;
        app.Quota.Changed += QueueRebuild;
        app.CliStatus.PropertyChanged += (_, _) => QueueRebuild();
        Unloaded += (_, _) => { S.Changed -= QueueRebuild; app.Quota.Changed -= QueueRebuild; };

        Rebuild();
        _ = app.CliStatus.RefreshAllAsync(S);
        if (_addingKey) Loaded += (_, _) => _newKey.Focus();
    }

    private void QueueRebuild()
    {
        if (_rebuildQueued) return;
        _rebuildQueued = true;
        Dispatcher.BeginInvoke(() => { _rebuildQueued = false; Rebuild(); });
    }

    private void Rebuild()
    {
        BuildStrip();
        Detach(_newKey, _newLabel, _customModel, _codexModel);
        Body.Children.Clear();
        Body.Children.Add(ActivationRow());
        if (_kind.UsesApiKeys()) BuildApi(); else BuildCli();
        Body.Children.Add(TestRow());
    }

    // MARK: Provider strip

    private void BuildStrip()
    {
        ProviderStrip.Children.Clear();
        foreach (var k in ProviderKindExtensions.All)
        {
            bool selected = k == _kind, active = S.Prefs.ActiveProvider == k;
            var logo = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
            logo.Children.Add(ProviderLogos.Create(k, 28));
            if (active)
            {
                var check = new Border
                {
                    Width = 13, Height = 13, CornerRadius = new CornerRadius(6.5), Background = ThemeManager.Brush("#22C55E"),
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -5, -6, 0),
                    BorderThickness = new Thickness(1.5), Child = UI.Glyph("", 7, Colors.White),
                };
                check.SetResourceReference(Border.BorderBrushProperty, "CardSolidBrush");
                ((TextBlock)check.Child).HorizontalAlignment = HorizontalAlignment.Center;
                logo.Children.Add(check);
            }
            var name = UI.Text(k.DisplayName(), 11.5, selected ? "TextPrimaryBrush" : "TextSecondaryBrush", selected ? FontWeights.SemiBold : FontWeights.Normal);
            name.HorizontalAlignment = HorizontalAlignment.Center;
            name.Margin = new Thickness(0, 6, 0, 0);
            var stack = new StackPanel();
            stack.Children.Add(logo);
            stack.Children.Add(name);
            var tint = ThemeManager.Hex(k.TintHex());
            var tile = new Border
            {
                Style = UI.Style("FluentCard"), Padding = new Thickness(4, 9, 4, 7), Margin = new Thickness(3), Child = stack, Cursor = Cursors.Hand,
                ToolTip = k.Subtitle(),
            };
            if (selected)
            {
                tile.Background = new SolidColorBrush(Color.FromArgb(ThemeManager.IsDark ? (byte)0x26 : (byte)0x1A, tint.R, tint.G, tint.B));
                tile.BorderBrush = new SolidColorBrush(Color.FromArgb(0xD9, tint.R, tint.G, tint.B));
                tile.BorderThickness = new Thickness(1.5);
            }
            var kind = k;
            tile.MouseLeftButtonUp += (_, _) =>
            {
                if (_kind == kind) return;
                _kind = kind;
                _addingKey = false; _keyError = null; _confirmRemove = null; _notice = null; _testResult = null;
                _newKey.Clear(); _newLabel.Clear(); _customModel.Clear();
                Rebuild();
                UI.FadeIn(Body, 220);
            };
            ProviderStrip.Children.Add(tile);
        }
    }

    private UIElement ActivationRow()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
        if (S.Prefs.ActiveProvider == _kind)
        {
            row.Children.Add(UI.Glyph("", 15, ThemeManager.Hex("#22C55E")));
            var t = UI.Text($"{_kind.DisplayName()} is active", 13.5, weight: FontWeights.SemiBold);
            t.Margin = new Thickness(8, 0, 0, 0);
            row.Children.Add(t);
            return row;
        }
        var btn = UI.Button($"Use {_kind.DisplayName()}", async () =>
        {
            _notice = await ProviderActions.ActivateAsync(_kind, S, _app.CliStatus);
            Rebuild();
        }, primary: true, glyph: "");
        btn.FontSize = 13;
        row.Children.Add(btn);
        if (_notice is not null)
        {
            var n = UI.Text(_notice, 12, "InfoWarningIconBrush", wrap: true);
            n.VerticalAlignment = VerticalAlignment.Center;
            n.Margin = new Thickness(10, 0, 0, 0);
            n.MaxWidth = 190;
            row.Children.Add(n);
        }
        return row;
    }

    // MARK: Gemini / Groq

    private void BuildApi()
    {
        var cfg = S.Api(_kind);
        Body.Children.Add(SectionTitle("API KEYS", UI.IconButton(_addingKey ? "" : "", _addingKey ? "Cancel" : "Add a key", () =>
        {
            _addingKey = !_addingKey;
            _keyError = null;
            Rebuild();
            if (_addingKey) _newKey.Focus();
        }, 12)));

        if (_addingKey || cfg.Keys.Count == 0) Body.Children.Add(AddKeyForm());

        var models = S.EffectiveModels(_kind);
        foreach (var meta in cfg.Keys.ToList()) Body.Children.Add(KeyRow(meta, models));

        Body.Children.Add(SectionTitle("MODELS", null));
        var hint = UI.Text("#1 is used first; the rest are fallbacks when it's limited. Click to add or remove.", 11.5, "TextSecondaryBrush", wrap: true);
        hint.Margin = new Thickness(0, -4, 0, 8);
        Body.Children.Add(hint);

        var catalog = ModelCatalog.Models(_kind);
        for (int i = 0; i < models.Count; i++) Body.Children.Add(ModelRow(models[i], i, ModelCatalog.Info(models[i], _kind)));
        foreach (var info in catalog.Where(m => !models.Contains(m.Id))) Body.Children.Add(ModelRow(info.Id, null, info));

        var custom = new Grid { Margin = new Thickness(0, 4, 0, 12) };
        custom.ColumnDefinitions.Add(new ColumnDefinition());
        custom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        custom.Children.Add(_customModel);
        var add = UI.Button("Add", AddCustomModel);
        add.Margin = new Thickness(6, 0, 0, 0);
        add.IsEnabled = _customModel.Text.Trim().Length > 0;
        _customAddButton = add;
        Grid.SetColumn(add, 1);
        custom.Children.Add(add);
        Body.Children.Add(custom);

        var limited = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        limited.Children.Add(new TextBlock { Text = "When limited", Style = UI.Style("CaptionText"), VerticalAlignment = VerticalAlignment.Center });
        var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Right, Width = 190, FontSize = 13 };
        combo.Items.Add(new ComboBoxItem { Content = FallbackOrder.KeysFirst.ShortLabel(), Tag = FallbackOrder.KeysFirst });
        combo.Items.Add(new ComboBoxItem { Content = FallbackOrder.ModelsFirst.ShortLabel(), Tag = FallbackOrder.ModelsFirst });
        combo.SelectedIndex = cfg.FallbackOrder == FallbackOrder.KeysFirst ? 0 : 1;
        combo.DropDownOpened += (_, _) => MenuOpening?.Invoke(true);
        combo.DropDownClosed += (_, _) => MenuOpening?.Invoke(false);
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: FallbackOrder o } && o != S.Api(_kind).FallbackOrder)
                S.UpdateApi(_kind, a => a.FallbackOrder = o);
        };
        limited.Children.Add(combo);
        Body.Children.Add(limited);
    }

    private UIElement AddKeyForm()
    {
        var stack = new StackPanel();
        Input.SetPlaceholder(_newKey, _kind.KeyPlaceholder());
        var keyRow = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        keyRow.ColumnDefinitions.Add(new ColumnDefinition());
        keyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        keyRow.Children.Add(_newKey);
        var paste = UI.IconButton("", "Paste key from clipboard", () =>
        {
            try { if (Clipboard.ContainsText()) _newKey.Password = Clipboard.GetText().Trim(); } catch { }
        });
        paste.Margin = new Thickness(4, 0, 0, 0);
        Grid.SetColumn(paste, 1);
        keyRow.Children.Add(paste);
        stack.Children.Add(keyRow);
        _newLabel.Margin = new Thickness(0, 0, 0, 8);
        stack.Children.Add(_newLabel);

        if (_keyError is not null)
        {
            var err = new InfoBar { Margin = new Thickness(0, 0, 0, 8), IsClosable = false };
            err.Set(Severity.Error, "", _keyError);
            stack.Children.Add(err);
        }

        var bottom = new Grid();
        if (_kind.KeyUrl() is { } url)
        {
            var link = UI.Link("Get a free key →", () => Shell.Open(url), 12.5);
            link.VerticalAlignment = VerticalAlignment.Center;
            bottom.Children.Add(link);
        }
        var addContent = new StackPanel { Orientation = Orientation.Horizontal };
        if (_validating)
        {
            var spin = UI.Spinner(13);
            spin.Margin = new Thickness(0, 0, 6, 0);
            addContent.Children.Add(spin);
        }
        addContent.Children.Add(new TextBlock { Text = _validating ? "Checking…" : "Add key", VerticalAlignment = VerticalAlignment.Center });
        var add = new Button { Style = UI.Style("FluentPrimaryButton"), Content = addContent, HorizontalAlignment = HorizontalAlignment.Right, FontSize = 13 };
        add.IsEnabled = !_validating && _newKey.Password.Trim().Length > 0;
        _addKeyButton = add;
        add.Click += (_, _) => AddKey();
        bottom.Children.Add(add);
        stack.Children.Add(bottom);

        var card = UI.Card(stack, new Thickness(10));
        card.Margin = new Thickness(0, 0, 0, 10);
        return card;
    }

    private async void AddKey()
    {
        if (_validating || _newKey.Password.Trim().Length == 0) return;
        _validating = true;
        _keyError = null;
        Rebuild();
        try
        {
            await ProviderActions.AddKeyAsync(_kind, _newKey.Password, _newLabel.Text, S, _app.Quota);
            _addingKey = false;
            _newKey.Clear();
            _newLabel.Clear();
        }
        catch (Exception ex)
        {
            _keyError = ex.Message;
        }
        finally
        {
            _validating = false;
            Rebuild();
        }
    }

    private UIElement KeyRow(APIKeyMeta meta, List<string> models)
    {
        var (text, color, help) = QuotaTracker.Badge(meta.IsEnabled ? _app.Quota.Status(meta.Id, models) : null);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var toggle = UI.Toggle(meta.IsEnabled, on =>
        {
            S.SetKeyEnabled(_kind, meta.Id, on);
            if (on) _app.Quota.ClearInvalid(meta.Id);
        });
        toggle.Margin = new Thickness(0, 0, 10, 0);
        toggle.ToolTip = meta.IsEnabled ? "Turn this key off" : "Turn this key on";
        grid.Children.Add(toggle);

        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(UI.Text(meta.Label, 13, weight: FontWeights.SemiBold));
        names.Children.Add(UI.Text("••••" + meta.Hint, 11, "TextSecondaryBrush", mono: true));
        Grid.SetColumn(names, 1);
        grid.Children.Add(names);

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (_confirmRemove == meta.Id)
        {
            var remove = new Button { Style = UI.Style("FluentDangerButton"), Content = "Remove", FontSize = 12, Padding = new Thickness(10, 3, 10, 4), MinHeight = 26 };
            remove.Click += (_, _) =>
            {
                S.RemoveKey(_kind, meta.Id);
                _app.Quota.Forget(meta.Id);
                _confirmRemove = null;
            };
            right.Children.Add(remove);
            right.Children.Add(UI.IconButton("", "Keep key", () => { _confirmRemove = null; Rebuild(); }, 10));
        }
        else
        {
            var badge = new StackPanel { Orientation = Orientation.Horizontal, ToolTip = help, Margin = new Thickness(6, 0, 2, 0) };
            badge.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 7, Height = 7, Fill = ThemeManager.Brush(color), Margin = new Thickness(0, 1, 5, 0), VerticalAlignment = VerticalAlignment.Center,
            });
            badge.Children.Add(UI.Text(text, 11, "TextSecondaryBrush"));
            right.Children.Add(badge);
            right.Children.Add(UI.IconButton("", "Remove key", () => { _confirmRemove = meta.Id; Rebuild(); }, 12));
        }
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

        var card = UI.Card(grid, new Thickness(10, 7, 6, 7));
        card.Margin = new Thickness(0, 0, 0, 6);
        card.Opacity = meta.IsEnabled ? 1 : 0.6;
        return card;
    }

    private UIElement ModelRow(string id, int? index, ModelInfo? info)
    {
        bool selected = index is not null;
        var tint = ThemeManager.Hex(_kind.TintHex());
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dot = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        if (selected)
        {
            dot.Background = new LinearGradientBrush(Color.FromArgb(255, (byte)Math.Min(255, tint.R + 40), (byte)Math.Min(255, tint.G + 40), (byte)Math.Min(255, tint.B + 40)), tint, 90);
            dot.Child = new TextBlock { Text = (index!.Value + 1).ToString(), Foreground = Brushes.White, FontSize = 10.5, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        }
        else dot.SetResourceReference(Border.BackgroundProperty, "ControlBorderBrush");
        grid.Children.Add(dot);

        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(UI.Text(info?.Name ?? id, 12.5, weight: FontWeights.SemiBold));
        if (info?.Recommended == true) title.Children.Add(UI.Tag("Best", "#16A34A"));
        if (info?.Preview == true) title.Children.Add(UI.Tag("Preview", "#9333EA"));
        if (info is null)
        {
            var paid = ModelCatalog.IsKnownPaid(id, _kind);
            title.Children.Add(UI.Tag(paid ? "Not free" : "Custom", paid ? "#DC2626" : "#8A8A8A"));
        }
        names.Children.Add(title);
        names.Children.Add(UI.Text(id, 10.5, "TextTertiaryBrush", mono: true));
        Grid.SetColumn(names, 1);
        grid.Children.Add(names);

        if (index is > 0)
        {
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            actions.Children.Add(UI.IconButton("", "Move up", () => S.MoveModelUp(_kind, id), 11));
            actions.Children.Add(UI.IconButton("", "Make primary", () => S.SetPrimaryModel(_kind, id), 12));
            Grid.SetColumn(actions, 2);
            grid.Children.Add(actions);
        }

        var card = UI.Card(grid, new Thickness(9, 6, 4, 6));
        card.Margin = new Thickness(0, 0, 0, 5);
        card.Cursor = Cursors.Hand;
        card.ToolTip = info?.Note;
        if (selected)
        {
            card.Background = new SolidColorBrush(Color.FromArgb(ThemeManager.IsDark ? (byte)0x22 : (byte)0x14, tint.R, tint.G, tint.B));
        }
        else card.Opacity = 0.75;
        card.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && FindParent<Button>(d) is not null) return;
            S.ToggleModel(_kind, id);
        };
        return card;
    }

    private void AddCustomModel()
    {
        var id = _customModel.Text.Trim();
        if (id.Length == 0) return;
        if (!S.EffectiveModels(_kind).Contains(id)) S.ToggleModel(_kind, id);
        _customModel.Clear();
    }

    // MARK: Claude Code / Codex

    private void BuildCli()
    {
        var status = _app.CliStatus.Status(_kind);
        Body.Children.Add(CliStatusCard(_app, _kind, status, compact: true, () => _ = _app.CliStatus.RefreshAsync(_kind, S.Cli(_kind))));

        if (_app.Quota.CliLimitSummary(_kind) is { } limit)
        {
            var bar = new InfoBar { Margin = new Thickness(0, 0, 0, 12), IsClosable = false };
            var reached = limit.Contains("reached", StringComparison.Ordinal);
            bar.Set(reached ? Severity.Warning : Severity.Info, reached ? "Plan limit reached" : "Plan usage", limit);
            Body.Children.Add(bar);
        }

        Body.Children.Add(SectionTitle("MODEL", null));
        if (_kind == ProviderKind.ClaudeCode)
        {
            var current = S.Cli(_kind).Model;
            foreach (var m in ModelCatalog.Claude) Body.Children.Add(ClaudeModelRow(m, m.Id == current));
            Body.Children.Add(new Border { Height = 10 });
        }
        else
        {
            if (!_codexModel.IsKeyboardFocused) _codexModel.Text = S.Cli(_kind).Model;
            _codexModel.Margin = new Thickness(0, 0, 0, 10);
            Body.Children.Add(_codexModel);
            var fast = UI.Toggle(S.Cli(_kind).FastMode, on => S.UpdateCli(_kind, c => c.FastMode = on), "Fast mode (low reasoning effort)");
            fast.FontSize = 13;
            fast.Margin = new Thickness(0, 0, 0, 12);
            Body.Children.Add(fast);
        }
    }

    private void CommitCodexModel()
    {
        if (_kind != ProviderKind.Codex) return;
        var v = _codexModel.Text.Trim();
        if (v != S.Cli(ProviderKind.Codex).Model) S.UpdateCli(ProviderKind.Codex, c => c.Model = v);
    }

    private UIElement ClaudeModelRow(ModelInfo m, bool selected)
    {
        var tint = ThemeManager.Hex(_kind.TintHex());
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var radio = new RadioButton { IsChecked = selected, IsHitTestVisible = false, Margin = new Thickness(0, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(radio);
        var names = new StackPanel();
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(UI.Text(m.Name, 12.5, weight: FontWeights.SemiBold));
        if (m.Recommended) title.Children.Add(UI.Tag("Best", "#16A34A"));
        names.Children.Add(title);
        names.Children.Add(UI.Text(m.Note, 11.5, "TextSecondaryBrush", wrap: true));
        Grid.SetColumn(names, 1);
        grid.Children.Add(names);
        var card = UI.Card(grid, new Thickness(9, 6, 9, 6));
        card.Margin = new Thickness(0, 0, 0, 5);
        card.Cursor = Cursors.Hand;
        if (selected) card.Background = new SolidColorBrush(Color.FromArgb(ThemeManager.IsDark ? (byte)0x22 : (byte)0x14, tint.R, tint.G, tint.B));
        card.MouseLeftButtonUp += (_, _) => S.UpdateCli(_kind, c => c.Model = m.Id);
        return card;
    }

    /// <summary>Install / sign-in status of a CLI, with the exact command to run when something's missing.</summary>
    public static UIElement CliStatusCard(AppState app, ProviderKind kind, CLIStatus status, bool compact, Action refresh)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        FrameworkElement icon = status.State switch
        {
            CLIStatusState.Checking => UI.Spinner(16),
            CLIStatusState.Ready => UI.Glyph("", 18, ThemeManager.Hex("#22C55E")),
            CLIStatusState.LoggedOut => UI.Glyph("", 18, ThemeManager.Hex("#F59E0B")),
            _ => UI.Glyph("", 18, ThemeManager.Hex("#EF4444")),
        };
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 1, 10, 0);
        grid.Children.Add(icon);

        var text = new StackPanel();
        switch (status.State)
        {
            case CLIStatusState.Checking:
                text.Children.Add(UI.Text($"Checking {kind.DisplayName()}…", 13));
                break;
            case CLIStatusState.Ready:
                text.Children.Add(UI.Text(status.Detail, 13, weight: FontWeights.SemiBold, wrap: true));
                var meta = string.Join(" · ", new[] { status.Version, compact ? null : status.Path }.Where(x => !string.IsNullOrEmpty(x)));
                if (meta.Length > 0) text.Children.Add(UI.Text(meta, 11, "TextSecondaryBrush", mono: true));
                break;
            case CLIStatusState.LoggedOut:
                text.Children.Add(UI.Text("Installed, but not signed in", 13, weight: FontWeights.SemiBold));
                text.Children.Add(UI.Text("Run this in a terminal, finish signing in, then check again:", 11.5, "TextSecondaryBrush", wrap: true));
                text.Children.Add(CommandBox(kind.LoginCommand()));
                break;
            default:
                text.Children.Add(UI.Text($"{kind.DisplayName()} isn't installed", 13, weight: FontWeights.SemiBold));
                text.Children.Add(UI.Text(kind == ProviderKind.ClaudeCode
                    ? "Install it in PowerShell, sign in, then check again. Already installed? Set its path in Settings."
                    : "Install it (needs Node.js), sign in, then check again. Already installed? Set its path in Settings.",
                    11.5, "TextSecondaryBrush", wrap: true));
                text.Children.Add(CommandBox(kind.InstallCommand()));
                break;
        }
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var again = compact ? UI.IconButton("", "Check again", refresh, 13) : UI.Button("Check again", refresh);
        again.VerticalAlignment = VerticalAlignment.Top;
        again.IsEnabled = status.State != CLIStatusState.Checking;
        again.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(again, 2);
        grid.Children.Add(again);

        var card = UI.Card(grid, new Thickness(12, 10, 8, 10));
        card.Margin = new Thickness(0, 0, 0, 12);
        var tintKey = status.State switch
        {
            CLIStatusState.Ready => "InfoSuccessBrush",
            CLIStatusState.Checking => "CardBrush",
            _ => "InfoWarningBrush",
        };
        card.SetResourceReference(Border.BackgroundProperty, tintKey);
        return card;
    }

    public static UIElement CommandBox(string command)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var t = new TextBox
        {
            Text = command, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            FontFamily = UI.MonoFont, FontSize = 11.5, Padding = new Thickness(0), MinHeight = 0, VerticalAlignment = VerticalAlignment.Center,
            Template = SimpleTextBoxTemplate(),
        };
        t.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        grid.Children.Add(t);
        var copy = UI.IconButton("", "Copy", () => { try { Clipboard.SetText(command); } catch { } }, 12);
        copy.MinHeight = 24;
        copy.MinWidth = 24;
        copy.Padding = new Thickness(4);
        Grid.SetColumn(copy, 1);
        grid.Children.Add(copy);
        var b = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 3, 3, 3), Margin = new Thickness(0, 6, 0, 0), Child = grid };
        b.SetResourceReference(Border.BackgroundProperty, "ControlBrush");
        return b;
    }

    private static ControlTemplate SimpleTextBoxTemplate()
    {
        var tpl = new ControlTemplate(typeof(TextBox));
        var host = new FrameworkElementFactory(typeof(ScrollViewer)) { Name = "PART_ContentHost" };
        host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        host.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        tpl.VisualTree = host;
        return tpl;
    }

    // MARK: Test

    private UIElement TestRow()
    {
        var stack = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        if (_testing) { var s = UI.Spinner(13); s.Margin = new Thickness(0, 0, 8, 0); content.Children.Add(s); }
        else { var g = UI.Glyph("", 13); g.Margin = new Thickness(0, 0, 8, 0); content.Children.Add(g); }
        content.Children.Add(new TextBlock { Text = _testing ? "Testing…" : "Send a test request", VerticalAlignment = VerticalAlignment.Center });
        var btn = new Button { Style = UI.Style("FluentButton"), Content = content, HorizontalAlignment = HorizontalAlignment.Left, FontSize = 13, IsEnabled = !_testing };
        btn.Click += async (_, _) =>
        {
            _testing = true;
            _testResult = null;
            Rebuild();
            var k = _kind;
            var r = await ProviderActions.TestAsync(k, _app.Router);
            _testing = false;
            if (k == _kind) _testResult = r;
            Rebuild();
        };
        stack.Children.Add(btn);
        if (_testResult is { } res)
        {
            var bar = new InfoBar { Margin = new Thickness(0, 8, 0, 0), IsClosable = false };
            if (res.Ok) bar.Set(Severity.Success, "", res.Message);
            else
            {
                var (title, detail) = Messages.Split(res.Message);
                bar.Set(Messages.IsWarning(res.Message) ? Severity.Warning : Severity.Error, title, detail);
            }
            stack.Children.Add(bar);
            UI.FadeIn(bar);
        }
        return stack;
    }

    // MARK: Helpers

    private static UIElement SectionTitle(string title, UIElement? trailing)
    {
        var g = new Grid { Margin = new Thickness(0, 2, 0, 6), MinHeight = 26 };
        var t = UI.Text(title, 11, "TextSecondaryBrush", FontWeights.Bold);
        t.VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(t);
        if (trailing is FrameworkElement fe)
        {
            fe.HorizontalAlignment = HorizontalAlignment.Right;
            g.Children.Add(fe);
        }
        return g;
    }

    private static void Detach(params FrameworkElement[] elements)
    {
        foreach (var e in elements)
        {
            switch (e.Parent)
            {
                case Panel p: p.Children.Remove(e); break;
                case Decorator d: d.Child = null; break;
                case ContentControl c: c.Content = null; break;
            }
        }
    }

    private static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        for (var cur = d; cur is not null; cur = VisualTreeHelper.GetParent(cur))
            if (cur is T t) return t;
        return null;
    }

    private void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke();
    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettingsRequested?.Invoke();
}
