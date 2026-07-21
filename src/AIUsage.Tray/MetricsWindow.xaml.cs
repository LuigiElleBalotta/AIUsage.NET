using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AIUsage.Core.App;
using AIUsage.Core.Models;
using AIUsage.Core.Support;
using AIUsage.Tray.Theme;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace AIUsage.Tray;

/// <summary>
/// The dashboard's host window: a rounded, borderless, topmost WPF window with a small custom theme
/// (dark palette, flat buttons/scrollbar, provider brand badges, real progress bars). Still a long way
/// from the original SwiftUI DashboardView (no drag-reorder, no charts, no model-breakdown popovers —
/// see PORTING_NOTES.md), but visually and structurally much closer to it than a plain ItemsControl of
/// text rows.
/// </summary>
public partial class MetricsWindow : Window
{
    private readonly AppContainer _container;

    public MetricsWindow(AppContainer container)
    {
        _container = container;
        InitializeComponent();
        VersionText.Text = $"v{AppVersion.Display()}";
    }

    public void Refresh()
    {
        ProviderList.Children.Clear();

        var visible = _container.Layout.VisiblePlaced;
        if (visible.Count == 0)
        {
            ProviderList.Children.Add(new TextBlock
            {
                Text = "No metrics enabled yet.",
                Foreground = Brushes.Gray,
                Margin = new Thickness(4, 16, 4, 16),
                HorizontalAlignment = HorizontalAlignment.Center
            });
            UpdateFooterStatus();
            return;
        }

        string? lastProviderId = null;
        StackPanel? currentCardBody = null;

        foreach (var widget in visible)
        {
            var descriptor = _container.Layout.DescriptorFor(widget);
            if (descriptor is null) continue;

            if (descriptor.ProviderId != lastProviderId)
            {
                lastProviderId = descriptor.ProviderId;
                var provider = _container.Registry.Provider(descriptor.ProviderId);
                var snapshot = _container.DataStore.Snapshots.GetValueOrDefault(descriptor.ProviderId);
                var (cardRoot, cardBody) = BuildProviderCard(provider?.DisplayName ?? descriptor.ProviderId, descriptor.ProviderId, snapshot?.Plan);
                ProviderList.Children.Add(cardRoot);
                currentCardBody = cardBody;
            }

            var data = _container.DataStore.Data(descriptor);
            currentCardBody?.Children.Add(BuildRow(data));
        }

        UpdateFooterStatus();
    }

    private void UpdateFooterStatus()
    {
        var last = _container.DataStore.LastRefreshAt;
        FooterStatusText.Text = last is { } t ? $"Updated {RelativeTime(t)}" : "Not refreshed yet";
    }

    private static string RelativeTime(DateTimeOffset at)
    {
        var elapsed = DateTimeOffset.UtcNow - at;
        if (elapsed.TotalSeconds < 30) return "just now";
        if (elapsed.TotalMinutes < 1) return $"{(int)elapsed.TotalSeconds}s ago";
        if (elapsed.TotalHours < 1) return $"{(int)elapsed.TotalMinutes}m ago";
        return $"{(int)elapsed.TotalHours}h ago";
    }

    /// <summary>Builds one provider card (icon badge + title + optional plan pill), returning both the
    /// root element to add to the page and the inner body panel that rows should be appended to.</summary>
    private static (UIElement Root, StackPanel Body) BuildProviderCard(string displayName, string providerId, string? plan)
    {
        var container = new Border
        {
            Background = (Brush)Application.Current.Resources["CardBrush"],
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 12, 14, 10),
            Margin = new Thickness(0, 0, 0, 10)
        };

        var inner = new StackPanel();

        var header = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var badge = ProviderIconCatalog.CreateBadge(providerId, 26);
        Grid.SetColumn(badge, 0);

        var title = new TextBlock
        {
            Text = displayName,
            FontWeight = FontWeights.Bold,
            FontSize = 14.5,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };
        Grid.SetColumn(title, 1);

