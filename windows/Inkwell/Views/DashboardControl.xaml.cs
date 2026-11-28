using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Inkwell.Core;
using Inkwell.Providers;

namespace Inkwell.Views;

public partial class DashboardControl : UserControl
{
    private AppState? _app;
    private StatsRange _range = StatsRange.Week;
    private bool _metricTokens = true;

    public event Action<ProviderKind>? OpenProviderRequested;
    public event Action? OpenSettingsRequested;
    public event Action? ContextMenuOpened;
    public event Action? ContextMenuClosed;

    public DashboardControl()
    {
        InitializeComponent();
    }

    public void Bind(AppState app)
    {
        _app = app;
        app.PropertyChanged += (_, _) => Dispatcher.BeginInvoke(Refresh);
        app.Settings.Changed += () => Dispatcher.BeginInvoke(Refresh);
        app.Usage.Changed += () => Dispatcher.BeginInvoke(Refresh);
        Refresh();
    }

    public void Refresh()
    {
        if (_app is null) return;

        EnabledSwitch.IsChecked = _app.Settings.Prefs.IsEnabled;
        StatusLabel.Text = _app.StatusText;
        HeaderGlyph.Text = _app.IsWorking ? "\uE734" : "\uE70F";

        var kind = _app.Settings.Prefs.ActiveProvider;
        ProviderLogoHost.Content = ProviderLogos.Create(kind, 28);
        ProviderTitle.Text = kind.DisplayName();
        ProviderDetail.Text = FormatProviderDetail(kind);
        ProviderCard.BorderBrush = new SolidColorBrush(Color.FromArgb(75,
            ThemeManager.ParseHex(kind.TintHex()).R,
            ThemeManager.ParseHex(kind.TintHex()).G,
            ThemeManager.ParseHex(kind.TintHex()).B));

        bool hasModelMenu = kind != ProviderKind.Codex &&
            (!kind.UsesApiKeys() || _app.Settings.HasUsableKey(kind));
        QuickModelButton.Visibility = hasModelMenu ? Visibility.Visible : Visibility.Collapsed;

        if (!string.IsNullOrWhiteSpace(_app.LastError))
        {
            ErrorText.Text = _app.LastError;
            ErrorBanner.Visibility = Visibility.Visible;
        }
        else
        {
            ErrorBanner.Visibility = Visibility.Collapsed;
        }

        UpdateSegmentedButtons();

        var summary = _app.Usage.Summary(_range);
        RunsValue.Text = summary.Runs.ToString();
        TokensValue.Text = summary.TotalTokens.Compact();
        TokensDetail.Text = $"{summary.InputTokens.Compact()} in · {summary.OutputTokens.Compact()} out";
        WordsValue.Text = summary.Words.Compact();
        AiTimeValue.Text = summary.AiTime.ShortDuration();
        AiTimeDetail.Text = summary.Runs > 0 ? $"{summary.AverageLatency:0.0}s avg" : "";
        AiTimeDetail.Visibility = summary.Runs > 0 ? Visibility.Visible : Visibility.Collapsed;

        RenderChart(summary);
        RenderTopTemplates(summary);
    }

    private string FormatProviderDetail(ProviderKind kind)
    {
        if (_app is null) return "";
        if (kind.UsesApiKeys())
        {
            int keys = _app.Settings.Api(kind).Keys.Count(k => k.IsEnabled);
            if (keys == 0) return "No API key — click to add one";
            var primary = _app.Settings.EffectiveModels(kind).FirstOrDefault() ?? "";
            return $"{primary} · {keys} key{(keys == 1 ? "" : "s")}";
        }
        var m = _app.Settings.Cli(kind).Model.Trim();
        return string.IsNullOrEmpty(m) ? "Default model" : m;
    }

    private void UpdateSegmentedButtons()
    {
        StyleSeg(RangeWeekBtn, _range == StatsRange.Week);
        StyleSeg(RangeMonthBtn, _range == StatsRange.Month);
        StyleSeg(RangeAllBtn, _range == StatsRange.All);
        StyleSeg(MetricTokensBtn, _metricTokens);
        StyleSeg(MetricRunsBtn, !_metricTokens);
    }

