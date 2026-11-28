using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Inkwell.Platform;

namespace Inkwell.Views;

/// <summary>Shows the floating "AI is working" pill. Never takes focus, so typing is never interrupted.</summary>
public static class HudController
{
    private static HudWindow? _window;
    private static HudWindow Window => _window ??= new HudWindow();

    public static void Working(string title) => Window.ShowWorking(title);
    public static void Done(string title) => Window.ShowDone(title);
    public static void Failed(string message) => Window.ShowFailed(message);
    public static void Hide() => _window?.HideAnimated();
    /// <summary>Screen-pixel rectangle of the field being edited; the pill floats just above it.</summary>
    public static void Anchor(System.Drawing.Rectangle? field) => Window.AnchorTo(field);
}

/// <summary>
/// A glassy pill with a colourful conic ring that spins clockwise while the AI works, a twinkling sparkle and
/// shimmering text. Turns green with a check when done, red with a clear two-line message on failure.
/// Sits bottom-centre above the taskbar, like Windows 11's own volume / brightness indicators.
/// </summary>
internal sealed class HudWindow : Window
{
    private const double Radius = 22;
    private const double RingWidth = 2.2;

    private readonly Grid _root = new() { Margin = new Thickness(28) };
    private readonly ScaleTransform _scale = new(0.85, 0.85);
    private readonly TranslateTransform _lift = new(0, 14);

    private readonly Grid _glow = new() { Opacity = 0, IsHitTestVisible = false };
    private readonly Canvas _glowCanvas = new();
    private readonly Image _glowImage = new();

    private readonly Grid _ring = new();
    private readonly Canvas _ringCanvas = new();
    private readonly Image _ringImage = new();
    private readonly Border _stateRing = new() { Opacity = 0 };
    private readonly RotateTransform _spin = new(0);

