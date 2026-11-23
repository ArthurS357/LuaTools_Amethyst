using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuaToolsGui.Models;
using LuaToolsGui.Services;
using LuaToolsGui.Services.Downloads;

namespace LuaToolsGui.ViewModels;

/// <summary>A game card in the Fixes grid.</summary>
public partial class FixGameCardVm(DenuvoGameListing g) : ObservableObject
{
    public string AppId { get; } = g.AppId;
    public string Name { get; } = g.Name;
    public string? HeaderImage { get; } = g.HeaderImage;
    public int FixCount { get; } = g.FixCount;

    /// <summary>Fully installed in the Steam library. Drives the same green badge as Manage.</summary>
    public bool IsInstalled { get; init; }
    public IReadOnlyList<string> TagIds { get; } = g.Tags.Select(t => t.Id).ToList();
    public string FixCountLabel => string.Format(Resources.Strings.Fixes_Count, FixCount);

    /// <summary>Local cached cover path (set after CoverCache resolves it); bound via ImagePathToSource.</summary>
    [ObservableProperty] private string? _cover;
    private int _resolving;

    public bool Matches(string q) =>
        Name.Contains(q, StringComparison.OrdinalIgnoreCase) || AppId.Contains(q);

    /// <summary>Cache the header image to disk once (CoverCache, keyed by appid), then expose its path.</summary>
    public async Task EnsureCoverAsync(CoverCache covers)
    {
        if (Cover is not null || string.IsNullOrWhiteSpace(HeaderImage)) return;
        if (!long.TryParse(AppId, out long appid)) return;
        if (Interlocked.Exchange(ref _resolving, 1) == 1) return;
        try
        {
            string? local = covers.GetLocalPath(appid) ?? await covers.EnsureAsync(appid, HeaderImage!);
            if (local is not null) Cover = local;
        }
        finally { Interlocked.Exchange(ref _resolving, 0); }
    }
}

/// <summary>A tag filter pill; IsSelected drives its active highlight.</summary>
public partial class TagPillVm(DenuvoTag t) : ObservableObject
{
    public string Id { get; } = t.Id;
    public string Name { get; } = t.Name;
    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// Live download state for one slot (<c>"manifest"</c> or <c>"fix"</c>) of a <see cref="FixItemVm"/>,
/// synchronized with its corresponding <see cref="DownloadItem"/> in <see cref="DownloadQueue"/>.
/// </summary>
public partial class FixSlotDownloadVm(string fixId, string slot) : ObservableObject
{
    public string FixId { get; } = fixId;
    public string Slot { get; } = slot;
    public string DedupeKey { get; } = FixesViewModel.FixKey(fixId, slot);

    private DownloadItem? _attachedItem;
    internal Action? StateChanged { get; set; }

    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private double _downloadProgress;
    [ObservableProperty] private bool _isProgressIndeterminate;
    [ObservableProperty] private string? _downloadStatus;
    [ObservableProperty] private bool _canCancelDownload;

    internal DownloadItem? AttachedItem => _attachedItem;

    internal void Bind(DownloadItem? item)
    {
        if (ReferenceEquals(_attachedItem, item))
        {
            RefreshFromItem();
            return;
        }

        if (_attachedItem is not null)
            _attachedItem.PropertyChanged -= OnItemPropertyChanged;

        _attachedItem = item;

        if (_attachedItem is not null)
            _attachedItem.PropertyChanged += OnItemPropertyChanged;

        RefreshFromItem();
    }

    /// <summary>Detach from the current <see cref="DownloadItem"/> so discarded slot VMs leave zero event handlers behind.</summary>
    public void Unbind() => Bind(null);

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e) =>
        RefreshFromItem();

    internal void RefreshFromItem()
    {
        if (_attachedItem is not { } item)
        {
            IsDownloading = false;
            DownloadProgress = 0;
            IsProgressIndeterminate = false;
            DownloadStatus = null;
            CanCancelDownload = false;
            StateChanged?.Invoke();
            return;
        }

        bool active = item.IsActive;
        IsDownloading = active;
        CanCancelDownload = item.CanCancel;
        IsProgressIndeterminate = active && (item.IsIndeterminate || item.Status == Services.Downloads.DownloadStatus.Installing);
        DownloadProgress = item.Status == Services.Downloads.DownloadStatus.Completed ? 100 : item.Percent;
        DownloadStatus = item.Status switch
        {
            Services.Downloads.DownloadStatus.Failed or Services.Downloads.DownloadStatus.Cancelled when !string.IsNullOrWhiteSpace(item.Message)
                => item.Message,
            _ => item.StatusLabel,
        };
        StateChanged?.Invoke();
    }
}

/// <summary>One fix (release) in the per-game flyout.</summary>
public partial class FixItemVm : ObservableObject
{
    public FixItemVm(DenuvoFix f)
    {
        Id = f.Id;
        Title = f.Title;
        Description = f.Description;
        Tags = f.Tags;
        HasManifest = f.HasManifest;
        HasFix = f.HasFix;
        ManifestFilename = f.ManifestFilename;
        FixFilename = f.FixFilename;
        DateLabel = FormatDate(f.CreatedAt);

        ManifestSlot = new FixSlotDownloadVm(f.Id, "manifest") { StateChanged = OnSlotStateChanged };
        FixSlot = new FixSlotDownloadVm(f.Id, "fix") { StateChanged = OnSlotStateChanged };
    }

    public string Id { get; }
    public string Title { get; }
    public string? Description { get; }
    public IReadOnlyList<DenuvoTag> Tags { get; }
    public bool HasManifest { get; }
    public bool HasFix { get; }
    public string? ManifestFilename { get; }
    public string? FixFilename { get; }
    public string DateLabel { get; }

    public FixSlotDownloadVm ManifestSlot { get; }
    public FixSlotDownloadVm FixSlot { get; }

    internal Action? SlotStateChanged { get; set; }

