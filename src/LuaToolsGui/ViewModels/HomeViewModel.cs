using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuaToolsGui.Services;

namespace LuaToolsGui.ViewModels;

/// <summary>
/// Home dashboard: library stats, a "recently added" cover strip, and the at-a-glance state of everything
/// the other pages own — Steam, the active backend, the store-page plugin, and this build's own posture.
/// Reuses the same stplug-in scan + name/cover caches as the Manage page.
///
/// <para>
/// <b>Nothing here costs a round trip except the plugin tile</b>, which is deliberately fire-and-forget so
/// the page never waits on GitHub. Everything else is a settings read, a <c>File.Exists</c> or a process
/// lookup, which is what lets <see cref="RefreshStatusAsync"/> be safe to call on demand.
/// </para>
/// </summary>
public partial class HomeViewModel : ObservableObject
{
    /// <summary>Set by App: navigate to Manage and open this appid's detail (Home "recently added" click).</summary>
    public Action<long>? NavigateToGame { get; set; }

    // Section-navigation hooks wired by App (each → MainWindow.NavigateToXxx). Fire the matching command
    // from the clickable dashboard cells.
    public Action? NavigateToPlugin { get; set; }
    public Action? NavigateToManage { get; set; }
    public Action? NavigateToSettings { get; set; }
    public Action? NavigateToMode { get; set; }
    public Action? NavigateToAbout { get; set; }

    private readonly SteamService _steam;
    private readonly AuthService _auth;
    private readonly SteamAppListCache _appList;
    private readonly SteamAppInfoCache _appInfo;
    private readonly CoverCache _covers;
    private readonly UnlockerService _unlocker;
    private readonly PluginInstallerService _plugin;
    private readonly AmethystToolService _amethyst;
    private readonly UpdateService _updates;
    private readonly ToastService _toast;

    /// <summary>Drag-and-drop installer shown on the page; refreshes the library after a drop.</summary>
    public DropInstallViewModel Drop { get; }

    // ── Library stats ───────────────────────────────────────────────
    [ObservableProperty] private int _gameCount;

    // ── Store-page plugin status (at-a-glance on the dashboard) ─────
    [ObservableProperty] private string _pluginStatusText = Resources.Strings.Plugin_Checking;
    /// <summary>Theme resource KEY (not a literal colour) — resolved by ResourceKeyToBrushConverter so the
    /// palette lives entirely in Themes/Colors.xaml.</summary>
    [ObservableProperty] private string _pluginStatusColor = "TextMutedBrush";
    /// <summary>Not installed → show the tile's inline Install button.</summary>
    [ObservableProperty] private bool _showPluginInstall;
    /// <summary>Install in progress → disable the button (via <see cref="NotInstallingPlugin"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotInstallingPlugin))]
    private bool _isInstallingPlugin;
    public bool NotInstallingPlugin => !IsInstallingPlugin;

    // ── Recently added strip ────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecent))]
    private ObservableCollection<LuaTileViewModel> _recent = [];

    public bool HasRecent => Recent.Count > 0;

    // ── Steam + account status ──────────────────────────────────────
    [ObservableProperty] private bool _steamFound;
    [ObservableProperty] private string _steamStatus = Resources.Strings.Home_CheckingSteam;

    /// <summary>Whether a steam.exe is up right now. Separate from <see cref="SteamFound"/>: an install
    /// that exists but is closed is the state most of this app's actions actually need, and a page that
    /// only said "detected" left the user to go and look.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SteamRunningText))]
    private bool _steamRunning;

