using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Inkwell.Platform;
using Microsoft.Win32;

namespace Inkwell.Views;

/// <summary>
/// Follows Windows' light/dark app mode and accent colour live, like WinUI apps do.
/// Every colour in the UI is a DynamicResource brush defined here, so a theme switch restyles everything at once.
/// </summary>
public static class ThemeManager
{
    public static bool IsDark { get; private set; }
    public static Color Accent { get; private set; } = Color.FromRgb(0x00, 0x67, 0xC0);

    public static event Action? ThemeChanged;

    /// <summary>Debug screenshot mode only: overrides the system theme.</summary>
    public static bool? ForcedDark { get; set; }

    public static void Initialize()
    {
        Apply();
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
                Application.Current?.Dispatcher.BeginInvoke(Apply);
        };
    }

    public static void Apply()
    {
        IsDark = ReadIsDark();
        var (accentLight, accentDark) = ReadAccent();
        Accent = IsDark ? accentLight : accentDark;

        var r = Application.Current.Resources;
        foreach (var (key, color) in Palette(IsDark)) r[key] = Freeze(new SolidColorBrush(color));
        r["AccentBrush"] = Freeze(new SolidColorBrush(Accent));
        r["AccentHoverBrush"] = Freeze(new SolidColorBrush(Color.FromArgb(0xE6, Accent.R, Accent.G, Accent.B)));
        r["AccentPressedBrush"] = Freeze(new SolidColorBrush(Color.FromArgb(0xCC, Accent.R, Accent.G, Accent.B)));
        r["AccentSubtleBrush"] = Freeze(new SolidColorBrush(Color.FromArgb(0x26, Accent.R, Accent.G, Accent.B)));
        // WinUI puts black text on accent buttons in dark mode, white in light mode.
        r["OnAccentBrush"] = Freeze(new SolidColorBrush(IsDark ? Colors.Black : Colors.White));
        r["AccentTextBrush"] = Freeze(new SolidColorBrush(IsDark ? accentLight : accentDark));
        ThemeChanged?.Invoke();
    }

    private static IEnumerable<(string, Color)> Palette(bool dark)
    {
        if (dark)
        {
            yield return ("TextPrimaryBrush", Hex("#FFFFFFFF"));
            yield return ("TextSecondaryBrush", Hex("#C5FFFFFF"));
            yield return ("TextTertiaryBrush", Hex("#87FFFFFF"));
            yield return ("TextDisabledBrush", Hex("#5DFFFFFF"));
            yield return ("WindowBrush", Hex("#FF202020"));
            yield return ("FlyoutBrush", Hex("#FF2C2C2C"));
            yield return ("PopupBrush", Hex("#FF2C2C2C"));
            yield return ("NavPaneBrush", Hex("#00000000"));
            yield return ("CardBrush", Hex("#0DFFFFFF"));
            yield return ("CardSolidBrush", Hex("#FF2B2B2B"));
            yield return ("CardHoverBrush", Hex("#15FFFFFF"));
            yield return ("CardBorderBrush", Hex("#1AFFFFFF"));
            yield return ("ControlBrush", Hex("#0FFFFFFF"));
            yield return ("ControlHoverBrush", Hex("#15FFFFFF"));
            yield return ("ControlPressedBrush", Hex("#08FFFFFF"));
            yield return ("ControlBorderBrush", Hex("#12FFFFFF"));
            yield return ("ControlStrongBorderBrush", Hex("#8BFFFFFF"));
            yield return ("InputBrush", Hex("#0BFFFFFF"));
            yield return ("InputFocusBrush", Hex("#B31E1E1E"));
            yield return ("SubtleHoverBrush", Hex("#0FFFFFFF"));
            yield return ("SubtlePressedBrush", Hex("#0AFFFFFF"));
            yield return ("SelectedBrush", Hex("#0FFFFFFF"));
            yield return ("DividerBrush", Hex("#15FFFFFF"));
            yield return ("SmokeBrush", Hex("#4D000000"));
            yield return ("InfoErrorBrush", Hex("#FF442726"));
            yield return ("InfoErrorIconBrush", Hex("#FFFF99A4"));
            yield return ("InfoWarningBrush", Hex("#FF433519"));
            yield return ("InfoWarningIconBrush", Hex("#FFFCE100"));
            yield return ("InfoSuccessBrush", Hex("#FF393D1B"));
            yield return ("InfoSuccessIconBrush", Hex("#FF6CCB5F"));
            yield return ("InfoNeutralBrush", Hex("#08FFFFFF"));
            yield return ("ScrollThumbBrush", Hex("#8BFFFFFF"));
            yield return ("ToggleOffKnobBrush", Hex("#CDFFFFFF"));
        }
        else
        {
            yield return ("TextPrimaryBrush", Hex("#E4000000"));
            yield return ("TextSecondaryBrush", Hex("#9E000000"));
            yield return ("TextTertiaryBrush", Hex("#72000000"));
            yield return ("TextDisabledBrush", Hex("#5C000000"));
            yield return ("WindowBrush", Hex("#FFF3F3F3"));
            yield return ("FlyoutBrush", Hex("#FFF9F9F9"));
            yield return ("PopupBrush", Hex("#FFF9F9F9"));
            yield return ("NavPaneBrush", Hex("#00000000"));
            yield return ("CardBrush", Hex("#B3FFFFFF"));
            yield return ("CardSolidBrush", Hex("#FFFBFBFB"));
            yield return ("CardHoverBrush", Hex("#FFFFFFFF"));
            yield return ("CardBorderBrush", Hex("#0F000000"));
            yield return ("ControlBrush", Hex("#B3FFFFFF"));
            yield return ("ControlHoverBrush", Hex("#80F9F9F9"));
            yield return ("ControlPressedBrush", Hex("#4DF9F9F9"));
            yield return ("ControlBorderBrush", Hex("#1A000000"));
            yield return ("ControlStrongBorderBrush", Hex("#72000000"));
            yield return ("InputBrush", Hex("#B3FFFFFF"));
            yield return ("InputFocusBrush", Hex("#FFFFFFFF"));
            yield return ("SubtleHoverBrush", Hex("#09000000"));
            yield return ("SubtlePressedBrush", Hex("#06000000"));
            yield return ("SelectedBrush", Hex("#09000000"));
            yield return ("DividerBrush", Hex("#12000000"));
            yield return ("SmokeBrush", Hex("#33000000"));
            yield return ("InfoErrorBrush", Hex("#FFFDE7E9"));
            yield return ("InfoErrorIconBrush", Hex("#FFC42B1C"));
            yield return ("InfoWarningBrush", Hex("#FFFFF4CE"));
            yield return ("InfoWarningIconBrush", Hex("#FF9D5D00"));
            yield return ("InfoSuccessBrush", Hex("#FFDFF6DD"));
            yield return ("InfoSuccessIconBrush", Hex("#FF0F7B0F"));
            yield return ("InfoNeutralBrush", Hex("#80F6F6F6"));
            yield return ("ScrollThumbBrush", Hex("#72000000"));
            yield return ("ToggleOffKnobBrush", Hex("#9E000000"));
        }
    }

    private static bool ReadIsDark()
    {
#if DEBUG
        if (ForcedDark is { } f) return f;
        if (Environment.GetEnvironmentVariable("INKWELL_THEME") is { } forced) return forced == "dark";
#endif
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    /// <summary>Taskbar/tray icons follow the *system* theme, which can differ from the app theme.</summary>
    public static bool SystemUsesDarkTaskbar()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return !(key?.GetValue("SystemUsesLightTheme") is int v && v == 1);
        }
        catch { return true; }
    }

    /// <summary>Windows' accent palette: index 1 = Light 2 (used in dark mode), index 4 = Dark 1 (light mode).</summary>
    private static (Color Light, Color Dark) ReadAccent()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
            if (key?.GetValue("AccentPalette") is byte[] p && p.Length >= 32)
                return (Color.FromRgb(p[4], p[5], p[6]), Color.FromRgb(p[16], p[17], p[18]));
        }
        catch { }
        return (Color.FromRgb(0x60, 0xCD, 0xFF), Color.FromRgb(0x00, 0x5F, 0xB8));
    }

    /// <summary>Applies dark title bar, rounded corners and the Mica / Acrylic backdrop to a window.</summary>
    public static bool ApplyBackdrop(Window window, int backdrop)
    {
        var ok = Win32.ApplyWindows11Backdrop(window, IsDark, backdrop);
        if (ok) window.Background = Brushes.Transparent;
        else window.SetResourceReference(Window.BackgroundProperty, backdrop == Win32.DWMSBT_TRANSIENTWINDOW ? "FlyoutBrush" : "WindowBrush");
        return ok;
    }

    public static Color ParseHex(string hex) => Hex(hex);

    public static Color Hex(string hex)
    {
        var h = hex.TrimStart('#');
        if (h.Length == 6) h = "FF" + h;
        var v = Convert.ToUInt32(h, 16);
        return Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    public static SolidColorBrush Brush(string hex, byte? alpha = null)
    {
        var c = Hex(hex);
        if (alpha.HasValue) c.A = alpha.Value;
        return Freeze(new SolidColorBrush(c));
    }

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }
}

/// <summary>Splits an error into a short bold title and a detail line, and picks a severity.</summary>
public static class Messages
{
    public static (string Title, string Detail) Split(string message)
    {
        var m = message.Trim();
        var cut = m.IndexOf(". ", StringComparison.Ordinal);
        if (cut > 0 && cut < 70) return (m[..cut], m[(cut + 2)..].Trim());
        if (m.Length <= 60) return (m.TrimEnd('.'), "");
        return ("Couldn't finish", m);
    }

    /// <summary>Limits and cooldowns are warnings (amber); everything else is an error (red).</summary>
    public static bool IsWarning(string message)
    {
        var l = message.ToLowerInvariant();
        return l.Contains("limit") || l.Contains("cooling down") || l.Contains("rate-limited") || l.Contains("switched fields");
    }
}
