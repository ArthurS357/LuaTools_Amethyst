using System.Windows.Controls;
using LuaToolsGui.ViewModels;

namespace LuaToolsGui.Views;

public partial class DownloadsView : UserControl
{
    public DownloadsView(DownloadsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
