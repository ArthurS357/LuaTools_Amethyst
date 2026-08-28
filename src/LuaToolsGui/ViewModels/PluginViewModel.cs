using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuaToolsGui.Services;

namespace LuaToolsGui.ViewModels;

/// <summary>
/// One plugin source card on the Plugin page — one creator's build of the plugin, its state, and the one
/// action that applies to it.
///
/// <para>
/// Deliberately shaped like <see cref="ModeCardViewModel"/>: same card, same ACTIVE badge, same rule that
/// the action button belongs to the card rather than to the page. The two pages ask the user the same kind
/// of question — which of several mutually exclusive things should be the installed one — and answering it
/// should not look like two different products.
/// </para>
/// </summary>
public partial class PluginSourceCardViewModel(PluginSource source) : ObservableObject
{
    public PluginSource Source { get; } = source;

    /// <summary>"owner/repo" — the card's title, and the only form of a source ever shown or logged.</summary>
    public string Slug => Source.Slug;

    /// <summary>The creator line. Sources are told apart by who publishes them, not by a product name:
    /// both repositories build the same plugin, so the owner IS the distinguishing fact.</summary>
    public string Creator => string.Format(Resources.Strings.Plugin_Source_By, Source.Owner);

    /// <summary>The default entry in the catalogue, marked so a user who has never chosen can see which
    /// one they are on and why.</summary>
    public bool IsDefault { get; } = source == AppConfig.DefaultPluginSource;

    /// <summary>The user's current choice. Exactly one card carries it — it is read from one persisted
    /// slug, so nothing has to walk the list turning the others off.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowActivate))]
    private bool _isActive;

    /// <summary>Whether what is on disk actually came from this source.</summary>
    [ObservableProperty] private bool _isInstalled;

    [ObservableProperty] private string _latestVersion = "—";
    [ObservableProperty] private string _statusText = Resources.Strings.Plugin_Checking;

    /// <summary>Why this source cannot be installed from, or null. Shown on the card itself, so a user
    /// choosing between sources can see what is wrong with the one they are not on.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowActivate))]
    private string? _problemText;

    /// <summary>Switching is offered only where it can work: not on the card already active, and not on a
    /// source that has just told us it publishes nothing installable.</summary>
    public bool ShowActivate => !IsActive && ProblemText is null;
}

/// <summary>
/// "Plugin" page: the store-page plugin MANAGER. The app no longer bundles the frontend — it installs,
/// updates, and removes the LuaTools plugin (the "Add via LuaTools" button on Steam store pages) by
/// downloading it from GitHub releases via <see cref="PluginInstallerService"/>. One install path
/// (LuaLoader); if the Millennium mod is present it just coexists (and install disables Millennium's own
/// redundant luatools plugin).
///
/// <para>
/// More than one creator publishes a build of the plugin, so the page lists each source as its own card
/// and the user picks which is active — the same shape the Mode page uses for mutually exclusive
/// backends. The choice is persisted and honoured exactly: if the active source is broken the page says
/// so and offers the switch, and nothing installs a different creator's build on the user's behalf.
/// </para>
/// </summary>
public partial class PluginViewModel : ObservableObject
{
    private readonly PluginInstallerService _installer;
    private readonly ToastService _toast;

    public PluginViewModel(PluginInstallerService installer, ToastService toast)
    {
        _installer = installer;
        _toast = toast;

        // Built once from the compiled-in catalogue, then only ever updated in place. The list of sources
        // cannot change at runtime — only which one is active can.
        foreach (var source in AppConfig.PluginSources)
            Sources.Add(new PluginSourceCardViewModel(source));
    }

    /// <summary>One card per configured source, in catalogue order.</summary>
    public ObservableCollection<PluginSourceCardViewModel> Sources { get; } = [];

    [ObservableProperty] private string _installedVersion = "—";
    [ObservableProperty] private string _latestVersion = "—";
    [ObservableProperty] private string _frontendStatus = Resources.Strings.Plugin_Checking;
    [ObservableProperty] private string _dllStatus = Resources.Strings.Plugin_Checking;