    public string SteamRunningText => SteamRunning
        ? Resources.Strings.Home_SteamRunning
        : Resources.Strings.Home_SteamClosed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGuest))]
    private bool _isSignedIn;

    public bool IsGuest => !IsSignedIn;
    [ObservableProperty] private string _accountStatus = Resources.Strings.Home_BrowsingAsGuest;

    // ── Active backend ──────────────────────────────────────────────

    /// <summary>What owns the proxy DLLs next to steam.exe, in one line.</summary>
    [ObservableProperty] private string _modeStatus = Resources.Strings.Home_NoModeSelected;

    /// <summary>Something holds the slot — drives the row's icon accent rather than a second string.</summary>
    [ObservableProperty] private bool _hasActiveBackend;

    // ── This build's own posture ────────────────────────────────────

    /// <summary>Version of the running build, e.g. "v1.6.1". Read through <see cref="AppVersion"/> so the
    /// dashboard, the nav footer and the User-Agent can never disagree about which build this is.</summary>
    public string VersionLabel { get; } = $"v{AppVersion.Current}";

    /// <summary>Whether self-update is off — the property that defines this fork, so it is stated rather
    /// than left for the user to find on the About page. Read from the LIVE resolution the updater uses,
    /// not re-derived from settings, for the reason given on <see cref="AboutViewModel"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrivacyText))]
    private bool _selfUpdateDisabled;

    public string PrivacyText => SelfUpdateDisabled
        ? Resources.Strings.Home_Privacy_UpdatesOff
        : Resources.Strings.Home_Privacy_UpdatesOn;

    public HomeViewModel(SteamService steam, AuthService auth,
        SteamAppListCache appList, SteamAppInfoCache appInfo, CoverCache covers, DropInstallViewModel drop,
        UnlockerService unlocker, PluginInstallerService plugin, AmethystToolService amethyst,
        UpdateService updates, ToastService toast)
    {
        _steam = steam;
        _auth = auth;
        _appList = appList;
        _appInfo = appInfo;
        _covers = covers;
        _unlocker = unlocker;
        _plugin = plugin;
        _amethyst = amethyst;
        _updates = updates;
        _toast = toast;
        Drop = drop;
        _auth.AuthStateChanged += RefreshAccount;
        // Library refresh on any install (drag-drop, plugin, Add page, Fixes) is driven by
        // LuaInstaller.Installed, wired in App → RefreshLibraryAsync.
    }

    /// <summary>Open a recently-added game in the Manage detail view.</summary>
    [RelayCommand]
    private void OpenGame(LuaTileViewModel tile) => NavigateToGame?.Invoke(tile.AppId);

    // Clickable dashboard cells → section navigation.
    [RelayCommand] private void OpenPlugin() => NavigateToPlugin?.Invoke();
    [RelayCommand] private void OpenManage() => NavigateToManage?.Invoke();
    [RelayCommand] private void OpenSettings() => NavigateToSettings?.Invoke();
    [RelayCommand] private void OpenMode() => NavigateToMode?.Invoke();

    /// <summary>The "check for updates" shortcut. Goes to the About page, which owns that button, rather
    /// than starting a check from here: one code path to the updater, and Home stays free of network work.</summary>
    [RelayCommand] private void OpenAbout() => NavigateToAbout?.Invoke();

    /// <summary>Re-read every status the page shows. Steam can be opened or closed, and a Mode installed,
    /// entirely outside this app — without this the dashboard goes stale and quietly keeps asserting it.</summary>
    [RelayCommand]
    private async Task RefreshStatusAsync()
    {
        RefreshSteam();
        RefreshAccount();
        RefreshBackend();
        RefreshPosture();
        await RefreshPluginStatusAsync();
    }

    /// <summary>Inline install of the store-page plugin from the Home tile (mirrors PluginViewModel.Install):
    /// confirm the Steam restart, install, toast the outcome, then refresh the tile.</summary>
    [RelayCommand]
    private async Task InstallPlugin()
    {
        if (IsInstallingPlugin) return;
        var confirm = System.Windows.MessageBox.Show(
            Resources.Strings.Plugin_Confirm_RestartBody,
            Resources.Strings.Plugin_Confirm_RestartCaption,
            System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.OK) return;

        IsInstallingPlugin = true;
        PluginStatusText = Resources.Strings.Plugin_Checking;
        PluginStatusColor = "TextMutedBrush";
        try
        {
            var (ok, error) = await _plugin.InstallAsync(progress: null);
            _toast.Show(Resources.Strings.Plugin_Toast_Title, ok
                ? Resources.Strings.Plugin_Toast_Installed
                : string.Format(Resources.Strings.Plugin_Toast_InstallFailed, error), error: !ok);
        }
        finally
        {
            IsInstallingPlugin = false;
            await RefreshPluginStatusAsync();
        }
    }

    /// <summary>Called when the page is shown — refresh everything.</summary>
    public async Task LoadAsync()
    {
        RefreshSteam();
        RefreshAccount();
        RefreshBackend();
        RefreshPosture();
        _ = RefreshPluginStatusAsync(); // fire-and-forget: may hit GitHub, must not delay the page
        await RefreshLibraryAsync();
    }

    /// <summary>Populate the "Plugin Status" dashboard tile from the same source the Plugin page uses.</summary>
    private async Task RefreshPluginStatusAsync()
    {
        try
        {
            var st = await _plugin.GetStatusAsync(force: false);
            bool installed = st.FrontendInstalled && st.DllInstalled;
            ShowPluginInstall = !installed;
            (PluginStatusText, PluginStatusColor) =
                !installed ? (Resources.Strings.Plugin_Status_NotInstalled, "TextMutedBrush")
                : st.UpdateAvailable ? (Resources.Strings.Plugin_Badge_UpdateAvailable, "WarningBrush")
                : (st.InstalledTag is { } tag
                      ? $"{Resources.Strings.Plugin_Status_Installed} · {tag}"
                      : Resources.Strings.Plugin_Status_Installed, "SuccessTextBrush");
        }
        catch { /* leave the prior value (e.g. "Checking…") on any failure */ }
    }

    /// <summary>
    /// Report whichever backend owns the proxy DLLs — including AmethystTool, which is not an
    /// <see cref="UnlockerMode"/>.
    ///
    /// <para>
    /// This used to read <c>SelectedModeDisplayName</c> alone, which is null while AmethystTool holds the
    /// slot: the dashboard therefore said "no mode selected" to every user running AmethystTool. There is
    /// one slot and one string behind it, so there is one question to ask — see
    /// <see cref="ActiveBackendPolicy"/>.
    /// </para>
    /// </summary>
    private void RefreshBackend()
    {
        // IsActive, not the selection alone: it also confirms the payload is still on disk, so a card
        // deleted outside this app is not reported as running.
        if (_amethyst.IsActive)
        {
            ModeStatus = Resources.Strings.Home_AmethystActive;
            HasActiveBackend = true;
            return;
        }

        string? mode = _unlocker.SelectedModeDisplayName;
        ModeStatus = mode is not null
            ? string.Format(Resources.Strings.Home_ModeIs, mode)
            : Resources.Strings.Home_NoModeSelected;
        HasActiveBackend = mode is not null;
    }

    private void RefreshPosture() => SelfUpdateDisabled = _updates.Sources.IsDisabled;

    private void RefreshSteam()
    {
        SteamFound = _steam.IsValid;
        SteamStatus = SteamFound
            ? string.Format(Resources.Strings.Home_SteamDetected, _steam.EffectivePath)
            : Resources.Strings.Home_SteamNotFound;
        SteamRunning = SteamFound && SteamService.IsSteamRunning();
    }

    /// <summary>Rebuild the library count + "Recently added" strip (and warm the recent covers). Public
    /// so App can call it from LuaInstaller.Installed to refresh live after any add.</summary>
    public async Task RefreshLibraryAsync()
    {
        string? dir = _steam.StPlugInDir;
        if (dir is null || !Directory.Exists(dir))
        {
            GameCount = 0;
            Recent = [];
            return;
        }

        await _appList.EnsureLoadedAsync();

        var tiles = await Task.Run(() =>
            Directory.EnumerateFiles(dir, "*.lua")
                .Select(path => (path, name: Path.GetFileNameWithoutExtension(path)))
                .Where(f => long.TryParse(f.name, out _))
                .Select(f =>
                {
                    long appid = long.Parse(f.name);
                    var info = new FileInfo(f.path);
                    string? name = _appList.GetName(appid) ?? _appInfo.GetCached(appid)?.Name;
                    // Base = when added to the folder; if edited since (LastWrite later), use that — newer is more relevant.
                    var added = info.LastWriteTime > info.CreationTime ? info.LastWriteTime : info.CreationTime;
                    return new LuaTileViewModel(appid, f.path, added, name ?? string.Format(Resources.Strings.Common_AppFallback, appid), name is null);
                })
                .OrderByDescending(t => t.AddedAt)
                .ToList());

        GameCount = tiles.Count;

        var recent = tiles.Take(4).ToList();
        Recent = new ObservableCollection<LuaTileViewModel>(recent);
        foreach (var t in recent) _ = t.EnsureResolvedAsync(_appInfo, _covers); // warm covers
    }

    private void RefreshAccount()
    {
        IsSignedIn = _auth.IsSignedIn;
        AccountStatus = IsSignedIn
            ? (_auth.DisplayName is { } n ? string.Format(Resources.Strings.Home_SignedInAs, n) : Resources.Strings.Home_SignedIn)
            : Resources.Strings.Home_BrowsingAsGuest;
    }
}
