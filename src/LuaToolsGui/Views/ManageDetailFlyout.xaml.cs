using System.Windows.Controls;
using LuaToolsGui.ViewModels;

namespace LuaToolsGui.Views;

/// <summary>The Manage page's game detail flyout. DataContext is inherited from ManageView.</summary>
public partial class ManageDetailFlyout : UserControl
{
    public ManageDetailFlyout() => InitializeComponent();

    private void Scrim_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        (DataContext as ManageViewModel)?.CloseDetailCommand.Execute(null);
}
