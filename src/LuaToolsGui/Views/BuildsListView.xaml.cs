using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using LuaToolsGui.ViewModels;

namespace LuaToolsGui.Views;

/// <summary>Steam-style compact virtualized list mode for the Depots library.</summary>
public partial class BuildsListView : UserControl
{
    public BuildsListView() => InitializeComponent();

    public void ScrollToTop() => Dispatcher.BeginInvoke(() =>
    {
        if (FindDescendant<ScrollViewer>(GameList) is { } sv) sv.ScrollToTop();
    }, System.Windows.Threading.DispatcherPriority.Background);

    private void Row_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (DataContext is BuildsViewModel vm && e.NewValue is LuaTileViewModel game)
            vm.ResolveGame(game);
    }

    private void Row_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is BuildsViewModel vm && sender is FrameworkElement { DataContext: LuaTileViewModel game })
            vm.ResolveGame(game);
    }

    private void Row_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject src && FindAncestor<ButtonBase>(src) is not null)
            return;
        if (DataContext is BuildsViewModel vm && sender is FrameworkElement { DataContext: LuaTileViewModel game })
            vm.OpenGameCommand.Execute(game);
    }

    private void List_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (DataContext is BuildsViewModel vm && e.OriginalSource is ListBoxItem { DataContext: LuaTileViewModel game })
        {
            e.Handled = true;
            vm.OpenGameCommand.Execute(game);
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
