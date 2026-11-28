using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuaToolsGui.Services;

namespace LuaToolsGui.ViewModels;

/// <summary>One installed lua file (a game added to Steam via config\stplug-in\&lt;appid&gt;.lua).</summary>
public partial class LuaTileViewModel : ObservableObject
{
    public long AppId { get; }
    public string AppIdText { get; }
    public string FilePath { get; }
    public DateTime AddedAt { get; }
    // Invariant culture so the month is always the 3-letter abbreviation ("Jun", not "June" or a
    // localized long form), 2-digit year ("'26") — keeps the combined "Added … • Released …" line
    // short enough to fit the card.
    public string AddedLabel { get; }

    [GeneratedRegex(@"\b(19|20)(\d{2})\b")]
    private static partial Regex FourDigitYearRegex();

    private string? _lastReleaseDateRaw;

    /// <summary>Steam release date for the card (e.g. "Released Feb 24, 2022"), or "" until details
    /// are cached. Set by <see cref="UpdateReleaseLabel"/> once the appdetails blob is available.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AddedReleaseLabel))]
    private string _releaseLabel = "";

    /// <summary>Single-line combined label for the card: "Added … • Released …" (just the Added part
    /// until the release date is known). Keeps both dates on one row so they don't clip the card.</summary>
    public string AddedReleaseLabel =>
        string.IsNullOrEmpty(ReleaseLabel) ? AddedLabel : $"{AddedLabel}  •  {ReleaseLabel}";

    private int _resolving; // 0/1 in-progress guard; retries on later views while Cover is still null
    private bool _nameIsPlaceholder; // cleared once a real name is resolved, so we stop re-fetching

    [ObservableProperty] private string _name;
    [ObservableProperty] private ImageSource? _cover;
    [ObservableProperty] private bool _isSelected;

    /// <summary>True once this app's full details are cached (so filters/sort can use it).</summary>
    [ObservableProperty] private bool _detailsLoaded;

    /// <summary>Which stored lua this game is currently running ("Default", "Build 18234567", …), shown
    /// on the Builds page's game list. Null on the Manage page, which doesn't display it.</summary>
    [ObservableProperty] private string? _variantBadge;

    /// <summary>Builds page only: the live lua is gone but stored builds keep the game listed. Shown as a
    /// hint so the row does not look like an installed game; "Remove game" stays the way to drop it.</summary>
    [ObservableProperty] private bool _savedBuildsOnly;

    /// <summary>Depots grid: true while <c>config\stplug-in\&lt;appid&gt;.lua</c> exists on disk.</summary>
    [ObservableProperty] private bool _hasActiveLua = true;

    /// <summary>Depots grid: true when at least one stored variant or loose build file exists.</summary>
    [ObservableProperty] private bool _hasStoredBuilds;

    /// <summary>Depots grid: number of stored presets/variants in <see cref="LuaVault"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPresetsCount))]
    [NotifyPropertyChangedFor(nameof(PresetsCountLabel))]
    private int _variantCount;

    public bool HasPresetsCount => VariantCount > 0;

    public string PresetsCountLabel => VariantCount == 1
        ? Resources.Strings.Builds_Card_OnePreset
        : string.Format(System.Globalization.CultureInfo.CurrentCulture,
            Resources.Strings.Builds_Card_PresetsCount, VariantCount);

    /// <summary>Whether Steam has this game's files. Starts Unknown and is filled in by
    /// <see cref="ManageViewModel.RefreshInstallStatesAsync"/>; Unknown is a usable state, not a broken one
    /// (see <see cref="SteamLaunchPolicy"/>), so the Play button works before it resolves.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayLabel))]
    private SteamGameInstallState _installState = SteamGameInstallState.Unknown;

    /// <summary>True when Steam's manifest says the game is fully on disk (drives the green "Installed"
    /// badge). Stricter than <see cref="InstallState"/>, which also counts a game mid-download.</summary>
    [ObservableProperty] private bool _isInstalled;

    /// <summary>Tooltip of the save icon ("Saves detected in: …"), or null while no save location is known.
    /// Filled in by <see cref="ManageViewModel.RefreshSaveLocationsAsync"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSaveLocations))]
    private string? _saveLocationsTooltip;

    public bool HasSaveLocations => !string.IsNullOrEmpty(SaveLocationsTooltip);

    /// <summary>"Play" or "Install", whichever the button is actually going to do. Read from
    /// <see cref="SteamLaunchPolicy.IntentFor"/> rather than restated here, so the label cannot promise one
    /// verb while the command performs the other.</summary>
    public string PlayLabel =>
        SteamLaunchPolicy.IntentFor(InstallState) == SteamLaunchIntent.Install
            ? Resources.Strings.Manage_Action_Install
            : Resources.Strings.Manage_Action_Play;

    /// <summary>Populate <see cref="ReleaseLabel"/> from cached app-details (no-op until they exist).
    /// Shows Steam's own date string ("Released 24 Feb, 2022"); blank for unreleased/unknown.</summary>
    public void UpdateReleaseLabel(SteamAppInfoCache appInfo) =>
        UpdateReleaseLabel(appInfo.GetFilterData(AppId));

    internal void UpdateReleaseLabel(AppFilterData? data)
    {
        string? text = data?.ReleaseDateText;
        if (_lastReleaseDateRaw == text && (!string.IsNullOrEmpty(ReleaseLabel) || string.IsNullOrWhiteSpace(text)))
            return;

        _lastReleaseDateRaw = text;
        if (string.IsNullOrWhiteSpace(text))
        {
            ReleaseLabel = "";
            return;
        }

        // Shorten a 4-digit year to 2 digits ("May 26, 2025" → "May 26, '25") to keep the combined
        // line on one row. Leaves non-date wording ("Coming soon") untouched.
        string formatted = FourDigitYearRegex().Replace(text, "'$2");
        ReleaseLabel = $"Released {formatted}";
    }

