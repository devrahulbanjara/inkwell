using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Inkwell.Core;
using Inkwell.Platform;

namespace Inkwell.Views;

// MARK: - General

public sealed class GeneralPage : StackPanel, IDisposable
{
    private readonly AppState _app;
    private readonly StackPanel _updateHost = new();

    public GeneralPage(AppState app)
    {
        _app = app;
        var s = app.Settings;

        Children.Add(Section("Behaviour"));
        Children.Add(UI.SettingsRow("", "Enable triggers", "Watch for ?fix, ?ask and your other triggers in every app",
            UI.Toggle(s.Prefs.IsEnabled, on => s.SetEnabled(on))));
        Children.Add(UI.SettingsRow("", "Play sounds", "A soft pop when the text is replaced, a beep if something goes wrong",
            UI.Toggle(s.Prefs.PlaySounds, on => s.Update(p => p.PlaySounds = on))));
        Children.Add(UI.SettingsRow("", "Show floating indicator while working", "An animated pill just above the field you're typing in",
            UI.Toggle(s.Prefs.ShowHud, on => s.Update(p => p.ShowHud = on))));
        Children.Add(UI.SettingsRow("", "Launch at login", "Start Inkwell in the notification area when you sign in to Windows",
            UI.Toggle(LaunchAtLogin.IsEnabled, on =>
            {
                try { LaunchAtLogin.IsEnabled = on; }
                catch (Exception ex) { app.LastError = $"Launch at login: {ex.Message}"; }
            })));

        Children.Add(Section("Updates"));
        Children.Add(_updateHost);
        BuildUpdate();
        app.Updates.Changed += OnUpdate;

        Children.Add(Section("Good to know"));
        Children.Add(UI.SettingsRow("", "Apps running as administrator",
            "Windows doesn't let normal apps type into elevated windows. To use triggers there, run Inkwell as administrator too.", null));
        Children.Add(UI.SettingsRow("", "Keyboard layout",
            "Inkwell sends Ctrl+A, Ctrl+C and Ctrl+V to read and replace text, so apps that remap those shortcuts may not work.", null));
    }

    private void OnUpdate() => Dispatcher.BeginInvoke(BuildUpdate);

    private void BuildUpdate()
    {
        _updateHost.Children.Clear();
        var u = _app.Updates;
        UIElement control;
        string header, desc;
        if (u.AvailableVersion is { } v)
        {
            header = $"Inkwell {v} is available";
            desc = u.Status ?? $"You have {UpdateChecker.CurrentVersionText}. The update installs in a few seconds and keeps your settings.";
            var b = UI.Button(u.IsInstalling ? "Updating…" : "Update now", () => _ = u.InstallAsync(), primary: true);
            b.IsEnabled = !u.IsInstalling;
            control = b;
        }
        else
        {
            header = "Inkwell is up to date";
            desc = $"Version {UpdateChecker.CurrentVersionText}. Inkwell checks for updates automatically.";
            Button? btn = null;
            btn = UI.Button("Check for updates", async () =>
            {
                btn!.IsEnabled = false;
                btn.Content = "Checking…";
                var found = await u.CheckAsync();
                if (!found) { btn.Content = "You're up to date"; }
                BuildUpdate();
            });
            control = btn;
        }
        _updateHost.Children.Add(UI.SettingsRow("", header, desc, control));
    }

    public void Dispose() => _app.Updates.Changed -= OnUpdate;

    public static TextBlock Section(string text)
    {
        var t = UI.Text(text, 14, weight: FontWeights.SemiBold);
        t.Margin = new Thickness(2, 18, 0, 8);
        return t;
    }
}

// MARK: - Templates

public sealed class TemplatesPage : Grid, IDisposable
{
    private readonly AppState _app;
    private readonly SettingsWindow _window;
    private readonly ListBox _list = new();
    private readonly ContentControl _editor = new();
    private Guid? _selected;
    private bool _syncing;

    private SettingsStore S => _app.Settings;

