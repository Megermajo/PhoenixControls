using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Phoenix.Controls.Architect.WinUI.Controls;

public sealed partial class DatabankInspector : UserControl
{
    public DatabankInspector()
    {
        InitializeComponent();
    }

    // Stub handlers wired by the Fields/Schema toggle pair in DatabankInspector.xaml.
    // The schema-side functionality (binding SchemaList to DatabankBrowser.SelectedSchema)
    // is a follow-up sprint; today the click just swaps which ScrollViewer is visible.
    private void OnFieldsToggleClick(object sender, RoutedEventArgs e)
    {
        if (FieldsScroll is null || SchemaScroll is null) return;
        FieldsScroll.Visibility = Visibility.Visible;
        SchemaScroll.Visibility = Visibility.Collapsed;
        if (FieldsToggle is not null) FieldsToggle.IsChecked = true;
        if (SchemaToggle is not null) SchemaToggle.IsChecked = false;
    }

    private void OnSchemaToggleClick(object sender, RoutedEventArgs e)
    {
        if (FieldsScroll is null || SchemaScroll is null) return;
        FieldsScroll.Visibility = Visibility.Collapsed;
        SchemaScroll.Visibility = Visibility.Visible;
        if (FieldsToggle is not null) FieldsToggle.IsChecked = false;
        if (SchemaToggle is not null) SchemaToggle.IsChecked = true;
    }
}
