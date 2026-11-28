using System.Windows;
using System.Windows.Controls;
using LuaToolsGui.Themes;
using LuaToolsGui.ViewModels;

namespace LuaToolsGui.Views;

/// <summary>The Manage page's game detail flyout. DataContext is inherited from ManageView.</summary>
public partial class ManageDetailFlyout : UserControl
{
    public ManageDetailFlyout()
    {
        InitializeComponent();
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && IsLoaded)
        {
            Motion.AnimateEntrance(Root, duration: Motion.ModeSwitchDuration);
            Motion.AnimateEntrance(FlyoutPanel, fromX: 24, duration: Motion.ViewTransitionDuration);
        }
    }

    private void Scrim_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataContext is ManageViewModel { IsRemoving: false } vm)
            vm.CloseDetailCommand.Execute(null);
    }
}
