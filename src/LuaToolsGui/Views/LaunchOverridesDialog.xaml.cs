using System.Windows;
using LuaToolsGui.ViewModels;

namespace LuaToolsGui.Views;

/// <summary>Modal list of every game whose launch options were edited, opened from the Manage page.</summary>
public partial class LaunchOverridesDialog : Wpf.Ui.Controls.FluentWindow
{
    public LaunchOverridesDialog(LaunchOverridesViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // A restore in progress has already closed Steam: keep the window, and its result, on screen.
        Closing += (_, e) => e.Cancel = viewModel.RestoreAllCommand.IsRunning;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
