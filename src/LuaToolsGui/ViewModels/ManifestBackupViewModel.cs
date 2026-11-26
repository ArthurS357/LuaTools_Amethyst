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
    private ManifestBackupModeOption _selectedMode;

    /// <summary>Drives the folder row and the batch button: shown only while the feature is on.</summary>
    public bool IsOn => SelectedMode.Mode != ManifestBackupMode.Disabled;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BackupAllCommand))]
    private string _folder;

    /// <summary>Why the picked folder was refused, or null.</summary>
    [ObservableProperty] private string? _folderError;

    /// <summary>Progress while the batch runs, then its summary. Null = hidden.</summary>
    [ObservableProperty] private string? _status;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotRunning))]
    [NotifyCanExecuteChangedFor(nameof(BrowseCommand))]
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

    private bool CanBackupAll() => IsOn && _backups.ActiveFolder is not null;

    [RelayCommand(CanExecute = nameof(CanBackupAll))]
    private async Task BackupAllAsync(CancellationToken ct)
    {
        IsRunning = true;
        bool active = true;
        var progress = new Progress<(int Done, int Total)>(p =>
        {
            if (!active) return;
            Status = string.Format(
                CultureInfo.CurrentCulture, Resources.Strings.Settings_ManifestBackup_Running, p.Done, p.Total);
        });
        try
        {
            var summary = await _backups.BackupAllAsync(progress, ct);
            active = false;
            Status = summary is null ? Resources.Strings.Err_SteamNotFound : Describe(summary);
        }
        catch (OperationCanceledException)
        {
            active = false;
            Status = null;
        }
        catch (Exception ex) when (ex is ArgumentException or System.IO.IOException or UnauthorizedAccessException)
        {
            active = false;
            AppLog.Log($"Settings: manifest backup batch failed: {ex.GetType().Name}: {ex.Message}");
            Status = ex.Message;
        }
        finally
        {
            active = false;
            IsRunning = false;
        }
    }

    internal static string Describe(ManifestBackupSummary s) => string.Format(
        CultureInfo.CurrentCulture, Resources.Strings.Settings_ManifestBackup_Result,
        s.Count(ManifestBackupChange.New), s.Count(ManifestBackupChange.Updated),
        s.Count(ManifestBackupChange.Unchanged), s.Failed, s.WithoutManifests.Count);
}