    public FixSlotDownloadVm GetSlot(string slot) =>
        string.Equals(slot, "manifest", StringComparison.OrdinalIgnoreCase) ? ManifestSlot : FixSlot;

    /// <summary>Detach both slots from any bound <see cref="DownloadItem"/> before this VM is discarded.</summary>
    public void Unbind()
    {
        SlotStateChanged = null;
        ManifestSlot.Unbind();
        FixSlot.Unbind();
    }

    /// <summary>
    /// Whether the game is installed on disk. Only the FIX slot cares: it extracts a zip into the game
    /// folder, so with no folder there is nothing to apply. The MANIFEST slot installs a lua and works
    /// whether or not the game is installed.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownloadFix), nameof(FixHint))]
    private bool _gameInstalled;

    /// <summary>True when the fix has been applied (its revert record exists on disk). Drives Revert.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownloadFix), nameof(CanRevert), nameof(FixHint))]
    private bool _isApplied;

    public bool IsManifestDownloading => ManifestSlot.IsDownloading;
    public bool IsFixDownloading => FixSlot.IsDownloading;
    public bool IsDownloading => ManifestSlot.IsDownloading || FixSlot.IsDownloading;

    public double DownloadProgress =>
        FixSlot.IsDownloading ? FixSlot.DownloadProgress
        : ManifestSlot.IsDownloading ? ManifestSlot.DownloadProgress
        : FixSlot.DownloadProgress > 0 ? FixSlot.DownloadProgress
        : ManifestSlot.DownloadProgress;

    public bool IsProgressIndeterminate =>
        FixSlot.IsDownloading ? FixSlot.IsProgressIndeterminate
        : ManifestSlot.IsDownloading && ManifestSlot.IsProgressIndeterminate;

    public string? DownloadStatus =>
        FixSlot.IsDownloading ? FixSlot.DownloadStatus
        : ManifestSlot.IsDownloading ? ManifestSlot.DownloadStatus
        : FixSlot.DownloadStatus ?? ManifestSlot.DownloadStatus;

    public bool CanCancelDownload => ManifestSlot.CanCancelDownload || FixSlot.CanCancelDownload;

    public bool CanDownloadManifest => HasManifest && !ManifestSlot.IsDownloading;
    public bool CanDownloadFix => HasFix && GameInstalled && !IsApplied && !FixSlot.IsDownloading;
    public bool CanRevert => IsApplied && !FixSlot.IsDownloading;

    /// <summary>Why the Fix button is greyed out, or null when it isn't. A null ToolTip shows nothing,
    /// so this doubles as the "should there be a tooltip at all" test.</summary>
    public string? FixHint =>
        IsApplied ? Resources.Strings.Fixes_Applied_Hint
        : GameInstalled ? null
        : Resources.Strings.Fixes_NotInstalled_Hint;

    private void OnSlotStateChanged()
    {
        OnPropertyChanged(nameof(IsManifestDownloading));
        OnPropertyChanged(nameof(IsFixDownloading));
        OnPropertyChanged(nameof(IsDownloading));
        OnPropertyChanged(nameof(DownloadProgress));
        OnPropertyChanged(nameof(IsProgressIndeterminate));
        OnPropertyChanged(nameof(DownloadStatus));
        OnPropertyChanged(nameof(CanCancelDownload));
        OnPropertyChanged(nameof(CanDownloadManifest));
        OnPropertyChanged(nameof(CanDownloadFix));
        OnPropertyChanged(nameof(CanRevert));
        SlotStateChanged?.Invoke();
    }

    private static string FormatDate(string? iso) =>
        DateTimeOffset.TryParse(iso, out var d) ? d.UtcDateTime.ToString("d MMM yyyy") : "";
}

/// <summary>
/// "Fixes" page: browse games with Denuvo fixes (grid + search + tag filter), open a game to see its
/// fixes, and download a fix's manifest (force-locked lua install) or fix zip (extract into the game
/// folder if installed) through the app-wide <see cref="DownloadQueue"/>. Downloads are auth-gated and
/// count toward the 25/day limit (server-side).
/// </summary>
public partial class FixesViewModel : PagedListViewModel<FixGameCardVm>
{
    private readonly LuaToolsApiClient api;
    private readonly AuthService auth;
    private readonly LuaInstaller installer;
    private readonly SteamService steam;
    private readonly SteamLibraryService library;
    private readonly CoverCache covers;
    private readonly ToastService toast;
    private readonly SettingsService settings;
    private readonly DenuvoFixService fixes;
    private readonly DownloadQueue _queue;
    private readonly ConcurrentDictionary<string, FixInstallOutcome> _installOutcomes = new();

    public FixesViewModel(
        LuaToolsApiClient api, AuthService auth, LuaInstaller installer, SteamService steam,
        SteamLibraryService library, CoverCache covers, ToastService toast, SettingsService settings,
        DenuvoFixService fixes, DownloadQueue? queue = null)
    {
        this.api = api;
        this.auth = auth;
        this.installer = installer;
        this.steam = steam;
        this.library = library;
        this.covers = covers;
        this.toast = toast;
        this.settings = settings;
        this.fixes = fixes;
        _queue = queue ?? new DownloadQueue(new CacheService());
        _queue.Items.CollectionChanged += OnQueueItemsChanged;
        _queue.StateChanged += SyncFixesWithQueue;
        InitPageSize(settings.FixesPageSize);
    }

    /// <summary>Unique dedupe key for a fix slot in <see cref="DownloadQueue"/> (<c>fix:&lt;fixId&gt;:&lt;slot&gt;</c>).</summary>
    public static string FixKey(string fixId, string slot) =>
        $"fix:{fixId}:{slot.ToLowerInvariant()}";

    /// <summary>Set by App so a guest hitting a download is sent through the Discord sign-in flow.</summary>
    public Func<Task>? RequestSignIn { get; set; }