    public TemplatesPage(AppState app, SettingsWindow window)
    {
        _app = app;
        _window = window;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        ColumnDefinitions.Add(new ColumnDefinition());
        MinHeight = 520;

        var intro = UI.Text("Type a trigger at the end of any text, in any app. Inkwell sends the text with your instruction to the AI and puts the result back.",
            14, "TextSecondaryBrush", wrap: true);
        intro.Margin = new Thickness(0, 0, 0, 16);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());
        SetColumnSpan(intro, 3);
        Children.Add(intro);

        // Left: list + toolbar
        var left = new Grid();
        left.RowDefinitions.Add(new RowDefinition());
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _list.Style = UI.Style("PlainList");
        _list.Padding = new Thickness(4);
        _list.SelectionChanged += (_, _) =>
        {
            if (_syncing) return;
            _selected = (_list.SelectedItem as ListBoxItem)?.Tag as Guid?;
            ShowEditor();
        };
        left.Children.Add(_list);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 6, 4, 4) };
        bar.Children.Add(UI.IconButton("", "Add template", Add, 13));
        bar.Children.Add(UI.IconButton("", "Remove template", Remove, 13));
        bar.Children.Add(UI.IconButton("", "Move up", () => Move(-1), 12));
        bar.Children.Add(UI.IconButton("", "Move down", () => Move(1), 12));
        var reset = UI.Link("Reset", () => _window.Confirm("Reset templates?", "Your templates will be replaced with Inkwell's defaults.", "Reset", () =>
        {
            S.Update(p => p.Templates = Template.CreateDefaults());
            _selected = null;
            Refresh();
        }), 13);
        reset.VerticalAlignment = VerticalAlignment.Center;
        reset.Margin = new Thickness(14, 0, 0, 0);
        bar.Children.Add(reset);
        SetRow(bar, 1);
        left.Children.Add(bar);
        var leftCard = UI.Card(left, new Thickness(4));
        SetRow(leftCard, 1);
        Children.Add(leftCard);

        SetRow(_editor, 1);
        SetColumn(_editor, 2);
        Children.Add(_editor);

        Refresh();
    }

    public void Dispose() { }

    private void Refresh()
    {
        _syncing = true;
        _list.Items.Clear();
        foreach (var t in S.Prefs.Templates) _list.Items.Add(new ListBoxItem { Content = ItemContent(t), Tag = t.Id });
        _list.SelectedItem = _list.Items.Cast<ListBoxItem>().FirstOrDefault(i => (Guid)i.Tag == _selected);
        _syncing = false;
        ShowEditor();
    }

    private static UIElement ItemContent(Template t)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var s = new StackPanel();
        s.Children.Add(UI.Text(string.IsNullOrWhiteSpace(t.Name) ? "Untitled" : t.Name, 14));
        s.Children.Add(UI.Text(t.Trigger, 12, "TextSecondaryBrush", mono: true));
        g.Children.Add(s);
        if (!t.IsEnabled)
        {
            var p = UI.Glyph("", 13, "TextTertiaryBrush");
            p.ToolTip = "Turned off";
            Grid.SetColumn(p, 1);
            g.Children.Add(p);
        }
        return g;
    }

    private Template? Current => S.Prefs.Templates.FirstOrDefault(t => t.Id == _selected);

    private void ShowEditor()
    {
        var t = Current;
        if (t is null)
        {
            var empty = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var g = UI.Glyph("", 40, "TextTertiaryBrush");
            g.HorizontalAlignment = HorizontalAlignment.Center;
            empty.Children.Add(g);
            var a = UI.Text("Select a template", 15, "TextSecondaryBrush");
            a.HorizontalAlignment = HorizontalAlignment.Center;
            a.Margin = new Thickness(0, 12, 0, 4);
            empty.Children.Add(a);
            var b = UI.Text("Type a trigger at the end of any text, in any app.", 13, "TextTertiaryBrush");
            b.HorizontalAlignment = HorizontalAlignment.Center;
            empty.Children.Add(b);
            _editor.Content = UI.Card(empty);
            ((Border)_editor.Content).VerticalAlignment = VerticalAlignment.Stretch;
            return;
        }

        var id = t.Id;
        var form = new StackPanel();
        var name = new TextBox { Text = t.Name };
        var trigger = new TextBox { Text = t.Trigger, FontFamily = UI.MonoFont };
        var dup = new InfoBar { IsClosable = false, Margin = new Thickness(0, 8, 0, 0) };
        dup.Set(Severity.Warning, "", "Another template uses this trigger.");
        var prompt = new TextBox
        {
            Text = t.Prompt, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 150, VerticalContentAlignment = VerticalAlignment.Top,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var mode = new ComboBox { Width = 220, HorizontalAlignment = HorizontalAlignment.Left };
        mode.Items.Add(new ComboBoxItem { Content = OutputMode.Replace.Label(), Tag = OutputMode.Replace });
        mode.Items.Add(new ComboBoxItem { Content = OutputMode.Append.Label(), Tag = OutputMode.Append });
        mode.SelectedIndex = t.Mode == OutputMode.Replace ? 0 : 1;

        void UpdateDup() => dup.Visibility = IsDuplicate(id, trigger.Text) ? Visibility.Visible : Visibility.Collapsed;
        UpdateDup();

        void Change(Action<Template> apply, bool refreshItem = true)
        {
            var cur = S.Prefs.Templates.FirstOrDefault(x => x.Id == id);
            if (cur is null) return;
            apply(cur);
            S.Save();
            if (refreshItem && _list.Items.Cast<ListBoxItem>().FirstOrDefault(i => (Guid)i.Tag == id) is { } item) item.Content = ItemContent(cur);
        }

        name.TextChanged += (_, _) => Change(x => x.Name = name.Text);
        trigger.TextChanged += (_, _) => { Change(x => x.Trigger = trigger.Text); UpdateDup(); };
        prompt.TextChanged += (_, _) => Change(x => x.Prompt = prompt.Text, refreshItem: false);
        mode.SelectionChanged += (_, _) => { if (mode.SelectedItem is ComboBoxItem { Tag: OutputMode m }) Change(x => x.Mode = m, false); };

        form.Children.Add(Field("Name", name));
        var trig = Field("Trigger", trigger);
        ((StackPanel)trig).Children.Add(dup);
        form.Children.Add(trig);
        var modeHint = UI.Text("Replace swaps the whole field for the result. Insert below keeps your text and adds the result under it.", 12, "TextSecondaryBrush", wrap: true);
        modeHint.Margin = new Thickness(0, 6, 0, 0);
        var modeField = Field("Result", mode);
        ((StackPanel)modeField).Children.Add(modeHint);
        form.Children.Add(modeField);
        var enabled = UI.Toggle(t.IsEnabled, on => Change(x => x.IsEnabled = on), "Enabled");
        enabled.Margin = new Thickness(0, 16, 0, 0);
        form.Children.Add(enabled);
        form.Children.Add(Field("Instruction for the AI", prompt));
        _editor.Content = UI.Card(form, new Thickness(20, 16, 20, 20));
    }

    private bool IsDuplicate(Guid id, string trigger) =>
        trigger.Length > 0 && S.Prefs.Templates.Any(t => t.Id != id && t.Trigger == trigger);

    private static UIElement Field(string label, Control input)
    {
        var s = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        var l = UI.Text(label, 13);
        l.Margin = new Thickness(0, 0, 0, 6);
        s.Children.Add(l);
        s.Children.Add(input);
        return s;
    }

    private void Add()
    {
        var t = new Template { Name = "New template", Trigger = "?new", Prompt = "Describe what the AI should do with the text." };
        S.Update(p => p.Templates.Add(t));
        _selected = t.Id;
        Refresh();
    }

    private void Remove()
    {
        if (Current is not { } t) return;
        _window.Confirm($"Delete “{(string.IsNullOrWhiteSpace(t.Name) ? "Untitled" : t.Name)}”?", $"The {t.Trigger} trigger will stop working.", "Delete", () =>
        {
            S.Update(p => p.Templates.RemoveAll(x => x.Id == t.Id));
            _selected = null;
            Refresh();
        });
    }

    private void Move(int delta)
    {
        var list = S.Prefs.Templates;
        var i = list.FindIndex(x => x.Id == _selected);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= list.Count) return;
        (list[i], list[j]) = (list[j], list[i]);
        S.Save();
        Refresh();
    }
}

