using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AIUsage.Core.App;
using AIUsage.Core.Models;
using Brushes = System.Windows.Media.Brushes;
using Point = System.Windows.Point;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using DragDropEffects = System.Windows.DragDropEffects;
using DataObject = System.Windows.DataObject;

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
                var snapshot = _container.DataStore.Snapshots.GetValueOrDefault(descriptor.ProviderId);
                var (cardRoot, cardBody) = MetricsCardView.BuildProviderCard(
                    _container.DisplayName(descriptor.ProviderId),
                    descriptor.ProviderId,
                    snapshot?.Plan);
                AttachProviderDragReorder(cardRoot, descriptor.ProviderId);
                AttachProviderHeaderContextMenu(cardRoot, descriptor.ProviderId);
                foreach (var badge in _container.DataStore.StatusBadges(descriptor.ProviderId))
                {
                    cardBody.Children.Add(MetricsCardView.BuildRow(badge));
                }
                ProviderList.Children.Add(cardRoot);
                currentCardBody = cardBody;
            }

            var data = _container.DataStore.Data(descriptor);
            var isPinned = _container.Layout.IsPinned(descriptor.Id);
            var row = MetricsCardView.BuildRow(data, isPinned);
            AttachMetricDragReorder(row, descriptor.ProviderId, descriptor.Id);
            AttachRowContextMenu(row, descriptor);
            currentCardBody?.Children.Add(row);
        }

        UpdateFooterStatus();
    }

    // MARK: - Drag-reorder (WPF native drag/drop, direct behavioral port of the Swift dashboard's
    // drag-to-reorder: drag a provider's whole header to reorder providers, drag a metric row to
    // reorder it within its provider. No lifted-preview ghost or spring animation like the SwiftUI
    // original — WPF's DragDrop gives a plain drag cursor instead — but the underlying reorder model
    // (LayoutStore.ReorderProvider/ReorderMetric) and drop-target behavior are the same.

    private const string ProviderDragFormat = "AIUsage.ProviderDrag";
    private const string MetricDragFormat = "AIUsage.MetricDrag";
    private Point? _dragStart;

    private void AttachProviderDragReorder(UIElement cardRoot, string providerId)
    {
        if (cardRoot is not Border card) return;
        card.AllowDrop = true;

        // The header (first child of the card's inner StackPanel) is the drag source, matching the
        // Swift dashboard where dragging a provider's header line reorders whole providers.
        if (card.Child is StackPanel { Children: [Grid header, ..] })
        {
            header.PreviewMouseLeftButtonDown += (_, e) => _dragStart = e.GetPosition(null);
            header.PreviewMouseMove += (s, e) => TryStartDrag(s, e, header, ProviderDragFormat, providerId);
        }

        card.DragOver += (_, e) => e.Effects = e.Data.GetDataPresent(ProviderDragFormat) ? DragDropEffects.Move : DragDropEffects.None;
        card.Drop += (_, e) =>
        {
            if (e.Data.GetData(ProviderDragFormat) is not string draggedId || draggedId == providerId) return;
            if (_container.Layout.ReorderProvider(draggedId, providerId)) Refresh();
        };
    }

    private void AttachMetricDragReorder(UIElement row, string providerId, string descriptorId)
    {
        if (row is not FrameworkElement element) return;
        element.AllowDrop = true;

        element.PreviewMouseLeftButtonDown += (_, e) => _dragStart = e.GetPosition(null);
        element.PreviewMouseMove += (s, e) => TryStartDrag(s, e, element, MetricDragFormat, $"{providerId}\u0001{descriptorId}");

        element.DragOver += (_, e) => e.Effects = e.Data.GetDataPresent(MetricDragFormat) ? DragDropEffects.Move : DragDropEffects.None;
        element.Drop += (_, e) =>
        {
            if (e.Data.GetData(MetricDragFormat) is not string payload) return;
            var parts = payload.Split('\u0001');
            if (parts.Length != 2) return;
            var (draggedProviderId, draggedDescriptorId) = (parts[0], parts[1]);
            // Cross-provider drops are ignored: reordering only makes sense within one provider's card.
            if (draggedProviderId != providerId || draggedDescriptorId == descriptorId) return;
            if (_container.Layout.ReorderMetric(draggedDescriptorId, descriptorId, providerId)) Refresh();
        };
    }

    private void TryStartDrag(object sender, MouseEventArgs e, UIElement source, string format, string payload)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragStart is not { } start) return;
        var current = e.GetPosition(null);
        if (Math.Abs(current.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _dragStart = null;
        var data = new DataObject(format, payload);
        DragDrop.DoDragDrop(source, data, DragDropEffects.Move);
    }

    /// <summary>Right-click menu on a provider's card header: "Rename..." for extra-account cards
    /// only (e.g. a second "claude@ab12cd34" card) — the plain WPF counterpart of the Swift
    /// dashboard's card-header Rename item. Non-account providers (Codex, Cursor, ...) get no menu at
    /// all since they have nothing to rename.</summary>
    private void AttachProviderHeaderContextMenu(UIElement cardRoot, string providerId)
    {
        if (!Core.Stores.ProviderAccountID.IsAccountCard(providerId)) return;
        if (cardRoot is not Border card) return;

        var menu = new ContextMenu();
        var rename = new MenuItem { Header = "Rename..." };
        rename.Click += (_, _) =>
        {
            var current = _container.DisplayName(providerId);
            var dialog = new RenameDialog(current) { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                _container.RenameProvider(providerId, dialog.ResultText);
                Refresh();
            }
        };
        menu.Items.Add(rename);
        card.ContextMenu = menu;
    }

    /// <summary>Right-click menu for a metric row: Hide (SetMetricEnabled false), Star/Unstar for the
    /// menu bar (LayoutStore pin model, capped per provider), and Refresh this provider — the same
    /// three actions the Swift dashboard's row context menu offers (minus Customize, which has no
    /// WPF surface yet).</summary>
    private void AttachRowContextMenu(UIElement row, WidgetDescriptor descriptor)
    {
        if (row is not FrameworkElement element) return;
        var menu = new ContextMenu();

        var hide = new MenuItem { Header = "Hide" };
        hide.Click += (_, _) =>
        {
            _container.Layout.SetMetricEnabled(descriptor.Id, false);
            Refresh();
        };
        menu.Items.Add(hide);

        if (descriptor.Pinnable)
        {
            var isPinned = _container.Layout.IsPinned(descriptor.Id);
            var pin = new MenuItem { Header = isPinned ? "Unstar" : "Star for menu bar" };
            pin.Click += (_, _) =>
            {
                if (isPinned)
                {
                    _container.Layout.SetPinned(false, descriptor.Id);
                }
                else if (_container.Layout.CanPin(descriptor.Id))
                {
                    _container.Layout.SetPinned(true, descriptor.Id);
                }
                Refresh();
            };
            menu.Items.Add(pin);
        }

        menu.Items.Add(new Separator());

        var refreshItem = new MenuItem { Header = $"Refresh {_container.DisplayName(descriptor.ProviderId)}" };
        refreshItem.Click += async (_, _) =>
        {
            await _container.RefreshNowAsync(descriptor.ProviderId).ConfigureAwait(true);
            Refresh();
        };
        menu.Items.Add(refreshItem);

        element.ContextMenu = menu;
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