    /// <summary>Test seam so end-to-end manifest install tests do not kill a running Steam client on the host.</summary>
    internal Func<bool>? RestartSteamOverride { get; set; }

    // The master list; the displayed page slice lives in the base's Items collection.
    private List<FixGameCardVm> _allGames = [];

    public ObservableCollection<TagPillVm> Tags { get; } = [];

    // IsLoading, EmptyMessage and the IsEmpty gating are inherited from PagedListViewModel<FixGameCardVm>.
    // Page size persists via SavePageSizeSetting below.
    protected override void SavePageSizeSetting(int size) => settings.FixesPageSize = size;

    /// <summary>Warm the cover images for just the freshly-shown page (idempotent, off-UI).</summary>
    protected override void OnPageSliced(IReadOnlyList<FixGameCardVm> slice)
    {
        foreach (var g in slice) _ = g.EnsureCoverAsync(covers);
    }

    [ObservableProperty] private string _searchText = "";
    partial void OnSearchTextChanged(string value) => ApplyFilter();

    [ObservableProperty] private string? _selectedTagId; // null = "All"

    // Appids with a lua in Steam's config/stplug-in ("my games"), so the page can filter the fix
    // listing down to games the user actually added. Empty when Steam isn't set up / no luas installed.
    private HashSet<long> _installedAppIds = [];

    /// <summary>True once the listing has been fetched (set at the end of LoadAsync).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanFilter))]
    private bool _loaded;

    /// <summary>Gates the filter pills ("my games" + tags) behind a finished, non-empty listing.</summary>
    public bool CanFilter => Loaded && _allGames.Count > 0;

    /// <summary>Only show fix games the user has added (a lua in stplug-in). Mirrors Manage's "my games".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MyGamesHint))]
    private bool _myGamesOnly;

    /// <summary>
    /// How many of the user's added games actually appear in the fix listing.
    /// </summary>
    /// <remarks>
    /// The count must be the INTERSECTION, not <c>_installedAppIds.Count</c>. The string reads "{0} of
    /// your games have fixes", but the raw count is every game with a lua added — so a library with 243
    /// added games advertised 243 fixes while the filtered grid showed a dozen. This mirrors exactly what
    /// <see cref="ApplyFilter"/> puts on screen when My games is on.
    /// </remarks>
    public string MyGamesHint
    {
        get
        {
            if (_installedAppIds.Count == 0) return Resources.Strings.Fixes_MyGames_NotInstalled;

            int withFixes = _allGames.Count(g =>
                long.TryParse(g.AppId, out long id) && _installedAppIds.Contains(id));
            return string.Format(Resources.Strings.Fixes_MyGames_Count, withFixes);
        }
    }

    partial void OnMyGamesOnlyChanged(bool value)
    {
        if (value)
        {
            SelectedTagId = null; // one filter at a time — turning "my games" on drops any tag
            foreach (var pill in Tags) pill.IsSelected = false;
        }
        ApplyFilter();
    }

    // ── Detail flyout ───────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDetailOpen))]
    private FixGameCardVm? _selectedGame;

    public bool IsDetailOpen => SelectedGame is not null;
    public ObservableCollection<FixItemVm> Fixes { get; } = [];
    [ObservableProperty] private bool _isLoadingFixes;

