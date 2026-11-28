using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using Inkwell.Core;
using Inkwell.Platform;

namespace Inkwell.Views;

public enum SettingsPage { Providers, General, Templates, About }

/// <summary>
/// Settings, styled like the Windows 11 Settings app: Mica backdrop, navigation pane on the left,
/// page title and cards on the right, and ContentDialog-style dialogs.
/// </summary>
public sealed class SettingsWindow : Window
{
    private static SettingsWindow? _shared;

    private readonly AppState _app;
    private readonly ListBox _nav = new() { Width = 260 };
    private readonly ContentControl _page = new();
    private readonly TextBlock _pageTitle = new();
    private readonly Grid _dialogLayer = new() { Visibility = Visibility.Collapsed };
    private readonly Border _dialogCard = new();
    private readonly ScrollViewer _scroller = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

    public static void Open(AppState app, SettingsPage? page = null)
    {
        _shared ??= new SettingsWindow(app);
        if (page is { } p) _shared.Navigate(p);
        if (!_shared.IsVisible) _shared.Show();
        if (_shared.WindowState == WindowState.Minimized) _shared.WindowState = WindowState.Normal;
        _shared.Activate();
    }

    internal SettingsWindow(AppState app)
    {
        _app = app;
        Title = "Inkwell Settings";
        Width = 1000;
        Height = 720;
        MinWidth = 760;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        UseLayoutRounding = true;
        FontFamily = (FontFamily)Application.Current.Resources["UIFont"];
        Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/AppIcon.ico"));
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 44,
            GlassFrameThickness = new Thickness(-1),
            ResizeBorderThickness = new Thickness(6),
            UseAeroCaptionButtons = true,
            CornerRadius = new CornerRadius(0),
        });

        Content = BuildLayout();
        SourceInitialized += (_, _) => ApplyChrome();
        ThemeManager.ThemeChanged += () => { ApplyChrome(); Navigate(Current, animate: false); };
        StateChanged += (_, _) => ((Grid)Content).Margin = WindowState == WindowState.Maximized ? new Thickness(8) : new Thickness(0);
        Closing += (_, e) => { e.Cancel = true; Hide(); }; // keep state; reopen instantly
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && _dialogLayer.Visibility == Visibility.Visible) { CloseDialog(); e.Handled = true; } };
        Navigate(SettingsPage.Providers, animate: false);
    }

    private void ApplyChrome() => ThemeManager.ApplyBackdrop(this, Win32.DWMSBT_MAINWINDOW);

    public SettingsPage Current { get; private set; }

    private UIElement BuildLayout()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
        root.RowDefinitions.Add(new RowDefinition());

        // Title bar (drawn by us, caption buttons by Windows)
        var titleBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        titleBar.Children.Add(new Image { Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/AppIcon.png")), Width = 16, Height = 16, Margin = new Thickness(0, 0, 12, 0) });
        titleBar.Children.Add(UI.Text("Inkwell Settings", 12));
        root.Children.Add(titleBar);

        var body = new Grid();
        Grid.SetRow(body, 1);
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition());

        // Navigation pane
        var navPane = new StackPanel { Margin = new Thickness(8, 4, 8, 8) };
        var profile = new Grid { Margin = new Thickness(12, 8, 8, 16) };
        profile.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        profile.ColumnDefinitions.Add(new ColumnDefinition());
        profile.Children.Add(new Image { Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/AppIcon.png")), Width = 56, Height = 56, Margin = new Thickness(0, 0, 14, 0) });
        var who = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        who.Children.Add(UI.Text("Inkwell", 16, weight: FontWeights.SemiBold));
        who.Children.Add(UI.Text($"Version {UpdateChecker.CurrentVersionText}", 12, "TextSecondaryBrush"));
        Grid.SetColumn(who, 1);
        profile.Children.Add(who);
        navPane.Children.Add(profile);

        _nav.Style = UI.Style("PlainList");
        foreach (var (page, glyph, label) in new[]
        {
            (SettingsPage.Providers, "", "AI Provider"),
            (SettingsPage.General, "", "General"),
            (SettingsPage.Templates, "", "Templates"),
            (SettingsPage.About, "", "About"),
        })
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var g = UI.Glyph(glyph, 16);
            g.Margin = new Thickness(4, 0, 14, 0);
            row.Children.Add(g);
            row.Children.Add(UI.Text(label, 14));
            _nav.Items.Add(new ListBoxItem { Content = row, Tag = page });
        }
        _nav.SelectionChanged += (_, _) =>
        {
            if (_nav.SelectedItem is ListBoxItem { Tag: SettingsPage p } && p != Current) Navigate(p);
        };
        navPane.Children.Add(_nav);
        body.Children.Add(navPane);

        // Page area
        var pageArea = new Grid { Margin = new Thickness(0, 0, 0, 0) };
        Grid.SetColumn(pageArea, 1);
        pageArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        pageArea.RowDefinitions.Add(new RowDefinition());
        _pageTitle.Style = UI.Style("TitleText");
        _pageTitle.Margin = new Thickness(40, 8, 40, 16);
        pageArea.Children.Add(_pageTitle);
        _scroller.Content = new Border { Padding = new Thickness(40, 0, 40, 32), MaxWidth = 1000 + 80, HorizontalAlignment = HorizontalAlignment.Stretch, Child = _page };
        Grid.SetRow(_scroller, 1);
        pageArea.Children.Add(_scroller);
        body.Children.Add(pageArea);
        root.Children.Add(body);

        // Dialog layer (smoke + centred card)
        var smoke = new Border();
        smoke.SetResourceReference(Border.BackgroundProperty, "SmokeBrush");
        _dialogLayer.Children.Add(smoke);
        _dialogCard.CornerRadius = new CornerRadius(8);
        _dialogCard.BorderThickness = new Thickness(1);
        _dialogCard.SetResourceReference(Border.BackgroundProperty, "PopupBrush");
        _dialogCard.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
        _dialogCard.HorizontalAlignment = HorizontalAlignment.Center;
        _dialogCard.VerticalAlignment = VerticalAlignment.Center;
        _dialogCard.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 32, ShadowDepth = 8, Direction = 270, Opacity = 0.3 };
        _dialogCard.RenderTransformOrigin = new Point(0.5, 0.5);
        _dialogLayer.Children.Add(_dialogCard);
        Grid.SetRowSpan(_dialogLayer, 2);
        root.Children.Add(_dialogLayer);
        WindowChrome.SetIsHitTestVisibleInChrome(_dialogLayer, true);
        return root;
    }

    public void Navigate(SettingsPage page, bool animate = true)
    {
        Current = page;
        foreach (ListBoxItem item in _nav.Items) if ((SettingsPage)item.Tag == page) _nav.SelectedItem = item;
        _pageTitle.Text = page switch
        {
            SettingsPage.Providers => "AI Provider",
            SettingsPage.General => "General",
            SettingsPage.Templates => "Templates",
            _ => "About",
        };
        if (_page.Content is IDisposable old) old.Dispose();
        _page.Content = page switch
        {
            SettingsPage.Providers => new ProvidersPage(_app, this),
            SettingsPage.General => new GeneralPage(_app),
            SettingsPage.Templates => new TemplatesPage(_app, this),
            _ => new AboutPage(_app, this),
        };
        _scroller.ScrollToTop();
        if (!animate) return;
        var shift = new TranslateTransform(0, 24);
        _page.RenderTransform = shift;
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(300)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        _page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
    }

    // MARK: Dialogs

    /// <summary>Shows a ContentDialog-style modal inside the window.</summary>
    public void ShowDialog(UIElement content, double width = 480)
    {
        _dialogCard.Width = width;
        _dialogCard.Child = content;
        _dialogLayer.Visibility = Visibility.Visible;
        var scale = new ScaleTransform(1.05, 1.05);
        _dialogCard.RenderTransform = scale;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
        _dialogLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
    }

    public void CloseDialog()
    {
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120));
        fade.Completed += (_, _) =>
        {
            _dialogLayer.Visibility = Visibility.Collapsed;
            _dialogCard.Child = null;
            _dialogLayer.BeginAnimation(OpacityProperty, null);
        };
        _dialogLayer.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Title, body and a button row (primary on the right), like WinUI's ContentDialog.</summary>
    public static UIElement DialogFrame(string title, UIElement body, params Button[] buttons)
    {
        var outer = new Grid();
        outer.RowDefinitions.Add(new RowDefinition());
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var top = new StackPanel { Margin = new Thickness(24, 22, 24, 22) };
        var t = UI.Text(title, 20, weight: FontWeights.SemiBold, wrap: true);
        t.FontFamily = (FontFamily)Application.Current.Resources["DisplayFont"];
        t.Margin = new Thickness(0, 0, 0, 12);
        top.Children.Add(t);
        top.Children.Add(body);
        outer.Children.Add(top);

        var footer = new Border { Padding = new Thickness(24), CornerRadius = new CornerRadius(0, 0, 8, 8), BorderThickness = new Thickness(0, 1, 0, 0) };
        footer.SetResourceReference(Border.BackgroundProperty, "WindowBrush");
        footer.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
        var row = new UniformGrid { Rows = 1 };
        foreach (var b in buttons) { b.Margin = new Thickness(4, 0, 4, 0); row.Children.Add(b); }
        row.Margin = new Thickness(-4, 0, -4, 0);
        footer.Child = row;
        Grid.SetRow(footer, 1);
        outer.Children.Add(footer);
        return outer;
    }

    /// <summary>"Remove this key?" style confirmation.</summary>
    public void Confirm(string title, string message, string confirmText, Action onConfirm, bool destructive = true)
    {
        var ok = new Button { Style = UI.Style(destructive ? "FluentDangerButton" : "FluentPrimaryButton"), Content = confirmText };
        var cancel = new Button { Style = UI.Style("FluentButton"), Content = "Cancel", IsCancel = true };
        ok.Click += (_, _) => { CloseDialog(); onConfirm(); };
        cancel.Click += (_, _) => CloseDialog();
        ShowDialog(DialogFrame(title, UI.Text(message, 14, "TextSecondaryBrush", wrap: true), ok, cancel), 440);
    }
}
