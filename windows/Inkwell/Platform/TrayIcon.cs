using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Inkwell.Platform;

public enum TrayState { Normal, Paused, Working }

/// <summary>
/// The notification-area icon, via Shell_NotifyIcon directly (NOTIFYICON_VERSION_4), so we get the icon's exact
/// on-screen rectangle for placing the flyout, and survive Explorer restarts. Shows the full-colour app icon;
/// dimmed when paused, with a pulsing sparkle badge while the AI is working.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int CallbackMessage = Win32.WM_APP + 1;
    private static readonly Guid IconGuid = new("6E3B7F0A-8C1D-4B5E-9F2A-1D4C7B8E9A01");

    private readonly HwndSource _window;
    private readonly int _taskbarCreated = Win32.RegisterWindowMessage("TaskbarCreated");
    private readonly DispatcherTimer _pulse = new() { Interval = TimeSpan.FromMilliseconds(110) };
    private IntPtr _icon;
    private IntPtr[] _workingFrames = Array.Empty<IntPtr>();
    private int _frame;
    private TrayState _state = TrayState.Normal;
    private string _tooltip = "Inkwell";
    private bool _added;
    private BitmapSource? _appIcon;

    public event Action? LeftClick;
    public event Action? RightClick;

    /// <summary>Handle of the hidden window that owns the icon (and the popup menus).</summary>
    public IntPtr Handle => _window.Handle;

    public TrayIcon()
    {
        var p = new HwndSourceParameters("InkwellTray") { Width = 0, Height = 0, WindowStyle = 0, ExtendedWindowStyle = Win32.WS_EX_TOOLWINDOW };
        _window = new HwndSource(p);
        _window.AddHook(WndProc);
        _pulse.Tick += (_, _) => NextFrame();
        Rebuild();
        Add();
    }

    public void Update(TrayState state, string tooltip)
    {
        var tip = tooltip.Length > 127 ? tooltip[..127] : tooltip;
        if (state == _state && tip == _tooltip) return;
        _tooltip = tip;
        if (state != _state)
        {
            var dimChanged = (state == TrayState.Paused) != (_state == TrayState.Paused);
            _state = state;
            if (dimChanged) Rebuild();
            if (state == TrayState.Working) { _frame = 0; _pulse.Start(); }
            else _pulse.Stop();
            SetIcon(state == TrayState.Working ? _workingFrames[0] : _icon);
        }
        Modify();
    }

    /// <summary>Re-renders the icon (DPI or theme changed).</summary>
    public void Rebuild()
    {
        foreach (var h in _workingFrames) Win32.DestroyIcon(h);
        if (_icon != IntPtr.Zero) Win32.DestroyIcon(_icon);
        int px = Math.Max(16, Win32.GetSystemMetrics(Win32.SM_CXSMICON));
        _icon = Render(px, _state == TrayState.Paused, -1);
        _workingFrames = new IntPtr[8];
        for (int i = 0; i < _workingFrames.Length; i++) _workingFrames[i] = Render(px, false, i / (double)_workingFrames.Length);
        _current = _state == TrayState.Working ? _workingFrames[0] : _icon;
        if (_added) Modify();
    }

    /// <summary>The icon's rectangle in screen pixels.</summary>
    public Win32.RECT? IconRect()
    {
        var id = new Win32.NOTIFYICONIDENTIFIER { cbSize = Marshal.SizeOf<Win32.NOTIFYICONIDENTIFIER>(), hWnd = Handle, uID = 1 };
        return Win32.Shell_NotifyIconGetRect(ref id, out var r) == 0 ? r : null;
    }

    private IntPtr _current;

    private void SetIcon(IntPtr icon) { _current = icon; }

    private void NextFrame()
    {
        _frame = (_frame + 1) % _workingFrames.Length;
        _current = _workingFrames[_frame];
        Modify();
    }

    private Win32.NOTIFYICONDATA Data(int flags) => new()
    {
        cbSize = Marshal.SizeOf<Win32.NOTIFYICONDATA>(),
        hWnd = Handle,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _current == IntPtr.Zero ? _icon : _current,
        szTip = _tooltip,
        szInfo = "",
        szInfoTitle = "",
        uVersion = Win32.NOTIFYICON_VERSION_4,
    };

    private void Add()
    {
        var d = Data(Win32.NIF_MESSAGE | Win32.NIF_ICON | Win32.NIF_TIP | Win32.NIF_SHOWTIP);
        _added = Win32.Shell_NotifyIcon(Win32.NIM_ADD, ref d);
        var v = Data(0);
        Win32.Shell_NotifyIcon(Win32.NIM_SETVERSION, ref v);
    }

    private void Modify()
    {
        var d = Data(Win32.NIF_ICON | Win32.NIF_TIP | Win32.NIF_SHOWTIP);
        if (!Win32.Shell_NotifyIcon(Win32.NIM_MODIFY, ref d)) Add();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == CallbackMessage)
        {
            int ev = (int)((long)lParam & 0xFFFF);
            switch (ev)
            {
                case Win32.NIN_SELECT:
                case Win32.NIN_KEYSELECT:
                    LeftClick?.Invoke();
                    handled = true;
                    break;
                case Win32.WM_CONTEXTMENU:
                    RightClick?.Invoke();
                    handled = true;
                    break;
            }
        }
        else if (msg == _taskbarCreated && _taskbarCreated != 0)
        {
            Add(); // Explorer restarted
        }
        else if (msg == 0x02E0 /* WM_DPICHANGED */ || msg == 0x001A /* WM_SETTINGCHANGE */)
        {
            Application.Current?.Dispatcher.BeginInvoke(Rebuild);
        }
        return IntPtr.Zero;
    }

    // MARK: Rendering

    /// <param name="pulse">-1 = no badge; otherwise 0..1 phase of the working sparkle's pulse.</param>
    private IntPtr Render(int px, bool dimmed, double pulse)
    {
        _appIcon ??= LoadAppIcon();
        var dv = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
        using (var dc = dv.RenderOpen())
        {
            if (dimmed) dc.PushOpacity(0.45);
            dc.DrawImage(_appIcon, new Rect(0, 0, px, px));
            if (dimmed) dc.Pop();

            if (pulse >= 0)
            {
                // Sparkle badge, bottom-right, breathing in size and brightness.
                double s = 0.5 + 0.5 * Math.Sin(pulse * 2 * Math.PI);
                double r = px * (0.26 + 0.08 * s);
                var c = new Point(px - px * 0.27, px - px * 0.27);
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(235, 24, 24, 32)), null, c, r * 1.05, r * 1.05);
                var fill = new LinearGradientBrush(Color.FromRgb(0xE9, 0xD5, 0xFF), Color.FromRgb(0x67, 0xE8, 0xF9), 45);
                dc.PushOpacity(0.75 + 0.25 * s);
                dc.DrawGeometry(fill, null, Star(c.X, c.Y, r * 0.85));
                dc.Pop();
            }
        }
        var rtb = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        return ToHIcon(rtb);
    }

    private static Geometry Star(double cx, double cy, double r)
    {
        var k = r * 0.2;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(cx, cy - r), true, true);
            ctx.QuadraticBezierTo(new Point(cx + k, cy - k), new Point(cx + r, cy), true, true);
            ctx.QuadraticBezierTo(new Point(cx + k, cy + k), new Point(cx, cy + r), true, true);
            ctx.QuadraticBezierTo(new Point(cx - k, cy + k), new Point(cx - r, cy), true, true);
            ctx.QuadraticBezierTo(new Point(cx - k, cy - k), new Point(cx, cy - r), true, true);
        }
        g.Freeze();
        return g;
    }

    private static BitmapSource LoadAppIcon()
    {
        var img = new BitmapImage();
        img.BeginInit();
        img.UriSource = new Uri("pack://application:,,,/Assets/AppIcon.png");
        img.DecodePixelWidth = 128;
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.EndInit();
        img.Freeze();
        return img;
    }

    private static IntPtr ToHIcon(BitmapSource src)
    {
        int w = src.PixelWidth, h = src.PixelHeight;
        var pixels = new byte[w * h * 4];
        src.CopyPixels(pixels, w * 4, 0);
        // Icons want straight (non-premultiplied) alpha.
        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte a = pixels[i + 3];
            if (a is > 0 and < 255)
            {
                pixels[i] = (byte)Math.Min(255, pixels[i] * 255 / a);
                pixels[i + 1] = (byte)Math.Min(255, pixels[i + 1] * 255 / a);
                pixels[i + 2] = (byte)Math.Min(255, pixels[i + 2] * 255 / a);
            }
        }
        var bmi = new Win32.BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<Win32.BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32,
        };
        var color = Win32.CreateDIBSection(IntPtr.Zero, ref bmi, 0, out var bits, IntPtr.Zero, 0);
        Marshal.Copy(pixels, 0, bits, pixels.Length);
        var mask = Win32.CreateBitmap(w, h, 1, 1, null);
        var info = new Win32.ICONINFO { fIcon = true, hbmColor = color, hbmMask = mask };
        var icon = Win32.CreateIconIndirect(ref info);
        Win32.DeleteObject(color);
        Win32.DeleteObject(mask);
        return icon;
    }

    public void Dispose()
    {
        _pulse.Stop();
        var d = Data(0);
        Win32.Shell_NotifyIcon(Win32.NIM_DELETE, ref d);
        foreach (var h in _workingFrames) Win32.DestroyIcon(h);
        if (_icon != IntPtr.Zero) Win32.DestroyIcon(_icon);
        _window.Dispose();
    }
}
