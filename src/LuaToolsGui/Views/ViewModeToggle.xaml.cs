using System.Windows;
using System.Windows.Controls;
using LuaToolsGui.Services;

namespace LuaToolsGui.Views;

/// <summary>Segmented Grid / List layout toggle shared between Manage and Depots.</summary>
public partial class ViewModeToggle : UserControl
{
    public static readonly DependencyProperty ViewModeProperty = DependencyProperty.Register(
        nameof(ViewMode),
        typeof(LibraryViewMode),
        typeof(ViewModeToggle),
        new FrameworkPropertyMetadata(
            LibraryViewMode.Grid,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            static (d, _) => ((ViewModeToggle)d).SyncChecked()));

    public LibraryViewMode ViewMode
    {
        get => (LibraryViewMode)GetValue(ViewModeProperty);
        set => SetValue(ViewModeProperty, value);
    }

    public ViewModeToggle()
    {
        InitializeComponent();
        SyncChecked();
    }

    /// <summary>IsChecked mirrors ViewMode (both ways), so screen readers report the active layout.</summary>
    private void SyncChecked()
    {
        GridButton.IsChecked = ViewMode == LibraryViewMode.Grid;
        ListButton.IsChecked = ViewMode == LibraryViewMode.List;
    }

    private void GridButton_Checked(object sender, RoutedEventArgs e) => ViewMode = LibraryViewMode.Grid;

    private void ListButton_Checked(object sender, RoutedEventArgs e) => ViewMode = LibraryViewMode.List;
}
