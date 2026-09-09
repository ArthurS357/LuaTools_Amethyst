using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuaToolsGui.Models;
using LuaToolsGui.Services;

namespace LuaToolsGui.ViewModels;

/// <summary>A game card in the Fixes grid.</summary>
public partial class FixGameCardVm(DenuvoGameListing g) : ObservableObject
{
    public string AppId { get; } = g.AppId;
    public string Name { get; } = g.Name;
    public string? HeaderImage { get; } = g.HeaderImage;
    public int FixCount { get; } = g.FixCount;
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

/// <summary>One fix (release) in the per-game flyout.</summary>
public partial class FixItemVm(DenuvoFix f) : ObservableObject
{
    public string Id { get; } = f.Id;
    public string Title { get; } = f.Title;
    public string? Description { get; } = f.Description;
    public IReadOnlyList<DenuvoTag> Tags { get; } = f.Tags;
    public bool HasManifest { get; } = f.HasManifest;
    public bool HasFix { get; } = f.HasFix;
    public string? ManifestFilename { get; } = f.ManifestFilename;
    public string? FixFilename { get; } = f.FixFilename;
    public string DateLabel { get; } = FormatDate(f.CreatedAt);

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
    [NotifyPropertyChangedFor(nameof(CanDownloadFix), nameof(FixHint))]
    private bool _isApplied;

    public bool CanDownloadFix => HasFix && GameInstalled && !IsApplied;

    /// <summary>Why the Fix button is greyed out, or null when it isn't. A null ToolTip shows nothing,
    /// so this doubles as the "should there be a tooltip at all" test.</summary>
    public string? FixHint =>
        IsApplied ? Resources.Strings.Fixes_Applied_Hint
        : GameInstalled ? null
        : Resources.Strings.Fixes_NotInstalled_Hint;

    private static string FormatDate(string? iso) =>
        DateTimeOffset.TryParse(iso, out var d) ? d.UtcDateTime.ToString("d MMM yyyy") : "";
}

/// <summary>
/// "Fixes" page: browse games with Denuvo fixes (grid + search + tag filter), open a game to see its
/// fixes, and download a fix's manifest (force-locked lua install) or fix zip (extract into the game
/// folder if installed). Downloads are auth-gated and count toward the 25/day limit (server-side).
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

