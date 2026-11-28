using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using LuaToolsGui.ViewModels;

namespace LuaToolsGui.Views;

/// <summary>Steam-style compact virtualized list mode for the Manage library.</summary>
public partial class ManageListView : UserControl
{
    public ManageListView() => InitializeComponent();

    public void ScrollToTop() => Dispatcher.BeginInvoke(() =>
    {
        if (FindDescendant<ScrollViewer>(GameList) is { } sv) sv.ScrollToTop();
    }, System.Windows.Threading.DispatcherPriority.Background);

    private void Row_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (DataContext is ManageViewModel vm && e.NewValue is LuaTileViewModel tile)
            vm.ResolveTile(tile);
    }

    private void Row_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is ManageViewModel vm && sender is FrameworkElement { DataContext: LuaTileViewModel tile })
            vm.ResolveTile(tile);
    }

    private void Row_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject src && FindAncestor<ButtonBase>(src) is not null)
            return;
        if (DataContext is not ManageViewModel vm || sender is not FrameworkElement { DataContext: LuaTileViewModel tile })
            return;

        if (vm.IsSelecting) tile.IsSelected = !tile.IsSelected;
        else vm.OpenDetailCommand.Execute(tile);
    }

    private void List_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Preview: ListBox's own class handler swallows Space before a bubbling KeyDown could see it.
        // Only the focused row itself counts — Enter/Space on its CheckBox or Remove button are left to
        // those controls, and never act on whatever row happens to be SelectedItem.
        if (DataContext is not ManageViewModel vm || e.OriginalSource is not ListBoxItem { DataContext: LuaTileViewModel tile })
            return;

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            vm.OpenDetailCommand.Execute(tile);
        }
        else if (e.Key == Key.Space)
        {
            e.Handled = true;
            tile.IsSelected = !tile.IsSelected;
        }
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } found) return found;
        }
        return null;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return null;
    }
}
