using System.Windows;
using System.Windows.Controls;

namespace Inkwell.Views;

/// <summary>Placeholder text for TextBox and PasswordBox (WinUI's PlaceholderText).</summary>
public static class Input
{
    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.RegisterAttached(
        "Placeholder", typeof(string), typeof(Input), new PropertyMetadata("", OnPlaceholderChanged));

    public static string GetPlaceholder(DependencyObject d) => (string)d.GetValue(PlaceholderProperty);
    public static void SetPlaceholder(DependencyObject d, string value) => d.SetValue(PlaceholderProperty, value);

    public static readonly DependencyProperty IsEmptyProperty = DependencyProperty.RegisterAttached(
        "IsEmpty", typeof(bool), typeof(Input), new PropertyMetadata(true));

    public static bool GetIsEmpty(DependencyObject d) => (bool)d.GetValue(IsEmptyProperty);
    public static void SetIsEmpty(DependencyObject d, bool value) => d.SetValue(IsEmptyProperty, value);

    private static void OnPlaceholderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        switch (d)
        {
            case TextBox tb:
                tb.TextChanged -= TextChanged;
                tb.TextChanged += TextChanged;
                SetIsEmpty(tb, string.IsNullOrEmpty(tb.Text));
                break;
            case PasswordBox pb:
                pb.PasswordChanged -= PasswordChanged;
                pb.PasswordChanged += PasswordChanged;
                SetIsEmpty(pb, pb.SecurePassword.Length == 0);
                break;
        }
    }

    private static void TextChanged(object sender, TextChangedEventArgs e)
    {
        var tb = (TextBox)sender;
        SetIsEmpty(tb, string.IsNullOrEmpty(tb.Text));
    }

    private static void PasswordChanged(object sender, RoutedEventArgs e)
    {
        var pb = (PasswordBox)sender;
        SetIsEmpty(pb, pb.SecurePassword.Length == 0);
    }
}
