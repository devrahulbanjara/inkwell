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

/// <summary>Add key → validate with the provider → choose models (the Mac AddKeySheet).</summary>
public static class AddKeyDialog
{
    public static void Show(SettingsWindow window, AppState app, ProviderKind kind)
    {
        var key = new PasswordBox { FontFamily = UI.MonoFont };
        Input.SetPlaceholder(key, kind.KeyPlaceholder());
        var label = new TextBox();
        Input.SetPlaceholder(label, "e.g. Personal, Project 2 (optional)");
        var error = new InfoBar { IsClosable = false, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 10, 0, 0) };

        var body = new StackPanel();
        body.Children.Add(UI.Text($"It's checked with {kind.DisplayName()} before saving, and stored encrypted for your Windows account.", 13.5, "TextSecondaryBrush", wrap: true));
        body.Children.Add(Labeled("API key", key));
        body.Children.Add(Labeled("Label", label));
        if (kind.KeyUrl() is { } url)
        {
            var link = UI.Link($"Get a free key from {(kind == ProviderKind.Gemini ? "Google AI Studio" : "GroqCloud")} →", () => Shell.Open(url));
            link.Margin = new Thickness(0, 12, 0, 0);
            body.Children.Add(link);
        }
        body.Children.Add(error);

        var cont = new Button { Style = UI.Style("FluentPrimaryButton"), Content = "Continue", IsDefault = true, IsEnabled = false };
        var cancel = new Button { Style = UI.Style("FluentButton"), Content = "Cancel", IsCancel = true };
        key.PasswordChanged += (_, _) => cont.IsEnabled = key.Password.Trim().Length > 0;
        cancel.Click += (_, _) => window.CloseDialog();
        cont.Click += async (_, _) =>
        {
            cont.IsEnabled = false;
            var spin = new StackPanel { Orientation = Orientation.Horizontal };
            var s = UI.Spinner(14);
            s.Margin = new Thickness(0, 0, 8, 0);
            spin.Children.Add(s);
            spin.Children.Add(new TextBlock { Text = "Checking…" });
            cont.Content = spin;
            error.Visibility = Visibility.Collapsed;
            try
            {
                var remote = await ProviderActions.AddKeyAsync(kind, key.Password, label.Text, app.Settings, app.Quota);
                var selection = app.Settings.Api(kind).Models.ToList();
                if (selection.Count == 0) selection = ModelCatalog.DefaultModelIDs(kind);
                ShowChooseModels(window, app, kind, remote, selection);
            }
            catch (Exception ex)
            {
                error.Set(Severity.Error, "Couldn't add this key", ex.Message);
                error.Visibility = Visibility.Visible;
                cont.Content = "Continue";
                cont.IsEnabled = true;
            }
        };