    /// <summary>Raised when IsSelected changes so the page can update its selection count/bar.</summary>
    public Action? SelectionChanged { get; set; }
    partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke();

    public LuaTileViewModel(long appId, string filePath, DateTime addedAt, string name, bool nameIsPlaceholder)
    {
        AppId = appId;
        AppIdText = appId.ToString(CultureInfo.InvariantCulture);
        FilePath = filePath;
        AddedAt = addedAt;
        AddedLabel = "Added " + addedAt.ToString(@"MMM d, \'yy", CultureInfo.InvariantCulture);
        _name = name;
        _nameIsPlaceholder = nameIsPlaceholder;
        // Cover stays blank until resolved — avoids flashing Steam's "Header Capsule" placeholder.
    }

    /// <summary>
    /// For a VISIBLE tile: ensure the cover file (warming if needed), decode off-UI, show it.
    /// Reuses decoded frozen bitmaps from <see cref="CoverCache"/> across Manage/Depots switches.
    /// </summary>
    public async Task EnsureResolvedAsync(SteamAppInfoCache appInfo, CoverCache covers)
    {
        // Refresh the release label whenever a card is (re)shown — its details may have backfilled
        // since the last ApplyFilter pass. Cheap (reads the memoized filter cache).
        if (string.IsNullOrEmpty(ReleaseLabel)) OnUi(() => UpdateReleaseLabel(appInfo));

        if (Cover is not null) return;
        if (!_nameIsPlaceholder && covers.TryGetDecoded(AppId) is { } memCover)
        {
            OnUi(() => Cover = memCover);
            return;
        }

        if (Interlocked.Exchange(ref _resolving, 1) == 1) return;
        try
        {
            string? local = await ResolveCoverFileAsync(AppId, appInfo, covers,
                name => { if (_nameIsPlaceholder) SetName(name); });

            // If the cover came from the CDN guess (fast path), the name was never resolved.
            // For a still-placeholder name, fetch appdetails once (throttled, saved to /details).
            if (_nameIsPlaceholder)
            {
                var info = await appInfo.GetCachedAsync(AppId) ?? await appInfo.ResolveAsync(AppId);
                if (!string.IsNullOrWhiteSpace(info?.Name)) SetName(info!.Name);
            }

            if (local is null) return;
            var image = covers.TryGetDecoded(AppId) ?? await Task.Run(() => covers.GetOrDecode(AppId, local));
            if (image is not null) OnUi(() => Cover = image);
        }
        finally
        {
            Interlocked.Exchange(ref _resolving, 0);
        }
    }

    /// <summary>
    /// Ensure the cover image FILE exists on disk (no decode, no UI). Used by the background prefetch
    /// so the visible page can decode instantly. Order: disk → CDN guess → appdetails header_image.
    /// Only flags "no cover" when Steam definitively has none — never on a transient rate-limit.
    /// </summary>
    public static async Task<string?> ResolveCoverFileAsync(
        long appId, SteamAppInfoCache appInfo, CoverCache covers, Action<string>? onName = null)
    {
        string? local = covers.GetLocalPath(appId);
        if (local is not null) return local;
        if (covers.IsKnownMissing(appId)) return null;

        // Fast path: predictable CDN URL. The grey "Header Capsule" placeholder it can serve for newer
        // apps is fingerprinted + rejected by CoverCache, so this falls through to header_image below.
        local = await covers.EnsureAsync(appId, SteamAppInfoCache.GuessHeaderImageUrl(appId));
        if (local is not null) return local;

        // Slow path: appdetails header_image (throttled) + optional name backfill.
        var info = await appInfo.GetCachedAsync(appId) ?? await appInfo.ResolveAsync(appId);
        if (onName is not null && !string.IsNullOrWhiteSpace(info?.Name)) onName(info!.Name);
        if (!string.IsNullOrWhiteSpace(info?.HeaderImage))
            local = await covers.EnsureAsync(appId, info!.HeaderImage!);

        // info != null means appdetails answered (so an empty header = genuinely no cover).
        // info == null means we couldn't reach it (rate-limited/offline) — don't give up, retry later.
        if (local is null && info is not null) covers.MarkMissing(appId);
        return local;
    }

    private void SetName(string name)
    {
        _nameIsPlaceholder = false; // got a real name — don't re-resolve
        OnUi(() => Name = name);
    }

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }

    public bool Matches(string q) =>
        Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
        AppIdText.Contains(q, StringComparison.OrdinalIgnoreCase);
}

public partial class ManageViewModel : PagedListViewModel<LuaTileViewModel>
{
    private readonly SteamService _steam;
    private readonly SteamAppListCache _appList;
    private readonly SteamAppInfoCache _appInfo;
    private readonly CoverCache _covers;
    private readonly ToastService _toast;
    private readonly SettingsService _settings;
    private readonly SteamlessService _steamless;
    private readonly SteamGameLauncher _launcher;
    private readonly SteamLibraryService _library;
    private readonly LuaVault _vault;
    private readonly GameCleanupService _cleanup;

    private List<LuaTileViewModel> _all = [];
    private CancellationTokenSource? _prefetchCts;
    internal Task PrefetchTask { get; private set; } = Task.CompletedTask;
    internal void CancelBackgroundPrefetch() => _prefetchCts?.Cancel();

    /// <summary>Set by App so "Update" can navigate to the Add page pre-seeded with the appid.</summary>
    public Action<long>? NavigateToAdd { get; set; }

    /// <summary>Set by App so "Manage Build" can open this game on the Builds page.</summary>
    public Action<long>? NavigateToBuilds { get; set; }