    private void StyleSeg(Button btn, bool active)
    {
        btn.BorderThickness = new Thickness(0);
        btn.Cursor = System.Windows.Input.Cursors.Hand;
        btn.FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        btn.FontSize = btn == MetricTokensBtn || btn == MetricRunsBtn ? 11 : 12;
        btn.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        btn.Padding = btn == MetricTokensBtn || btn == MetricRunsBtn ? new Thickness(8, 3, 8, 3) : new Thickness(6, 4, 6, 4);
        btn.SetResourceReference(Control.ForegroundProperty, active ? "TextPrimaryBrush" : "TextSecondaryBrush");

        var tpl = new ControlTemplate(typeof(Button));
        var bd = new FrameworkElementFactory(typeof(Border));
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        bd.SetValue(Border.PaddingProperty, btn.Padding);
        if (active) bd.SetResourceReference(Border.BackgroundProperty, "CardHoverBrush");
        else bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);

        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        bd.AppendChild(cp);
        tpl.VisualTree = bd;
        btn.Template = tpl;
    }

    private void RenderChart(UsageSummary s)
    {
        ChartGrid.Children.Clear();
        ChartGrid.RowDefinitions.Clear();
        ChartGrid.ColumnDefinitions.Clear();

        if (s.Runs == 0)
        {
            EmptyChartOverlay.Visibility = Visibility.Visible;
            return;
        }
        EmptyChartOverlay.Visibility = Visibility.Collapsed;

        var buckets = s.Daily;
        if (buckets.Count == 0) return;

        // If All-Time has many days, group into up to 14 buckets so bars stay crisp.
        var display = Downsample(buckets, 18);
        int maxVal = Math.Max(1, display.Max(b => _metricTokens ? b.Tokens : b.Runs));

        ChartGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        ChartGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var barsPanel = new Grid();
        for (int i = 0; i < display.Count; i++)
        {
            barsPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        // Subtle mid & top horizontal gridlines
        var linesGrid = new Grid();
        linesGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        linesGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        for (int r = 0; r < 2; r++)
        {
            var line = new Border
            {
                Height = 1,
                VerticalAlignment = VerticalAlignment.Top,
                Opacity = 0.65
            };
            line.SetResourceReference(Border.BackgroundProperty, "DividerBrush");
            Grid.SetRow(line, r);
            linesGrid.Children.Add(line);
        }
        ChartGrid.Children.Add(linesGrid);

        var barBrush = new LinearGradientBrush(
            Color.FromRgb(168, 85, 247),
            Color.FromRgb(79, 70, 229),
            new Point(0, 0), new Point(0, 1));

        for (int i = 0; i < display.Count; i++)
        {
            var b = display[i];
            int val = _metricTokens ? b.Tokens : b.Runs;
            double ratio = (double)val / maxVal;

            var col = new Grid { Margin = new Thickness(1.5, 4, 1.5, 0) };
            var barHeight = Math.Max(val > 0 ? 5 : 2, ratio * 74);
            var bar = new Border
            {
                Height = barHeight,
                VerticalAlignment = VerticalAlignment.Bottom,
                CornerRadius = new CornerRadius(3, 3, 1, 1),
                Background = val > 0 ? barBrush : new SolidColorBrush(Color.FromArgb(30, 128, 128, 128)),
                ToolTip = $"{b.Day:MMM d}: {(_metricTokens ? $"{b.Tokens:N0} tokens" : $"{b.Runs} runs")}"
            };
            col.Children.Add(bar);
            Grid.SetColumn(col, i);
            barsPanel.Children.Add(col);
        }
        ChartGrid.Children.Add(barsPanel);

        // X-axis labels
        var axis = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        int step = Math.Max(1, display.Count / 4);
        for (int i = 0; i < display.Count; i++)
        {
            axis.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (i == 0 || i == display.Count - 1 || (display.Count <= 7) || (i % step == 0 && display.Count - 1 - i >= step / 2))
            {
                var lbl = new TextBlock
                {
                    Text = _range == StatsRange.Week ? display[i].Day.ToString("ddd") : display[i].Day.ToString("MMM d"),
                    FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                    FontSize = 10,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                lbl.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");
                Grid.SetColumn(lbl, i);
                axis.Children.Add(lbl);
            }
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
        if (s.TopTemplates.Count == 0)
        {
            TopTemplatesCard.Visibility = Visibility.Collapsed;
            return;
        }
        TopTemplatesCard.Visibility = Visibility.Visible;
        int maxCount = Math.Max(1, s.TopTemplates[0].Count);

        var fill = new LinearGradientBrush(
            Color.FromRgb(79, 70, 229),
            Color.FromRgb(168, 85, 247),
            new Point(0, 0), new Point(1, 0));

        foreach (var t in s.TopTemplates)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 5) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(98) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });

            var name = new TextBlock
            {
                Text = t.Name,
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                FontSize = 11.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            name.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");

            var track = new Grid { Height = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) };
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.08, (double)t.Count / maxCount), GridUnitType.Star) });
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.001, 1.0 - (double)t.Count / maxCount), GridUnitType.Star) });
            var pill = new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = fill };
            track.Children.Add(pill);

            var count = new TextBlock
            {
                Text = t.Count.ToString(),
                FontFamily = new FontFamily("Consolas, Segoe UI"),
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            count.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");

            Grid.SetColumn(name, 0);
            Grid.SetColumn(track, 1);
            Grid.SetColumn(count, 2);
            row.Children.Add(name);
            row.Children.Add(track);
            row.Children.Add(count);
            TopTemplatesList.Children.Add(row);
        }
    }

    private void EnabledSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (_app is null) return;
        _app.Settings.SetEnabled(EnabledSwitch.IsChecked == true);
    }

    private void ProviderCard_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_app is null) return;
        OpenProviderRequested?.Invoke(_app.Settings.Prefs.ActiveProvider);
    }

    private void QuickModelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_app is null) return;
        var kind = _app.Settings.Prefs.ActiveProvider;
        IReadOnlyList<ModelInfo> options;
        string current;

        if (kind is ProviderKind.Gemini or ProviderKind.Groq)
        {
            if (!_app.Settings.HasUsableKey(kind)) return;
            options = ModelCatalog.Models(kind);
            current = _app.Settings.EffectiveModels(kind).FirstOrDefault() ?? "";
        }
        else if (kind == ProviderKind.ClaudeCode)
        {
            options = ModelCatalog.Claude;
            current = _app.Settings.Cli(kind).Model;
        }
        else return;

        var menu = new ContextMenu();
        var header = new MenuItem { Header = "Primary model", IsEnabled = false };
        menu.Items.Add(header);
        menu.Items.Add(new Separator());
        foreach (var m in options)
        {
            var mi = new MenuItem { Header = m.Name, IsChecked = m.Id == current };
            mi.Click += (_, _) => _app.Settings.SetPrimaryModel(kind, m.Id);
            menu.Items.Add(mi);
        }

        menu.Opened += (_, _) => ContextMenuOpened?.Invoke();
        menu.Closed += (_, _) => ContextMenuClosed?.Invoke();
        menu.PlacementTarget = QuickModelButton;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void DismissError_Click(object sender, RoutedEventArgs e)
    {
        if (_app is not null) _app.LastError = null;
    }

    private void RangeWeek_Click(object sender, RoutedEventArgs e) { _range = StatsRange.Week; Refresh(); }
    private void RangeMonth_Click(object sender, RoutedEventArgs e) { _range = StatsRange.Month; Refresh(); }
    private void RangeAll_Click(object sender, RoutedEventArgs e) { _range = StatsRange.All; Refresh(); }
    private void MetricTokens_Click(object sender, RoutedEventArgs e) { _metricTokens = true; Refresh(); }
    private void MetricRuns_Click(object sender, RoutedEventArgs e) { _metricTokens = false; Refresh(); }
    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettingsRequested?.Invoke();
    private void Quit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
}
