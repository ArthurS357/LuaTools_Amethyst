using System.Globalization;
using System.Windows;

namespace LuaToolsGui.Views;

/// <summary>
/// Dark confirmation for Depots "Remove game" (forget the game: live lua, loose build luas, stored builds),
/// in the same chrome as <see cref="GameCleanupDialog"/>. Replaces the white native MessageBox.
/// </summary>
public partial class RemoveGameDialog : Wpf.Ui.Controls.FluentWindow
{
    private bool _answered;

    public RemoveGameDialog(string gameName, long appId)
    {
        InitializeComponent();
        GameNameText.Text = gameName;
        AppIdText.Text = string.Format(CultureInfo.CurrentCulture, LuaToolsGui.Resources.Strings.Common_AppId, appId);
        BodyText.Text = string.Format(CultureInfo.CurrentCulture, LuaToolsGui.Resources.Strings.Builds_RemoveGame_Body, gameName, appId);
        // Safe default: Enter on open cancels, it never deletes.
        Loaded += (_, _) => CancelButton.Focus();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Answer(false);

    private void Remove_Click(object sender, RoutedEventArgs e) => Answer(true);

    /// <summary>First answer wins: a double click on Remove (or Remove then Esc) cannot answer twice.</summary>
    private void Answer(bool remove)
    {
        if (_answered) return;
        _answered = true;
        DialogResult = remove;
    }
}
