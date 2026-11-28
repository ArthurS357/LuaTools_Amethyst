using System.Globalization;
using System.Windows;
using LuaToolsGui.ViewModels;

namespace LuaToolsGui.Views;

/// <summary>
/// The app's dark confirmation, in the same chrome as <see cref="GameCleanupDialog"/>: Depots "Remove game" and
/// every other confirm that used to be a white native MessageBox (delete preset, restart Steam, Steamless,
/// bulk lua removal). The name predates the generalisation.
/// </summary>
public partial class RemoveGameDialog : Wpf.Ui.Controls.FluentWindow
{
    private bool _answered;

    /// <summary>Depots "Remove game" (forget the game: live lua, loose build luas, stored builds).</summary>
    public RemoveGameDialog(string gameName, long appId)
        : this(new ConfirmRequest(
            LuaToolsGui.Resources.Strings.Builds_RemoveGame_Title,
            string.Format(CultureInfo.CurrentCulture, LuaToolsGui.Resources.Strings.Builds_RemoveGame_Body, gameName, appId),
            LuaToolsGui.Resources.Strings.Cleanup_Action_Confirm,
            IsDanger: true,
            Heading: gameName,
            Caption: string.Format(CultureInfo.CurrentCulture, LuaToolsGui.Resources.Strings.Common_AppId, appId),
            ShowVaultNotice: true))
    {
    }

    public RemoveGameDialog(ConfirmRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        InitializeComponent();
        Title = request.Title;
        DialogTitleBar.Title = request.Title;
        GameNameText.Text = request.Heading;
        GameNameText.Visibility = request.Heading is null ? Visibility.Collapsed : Visibility.Visible;
        AppIdText.Text = request.Caption;
        AppIdText.Visibility = request.Caption is null ? Visibility.Collapsed : Visibility.Visible;
        BodyText.Text = request.Body;
        VaultNotice.Visibility = request.ShowVaultNotice ? Visibility.Visible : Visibility.Collapsed;
        RemoveButton.Content = request.ConfirmText;
        if (!request.IsDanger)
        {
            RemoveButton.Appearance = Wpf.Ui.Controls.ControlAppearance.Primary;
            RemoveButton.Icon = null;
        }
        // Safe default: Enter on open cancels, it never confirms.
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
