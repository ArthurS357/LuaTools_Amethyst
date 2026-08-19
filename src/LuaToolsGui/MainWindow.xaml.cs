using System.Windows;
using LuaToolsGui.Services;
using LuaToolsGui.ViewModels;
using LuaToolsGui.Views;

namespace LuaToolsGui;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow, ITrayWindow
{
    private readonly SettingsService _settings;
    private readonly TrayService _tray;

    public MainWindow(MainViewModel viewModel, IServiceProvider services, SettingsService settings)
    {
        _settings = settings;
        InitializeComponent();
        DataContext = viewModel;

        // NavigationView resolves page instances (DownloadView/SettingsView) from DI.
        RootNavigation.SetServiceProvider(services);

        // The tray icon and the close-versus-quit rule live in TrayService; this window is only the
        // ITrayWindow it drives. Everything the rule depends on is read at each close, not captured here —
        // the setting is a toggle the user can flip while the app runs, and Program.SessionTrayLock is set
        // by a signal from a --tray-locked relaunch after this constructor has long returned.
        _tray = new TrayService(
            new NotifyIconTray(
                LuaToolsGui.Resources.Strings.App_DisplayName,
                LuaToolsGui.Resources.Strings.Tray_Open,
                LuaToolsGui.Resources.Strings.Tray_Exit),
            this,
            closeToTrayEnabled: () => _settings.MinimizeToTray || Program.SessionTrayLock);

        Closing += OnWindowClosing;

        Loaded += async (_, _) =>
        {
            RootNavigation.Navigate(typeof(HomeView));
            try { await viewModel.InitializeAsync(); }
            catch { /* auth restore failed (e.g. offline) — UI still loads as guest */ }
        };
    }

    // ── System tray ─────────────────────────────────────────────────

    /// <summary>Clicking the window's close (X) button hides to the tray instead of quitting, when the
    /// MinimizeToTray setting is on (the default) OR the app was launched with --tray-locked (the loader
    /// passes this to keep the backend alive — session-only, doesn't touch the saved setting). Either way,
    /// only the tray "Exit" item actually closes the app. The rule itself is
    /// <see cref="TrayService.Decide"/>.</summary>
    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e) =>
        e.Cancel = _tray.HandleCloseRequest() is WindowCloseAction.HideToTray;

    /// <summary>Bring the window back from the tray (tray double-click, "Open" menu, or Minimize-to-tray
    /// being turned off in Settings).</summary>
    public void RestoreFromTray() => _tray.Restore();

    /// <summary>Launch tray-only for a headless install (luatools://install/silent/&lt;id&gt;): show the tray
    /// icon but never surface the window. The app sits in the background and reports via a balloon tip.</summary>
    public void StartSilent() => _tray.ShowIconOnly();

    /// <summary>Pop a Windows balloon from the tray icon — reports a silent install's outcome.</summary>
    public void ShowInstallNotification(string message, bool error) =>
        _tray.ShowBalloon(LuaToolsGui.Resources.Strings.App_DisplayName, message, error);

    // ── ITrayWindow ─────────────────────────────────────────────────

    void ITrayWindow.HideWindow() => Hide();

    void ITrayWindow.RestoreWindow()
    {
        Show();
        WindowState = WindowState.Normal;

        // Activate() alone often loses to Windows' foreground rules when the request comes from another
        // process (a relaunch). Bouncing Topmost reliably pulls the window to the front.
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    void ITrayWindow.CloseWindow() => Close();

    // ShutdownMode is OnExplicitShutdown (a silent/--minimized launch never shows a window, so
    // OnLastWindowClose would otherwise tear the app down before it does anything) — so a real window
    // close has to ask for shutdown itself instead of relying on the window count.
    void ITrayWindow.Shutdown() => Application.Current.Shutdown();

    /// <summary>Switch to the Add page (used by the Manage page's "Update" action).</summary>
    public void NavigateToAdd() => RootNavigation.Navigate(typeof(DownloadView));

    /// <summary>Switch to Manage (used by Home's "recently added" cards). Caller opens the detail.</summary>
    public void NavigateToManage() => RootNavigation.Navigate(typeof(ManageView));

    /// <summary>Switch to Builds (used by the Manage flyout's "Manage Build"). Caller selects the game.</summary>
    public void NavigateToBuilds() => RootNavigation.Navigate(typeof(BuildsView));

    /// <summary>Switch to Settings (used when a guest hits a protected action).</summary>
    public void NavigateToSettings() => RootNavigation.Navigate(typeof(SettingsView));

    /// <summary>Switch to Fixes (used by the protocol handler to open a specific game's fixes).</summary>
    public void NavigateToFixes() => RootNavigation.Navigate(typeof(FixesView));

    /// <summary>Switch to Plugin (used by Home's "Plugin Status" tile).</summary>
    public void NavigateToPlugin() => RootNavigation.Navigate(typeof(PluginView));

    /// <summary>Switch to Mode (used by Home's mode status row).</summary>
    public void NavigateToMode() => RootNavigation.Navigate(typeof(ModeView));

    // "Restart Steam" is an action, not a page — run the command, don't leave it selected.
    private void RestartSteam_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.RestartSteamCommand.Execute(null);
    }
}
