using LuaToolsGui.ViewModels;

namespace LuaToolsGui.Views;

/// <summary>Modal save-backup list for one game, opened from the Manage page.</summary>
public partial class SaveBackupDialog : Wpf.Ui.Controls.FluentWindow
{
    public SaveBackupDialog(SaveBackupViewModel viewModel, long appId, string gameName)
    {
        InitializeComponent();
        viewModel.Load(appId, gameName);
        DataContext = viewModel;
    }

    private void Close_Click(object sender, System.Windows.RoutedEventArgs e) => Close();
}
