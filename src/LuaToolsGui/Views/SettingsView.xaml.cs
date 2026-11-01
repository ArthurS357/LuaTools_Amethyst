using System.Windows.Controls;
using System.Windows.Threading;
using LuaToolsGui.ViewModels;

namespace LuaToolsGui.Views;

public partial class SettingsView : UserControl
{
    private readonly SettingsViewModel _vm;

    public SettingsView(SettingsViewModel viewModel, BackupCleanupViewModel backups)
    {
        InitializeComponent();
        DataContext = viewModel;
        BackupsSection.DataContext = backups;
        _vm = viewModel;
        Loaded += (_, _) =>
        {
            _vm.OnViewLoaded();
            backups.RefreshCommand.Execute(null); // rescan on every visit: the Steam folder may have changed
            FocusCodeBoxIfAsked();
        };
        _vm.FocusCodeRequested += () => Dispatcher.BeginInvoke(FocusCodeBoxIfAsked, DispatcherPriority.ContextIdle);
    }

    /// <summary>Moves focus to the Discord code box when a cancelled sign-in sent the user here. After
    /// navigation the view may still be loading, so this runs from both Loaded and the request event.</summary>
    private void FocusCodeBoxIfAsked()
    {
        if (!IsLoaded || !_vm.TakeCodeFocusRequest()) return;
        CodeBox.BringIntoView();
        CodeBox.Focus();
    }
}
