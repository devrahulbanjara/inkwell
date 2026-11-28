#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Inkwell.Core;
using Inkwell.Providers;

namespace Inkwell.Views;

/// <summary>
/// Renders the main screens to PNGs with sample data for the README (debug builds only), like the Mac app's
/// INKWELL_SCREENSHOTS mode:  Inkwell.exe --screenshots docs\screenshots\windows
/// Uses throwaway stores, so it never touches real settings, keys or usage stats.
/// </summary>
public static class Screenshots
{
    public static async Task RunAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        DashboardControl.StaticRendering = true;
        foreach (var dark in new[] { false, true })
        {
            ThemeManager.ForcedDark = dark;
            ThemeManager.Apply();
            var mode = dark ? "dark" : "light";
            var app = DemoState();

            // Flyout: dashboard + provider page
            var dash = new DashboardControl();
            dash.Bind(app);
            await Settle();
            Save(Frame(dash, 380, "FlyoutBrush", 8), Path.Combine(dir, $"dashboard-{mode}.png"));

            var prov = new QuickProviderControl(app, ProviderKind.Gemini, false);
            await Settle();
            Save(Frame(prov, 380, "FlyoutBrush", 8), Path.Combine(dir, $"providers-{mode}.png"));

            // Settings window
            var settings = new SettingsWindow(app) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0 };
            settings.Show();
            foreach (var (page, name) in new[] { (SettingsPage.Providers, "settings"), (SettingsPage.Templates, "templates") })
            {
                settings.Navigate(page, animate: false);
                await Settle();
                var root = (Grid)settings.Content;
                root.SetResourceReference(Panel.BackgroundProperty, "WindowBrush");
                Save(Shadowed(root, settings.ActualWidth, settings.ActualHeight, 8), Path.Combine(dir, $"{name}-{mode}.png"));
            }
            settings.Hide();

            // Floating pill, mid-spin
            var hud = new HudWindow();
            var pill = hud.FreezeForScreenshot("Fixing grammar", 35);
            await Settle();
            // Draw through a VisualBrush so the glow around the pill is kept and the window offset isn't.
            var w = pill.ActualWidth + 56;
            var h = pill.ActualHeight + 56;
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
                dc.DrawRectangle(new VisualBrush(pill) { Stretch = Stretch.None }, null, new Rect(0, 0, w, h));
            Save(Render(dv, w, h), Path.Combine(dir, $"hud-{mode}.png"));
            hud.Hide();
        }
        Application.Current.Shutdown();
    }

    private static Task Settle() => Task.Delay(900);

    private static AppState DemoState()
    {
        var settings = new SettingsStore(ephemeral: true);
        settings.AddKey(ProviderKind.Gemini, "AIzaSyDemoKeyNotReal000000000000a1b2", "Personal");
        settings.AddKey(ProviderKind.Gemini, "AIzaSyDemoKeyNotReal000000000000c3d4", "Project 2");
        settings.AddKey(ProviderKind.Groq, "gsk_DemoKeyNotReal0000000000000000e5f6", "Groq free");

        var rng = new Random(7);
        var names = new[] { "Fix grammar", "Fix grammar", "Fix grammar", "Make formal", "Rewrite", "Ask", "Make shorter", "Reply" };
        var events = new List<UsageEvent>();
        var today = DateTime.Today;
        int[] perDay = { 6, 9, 4, 12, 8, 14, 11 };
        for (int d = 6; d >= 0; d--)
        {
            for (int i = 0; i < perDay[6 - d]; i++)
            {
                var words = rng.Next(12, 140);
                events.Add(new UsageEvent
                {
                    Date = today.AddDays(-d).AddHours(9 + rng.Next(0, 10)).AddMinutes(rng.Next(0, 59)),
                    TemplateName = names[rng.Next(names.Length)], Provider = "gemini", Model = "gemini-3.5-flash-lite",
                    InputTokens = 90 + words * 2, OutputTokens = words + rng.Next(5, 40), Latency = 0.8 + rng.NextDouble() * 1.4,
                    InputWords = words, Success = true, KeyLabel = "Personal",
                });
            }
        }
        var app = new AppState(settings, new UsageStore(ephemeral: true, seed: events));
        app.CliStatus.Frozen = true;
        app.CliStatus.Set(ProviderKind.ClaudeCode, new CLIStatus(CLIStatusState.Ready, @"C:\Users\you\.local\bin\claude.exe", "2.1.332 (Claude Code)", "Signed in · Claude subscription"));
        app.CliStatus.Set(ProviderKind.Codex, new CLIStatus(CLIStatusState.NotInstalled, Detail: "codex was not found on this PC."));
        return app;
    }

    /// <summary>Puts a control on a rounded, bordered flyout surface with a soft shadow, like the real window.</summary>
    private static FrameworkElement Frame(FrameworkElement content, double width, string background, double radius)
    {
        var card = new Border { Width = width, Padding = new Thickness(16), CornerRadius = new CornerRadius(radius), BorderThickness = new Thickness(1), Child = content };
        card.SetResourceReference(Border.BackgroundProperty, background);
        card.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
        card.Measure(new Size(width, double.PositiveInfinity));
        card.Arrange(new Rect(card.DesiredSize));
        card.UpdateLayout();
        return Shadowed(card, card.ActualWidth, card.ActualHeight, radius);
    }

    private static FrameworkElement Shadowed(FrameworkElement visual, double w, double h, double radius)
    {
        var brush = new VisualBrush(visual) { Stretch = Stretch.None };
        var grid = new Grid { Margin = new Thickness(28) };
        var shadow = new Border { Width = w, Height = h, CornerRadius = new CornerRadius(radius), Background = Brushes.Black, Opacity = 0.18, Effect = new BlurEffect { Radius = 26 }, Margin = new Thickness(0, 8, 0, -8) };
        grid.Children.Add(shadow);
        var rounded = new Border { Width = w, Height = h, CornerRadius = new CornerRadius(radius), Background = brush };
        rounded.Clip = new RectangleGeometry(new Rect(0, 0, w, h), radius, radius);
        grid.Children.Add(rounded);
        grid.Measure(new Size(w + 56, h + 56));
        grid.Arrange(new Rect(0, 0, w + 56, h + 56));
        grid.UpdateLayout();
        return grid;
    }

    private static BitmapSource Render(Visual visual, double w, double h)
    {
        const double scale = 2; // crisp on high-DPI READMEs
        var rtb = new RenderTargetBitmap((int)Math.Ceiling(w * scale), (int)Math.Ceiling(h * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    private static void Save(FrameworkElement e, string path) => Save(Render(e, e.ActualWidth + e.Margin.Left + e.Margin.Right, e.ActualHeight + e.Margin.Top + e.Margin.Bottom), path);

    private static void Save(BitmapSource bmp, string path)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
#endif
