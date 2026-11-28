using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Inkwell.Core;
using Inkwell.Platform;
using Inkwell.Providers;

namespace Inkwell.Views;

public partial class DashboardControl : UserControl
{
    private AppState? _app;
    private StatsRange _range = StatsRange.Week;
    private bool _metricTokens = true;
    private bool _pulsing;

    /// <summary>Screenshot mode: draw final states instead of animating.</summary>
    public static bool StaticRendering { get; set; }

    public event Action<ProviderKind>? OpenProviderRequested;
    public event Action? OpenSettingsRequested;
    /// <summary>True while a native menu is open (the flyout must not auto-hide).</summary>
    public event Action<bool>? MenuOpening;

    public DashboardControl()
    {
        InitializeComponent();
        ErrorBar.IsClosable = true;
        ErrorBar.Closed += () => { if (_app is not null) _app.LastError = null; };
        UpdateBar.IsClosable = true;
        UpdateBar.Closed += () => { if (_app is not null) _app.Updates.Dismissed = true; };
    }

    public void Bind(AppState app)
    {
        _app = app;
        app.PropertyChanged += (_, _) => Dispatcher.BeginInvoke(Refresh);
        app.Settings.Changed += () => Dispatcher.BeginInvoke(Refresh);
        app.Usage.Changed += () => Dispatcher.BeginInvoke(Refresh);
        app.Quota.Changed += () => Dispatcher.BeginInvoke(Refresh);
        app.Updates.Changed += () => Dispatcher.BeginInvoke(Refresh);
        Refresh();
    }

    public void Refresh()
    {
        if (_app is null) return;
        var s = _app.Settings;

        EnabledSwitch.IsChecked = s.Prefs.IsEnabled;
        StatusLabel.Text = _app.StatusText;
        SetPulse(_app.IsWorking);

        // Update banner
        var upd = _app.Updates;
        if (upd.AvailableVersion is { } version && !upd.Dismissed)
        {
            UpdateBar.Set(Severity.Info, $"Inkwell {version} is available", upd.Status ?? $"You have {UpdateChecker.CurrentVersionText}. Updating takes a few seconds.");
            var btn = UI.Button(upd.IsInstalling ? "Updating…" : "Update now", () => _ = upd.InstallAsync(), primary: true);
            btn.IsEnabled = !upd.IsInstalling;
            UpdateBar.SetActions(btn);
            UpdateBar.Visibility = Visibility.Visible;
        }
        else UpdateBar.Visibility = Visibility.Collapsed;

        // Provider card
        var kind = s.Prefs.ActiveProvider;
        ProviderLogoHost.Content = ProviderLogos.Create(kind, 32);
        ProviderTitle.Text = kind.DisplayName();
        ProviderDetail.Text = FormatProviderDetail(kind);
        var limit = kind.UsesApiKeys() ? null : _app.Quota.CliLimitSummary(kind);
        ProviderLimit.Text = limit ?? "";
        ProviderLimit.Visibility = limit is null ? Visibility.Collapsed : Visibility.Visible;
        var tint = ThemeManager.Hex(kind.TintHex());
        ProviderCard.Background = new SolidColorBrush(Color.FromArgb(ThemeManager.IsDark ? (byte)0x1F : (byte)0x14, tint.R, tint.G, tint.B));
        QuickModelButton.Visibility = kind != ProviderKind.Codex && (!kind.UsesApiKeys() || s.HasUsableKey(kind))
            ? Visibility.Visible : Visibility.Collapsed;

        // Error
        if (!string.IsNullOrWhiteSpace(_app.LastError))
        {
            var (title, detail) = Messages.Split(_app.LastError!);
            ErrorBar.Set(Messages.IsWarning(_app.LastError!) ? Severity.Warning : Severity.Error, title, detail);
            if (ErrorBar.Visibility != Visibility.Visible) UI.FadeIn(ErrorBar);
            ErrorBar.Visibility = Visibility.Visible;
        }
        else ErrorBar.Visibility = Visibility.Collapsed;

        // Stats
        var sum = _app.Usage.Summary(_range);
        RunsValue.Text = sum.Runs.ToString();
        TokensValue.Text = sum.TotalTokens.Compact();
        TokensDetail.Text = $"{sum.InputTokens.Compact()} in · {sum.OutputTokens.Compact()} out";
        WordsValue.Text = sum.Words.Compact();
        AiTimeValue.Text = sum.AiTime.ShortDuration();
        AiTimeDetail.Text = sum.Runs > 0 ? $"{sum.AverageLatency.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}s avg" : "";
        AiTimeDetail.Visibility = sum.Runs > 0 ? Visibility.Visible : Visibility.Collapsed;

        RenderChart(sum);
        RenderTopTemplates(sum);
    }

