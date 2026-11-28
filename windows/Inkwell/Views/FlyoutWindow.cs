using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using Inkwell.Core;
using Inkwell.Platform;

namespace Inkwell.Views;

/// <summary>
/// The tray flyout (the Mac popover): acrylic, rounded, slides up from the taskbar next to the tray icon like
/// Windows 11's Quick Settings, and closes when you click elsewhere. Hosts the dashboard and the provider page.
/// </summary>
public sealed class FlyoutWindow : Window
{
    private readonly AppState _app;
    private readonly Grid _host = new() { ClipToBounds = true };
    private readonly DashboardControl _dashboard = new();
    private QuickProviderControl? _provider;
    private readonly TranslateTransform _slide = new();
    private Win32.RECT? _anchor;
    private DateTime _hiddenAt = DateTime.MinValue;

    /// <summary>While a native menu or dialog is open, losing focus must not close the flyout.</summary>
    public bool SuppressAutoHide { get; set; }

    public event Action? OpenSettingsRequested;
    public event Action<SettingsPage>? OpenSettingsPageRequested;

    public FlyoutWindow(AppState app)
    {
        _app = app;
        Title = "Inkwell";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.Height;
        Width = 380;
        UseLayoutRounding = true;
        FontFamily = (FontFamily)Application.Current.Resources["UIFont"];
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            GlassFrameThickness = new Thickness(-1),
            ResizeBorderThickness = new Thickness(0),
            UseAeroCaptionButtons = false,
            CornerRadius = new CornerRadius(0),
        });

        _host.Children.Add(_dashboard);
        var root = new Border { Padding = new Thickness(16), Child = _host };
        root.RenderTransform = _slide;
        Content = root;

        _dashboard.Bind(app);
        _dashboard.OpenProviderRequested += kind => ShowProvider(kind, addKey: false);
        _dashboard.OpenSettingsRequested += () => { HideFlyout(); OpenSettingsRequested?.Invoke(); };
        _dashboard.MenuOpening += open => SuppressAutoHide = open;

        SourceInitialized += (_, _) => ApplyChrome();
        ThemeManager.ThemeChanged += ApplyChrome;
        Deactivated += (_, _) => { if (!SuppressAutoHide) HideFlyout(); };
        SizeChanged += (_, _) => Reposition();
        PreviewKeyDown += OnKey;
    }

    private void ApplyChrome() => ThemeManager.ApplyBackdrop(this, Win32.DWMSBT_TRANSIENTWINDOW);

    // MARK: Show / hide

    /// <summary>True when the flyout was hidden a moment ago by the same click that's now toggling it.</summary>
    public bool JustHidden => (DateTime.Now - _hiddenAt).TotalMilliseconds < 350;

    public void Toggle(Win32.RECT? trayIcon)
    {
        if (IsVisible) { HideFlyout(); return; }
        if (JustHidden) return;
        ShowFlyout(trayIcon);
    }

    public void ShowFlyout(Win32.RECT? trayIcon, (ProviderKind Kind, bool AddKey)? page = null)
    {
        _anchor = trayIcon ?? _anchor;
        if (page is { } p) ShowProvider(p.Kind, p.AddKey, animate: false);
        else if (!IsVisible) ShowDashboard(animate: false);

        var wasVisible = IsVisible;
        Opacity = 0;
        Show();
        UpdateLayout();
        Reposition();
        Activate();
        Win32.SetForegroundWindow(new WindowInteropHelper(this).Handle);
        Opacity = 1;
        if (!wasVisible)
        {
            // Slide up from the taskbar, like Windows 11 flyouts.
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            _slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(36, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
            ((UIElement)Content).BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        }
        _dashboard.Refresh();
    }

    public void HideFlyout()
    {
        if (!IsVisible) return;
        _hiddenAt = DateTime.Now;
        Hide();
        if (_provider is not null) ShowDashboard(animate: false);
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (_provider is not null && _host.Children.Contains(_provider)) ShowDashboard(animate: true);
        else HideFlyout();
        e.Handled = true;
    }

    // MARK: Pages

    public void ShowProvider(ProviderKind kind, bool addKey, bool animate = true)
    {
        _provider = new QuickProviderControl(_app, kind, addKey);
        _provider.BackRequested += () => ShowDashboard(animate: true);
        _provider.OpenSettingsRequested += () => { HideFlyout(); OpenSettingsPageRequested?.Invoke(SettingsPage.Providers); };
        _provider.MenuOpening += open => SuppressAutoHide = open;
        SwapTo(_provider, forward: true, animate);
    }

    public void ShowDashboard(bool animate)
    {
        SwapTo(_dashboard, forward: false, animate);
        _provider = null;
        _dashboard.Refresh();
    }

    private void SwapTo(FrameworkElement next, bool forward, bool animate)
    {
        if (_host.Children.Count == 1 && _host.Children[0] == next) return;
        var old = _host.Children.Count > 0 ? (FrameworkElement)_host.Children[0] : null;
        _host.Children.Clear();
        _host.Children.Add(next);
        if (!animate || old is null) return;

        var shift = new TranslateTransform(forward ? 40 : -40, 0);
        next.RenderTransform = shift;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        next.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
    }

    // MARK: Position

    /// <summary>Above the tray icon (or beside / below it when the taskbar is on another edge), 12 px from the edges.</summary>
    private void Reposition()
    {
        if (!IsVisible) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !Win32.GetWindowRect(hwnd, out var wr)) return;
        int w = wr.Right - wr.Left, h = wr.Bottom - wr.Top;
        double scale = ActualWidth > 0 ? w / ActualWidth : 1;
        int gap = (int)Math.Round(12 * scale);

        System.Drawing.Point at;
        if (_anchor is { } a) at = new System.Drawing.Point((a.Left + a.Right) / 2, (a.Top + a.Bottom) / 2);
        else at = System.Windows.Forms.Cursor.Position;
        var screen = System.Windows.Forms.Screen.FromPoint(at);
        var work = screen.WorkingArea;
        var bounds = screen.Bounds;

        int x = at.X - w / 2, y;
        if (work.Top > bounds.Top && at.Y < work.Top) y = work.Top + gap;                       // taskbar on top
        else if (work.Left > bounds.Left && at.X < work.Left) { x = work.Left + gap; y = at.Y - h / 2; } // left
        else if (work.Right < bounds.Right && at.X > work.Right) { x = work.Right - w - gap; y = at.Y - h / 2; } // right
        else y = work.Bottom - h - gap;                                                           // bottom (default)

        x = Math.Clamp(x, work.Left + gap, Math.Max(work.Left + gap, work.Right - w - gap));
        y = Math.Clamp(y, work.Top + gap, Math.Max(work.Top + gap, work.Bottom - h - gap));
        // Move through WPF (in DIPs) so its own layout never puts the window back somewhere else.
        Left = x / scale;
        Top = y / scale;
    }
}
