using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuaToolsGui.Services;

namespace LuaToolsGui.ViewModels;

/// <summary>Which set of cleanup scopes the consolidated confirmation dialog offers.</summary>
public enum GameCleanupDialogMode
{
    Manage,
    Depots,
}

/// <summary>
/// ViewModel for the consolidated game cleanup / uninstall confirmation dialog shared between
/// Manage (4 scopes) and Depots (3 scopes), with async inspection and reentrancy locking.
/// </summary>
public sealed partial class GameCleanupDialogViewModel : ObservableObject
{
    private readonly GameCleanupService _cleanup;
    private CancellationTokenSource? _inspectCts;

    public GameCleanupDialogViewModel(GameCleanupService cleanup) => _cleanup = cleanup;

    [ObservableProperty] private long _appId;
    [ObservableProperty] private string _gameName = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGameOnlyOption))]
    [NotifyPropertyChangedFor(nameof(ShowEverythingOption))]
    [NotifyPropertyChangedFor(nameof(ShowManifestsOnlyOption))]
    [NotifyPropertyChangedFor(nameof(ShowSizeLine))]
    private GameCleanupDialogMode _mode = GameCleanupDialogMode.Manage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSizeLine))]
    [NotifyPropertyChangedFor(nameof(SizeLabel))]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCleanupCommand))]
    private bool _isInspecting = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotRemoving))]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCleanupCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isRemoving;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSelectGameOnly))]
    [NotifyPropertyChangedFor(nameof(CanSelectLuaOnly))]
    [NotifyPropertyChangedFor(nameof(CanSelectLuaAndManifests))]
    [NotifyPropertyChangedFor(nameof(CanSelectManifestsOnly))]
    [NotifyPropertyChangedFor(nameof(CanSelectEverything))]
    [NotifyPropertyChangedFor(nameof(ShowSizeLine))]
    [NotifyPropertyChangedFor(nameof(SizeLabel))]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCleanupCommand))]
    private GameCleanupAvailability _availability = new(false, false, false, null, null);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGameOnlySelected))]
    [NotifyPropertyChangedFor(nameof(IsLuaOnlySelected))]
    [NotifyPropertyChangedFor(nameof(IsLuaAndManifestsSelected))]
    [NotifyPropertyChangedFor(nameof(IsManifestsOnlySelected))]
    [NotifyPropertyChangedFor(nameof(IsEverythingSelected))]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCleanupCommand))]
    private GameCleanupScope _selectedScope = GameCleanupScope.LuaOnly;

    [ObservableProperty] private string? _errorMessage;

    public GameCleanupResult? Result { get; private set; }
    public Action<bool>? CloseWith { get; set; }

    public bool NotRemoving => !IsRemoving;

    public bool ShowGameOnlyOption => Mode == GameCleanupDialogMode.Manage;
    public bool ShowEverythingOption => Mode == GameCleanupDialogMode.Manage;
    public bool ShowManifestsOnlyOption => Mode == GameCleanupDialogMode.Depots;

    public bool CanSelectGameOnly => Availability.HasInstalledGame;
    public bool CanSelectLuaOnly => Availability.HasActiveLua;
    public bool CanSelectLuaAndManifests => Availability.HasActiveLua || Availability.HasStoredBuilds || Availability.HasInstalledGame;
    public bool CanSelectManifestsOnly => Availability.HasActiveLua || Availability.HasStoredBuilds || Availability.HasInstalledGame;
    public bool CanSelectEverything => Availability.HasActiveLua || Availability.HasInstalledGame || Availability.HasStoredBuilds;

    public bool ShowSizeLine =>
        Mode == GameCleanupDialogMode.Manage && (IsInspecting || Availability.EstimatedGameBytes is > 0);

    public string SizeLabel => IsInspecting
        ? Resources.Strings.Cleanup_Size_Calculating
        : Availability.EstimatedGameBytes is { } bytes and > 0
            ? string.Format(CultureInfo.CurrentCulture, Resources.Strings.Cleanup_Size_Estimated,
                ByteSize.Format(bytes, CultureInfo.CurrentCulture))
            : string.Empty;

    public string LuaOnlyDesc => string.Format(
        CultureInfo.CurrentCulture,
        Resources.Strings.Cleanup_Scope_LuaOnly_Desc,
        AppId);

    public bool IsGameOnlySelected
    {
        get => SelectedScope == GameCleanupScope.GameFilesOnly;
        set { if (value) SelectedScope = GameCleanupScope.GameFilesOnly; }
    }

    public bool IsLuaOnlySelected
    {
        get => SelectedScope == GameCleanupScope.LuaOnly;
        set { if (value) SelectedScope = GameCleanupScope.LuaOnly; }
    }

    public bool IsLuaAndManifestsSelected
    {
        get => SelectedScope == GameCleanupScope.LuaAndManifests;
        set { if (value) SelectedScope = GameCleanupScope.LuaAndManifests; }
    }

    public bool IsManifestsOnlySelected
    {
        get => SelectedScope == GameCleanupScope.ManifestsOnly;
        set { if (value) SelectedScope = GameCleanupScope.ManifestsOnly; }
    }

    public bool IsEverythingSelected
    {
        get => SelectedScope == GameCleanupScope.Everything;
        set { if (value) SelectedScope = GameCleanupScope.Everything; }
    }

    public bool CanConfirm => !IsRemoving && !IsInspecting && IsScopeEnabled(SelectedScope);

    public bool IsScopeEnabled(GameCleanupScope scope) => scope switch
    {
        GameCleanupScope.GameFilesOnly => CanSelectGameOnly,
        GameCleanupScope.LuaOnly => CanSelectLuaOnly,
        GameCleanupScope.LuaAndManifests => CanSelectLuaAndManifests,
        GameCleanupScope.ManifestsOnly => CanSelectManifestsOnly,
        GameCleanupScope.Everything => CanSelectEverything,
        _ => false,
    };

    /// <summary>Inspect the game off the UI thread and choose the safest default enabled scope.</summary>
    public async Task LoadAsync(long appId, string gameName, GameCleanupDialogMode mode, CancellationToken ct = default)
    {
        _inspectCts?.Cancel();
        _inspectCts?.Dispose();
        _inspectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var linked = _inspectCts.Token;

        AppId = appId;
        GameName = gameName;
        Mode = mode;
        Result = null;
        ErrorMessage = null;
        OnPropertyChanged(nameof(LuaOnlyDesc));

        IsInspecting = true;
        try
        {
            var availability = await _cleanup.InspectAsync(appId, linked);
            if (linked.IsCancellationRequested) return;
            Availability = availability;
            SelectedScope = PickDefaultScope(mode, availability);
        }
        catch (OperationCanceledException)
        {
            /* dialog closed during inspection */
        }
        finally
        {
            IsInspecting = false;
        }
    }

    private static GameCleanupScope PickDefaultScope(GameCleanupDialogMode mode, GameCleanupAvailability a) =>
        mode == GameCleanupDialogMode.Manage
            ? a.HasActiveLua ? GameCleanupScope.LuaOnly
            : a.HasInstalledGame ? GameCleanupScope.GameFilesOnly
            : GameCleanupScope.LuaAndManifests
            : a.HasActiveLua ? GameCleanupScope.LuaAndManifests
            : GameCleanupScope.ManifestsOnly;

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private async Task ConfirmCleanupAsync()
    {
        if (IsRemoving || IsInspecting || !IsScopeEnabled(SelectedScope)) return;

        IsRemoving = true;
        ErrorMessage = null;
        GameCleanupResult outcome;
        try
        {
            outcome = await _cleanup.CleanAsync(AppId, SelectedScope);
        }
        finally
        {
            IsRemoving = false;
        }

        Result = outcome;
        if (outcome.IsBlocked)
        {
            ErrorMessage = GameCleanupMessages.Describe(GameName, outcome);
            return;
        }

        // Only after the lock is released: the window's Closing handler cancels any close while
        // IsRemoving is true, which would leave a finished cleanup's dialog stuck open.
        CloseWith?.Invoke(true);
    }

    /// <summary>Cancel any pending disk inspection without closing the dialog.</summary>
    public void CancelInspection() => _inspectCts?.Cancel();

    [RelayCommand(CanExecute = nameof(NotRemoving))]
    private void Cancel()
    {
        if (IsRemoving) return;
        CancelInspection();
        CloseWith?.Invoke(false);
    }
}