        window.ShowDialog(SettingsWindow.DialogFrame($"Add a {kind.DisplayName()} key", body, cont, cancel), 520);
        key.Dispatcher.BeginInvoke(() => key.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    private static void ShowChooseModels(SettingsWindow window, AppState app, ProviderKind kind, List<RemoteModel> remote, List<string> selection)
    {
        var picker = new ModelPickerList(kind, remote, selection);
        var body = new StackPanel();
        var saved = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        saved.Children.Add(UI.Glyph("", 13, "InfoSuccessIconBrush"));
        var st = UI.Text("Key saved. Pick a primary model and optional fallbacks; free models are listed first.", 13.5, "TextSecondaryBrush", wrap: true);
        st.Margin = new Thickness(8, 0, 0, 0);
        saved.Children.Add(st);
        body.Children.Add(saved);
        body.Children.Add(picker);

        var save = new Button { Style = UI.Style("FluentPrimaryButton"), Content = "Save models", IsDefault = true };
        var keep = new Button { Style = UI.Style("FluentButton"), Content = "Keep current models", IsCancel = true };
        picker.SelectionChanged += () => save.IsEnabled = picker.Selection.Count > 0;
        save.Click += (_, _) =>
        {
            if (picker.Selection.Count > 0) app.Settings.UpdateApi(kind, a => a.Models = picker.Selection.ToList());
            window.CloseDialog();
        };
        keep.Click += (_, _) => window.CloseDialog();
        window.ShowDialog(SettingsWindow.DialogFrame("Which models should this use?", body, save, keep), 600);
    }

    private static UIElement Labeled(string title, Control input)
    {
        var s = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        var t = UI.Text(title, 13);
        t.Margin = new Thickness(0, 0, 0, 6);
        s.Children.Add(t);
        s.Children.Add(input);
        return s;
    }
}

/// <summary>Change the model order for a provider (the Mac ModelsSheet).</summary>
public static class ModelsDialog
{
    public static void Show(SettingsWindow window, AppState app, ProviderKind kind)
    {
        var picker = new ModelPickerList(kind, new List<RemoteModel>(), app.Settings.EffectiveModels(kind)) { Loading = true };
        var body = new StackPanel();
        body.Children.Add(UI.Text("Click models in the order you want them tried. #1 is primary; the rest are fallbacks.", 13.5, "TextSecondaryBrush", wrap: true));
        picker.Margin = new Thickness(0, 12, 0, 0);
        body.Children.Add(picker);

        var save = new Button { Style = UI.Style("FluentPrimaryButton"), Content = "Save", IsDefault = true };
        var reset = new Button { Style = UI.Style("FluentButton"), Content = "Reset to recommended" };
        var cancel = new Button { Style = UI.Style("FluentButton"), Content = "Cancel", IsCancel = true };
        picker.SelectionChanged += () => save.IsEnabled = picker.Selection.Count > 0;
        save.Click += (_, _) => { app.Settings.UpdateApi(kind, a => a.Models = picker.Selection.ToList()); window.CloseDialog(); };
        reset.Click += (_, _) => picker.SetSelection(ModelCatalog.DefaultModelIDs(kind));
        cancel.Click += (_, _) => window.CloseDialog();
        window.ShowDialog(SettingsWindow.DialogFrame($"{kind.DisplayName()} models", body, save, reset, cancel), 620);

        if (app.Settings.FirstSecret(kind) is { } key)
        {
            _ = LoadAsync();
            async System.Threading.Tasks.Task LoadAsync()
            {
                try { picker.SetRemote(await HTTPProviders.ListModelsAsync(kind, key)); }
                catch { picker.SetRemote(new List<RemoteModel>()); }
            }
        }
        else picker.Loading = false;
    }
}

/// <summary>Numbered, reorderable model list: curated free models, models the key can see, and custom IDs.</summary>
public sealed class ModelPickerList : StackPanel
{
    private readonly ProviderKind _kind;
    private List<RemoteModel> _remote;
    private readonly List<string> _selection;
    private readonly StackPanel _list = new();
    private readonly TextBox _custom = new() { FontFamily = UI.MonoFont, FontSize = 12.5 };
    private bool _loading;

    public event Action? SelectionChanged;
    public IReadOnlyList<string> Selection => _selection;

    public bool Loading { get => _loading; set { _loading = value; Rebuild(); } }

    public ModelPickerList(ProviderKind kind, List<RemoteModel> remote, IEnumerable<string> selection)
    {
        _kind = kind;
        _remote = remote;
        _selection = selection.ToList();
        Input.SetPlaceholder(_custom, "Add a model ID manually");
        _custom.KeyDown += (_, e) => { if (e.Key == Key.Enter) AddCustom(); };
        var scroller = new ScrollViewer { MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _list, Padding = new Thickness(0, 0, 8, 0) };
        Children.Add(scroller);
        var add = new Grid { Margin = new Thickness(0, 10, 8, 0) };
        add.ColumnDefinitions.Add(new ColumnDefinition());
        add.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        add.Children.Add(_custom);
        var addBtn = UI.Button("Add", AddCustom);
        addBtn.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(addBtn, 1);
        add.Children.Add(addBtn);
        Children.Add(add);
        Rebuild();
    }

    public void SetRemote(List<RemoteModel> remote) { _remote = remote; _loading = false; Rebuild(); }

    public void SetSelection(IEnumerable<string> ids)
    {
        _selection.Clear();
        _selection.AddRange(ids);
        Rebuild();
        SelectionChanged?.Invoke();
    }