    /// <summary>"owner/repo" the user has chosen to install from. Always known — it is a persisted choice,
    /// not the outcome of a lookup, so it is shown offline and when the source is broken too.</summary>
    [ObservableProperty] private string _activeSource = "—";

    /// <summary>Why the ACTIVE source cannot serve an install, or null. This is an error, not a nudge:
    /// nothing falls back to the other source, so saying so is the only thing that tells the user their
    /// plugin is stuck and that switching is the way out.</summary>
    [ObservableProperty] private string? _activeSourceError;

    // Per-component status flags — drive the colored status icons in the view (green check / amber
    // warning / grey info / grey dismiss). The *Status strings above stay the row label text.
    [ObservableProperty] private bool _frontendInstalled;
    [ObservableProperty] private bool _dllOk;
    [ObservableProperty] private bool _dllOutOfDate;

    /// <summary>
    /// The loader is installed but nothing could be compared against it — the active source is offline or
    /// publishes nothing installable, so there is no release digest to judge the file by.
    ///
    /// <para>
    /// Its own state rather than a shrug into <see cref="DllOutOfDate"/>. That flag paints an amber warning
    /// and the row reads "Out of date", which is a claim about a release we never saw: the DLL on disk may
    /// well be current. The one thing that is actually known here is that it is installed, so that is all
    /// this says.
    /// </para>
    /// </summary>
    [ObservableProperty] private bool _dllUnknown;

