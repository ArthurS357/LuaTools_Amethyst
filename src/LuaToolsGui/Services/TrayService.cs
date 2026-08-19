namespace LuaToolsGui.Services;

/// <summary>What the main window must do when the user asks to close it.</summary>
public enum WindowCloseAction
{
    /// <summary>Cancel the close, hide the window and leave the app resident in the tray.</summary>
    HideToTray,

    /// <summary>Let the close through and tear the application down for real.</summary>
    Shutdown,
}

/// <summary>
/// The tray icon, behind an interface.
///
/// <para>
/// <see cref="System.Windows.Forms.NotifyIcon"/> needs a WinForms message loop and a live window handle, so
/// nothing that touches it directly can be exercised in a unit test. Everything that DECIDES — close means
/// hide or close means quit, when the icon becomes visible, when it is disposed — lives in
/// <see cref="TrayService"/> against this interface instead. <see cref="NotifyIconTray"/> is the one
/// implementation the app ships, and it holds no logic worth testing.
/// </para>
/// </summary>
public interface ITrayIcon : IDisposable
{
    /// <summary>Whether the icon is currently in the notification area.</summary>
    bool Visible { get; set; }

    /// <summary>Tray double-click, or the context menu's "Open".</summary>
    event EventHandler? OpenRequested;

    /// <summary>The context menu's "Exit" — the only route to a real shutdown once close-to-tray is on.</summary>
    event EventHandler? ExitRequested;

    /// <summary>Pop a Windows balloon from the icon (used to report a silent install's outcome).</summary>
    void ShowBalloon(string title, string message, bool error);
}

/// <summary>
/// The window the tray drives, behind an interface for the same reason as <see cref="ITrayIcon"/>.
/// </summary>
/// <remarks>
/// <see cref="CloseWindow"/> and <see cref="Shutdown"/> are deliberately separate. Picking "Exit" asks the
/// WINDOW to close, which re-enters <see cref="TrayService.HandleCloseRequest"/> — this time with
/// <see cref="TrayService.ExitRequested"/> set, so the same one decision point answers "quit" and the
/// shutdown happens on that second pass. Collapsing them would give the tray a way to bypass the close
/// path, and the window a way to be disposed twice.
/// </remarks>
public interface ITrayWindow
{
    /// <summary>Take the window off screen and out of the taskbar, leaving the process alive.</summary>
    void HideWindow();

    /// <summary>Bring the window back: visible, un-minimized and in front.</summary>
    void RestoreWindow();

    /// <summary>Ask the window to close — re-enters the closing handler.</summary>
    void CloseWindow();

    /// <summary>Tear the application down.</summary>
    void Shutdown();
}

/// <summary>
/// Owns the tray icon and the close-versus-quit decision for the main window.
///
/// <para>
/// This used to be four fields and three methods on <c>MainWindow</c>'s code-behind: an
/// <c>_reallyExiting</c> flag, a <c>NotifyIcon</c>, and the rule itself inlined in the <c>Closing</c>
/// handler. Nothing about it could be tested — the whole thing needed a real window — so the one rule that
/// decides whether the user's X button quits the app was the only part of the flow with no coverage at
/// all. It is a rule with two ways to be wrong, and both are bad: quit when the user expected the tray
/// (work lost, the Steam backend goes down with it), or refuse to quit when the user picked "Exit" (an app
/// that cannot be closed).
/// </para>
///
/// <para>
/// The service also guarantees the icon is disposed exactly once, on the shutdown path only. A
/// <see cref="System.Windows.Forms.NotifyIcon"/> that is not disposed leaves a dead icon in the
/// notification area until the user hovers over it.
/// </para>
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly ITrayIcon _icon;
    private readonly ITrayWindow _window;
    private readonly Func<bool> _closeToTrayEnabled;

    /// <summary>True once the user picked the tray's "Exit" — the flag that lets the next close through.</summary>
    public bool ExitRequested { get; private set; }

    /// <summary>True once the tray icon has been disposed. Idempotent; a second dispose is a no-op.</summary>
    public bool IsDisposed { get; private set; }

    /// <param name="icon">The tray icon to own.</param>
    /// <param name="window">The window to hide, restore and close.</param>
    /// <param name="closeToTrayEnabled">Evaluated at each close, NOT captured once: the saved
    /// "Minimize to tray" setting can be toggled while the app runs, and the loader's session tray-lock
    /// (<c>--tray-locked</c>) is set by a signal from another instance long after this is constructed.</param>
    public TrayService(ITrayIcon icon, ITrayWindow window, Func<bool> closeToTrayEnabled)
    {
        _icon = icon;
        _window = window;
        _closeToTrayEnabled = closeToTrayEnabled;

        _icon.OpenRequested += (_, _) => Restore();
        _icon.ExitRequested += (_, _) => RequestExit();
    }

    /// <summary>
    /// The rule, on its own so it can be read and tested without a window: a close quits only when the user
    /// asked to quit, or when nothing is keeping the app resident.
    /// </summary>
    /// <param name="exitRequested">The user picked the tray's "Exit".</param>
    /// <param name="closeToTrayEnabled">The "Minimize to tray" setting is on, or this session is
    /// tray-locked by the loader.</param>
    public static WindowCloseAction Decide(bool exitRequested, bool closeToTrayEnabled) =>
        !exitRequested && closeToTrayEnabled ? WindowCloseAction.HideToTray : WindowCloseAction.Shutdown;

    /// <summary>
    /// Run the close: hide to the tray, or dispose the icon and shut down. The caller cancels the WPF close
    /// event when this answers <see cref="WindowCloseAction.HideToTray"/>.
    /// </summary>
    public WindowCloseAction HandleCloseRequest()
    {
        var action = Decide(ExitRequested, _closeToTrayEnabled());

        if (action is WindowCloseAction.HideToTray)
        {
            _window.HideWindow();
            _icon.Visible = true;
            return action;
        }

        Dispose();
        _window.Shutdown();
        return action;
    }

    /// <summary>Bring the window back and take the icon out of the tray (double-click, "Open", or
    /// "Minimize to tray" being switched off in Settings while the window is hidden).</summary>
    public void Restore()
    {
        _window.RestoreWindow();
        _icon.Visible = false;
    }

    /// <summary>Quit for real, from the tray's "Exit". Sets the flag and asks the window to close; the
    /// close re-enters <see cref="HandleCloseRequest"/>, which now answers
    /// <see cref="WindowCloseAction.Shutdown"/>.</summary>
    public void RequestExit()
    {
        ExitRequested = true;
        _icon.Visible = false;
        _window.CloseWindow();
    }

    /// <summary>Show the icon without ever surfacing the window — a headless
    /// <c>luatools://install/silent/&lt;id&gt;</c> launch lives entirely in the tray.</summary>
    public void ShowIconOnly() => _icon.Visible = true;

    /// <summary>Report a silent install's outcome as a Windows balloon. Makes the icon visible first: the
    /// balloon has nowhere to come from otherwise.</summary>
    public void ShowBalloon(string title, string message, bool error)
    {
        _icon.Visible = true;
        _icon.ShowBalloon(title, message, error);
    }

    /// <summary>Drop the icon out of the notification area. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        _icon.Dispose();
    }
}