    private void Rebuild()
    {
        _list.Children.Clear();
        var catalog = ModelCatalog.Models(_kind);
        _list.Children.Add(Header("FREE MODELS"));
        foreach (var info in catalog)
        {
            var available = _remote.Count == 0 || _remote.Any(r => r.Id == info.Id) || info.Id.EndsWith("-latest", StringComparison.Ordinal);
            _list.Children.Add(Row(info.Id, info.Name, info.Note, info.Recommended ? ("Recommended", "#16A34A") : info.Preview ? ("Preview", "#9333EA") : null, available));
        }
        if (_loading)
        {
            var l = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            l.Children.Add(UI.Spinner(14));
            var t = UI.Text("Loading models your key can access…", 12.5, "TextSecondaryBrush");
            t.Margin = new Thickness(8, 0, 0, 0);
            l.Children.Add(t);
            _list.Children.Add(l);
        }
        var known = catalog.Select(c => c.Id).ToHashSet();
        var extras = _remote.Where(r => !known.Contains(r.Id)).OrderBy(r => r.Id, StringComparer.Ordinal).ToList();
        if (extras.Count > 0)
        {
            _list.Children.Add(Header("ALSO AVAILABLE ON YOUR KEY"));
            var note = UI.Text("Not verified as free. Paid-only models usually fail with a quota error on free keys.", 12, "TextTertiaryBrush", wrap: true);
            note.Margin = new Thickness(0, -4, 0, 6);
            _list.Children.Add(note);
            foreach (var r in extras)
                _list.Children.Add(Row(r.Id, r.DisplayName, null, ModelCatalog.IsKnownPaid(r.Id, _kind) ? ("Not free", "#DC2626") : ("Unverified", "#8A8A8A"), true));
        }
        var allKnown = known.Union(_remote.Select(r => r.Id)).ToHashSet();
        foreach (var id in _selection.Where(id => !allKnown.Contains(id)).ToList())
            _list.Children.Add(Row(id, id, "Custom model ID", ("Unverified", "#8A8A8A"), true));
    }

    private static UIElement Header(string text)
    {
        var t = UI.Text(text, 11, "TextSecondaryBrush", FontWeights.Bold);
        t.Margin = new Thickness(0, 10, 0, 6);
        return t;
    }

    private UIElement Row(string id, string title, string? note, (string Text, string Hex)? badge, bool available)
    {
        var index = _selection.IndexOf(id);
        var tint = ThemeManager.Hex(_kind.TintHex());
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var dot = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        if (index >= 0)
        {
            dot.Background = new SolidColorBrush(tint);
            dot.Child = new TextBlock { Text = (index + 1).ToString(), Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        }
        else dot.SetResourceReference(Border.BackgroundProperty, "ControlBorderBrush");
        grid.Children.Add(dot);

        var names = new StackPanel();
        var t = new StackPanel { Orientation = Orientation.Horizontal };
        t.Children.Add(UI.Text(title, 13.5, weight: FontWeights.SemiBold));
        if (badge is { } b) t.Children.Add(UI.Tag(b.Text, b.Hex));
        if (!available) t.Children.Add(UI.Tag("Not on this key", "#EA580C"));
        names.Children.Add(t);
        names.Children.Add(UI.Text(id, 11.5, "TextTertiaryBrush", mono: true));
        if (note is not null) names.Children.Add(UI.Text(note, 12, "TextSecondaryBrush", wrap: true));
        Grid.SetColumn(names, 1);
        grid.Children.Add(names);

        if (index >= 0)
        {
            var moves = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var up = UI.IconButton("", "Move up", () => Move(id, -1), 11);
            up.IsEnabled = index > 0;
            var down = UI.IconButton("", "Move down", () => Move(id, 1), 11);
            down.IsEnabled = index < _selection.Count - 1;
            moves.Children.Add(up);
            moves.Children.Add(down);
            Grid.SetColumn(moves, 2);
            grid.Children.Add(moves);
        }

        var card = UI.Card(grid, new Thickness(12, 8, 8, 8));
        card.Margin = new Thickness(0, 0, 0, 4);
        card.Cursor = Cursors.Hand;
        if (index >= 0) card.Background = new SolidColorBrush(Color.FromArgb(ThemeManager.IsDark ? (byte)0x22 : (byte)0x16, tint.R, tint.G, tint.B));
        card.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && IsInButton(d)) return;
            if (_selection.Contains(id)) _selection.Remove(id); else _selection.Add(id);
            Rebuild();
            SelectionChanged?.Invoke();
        };
        return card;
    }

    private static bool IsInButton(DependencyObject d)
    {
        for (var c = d; c is not null; c = VisualTreeHelper.GetParent(c)) if (c is Button) return true;
        return false;
    }

    private void Move(string id, int delta)
    {
        var i = _selection.IndexOf(id);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= _selection.Count) return;
        (_selection[i], _selection[j]) = (_selection[j], _selection[i]);
        Rebuild();
        SelectionChanged?.Invoke();
    }

    private void AddCustom()
    {
        var id = _custom.Text.Trim();
        if (id.Length == 0) return;
        if (!_selection.Contains(id)) _selection.Add(id);
        _custom.Clear();
        Rebuild();
        SelectionChanged?.Invoke();
    }
}
