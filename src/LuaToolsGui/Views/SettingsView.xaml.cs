using System.Windows.Controls;
using LuaToolsGui.ViewModels;

namespace LuaToolsGui.Views;

public partial class SettingsView : UserControl
{
    private readonly SettingsViewModel _vm;

    public SettingsView(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _vm = viewModel;
        Loaded += (_, _) => _vm.OnViewLoaded();
    }
}