    private void SetPulse(bool on)
    {
        if (on == _pulsing) return;
        _pulsing = on;
        AppIconImage.BeginAnimation(OpacityProperty, on
            ? new DoubleAnimation(1, 0.45, TimeSpan.FromMilliseconds(600)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever }
            : null);
    }

    private string FormatProviderDetail(ProviderKind kind)
    {
        var s = _app!.Settings;
        if (kind.UsesApiKeys())
        {
            int keys = s.Api(kind).Keys.Count(k => k.IsEnabled);
            if (keys == 0) return "No API key — click to add one";
            return $"{s.EffectiveModels(kind).FirstOrDefault()} · {keys} key{(keys == 1 ? "" : "s")}";
        }
        var m = s.Cli(kind).Model.Trim();
        return m.Length == 0 ? "Default model" : m;
    }

    // MARK: Chart

    private void RenderChart(UsageSummary s)
    {
        ChartGrid.Children.Clear();
        ChartGrid.RowDefinitions.Clear();
        EmptyChartOverlay.Visibility = s.Runs == 0 ? Visibility.Visible : Visibility.Collapsed;

        var display = Downsample(s.Daily, 31);
        if (display.Count == 0) return;
        int maxVal = Math.Max(1, display.Max(b => _metricTokens ? b.Tokens : b.Runs));

        ChartGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        ChartGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Y axis: three grid lines with labels on the left, like the Mac chart.
        var plot = new Grid();
        plot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        plot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var lines = new Grid();
        for (int r = 0; r < 3; r++) lines.RowDefinitions.Add(new RowDefinition());
        var labels = new Grid();
        for (int r = 0; r < 3; r++) labels.RowDefinitions.Add(new RowDefinition());
        for (int r = 0; r < 3; r++)
        {
            var line = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Top };
            line.SetResourceReference(Border.BackgroundProperty, "DividerBrush");
            Grid.SetRow(line, r);
            lines.Children.Add(line);
            if (s.Runs == 0) continue;
            var value = (int)Math.Round(maxVal * (3 - r) / 3.0);
            if (r > 0 && value == (int)Math.Round(maxVal * (4 - r) / 3.0)) continue; // no repeated labels for tiny ranges
            var lbl = UI.Text(value.Compact(), 9.5, "TextTertiaryBrush");
            lbl.VerticalAlignment = VerticalAlignment.Top;
            lbl.Margin = new Thickness(0, -6, 0, 0);
            Grid.SetRow(lbl, r);
            labels.Children.Add(lbl);
        }
        var baseline = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Bottom };
        baseline.SetResourceReference(Border.BackgroundProperty, "DividerBrush");
        Grid.SetColumn(lines, 1);
        Grid.SetColumn(baseline, 1);
        plot.Children.Add(labels);
        plot.Children.Add(lines);
        plot.Children.Add(baseline);

        var bars = new Grid();
        Grid.SetColumn(bars, 1);
        var barBrush = new LinearGradientBrush(Color.FromRgb(0xA8, 0x55, 0xF7), Color.FromRgb(0x4F, 0x46, 0xE5), 90);
        for (int i = 0; i < display.Count; i++)
        {
            bars.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var b = display[i];
            int val = _metricTokens ? b.Tokens : b.Runs;
            var bar = new Border
            {
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(display.Count > 14 ? 1 : 3, 0, display.Count > 14 ? 1 : 3, 0),
                CornerRadius = new CornerRadius(3, 3, 0, 0),
                Background = barBrush,
                Height = 0,
                ToolTip = $"{b.Day:ddd, MMM d}: {(_metricTokens ? $"{b.Tokens:N0} tokens" : $"{b.Runs} run{(b.Runs == 1 ? "" : "s")}")}",
                Visibility = val > 0 ? Visibility.Visible : Visibility.Hidden,
            };
            var target = Math.Max(3, 84.0 * val / maxVal);
            if (StaticRendering) bar.Height = target;
            else bar.BeginAnimation(HeightProperty, new DoubleAnimation(0, target, TimeSpan.FromMilliseconds(380))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                BeginTime = TimeSpan.FromMilliseconds(Math.Min(i * 12, 240)),
            });
            Grid.SetColumn(bar, i);
            bars.Children.Add(bar);
        }
        plot.Children.Add(bars);
        ChartGrid.Children.Add(plot);

        // X axis
        var axis = new Grid { Margin = new Thickness(30, 4, 0, 0) };
        int want = _range == StatsRange.Week ? 7 : 4;
        int step = Math.Max(1, (int)Math.Ceiling(display.Count / (double)want));
        for (int i = 0; i < display.Count; i++)
        {
            axis.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (i % step != 0) continue;
            var text = _range == StatsRange.Week ? display[i].Day.ToString("ddd") : display[i].Day.ToString("MMM d");
            var lbl = UI.Text(text, 10, "TextTertiaryBrush");
            lbl.TextTrimming = TextTrimming.None;
            lbl.HorizontalAlignment = HorizontalAlignment.Left;
            Grid.SetColumn(lbl, i);
            Grid.SetColumnSpan(lbl, Math.Min(step, display.Count - i));
            axis.Children.Add(lbl);
        }
        Grid.SetRow(axis, 1);
        ChartGrid.Children.Add(axis);
    }

    private static List<DayBucket> Downsample(List<DayBucket> raw, int maxBars)
    {
        if (raw.Count <= maxBars) return raw;
        int groupSize = (int)Math.Ceiling((double)raw.Count / maxBars);
        var result = new List<DayBucket>();
        for (int i = 0; i < raw.Count; i += groupSize)
        {
            var slice = raw.Skip(i).Take(groupSize).ToList();
            result.Add(new DayBucket(slice[0].Day, slice.Sum(x => x.Runs), slice.Sum(x => x.Tokens)));
        }
        return result;
    }

    private void RenderTopTemplates(UsageSummary s)
    {
        TopTemplatesList.Children.Clear();
        TopTemplatesCard.Visibility = s.TopTemplates.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (s.TopTemplates.Count == 0) return;
        int maxCount = Math.Max(1, s.TopTemplates[0].Count);
        var fill = new LinearGradientBrush(Color.FromRgb(0x4F, 0x46, 0xE5), Color.FromRgb(0xA8, 0x55, 0xF7), 0);

        foreach (var t in s.TopTemplates)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            var name = UI.Text(t.Name, 12);
            name.VerticalAlignment = VerticalAlignment.Center;
            var track = new Grid { Height = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) };
            double ratio = (double)t.Count / maxCount;
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.04, ratio), GridUnitType.Star) });
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0001, 1 - ratio), GridUnitType.Star) });
            track.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = fill });
            var count = UI.Text(t.Count.ToString(), 11.5, "TextSecondaryBrush", mono: true);
            count.HorizontalAlignment = HorizontalAlignment.Right;
            count.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(track, 1);
            Grid.SetColumn(count, 2);
            row.Children.Add(name);
            row.Children.Add(track);
            row.Children.Add(count);
            TopTemplatesList.Children.Add(row);
        }
    }

    // MARK: Events

    private void EnabledSwitch_Click(object sender, RoutedEventArgs e) => _app?.Settings.SetEnabled(EnabledSwitch.IsChecked == true);

    private void ProviderCard_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_app is null || e.OriginalSource is DependencyObject d && IsInside<Button>(d)) return;
        OpenProviderRequested?.Invoke(_app.Settings.Prefs.ActiveProvider);
    }

    private static bool IsInside<T>(DependencyObject d) where T : DependencyObject
    {
        for (var cur = d; cur is not null; cur = VisualTreeHelper.GetParent(cur) ?? LogicalTreeHelper.GetParent(cur))
            if (cur is T) return true;
        return false;
    }

    /// <summary>Quick primary-model switch, as a native menu under the button.</summary>
    private void QuickModelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_app is null) return;
        var s = _app.Settings;
        var kind = s.Prefs.ActiveProvider;
        IReadOnlyList<ModelInfo> options;
        string current;
        if (kind.UsesApiKeys())
        {
            if (!s.HasUsableKey(kind)) return;
            options = ModelCatalog.Models(kind);
            current = s.EffectiveModels(kind).FirstOrDefault() ?? "";
        }
        else if (kind == ProviderKind.ClaudeCode)
        {
            options = ModelCatalog.Claude;
            current = s.Cli(kind).Model;
        }
        else return;

        using var menu = new NativeMenu();
        menu.Header("Primary model");
        foreach (var m in options)
        {
            var id = m.Id;
            menu.Item(m.Name, () => s.SetPrimaryModel(kind, id), isChecked: id == current, radio: true);
        }
        var hwnd = new WindowInteropHelper(Window.GetWindow(this)!).Handle;
        var pt = QuickModelButton.PointToScreen(new Point(0, QuickModelButton.ActualHeight));
        MenuOpening?.Invoke(true);
        try { menu.Show(hwnd, (int)pt.X, (int)pt.Y); }
        finally { MenuOpening?.Invoke(false); }
        e.Handled = true;
    }

    private void Range_Checked(object sender, RoutedEventArgs e)
    {
        _range = sender == RangeMonth ? StatsRange.Month : sender == RangeAll ? StatsRange.All : StatsRange.Week;
        if (_app is not null) Refresh();
    }

    private void Metric_Checked(object sender, RoutedEventArgs e)
    {
        _metricTokens = sender != MetricRuns;
        if (_app is not null) Refresh();
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettingsRequested?.Invoke();
    private void Quit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
}