    private readonly Border _pill = new();
    private readonly Border _shadow = new();
    private readonly Grid _iconHost = new() { Width = 22, Height = 22, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _title = new();
    private readonly TextBlock _subtitle = new() { Visibility = Visibility.Collapsed };
    private readonly LinearGradientBrush _shimmer;
    private readonly TranslateTransform _shimmerShift = new(-1, 0);

    private readonly DispatcherTimer _hideTimer = new();
    private bool _spinning;
    private enum Phase { Hidden, Working, Done, Failed }
    private Phase _phase = Phase.Hidden;

    public HudWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Title = "Inkwell indicator";
        UseLayoutRounding = true;

        var conic = ConicBitmap();
        _ringImage.Source = conic;
        _glowImage.Source = conic;
        _ringImage.RenderTransform = _spin;
        _ringImage.RenderTransformOrigin = new Point(0.5, 0.5);
        _glowImage.RenderTransform = _spin;
        _glowImage.RenderTransformOrigin = new Point(0.5, 0.5);
        _ringCanvas.Children.Add(_ringImage);
        _glowCanvas.Children.Add(_glowImage);

        // Glow: the same spinning colours, blurred, behind the pill.
        _glow.Children.Add(_glowCanvas);
        _glow.Effect = new BlurEffect { Radius = 22, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance };

        _shadow.CornerRadius = new CornerRadius(Radius);
        _shadow.Background = Brushes.Black;
        _shadow.Opacity = 0.22;
        _shadow.Margin = new Thickness(6, 8, 6, -2);
        _shadow.Effect = new BlurEffect { Radius = 18 };

        _ring.Children.Add(_ringCanvas);
        _stateRing.CornerRadius = new CornerRadius(Radius);
        _ring.Children.Add(_stateRing);
        _ring.SizeChanged += (_, _) => LayoutRing();

        _pill.CornerRadius = new CornerRadius(Radius - RingWidth);
        _pill.Margin = new Thickness(RingWidth);
        _pill.Padding = new Thickness(14, 10, 18, 10);
        _pill.MinHeight = 2 * Radius - 2 * RingWidth;

        _shimmer = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5), RelativeTransform = _shimmerShift };

        _title.FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
        _title.FontSize = 14;
        _title.FontWeight = FontWeights.SemiBold;
        _title.TextTrimming = TextTrimming.CharacterEllipsis;
        _title.MaxWidth = 380;
        _subtitle.FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        _subtitle.FontSize = 12.5;
        _subtitle.TextWrapping = TextWrapping.Wrap;
        _subtitle.MaxWidth = 380;
        _subtitle.Margin = new Thickness(0, 2, 0, 0);
        _subtitle.MaxHeight = 3 * 17;
        _subtitle.TextTrimming = TextTrimming.CharacterEllipsis;

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        text.Children.Add(_title);
        text.Children.Add(_subtitle);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_iconHost);
        row.Children.Add(text);
        _pill.Child = row;
        _ring.Children.Add(_pill);

        _root.Children.Add(_glow);
        _root.Children.Add(_shadow);
        _root.Children.Add(_ring);
        Panel.SetZIndex(_glow, 0);
        Panel.SetZIndex(_shadow, 1);
        Panel.SetZIndex(_ring, 2);
        _root.RenderTransformOrigin = new Point(0.5, 1);
        _root.RenderTransform = new TransformGroup { Children = { _scale, _lift } };
        _root.Opacity = 0;
        Content = _root;

        ApplyTheme();
        ThemeManager.ThemeChanged += ApplyTheme;
        SizeChanged += (_, _) => Reposition();
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); HideAnimated(); };
        SourceInitialized += (_, _) => Win32.MakeNonActivatingOverlay(this);
    }

    // MARK: States

    public void ShowWorking(string title)
    {
        _hideTimer.Stop();
        var wasWorking = _phase == Phase.Working;
        _phase = Phase.Working;
        SetIcon(Sparkle());
        _subtitle.Visibility = Visibility.Collapsed;
        SetTitle(title.EndsWith("…", StringComparison.Ordinal) ? title : title + "…", shimmer: true, animate: wasWorking);
        Fade(_stateRing, 0, 250);
        Fade(_ringCanvas, 1, 250);
        Fade(_glow, 0.75, 400);
        StartSpin();
        Present();
    }

    public void ShowDone(string title)
    {
        _phase = Phase.Done;
        SetIcon(Badge("#22C55E", check: true));
        _subtitle.Visibility = Visibility.Collapsed;
        SetTitle(title, shimmer: false, animate: true);
        _stateRing.Background = new LinearGradientBrush(ThemeManager.Hex("#4ADE80"), ThemeManager.Hex("#16A34A"), 90);
        Fade(_stateRing, 1, 300);
        Fade(_ringCanvas, 0, 300);
        Fade(_glow, 0, 500);
        Present();
        HideAfter(TimeSpan.FromSeconds(1.1));
    }

    public void ShowFailed(string message)
    {
        _phase = Phase.Failed;
        SetIcon(Badge("#EF4444", check: false));
        var (title, detail) = SplitError(message);
        SetTitle(title, shimmer: false, animate: true);
        _subtitle.Text = detail;
        _subtitle.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
        _stateRing.Background = new LinearGradientBrush(ThemeManager.Hex("#F87171"), ThemeManager.Hex("#DC2626"), 90);
        Fade(_stateRing, 1, 300);
        Fade(_ringCanvas, 0, 300);
        Fade(_glow, 0, 500);
        Present();
        // Long enough to read: ~3 s plus a little per character, at most 7 s.
        HideAfter(TimeSpan.FromSeconds(Math.Min(7, 3 + message.Length * 0.03)));
    }

    /// <summary>"All Gemini keys hit today's free limit. Next available…" → title + detail.</summary>
    private static (string Title, string Detail) SplitError(string message)
    {
        var m = message.Trim();
        var cut = m.IndexOf(". ", StringComparison.Ordinal);
        if (cut > 0 && cut < 70) return (m[..cut], m[(cut + 2)..].Trim());
        if (m.Length <= 60) return (m.TrimEnd('.'), "");
        return ("Couldn't finish", m);
    }

    public void HideAnimated()
    {
        if (_phase == Phase.Hidden && !IsVisible) return;
        _phase = Phase.Hidden;
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var d = TimeSpan.FromMilliseconds(220);
        var fade = new DoubleAnimation(0, d) { EasingFunction = ease };
        fade.Completed += (_, _) =>
        {
            if (_phase != Phase.Hidden) return;
            StopSpin();
            Hide();
            _anchor = null;
        };
        _root.BeginAnimation(OpacityProperty, fade);
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.92, d) { EasingFunction = ease });
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.92, d) { EasingFunction = ease });
        _lift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, d) { EasingFunction = ease });
    }

    private void HideAfter(TimeSpan delay)
    {
        _hideTimer.Stop();
        _hideTimer.Interval = delay;
        _hideTimer.Start();
    }

    private void Present()
    {
        var appearing = !IsVisible || _root.Opacity < 0.99;
        if (!IsVisible)
        {
            Show();
            Reposition();
        }
        if (!appearing) return;
        var spring = new BackEase { Amplitude = 0.45, EasingMode = EasingMode.EaseOut };
        var d = TimeSpan.FromMilliseconds(420);
        _root.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)));
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.82, 1, d) { EasingFunction = spring });
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.82, 1, d) { EasingFunction = spring });
        _lift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(16, 0, d) { EasingFunction = spring });
    }

    // MARK: Pieces

    private void SetTitle(string text, bool shimmer, bool animate)
    {
        void Apply()
        {
            _title.Text = text;
            if (shimmer)
            {
                _title.Foreground = _shimmer;
                var a = new DoubleAnimation(-1, 1, TimeSpan.FromSeconds(1.8)) { RepeatBehavior = RepeatBehavior.Forever };
                _shimmerShift.BeginAnimation(TranslateTransform.XProperty, a);
            }
            else
            {
                _shimmerShift.BeginAnimation(TranslateTransform.XProperty, null);
                _title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            }
        }

        if (!animate || _title.Text == text) { Apply(); return; }
        var outA = new DoubleAnimation(0, TimeSpan.FromMilliseconds(90));
        outA.Completed += (_, _) =>
        {
            Apply();
            _title.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        };
        _title.BeginAnimation(OpacityProperty, outA);
    }

    private void SetIcon(UIElement icon)
    {
        _iconHost.Children.Clear();
        _iconHost.Children.Add(icon);
        var pop = new ScaleTransform(0.3, 0.3);
        if (icon is FrameworkElement fe)
        {
            fe.RenderTransformOrigin = new Point(0.5, 0.5);
            fe.RenderTransform = fe.RenderTransform is TransformGroup g ? AddFirst(g, pop) : pop;
        }
        var spring = new BackEase { Amplitude = 0.6, EasingMode = EasingMode.EaseOut };
        pop.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(380)) { EasingFunction = spring });
        pop.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(380)) { EasingFunction = spring });

        static Transform AddFirst(TransformGroup g, Transform t) { g.Children.Insert(0, t); return g; }
    }

    /// <summary>A four-point AI sparkle with a twinkling companion star, gently pulsing.</summary>
    private static UIElement Sparkle()
    {
        var fill = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0), EndPoint = new Point(1, 1),
            GradientStops =
            {
                new GradientStop(ThemeManager.Hex("#C084FC"), 0),
                new GradientStop(ThemeManager.Hex("#818CF8"), 0.5),
                new GradientStop(ThemeManager.Hex("#22D3EE"), 1),
            }
        };
        var big = new Path { Data = Star(11, 12, 9), Fill = fill };
        var small = new Path { Data = Star(18.5, 4.5, 3.6), Fill = fill };
        var canvas = new Canvas { Width = 22, Height = 22 };
        canvas.Children.Add(big);
        canvas.Children.Add(small);

        var pulse = new ScaleTransform(1, 1, 11, 12);
        big.RenderTransform = pulse;
        var beat = new DoubleAnimation(0.82, 1.08, TimeSpan.FromMilliseconds(650))
        {
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        pulse.BeginAnimation(ScaleTransform.ScaleXProperty, beat);
        pulse.BeginAnimation(ScaleTransform.ScaleYProperty, beat);
        small.BeginAnimation(OpacityProperty, new DoubleAnimation(0.15, 1, TimeSpan.FromMilliseconds(500))
        {
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromMilliseconds(250)
        });
        return canvas;
    }

    private static Geometry Star(double cx, double cy, double r)
    {
        // Concave four-point star: tips on the axes, curves pinched toward the centre.
        var k = r * 0.18;
        var g = Geometry.Parse(FormattableString.Invariant(
            $"M {cx},{cy - r} Q {cx + k},{cy - k} {cx + r},{cy} Q {cx + k},{cy + k} {cx},{cy + r} Q {cx - k},{cy + k} {cx - r},{cy} Q {cx - k},{cy - k} {cx},{cy - r} Z"));
        g.Freeze();
        return g;
    }

    private static UIElement Badge(string hex, bool check)
    {
        var grid = new Grid { Width = 22, Height = 22 };
        grid.Children.Add(new Ellipse { Fill = new SolidColorBrush(ThemeManager.Hex(hex)) });
        if (check)
        {
            grid.Children.Add(new Path
            {
                Data = Geometry.Parse("M 6.5,11.5 L 9.6,14.6 L 15.6,8"),
                Stroke = Brushes.White, StrokeThickness = 2.2,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round
            });
        }
        else
        {
            grid.Children.Add(new Rectangle { Width = 2.4, Height = 7.5, RadiusX = 1.2, RadiusY = 1.2, Fill = Brushes.White, Margin = new Thickness(0, -4, 0, 0) });
            grid.Children.Add(new Ellipse { Width = 2.8, Height = 2.8, Fill = Brushes.White, Margin = new Thickness(0, 11, 0, 0) });
        }
        return grid;
    }

    // MARK: Spin

    private void StartSpin()
    {
        if (_spinning) return;
        _spinning = true;
        // Positive angles rotate clockwise on screen.
        _spin.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(2.4)) { RepeatBehavior = RepeatBehavior.Forever });
    }

    private void StopSpin()
    {
        _spinning = false;
        _spin.BeginAnimation(RotateTransform.AngleProperty, null);
        _shimmerShift.BeginAnimation(TranslateTransform.XProperty, null);
    }

    private void LayoutRing()
    {
        double w = _ring.ActualWidth, h = _ring.ActualHeight;
        if (w <= 0 || h <= 0) return;
        var d = Math.Sqrt(w * w + h * h) + 4;
        foreach (var (canvas, image) in new[] { (_ringCanvas, _ringImage), (_glowCanvas, _glowImage) })
        {
            image.Width = d;
            image.Height = d;
            Canvas.SetLeft(image, (w - d) / 2);
            Canvas.SetTop(image, (h - d) / 2);
            canvas.Width = w;
            canvas.Height = h;
        }
        var r = Math.Min(Radius, h / 2);
        _ringCanvas.Clip = new RectangleGeometry(new Rect(0, 0, w, h), r, r);
        _glowCanvas.Clip = new RectangleGeometry(new Rect(-2, -2, w + 4, h + 4), r, r);
        _stateRing.CornerRadius = new CornerRadius(r);
        _pill.CornerRadius = new CornerRadius(Math.Max(0, r - RingWidth));
        _shadow.CornerRadius = new CornerRadius(r);
    }

    /// <summary>Purple → indigo → blue → cyan → pink → purple, swept around the centre.</summary>
    private static BitmapSource ConicBitmap()
    {
        const int size = 256;
        var stops = new[] { "#A855F7", "#6366F1", "#3B82F6", "#22D3EE", "#F472B6", "#A855F7" };
        var colors = Array.ConvertAll(stops, ThemeManager.Hex);
        var pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double a = (Math.Atan2(y - size / 2.0, x - size / 2.0) + Math.PI) / (2 * Math.PI); // 0..1
                double p = a * (colors.Length - 1);
                int i = Math.Min((int)p, colors.Length - 2);
                double t = p - i;
                var c0 = colors[i];
                var c1 = colors[i + 1];
                int o = (y * size + x) * 4;
                pixels[o] = (byte)(c0.B + (c1.B - c0.B) * t);
                pixels[o + 1] = (byte)(c0.G + (c1.G - c0.G) * t);
                pixels[o + 2] = (byte)(c0.R + (c1.R - c0.R) * t);
                pixels[o + 3] = 255;
            }
        }
        var bmp = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
        bmp.Freeze();
        return bmp;
    }

