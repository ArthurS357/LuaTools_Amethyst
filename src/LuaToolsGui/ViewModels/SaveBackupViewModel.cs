using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuaToolsGui.Services;
using LuaToolsGui.Services.Backups;

namespace LuaToolsGui.ViewModels;

/// <summary>One backup zip in the dialog's list.</summary>
public sealed class SaveBackupRow(SaveBackupEntry entry)
{
    public SaveBackupEntry Entry { get; } = entry;
    public string Title => Entry.Name;
    public string Subtitle => string.Create(CultureInfo.CurrentCulture,
        $"{Entry.CreatedUtc.ToLocalTime():g} · {ByteSize.Format(Entry.SizeBytes)}");
}

/// <summary>Dialog behind the Manage page's "Save backup…" action: create, restore and delete the zips of
/// one game's <c>userdata</c> folder.</summary>
public sealed partial class SaveBackupViewModel(SaveBackupService backups, ToastService toast) : ObservableObject
{
    public long AppId { get; private set; }
    public string GameName { get; private set; } = "";

    public ObservableCollection<SaveBackupRow> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotBusy))]
    private bool _isBusy;

    public bool NotBusy => !IsBusy;
    public bool IsEmpty => Items.Count == 0;

    public void Load(long appId, string gameName)
    {
        AppId = appId;
        GameName = gameName;
        OnPropertyChanged(nameof(AppId));
        OnPropertyChanged(nameof(GameName));
        Reload();
    }

    private void Reload()
    {
        Items.Clear();
        foreach (var e in backups.ListBackups(AppId)) Items.Add(new SaveBackupRow(e));
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var r = await backups.BackupSavesAsync(AppId);
            if (!Report(r, string.Format(CultureInfo.CurrentCulture,
                    Resources.Strings.Saves_Toast_Created, r.Files, ByteSize.Format(r.Bytes))))
                return;
            Reload();
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task RestoreAsync(SaveBackupRow row)
    {
        if (IsBusy) return;
        if (!await ConfirmAsync(string.Format(CultureInfo.CurrentCulture,
                Resources.Strings.Saves_Confirm_Restore, row.Title), Resources.Strings.Saves_Restore)) return;

        IsBusy = true;
        try
        {
            var r = await backups.RestoreBackupAsync(row.Entry.FullPath, AppId);
            Report(r, string.Format(CultureInfo.CurrentCulture, Resources.Strings.Saves_Toast_Restored, r.Files));
            Reload(); // the safety copy taken before the restore is a new entry
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task DeleteAsync(SaveBackupRow row)
    {
        if (IsBusy) return;
        if (!await ConfirmAsync(string.Format(CultureInfo.CurrentCulture,
                Resources.Strings.Saves_Confirm_Delete, row.Title), Resources.Strings.Saves_Delete)) return;

        if (backups.DeleteBackup(row.Entry.FullPath, AppId))
            toast.Show(Resources.Strings.Saves_Title, Resources.Strings.Saves_Toast_Deleted);
        else
            toast.Show(Resources.Strings.Saves_Title, Resources.Strings.Saves_Toast_Failed, error: true);
        Reload();
    }

    /// <summary>Toast the outcome. True when it succeeded.</summary>
    private bool Report(SaveBackupResult r, string okMessage)
    {
        string? error = r.Outcome switch
        {
            SaveBackupOutcome.Ok => null,
            SaveBackupOutcome.SteamNotFound => Resources.Strings.Saves_Toast_SteamNotFound,
            SaveBackupOutcome.NoSaves => Resources.Strings.Saves_Toast_NoSaves,
            SaveBackupOutcome.Refused => Resources.Strings.Saves_Toast_Refused,
            _ => Resources.Strings.Saves_Toast_Failed,
        };
        toast.Show(Resources.Strings.Saves_Title, error ?? okMessage, error: error is not null);
        return error is null;
    }

    private static async Task<bool> ConfirmAsync(string body, string primary)
    {
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = Resources.Strings.Saves_Title,
            Content = body,
            PrimaryButtonText = primary,
            CloseButtonText = Resources.Strings.Builds_Action_Cancel,
        };
        return await box.ShowDialogAsync() == Wpf.Ui.Controls.MessageBoxResult.Primary;
    }
}
