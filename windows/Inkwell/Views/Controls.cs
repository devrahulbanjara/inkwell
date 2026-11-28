using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Inkwell.Views;

public enum Severity { Info, Success, Warning, Error }

/// <summary>Windows 11's InfoBar: tinted card, round icon badge, bold title, message, optional action and close.</summary>
public sealed class InfoBar : Border
{
    private readonly TextBlock _icon = new();
    private readonly Border _badge = new();
    private readonly TextBlock _title = new();
    private readonly TextBlock _message = new();
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _close = new();

    public event Action? Closed;

    public InfoBar()
    {
        CornerRadius = new CornerRadius(8);
        BorderThickness = new Thickness(1);
        SetResourceReference(BorderBrushProperty, "CardBorderBrush");
        Padding = new Thickness(12, 10, 6, 10);

        _badge.Width = 18;
        _badge.Height = 18;
        _badge.CornerRadius = new CornerRadius(9);
        _badge.VerticalAlignment = VerticalAlignment.Top;
        _badge.Margin = new Thickness(0, 1, 10, 0);
        _icon.FontFamily = (FontFamily)Application.Current.Resources["IconFont"];
        _icon.FontSize = 10;
        _icon.FontWeight = FontWeights.Bold;
        _icon.HorizontalAlignment = HorizontalAlignment.Center;
        _icon.VerticalAlignment = VerticalAlignment.Center;
        _badge.Child = _icon;

        _title.Style = (Style)Application.Current.Resources["BaseText"];
        _title.FontWeight = FontWeights.SemiBold;
        _title.FontSize = 13;
        _title.TextWrapping = TextWrapping.Wrap;
        _message.Style = (Style)Application.Current.Resources["BaseText"];
        _message.FontSize = 12.5;
        _message.TextWrapping = TextWrapping.Wrap;
        _message.Margin = new Thickness(0, 2, 0, 0);
        _message.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");

        _close.Style = (Style)Application.Current.Resources["FluentIconButton"];
        _close.Content = new TextBlock { Text = "", FontFamily = (FontFamily)Application.Current.Resources["IconFont"], FontSize = 10 };
        _close.VerticalAlignment = VerticalAlignment.Top;
        _close.Margin = new Thickness(6, -4, 0, 0);
        _close.ToolTip = "Dismiss";
        _close.Click += (_, _) => Closed?.Invoke();

        var text = new StackPanel();
        text.Children.Add(_title);
        text.Children.Add(_message);
        text.Children.Add(_actions);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(_badge);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        Grid.SetColumn(_close, 2);
        grid.Children.Add(_close);
        Child = grid;
        Set(Severity.Info, "", "");
    }

    public bool IsClosable { get => _close.Visibility == Visibility.Visible; set => _close.Visibility = value ? Visibility.Visible : Visibility.Collapsed; }

