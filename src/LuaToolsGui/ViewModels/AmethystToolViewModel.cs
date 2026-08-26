using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuaToolsGui.Services;

namespace LuaToolsGui.ViewModels;

/// <summary>
/// The AmethystTool card on the Plugin page. AmethystTool is a NATIVE injection plugin (a fork of
/// BetterSteamTools) whose files go into the Steam install root, so this is a sibling of — not part of —
/// the store-page plugin the rest of that page manages: different source, different payload, different
/// install target. Kept as its own view model rather than more flags on <see cref="PluginViewModel"/> so
/// neither one's state can be read as the other's.
/// </summary>
public sealed partial class AmethystToolViewModel(AmethystToolService installer, ToastService toast)
    : ObservableObject
{
    [ObservableProperty] private string _installedVersion = "—";
    [ObservableProperty] private string _latestVersion = "—";
    [ObservableProperty] private string _status = Resources.Strings.Plugin_Checking;

    /// <summary>Drives the leading status glyph: green check / amber warning / grey dismiss.</summary>
    [ObservableProperty] private bool _statusOk;
    [ObservableProperty] private bool _statusOutOfDate;
    [ObservableProperty] private bool _statusNotInstalled = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallButtonText))]
    [NotifyPropertyChangedFor(nameof(InstallIsPrimary))]
    [NotifyPropertyChangedFor(nameof(ShowUpToDate))]
    private bool _isInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallButtonText))]
    [NotifyPropertyChangedFor(nameof(InstallIsPrimary))]
    [NotifyPropertyChangedFor(nameof(ShowUpToDate))]
    private bool _updateAvailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotBusy))]
    [NotifyPropertyChangedFor(nameof(CanUninstall))]
    private bool _isBusy;

    /// <summary>
    /// True when Uninstall has something provable to work from. Gated on EVIDENCE, not on the files being
    /// present: without it there is nothing this app can show it placed, and removing by name would take
    /// out a proxy DLL some other tool owns. A copy installed by hand shows the hint below instead of an
    /// enabled button.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUninstall))]
    [NotifyPropertyChangedFor(nameof(ShowNoRecordHint))]
    private bool _hasInstallRecord;

    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isProgressIndeterminate;

    /// <summary>Non-null shows an info/warning line under the buttons (offline, GitHub rate limit).</summary>
    [ObservableProperty] private string? _statusLine;

    /// <summary>Non-null after an install that displaced existing files — says where they went.</summary>
    [ObservableProperty] private string? _backupLine;

    public bool NotBusy => !IsBusy;
    public bool CanUninstall => HasInstallRecord && !IsBusy;

    /// <summary>Installed, but before install records existed — explain why Uninstall is unavailable
    /// rather than showing a dead button.</summary>
    public bool ShowNoRecordHint => IsInstalled && !HasInstallRecord;

    /// <summary>Loud primary CTA only when there is something to do; a healthy "Reinstall" stays
    /// secondary, matching the store-page plugin card above it.</summary>
    public bool InstallIsPrimary => !IsInstalled || UpdateAvailable;

    public bool ShowUpToDate => IsInstalled && !UpdateAvailable;

    public string InstallButtonText => !IsInstalled
        ? Resources.Strings.Plugin_Btn_Install
        : UpdateAvailable ? Resources.Strings.Plugin_Btn_Update : Resources.Strings.Plugin_Btn_Reinstall;

    public Task LoadAsync(CancellationToken ct = default) => RefreshAsync(force: false, ct);

    private async Task RefreshAsync(bool force, CancellationToken ct)
    {
        var st = await installer.GetStatusAsync(force, ct);

        IsInstalled = st.Installed;
        UpdateAvailable = st.UpdateAvailable;
        InstalledVersion = st.InstalledTag
            ?? (st.Installed ? Resources.Strings.Plugin_Version_Unknown : "—");
        LatestVersion = st.Offline ? Resources.Strings.Plugin_Version_Offline : (st.LatestTag ?? "—");

        StatusOk = st.Installed && !st.UpdateAvailable;
        StatusOutOfDate = st.Installed && st.UpdateAvailable;
        StatusNotInstalled = !st.Installed;
        Status = !st.Installed
            ? Resources.Strings.Plugin_Status_NotInstalled
            : st.UpdateAvailable ? Resources.Strings.Plugin_Status_OutOfDate
                                 : Resources.Strings.Plugin_Status_UpToDate;

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

    /// <summary>Steam has to go down for the proxy DLLs to be replaceable, so say so before starting —
    /// same prompt the store-page plugin uses for the same reason.</summary>
    private static bool ConfirmSteamRestart() =>
        System.Windows.MessageBox.Show(
            Resources.Strings.Plugin_Confirm_RestartBody,
            Resources.Strings.Plugin_Confirm_RestartCaption,
            System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Warning) == System.Windows.MessageBoxResult.OK;

    [RelayCommand]
    private async Task Install(CancellationToken ct)
    {
        if (IsBusy) return;
        if (!ConfirmSteamRestart()) return;

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

    [RelayCommand]
    private async Task CheckForUpdates(CancellationToken ct)
    {
        if (IsBusy) return;
        IsBusy = true;
        IsProgressIndeterminate = true;
        try
        {
            await RefreshAsync(force: true, ct);
            if (StatusLine is null)
                toast.Show(Resources.Strings.Amethyst_CardTitle,
                    UpdateAvailable
                        ? Resources.Strings.Plugin_Toast_UpdateAvailable
                        : Resources.Strings.Plugin_Toast_UpToDate);
        }
        catch (OperationCanceledException) { /* page navigated away mid-check */ }
        finally { IsBusy = false; }
    }

    /// <summary>Uninstall asks for confirmation because it stops Steam and does not bring it back.</summary>
    private static bool ConfirmUninstall() =>
        System.Windows.MessageBox.Show(
            Resources.Strings.Removal_Confirm_Body,
            Resources.Strings.Removal_Confirm_Caption,
            System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Warning) == System.Windows.MessageBoxResult.OK;

    [RelayCommand]
    private async Task Uninstall(CancellationToken ct)
    {
        if (IsBusy || !HasInstallRecord) return;
        if (!ConfirmUninstall()) return;

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
