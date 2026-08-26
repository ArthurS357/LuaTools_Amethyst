using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuaToolsGui.Models;
using LuaToolsGui.Services;

namespace LuaToolsGui.ViewModels;

/// <summary>One card on the Mode page — a single unlocker backend's name, status, and action button.</summary>
public partial class ModeCardViewModel(UnlockerMode mode, string title, string description) : ObservableObject
{
    public UnlockerMode Mode { get; } = mode;
    public string Title { get; } = title;
    public string Description { get; } = description;

    [ObservableProperty] private string _statusText = Resources.Strings.Mode_Checking;
    [ObservableProperty] private string _buttonText = Resources.Strings.Mode_Btn_Install;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowManage))]
    [NotifyPropertyChangedFor(nameof(ShowUninstall))]
    [NotifyPropertyChangedFor(nameof(ShowNoRecordHint))]
    private bool _isActive;

    /// <summary>
    /// Whether this app can prove which files next to steam.exe belong to this mode. Mirrored from the page
    /// (<see cref="ModeViewModel.ModeHasRecord"/>) because only the card knows whether it is the active
    /// one, and the hint must not appear on the cards that are not.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoRecordHint))]
    private bool _hasInstallRecord;

    /// <summary>Uninstall belongs to the mode that is actually installed, so it shows on that card only.</summary>
    public bool ShowUninstall => IsActive;

    /// <summary>Active but with nothing recorded — explain why Uninstall is unavailable rather than showing
    /// a dead button with no reason attached.</summary>
    public bool ShowNoRecordHint => IsActive && !HasInstallRecord;

    /// <summary>The CloudRedirect "Manage" button shows only on the CloudRedirect card while it's active.</summary>
    public bool IsCloudRedirect => Mode == UnlockerMode.CloudRedirect;
    public bool ShowManage => IsCloudRedirect && IsActive;

    /// <summary>BetterSteamTools (enum still OpenSteamTools internally) gets a "Recommended" badge.</summary>
    public bool IsRecommended => Mode == UnlockerMode.OpenSteamTools;

    /// <summary>The nightly build gets "Experimental" + "CloudRedirect Support" badges.</summary>
    public bool IsExperimental => Mode == UnlockerMode.OpenSteamToolsNightly;
    public bool SupportsCloudRedirect => Mode == UnlockerMode.OpenSteamToolsNightly;
}

/// <summary>
/// "Mode" page: AmethystTool and the OpenSteamTools builds — mutually exclusive, one active at a time.
/// Checks status on page open; each card installs/switches after a Steam-shutdown confirmation, then
/// relaunches Steam so the new mode takes effect.
///
/// <para>
/// <b>Exclusivity is a property of the data, not of this page.</b> Every backend here — the cards and
/// AmethystTool alike — reads its active state from the one slot <see cref="ActiveBackendPolicy"/> owns,
/// so refreshing after an install is enough to demote whatever held it before. Nothing walks the list
/// turning other cards off, because nothing can be on twice.
/// </para>
/// </summary>
public partial class ModeViewModel : ObservableObject
{
    private readonly UnlockerService _unlocker;
    private readonly ToastService _toast;
    private readonly SteamService _steam;
    private readonly CloudRedirectService _cloudRedirect;
    private readonly ModeRemovalService _modeRemoval;