    public FixesViewModel(
        LuaToolsApiClient api, AuthService auth, LuaInstaller installer, SteamService steam,
        SteamLibraryService library, CoverCache covers, ToastService toast, SettingsService settings,
        DenuvoFixService fixes)
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
        InitPageSize(settings.FixesPageSize);
    }

    /// <summary>Set by App so a guest hitting a download is sent through the Discord sign-in flow.</summary>
    public Func<Task>? RequestSignIn { get; set; }

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

    /// <param name="force">True to re-fetch even if already loaded (the Refresh button); otherwise the
    /// listing loads once per session.</param>
    public async Task LoadAsync(bool force = false)
    {
        if (!force && _allGames.Count > 0) return; // load once per session
        IsLoading = true;
        try
        {
            var data = await api.GetDenuvoListingsAsync();
            if (data is null)
            {
                EmptyMessage = Resources.Strings.Fixes_Err_Load;
                return;
            }

            _allGames = data.Games.Select(g => new FixGameCardVm(g)).ToList();
            Tags.Clear();
            foreach (var t in data.Tags) Tags.Add(new TagPillVm(t));

            // "My games" filter source: the same stplug-in scan the Manage page uses, so the toggle
            // shows only games the user actually added. Scanned once per listing load, off the UI thread.
            _installedAppIds = steam.StPlugInDir is { } dir
                ? await Task.Run(() => LuaInstaller.EnumerateInstalled(dir).Select(i => i.AppId).ToHashSet())
                : [];
            OnPropertyChanged(nameof(MyGamesHint));

            ApplyFilter();
            if (_allGames.Count == 0) EmptyMessage = Resources.Strings.Fixes_Empty_None;
        }
        catch
        {
            EmptyMessage = Resources.Strings.Fixes_Err_Load;
        }
        finally
        {
            IsLoading = false;
            Loaded = true; // gates the filter pills: they appear only once the listing settled
        }
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
        if (_allGames.Count == 0)
            await LoadAsync();

        var game = _allGames.FirstOrDefault(g => g.AppId == appId.ToString());
        if (game is null) return;

        SearchText = "";
        SelectedTagId = null;
        ApplyFilter();

        await OpenGame(game);
    }

    [RelayCommand]
    private async Task OpenGame(FixGameCardVm game)
    {
        SelectedGame = game;
        _ = game.EnsureCoverAsync(covers); // ensure the flyout header image is cached too
        Fixes.Clear();
        _allFixes = [];
        FixTags.Clear();
        SelectedFixTagId = null;
        IsLoadingFixes = true;
        try
        {
            var data = await api.GetDenuvoFixesAsync(game.AppId);
            if (data is not null)
            {
                _allFixes = data.Fixes.Select(f => new FixItemVm(f)).ToList();

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
                }

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
        catch { /* leave empty — flyout shows "no fixes" */ }
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
    private void CloseDetail() => SelectedGame = null;

    // ── Downloads ────────────────────────────────────────────────────

    [RelayCommand]
    private Task DownloadManifest(FixItemVm fix) => RunDownload(fix, "manifest");

    [RelayCommand]
    private Task DownloadFix(FixItemVm fix) => RunDownload(fix, "fix");

    private async Task RunDownload(FixItemVm fix, string slot)
    {
        if (IsBusy) return;
        if (await PromptSignInIfGuestAsync(Resources.Strings.Fixes_SignIn)) return;
        if (SelectedGame is not { } game) return;
        if (!long.TryParse(game.AppId, out long appId)) return;

        IsBusy = true;
        IsProgressIndeterminate = true;
        Progress = 0;
        try
        {
            string fallback = slot == "manifest"
                ? fix.ManifestFilename ?? $"{game.AppId}.zip"
                : fix.FixFilename ?? $"{game.AppId}_fix.zip";

            var prog = new Progress<double?>(p =>
            {
                IsProgressIndeterminate = p is null;
                if (p is not null) Progress = p.Value * 100;
            });

            var file = await api.DownloadDenuvoAsync(fix.Id, slot, fallback, prog);

            if (slot == "manifest")
                InstallManifest(file, appId, game.Name);
            else
                ApplyFix(file, appId, fix, game.Name);
        }
        catch (ApiException ex)
        {
            toast.Show(Resources.Strings.Fixes_Toast_DownloadFailed, ex.Message, error: true);
        }
        catch (Exception)
        {
            toast.Show(Resources.Strings.Fixes_Toast_DownloadFailed, Resources.Strings.Fixes_Toast_DownloadFailed_Body, error: true);
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    /// <summary>Manifest slot: install into Steam force-LOCKED (Denuvo fixes must stay version-pinned).</summary>
    private void InstallManifest(DownloadedFile file, long appId, string gameName)
    {
        bool isZip = file.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

        // Screen the staged download before it reaches Steam's folders. InstallZip flattens entries to
        // fixed names (<appid>.lua / the manifest's own name) so it is not path-traversable — hence the
        // null destination root — but size, shape and lua-content checks all still apply here.
        if (!ScreenBeforeInstall(file, destinationRoot: null, isZip)) return;

        var result = isZip
            ? installer.InstallZip(file.FilePath, appId, forceLocked: true)
            : installer.InstallLuaFile(file.FilePath, appId, forceLocked: true);
        DeleteStaged(file.FilePath); // consumed by the install — drop the temp staging copy

        if (result.AnyFailed)
        {
            toast.Show(Resources.Strings.Fixes_Toast_InstallFailed,
                result.Error ?? Resources.Strings.Fixes_Toast_InstallFailed_Body,
                error: true);
            return;
        }

        bool restarted = steam.RestartSteam();
        toast.Show(Resources.Strings.Fixes_Toast_FixInstalled, restarted
            ? string.Format(Resources.Strings.Fixes_Toast_FixInstalled_Restarting, gameName)
            : string.Format(Resources.Strings.Fixes_Toast_FixInstalled_Restart, gameName));
    }

    /// <summary>Fix slot: only applicable if the game is installed — extract the zip into its folder.</summary>
    /// <remarks>
    /// The extract itself lives in <see cref="DenuvoFixService"/>, which also writes the revert record
    /// that makes the fix undoable. This method stays the security gate (screening) and the single toast
    /// emitter for the action — one outcome, one toast.
    /// </remarks>
    private void ApplyFix(DownloadedFile file, long appId, FixItemVm fix, string gameName)
    {
        string? installDir = library.GetInstallDir(appId);
        if (installDir is null)
        {
            toast.Show(Resources.Strings.Fixes_Toast_GameNotFound, string.Format(Resources.Strings.Fixes_Toast_GameNotFound_Body, gameName), error: true);
            return;
        }

        // Screen the archive against the folder it would be extracted into, BEFORE anything is written.
        if (!ScreenBeforeInstall(file, destinationRoot: installDir, isArchive: true)) return;

        try
        {
            var result = fixes.Apply(file.FilePath, appId, fix.Id, installDir);

            switch (result.Outcome)
            {
                case FixApplyOutcome.Applied:
                    toast.Show(Resources.Strings.Fixes_Toast_FixApplied,
                        string.Format(Resources.Strings.Fixes_Toast_FixApplied_Body, gameName));
                    break;

                case FixApplyOutcome.PartiallyApplied:
                    toast.Show(Resources.Strings.Fixes_Toast_PartiallyApplied,
                        string.Format(Resources.Strings.Fixes_Toast_PartiallyApplied_Body, result.Failed),
                        error: true);
                    break;

                default: // RecordFailed / Failed — nothing was applied
                    toast.Show(Resources.Strings.Fixes_Toast_CouldntApply,
                        result.Error ?? Resources.Strings.Fixes_Toast_InstallFailed_Body, error: true);
                    break;
            }

            // A partial apply still backed files up and still wrote a record, so it IS applied and MUST be
            // revertable — offer Revert for both, and only for those.
            if (result.Outcome is FixApplyOutcome.Applied or FixApplyOutcome.PartiallyApplied)
                fix.IsApplied = true;
        }
        finally
        {
            DeleteStaged(file.FilePath); // the service is done with it — drop the temp staging copy
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
    private bool ScreenBeforeInstall(DownloadedFile file, string? destinationRoot, bool isArchive)
    {
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
            toast.Show(Resources.Strings.Fixes_Toast_Blocked,
                string.Format(Resources.Strings.Fixes_Toast_Blocked_Body, file.FileName, ex.Message), error: true);
            DeleteStaged(file.FilePath);
            return false;
        }

        AppLog.Log($"fix screen: {file.FileName} → {(analysis.Blocked ? "BLOCKED" : "ok")} ({analysis.Summary})");

        if (!analysis.Blocked) return true;

        toast.Show(Resources.Strings.Fixes_Toast_Blocked,
            string.Format(Resources.Strings.Fixes_Toast_Blocked_Body, file.FileName, analysis.BlockReason),
            error: true);
        DeleteStaged(file.FilePath);
        return false;
    }

    /// <summary>Best-effort delete of a staged download after it's been consumed by an install.</summary>
    private static void DeleteStaged(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    private async Task<bool> PromptSignInIfGuestAsync(string message)
    {
        if (!auth.IsGuest) return false;
        toast.Show(Resources.Strings.Fixes_SignInRequired, message);
        if (RequestSignIn is not null) await RequestSignIn();
        return true;
    }
}