// MARK: - About

public sealed class AboutPage : StackPanel, IDisposable
{
    public AboutPage(AppState app, SettingsWindow window)
    {
        var hero = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 20) };
        hero.Children.Add(new Image { Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/AppIcon.png")), Width = 96, Height = 96 });
        var name = new TextBlock { Text = "Inkwell", Style = UI.Style("SubtitleText"), FontSize = 24, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
        hero.Children.Add(name);
        var ver = UI.Text($"Version {UpdateChecker.CurrentVersionText}", 13, "TextSecondaryBrush");
        ver.HorizontalAlignment = HorizontalAlignment.Center;
        hero.Children.Add(ver);
        var desc = UI.Text("AI text triggers in every app. Your API keys stay encrypted on this PC; text goes only to the provider you choose.", 14, "TextSecondaryBrush", wrap: true);
        desc.TextAlignment = TextAlignment.Center;
        desc.MaxWidth = 440;
        desc.Margin = new Thickness(0, 12, 0, 10);
        hero.Children.Add(desc);
        var site = UI.Link("inkwell.rahuldevbanjara.com.np", () => Shell.Open("https://inkwell.rahuldevbanjara.com.np"), 14);
        site.HorizontalAlignment = HorizontalAlignment.Center;
        site.ToolTip = "Open the Inkwell website";
        hero.Children.Add(site);
        Children.Add(hero);

        var author = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        var made = UI.Text("Made by Rahul Dev Banjara", 14, weight: FontWeights.SemiBold);
        made.HorizontalAlignment = HorizontalAlignment.Center;
        made.Margin = new Thickness(0, 0, 0, 10);
        author.Children.Add(made);
        var links = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var (title, glyph, url) in new[]
        {
            ("LinkedIn", "", "https://www.linkedin.com/in/devrahulbanjara/"),
            ("Website", "", "https://rahuldevbanjara.com.np"),
            ("GitHub", "", "https://github.com/devrahulbanjara"),
        })
        {
            var b = UI.Button(title, () => Shell.Open(url), glyph: glyph);
            b.Margin = new Thickness(5, 0, 5, 0);
            b.ToolTip = url;
            links.Children.Add(b);
        }
        author.Children.Add(links);
        var card = UI.Card(author, new Thickness(24, 18, 24, 18));
        card.HorizontalAlignment = HorizontalAlignment.Center;
        Children.Add(card);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 20, 0, 0) };
        var folder = UI.Button("Open data folder", () => Shell.Open(app.Usage.Folder), glyph: "");
        folder.Margin = new Thickness(5, 0, 5, 0);
        actions.Children.Add(folder);
        var reset = UI.Button("Reset statistics…", () => window.Confirm("Delete all usage statistics?",
            "Runs, tokens and activity history will be cleared. Your settings and keys are kept.", "Delete", () => app.Usage.Reset()), glyph: "");
        reset.Margin = new Thickness(5, 0, 5, 0);
        actions.Children.Add(reset);
        Children.Add(actions);

        var legal = UI.Text("Gemini, Groq, Claude and OpenAI names and logos are trademarks of their respective owners and are used only to identify each provider. MIT License.",
            11.5, "TextTertiaryBrush", wrap: true);
        legal.TextAlignment = TextAlignment.Center;
        legal.MaxWidth = 520;
        legal.Margin = new Thickness(0, 28, 0, 0);
        Children.Add(legal);
    }

    public void Dispose() { }
}
