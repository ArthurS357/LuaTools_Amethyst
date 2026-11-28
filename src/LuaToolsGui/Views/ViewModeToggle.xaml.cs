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
        new FrameworkPropertyMetadata(LibraryViewMode.Grid, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public LibraryViewMode ViewMode
    {
        get => (LibraryViewMode)GetValue(ViewModeProperty);
        set => SetValue(ViewModeProperty, value);
    }

    public ViewModeToggle() => InitializeComponent();

    private void GridButton_Click(object sender, RoutedEventArgs e) => ViewMode = LibraryViewMode.Grid;

    private void ListButton_Click(object sender, RoutedEventArgs e) => ViewMode = LibraryViewMode.List;
}
