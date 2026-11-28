using System.Windows.Input;
using LuaToolsGui.Services;
using LuaToolsGui.ViewModels;

namespace LuaToolsGui.Views;

/// <summary>
/// Consolidated confirmation modal for removing a game's lua, exclusive manifests, and/or
/// installed files from Manage (4 scopes) or Depots (3 scopes).
/// </summary>
public partial class GameCleanupDialog : Wpf.Ui.Controls.FluentWindow
{
    private readonly GameCleanupDialogViewModel _viewModel;
    private bool _closing;

    public GameCleanupDialog(
        GameCleanupDialogViewModel viewModel,
        long appId,
        string gameName,
        GameCleanupDialogMode mode)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;

        _viewModel.CloseWith = ok =>
        {
            if (!IsLoaded || _closing) return;
            _closing = true;
            DialogResult = ok;
            Close();
        };

        // Block accidental close while files are actively being deleted.
        Closing += (_, e) =>
        {
            if (_viewModel.IsRemoving) e.Cancel = true;
            else
            {
                _closing = true;
                _viewModel.CancelInspection();
            }
        };

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            if (!_viewModel.IsRemoving)
                _viewModel.CancelCommand.Execute(null);
        };

        Loaded += async (_, _) => await _viewModel.LoadAsync(appId, gameName, mode);
    }

    public GameCleanupResult? Outcome => _viewModel.Result;
}
