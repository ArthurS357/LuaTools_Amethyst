using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using LuaToolsGui.Themes;
using LuaToolsGui.ViewModels;

namespace LuaToolsGui.Views;

public partial class ManageView : UserControl
{
    private readonly ManageViewModel _viewModel;

    public ManageView(ManageViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        // On a page change, jump the grid back to the top (the new page starts fresh).
        _viewModel.ScrollToTop = ScrollGridToTop;
        // Re-scan stplug-in each time the page is shown (lua files change as games are added).
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape && _viewModel.IsDetailOpen && !_viewModel.IsRemoving)
            {
                e.Handled = true;
                _viewModel.CloseDetailCommand.Execute(null);
            }
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        await _viewModel.LoadAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) =>
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ManageViewModel.ViewMode) && IsLoaded)
        {
            FrameworkElement target = _viewModel.IsListMode ? GameListView : TileScroller;
            Motion.AnimateEntrance(target, fromY: 8, duration: Motion.ModeSwitchDuration);
        }
    }

    /// <summary>Scroll the active grid or list back to offset 0 after page changes.</summary>
    private void ScrollGridToTop()
    {
        if (_viewModel.IsListMode)
        {
            GameListView.ScrollToTop();
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (FindDescendant<ScrollViewer>(TileScroller) is { } sv) sv.ScrollToTop();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } found) return found;
        }
        return null;
    }

    // Both fire as a virtualized tile is realized/scrolled into view — resolve its cover.
    // EnsureResolvedAsync is idempotent (skips if a cover is already loaded), so double-firing is safe.
    private void Tile_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is LuaTileViewModel tile) _viewModel.ResolveTile(tile);
    }

    private void Tile_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LuaTileViewModel tile }) _viewModel.ResolveTile(tile);
    }

    // Click a tile → toggle selection while in selection mode, otherwise open the detail flyout.
    private void Tile_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Ignore clicks on a button/checkbox so they don't also trigger the tile action.
        if (e.OriginalSource is DependencyObject src && FindAncestor<System.Windows.Controls.Primitives.ButtonBase>(src) is not null)
            return;
        if (sender is not FrameworkElement { DataContext: LuaTileViewModel tile }) return;

        if (_viewModel.IsSelecting) tile.IsSelected = !tile.IsSelected;
        else _viewModel.OpenDetailCommand.Execute(tile);
    }

    private void ToggleSelect_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LuaTileViewModel tile })
            tile.IsSelected = !tile.IsSelected;
    }

    private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }
        return null;
    }
}
