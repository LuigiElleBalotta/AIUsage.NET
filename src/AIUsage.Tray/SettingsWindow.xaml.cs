using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AIUsage.Core.App;
using AIUsage.Core.Stores;
using AIUsage.Tray.Theme;
using Brush = System.Windows.Media.Brush;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace AIUsage.Tray;

/// <summary>
/// Minimal Settings window: lets the user turn providers on/off through
/// <see cref="AIUsage.Core.Stores.ProviderEnablementStore"/>. There is no equivalent yet of the
/// original's full Customize screen (metric-level toggles, drag-reorder, pins, per-provider Reset) —
/// see PORTING_NOTES.md and docs/dashboard.md for what's intentionally left out for now.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppContainer _container;

    public SettingsWindow(AppContainer container)
    {
        _container = container;
        InitializeComponent();
        VersionText.Text = $"AIUsage.NET v{AppVersion.Display()}";
        BuildProviderToggles();
        BuildMetricToggles();
    }

    private void BuildProviderToggles()
    {
        ProviderTogglesList.Children.Clear();
        var providers = _container.Registry.Providers;
        for (var i = 0; i < providers.Count; i++)
        {
            var provider = providers[i];
            var row = BuildProviderRow(provider.Id, _container.DisplayName(provider.Id));
            ProviderTogglesList.Children.Add(row);
            if (i < providers.Count - 1)
            {
                ProviderTogglesList.Children.Add(new Border
                {
                    Height = 1,
                    Background = (Brush)Application.Current.Resources["DividerBrush"],
                    Margin = new Thickness(10, 0, 10, 0)
                });
            }
        }
    }

    private UIElement BuildProviderRow(string providerId, string displayName)
    {
        var grid = new Grid { Margin = new Thickness(10, 9, 10, 9) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var badge = ProviderIconCatalog.CreateBadge(providerId, 24);
        Grid.SetColumn(badge, 0);

        var title = new TextBlock
        {
            Text = displayName,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };
        Grid.SetColumn(title, 1);

        var toggle = new CheckBox
        {
            Style = (Style)Application.Current.Resources["ToggleSwitchStyle"],
            IsChecked = _container.Enablement.IsEnabled(providerId),
            VerticalAlignment = VerticalAlignment.Center
        };
        toggle.Checked += (_, _) => _container.Enablement.SetEnabled(true, providerId);
        toggle.Unchecked += (_, _) => _container.Enablement.SetEnabled(false, providerId);
        Grid.SetColumn(toggle, 2);

        grid.Children.Add(badge);
        grid.Children.Add(title);
        grid.Children.Add(toggle);
        return grid;
    }

    /// <summary>Per-metric Customize list: one toggle per WidgetDescriptor, grouped under a small
    /// provider header, backed directly by LayoutStore.SetMetricEnabled/IsPinned's underlying
    /// membership test (whether the descriptor is currently in Placed). Unlike the provider on/off
    /// toggles above (which gate whether a provider refreshes at all), this only controls whether an
    /// individual metric tile is shown on the dashboard for providers that are enabled.</summary>
    private void BuildMetricToggles()
    {
        MetricTogglesList.Children.Clear();
        var providers = _container.Registry.Providers;
        var placedIds = new HashSet<string>(_container.Layout.Placed.Select(w => w.DescriptorId));

        for (var i = 0; i < providers.Count; i++)
        {
            var provider = providers[i];
            var descriptors = _container.Registry.DescriptorsFor(provider.Id);
            if (descriptors.Count == 0) continue;

            MetricTogglesList.Children.Add(new TextBlock
            {
                Text = _container.DisplayName(provider.Id),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"],
                Margin = new Thickness(10, i == 0 ? 8 : 14, 10, 4)
            });

            foreach (var descriptor in descriptors)
            {
                MetricTogglesList.Children.Add(BuildMetricRow(descriptor.Id, descriptor.Title, placedIds.Contains(descriptor.Id)));
            }
        }
    }

    private UIElement BuildMetricRow(string descriptorId, string title, bool isEnabled)
    {
        var grid = new Grid { Margin = new Thickness(10, 6, 10, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
        {
            Text = title,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(label, 0);

        var toggle = new CheckBox
        {
            Style = (Style)Application.Current.Resources["ToggleSwitchStyle"],
            IsChecked = isEnabled,
            VerticalAlignment = VerticalAlignment.Center
        };
        toggle.Checked += (_, _) => _container.Layout.SetMetricEnabled(descriptorId, true);
        toggle.Unchecked += (_, _) => _container.Layout.SetMetricEnabled(descriptorId, false);
        Grid.SetColumn(toggle, 1);

        grid.Children.Add(label);
        grid.Children.Add(toggle);
        return grid;
    }

    // MARK: - Usage history backup (local export/import — the Windows replacement for the Swift
    // edition's iCloud sync, which only ever moved per-provider usage history; see PORTING_NOTES.md).

    private void ExportHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export Usage History",
            Filter = "AIUsage history backup (*.json)|*.json",
            FileName = $"aiusage-history-{DateTime.Now:yyyy-MM-dd}.json"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            UsageHistoryBackupStore.Export(_container.DataStore.Cache, dialog.FileName);
            ShowBackupStatus($"Exported to {dialog.FileName}");
        }
        catch (UsageHistoryBackupException ex)
        {
            ShowBackupStatus(ex.Message, isError: true);
        }
        catch (Exception ex)
        {
            ShowBackupStatus($"Export failed: {ex.Message}", isError: true);
        }
    }

    private void ImportHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import Usage History",
            Filter = "AIUsage history backup (*.json)|*.json"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var mergedCount = UsageHistoryBackupStore.Import(_container.DataStore.Cache, dialog.FileName);
            ShowBackupStatus(mergedCount == 0
                ? "No providers to merge into — refresh those providers here first, then import again."
                : $"Imported history for {mergedCount} provider(s).");
        }
        catch (UsageHistoryBackupException ex)
        {
            ShowBackupStatus(ex.Message, isError: true);
        }
        catch (Exception ex)
        {
            ShowBackupStatus($"Import failed: {ex.Message}", isError: true);
        }
    }

    private void ShowBackupStatus(string message, bool isError = false)
    {
        BackupStatusText.Text = message;
        BackupStatusText.Foreground = isError
            ? (Brush)Application.Current.Resources["CriticalBrush"]
            : (Brush)Application.Current.Resources["TextSecondaryBrush"];
        BackupStatusText.Visibility = Visibility.Visible;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
