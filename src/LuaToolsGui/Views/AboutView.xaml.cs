using System.Windows.Controls;
using LuaToolsGui.ViewModels;

namespace LuaToolsGui.Views;

public partial class AboutView : UserControl
{
    public AboutView(AboutViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
