using System.Windows;

namespace AIUsage.Tray;

/// <summary>Minimal rename prompt used by the provider card header's "Rename..." menu item — the
/// plain WPF counterpart of the Swift dashboard's inline rename field. An empty/blank result clears
/// the rename back to the derived name (see ProviderAccountsStore.Rename).</summary>
public partial class RenameDialog : Window
{
    public string? ResultText { get; private set; }

    public RenameDialog(string currentName)
    {
        InitializeComponent();
        NameBox.Text = currentName;
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ResultText = NameBox.Text;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
