using System.Windows.Controls;
using LuaToolsGui.ViewModels;

namespace LuaToolsGui.Views;

public partial class HomeView : UserControl
{
    private readonly HomeViewModel _viewModel;

    public HomeView(HomeViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        Loaded += async (_, _) => await _viewModel.LoadAsync();
    }
}
