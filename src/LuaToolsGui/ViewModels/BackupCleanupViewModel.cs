using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuaToolsGui.Services;
using LuaToolsGui.Services.Backups;

namespace LuaToolsGui.ViewModels;

/// <summary>One row of the backup list.</summary>
public partial class BackupEntryViewModel(BackupEntry entry) : ObservableObject
{
    public BackupEntry Entry { get; } = entry;

    [ObservableProperty] private bool _isSelected;

    public string Name => Entry.Name;
    public string CreatedLabel => Entry.CreatedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    public string SizeLabel => ByteSize.Format(Entry.SizeBytes);
    public int FileCount => Entry.FileCount;
}

/// <summary>
/// Settings → Maintenance: the AmethystTool / plugin-removal backup folders piling up in the Steam root, and
/// the two ways to delete them. Every delete is confirmed first and goes through
/// <see cref="BackupService.DeleteAsync"/>, which re-validates each folder before touching any.
/// </summary>
public partial class BackupCleanupViewModel : ObservableObject
{
    /// <summary>Age past which "Delete older than 30 days" picks a backup up.</summary>
    internal const int OldAfterDays = 30;

    private readonly BackupService _backups;
    private readonly SteamService _steam;
    private readonly ToastService _toast;

    public BackupCleanupViewModel(BackupService backups, SteamService steam, ToastService toast)
    {
        _backups = backups;
        _steam = steam;
        _toast = toast;
    }

    /// <summary>Test seam: the age filter's "now".</summary>
    internal TimeProvider Clock { get; set; } = TimeProvider.System;

    /// <summary>Test seam: asks before deleting (folder count, total bytes); true = go ahead.</summary>
    internal Func<int, long, Task<bool>> ConfirmDelete { get; set; } = ConfirmWithDialogAsync;

    public ObservableCollection<BackupEntryViewModel> Entries { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _isLoading;

    /// <summary>False when the Steam folder is unknown or missing: the list is disabled and a hint shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(HasNoSteam))]
    private bool _hasSteamConfigured = true;

    public bool HasNoSteam => !HasSteamConfigured;

    public bool IsEmpty => HasSteamConfigured && !IsLoading && Entries.Count == 0;

    /// <summary>Shows the column header row.</summary>
    public bool HasEntries => Entries.Count > 0;

    /// <summary>Gates "Delete older than 30 days".</summary>
    public bool HasOld => OldEntries().Count > 0;

    /// <summary>One gate for all three commands: a second scan (Loaded fires on every visit) or an overlapping
    /// delete is dropped rather than run alongside. ponytail: drop, not queue.</summary>
    private bool _busy;

    [RelayCommand]
    private async Task Refresh()
    {
        if (_busy) return;
        _busy = true;
        try { await RescanAsync(); }
        finally { _busy = false; }
    }

    private async Task RescanAsync()
    {
        string? root = _steam.EffectivePath;
        HasSteamConfigured = root is not null && Directory.Exists(root);
        Entries.Clear();
        try
        {
            if (!HasSteamConfigured) return;
            IsLoading = true;
            foreach (var e in await _backups.ScanAsync(root!)) Entries.Add(new BackupEntryViewModel(e));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Log($"backup cleanup: scan failed: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(HasEntries));
            OnPropertyChanged(nameof(HasOld));
        }
    }

    [RelayCommand]
    private Task DeleteSelected() => DeleteAsync([.. Entries.Where(e => e.IsSelected).Select(e => e.Entry)]);

    [RelayCommand]
    private Task DeleteOlderThan30Days() => DeleteAsync(OldEntries());

    private IReadOnlyList<BackupEntry> OldEntries() =>
        BackupInventory.OlderThan(Entries.Select(e => e.Entry), Clock.GetUtcNow().UtcDateTime, OldAfterDays);

    private async Task DeleteAsync(IReadOnlyList<BackupEntry> targets)
    {
        if (_busy || targets.Count == 0 || _steam.EffectivePath is not { } root) return;
        _busy = true;
        try
        {
            if (!await ConfirmDelete(targets.Count, targets.Sum(t => t.SizeBytes))) return;

            var result = await _backups.DeleteAsync(targets, root);
            if (result.DeletedCount > 0)
                _toast.Show(Resources.Strings.Settings_Backups_Title, string.Format(CultureInfo.CurrentCulture,
                    Resources.Strings.Settings_Backups_Toast_Deleted, result.DeletedCount, ByteSize.Format(result.BytesFreed)));
            if (result.Errors.Count > 0)
                _toast.Show(Resources.Strings.Settings_Backups_Title, string.Format(CultureInfo.CurrentCulture,
                    Resources.Strings.Settings_Backups_Toast_Failed, result.Errors.Count), error: true);

            await RescanAsync();
        }
        finally { _busy = false; }
    }

    private static async Task<bool> ConfirmWithDialogAsync(int count, long bytes)
    {
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = Resources.Strings.Settings_Backups_Confirm_Title,
            Content = string.Format(CultureInfo.CurrentCulture,
                Resources.Strings.Settings_Backups_Confirm_Body, count, ByteSize.Format(bytes)),
            PrimaryButtonText = Resources.Strings.Settings_Backups_Confirm_Delete,
            CloseButtonText = Resources.Strings.Settings_Backups_Cancel,
        };
        return await box.ShowDialogAsync() == Wpf.Ui.Controls.MessageBoxResult.Primary;
    }
}
