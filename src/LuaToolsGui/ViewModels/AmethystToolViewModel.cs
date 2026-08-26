using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuaToolsGui.Services;

namespace LuaToolsGui.ViewModels;

/// <summary>
/// The AmethystTool card on the <b>Mode</b> page. AmethystTool is a fork of BetterSteamTools that injects
/// natively from the Steam install root, and two of the four files it places there — <c>dwmapi.dll</c> and
/// <c>xinput1_4.dll</c> — are the same proxy DLLs every other Mode writes. It is therefore a Mode in the
/// only sense that matters: mutually exclusive with them on disk. It was on the Plugin page, next to the
/// store-page frontend it shares nothing with.
///
/// <para>
/// Kept as its own view model rather than as a <see cref="ModeCardViewModel"/>: the Mode cards carry a
/// status line and one button, while this carries versions, a backup location and an install record. It is
/// not a <see cref="ModeViewModel"/> field either, so neither one's busy/installed state can be read as
/// the other's.
/// </para>
///
/// <para>
/// <b>Install and uninstall take no confirmation of their own.</b> <see cref="ModeViewModel"/> owns the
/// Steam-shutdown overlay that the Mode cards already use, and drives this card through the same one — so
/// the page asks for permission to close Steam in exactly one voice. That is also what serialises the two:
/// the page's busy flag is held for the whole operation, which is what stops an AmethystTool install and a
/// Mode install from writing the same proxy DLLs at once.
/// </para>
/// </summary>
public sealed partial class AmethystToolViewModel(AmethystToolService installer, ToastService toast)
    : ObservableObject
{
    [ObservableProperty] private string _installedVersion = "—";
    [ObservableProperty] private string _latestVersion = "—";
    [ObservableProperty] private string _status = Resources.Strings.Mode_Checking;

    /// <summary>Drives the leading status glyph: green check / amber warning / grey dismiss.</summary>
    [ObservableProperty] private bool _statusOk;
    [ObservableProperty] private bool _statusOutOfDate;
    [ObservableProperty] private bool _statusNotInstalled = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallButtonText))]
    [NotifyPropertyChangedFor(nameof(InstallIsPrimary))]
    [NotifyPropertyChangedFor(nameof(ShowUpToDate))]
    private bool _isInstalled;

    /// <summary>
    /// Whether the ACTIVE badge is shown — AmethystTool holds the proxy-DLL slot right now.
    ///
    /// <para>
    /// Separate from <see cref="IsInstalled"/>, and the badge is bound to THIS one. Installing a Mode
    /// overwrites the two proxy DLLs but leaves <c>AmethystTool.dll</c> and <c>amethysttool.toml</c> where
    /// they are, so the files stay "installed" while the slot has moved on; binding the badge to file
    /// presence is what used to leave this card and a Mode card both claiming to be active. See
    /// <see cref="Services.AmethystToolService.IsActive"/>.
    /// </para>
    /// </summary>
    [ObservableProperty] private bool _isActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallButtonText))]
    [NotifyPropertyChangedFor(nameof(InstallIsPrimary))]
    [NotifyPropertyChangedFor(nameof(ShowUpToDate))]
    private bool _updateAvailable;

    [ObservableProperty] private bool _isBusy;

    /// <summary>
    /// True when Uninstall has something provable to work from — read by
    /// <see cref="ModeViewModel.CanUninstallAmethyst"/>, which is what the button is actually bound to.
    /// Gated on EVIDENCE, not on the files being present: without it there is nothing this app can show it placed, and removing by name would take
    /// out a proxy DLL some other tool owns. A copy installed by hand shows the hint below instead of an
    /// enabled button.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoRecordHint))]
    private bool _hasInstallRecord;

    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isProgressIndeterminate;

    /// <summary>Non-null shows an info/warning line under the buttons (offline, GitHub rate limit).</summary>
    [ObservableProperty] private string? _statusLine;

    /// <summary>Non-null after an install that displaced existing files — says where they went.</summary>
    [ObservableProperty] private string? _backupLine;

    /// <summary>Installed, but before install records existed — explain why Uninstall is unavailable
    /// rather than showing a dead button.</summary>
    public bool ShowNoRecordHint => IsInstalled && !HasInstallRecord;

    /// <summary>Loud primary CTA only when there is something to do; a healthy "Reinstall" stays
    /// secondary.</summary>
    public bool InstallIsPrimary => !IsInstalled || UpdateAvailable;

    public bool ShowUpToDate => IsInstalled && !UpdateAvailable;

    public string InstallButtonText => !IsInstalled
        ? Resources.Strings.Mode_Btn_Install
        : UpdateAvailable ? Resources.Strings.Mode_Btn_Update : Resources.Strings.Mode_Btn_Reinstall;

    /// <summary>Page open, or the page's "Check for updates". <paramref name="forceRefresh"/> is what
    /// decides whether GitHub is asked again or the cached release is reused.</summary>
    public Task LoadAsync(bool forceRefresh = false, CancellationToken ct = default) =>
        RefreshAsync(forceRefresh, ct);

    private async Task RefreshAsync(bool force, CancellationToken ct)
    {
        var st = await installer.GetStatusAsync(force, ct);

        IsInstalled = st.Installed;
        IsActive = installer.IsActive;
        UpdateAvailable = st.UpdateAvailable;
        InstalledVersion = st.InstalledTag
            ?? (st.Installed ? Resources.Strings.Plugin_Version_Unknown : "—");
        LatestVersion = st.Offline ? Resources.Strings.Plugin_Version_Offline : (st.LatestTag ?? "—");

        StatusOk = st.Installed && !st.UpdateAvailable;
        StatusOutOfDate = st.Installed && st.UpdateAvailable;
        StatusNotInstalled = !st.Installed;
        Status = !st.Installed
            ? Resources.Strings.Mode_NotInstalled
            : st.UpdateAvailable ? Resources.Strings.Mode_UpdateAvailable
                                 : Resources.Strings.Mode_UpToDate;

        HasInstallRecord = installer.CanUninstall;
        StatusLine = st.Offline ? Resources.Strings.Plugin_Status_OfflineCheck : null;
        BackupLine = installer.LastBackupDirectory is { } dir
            ? string.Format(Resources.Strings.Amethyst_Backup_Line, dir)
            : null;
    }

    private IProgress<double?> MakeProgress() => new Progress<double?>(p =>
    {
        if (p is null) { IsProgressIndeterminate = true; }
        else { IsProgressIndeterminate = false; Progress = p.Value * 100; }
    });

    /// <summary>
    /// Run the install. The caller has already confirmed closing Steam — see the class remarks for why the
    /// prompt lives on <see cref="ModeViewModel"/> and not here.
    /// </summary>
    public async Task InstallConfirmedAsync(CancellationToken ct = default)
    {
        if (IsBusy) return;

        IsBusy = true;
        IsProgressIndeterminate = true;
        Progress = 0;
        try
        {
            var (ok, error) = await installer.InstallAsync(MakeProgress(), ct);
            toast.Show(Resources.Strings.Amethyst_CardTitle, ok
                ? Resources.Strings.Amethyst_Toast_Installed
                : string.Format(Resources.Strings.Amethyst_Toast_Failed, error), error: !ok);
        }
        catch (OperationCanceledException) { /* the page went away; nothing to report */ }
        finally
        {
            IsBusy = false;
            await RefreshAsync(force: true, CancellationToken.None);
        }
    }

    /// <summary>
    /// Remove what the install recorded. Already confirmed by the caller. Still gated on
    /// <see cref="HasInstallRecord"/>: the button is only one of the two ways in, and removing without a
    /// record would mean deleting a proxy DLL by name that some other tool may own.
    /// </summary>
    public async Task UninstallConfirmedAsync(CancellationToken ct = default)
    {
        if (IsBusy || !HasInstallRecord) return;

        IsBusy = true;
        IsProgressIndeterminate = true;
        try
        {
            // Through the service, not straight to PluginRemovalService: this is the path that back-fills a
            // record for a copy installed before records existed, and that clears the version manifest
            // afterwards so the card stops reporting a tag for something no longer there.
            var outcome = await installer.UninstallAsync(ct);
            toast.Show(Resources.Strings.Amethyst_CardTitle, RemovalMessage.Describe(outcome),
                error: outcome.Failed);
        }
        catch (OperationCanceledException) { /* page navigated away mid-removal */ }
        finally
        {
            IsBusy = false;
            await RefreshAsync(force: false, CancellationToken.None);
        }
    }

}