        header.Children.Add(badge);
        header.Children.Add(title);

        if (!string.IsNullOrWhiteSpace(plan))
        {
            var pill = new Border
            {
                Background = (Brush)Application.Current.Resources["PillBrush"],
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 2, 8, 2),
                VerticalAlignment = VerticalAlignment.Center
            };
            pill.Child = new TextBlock
            {
                Text = plan,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"]
            };
            Grid.SetColumn(pill, 2);
            header.Children.Add(pill);
        }

        inner.Children.Add(header);
        container.Child = inner;
        return (container, inner);
    }

    private static UIElement BuildRow(WidgetData data)
    {
        var row = new StackPanel { Margin = new Thickness(0, 5, 0, 5) };

        var headerLine = new Grid();
        headerLine.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            Text = data.Title,
            Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"],
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(title, 0);

        var headline = new TextBlock
        {
            Text = data.Headline,
            Foreground = SeverityBrush(data),
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };
        Grid.SetColumn(headline, 1);

        headerLine.Children.Add(title);
        headerLine.Children.Add(headline);
        if (data.HasModelBreakdown) headerLine.ToolTip = BuildModelBreakdownTooltip(data);
        row.Children.Add(headerLine);

        if (data.IsChart)
        {
            row.Children.Add(BuildChart(data));
            return row;
        }

        if (data.IsBounded)
        {
            row.Children.Add(BuildProgressBar(data));
        }

        var subtitle = data.IsBounded ? data.BoundedSubtitle : data.UnboundedDetail;
        if (!string.IsNullOrEmpty(subtitle) && !(data.IsBounded && subtitle == data.Headline))
        {
            row.Children.Add(new TextBlock
            {
                Text = subtitle,
                Foreground = (Brush)Application.Current.Resources["TextTertiaryBrush"],
                FontSize = 11,
                Margin = new Thickness(0, 3, 0, 0),
                HorizontalAlignment = data.IsBounded ? System.Windows.HorizontalAlignment.Left : System.Windows.HorizontalAlignment.Right
            });
        }

        return row;
    }

    /// <summary>Renders MetricLine.Chart ("Usage Trend") as a small bar chart: one thin bar per day,
    /// height proportional to that day's value against the series max, with a hover tooltip showing
    /// the pre-formatted readout and axis label. No equivalent yet of the original's smooth area
    /// chart with axis gridlines — this is a lightweight bar-chart approximation.</summary>
    private static UIElement BuildChart(WidgetData data)
    {
        const double chartHeight = 44;
        var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };

        if (data.ChartPoints.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "No usage data yet",
                Foreground = (Brush)Application.Current.Resources["TextTertiaryBrush"],
                FontSize = 11
            });
            return panel;
        }

        var maxValue = Math.Max(1, data.ChartPoints.Max(p => p.Value));
        var barsHost = new Grid { Height = chartHeight };
        var bars = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };

        foreach (var point in data.ChartPoints)
        {
            var fraction = Math.Clamp(point.Value / maxValue, 0, 1);
            var bar = new Rectangle
            {
                Width = 5,
                Height = Math.Max(2, fraction * chartHeight),
                RadiusX = 1.5,
                RadiusY = 1.5,
                Fill = (Brush)Application.Current.Resources["AccentBrush"],
                Margin = new Thickness(1, 0, 1, 0),
                VerticalAlignment = VerticalAlignment.Bottom,
                ToolTip = $"{point.Label}: {point.Readout}"
            };
            bars.Children.Add(bar);
        }

        barsHost.Children.Add(bars);
        panel.Children.Add(barsHost);

        if (!string.IsNullOrEmpty(data.ChartNote))
        {
            panel.Children.Add(new TextBlock
            {
                Text = data.ChartNote,
                Foreground = (Brush)Application.Current.Resources["TextTertiaryBrush"],
                FontSize = 10.5,
                Margin = new Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });
        }

        return panel;
    }

    private static UIElement BuildProgressBar(WidgetData data)
    {
        const double barHeight = 6;
        var fraction = Math.Clamp(data.DisplayMode == WidgetDisplayMode.Remaining ? data.RemainingFraction : data.Fraction, 0, 1);

        var track = new Grid { Height = barHeight, Margin = new Thickness(0, 6, 0, 0) };
        track.Children.Add(new Rectangle
        {
            Height = barHeight,
            RadiusX = barHeight / 2,
            RadiusY = barHeight / 2,
            Fill = (Brush)Application.Current.Resources["TrackBrush"],
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch
        });

        var fillBrush = SeverityBrush(data);
        var fill = new Rectangle
        {
            Height = barHeight,
            RadiusX = barHeight / 2,
            RadiusY = barHeight / 2,
            Fill = fillBrush,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left
        };
        // Bind width to the parent's ActualWidth * fraction via a SizeChanged handler, since WPF
        // Grid columns don't support fractional-of-sibling-width without a converter.
        track.SizeChanged += (_, e) => fill.Width = Math.Max(0, e.NewSize.Width * fraction);
        track.Children.Add(fill);

        return track;
    }

    /// <summary>Builds a hover tooltip listing per-model token/cost breakdown for a period-scoped
    /// spend row (Today / Yesterday / Last 30 Days). No equivalent yet of the original's rich
    /// ModelUsageDetail popover view (per-model rows with icons, sorting affordances) — this is a
    /// plain-text WPF ToolTip showing the same underlying data (ModelUsageBreakdown), attached
    /// directly to the row's header line whenever HasModelBreakdown is true.</summary>
    private static object BuildModelBreakdownTooltip(WidgetData data)
    {
        var breakdown = data.ModelBreakdown!;
        var panel = new StackPanel { MaxWidth = 260 };
        panel.Children.Add(new TextBlock
        {
            Text = "Model breakdown",
            FontWeight = FontWeights.Bold,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 4)
        });

        foreach (var model in breakdown.Models)
        {
            var line = new TextBlock { FontSize = 11.5, Margin = new Thickness(0, 1, 0, 1) };
            var costText = model.CostUSD is { } cost ? $" — {Formatters.Currency(cost, 2)}" : "";
            line.Text = $"{model.Model}: {model.TotalTokens:N0} tokens{costText}";
            panel.Children.Add(line);
        }

        if (breakdown.TotalCostUSD is { } totalCost)
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"Total: {breakdown.TotalTokens:N0} tokens — {Formatters.Currency(totalCost, 2)}",
                FontWeight = FontWeights.SemiBold,
                FontSize = 11.5,
                Margin = new Thickness(0, 4, 0, 0)
            });
        }

        panel.Children.Add(new TextBlock
        {
            Text = breakdown.SourceNote,
            Foreground = Brushes.Gray,
            FontSize = 10.5,
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });

        return panel;
    }

    private static Brush SeverityBrush(WidgetData data)
    {
        if (!data.HasData) return (Brush)Application.Current.Resources["TextTertiaryBrush"];
        var severity = data.GetMeterState().Severity;
        return severity switch
        {
            WidgetData.MeterSeverity.Critical => (Brush)Application.Current.Resources["CriticalBrush"],
            WidgetData.MeterSeverity.Warning => (Brush)Application.Current.Resources["WarningBrush"],
            WidgetData.MeterSeverity.Normal => (Brush)Application.Current.Resources["NormalBrush"],
            _ => (Brush)Application.Current.Resources["TextPrimaryBrush"]
        };
    }

    public void PositionNearTray()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 12;
        Top = workArea.Bottom - Height - 12;
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await _container.RefreshAllNowAsync().ConfigureAwait(true);
        Refresh();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var settingsWindow = new SettingsWindow(_container) { Owner = this };
        settingsWindow.Closed += (_, _) => Refresh();
        settingsWindow.ShowDialog();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Hide();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();

    private void Window_Deactivated(object? sender, EventArgs e) => Hide();
}
