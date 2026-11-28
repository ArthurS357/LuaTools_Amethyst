using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuaToolsGui.Services;
using LuaToolsGui.Services.Backups;
using Microsoft.Win32;

namespace LuaToolsGui.ViewModels;

/// <summary>A selectable backup mode. <see cref="Mode"/> is persisted; <see cref="Display"/> is localised.</summary>
public sealed record ManifestBackupModeOption(string Display, ManifestBackupMode Mode);

/// <summary>Settings → Manifest backup: mode, destination folder and the "back up existing games" batch.</summary>
public sealed partial class ManifestBackupViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly ManifestBackupService _backups;

    public ManifestBackupModeOption[] ModeOptions { get; } =
    [
        new(Resources.Strings.Settings_ManifestBackup_Mode_Off, ManifestBackupMode.Disabled),
        new(Resources.Strings.Settings_ManifestBackup_Mode_Folder, ManifestBackupMode.LocalFolder),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOn))]
    [NotifyCanExecuteChangedFor(nameof(BackupAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(VerifyCommand))]
    private ManifestBackupModeOption _selectedMode;

    /// <summary>Drives the folder row and the batch button: shown only while the feature is on.</summary>
    public bool IsOn => SelectedMode.Mode != ManifestBackupMode.Disabled;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BackupAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(VerifyCommand))]
    private string _folder;

    /// <summary>Why the picked folder was refused, or null.</summary>
    [ObservableProperty] private string? _folderError;

    /// <summary>Progress while the batch runs, then its summary. Null = hidden.</summary>
    [ObservableProperty] private string? _status;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotRunning))]
    [NotifyCanExecuteChangedFor(nameof(BrowseCommand))]
    [NotifyCanExecuteChangedFor(nameof(BackupAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(VerifyCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelRunCommand))]
    private bool _isRunning;

    public bool IsNotRunning => !IsRunning;

    public ManifestBackupViewModel(SettingsService settings, ManifestBackupService backups)
    {
        _settings = settings;
        _backups = backups;
        _selectedMode = ModeOptions.First(o => o.Mode == settings.ManifestBackupMode); // field init: no write-back
        _folder = settings.ManifestBackupFolder ?? "";
    }

    partial void OnSelectedModeChanged(ManifestBackupModeOption value)
    {
        _settings.ManifestBackupMode = value.Mode;
        if (value.Mode == ManifestBackupMode.Disabled) FolderError = null;
    }

    private bool CanBrowse() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanBrowse))]
    private void Browse()
    {
        var dialog = new OpenFolderDialog
        {
            Title = Resources.Strings.Settings_ManifestBackup_ChooseFolder,
            InitialDirectory = System.IO.Directory.Exists(Folder) ? Folder : "",
        };
        if (dialog.ShowDialog() == true) TrySetFolder(dialog.FolderName);
    }

    /// <summary>Persist <paramref name="path"/> when the policy accepts it; otherwise keep the old folder and say why.</summary>
    public bool TrySetFolder(string path)
    {
        if (!_backups.IsAcceptableFolder(path))
        {
            FolderError = Resources.Strings.Settings_ManifestBackup_FolderRefused;
            return false;
        }

        FolderError = null;
        _settings.ManifestBackupFolder = path;
        Folder = path;
        return true;
    }

    // Path-only (IsConfigured, no disk access): CanExecute runs on the UI thread, and an offline drive must not
    // stall it. The run itself re-checks the folder on disk. Batch and verify exclude each other via IsRunning.
    private bool CanRun() => IsOn && !IsRunning && _backups.IsConfigured;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task BackupAllAsync(CancellationToken ct) => RunAsync("batch", async progress =>
    {
        var summary = await _backups.BackupAllAsync(progress, ct);
        return summary is null ? Resources.Strings.Err_SteamNotFound : Describe(summary);
    }, Resources.Strings.Settings_ManifestBackup_Running);

    /// <summary>Settings → "Verify integrity": the read-only vault check, in the background, cancellable.</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task VerifyAsync(CancellationToken ct) => RunAsync("verify", async progress =>
    {
        var report = await _backups.VerifyVaultAsync(progress, ct);
        return report is null ? Resources.Strings.Settings_ManifestBackup_FolderRefused : Describe(report);
    }, Resources.Strings.Settings_ManifestBackup_Verifying);

    /// <summary>
    /// Stop whichever run is active. The toolkit owns each command's token source (created per execution,
    /// never disposed under us), so this only signals it: the batch stops between games and its finally still
    /// re-syncs index.json; verify stops between games too. The buttons come back when the run returns.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void CancelRun()
    {
        BackupAllCommand.Cancel();
        VerifyCommand.Cancel();
    }

    /// <summary>Shared run shell: IsRunning, progress text that a late callback cannot overwrite, and the outcome.</summary>
    private async Task RunAsync(string what, Func<IProgress<(int Done, int Total)>, Task<string>> run, string progressFormat)
    {
        IsRunning = true;
        bool active = true;
        var progress = new Progress<(int Done, int Total)>(p =>
        {
            if (!active) return;
            Status = string.Format(CultureInfo.CurrentCulture, progressFormat, p.Done, p.Total);
        });
        try
        {
            string result = await run(progress);
            active = false;
            Status = result;
        }
        catch (OperationCanceledException)
        {
            active = false;
            Status = Resources.Strings.Settings_ManifestBackup_Cancelled; // stopped on request: not an error
        }
        catch (Exception ex) when (ex is ArgumentException or System.IO.IOException or UnauthorizedAccessException)
        {
            active = false;
            AppLog.Log($"Settings: manifest backup {what} failed: {ex.GetType().Name}: {ex.Message}");
            Status = ex.Message;
        }
        finally
        {
            active = false;
            IsRunning = false;
        }
    }

    internal static string Describe(ManifestVaultReport r)
    {
        string text = string.Format(
            CultureInfo.CurrentCulture, Resources.Strings.Settings_ManifestBackup_VerifyResult,
            r.Count(ManifestVaultHealth.Healthy), r.Count(ManifestVaultHealth.LuaOnly),
            r.Count(ManifestVaultHealth.Corrupt), r.Count(ManifestVaultHealth.Unreadable));
        return r.NeedsAttention.Count == 0 ? text : text + " " + string.Format(
            CultureInfo.CurrentCulture, Resources.Strings.Settings_ManifestBackup_VerifyAttention,
            string.Join(", ", r.NeedsAttention.Select(static id => id.ToString(CultureInfo.InvariantCulture))));
    }

    internal static string Describe(ManifestBackupSummary s) => string.Format(
        CultureInfo.CurrentCulture, Resources.Strings.Settings_ManifestBackup_Result,
        s.Count(ManifestBackupChange.New), s.Count(ManifestBackupChange.Updated),
        s.Count(ManifestBackupChange.Unchanged), s.Failed, s.WithoutManifests.Count);
}