#if DEBUG
    /// <summary>Screenshot mode: freezes the working state mid-spin, offscreen, and returns the visual to render.</summary>
    internal FrameworkElement FreezeForScreenshot(string title, double angle)
    {
        ShowWorking(title);
        Left = -20000;
        foreach (var (el, value) in new (UIElement, double)[] { (_root, 1), (_glow, 0.75), (_ringCanvas, 1), (_stateRing, 0), (_title, 1) })
        {
            el.BeginAnimation(OpacityProperty, null);
            el.Opacity = value;
        }
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _scale.ScaleX = _scale.ScaleY = 1;
        _lift.BeginAnimation(TranslateTransform.YProperty, null);
        _lift.Y = 0;
        _spin.BeginAnimation(RotateTransform.AngleProperty, null);
        _spin.Angle = angle;
        _shimmerShift.BeginAnimation(TranslateTransform.XProperty, null);
        _shimmerShift.X = 0.05;
        UpdateLayout();
        LayoutRing();
        return _root;
    }
#endif

    // MARK: Theme & position

    private void ApplyTheme()
    {
        var dark = ThemeManager.IsDark;
        _pill.Background = new SolidColorBrush(dark ? ThemeManager.Hex("#F21E1E22") : ThemeManager.Hex("#F7FCFCFE"));
        var baseColor = dark ? Colors.White : ThemeManager.Hex("#1B1B1F");
        var shine = dark ? ThemeManager.Hex("#C4B5FD") : ThemeManager.Hex("#7C3AED");
        _shimmer.GradientStops = new GradientStopCollection
        {
            new(baseColor, 0), new(baseColor, 0.35), new(shine, 0.5), new(baseColor, 0.65), new(baseColor, 1),
        };
        _subtitle.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        if (_phase != Phase.Working) _title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
    }

    private System.Drawing.Rectangle? _anchor;

    public void AnchorTo(System.Drawing.Rectangle? field)
    {
        _anchor = field;
        Reposition();
    }

    /// <summary>
    /// Centres the pill just above the field (or below it when there's no room above). Works in physical pixels via
    /// SetWindowPos, so it lands correctly on monitors with different scaling. Without a field: bottom-centre.
    /// </summary>
    private void Reposition()
    {
        if (!IsVisible) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !Win32.GetWindowRect(hwnd, out var wr)) return;
        int w = wr.Right - wr.Left, h = wr.Bottom - wr.Top;
        double scale = ActualHeight > 0 ? h / ActualHeight : 1;
        int margin = (int)Math.Round(_root.Margin.Top * scale); // transparent room for the glow around the pill
        int gap = (int)Math.Round(6 * scale);

        int x, y;
        System.Drawing.Rectangle work;
        if (_anchor is { } a)
        {
            work = System.Windows.Forms.Screen.FromRectangle(a).WorkingArea;
            x = a.Left + a.Width / 2 - w / 2;
            y = a.Top - (h - margin) - gap;                      // pill bottom sits `gap` above the field
            if (y + margin < work.Top) y = a.Bottom + gap - margin; // no room above → just below
        }
        else
        {
            work = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
            x = work.Left + (work.Width - w) / 2;
            y = work.Bottom - h + margin - (int)Math.Round(24 * scale);
        }
        x = Math.Clamp(x, work.Left - margin, Math.Max(work.Left - margin, work.Right - w + margin));
        y = Math.Clamp(y, work.Top - margin, Math.Max(work.Top - margin, work.Bottom - h + margin));
        // Move through WPF (in DIPs) so its own layout never puts the window back somewhere else.
        Left = x / scale;
        Top = y / scale;
    }

    private static void Fade(UIElement e, double to, int ms) =>
        e.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new CubicEase() });
}