    [ObservableProperty] private bool _dllNotInstalled = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallButtonText))]
    [NotifyPropertyChangedFor(nameof(InstallIsPrimary))]
    [NotifyPropertyChangedFor(nameof(CanUninstall))]
    private bool _isInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallButtonText))]
    [NotifyPropertyChangedFor(nameof(InstallIsPrimary))]
    private bool _updateAvailable;

    /// <summary>True when the install button should be the loud green primary CTA — only when there's an
    /// actionable state (fresh install or an update). A healthy up-to-date "Reinstall" stays secondary.</summary>
    public bool InstallIsPrimary => !IsInstalled || UpdateAvailable;

    /// <summary>Green "Up to date" pill on the version line. Set from
    /// <see cref="PluginLoaderPolicy.ShowUpToDate"/> rather than computed here, so the rule — which is
    /// subtler than it looks — lives in one testable place instead of being restated at the binding.</summary>
    [ObservableProperty] private bool _showUpToDate;

    /// <summary>True when the Millennium mod is detected — shown as a "coexisting" info line, not a card.</summary>
    [ObservableProperty] private bool _millenniumCoexisting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotBusy))]
    [NotifyPropertyChangedFor(nameof(CanUninstall))]
    private bool _isBusy;
    public bool NotBusy => !IsBusy;
    public bool CanUninstall => IsInstalled && !IsBusy;

    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isProgressIndeterminate;

    /// <summary>Non-null shows a small info/error line under the buttons (e.g. offline).</summary>
    [ObservableProperty] private string? _statusLine;

    public string InstallButtonText => !IsInstalled
        ? Resources.Strings.Plugin_Btn_Install
        : UpdateAvailable ? Resources.Strings.Plugin_Btn_Update : Resources.Strings.Plugin_Btn_Reinstall;

    public Task LoadAsync() => RefreshAsync(force: false);

    private async Task RefreshAsync(bool force)
    {
        var st = await _installer.GetStatusAsync(force);
        // Every claim this page makes about how current the install is comes from PluginLoaderPolicy, not
        // from reading st.DllMatches / st.UpdateAvailable directly. Those two are false both when
        // everything is current and when nothing could be reached, so restating the rule here is how the
        // page ends up asserting a fact it never established.
        var loader = PluginLoaderPolicy.Loader(st);
        IsInstalled = PluginLoaderPolicy.IsInstalled(st);
        ShowUpToDate = PluginLoaderPolicy.ShowUpToDate(st);
        InstalledVersion = st.InstalledTag ?? (IsInstalled ? Resources.Strings.Plugin_Version_Unknown : "—");
        LatestVersion = st.Offline ? Resources.Strings.Plugin_Version_Offline : (st.LatestTag ?? "—");
        FrontendInstalled = st.FrontendInstalled;
        FrontendStatus = st.FrontendInstalled ? Resources.Strings.Plugin_Status_Installed : Resources.Strings.Plugin_Status_NotInstalled;
        // One enum, four mutually exclusive flags — the row cannot paint two icons or none.
        DllOk = loader is PluginLoaderState.UpToDate;
        DllOutOfDate = loader is PluginLoaderState.OutOfDate;
        DllUnknown = loader is PluginLoaderState.Unverifiable;
        DllNotInstalled = loader is PluginLoaderState.NotInstalled;
        DllStatus = loader switch
        {
            PluginLoaderState.NotInstalled => Resources.Strings.Plugin_Status_NotInstalled,
            PluginLoaderState.UpToDate => Resources.Strings.Plugin_Status_UpToDate,
            PluginLoaderState.OutOfDate => Resources.Strings.Plugin_Status_OutOfDate,
            // Installed, and that is genuinely all that is known — the reason why sits in the error box or
            // the offline line right next to this row, so the row itself does not need to guess.
            _ => Resources.Strings.Plugin_Status_Installed,
        };
        UpdateAvailable = st.UpdateAvailable;
        MillenniumCoexisting = st.MillenniumPresent;
        ActiveSource = st.ActiveSource;
        // A broken active source is reported and left broken. Offline is excluded because it is not a
        // fault of the source, and it already has its own line below.
        ActiveSourceError = st.ActiveSourceProblem is PluginSourceRejection.None || st.Offline
            ? null
            : PluginInstallerService.SourceProblemText(
                new PluginSourceProblem(default, st.ActiveSourceProblem, st.ActiveSourceProblemAsset));
        ApplySourceStatuses(st);
        // Offline takes priority (it's the more actionable/common case); the port warning is secondary and
        // only worth surfacing once we actually know install state, not on every offline check.
        StatusLine = st.Offline ? Resources.Strings.Plugin_Status_OfflineCheck
            : st.Port8080Busy ? Resources.Strings.Plugin_Status_Port8080Busy
            : null;
    }

    /// <summary>
    /// Fold the per-source report onto the cards. Matched by slug rather than by index so a card can never
    /// end up wearing another source's state — the one bug in this shape that would be silent and would
    /// mislead the user about which creator they are installing.
    /// </summary>
    private void ApplySourceStatuses(PluginStatus st)
    {
        foreach (var card in Sources)
        {
            var entry = st.Sources?.FirstOrDefault(s => s.Source == card.Source);
            if (entry is null) continue;

            card.IsActive = entry.IsActive;
            card.IsInstalled = entry.IsInstalled;
            card.LatestVersion = entry.LatestTag ?? "—";
            card.ProblemText = entry.Problem is PluginSourceRejection.None
                ? null
                : PluginInstallerService.SourceProblemText(
                    new PluginSourceProblem(entry.Source, entry.Problem, entry.ProblemAsset));
            card.StatusText = entry.IsInstalled ? Resources.Strings.Plugin_Source_Installed
                : entry.IsActive ? Resources.Strings.Plugin_Source_ActiveNotInstalled
                : Resources.Strings.Plugin_Source_Available;
        }
    }

    private static bool ConfirmUninstall() =>
        System.Windows.MessageBox.Show(
            Resources.Strings.Removal_Confirm_Body,
            Resources.Strings.Removal_Confirm_Caption,
            System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Warning) == System.Windows.MessageBoxResult.OK;

    private bool ConfirmSteamRestart()
    {
        var result = System.Windows.MessageBox.Show(
            Resources.Strings.Plugin_Confirm_RestartBody,
            Resources.Strings.Plugin_Confirm_RestartCaption,
            System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Warning);
        return result == System.Windows.MessageBoxResult.OK;
    }

    /// <summary>Its own prompt, naming the source: a switch replaces what is installed with a DIFFERENT
    /// creator's build, which the generic "restart Steam" wording does not say.</summary>
    private static bool ConfirmSwitch(PluginSource target) =>
        System.Windows.MessageBox.Show(
            string.Format(Resources.Strings.Plugin_Confirm_SwitchBody, target.Slug),
            Resources.Strings.Plugin_Confirm_SwitchCaption,
            System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Warning) == System.Windows.MessageBoxResult.OK;

    private IProgress<double?> MakeProgress() => new Progress<double?>(p =>
    {
        if (p is null) { IsProgressIndeterminate = true; }
        else { IsProgressIndeterminate = false; Progress = p.Value * 100; }
    });

    [RelayCommand]
    private async Task Install()
    {
        if (IsBusy) return;
        if (!ConfirmSteamRestart()) return;

        IsBusy = true;
        IsProgressIndeterminate = true;
        Progress = 0;
        try
        {
            var (ok, error) = await _installer.InstallAsync(MakeProgress());
            _toast.Show(Resources.Strings.Plugin_Toast_Title, ok
                ? Resources.Strings.Plugin_Toast_Installed
                : string.Format(Resources.Strings.Plugin_Toast_InstallFailed, error), error: !ok);
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync(force: true);
        }
    }

    /// <summary>
    /// Make one source the active one, by installing it. The service persists the choice only after the
    /// install has fully succeeded, so a failure here leaves the user on the source they already had
    /// rather than pointed at one that never landed.
    /// </summary>
    [RelayCommand]
    private async Task Activate(PluginSourceCardViewModel? card)
    {
        if (card is null || IsBusy || card.IsActive) return;
        if (!ConfirmSwitch(card.Source)) return;

        IsBusy = true;
        IsProgressIndeterminate = true;
        Progress = 0;
        try
        {
            var (ok, error) = await _installer.InstallSourceAsync(card.Source, MakeProgress());
            _toast.Show(Resources.Strings.Plugin_Toast_Title, ok
                ? string.Format(Resources.Strings.Plugin_Toast_SourceSwitched, card.Slug)
                : string.Format(Resources.Strings.Plugin_Toast_InstallFailed, error), error: !ok);
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync(force: true);
        }
    }

    [RelayCommand]
    private async Task Uninstall()
    {
        if (IsBusy || !IsInstalled) return;
        // Its own prompt, not the install one: uninstall stops Steam and — unlike install — does not bring
        // it back, so a message promising a restart would be wrong.
        if (!ConfirmUninstall()) return;

        IsBusy = true;
        IsProgressIndeterminate = true;
        try
        {
            var (ok, error) = await _installer.UninstallAsync();
            // Success says Steam was stopped: a user whose client vanished without explanation reads that
            // as the app having broken something.
            _toast.Show(Resources.Strings.Plugin_Toast_Title, ok
                ? Resources.Strings.Removal_Toast_RemovedSteamStopped
                : string.Format(Resources.Strings.Plugin_Toast_UninstallFailed, error), error: !ok);
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync(force: true);
        }
    }

    [RelayCommand]
    private async Task CheckForUpdates()
    {
        if (IsBusy) return;
        IsBusy = true;
        IsProgressIndeterminate = true;
        try
        {
            await RefreshAsync(force: true);
            if (StatusLine is null && ActiveSourceError is null)
                _toast.Show(Resources.Strings.Plugin_Toast_Title,
                    UpdateAvailable ? Resources.Strings.Plugin_Toast_UpdateAvailable : Resources.Strings.Plugin_Toast_UpToDate);
        }
        finally { IsBusy = false; }
    }
}
