using System.Windows.Controls;
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
        };
    }
}