    public void Set(Severity severity, string title, string message)
    {
        var (bg, fg, glyph) = severity switch
        {
            Severity.Error => ("InfoErrorBrush", "InfoErrorIconBrush", ""),
            Severity.Warning => ("InfoWarningBrush", "InfoWarningIconBrush", "!"),
            Severity.Success => ("InfoSuccessBrush", "InfoSuccessIconBrush", ""),
            _ => ("InfoNeutralBrush", "AccentBrush", "i"),
        };
        SetResourceReference(BackgroundProperty, bg);
        _badge.SetResourceReference(BackgroundProperty, fg);
        _icon.SetResourceReference(TextBlock.ForegroundProperty, severity == Severity.Info ? "OnAccentBrush" : "CardSolidBrush");
        _icon.Text = glyph;
        _icon.FontFamily = glyph.Length == 1 && char.IsLetterOrDigit(glyph[0]) || glyph == "!"
            ? new FontFamily("Segoe UI Variable Display, Segoe UI")
            : (FontFamily)Application.Current.Resources["IconFont"];
        _icon.FontSize = glyph is "!" or "i" ? 12 : 9;
        _title.Text = title;
        _title.Visibility = string.IsNullOrEmpty(title) ? Visibility.Collapsed : Visibility.Visible;
        _message.Text = message;
        _message.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    public void SetActions(params UIElement[] actions)
    {
        _actions.Children.Clear();
        foreach (var a in actions) _actions.Children.Add(a);
        _actions.Visibility = actions.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}

public static class UI
{
    public static Style Style(string key) => (Style)Application.Current.Resources[key];
    public static FontFamily IconFont => (FontFamily)Application.Current.Resources["IconFont"];
    public static FontFamily MonoFont => (FontFamily)Application.Current.Resources["MonoFont"];

    public static TextBlock Text(string text, double size = 14, string brush = "TextPrimaryBrush", FontWeight? weight = null,
        bool wrap = false, bool mono = false)
    {
        var t = new TextBlock { Text = text, Style = Style("BaseText"), FontSize = size };
        if (weight is { } w) t.FontWeight = w;
        if (wrap) t.TextWrapping = TextWrapping.Wrap;
        else t.TextTrimming = TextTrimming.CharacterEllipsis;
        if (mono) t.FontFamily = MonoFont;
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    public static TextBlock Glyph(string glyph, double size = 14, string brush = "TextPrimaryBrush")
    {
        var t = new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = size, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    public static TextBlock Glyph(string glyph, double size, Color color) =>
        new() { Text = glyph, FontFamily = IconFont, FontSize = size, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(color) };

    /// <summary>Small coloured pill: "Best", "Preview", "Not free".</summary>
    public static Border Tag(string text, string hex)
    {
        var c = ThemeManager.Hex(hex);
        return new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6, 0, 6, 1),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb(0x26, c.R, c.G, c.B)),
            Child = new TextBlock { Text = text, Style = Style("TagText"), Foreground = new SolidColorBrush(c) },
        };
    }

    public static Button IconButton(string glyph, string tooltip, Action onClick, double size = 13)
    {
        var b = new Button { Style = Style("FluentIconButton"), Content = Glyph(glyph, size), ToolTip = tooltip };
        b.Click += (_, _) => onClick();
        return b;
    }

    public static Button Button(string text, Action onClick, bool primary = false, string? glyph = null)
    {
        object content = text;
        if (glyph is not null)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            var g = Glyph(glyph, 13, primary ? "OnAccentBrush" : "TextPrimaryBrush");
            g.Margin = new Thickness(0, 0, 8, 0);
            sp.Children.Add(g);
            sp.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            content = sp;
        }
        var b = new Button { Style = Style(primary ? "FluentPrimaryButton" : "FluentButton"), Content = content };
        b.Click += (_, _) => onClick();
        return b;
    }

    public static Button Link(string text, Action onClick, double size = 13)
    {
        var b = new Button { Style = Style("LinkButton"), Content = text, FontSize = size };
        b.Click += (_, _) => onClick();
        return b;
    }

    public static Border Card(UIElement child, Thickness? padding = null)
    {
        var b = new Border { Style = Style("FluentCard"), Child = child };
        if (padding is { } p) b.Padding = p;
        return b;
    }

    /// <summary>A Windows Settings style row: icon, header + description, control on the right.</summary>
    public static Border SettingsRow(string glyph, string header, string? description, UIElement? control)
    {
        var grid = new Grid { MinHeight = 44 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = Glyph(glyph, 18);
        icon.Margin = new Thickness(2, 0, 16, 0);
        grid.Children.Add(icon);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Text(header));
        if (!string.IsNullOrEmpty(description)) text.Children.Add(Text(description!, 12, "TextSecondaryBrush", wrap: true));
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        if (control is not null)
        {
            if (control is FrameworkElement fe) { fe.VerticalAlignment = VerticalAlignment.Center; fe.Margin = new Thickness(16, 0, 0, 0); }
            Grid.SetColumn(control, 2);
            grid.Children.Add(control);
        }
        var card = Card(grid, new Thickness(16, 10, 16, 10));
        card.Margin = new Thickness(0, 0, 0, 4);
        return card;
    }

    public static CheckBox Toggle(bool isOn, Action<bool> changed, string? label = null)
    {
        var t = new CheckBox { Style = Style("FluentToggleSwitch"), IsChecked = isOn, Content = label };
        t.Click += (_, _) => changed(t.IsChecked == true);
        return t;
    }

    /// <summary>Indeterminate progress ring (WinUI ProgressRing look): an arc that spins.</summary>
    public static FrameworkElement Spinner(double size = 16)
    {
        var arc = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse($"M {size / 2},1.5 A {size / 2 - 1.5},{size / 2 - 1.5} 0 0 1 {size - 1.5},{size / 2}".Replace(",", ",")),
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = size,
            Height = size,
        };
        arc.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "AccentBrush");
        var rot = new RotateTransform(0, size / 2, size / 2);
        arc.RenderTransform = rot;
        rot.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
        return arc;
    }

    public static void FadeIn(UIElement e, int ms = 180)
    {
        e.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)));
    }
}
