using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LuaToolsGui.Themes;
using LuaToolsGui.ViewModels;

namespace LuaToolsGui.Views;

public partial class BuildsView : UserControl
{
    private readonly BuildsViewModel _viewModel;

    public BuildsView(BuildsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        // On a page change, jump the grid back to the top (the new page starts fresh).
        _viewModel.ScrollToTop = ScrollGridToTop;
        // Re-scan stplug-in each time the page is shown (lua files change as games are added/switched).
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        PreviewKeyDown += OnViewPreviewKeyDown;
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
        if (!IsLoaded) return;

        if (e.PropertyName == nameof(BuildsViewModel.IsShowingDetail))
        {
            if (_viewModel.IsShowingDetail)
                Motion.AnimateEntrance(DetailPane, fromX: 18, duration: Motion.ViewTransitionDuration);
            else
                Motion.AnimateEntrance(State1Host, fromX: -18, duration: Motion.ViewTransitionDuration);
        }
        else if (e.PropertyName == nameof(BuildsViewModel.ViewMode) && _viewModel.IsShowingGrid)
        {
            FrameworkElement target = _viewModel.IsListMode ? GameListView : GameGrid;
            Motion.AnimateEntrance(target, fromY: 8, duration: Motion.ModeSwitchDuration);
        }
    }

    private void OnViewPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !_viewModel.IsShowingDetail || _viewModel.IsRemoving)
            return;

        if (_viewModel.IsDepotPickerOpen)
        {
            e.Handled = true;
            _viewModel.CancelDepotDownloadCommand.Execute(null);
            return;
        }
        if (_viewModel.IsRenaming)
        {
            e.Handled = true;
            _viewModel.CancelRenameCommand.Execute(null);
            return;
        }
        if (_viewModel.IsEditing)
        {
            e.Handled = true;
            _viewModel.CancelEditCommand.Execute(null);
            return;
        }

        e.Handled = true;
        _viewModel.BackToGridCommand.Execute(null);
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
            if (FindDescendant<ScrollViewer>(GameGrid) is { } sv) sv.ScrollToTop();
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

    // Both fire as a virtualized game card is realized/scrolled into view — resolve its cover.
    // EnsureResolvedAsync is idempotent (skips if a cover is already loaded), so double-firing is safe.
    private void GameRow_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is LuaTileViewModel game) _viewModel.ResolveGame(game);
    }

    private void GameRow_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LuaTileViewModel game }) _viewModel.ResolveGame(game);
    }
}