    // Per-game tag filter (only meaningful when this game's fixes span multiple tags).
    private List<FixItemVm> _allFixes = [];
    private readonly HashSet<FixItemVm> _trackedFixes = [];
    private readonly SemaphoreSlim _installGate = new(1, 1);
    private int _pendingManifestInstalls;
    public ObservableCollection<TagPillVm> FixTags { get; } = [];
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFixTags))]
    private string? _selectedFixTagId;
    public bool HasFixTags => FixTags.Count > 0;

    // ── Download state (one at a time) ───────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotBusy))]
    private bool _isBusy;
    public bool NotBusy => !IsBusy;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isProgressIndeterminate;

    // ── Load ─────────────────────────────────────────────────────────

    // The whole catalog as the API sent it (kept across visits; Refresh re-fetches it).
    private DenuvoListingsResponse? _catalog;
    private bool _catalogFailed;
    private DateTime? _catalogCachedAt; // non-null = _catalog is the offline copy saved at that time

    // The Steam library as last scanned. Failed means "could not be read", which is not "nothing installed".
    private List<InstalledGame> _installed = [];
    private bool _libraryFailed;

    // One load at a time: the fix:// handler and the view's own Loaded both call LoadAsync, and the second
    // must wait for the first (not cancel it, not race it on the shared state).
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private CancellationTokenSource _leaveCts = new();

    /// <summary>Called by the view when the tab is left: loads started or queued before it stop.</summary>
    public void CancelLoad() => Interlocked.Exchange(ref _leaveCts, new CancellationTokenSource()).Cancel();

    /// <summary>The "installed games with no fix" InfoBar is open. Two-way with <c>ui:InfoBar.IsOpen</c>: the
    /// InfoBar's own close button sets it false, which persists the dismissal; <see cref="LoadAsync"/>
    /// changes it only under <see cref="_syncingBanner"/>, so a reload never counts as a dismissal.</summary>
    [ObservableProperty] private bool _showBanner;

    private bool _syncingBanner;

    partial void OnShowBannerChanged(bool value)
    {
        if (!value && !_syncingBanner) settings.FixesOrphanBannerDismissed = true;
    }
    [ObservableProperty] private string _bannerText = "";
    [ObservableProperty] private string _bannerNames = "";
    [ObservableProperty] private bool _showLibraryWarning;

    /// <summary>"Offline mode: showing the catalog saved on …" while the grid is the on-disk copy; null
    /// (banner hidden) when the catalog is live or could not be loaded at all.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOfflineBanner))]
    private string? _offlineBannerText;

    public bool ShowOfflineBanner => OfflineBannerText is not null;

    /// <summary>
    /// Fetch the catalog (once per session) and join it with the games installed in the Steam library.
    /// The library is rescanned on every call, so a game installed since the last visit shows up.
    /// </summary>
    /// <param name="force">True to re-fetch the catalog even if already loaded (the Refresh button).</param>
    public async Task LoadAsync(bool force = false)
    {
        var ct = _leaveCts.Token;
        try { await _loadGate.WaitAsync(ct); }
        catch (OperationCanceledException) { return; }

        bool left = false;
        IsLoading = true;
        try
        {
            // Both in parallel: the library scan is file I/O, the catalog is a request.
            var scanTask = Task.Run(() =>
            {
                try { return library.ScanFullyInstalled(); }
                catch (Exception ex)
                {
                    AppLog.Log($"Fixes: library scan failed: {ex.GetType().Name}: {ex.Message}");
                    return ([], true);
                }
            }, ct);

            _catalogFailed = false;
            if (force || _catalog is null)
            {
                var result = await api.GetDenuvoListingsAsync(ct);
                if (result is null) _catalogFailed = true;
                else (_catalog, _catalogCachedAt) = (result.Data, result.CachedAt);
            }
            (_installed, _libraryFailed) = await scanTask;

            // "My games" filter source: games with a lua in stplug-in, a narrowing of the catalog.
            _installedAppIds = steam.StPlugInDir is { } dir
                ? await Task.Run(() => LuaInstaller.EnumerateInstalled(dir).Select(i => i.AppId).ToHashSet(), ct)
                : [];
            Present();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            left = true; // tab left — the next visit's load owns the state
        }
        catch (Exception ex)
        {
            AppLog.Log($"Fixes: game list load failed: {ex.GetType().Name}: {ex.Message}");
            _catalogFailed = true;
            Present();
        }
        finally
        {
            if (!left)
            {
                IsLoading = false;
                Loaded = true; // gates the filter pills: they appear only once the listing settled
                OnPropertyChanged(nameof(CanFilter)); // Loaded may not have changed, the list may have
            }
            _loadGate.Release();
        }
    }

    /// <summary>Turn the catalog + library results into the grid, the banner and the empty message.</summary>
    private void Present()
    {
        var games = _catalogFailed ? [] : _catalog?.Games ?? [];
        var installedIds = _installed.Select(g => g.AppId).ToHashSet();
        var catalogIds = games.Select(g => long.TryParse(g.AppId, out long id) ? id : -1).ToHashSet();
        var without = InstalledFixes.FindInstalledWithoutFix(_installed, catalogIds);
        var state = InstalledFixes.Resolve(_catalogFailed, games.Count, _libraryFailed, without.Count);

        // A failed library hides the badges: "installed" is unknown, not false.
        _allGames = InstalledFixes.JoinCatalogWithInstalled(games, installedIds)
            .Select(c => new FixGameCardVm(c.Listing) { IsInstalled = state.ShowBadges && c.IsInstalled }).ToList();

        Tags.Clear();
        var used = _allGames.SelectMany(g => g.TagIds).ToHashSet();
        if (SelectedTagId is { } sel && !used.Contains(sel)) SelectedTagId = null; // its games are gone
        foreach (var t in (_catalog?.Tags ?? []).Where(t => used.Contains(t.Id)))
            Tags.Add(new TagPillVm(t) { IsSelected = t.Id == SelectedTagId });

        OfflineBannerText = !_catalogFailed && _catalogCachedAt is { } saved
            ? string.Format(Resources.Strings.Fixes_OfflineMode, saved.ToString("g"))
            : null;
        _syncingBanner = true;
        try { ShowBanner = state.ShowBanner && !settings.FixesOrphanBannerDismissed; }
        finally { _syncingBanner = false; }
        ShowLibraryWarning = state.ShowLibraryWarning;
        BannerText = string.Format(Resources.Strings.Fixes_Warn_InstalledWithoutFix, without.Count);
        BannerNames = string.Join(Environment.NewLine, without.Select(g =>
            g.Name.Length > 0 ? g.Name : string.Format(Resources.Strings.Common_AppFallback, g.AppId)));

        OnPropertyChanged(nameof(MyGamesHint));
        ApplyFilter();
        EmptyMessage = InstalledFixes.EmptyMessage(state.Empty);
    }

    [RelayCommand]
    private Task Refresh() => RefreshWithCooldownAsync(async () =>
    {
        if (SearchText.Length > 0) SearchText = ""; // reset filter → full list visible
        if (SelectedTagId is not null) SelectTag(SelectedTagId); // clear active tag (toggles off)
        if (MyGamesOnly) MyGamesOnly = false; // ditto for the "my games" filter
        await LoadAsync(force: true);
        toast.Show(Resources.Strings.Fixes_Toast_Refreshed_Title,
            string.Format(Resources.Strings.Fixes_Toast_Refreshed_Body, _allGames.Count));
    });

    [RelayCommand]
    private void SelectTag(string? tagId)
    {
        SelectedTagId = SelectedTagId == tagId ? null : tagId; // toggle off when re-clicked
        if (MyGamesOnly) MyGamesOnly = false; // one filter at a time — picking a tag drops "my games"
        foreach (var pill in Tags) pill.IsSelected = pill.Id == SelectedTagId;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        string q = SearchText.Trim();
        IEnumerable<FixGameCardVm> shown = _allGames;
        if (SelectedTagId is { } tag) shown = shown.Where(g => g.TagIds.Contains(tag));
        if (MyGamesOnly) shown = shown.Where(g => long.TryParse(g.AppId, out long id) && _installedAppIds.Contains(id));
        if (q.Length > 0) shown = shown.Where(g => g.Matches(q));

        // Hand the filtered list to the base — it slices the visible page and (via OnPageSliced) warms
        // that page's covers.
        SetFiltered(shown);
    }

    // ── Detail flyout ───────────────────────────────────────────────

    /// <summary>
    /// Open the detail flyout for a specific game by its Steam AppId. Loads the listing if needed,
    /// finds the game card, and opens its fix detail (the flyout makes its own per-appid API call).
    /// Used by the luatools://fix/ protocol handler.
    /// </summary>
    public async Task OpenForAppIdAsync(long appId)
    {
        if (_catalog is null)
            await LoadAsync();

        string id = appId.ToString();
        var game = _allGames.FirstOrDefault(g => g.AppId == id);
        if (game is null) return;

        SearchText = "";
        SelectedTagId = null;
        ApplyFilter();

        await OpenGame(game);
    }

    /// <summary>Unbind every existing <see cref="FixItemVm"/> and replace the backing list so discarded items
    /// never retain <see cref="DownloadItem.PropertyChanged"/> subscriptions.</summary>
    internal void ReplaceFixes(IReadOnlyList<FixItemVm> next)
    {
        foreach (var tracked in _trackedFixes.ToArray())
        {
            if (!next.Contains(tracked))
            {
                tracked.Unbind();
                _trackedFixes.Remove(tracked);
            }
        }
        foreach (var existing in _allFixes)
            if (!next.Contains(existing))
                existing.Unbind();
        foreach (var shown in Fixes)
            if (!next.Contains(shown))
                shown.Unbind();

        Fixes.Clear();
        _allFixes = [.. next];
        foreach (var f in _allFixes)
            _trackedFixes.Add(f);
    }

    [RelayCommand]
    private async Task OpenGame(FixGameCardVm game)
    {
        SelectedGame = game;
        _ = game.EnsureCoverAsync(covers); // ensure the flyout header image is cached too
        ReplaceFixes([]);
        FixTags.Clear();
        SelectedFixTagId = null;
        IsLoadingFixes = true;
        try
        {
            var data = await api.GetDenuvoFixesAsync(game.AppId);
            if (data is not null)
            {
                ReplaceFixes(data.Fixes.Select(f => new FixItemVm(f)).ToList());

                // Is the game on disk? GetInstallDir walks libraryfolders.vdf + appmanifest_*.acf, so it
                // is file I/O — off the UI thread. Resolved once here rather than per fix row.
                string? installDir = long.TryParse(game.AppId, out long gameAppId)
                    ? await Task.Run(() => library.GetInstallDir(gameAppId))
                    : null;
                foreach (var f in _allFixes)
                {
                    f.GameInstalled = installDir is not null;
                    // Whether this specific fix has been applied (its revert record on disk).
                    f.IsApplied = DenuvoFixService.IsApplied(installDir, f.Id);
                    f.SlotStateChanged = UpdatePageDownloadState;
                }

                SyncFixesWithQueue();

                // Build the per-game filter pills from the distinct tags across this game's fixes —
                // but only when there's more than one (a single tag is no filter).
                var distinct = _allFixes.SelectMany(f => f.Tags)
                    .GroupBy(t => t.Id).Select(g => g.First())
                    .OrderBy(t => t.Name).ToList();
                if (distinct.Count > 1)
                    foreach (var t in distinct) FixTags.Add(new TagPillVm(t));

                OnPropertyChanged(nameof(HasFixTags));
                ApplyFixFilter();
            }
        }
        catch (Exception ex) { AppLog.Log($"Fixes: fix list load failed: {ex.GetType().Name}: {ex.Message}"); /* leave empty — flyout shows "no fixes" */ }
        finally { IsLoadingFixes = false; }
    }

    [RelayCommand]
    private void SelectFixTag(string? tagId)
    {
        SelectedFixTagId = SelectedFixTagId == tagId ? null : tagId;
        foreach (var pill in FixTags) pill.IsSelected = pill.Id == SelectedFixTagId;
        ApplyFixFilter();
    }

    private void ApplyFixFilter()
    {
        IEnumerable<FixItemVm> shown = _allFixes;
        if (SelectedFixTagId is { } tag) shown = shown.Where(f => f.Tags.Any(t => t.Id == tag));
        Fixes.Clear();
        foreach (var f in shown) Fixes.Add(f);
    }

    [RelayCommand]
    private void CloseDetail()
    {
        SelectedGame = null;
        ReplaceFixes([]);
        UpdatePageDownloadState();
    }

    // ── Downloads (via DownloadQueue) ────────────────────────────────

    private sealed record FixInstallOutcome(
        JobResult JobResult,
        string ToastTitle,
        string ToastBody,
        bool IsError,
        bool MarkApplied = false);

    [RelayCommand]
    private Task DownloadManifest(FixItemVm fix) => RunDownload(fix, "manifest");

    [RelayCommand]
    private Task DownloadFix(FixItemVm fix) => RunDownload(fix, "fix");

    [RelayCommand]
    private void CancelDownload(FixItemVm? fix)
    {
        if (fix is null) return;
        CancelSlotDownload(fix, "manifest");
        CancelSlotDownload(fix, "fix");
    }

    public void CancelSlotDownload(FixItemVm fix, string slot)
    {
        var slotVm = fix.GetSlot(slot);
        var item = slotVm.AttachedItem is { IsActive: true } attached
            ? attached
            : _queue.FindActive(FixKey(fix.Id, slot));
        if (item is not null)
            _queue.Cancel(item);
    }

    private Task RunDownload(FixItemVm fix, string slot) =>
        EnqueueFixDownloadAsync(fix, slot, SelectedGame);

    /// <summary>
    /// Enqueue a fix slot (<c>"manifest"</c> or <c>"fix"</c>) into <see cref="DownloadQueue"/>.
    /// <list type="bullet">
    ///   <item>Auth check and modal sign-in prompt run on the click BEFORE <see cref="DownloadQueue.Enqueue"/>.</item>
    ///   <item>The signed-URL grant (<c>DownloadDenuvoAsync</c>) runs INSIDE the queue worker callback so the
    ///   short-lived token cannot expire while waiting in the queue, and is re-requested fresh on Retry.</item>
    ///   <item>Keyed by <c>(fix.Id, slot)</c> (<see cref="FixKey"/>), so <c>manifest</c> and <c>fix</c> slots
    ///   of the same fix — or two fixes of the same game, even with <c>SteamAppId == null</c> / <c>0</c> —
    ///   never collide.</item>
    /// </list>
    /// </summary>
    public async Task<DownloadItem?> EnqueueFixDownloadAsync(FixItemVm fix, string slot, FixGameCardVm? game = null)
    {
        if (await PromptSignInIfGuestAsync(Resources.Strings.Fixes_SignIn)) return null;

        game ??= SelectedGame;
        string normalizedSlot = string.Equals(slot, "manifest", StringComparison.OrdinalIgnoreCase) ? "manifest" : "fix";
        bool isManifest = normalizedSlot == "manifest";

        string? appIdRaw = string.IsNullOrWhiteSpace(game?.AppId) ? null : game!.AppId.Trim();
        long appId = long.TryParse(appIdRaw, out long parsedId) && parsedId > 0 ? parsedId : 0;
        string gameName = !string.IsNullOrWhiteSpace(game?.Name) ? game!.Name : fix.Title;
        string fallbackIdPrefix = appIdRaw ?? fix.Id;
        string fallback = isManifest
            ? fix.ManifestFilename ?? $"{fallbackIdPrefix}.zip"
            : fix.FixFilename ?? $"{fallbackIdPrefix}_fix.zip";

        string? coverPath = appId > 0 ? covers.GetLocalPath(appId) ?? game?.Cover : game?.Cover;
        string subtitle = $"{string.Format(Resources.Strings.Fixes_Build, fix.Title)} · "
                        + (isManifest ? Resources.Strings.Fixes_Manifest : Resources.Strings.Fixes_Fix);

        var job = new DownloadJob(
            Kind: isManifest ? DownloadKind.FixManifest : DownloadKind.DenuvoFix,
            DedupeKey: FixKey(fix.Id, normalizedSlot),
            AppId: appId,
            Title: gameName,
            SubTitle: subtitle,
            CoverPath: coverPath,
            DownloadAsync: (_, progress, ct) =>
                api.DownloadDenuvoAsync(fix.Id, normalizedSlot, fallback, progress, ct, appid: appIdRaw),
            InstallAsync: (file, item, ct) =>
                RunSerializedInstallAsync(file, item, isManifest, appId, fix.Id, gameName, ct),
            OnFinished: (item, result) => OnFixJobFinished(fix, normalizedSlot, item, result));

        var queued = _queue.Enqueue(job);
        fix.GetSlot(normalizedSlot).Bind(queued);
        UpdatePageDownloadState();
        return queued;
    }

    private async Task<JobResult> RunSerializedInstallAsync(
        DownloadedFile file,
        DownloadItem item,
        bool isManifest,
        long appId,
        string fixId,
        string gameName,
        CancellationToken ct)
    {
        if (isManifest)
            Interlocked.Increment(ref _pendingManifestInstalls);

        bool acquired = false;
        try
        {
            await _installGate.WaitAsync(ct);
            acquired = true;

            FixInstallOutcome outcome;
            if (isManifest)
            {
                // If another manifest install is already queued at the gate behind this one, let the last one
                // in the burst perform the Steam restart so two concurrent manifest jobs never restart Steam twice.
                bool shouldRestartSteam = Volatile.Read(ref _pendingManifestInstalls) <= 1;
                outcome = InstallManifestCore(file, appId, gameName, shouldRestartSteam);
            }
            else
            {
                outcome = await ApplyFixCoreAsync(file, appId, fixId, gameName, ct);
            }

            return RecordInstallOutcome(item.Id, outcome);
        }
        finally
        {
            if (isManifest)
                Interlocked.Decrement(ref _pendingManifestInstalls);
            if (acquired)
                _installGate.Release();
        }
    }

    private JobResult RecordInstallOutcome(string itemId, FixInstallOutcome outcome)
    {
        _installOutcomes[itemId] = outcome;
        return outcome.JobResult;
    }

    private void OnFixJobFinished(FixItemVm fix, string slot, DownloadItem item, JobResult? result)
    {
        if (_installOutcomes.TryRemove(item.Id, out var outcome))
        {
            if (outcome.MarkApplied)
            {
                fix.IsApplied = true;
                foreach (var f in _allFixes.Where(f => string.Equals(f.Id, fix.Id, StringComparison.Ordinal)))
                    f.IsApplied = true;
            }
            toast.Show(outcome.ToastTitle, outcome.ToastBody, error: outcome.IsError);
        }
        else if (item.Status == DownloadStatus.Failed)
        {
            toast.Show(
                Resources.Strings.Fixes_Toast_DownloadFailed,
                !string.IsNullOrWhiteSpace(item.Message) ? item.Message! : Resources.Strings.Fixes_Toast_DownloadFailed_Body,
                error: true);
        }

        if (_allFixes.Contains(fix))
            fix.GetSlot(slot).RefreshFromItem();
        else
            fix.Unbind();

        SyncFixesWithQueue();
    }

    private void OnQueueItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        SyncFixesWithQueue();

    /// <summary>
    /// Synchronize every loaded <see cref="FixItemVm"/> slot with the current items in <see cref="DownloadQueue"/>.
    /// Keeps card progress, cancel state, and status in sync across navigation, external cancellation from
    /// the Downloads tab, and queue Retries.
    /// </summary>
    public void SyncFixesWithQueue()
    {
        foreach (var tracked in _trackedFixes.ToArray())
        {
            if (!_allFixes.Contains(tracked))
            {
                tracked.Unbind();
                _trackedFixes.Remove(tracked);
            }
        }

        foreach (var fix in _allFixes)
        {
            _trackedFixes.Add(fix);
            SyncSlotWithQueue(fix.ManifestSlot);
            SyncSlotWithQueue(fix.FixSlot);
        }
        UpdatePageDownloadState();
    }

    private void SyncSlotWithQueue(FixSlotDownloadVm slotVm)
    {
        var active = _queue.Items.FirstOrDefault(i =>
            i.IsActive && string.Equals(i.Job.DedupeKey, slotVm.DedupeKey, StringComparison.OrdinalIgnoreCase));

        if (active is not null)
        {
            slotVm.Bind(active);
            return;
        }

        // If the currently attached item is still in Queue.Items (e.g. settled as Failed/Cancelled or
        // waiting for the 3s Completed auto-dismiss), keep it bound so the card shows the terminal status.
        if (slotVm.AttachedItem is { } current && _queue.Items.Contains(current))
        {
            slotVm.RefreshFromItem();
            return;
        }

        var latest = _queue.Items.LastOrDefault(i =>
            string.Equals(i.Job.DedupeKey, slotVm.DedupeKey, StringComparison.OrdinalIgnoreCase));
        slotVm.Bind(latest);
    }

    private void UpdatePageDownloadState()
    {
        var activeFix = _allFixes.FirstOrDefault(static f => f.IsDownloading);
        if (activeFix is null)
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
            Progress = 0;
            return;
        }

        IsBusy = true;
        IsProgressIndeterminate = activeFix.IsProgressIndeterminate;
        Progress = activeFix.DownloadProgress;
    }

    /// <summary>Manifest slot: install into Steam force-LOCKED (Denuvo fixes must stay version-pinned).</summary>
    private FixInstallOutcome InstallManifestCore(
        DownloadedFile file, long appId, string gameName, bool shouldRestartSteam = true)
    {
        bool isZip = file.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

        // Screen the staged download before it reaches Steam's folders. InstallZip flattens entries to
        // fixed names (<appid>.lua / the manifest's own name) so it is not path-traversable — hence the
        // null destination root — but size, shape and lua-content checks all still apply here.
        if (!ScreenBeforeInstall(file, destinationRoot: null, isZip, out string? blockedReason))
        {
            return new FixInstallOutcome(
                new JobResult(false, blockedReason),
                Resources.Strings.Fixes_Toast_Blocked,
                blockedReason!,
                IsError: true);
        }

        long effectiveAppId = appId > 0 ? appId : (LuaInstaller.AppIdFromFileName(file.FileName) ?? 0);
        var result = isZip
            ? installer.InstallZip(file.FilePath, effectiveAppId, forceLocked: true)
            : installer.InstallLuaFile(file.FilePath, effectiveAppId, forceLocked: true);
        file.Discard(); // consumed by the install — release the pin and drop the temp staging copy

        if (result.AnyFailed || result.Error is not null)
        {
            string err = result.Error ?? Resources.Strings.Fixes_Toast_InstallFailed_Body;
            return new FixInstallOutcome(
                new JobResult(false, err),
                Resources.Strings.Fixes_Toast_InstallFailed,
                err,
                IsError: true);
        }

        bool restarted = shouldRestartSteam && (RestartSteamOverride?.Invoke() ?? steam.RestartSteam());
        string okBody = restarted
            ? string.Format(Resources.Strings.Fixes_Toast_FixInstalled_Restarting, gameName)
            : string.Format(Resources.Strings.Fixes_Toast_FixInstalled_Restart, gameName);

        return new FixInstallOutcome(
            new JobResult(true, okBody, effectiveAppId > 0 ? installer.ReadInstalledLua(effectiveAppId) : null),
            Resources.Strings.Fixes_Toast_FixInstalled,
            okBody,
            IsError: false);
    }

    /// <summary>Fix slot: only applicable if the game is installed — extract the zip into its folder.</summary>
    private async Task<FixInstallOutcome> ApplyFixCoreAsync(
        DownloadedFile file, long appId, string fixId, string gameName, CancellationToken ct)
    {
        string? installDir = appId > 0 ? library.GetInstallDir(appId) : null;
        if (installDir is null)
        {
            file.Discard();
            string notFoundBody = string.Format(Resources.Strings.Fixes_Toast_GameNotFound_Body, gameName);
            return new FixInstallOutcome(
                new JobResult(false, notFoundBody),
                Resources.Strings.Fixes_Toast_GameNotFound,
                notFoundBody,
                IsError: true);
        }

        // Screen the archive against the folder it would be extracted into, BEFORE anything is written.
        if (!ScreenBeforeInstall(file, destinationRoot: installDir, isArchive: true, out string? blockedReason))
        {
            return new FixInstallOutcome(
                new JobResult(false, blockedReason),
                Resources.Strings.Fixes_Toast_Blocked,
                blockedReason!,
                IsError: true);
        }

        try
        {
            var result = await fixes.ApplyAsync(file.FilePath, appId, fixId, installDir, ct);

            switch (result.Outcome)
            {
                case FixApplyOutcome.Applied:
                    {
                        string body = string.Format(Resources.Strings.Fixes_Toast_FixApplied_Body, gameName);
                        return new FixInstallOutcome(
                            new JobResult(true, body, installDir),
                            Resources.Strings.Fixes_Toast_FixApplied,
                            body,
                            IsError: false,
                            MarkApplied: true);
                    }

                case FixApplyOutcome.PartiallyApplied:
                    {
                        string body = string.Format(Resources.Strings.Fixes_Toast_PartiallyApplied_Body, result.Failed);
                        // A partial apply still backed files up and still wrote a record, so it IS applied and
                        // MUST be revertable — MarkApplied is true for both Applied and PartiallyApplied.
                        return new FixInstallOutcome(
                            new JobResult(false, body, installDir),
                            Resources.Strings.Fixes_Toast_PartiallyApplied,
                            body,
                            IsError: true,
                            MarkApplied: true);
                    }

                default: // RecordFailed / Failed — nothing was applied
                    {
                        string body = result.Error ?? Resources.Strings.Fixes_Toast_InstallFailed_Body;
                        return new FixInstallOutcome(
                            new JobResult(false, body),
                            Resources.Strings.Fixes_Toast_CouldntApply,
                            body,
                            IsError: true,
                            MarkApplied: false);
                    }
            }
        }
        finally
        {
            file.Discard(); // the service is done with it — release the pin and drop the temp staging copy
        }
    }

    // ── Revert ───────────────────────────────────────────────────────

    /// <summary>Ask first, then revert. Reverting rewrites files inside the user's game folder.</summary>
    [RelayCommand]
    private void RevertFix(FixItemVm fix)
    {
        if (SelectedGame is not { } game) return;
        _pendingRevert = (fix, game);
        ConfirmRevertTitle = string.Format(Resources.Strings.Fixes_Revert_Confirm_Title, game.Name);
        ConfirmRevertBody = string.Format(Resources.Strings.Fixes_Revert_Confirm_Body, game.Name);
        IsConfirmingRevert = true;
    }

    [RelayCommand]
    private void CancelRevertConfirm()
    {
        IsConfirmingRevert = false;
        _pendingRevert = null;
    }

    [RelayCommand]
    private async Task ConfirmRevert()
    {
        IsConfirmingRevert = false;
        var pending = _pendingRevert;
        _pendingRevert = null;
        if (pending is not (var fix, var game)) return;
        if (!long.TryParse(game.AppId, out long appId)) return;

        string? installDir = await Task.Run(() => library.GetInstallDir(appId));
        if (installDir is null)
        {
            toast.Show(Resources.Strings.Fixes_Toast_GameNotFound,
                string.Format(Resources.Strings.Fixes_Toast_GameNotFound_Body, game.Name), error: true);
            return;
        }

        var result = await Task.Run(() => fixes.Revert(fix.Id, installDir));

        // Exactly one toast per outcome. The view model used to be able to add a second on failure, which
        // made every partial revert — the common case, from a locked file while the game is running —
        // fire twice for a single action.
        switch (result.Outcome)
        {
            case FixRevertOutcome.Done:
                toast.Show(Resources.Strings.Fixes_Revert_Done,
                    string.Format(Resources.Strings.Fixes_Revert_Done_Body, result.Restored, result.Deleted));
                fix.IsApplied = false;
                break;

            case FixRevertOutcome.Conflict:
                toast.Show(Resources.Strings.Fixes_Revert_Conflict,
                    string.Format(Resources.Strings.Fixes_Revert_Conflict_Body, result.ConflictFile, result.Conflicts),
                    error: true);
                break;

            case FixRevertOutcome.Partial:
                toast.Show(Resources.Strings.Fixes_Revert_Partial,
                    string.Format(Resources.Strings.Fixes_Revert_Partial_Body, result.Errors), error: true);
                break;

            case FixRevertOutcome.NoRecord:
                toast.Show(Resources.Strings.Fixes_Revert_Failed,
                    Resources.Strings.Fixes_Revert_NoRecord, error: true);
                // Nothing to revert, so the button should stop offering it.
                fix.IsApplied = false;
                break;

            default: // Failed
                toast.Show(Resources.Strings.Fixes_Revert_Failed,
                    result.Error ?? Resources.Strings.Fixes_Revert_NoRecord, error: true);
                break;
        }
    }

    private (FixItemVm Fix, FixGameCardVm Game)? _pendingRevert;
    [ObservableProperty] private bool _isConfirmingRevert;
    [ObservableProperty] private string _confirmRevertTitle = "";
    [ObservableProperty] private string _confirmRevertBody = "";

    /// <summary>
    /// Run <see cref="FixAnalyzer"/> over a staged download and decide whether to proceed.
    ///
    /// <para>
    /// This is the gate between "downloaded" and "written to disk". Fixes and manifests arrive from the
    /// API as opaque archives that end up either in Steam's own folders or inside a game's install
    /// directory, so this is the last point at which their shape and contents can be judged at all.
    /// </para>
    ///
    /// <para>
    /// On a block the staged file is deleted and the user is told why. Non-blocking findings are logged
    /// and the install continues — a fix zip containing executables is entirely normal, and refusing
    /// those would break the feature rather than protect it.
    /// </para>
    /// </summary>
    /// <returns>True to continue with the install; false when it was refused.</returns>
    private static bool ScreenBeforeInstall(
        DownloadedFile file, string? destinationRoot, bool isArchive, out string? blockedReason)
    {
        blockedReason = null;
        FixAnalysis analysis;
        try
        {
            analysis = isArchive
                ? FixAnalyzer.AnalyzeArchive(file.FilePath, destinationRoot)
                : FixAnalyzer.AnalyzeLuaFile(file.FilePath);
        }
        catch (Exception ex)
        {
            // The analyzer itself failing must not become an install crash — but it also must not become
            // an implicit "allow": refuse and say so.
            AppLog.Log($"fix screen: analyzer failed on {file.FileName} — {ex.Message}");
            blockedReason = string.Format(Resources.Strings.Fixes_Toast_Blocked_Body, file.FileName, ex.Message);
            file.Discard();
            return false;
        }

        AppLog.Log($"fix screen: {file.FileName} → {(analysis.Blocked ? "BLOCKED" : "ok")} ({analysis.Summary})");

        if (!analysis.Blocked) return true;

        blockedReason = string.Format(Resources.Strings.Fixes_Toast_Blocked_Body, file.FileName, analysis.BlockReason);
        file.Discard();
        return false;
    }

    private async Task<bool> PromptSignInIfGuestAsync(string message)
    {
        if (!auth.IsGuest) return false;
        toast.Show(Resources.Strings.Fixes_SignInRequired, message);
        if (RequestSignIn is not null) await RequestSignIn();
        return true;
    }
}