    // Paging (Items/PageSize/CurrentPage/…), the filtered slice, refresh cooldown, IsLoading/EmptyMessage
    // and the empty-state gating all live in PagedListViewModel<LuaTileViewModel>.

    [ObservableProperty] private string _searchText = "";

    // ── View mode (Grid vs Steam-style compact List) ──────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGridMode))]
    [NotifyPropertyChangedFor(nameof(IsListMode))]
    [NotifyPropertyChangedFor(nameof(GridItems))]
    [NotifyPropertyChangedFor(nameof(ListItems))]
    private LibraryViewMode _viewMode = LibraryViewMode.Grid;

    public bool IsGridMode => ViewMode == LibraryViewMode.Grid;
    public bool IsListMode => ViewMode == LibraryViewMode.List;
    public ObservableCollection<LuaTileViewModel>? GridItems => IsGridMode ? Items : null;
    public ObservableCollection<LuaTileViewModel>? ListItems => IsListMode ? Items : null;

    protected override void OnPageSliced(IReadOnlyList<LuaTileViewModel> slice)
    {
        OnPropertyChanged(nameof(GridItems));
        OnPropertyChanged(nameof(ListItems));
    }

    partial void OnViewModeChanged(LibraryViewMode value)
    {
        if (_settings.ManageViewMode != value)
            _settings.ManageViewMode = value;
    }

    // ── Filters & sort (Manage page) ─────────────────────────────────
    [ObservableProperty] private bool _isFilterPanelOpen;

    // "Any" sentinel = no filter for that category.
    public const string AnyOption = "Any";

    // Dropdown option lists. Seeded with "Any" so the default shows before library details load
    // (PopulateFilterOptions re-adds the real values, keeping "Any" first).
    public ObservableCollection<string> TypeOptions { get; } = [AnyOption];
    public ObservableCollection<string> GenreOptions { get; } = [AnyOption];
    public ObservableCollection<string> YearOptions { get; } = [AnyOption];
    public ObservableCollection<string> PriceOptions { get; } = [AnyOption, "Free", "Paid"];
    // Static like Price: stored VALUES stay English (localized for display via FilterOptionDisplayConverter).
    public ObservableCollection<string> ContentOptions { get; } = [AnyOption, "Hide adult", "Adult only"];
    public const string InstalledFirstOption = "Installed first";