    public ObservableCollection<ModeCardViewModel> Cards { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotBusy))]
    [NotifyPropertyChangedFor(nameof(CanUseCloudRedirect))]
    [NotifyPropertyChangedFor(nameof(CanUninstallMode))]
    [NotifyPropertyChangedFor(nameof(CanUninstallAmethyst))]
    private bool _isBusy;
    public bool NotBusy => !IsBusy;

    /// <summary>True when the active mode has an install record to remove — see
    /// <see cref="ModeRemovalService.CanUninstall"/> for why the record, and not the files, is the gate.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUninstallMode))]
    private bool _modeHasRecord;

    public bool CanUninstallMode => ModeHasRecord && !IsBusy;

    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isProgressIndeterminate;

    // ── Steam-shutdown confirmation overlay ──────────────────────────
    //
    // One overlay serves install and uninstall, for the Mode cards and for AmethystTool alike. All four ask
    // for the same permission — to close Steam — and a second scrim with its own copy of the buttons would
    // drift out of step with this one. It replaced a pair of MessageBox prompts the AmethystTool card used
    // to raise while it lived on the Plugin page.
    private enum PendingAction { Install, Uninstall, AmethystInstall, AmethystUninstall }

    [ObservableProperty] private bool _isConfirming;
    [ObservableProperty] private string _confirmTitle = "";
    [ObservableProperty] private string _confirmBody = Resources.Strings.Mode_Confirm_Body;
    private ModeCardViewModel? _pendingCard;
    private PendingAction _pending;

    public ModeViewModel(UnlockerService unlocker, ToastService toast, SteamService steam,
        CloudRedirectService cloudRedirect, ModeRemovalService modeRemoval,
        AmethystToolViewModel amethyst)
    {
        _unlocker = unlocker;
        _toast = toast;
        _steam = steam;
        _cloudRedirect = cloudRedirect;
        _modeRemoval = modeRemoval;
        Amethyst = amethyst;

        // The Uninstall gate reads a flag the card owns and refreshes on its own (after an install, after a
        // removal). Without this the button keeps whatever enabled state it had when the page last loaded.
        // Both objects are singletons with the app's lifetime, so there is nothing to unsubscribe.
        Amethyst.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(AmethystToolViewModel.HasInstallRecord):
                    OnPropertyChanged(nameof(CanUninstallAmethyst));
                    break;

                // The card draws no progress bar of its own. This page has one, docked at the bottom and
                // shared by every Mode, and an AmethystTool install is a Mode install — it should move the
                // same bar rather than growing a second one halfway up the page.
                case nameof(AmethystToolViewModel.Progress):
                    Progress = Amethyst.Progress;
                    break;
                case nameof(AmethystToolViewModel.IsProgressIndeterminate):
                    IsProgressIndeterminate = Amethyst.IsProgressIndeterminate;
                    break;
            }
        };
    }

    /// <summary>
    /// The AmethystTool card. A Mode by the only test that matters — it writes the same <c>dwmapi.dll</c> and
    /// <c>xinput1_4.dll</c> next to <c>steam.exe</c> that every other Mode does, so having it installed and a
    /// Mode installed are the same slot. Held as its own view model rather than folded into
    /// <see cref="Cards"/>: it reports versions, a backup folder and an install record, none of which a
    /// <see cref="ModeCardViewModel"/> has anywhere to put.
    /// </summary>
    public AmethystToolViewModel Amethyst { get; }

    /// <summary>
    /// Whether AmethystTool's Uninstall button is live. Page-level, like
    /// <see cref="CanUninstallMode"/>: it takes the page's busy flag into account, so a Mode install in
    /// progress disables it too. Those two operations write the same files.
    /// </summary>
    public bool CanUninstallAmethyst => Amethyst.HasInstallRecord && !IsBusy;

    /// <summary>CloudRedirect "Manage": download (cache) the CloudRedirect GUI and launch it.</summary>
    [RelayCommand]
    private async Task ManageCloudRedirect()
    {
        if (IsBusy) return;
        IsBusy = true;
        IsProgressIndeterminate = true;
        Progress = 0;
        try
        {
            var prog = new Progress<double?>(p =>
            {
                IsProgressIndeterminate = p is null;
                if (p is not null) Progress = p.Value * 100;
            });

            bool ok = await _cloudRedirect.LaunchAsync(prog);
            if (!ok)
                _toast.Show(Resources.Strings.Mode_CloudRedirect_Manage,
                    Resources.Strings.Mode_CloudRedirect_LaunchFailed, error: true);
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    // ── CloudRedirect add-on (bottom panel; usable only when Nightly BST is active) ───
    private const string CloudRedirectTitle = "CloudRedirect"; // product name — not localized

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCloudRedirectManage))]
    [NotifyPropertyChangedFor(nameof(ShowCloudRedirectUpdate))]
    [NotifyPropertyChangedFor(nameof(CanUseCloudRedirect))]
    private bool _cloudRedirectUnlocked;

    /// <summary>Buttons on the add-on panel are usable only when unlocked (Nightly active) and idle.</summary>
    public bool CanUseCloudRedirect => CloudRedirectUnlocked && !IsBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CloudRedirectToggleText))]
    [NotifyPropertyChangedFor(nameof(ShowCloudRedirectManage))]
    private bool _cloudRedirectEnabled;

    [ObservableProperty] private bool _cloudRedirectInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCloudRedirectUpdate))]
    private bool _cloudRedirectUpdateAvailable;

    [ObservableProperty] private string _cloudRedirectStatusText = "";

    public string CloudRedirectToggleText => CloudRedirectEnabled
        ? Resources.Strings.Mode_CloudRedirect_Disable
        : Resources.Strings.Mode_CloudRedirect_Enable;
    public bool ShowCloudRedirectUpdate => CloudRedirectUnlocked && CloudRedirectUpdateAvailable;
    public bool ShowCloudRedirectManage => CloudRedirectUnlocked && CloudRedirectEnabled;

    /// <summary>Refresh the add-on panel state. Reads dll/toml from disk (cheap); checks GitHub for an
    /// update only when unlocked (Nightly active), respecting forceRefresh.</summary>
    private async Task RefreshCloudRedirectAsync(bool forceRefresh)
    {
        CloudRedirectUnlocked = _unlocker.SelectedMode == UnlockerMode.OpenSteamToolsNightly;
        var s = await _unlocker.GetCloudRedirectStateAsync(checkUpdate: CloudRedirectUnlocked, forceRefresh);
        CloudRedirectInstalled = s.Installed;
        CloudRedirectEnabled = s.Enabled;
        CloudRedirectUpdateAvailable = s.UpdateAvailable;

        CloudRedirectStatusText = !CloudRedirectUnlocked ? Resources.Strings.Mode_CloudRedirect_Locked
            : !s.Installed ? Resources.Strings.Mode_CloudRedirect_Status_NotInstalled
            : s.UpdateAvailable ? Resources.Strings.Mode_CloudRedirect_Status_UpdateAvailable
            : s.Enabled ? Resources.Strings.Mode_CloudRedirect_Status_Enabled
            : Resources.Strings.Mode_CloudRedirect_Status_Disabled;
    }

    /// <summary>Enable/disable the add-on (edits opensteamtool.toml; first enable also downloads the dll).
    /// Lightweight — no Steam close; the change applies on the next Steam launch.</summary>
    [RelayCommand]
    private async Task ToggleCloudRedirect()
    {
        if (IsBusy || !CloudRedirectUnlocked) return;
        bool enabling = !CloudRedirectEnabled;
        IsBusy = true;
        IsProgressIndeterminate = true;
        Progress = 0;
        try
        {
            var prog = new Progress<double?>(p =>
            {
                IsProgressIndeterminate = p is null;
                if (p is not null) Progress = p.Value * 100;
            });

            var result = enabling
                ? await _unlocker.EnableCloudRedirectAsync(prog)
                : _unlocker.DisableCloudRedirect();

            if (result.Success)
                _toast.Show(CloudRedirectTitle, enabling
                    ? Resources.Strings.Mode_CloudRedirect_Toast_Enabled
                    : Resources.Strings.Mode_CloudRedirect_Toast_Disabled);
            else
                _toast.Show(CloudRedirectTitle, result.Error ?? "", error: true);

            await LoadAsync();
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    /// <summary>Replace cloud_redirect.dll with the latest. Reports "close Steam" if it's locked.</summary>
    [RelayCommand]
    private async Task UpdateCloudRedirect()
    {
        if (IsBusy || !CloudRedirectUnlocked) return;
        IsBusy = true;
        IsProgressIndeterminate = true;
        Progress = 0;
        try
        {
            var prog = new Progress<double?>(p =>
            {
                IsProgressIndeterminate = p is null;
                if (p is not null) Progress = p.Value * 100;
            });

            var result = await _unlocker.UpdateCloudRedirectAsync(prog);
            if (result.Success)
                _toast.Show(CloudRedirectTitle, Resources.Strings.Mode_CloudRedirect_Toast_Updated);
            else
                _toast.Show(CloudRedirectTitle, result.Error ?? "", error: true);

            await LoadAsync();
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    /// <summary>True if a hidden mode should be shown: its reveal-file exists, or it's the active mode.</summary>
    private bool IsModeVisible(ModeDefinition def)
    {
        if (def.HiddenUnlessFile is null) return true;          // always-visible mode
        if (_unlocker.SelectedMode == def.Mode) return true;    // active → keep visible even if file gone
        string? root = _steam.EffectivePath;
        return root is not null && File.Exists(Path.Combine(root, def.HiddenUnlessFile));
    }

    /// <summary>Rebuild the visible card list (hidden modes appear only when revealed). Preserves
    /// existing cards so their bound state isn't reset; adds/removes as visibility changes.</summary>
    private void SyncCards()
    {
        // Which modes are offered at all is ModeCatalog's decision, not this page's — see there for why
        // CloudRedirect and a retired mode are hidden on different terms. IsModeVisible is the separate,
        // per-install question of whether a reveal-file is present.
        var visible = ModeCatalog.Offered(_unlocker.Modes, _unlocker.SelectedMode)
            .Where(IsModeVisible)
            .ToList();

        // Remove cards no longer visible.
        for (int i = Cards.Count - 1; i >= 0; i--)
            if (!visible.Any(d => d.Mode == Cards[i].Mode))
                Cards.RemoveAt(i);

        // Add newly-visible cards in definition order.
        foreach (var def in visible)
            if (!Cards.Any(c => c.Mode == def.Mode))
            {
                int idx = visible.IndexOf(def);
                idx = Math.Min(idx, Cards.Count);
                Cards.Insert(idx, new ModeCardViewModel(def.Mode, def.DisplayName, def.Description));
            }
    }

    /// <summary>
    /// Page open / refresh. Only the ACTIVE mode is checked against GitHub (inactive cards just show
    /// "Switch to this" — switching re-fetches anyway, so pinging for them is wasted, and their hash
    /// check would be misleading since modes share filenames). Active check is cached briefly.
    /// </summary>
    private bool _detectionAttempted;

    public async Task LoadAsync(bool forceRefresh = false)
    {
        // First time with no mode selected: try to auto-detect an existing install by hashing the
        // on-disk DLLs against published releases, and adopt the match as active.
        if (!_detectionAttempted && _unlocker.SelectedMode is null)
        {
            _detectionAttempted = true;
            await _unlocker.DetectActiveModeAsync();
        }

        // Re-evaluate which cards are visible (hidden modes appear only when revealed).
        SyncCards();

        var active = _unlocker.SelectedMode;
        ModeHasRecord = _modeRemoval.CanUninstall;
        foreach (var card in Cards)
        {
            card.HasInstallRecord = ModeHasRecord;
            if (card.Mode == active)
            {
                card.StatusText = Resources.Strings.Mode_Checking;
                Apply(card, await _unlocker.GetStateAsync(card.Mode, forceRefresh));
            }
            else
            {
                // No network for inactive modes.
                Apply(card, new ModeState(card.Mode, ModeStatus.NotInstalled, IsActive: false, null));
            }
        }

        // AmethystTool sits with the Mode cards and refreshes with them.
        await Amethyst.LoadAsync(forceRefresh);
        OnPropertyChanged(nameof(CanUninstallAmethyst));

        // Bottom CloudRedirect add-on panel (locked unless Nightly BST is the active mode).
        await RefreshCloudRedirectAsync(forceRefresh);
    }

    private DateTime _lastCheck;

    [RelayCommand]
    private async Task CheckForUpdates()
    {
        // 30s cooldown: a forced refresh hits GitHub, and the unauthenticated API allows only 60/hr.
        if (IsBusy || DateTime.UtcNow - _lastCheck < TimeSpan.FromSeconds(30)) return;
        _lastCheck = DateTime.UtcNow;
        await LoadAsync(forceRefresh: true);
    }

    private void Apply(ModeCardViewModel card, ModeState s)
    {
        // Only the ACTIVE mode shows real install/update status. Inactive modes share filenames with
        // the active one (different contents), so their hash check is meaningless — show them simply
        // as the switch target instead of a misleading "Update available".
        if (s.Status == ModeStatus.Unknown)
            card.StatusText = Resources.Strings.Mode_StatusUnavailable;
        else if (!s.IsActive)
            card.StatusText = Resources.Strings.Mode_NotActive;
        else
            card.StatusText = s.Status switch
            {
                ModeStatus.NotInstalled => Resources.Strings.Mode_NotInstalled,
                ModeStatus.UpToDate => Resources.Strings.Mode_UpToDate,
                ModeStatus.UpdateAvailable => Resources.Strings.Mode_UpdateAvailable,
                _ => Resources.Strings.Mode_StatusUnavailable,
            };

        card.ButtonText = (s.IsActive, s.Status) switch
        {
            (true, ModeStatus.UpToDate) => Resources.Strings.Mode_Btn_Reinstall,
            (true, ModeStatus.UpdateAvailable) => Resources.Strings.Mode_Btn_Update,
            (true, _) => Resources.Strings.Mode_Btn_Install,
            (false, _) => Resources.Strings.Mode_Btn_Switch,
        };
        card.IsActive = s.IsActive;
    }

    // ── Install with confirmation ────────────────────────────────────

    /// <summary>Card button → ask the user to confirm (Steam will be closed) before doing anything.</summary>
    [RelayCommand]
    private void Install(ModeCardViewModel card)
    {
        if (IsBusy) return;
        _pending = PendingAction.Install;
        _pendingCard = card;
        ConfirmTitle = card.IsActive
            ? string.Format(Resources.Strings.Mode_Confirm_Reinstall, card.Title)
            : string.Format(Resources.Strings.Mode_Confirm_Switch, card.Title);
        ConfirmBody = Resources.Strings.Mode_Confirm_Body;
        IsConfirming = true;
    }

    /// <summary>
    /// Uninstall button → confirm first. The body says what an uninstall does that an install does not:
    /// the files are moved to a backup folder, and Steam stays closed afterwards.
    /// </summary>
    [RelayCommand]
    private void UninstallMode(ModeCardViewModel card)
    {
        if (IsBusy || !CanUninstallMode) return;
        _pending = PendingAction.Uninstall;
        _pendingCard = card;
        ConfirmTitle = string.Format(Resources.Strings.Mode_Confirm_Uninstall, card.Title);
        ConfirmBody = Resources.Strings.Mode_Confirm_Uninstall_Body;
        IsConfirming = true;
    }

    [RelayCommand]
    private void CancelConfirm()
    {
        IsConfirming = false;
        _pendingCard = null;
    }

    /// <summary>The overlay's primary button. Which action it runs was fixed when the overlay opened.</summary>
    [RelayCommand]
    private async Task Confirm()
    {
        IsConfirming = false;
        var card = _pendingCard;
        _pendingCard = null;

        // The AmethystTool actions carry no card — it is not one of Cards — so the card is checked per
        // branch rather than up front.
        switch (_pending)
        {
            case PendingAction.AmethystInstall: await RunAmethystInstall(); break;
            case PendingAction.AmethystUninstall: await RunAmethystUninstall(); break;
            case PendingAction.Uninstall when card is not null: await RunUninstall(); break;
            case PendingAction.Install when card is not null: await RunInstall(card.Mode); break;
        }
    }

    // ── AmethystTool ──────────────────────────────────────────

    /// <summary>
    /// AmethystTool's install/reinstall button → the same confirmation the Mode cards raise. The wording is
    /// theirs too: installing it takes the proxy-DLL slot the active Mode is using, which is what "switch"
    /// means on this page.
    /// </summary>
    [RelayCommand]
    private void InstallAmethyst()
    {
        if (IsBusy) return;
        _pending = PendingAction.AmethystInstall;
        _pendingCard = null;
        ConfirmTitle = string.Format(
            Amethyst.IsInstalled ? Resources.Strings.Mode_Confirm_Reinstall : Resources.Strings.Mode_Confirm_Switch,
            Resources.Strings.Amethyst_CardTitle);
        ConfirmBody = Resources.Strings.Mode_Confirm_Body;
        IsConfirming = true;
    }

    /// <summary>AmethystTool's Uninstall button → confirm, with the body that says the files are moved to a
    /// backup folder and Steam is left closed.</summary>
    [RelayCommand]
    private void UninstallAmethyst()
    {
        if (!CanUninstallAmethyst) return;
        _pending = PendingAction.AmethystUninstall;
        _pendingCard = null;
        ConfirmTitle = string.Format(Resources.Strings.Mode_Confirm_Uninstall, Resources.Strings.Amethyst_CardTitle);
        ConfirmBody = Resources.Strings.Mode_Confirm_Uninstall_Body;
        IsConfirming = true;
    }

    /// <summary>
    /// Hand off to <see cref="AmethystToolViewModel"/>, which owns the install and reports its own toast.
    ///
    /// <para>
    /// The page's busy flag is held for the whole call, and that is the point of routing it through here:
    /// AmethystTool and the Mode cards write the same two proxy DLLs next to <c>steam.exe</c>, and before
    /// this card moved onto this page nothing stopped a user from starting both. Steam itself is stopped and
    /// restarted inside the service, as it is for a Mode.
    /// </para>
    /// </summary>
    private async Task RunAmethystInstall()
    {
        if (IsBusy) return;
        IsBusy = true;
        IsProgressIndeterminate = true;
        Progress = 0;
        try
        {
            await Amethyst.InstallConfirmedAsync();
            await LoadAsync();
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    /// <summary>
    /// Remove AmethystTool through its own service, which back-fills a record for a copy installed before
    /// records existed and clears the version manifest afterwards. It keeps any proxy DLL the active Mode
    /// still claims — see <see cref="PluginRemovalService.ClaimedByOthers"/> — and says so in the toast.
    /// </summary>
    private async Task RunAmethystUninstall()
    {
        if (IsBusy) return;
        IsBusy = true;
        IsProgressIndeterminate = true;
        Progress = 0;
        try
        {
            await Amethyst.UninstallConfirmedAsync();
            await LoadAsync();
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    /// <summary>
    /// Take the active mode's recorded files back out of the Steam root and deselect it.
    ///
    /// <para>
    /// Steam is stopped by the removal and deliberately <b>not</b> restarted — unlike an install, which
    /// relaunches it. Putting a client back up onto proxy DLLs that were there a moment ago and now are not
    /// is the user's decision, not the uninstaller's, and the toast says Steam was closed.
    /// </para>
    /// </summary>
    private async Task RunUninstall()
    {
        if (IsBusy) return;
        IsBusy = true;
        IsProgressIndeterminate = true;
        Progress = 0;
        try
        {
            var outcome = await _modeRemoval.UninstallActiveModeAsync();
            _toast.Show(Resources.Strings.Mode_Toast_Uninstalled, RemovalMessage.Describe(outcome),
                error: outcome.Failed);

            await LoadAsync();
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    private async Task RunInstall(UnlockerMode mode)
    {
        if (IsBusy) return;
        IsBusy = true;
        IsProgressIndeterminate = true;
        Progress = 0;
        try
        {
            var prog = new Progress<double?>(p =>
            {
                IsProgressIndeterminate = p is null;
                if (p is not null) Progress = p.Value * 100;
            });

            // Correct order: kill Steam → write the files → relaunch Steam.
            // (Files can't be overwritten while Steam holds them open. CloudRedirect's CLI also closes
            //  Steam itself, but stopping first is harmless and keeps all modes consistent.)
            await Task.Run(_steam.StopSteam);

            var result = await _unlocker.InstallAsync(mode, prog);

            if (result.Success)
            {
                bool started = await Task.Run(_steam.StartSteam);
                _toast.Show(Resources.Strings.Mode_Toast_Updated, started
                    ? string.Format(Resources.Strings.Mode_Toast_Updated_Restarting, mode)
                    : string.Format(Resources.Strings.Mode_Toast_Updated_Start, mode));
            }
            else
            {
                // Install failed — bring Steam back up anyway so the user isn't left without it.
                await Task.Run(_steam.StartSteam);
                _toast.Show(Resources.Strings.Mode_Toast_InstallFailed, result.Error ?? Resources.Strings.Mode_Toast_InstallFailed_Body, error: true);
            }

            await LoadAsync();
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }
}
