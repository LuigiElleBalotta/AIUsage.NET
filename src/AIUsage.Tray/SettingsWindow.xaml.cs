using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AIUsage.Core.App;
using AIUsage.Tray.Theme;
using Brush = System.Windows.Media.Brush;

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
        VersionText.Text = $"AIUsage.NET {VersionString()}";
        BuildProviderToggles();
    }

    private static string VersionString()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version is null ? "(dev build)" : version.ToString(3);
    }

    private void BuildProviderToggles()
    {
        ProviderTogglesList.Children.Clear();
        var providers = _container.Registry.Providers;
        for (var i = 0; i < providers.Count; i++)
        {
            var provider = providers[i];
            var row = BuildProviderRow(provider.Id, provider.DisplayName);
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

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
