using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuaToolsGui.Services;
using LuaToolsGui.Services.Downloads;
using Microsoft.Win32;

namespace LuaToolsGui.ViewModels;

/// <summary>One depot offered in the download picker.</summary>
/// <remarks>
/// Separate from <see cref="DepotRow"/> on purpose: that row describes what a lua DECLARES and drives the
/// enable/lock switches, while this one describes what can be FETCHED and carries the tick state. Merging
/// them would put a download concern into the record the whole Depots table binds to.
/// </remarks>
public partial class DepotPickRow : ObservableObject
{
    public required long DepotId { get; init; }
    public required string Title { get; init; }
    public required string Meta { get; init; }
    public required long Size { get; init; }

    /// <summary>The manifest Steam currently ships for this depot; null means there is nothing to fetch.</summary>
    public required string? ManifestId { get; init; }

    public string? Os { get; init; }
    public string? Language { get; init; }
    public bool HasKey { get; init; }

    /// <summary>Why this depot can't be picked, or null when it can.</summary>
    public string? BlockedReason { get; init; }

    public bool CanPick => BlockedReason is null;

    [ObservableProperty] private bool _isSelected;
}

public partial class BuildsViewModel
{
    // ── Picker state ─────────────────────────────────────────────────

    /// <summary>Every depot of the selected game that could be downloaded, pickable or not.</summary>
    public ObservableCollection<DepotPickRow> DepotPicks { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDepotPicks))]
    private bool _isDepotPickerOpen;

    public bool HasDepotPicks => DepotPicks.Any(p => p.CanPick);

    /// <summary>
    /// False when this build carries no usable pin for the downloader, which disables the feature outright
    /// rather than letting it fetch an unverified binary. See <see cref="AppConfig"/>.
    /// </summary>
    /// <remarks>Instance, not static, so the picker can bind to it directly.</remarks>
    public bool IsDepotDownloadAvailable => DepotDownloaderService.PinIsUsable;

    /// <summary>Label on the confirm button, carrying how many depots the tick state currently commits to.</summary>
    public string DepotConfirmLabel => string.Format(
        CultureInfo.CurrentCulture, Resources.Strings.Builds_Depot_Confirm,
        DepotPicks.Count(p => p.IsSelected && p.CanPick));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpaceLabel))]
    [NotifyPropertyChangedFor(nameof(HasEnoughSpace))]
    private string _depotOutDir = "";

    /// <summary>
    /// Free bytes on <see cref="DepotOutDir"/>'s volume. Cached rather than computed in the label's getter:
    /// <c>AvailableFreeSpace</c> is a syscall and the label re-evaluates on every tick change.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpaceLabel))]
    [NotifyPropertyChangedFor(nameof(HasEnoughSpace))]
    private long? _freeBytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpaceLabel))]
    [NotifyPropertyChangedFor(nameof(HasEnoughSpace))]
    [NotifyPropertyChangedFor(nameof(DepotConfirmLabel))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmDepotDownloadCommand))]
    private long _requiredBytes;

    public string SpaceLabel => string.Format(
        CultureInfo.CurrentCulture,
        Resources.Strings.Builds_Depot_Space,
        ByteSize.Format(RequiredBytes),
        FreeBytes is { } free ? ByteSize.Format(free) : "—",
        DepotDownloaderService.DriveOf(DepotOutDir));

    /// <summary>
    /// True when the budget clears. Advisory only — the download re-checks before allocating and again
    /// between depots, because the volume is shared with everything else on the machine.
    /// </summary>
    public bool HasEnoughSpace => FreeBytes is not { } free || RequiredBytes <= free;

    // ── Run state ────────────────────────────────────────────────────

    // Progress, pause/resume, cancel-and-clean-up and the per-depot phase line all moved to the Downloads
    // page when depot downloads joined the app-wide queue. What is left here is the handoff: this page
    // decides WHAT to download and where, and reports that it was sent.

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDepotError))]
    [NotifyPropertyChangedFor(nameof(IsDepotStripVisible))]
    private string? _depotDownloadError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDepotResult))]
    [NotifyPropertyChangedFor(nameof(IsDepotStripVisible))]
    private string? _depotDownloadDone;

    public bool HasDepotError => !string.IsNullOrEmpty(DepotDownloadError);

    public bool HasDepotResult => !string.IsNullOrEmpty(DepotDownloadDone);

    /// <summary>The strip under the depot list carries either a failure or the handoff notice.</summary>
    public bool IsDepotStripVisible => HasDepotError || HasDepotResult;

    // ── Building the pick list ───────────────────────────────────────

    /// <summary>
    /// Refresh the picker from the depot info just loaded. Selection is re-derived rather than preserved:
    /// this runs when the game or the inspected build changes, and carrying ticks across would offer to
    /// download a depot the user picked for a different title.
    /// </summary>
    private void RebuildDepotPicks(AppDepotInfo info, LuaContents? lua)
    {
        foreach (var old in DepotPicks) old.PropertyChanged -= OnPickChanged;
        DepotPicks.Clear();

        var declared = Declarations(lua);
        bool canFetch = _depotTool.CanFetchManifests;

        foreach (var d in info.Depots.OrderBy(d => d.Id))
        {
            // Shared redistributables resolve their gid and size from the owning app, and DLC entitlements
            // have no content of their own. Neither is something this picker can budget honestly, so they
            // stay out rather than appearing as un-downloadable noise.
            if (d.IsShared || d.IsDlc || d.Size <= 0) continue;

            declared.TryGetValue(d.Id, out var entry);
            bool hasKey = entry?.Key is { Length: 64 };
            bool cached = d.PublicManifestId is { } gid && _depotTool.ResolveManifestPath(d.Id, gid) is not null;

            string? blocked =
                d.PublicManifestId is null ? Resources.Strings.Builds_Depot_NeedsManifest
                : !hasKey ? Resources.Strings.Builds_Depot_NeedsKey
                // A guest can still download depots whose manifest Steam already has; only a MISSING one
                // needs an account. Decided locally so the picker never makes a request to grey a row out.
                : !cached && !canFetch ? Resources.Strings.Builds_Depot_NeedsSignIn
                : null;

            var row = new DepotPickRow
            {
                DepotId = d.Id,
                Title = entry?.Comment ?? Resources.Strings.Manage_Depot,
                Meta = BuildPickMeta(d),
                Size = d.Size,
                ManifestId = d.PublicManifestId,
                Os = d.Os,
                Language = d.Language,
                HasKey = hasKey,
                BlockedReason = blocked,
                IsSelected = blocked is null && MatchesThisMachine(d),
            };
            row.PropertyChanged += OnPickChanged;
            DepotPicks.Add(row);
        }

        OnPropertyChanged(nameof(HasDepotPicks));
        RecalculateRequiredBytes();
    }

    private static string BuildPickMeta(ContentDepot d)
    {
        var meta = new List<string> { d.Id.ToString(CultureInfo.InvariantCulture), ByteSize.Format(d.Size) };
        if (!string.IsNullOrWhiteSpace(d.Os)) meta.Add(PrettyOs(d.Os));
        if (!string.IsNullOrWhiteSpace(d.Language)) meta.Add(d.Language!);
        return string.Join("  ·  ", meta);
    }

    /// <summary>
    /// Whether a depot is for THIS machine — the default tick state.
    /// </summary>
    /// <remarks>
    /// Both fields are absent far more often than not, and absent means "applies to everything": the bulk
    /// of a game's content is one platform-agnostic, language-agnostic depot. So the rule is exclusion, not
    /// inclusion — a depot is skipped only when it explicitly declares a platform or a language that is not
    /// this one. Getting that backwards would leave the default selection empty for most titles.
    /// </remarks>
    internal static bool MatchesThisMachine(ContentDepot d) =>
        (string.IsNullOrWhiteSpace(d.Os) || d.Os.Contains("windows", StringComparison.OrdinalIgnoreCase))
        && (string.IsNullOrWhiteSpace(d.Language)
            || d.Language.Equals(SteamLanguageName(CultureInfo.CurrentUICulture), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Steam's own name for a UI culture's language (its <c>dlclanguage</c> values), falling back to
    /// English — which is what a depot list uses when it ships one language.
    /// </summary>
    internal static string SteamLanguageName(CultureInfo culture) => culture.TwoLetterISOLanguageName switch
    {
        "pt" => culture.Name.StartsWith("pt-BR", StringComparison.OrdinalIgnoreCase) ? "brazilian" : "portuguese",
        "zh" => culture.Name.Contains("Hant", StringComparison.OrdinalIgnoreCase) ? "tchinese" : "schinese",
        "es" => culture.Name.StartsWith("es-419", StringComparison.OrdinalIgnoreCase) ? "latam" : "spanish",
        "de" => "german",
        "fr" => "french",
        "it" => "italian",
        "ja" => "japanese",
        "ko" => "koreana",
        "ru" => "russian",
        "pl" => "polish",
        "nl" => "dutch",
        "da" => "danish",
        "fi" => "finnish",
        "nb" or "no" => "norwegian",
        "sv" => "swedish",
        "cs" => "czech",
        "hu" => "hungarian",
        "el" => "greek",
        "tr" => "turkish",
        "th" => "thai",
        "uk" => "ukrainian",
        "bg" => "bulgarian",
        "ro" => "romanian",
        "vi" => "vietnamese",
        "id" => "indonesian",
        "ar" => "arabic",
        _ => "english",
    };

    private void OnPickChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DepotPickRow.IsSelected)) RecalculateRequiredBytes();
    }

    private void RecalculateRequiredBytes()
    {
        RequiredBytes = DepotPicks.Where(p => p.IsSelected && p.CanPick).Sum(p => p.Size);
        // Not covered by RequiredBytes' change notification: swapping two equally sized depots changes the
        // COUNT on the button without moving the total, and the label would then contradict the ticks.
        OnPropertyChanged(nameof(DepotConfirmLabel));
        ConfirmDepotDownloadCommand.NotifyCanExecuteChanged();
    }

    partial void OnDepotOutDirChanged(string value) =>
        FreeBytes = string.IsNullOrWhiteSpace(value) ? null : DepotDownloaderService.FreeSpaceFor(value);

    // ── Commands ─────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanStartDepotDownload))]
    private void StartDepotDownload()
    {
        DepotDownloadError = null;
        DepotDownloadDone = null;


        // Chosen HERE, before the picker is confirmed, so the free-space figure is on screen while the user
        // is still deciding what to tick rather than after they commit.
        DepotOutDir = Path.Combine(DownloadsFolder(), "LuaTools Depots",
            (ActiveGame?.AppId ?? 0).ToString(CultureInfo.InvariantCulture));

        IsDepotPickerOpen = true;
    }

    // No busy check any more: the queue dedupes on the game's depot key, so opening the picker again while
    // a selection for the same game is running joins that item rather than starting a rival download.
    private bool CanStartDepotDownload() => HasSelection;

    [RelayCommand]
    private void ChangeDepotFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = Resources.Strings.Builds_Depot_Destination,
            InitialDirectory = Directory.Exists(DepotOutDir) ? DepotOutDir : DownloadsFolder(),
        };
        if (dialog.ShowDialog() == true) DepotOutDir = dialog.FolderName;
    }

    [RelayCommand]
    private void SelectAllDepots()
    {
        foreach (var p in DepotPicks.Where(p => p.CanPick)) p.IsSelected = true;
    }

    [RelayCommand]
    private void SelectNoDepots()
    {
        foreach (var p in DepotPicks) p.IsSelected = false;
    }

    /// <summary>
    /// Hand the ticked depots to <see cref="DownloadQueue"/> and close the picker.
    /// </summary>
    /// <remarks>
    /// Returns as soon as the job is queued rather than awaiting the transfer: a depot selection can run for
    /// hours, and holding this page's command open for it is what made the download die whenever the user
    /// navigated away. The Downloads page owns the progress, the pause/resume and the cancel-with-cleanup
    /// from here on.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanConfirmDepotDownload))]
    private void ConfirmDepotDownload()
    {
        if (ActiveGame is not { } game) return;

        var picked = DepotPicks
            .Where(p => p.IsSelected && p.CanPick)
            .Select(p => new DepotSelection(p.DepotId, p.ManifestId, null, p.Size))
            .ToList();
        if (picked.Count == 0) return;

        IsDepotPickerOpen = false;
        DepotDownloadError = null;

        string outDir = DepotOutDir;
        string name = game.Name ?? game.AppId.ToString(CultureInfo.InvariantCulture);

        _queue.Enqueue(_jobs.CreateDepotJob(game.AppId, name, picked, outDir));

        DepotDownloadDone = string.Format(CultureInfo.CurrentCulture,
            Resources.Strings.Builds_Depot_Queued, picked.Count);
    }

    private bool CanConfirmDepotDownload() => RequiredBytes > 0;

    /// <summary>Close the picker without queueing anything.</summary>
    [RelayCommand]
    private void CancelDepotDownload() => IsDepotPickerOpen = false;

    /// <summary>Jump to the Downloads page, from the "sent to Downloads" notice.</summary>
    [RelayCommand]
    private void OpenDownloads() => NavigateToDownloads?.Invoke();

    /// <summary>Set by App: bring the Downloads page up.</summary>
    public Action? NavigateToDownloads { get; set; }

    [RelayCommand]
    private void OpenDepotFolder()
    {
        if (Directory.Exists(DepotOutDir)) SteamService.OpenUrl(DepotOutDir);
    }

    /// <summary>The user's Downloads folder, honouring a relocated one.</summary>
    /// <remarks>
    /// There is no %DOWNLOADS% variable and <c>Environment.SpecialFolder</c> has no member for it, so this
    /// reads FOLDERID_Downloads out of the shell folder registry. Falling back to
    /// <c>%USERPROFILE%\Downloads</c> would be wrong for exactly the people this matters to most: anyone
    /// who moved Downloads onto a bigger drive is precisely the person about to fetch tens of GB of depot
    /// content, and the naive path would send it to the drive they moved it off.
    /// </remarks>
    internal static string DownloadsFolder()
    {
        const string DownloadsGuid = "{374DE290-123F-4565-9164-39C4925E467B}";
        try
        {
            // "User Shell Folders" holds the UNEXPANDED form (e.g. "%USERPROFILE%\Downloads"); the sibling
            // "Shell Folders" key can be stale until the shell rewrites it.
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
            if (key?.GetValue(DownloadsGuid) is string raw && raw.Length > 0)
            {
                string expanded = Environment.ExpandEnvironmentVariables(raw);
                if (Directory.Exists(expanded)) return expanded;
            }
        }
        catch (Exception ex) when (ex is IOException or System.Security.SecurityException
                                      or UnauthorizedAccessException)
        {
            // Registry unreadable — fall through to the profile default.
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }
}