    public ObservableCollection<string> SortOptions { get; } =
        [InstalledFirstOption, "Recently added", "Name (A–Z)", "Release date (newest)", "Metacritic", "Most reviewed"];

    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasActiveFilters))] private string _selectedType = AnyOption;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasActiveFilters))] private string _selectedGenre = AnyOption;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasActiveFilters))] private string _selectedYear = AnyOption;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasActiveFilters))] private string _selectedPrice = AnyOption;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasActiveFilters))] private string _selectedContent = AnyOption;
    [ObservableProperty] private string _selectedSort = InstalledFirstOption;

    // Pagination (PageSizeOptions/SelectedPageSize/CurrentPage/PageNumbers/Prev-Next-GoToPage/…) is
    // inherited from PagedListViewModel<LuaTileViewModel>; page size persists via SavePageSizeSetting below.
    protected override void SavePageSizeSetting(int size) => _settings.ManagePageSize = size;

    public bool HasActiveFilters =>
        SelectedType != AnyOption || SelectedGenre != AnyOption || SelectedYear != AnyOption ||
        SelectedPrice != AnyOption || SelectedContent != AnyOption;

    // How many library apps still lack cached details while a filter is active (can't be filtered yet).
    // The text updates as the count ticks down. HasPendingDetails (which drives the spinner's
    // Visibility) is a SEPARATE bool that only flips at the 0/non-0 boundary — so a count change
    // doesn't re-fire the visibility binding and restart the ProgressRing animation.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilterPendingText))]
    private int _pendingDetailsCount;

    [ObservableProperty] private bool _hasPendingDetails;

    partial void OnPendingDetailsCountChanged(int value) => HasPendingDetails = value > 0;

    public string FilterPendingText =>
        string.Format(Resources.Strings.Manage_FetchingDetails, PendingDetailsCount);

    partial void OnSelectedTypeChanged(string value) => ApplyFilter();
    partial void OnSelectedGenreChanged(string value) => ApplyFilter();
    partial void OnSelectedYearChanged(string value) => ApplyFilter();
    partial void OnSelectedPriceChanged(string value) => ApplyFilter();
    partial void OnSelectedContentChanged(string value) => ApplyFilter();
    partial void OnSelectedSortChanged(string value) => ApplyFilter();

    // IsLoading, EmptyMessage and the HasItems/ShowItems/IsEmpty gating are inherited from the base.

    // ── Detail flyout ───────────────────────────────────────────────
    // The depot/DLC breakdown that used to live here now belongs to the Builds page (BuildsViewModel),
    // where it can also show manifest pins per build. This flyout is cover + title + actions only.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDetailOpen))]
    private LuaTileViewModel? _selectedTile;

    public bool IsDetailOpen => SelectedTile is not null;

    // ── Multi-select ────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSelecting))]
    [NotifyPropertyChangedFor(nameof(SelectionLabel))]
    private int _selectedCount;

    public bool IsSelecting => SelectedCount > 0;
    public string SelectionLabel => string.Format(Resources.Strings.Manage_SelectionLabel, SelectedCount);

    public ManageViewModel(SteamService steam, SteamAppListCache appList, SteamAppInfoCache appInfo,
        CoverCache covers, ToastService toast, SettingsService settings,
        SteamlessService steamless, SteamGameLauncher launcher, SteamLibraryService library,
        LuaVault vault, GameCleanupService cleanup)
    {
        _steam = steam;
        _appList = appList;
        _appInfo = appInfo;
        _covers = covers;
        _toast = toast;
        _settings = settings;
        _steamless = steamless;
        _launcher = launcher;
        _library = library;
        _vault = vault;
        _cleanup = cleanup;
        ViewMode = settings.ManageViewMode;
        InitPageSize(settings.ManagePageSize);
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    /// <summary>Called by the view as a tile scrolls into view — resolves its cover (cached after).</summary>
    public void ResolveTile(LuaTileViewModel tile) => _ = tile.EnsureResolvedAsync(_appInfo, _covers);

    /// <summary>
    /// Open a game's detail flyout by appid (Home "recently added" / Add page "Reveal"). Re-scans if
    /// the tile isn't found — the library may be stale (e.g. the game was removed then re-added, or
    /// this VM hasn't loaded yet). If it's genuinely gone, tell the user instead of failing silently.
    /// </summary>
    public async Task OpenDetailForAppIdAsync(long appId)
    {
        var tile = _all.FirstOrDefault(t => t.AppId == appId);
        if (tile is null)
        {
            await LoadAsync();
            tile = _all.FirstOrDefault(t => t.AppId == appId);
        }

        if (tile is not null) await OpenDetailAsync(tile);
        else _toast.Show(Resources.Strings.Manage_Toast_NotFound_Title, Resources.Strings.Manage_Toast_NotFound_Body, error: true);
    }

    // ── Tile actions ────────────────────────────────────────────────

    /// <summary>Blurb + studio + genres for the open flyout, read from the cached appdetails blob.
    /// Null when this game has no details cached (the section collapses).</summary>
    [ObservableProperty] private AppOverview? _overview;

    /// <summary>Open the detail flyout for a tile (cover, title, game info, actions). Concurrent: the
    /// body re-checks SelectedTile after every await, so opening another game while the previous one's
    /// details are still loading must switch immediately instead of being silently ignored.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task OpenDetailAsync(LuaTileViewModel tile)
    {
        SelectedTile = tile;
        Overview = null;
        var overview = await _appInfo.GetOverviewAsync(tile.AppId);
        if (SelectedTile != tile) return; // superseded: don't spend a rate-limited fetch on a game left behind
        Overview = overview;

        // The flyout binds its cover to SelectedTile.Cover. When opened from outside Manage
        // (Home → game detail) the tile was never rendered/scrolled into view, so its cover
        // was never resolved and the pullout image is blank. Ensure it here (idempotent).
        await tile.EnsureResolvedAsync(_appInfo, _covers);
        if (SelectedTile != tile) return;

        // No cached blob yet (the background backfill hasn't reached this game) — fetch it at
        // interactive priority, then fill the section in. Re-checked afterwards because the user can
        // close the flyout or switch games while that request is in flight.
        if (Overview is null && await _appInfo.EnsureFullDetailsAsync(tile.AppId) && SelectedTile == tile)
        {
            var fetched = await _appInfo.GetOverviewAsync(tile.AppId);
            if (SelectedTile == tile) Overview = fetched;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCloseDetail))]
    private void CloseDetail()
    {
        if (IsRemoving) return;
        SelectedTile = null;
        Overview = null;
    }

    private bool CanCloseDetail() => !IsRemoving;

    /// <summary>
    /// Run the game through Steam, or put it in to download when Steam does not have it yet — starting
    /// Steam first if it is not up. Which of those happens is <see cref="SteamLaunchPolicy"/>'s decision;
    /// this only reports the result.
    /// </summary>
    [RelayCommand]
    private async Task PlayAsync(LuaTileViewModel? tile, CancellationToken ct)
    {
        if (tile is null) return;

        SteamLaunchOutcome outcome;
        try
        {
            outcome = await _launcher.LaunchAsync(tile.AppId, ct);
        }
        catch (OperationCanceledException)
        {
            return; // navigated away mid-launch — the user is no longer waiting on this
        }

        switch (outcome)
        {
            case SteamLaunchOutcome.Installing:
                tile.InstallState = SteamGameInstallState.Installed; // Steam now has a manifest for it
                _toast.Show(Resources.Strings.Manage_Play_Installing_Title,
                            string.Format(Resources.Strings.Manage_Play_Installing_Body, tile.Name));
                break;

            case SteamLaunchOutcome.SteamUnavailable:
                _toast.Show(Resources.Strings.Manage_Play_Failed_Title,
                            Resources.Strings.Manage_Play_SteamUnavailable_Body, error: true);
                break;

            case SteamLaunchOutcome.InvalidAppId:
            case SteamLaunchOutcome.Failed:
                _toast.Show(Resources.Strings.Manage_Play_Failed_Title,
                            Resources.Strings.Manage_Play_Failed_Body, error: true);
                break;

            case SteamLaunchOutcome.Launched:
                break; // Steam takes over the screen — a toast on top of it is noise
        }
    }

    /// <summary>
    /// Fill in every tile's <see cref="LuaTileViewModel.InstallState"/> from one scan of Steam's library
    /// manifests. Best-effort and non-blocking: a tile left Unknown still has a working Play button.
    /// </summary>
    public async Task RefreshInstallStatesAsync()
    {
        var tiles = _all;
        if (tiles.Count == 0) return;

        var (installed, fully) = await Task.Run(() => (_library.GetInstalledAppIds(), _library.GetFullyInstalledAppIds()));

        foreach (var tile in tiles)
        {
            tile.InstallState = installed.Contains(tile.AppId)
                ? SteamGameInstallState.Installed
                : SteamGameInstallState.NotInstalled;
            tile.IsInstalled = fully.Contains(tile.AppId);
        }

        // The scan lands after LoadAsync already rendered the list, and the default sort keys on
        // IsInstalled — re-sort now, staying on the user's current page.
        OnUi(() => ApplyFilter(resetPage: false));

        await RefreshSaveLocationsAsync();
    }

    /// <summary>Open this game on the Builds page (switch build, inspect depots/manifests, edit).</summary>
    [RelayCommand]
    private void ManageBuild(LuaTileViewModel tile) => NavigateToBuilds?.Invoke(tile.AppId);

    /// <summary>Set by App — opens the launch-option editor for a game (appid, name).</summary>
    public Action<long, string>? OpenLaunchOptions { get; set; }

    /// <summary>Edit this game's Steam launch options (the entries behind the Play button).</summary>
    [RelayCommand]
    private void EditLaunchOptions(LuaTileViewModel tile) => OpenLaunchOptions?.Invoke(tile.AppId, tile.Name);

    /// <summary>Set by App — opens the list of every game whose launch options were edited.</summary>
    public Action? OpenLaunchOverrides { get; set; }

    [RelayCommand]
    private void ShowLaunchOverrides() => OpenLaunchOverrides?.Invoke();

    /// <summary>Set by App — opens the save-backup dialog for a game (appid, name).</summary>
    public Action<long, string>? OpenSaveBackup { get; set; }

    /// <summary>Set by App — friendly names of the places this game keeps saves (game name, appid), empty when
    /// none. Runs on a background thread, once per card.</summary>
    public Func<string, long, IReadOnlyList<string>>? DetectSaveNames { get; set; }

    /// <summary>Back up / restore this game's saves. Always available: with nothing detected, the dialog is where a
    /// folder is added by hand.</summary>
    [RelayCommand]
    private void SaveBackup(LuaTileViewModel tile) => OpenSaveBackup?.Invoke(tile.AppId, tile.Name);

    /// <summary>Fill each card's save icon from <see cref="DetectSaveNames"/>. Best-effort, off the UI thread.</summary>
    public async Task RefreshSaveLocationsAsync()
    {
        var detect = DetectSaveNames;
        var tiles = _all;
        if (detect is null || tiles.Count == 0) return;

        var games = tiles.Select(static t => (Tile: t, t.Name, t.AppId)).ToList();
        var found = await Task.Run(() => games.Select(g => (g.Tile, Names: detect(g.Name, g.AppId))).ToList());

        OnUi(() =>
        {
            foreach (var (tile, names) in found)
                tile.SaveLocationsTooltip = names.Count == 0
                    ? null
                    : string.Format(Resources.Strings.Manage_SaveIcon_Tooltip, string.Join(", ", names));
        });
    }

    /// <summary>Set by App — runs the "import a save zip" flow for a game (appid, name).</summary>
    public Action<long, string>? OpenSaveImport { get; set; }

    /// <summary>Restore a save from an external .zip. Always available: the typical case is a game that was
    /// just downloaded again, so it has no saves or backups yet.</summary>
    [RelayCommand]
    private void ImportSave(LuaTileViewModel tile) => OpenSaveImport?.Invoke(tile.AppId, tile.Name);

    [RelayCommand]
    private static void OpenStorePage(LuaTileViewModel tile) =>
        SteamService.OpenUrl($"steam://store/{tile.AppId}");

    [RelayCommand]
    private static void OpenInSteam(LuaTileViewModel tile) =>
        SteamService.OpenUrl($"steam://nav/games/details/{tile.AppId}");

    [RelayCommand]
    private static void RevealFile(LuaTileViewModel tile) =>
        SteamService.RevealInExplorer(tile.FilePath);

    [RelayCommand]
    private static void CopyAppId(LuaTileViewModel tile) =>
        Clipboard.SetText(tile.AppId.ToString());

    [RelayCommand]
    private void Update(LuaTileViewModel tile) => NavigateToAdd?.Invoke(tile.AppId);

    // ── Steamless + Cleanup locks ─────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotBusy))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotBusy))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(CloseDetailCommand))]
    private bool _isRemoving;

    public bool NotBusy => !IsBusy && !IsRemoving;

    /// <summary>Set by App — opens the 4-scope consolidated cleanup dialog for a game.</summary>
    public Func<long, string, Task<GameCleanupResult?>>? OpenCleanupDialog { get; set; }

    /// <summary>Test seam replacing the restart-Steam offer (the dark confirm is already declined when unwired).</summary>
    internal Action? OfferRestartSteamOverride { get; set; }

    /// <summary>Set by App — dark confirmation (Steamless, bulk removal, restart Steam). Unset = declined (fail-closed).</summary>
    public Func<ConfirmRequest, bool>? Confirm { get; set; }

    private bool Ask(ConfirmRequest request) => Confirm?.Invoke(request) == true;

    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isProgressIndeterminate;

    /// <summary>Download Steamless (once) and strip SteamStub DRM from this game's executable(s).</summary>
    [RelayCommand]
    private async Task RemoveDrm(LuaTileViewModel? tile)
    {
        if (tile is null || IsBusy) return;

        if (!Ask(new ConfirmRequest(
                Resources.Strings.Manage_Steamless_Confirm_Title,
                Resources.Strings.Manage_Steamless_Confirm_Body,
                Resources.Strings.Manage_Action_RemoveDrm,
                Heading: tile.Name)))
            return;

        IsBusy = true;
        IsProgressIndeterminate = true;
        Progress = 0;
        var prog = new Progress<double?>(p =>
        {
            IsProgressIndeterminate = p is null;
            if (p is not null) Progress = p.Value * 100;
        });

        try
        {
            var result = await _steamless.PatchGameAsync(tile.AppId, prog);
            if (result.Failed)
            {
                string msg = result.Error switch
                {
                    "no-install" => Resources.Strings.Manage_Steamless_NoInstall,
                    "no-exe" => Resources.Strings.Manage_Steamless_NoInstall,
                    _ => string.Format(Resources.Strings.Manage_Steamless_Failed, ""),
                };
                _toast.Show(Resources.Strings.Manage_Action_RemoveDrm, msg, error: true);
            }
            else
            {
                _toast.Show(Resources.Strings.Manage_Action_RemoveDrm,
                    string.Format(Resources.Strings.Manage_Toast_Steamless_Done, result.Patched, result.Unchanged));
            }
        }
        catch (Exception ex)
        {
            _toast.Show(Resources.Strings.Manage_Action_RemoveDrm,
                string.Format(Resources.Strings.Manage_Steamless_Failed, ex.Message), error: true);
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    /// <summary>Build the confirmation body for bulk-deleting active luas, noting preserved Depots builds when present.</summary>
    internal static string BuildDeleteManyConfirmBody(int totalCount, int withStoredBuildsCount) =>
        withStoredBuildsCount > 0
            ? string.Format(Resources.Strings.Manage_DeleteMany_BodyWithBuilds, totalCount, withStoredBuildsCount)
            : string.Format(Resources.Strings.Manage_DeleteMany_Body, totalCount);

    /// <summary>Open the 4-scope removal/uninstall dialog for a game and apply the outcome cleanly.</summary>
    [RelayCommand(CanExecute = nameof(CanDeleteTile))]
    private async Task DeleteAsync(LuaTileViewModel? tile)
    {
        if (tile is null || IsRemoving || IsBusy) return;
        if (OpenCleanupDialog is null) return;

        IsRemoving = true;
        try
        {
            var result = await OpenCleanupDialog(tile.AppId, tile.Name);
            if (result is null) return;
            await ApplyCleanupResultAsync(tile, result);
        }
        finally
        {
            IsRemoving = false;
        }
    }

    private bool CanDeleteTile(LuaTileViewModel? tile) => tile is not null && NotBusy;

    internal async Task ApplyCleanupResultAsync(LuaTileViewModel tile, GameCleanupResult result)
    {
        if (result.IsBlocked)
        {
            _toast.Show(tile.Name, GameCleanupMessages.Describe(tile.Name, result), error: true);
            return;
        }

        _toast.Show(tile.Name, GameCleanupMessages.Describe(tile.Name, result), error: result.HasErrors);

        if (result.LuaDeleted)
        {
            RemoveTile(tile);
        }
        else if (result.GameFilesDeleted || result.AcfDeleted)
        {
            tile.IsInstalled = false;
            tile.InstallState = SteamGameInstallState.NotInstalled;
            ApplyFilter(resetPage: false);
            await RefreshInstallStatesAsync();
        }

        if (result.AnyRemoved)
        {
            if (OfferRestartSteamOverride is not null) OfferRestartSteamOverride();
            else OfferRestartSteam();
        }
    }

    private void RecountSelection() => SelectedCount = _all.Count(t => t.IsSelected);

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var t in _all.Where(t => t.IsSelected)) t.IsSelected = false;
        SelectedCount = 0;
    }

    /// <summary>Delete all selected lua files after one confirm; offer a Steam restart afterwards.</summary>
    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task DeleteSelectedAsync()
    {
        if (IsRemoving || IsBusy) return;
        var targets = _all.Where(t => t.IsSelected).ToList();
        if (targets.Count == 0) return;

        int withStoredBuilds = targets.Count(t => _vault.HasStoredBuilds(t.AppId));
        if (!Ask(new ConfirmRequest(
                Resources.Strings.Manage_DeleteMany_Title,
                BuildDeleteManyConfirmBody(targets.Count, withStoredBuilds),
                Resources.Strings.Cleanup_Action_Confirm)))
            return;

        IsRemoving = true;
        int failed = 0;
        bool anyRemoved = false;
        try
        {
            foreach (var t in targets)
            {
                var outcome = await _cleanup.CleanAsync(t.AppId, GameCleanupScope.LuaOnly);
                if (outcome.LuaDeleted)
                {
                    anyRemoved = true;
                    t.SelectionChanged = null;
                    _all.Remove(t);
                    // Not CloseDetail(): it refuses while IsRemoving, which is true for this whole batch.
                    if (SelectedTile == t) { SelectedTile = null; Overview = null; }
                }
                else if (outcome.IsBlocked || outcome.HasErrors)
                {
                    failed++;
                }
            }
            // Rebuild the (filtered) grid in one pass — reliable refresh vs. per-item Tiles.Remove on the
            // virtualized panel, which didn't always visually update when filters were active.
            ApplyFilter();
            SelectedCount = _all.Count(t => t.IsSelected);
        }
        finally
        {
            IsRemoving = false;
        }

        if (failed > 0)
            _toast.Show(Resources.Strings.Manage_RemoveFailed_Title,
                string.Format(Resources.Strings.Manage_RemoveFailed_Count, failed), error: true);

        if (anyRemoved)
        {
            if (OfferRestartSteamOverride is not null) OfferRestartSteamOverride();
            else OfferRestartSteam();
        }
    }

    private void OfferRestartSteam()
    {
        if (Ask(BuildsViewModel.RestartSteamRequest) && !_steam.RestartSteam())
            _toast.Show(Resources.Strings.Manage_RestartSteam_Title, Resources.Strings.Manage_RestartSteam_Failed, error: true);
    }

    /// <summary>Remove a tile from the lists after its file is gone; update empty/detail state.</summary>
    private void RemoveTile(LuaTileViewModel tile)
    {
        tile.SelectionChanged = null;
        _all.Remove(tile);
        if (SelectedTile == tile)
        {
            SelectedTile = null;
            Overview = null;
        }
        ApplyFilter(); // rebuild the (filtered) grid in one pass — reliable virtualized refresh
    }

    /// <summary>Scan config\stplug-in for &lt;appid&gt;.lua files. Called when the page is shown.</summary>
    public async Task LoadAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        try
        {
            string? steamPath = _steam.EffectivePath;
            if (steamPath is null)
            {
                _all = [];
                ApplyFilter();
                SetEmpty(Resources.Strings.Manage_Empty_NoSteam);
                return;
            }

            string dir = Path.Combine(steamPath, "config", "stplug-in");
            if (!Directory.Exists(dir))
            {
                _all = [];
                ApplyFilter();
                SetEmpty(Resources.Strings.Manage_Empty_NoLuas);
                return;
            }

            // Bulk game-name list (downloaded once, cached) — gives every game a name with no rate limit.
            await _appList.EnsureLoadedAsync();

            var tiles = await Task.Run(() =>
                LuaInstaller.EnumerateInstalled(dir) // shared scan rule (skips Steamtools.lua etc.)
                    .Select(f =>
                    {
                        var info = new FileInfo(f.Path);
                        string? name = _appList.GetName(f.AppId) ?? ParseLuaName(f.Path) ?? _appInfo.GetCached(f.AppId)?.Name;
                        // Warm filter metadata off the UI thread so PopulateFilterOptions / ApplyFilter hit RAM only.
                        _appInfo.GetFilterData(f.AppId);
                        bool placeholder = name is null;
                        // Base = when added to the folder; if edited since (LastWrite later), use that — newer is more relevant.
                        var added = info.LastWriteTime > info.CreationTime ? info.LastWriteTime : info.CreationTime;
                        return new LuaTileViewModel(f.AppId, f.Path, added, name ?? string.Format(Resources.Strings.Common_AppFallback, f.AppId), placeholder);
                    })
                    .OrderByDescending(t => t.AddedAt)
                    .ToList());

            foreach (var t in tiles) t.SelectionChanged = RecountSelection;
            _all = tiles;
            SelectedCount = 0; // fresh scan clears any prior selection
            PopulateFilterOptions();
            ApplyFilter();
            if (_all.Count == 0) SetEmpty(Resources.Strings.Manage_Empty_NoLuas);

            StartCoverPrefetch(_all);
            _ = RefreshInstallStatesAsync(); // fills the Play/Install labels; never blocks the list
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Warm cover FILES for the whole library in the background (download only — no decode, no UI),
    /// so when you jump to any page it decodes instantly from disk.
    /// </summary>
    private void StartCoverPrefetch(IReadOnlyList<LuaTileViewModel> tiles)
    {
        _prefetchCts?.Cancel();
        var cts = _prefetchCts = new CancellationTokenSource();
        var appids = tiles.Select(t => t.AppId).ToList();
        PrefetchTask = Task.Run(async () =>
        {
            try
            {
                // 1. Priority: warm cover images + names for the whole library (CDN-first, fast).
                await Parallel.ForEachAsync(
                    appids,
                    new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cts.Token },
                    async (appid, _) =>
                    {
                        try { await LuaTileViewModel.ResolveCoverFileAsync(appid, _appInfo, _covers); }
                        catch { /* one cover failing shouldn't stop the rest */ }
                    });

                // 2. Then: gently backfill full app-details (for filters) for any not yet cached.
                //    Trickles under the rate cap and yields to interactive lookups. As details arrive,
                //    refresh the dropdowns + pending count live (throttled, so we don't re-render per app).
                var lastUi = DateTime.MinValue;
                await _appInfo.BackfillFullDetailsAsync(appids, onProgress: () =>
                {
                    if (cts.Token.IsCancellationRequested) return;
                    if (DateTime.UtcNow - lastUi < TimeSpan.FromSeconds(2)) return;
                    lastUi = DateTime.UtcNow;
                    OnUi(() => { PopulateFilterOptions(); RefreshPendingCount(); });
                }, cts.Token);

                // Final refresh once backfill completes (dropdowns + counts + re-apply current filters).
                // resetPage:false so a user who's paged away isn't yanked back to page 1.
                if (!cts.Token.IsCancellationRequested)
                    OnUi(() => { PopulateFilterOptions(); ApplyFilter(resetPage: false); });
            }
            catch (OperationCanceledException) { /* superseded by a newer load */ }
        });
    }

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }

    [RelayCommand]
    private Task Refresh() => RefreshWithCooldownAsync(async () =>
    {
        if (!string.IsNullOrEmpty(SearchText)) SearchText = ""; // reset filter → full library visible
        await LoadAsync();
        _toast.Show(Resources.Strings.Manage_Toast_Refreshed_Title, string.Format(Resources.Strings.Manage_Toast_Refreshed_Body, _all.Count));
    });

    // IsEmpty is computed (in the base) from Items + IsLoading; this just sets the message for that state.
    private void SetEmpty(string message) => EmptyMessage = message;

    /// <summary>Recount library apps still missing details (drives the "fetching details" notice).</summary>
    private void RefreshPendingCount() =>
        PendingDetailsCount = _all.Count(t => _appInfo.GetFilterData(t.AppId) is null);

    /// <param name="resetPage">True (default) for a user-initiated filter/search/sort/page-size change —
    /// jump back to page 1. False for passive re-renders (backfill completing) so the user stays on
    /// their current page; the page is still clamped into range below.</param>
    private void ApplyFilter(bool resetPage = true)
    {
        string q = SearchText.Trim();
        bool hasQuery = !string.IsNullOrEmpty(q);
        bool filtersActive = HasActiveFilters;

        var matched = new List<LuaTileViewModel>(_all.Count);
        int pending = 0;

        foreach (var t in _all)
        {
            var data = _appInfo.GetFilterData(t.AppId);
            if (data is null) pending++;

            t.DetailsLoaded = data is not null;
            t.UpdateReleaseLabel(data);

            if (hasQuery && !t.Matches(q)) continue;
            if (filtersActive && (data is null || !MatchesFilters(data))) continue;

            matched.Add(t);
        }

        var list = SortTiles(matched);

        // How many library apps still lack details — shown always while backfilling (even with no
        // filters), since on startup nothing's filtered but details are still loading.
        PendingDetailsCount = pending;

        EmptyMessage = _all.Count > 0
            ? Resources.Strings.Manage_Empty_NoMatch
            : Resources.Strings.Manage_Empty_NoLuas;

        // Hand the filtered+sorted list to the base, which re-slices the visible page (clamping the
        // current page into range for the resetPage:false backfill path).
        SetFiltered(list, resetPage);
    }

    private bool MatchesFilters(AppFilterData d)
    {
        if (SelectedType != AnyOption &&
            !string.Equals(d.Type, SelectedType, StringComparison.OrdinalIgnoreCase)) return false;

        if (SelectedGenre != AnyOption &&
            !d.Genres.Any(g => string.Equals(g, SelectedGenre, StringComparison.OrdinalIgnoreCase))) return false;

        if (SelectedYear != AnyOption &&
            (d.ReleaseYear is null || d.ReleaseYear.Value.ToString() != SelectedYear)) return false;

        if (SelectedPrice != AnyOption)
        {
            if (SelectedPrice == "Free" && !d.IsFree) return false;
            if (SelectedPrice == "Paid" && d.IsFree) return false;
        }

        if (SelectedContent == "Hide adult" && d.IsAdult) return false;
        if (SelectedContent == "Adult only" && !d.IsAdult) return false;

        return true;
    }

    /// <summary>Default order: fully installed games first (green badge), then A–Z by name within each group.</summary>
    public static List<LuaTileViewModel> SortInstalledFirst(IEnumerable<LuaTileViewModel> tiles) =>
        tiles.OrderByDescending(t => t.IsInstalled)
             .ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
             .ToList();

    private List<LuaTileViewModel> SortTiles(List<LuaTileViewModel> tiles) => SelectedSort switch
    {
        InstalledFirstOption => SortInstalledFirst(tiles),
        "Name (A–Z)" => tiles.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList(),
        "Release date (newest)" => tiles
            .OrderByDescending(t => _appInfo.GetFilterData(t.AppId)?.ReleaseDate ?? DateTime.MinValue).ToList(),
        "Metacritic" => tiles
            .OrderByDescending(t => _appInfo.GetFilterData(t.AppId)?.Metacritic ?? int.MinValue).ToList(),
        "Most reviewed" => tiles
            .OrderByDescending(t => _appInfo.GetFilterData(t.AppId)?.Reviews ?? long.MinValue).ToList(),
        _ => tiles.OrderByDescending(t => t.AddedAt).ToList(), // "Recently added" (default)
    };

    /// <summary>Reset every filter (keeps the search text and sort).</summary>
    [RelayCommand]
    private void ClearFilters()
    {
        SelectedType = AnyOption;
        SelectedGenre = AnyOption;
        SelectedYear = AnyOption;
        SelectedPrice = AnyOption;
        SelectedContent = AnyOption;
        OnPropertyChanged(nameof(HasActiveFilters));
        ApplyFilter();
    }

    [RelayCommand]
    private void ToggleFilterPanel() => IsFilterPanelOpen = !IsFilterPanelOpen;

    /// <summary>Build the Type/Genre/Year dropdown options from whatever library details are cached.</summary>
    private void PopulateFilterOptions()
    {
        var types = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var genres = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var years = new SortedSet<int>();

        foreach (var t in _all)
        {
            var d = _appInfo.GetFilterData(t.AppId);
            if (d is null) continue;
            if (!string.IsNullOrWhiteSpace(d.Type)) types.Add(d.Type!);
            foreach (var g in d.Genres) genres.Add(g);
            if (d.ReleaseYear is { } y) years.Add(y);
        }

        RebuildOptionList(TypeOptions, types);
        RebuildOptionList(GenreOptions, genres);
        RebuildOptionList(YearOptions, years.OrderByDescending(y => y).Select(y => y.ToString()));
    }

    /// <summary>Sync a dropdown's options to <paramref name="values"/> WITHOUT a full Clear() — clearing
    /// drops the ComboBox's bound SelectedItem (which made Type/Genre/Year render blank). "Any" stays
    /// at index 0; we add new values and remove stale ones in place.</summary>
    private static void RebuildOptionList(ObservableCollection<string> target, IEnumerable<string> values)
    {
        var wanted = values.ToList();
        if (target.Count == 0 || target[0] != AnyOption) target.Insert(0, AnyOption);

        // Remove options no longer present (skip index 0 = "Any").
        for (int i = target.Count - 1; i >= 1; i--)
            if (!wanted.Contains(target[i]))
                target.RemoveAt(i);

        // Add any new ones (append; order is best-effort).
        foreach (var v in wanted)
            if (!target.Contains(v))
                target.Add(v);
    }

    /// <summary>Best-effort name from the lua header comment (Morrenus/Hubcap). Fallback when the app list misses.</summary>
    private static string? ParseLuaName(string path)
    {
        try
        {
            using var reader = new StreamReader(path);
            string? line0 = reader.ReadLine();
            string? line1 = reader.ReadLine();
            if (line0 is not null && line1 is not null &&
                line0.Contains("Created by", StringComparison.OrdinalIgnoreCase) &&
                line1.StartsWith("--"))
            {
                string name = line1.TrimStart('-', ' ').Trim();
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
        }
        catch { /* unreadable — fall back to appid */ }
        return null;
    }
}
