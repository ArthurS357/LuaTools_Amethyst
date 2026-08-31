using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuaToolsGui.Services;
using LuaToolsGui.Services.Downloads;

namespace LuaToolsGui.ViewModels;

/// <summary>
/// The Downloads page. A thin projection over <see cref="DownloadQueue"/>: it holds no download state of
/// its own, so the queue stays the single source of truth for every entry point (Add, Depots, the store
/// plugin and the HTTP bridge).
/// </summary>
public partial class DownloadsViewModel : ObservableObject
{
    private readonly DownloadQueue _queue;
    private readonly ToastService _toast;

    public DownloadsViewModel(DownloadQueue queue, ToastService toast)
    {
        _queue = queue;
        _toast = toast;

        _queue.Items.CollectionChanged += (_, _) => RaiseCounts();
        _queue.History.CollectionChanged += (_, _) => RaiseCounts();
        _queue.StateChanged += RaiseCounts;
    }

    /// <summary>Bound directly by the view: <c>Queue.Items</c> and <c>Queue.History</c>.</summary>
    public DownloadQueue Queue => _queue;

    /// <summary>Set by App: jump to the page that owns an item's pending confirmation.</summary>
    public Action<DownloadItem>? RevealItem { get; set; }

    public bool HasItems => _queue.Items.Count > 0;
    public bool HasHistory => _queue.History.Count > 0;
    public bool IsEmpty => !HasItems && !HasHistory;

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(HasHistory));
        OnPropertyChanged(nameof(IsEmpty));
    }

    // ── Queue row actions ────────────────────────────────────────────

    /// <summary>
    /// Cancel an item. A depot download that has already written to disk asks what to do with the files
    /// first — cancelling leaves them behind otherwise, and they are not small.
    /// </summary>
    /// <remarks>
    /// Only depot downloads prompt: they are the only kind that writes a folder rather than a staged file
    /// the queue already cleans up. And only when the downloader actually used that folder, which
    /// <see cref="DepotDownloaderService.HasDownloadedContent"/> establishes from its own marker — the
    /// output directory is user-chosen and may hold unrelated files.
    /// </remarks>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task CancelAsync(DownloadItem item)
    {
        string? outDir = item.Job.OutputPath;
        if (item.Job.Kind is not DownloadKind.Depot
            || !DepotDownloaderService.HasDownloadedContent(outDir))
        {
            _queue.Cancel(item);
            return;
        }

        // Yes = stop and delete, No = stop and keep, Cancel = keep downloading. Escape lands on Cancel, so
        // the accidental keypress is the harmless one.
        var choice = MessageBox.Show(
            string.Format(CultureInfo.CurrentCulture, Resources.Strings.Builds_Depot_CleanUp_Body,
                item.CreatedFilesCount, outDir),
            Resources.Strings.Builds_Depot_CleanUp_Title,
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);

        if (choice == MessageBoxResult.Cancel) return; // leave the download running

        _queue.Cancel(item);
        if (choice != MessageBoxResult.Yes) return;

        // The kill is asynchronous and the downloader holds handles on everything it pre-allocated, so
        // deleting before the item settles would just fail on a locked file.
        await item.Completion;
        if (!DepotDownloaderService.TryDeleteCreatedFiles(outDir, item.CreatedFilesSnapshot()))
            _toast.Show(Resources.Strings.Builds_Depot_CleanUp_Title,
                Resources.Strings.Builds_Depot_CleanUp_Failed, error: true);
    }

    [RelayCommand]
    private void Retry(DownloadItem item) => _queue.Retry(item);

    /// <summary>Depot downloads only — see <see cref="DownloadItem.CanPause"/>.</summary>
    [RelayCommand]
    private void Pause(DownloadItem item) => _queue.Pause(item);

    [RelayCommand]
    private void Resume(DownloadItem item) => _queue.Resume(item);

    [RelayCommand]
    private void Remove(DownloadItem item) => _queue.Remove(item);

    [RelayCommand]
    private void MoveUp(DownloadItem item) => _queue.Move(item, -1);

    [RelayCommand]
    private void MoveDown(DownloadItem item) => _queue.Move(item, +1);

    /// <summary>Jump to the page that can resolve this item (e.g. the pending overwrite confirmation).</summary>
    [RelayCommand]
    private void Review(DownloadItem item)
    {
        item.Job.OnReveal?.Invoke();
        RevealItem?.Invoke(item);
    }

    // ── Context-menu actions ─────────────────────────────────────────
    // Two commands per action because RelayCommand is typed and the queue and the history list bind
    // different row types. Both funnel into the same pair of helpers so the behaviour cannot diverge.

    [RelayCommand]
    private void CopyAppId(DownloadItem item) => CopyId(item.AppId);

    [RelayCommand]
    private void CopyHistoryAppId(DownloadHistoryEntry entry) => CopyId(entry.AppId);

    [RelayCommand]
    private void ShowInFolder(DownloadItem item) => Show(item.RevealPath);

    [RelayCommand]
    private void ShowHistoryInFolder(DownloadHistoryEntry entry) => Show(entry.Record.RevealPath);

    private void CopyId(long appId)
    {
        try { Clipboard.SetText(appId.ToString(CultureInfo.InvariantCulture)); }
        catch (ExternalException)
        {
            // Another process holds the clipboard open. Nothing was copied, so say so rather than letting
            // the user paste whatever was there before and think it worked.
            _toast.Show(Resources.Strings.Downloads_Action_CopyAppId,
                Resources.Strings.Downloads_Err_ClipboardBusy, error: true);
        }
    }

    /// <summary>
    /// Open the install location.
    /// </summary>
    /// <remarks>
    /// Existence is re-checked rather than trusted: the path was recorded when the job finished, it is read
    /// back out of cache.json on a later run, and the user can have deleted or moved it since. Only a
    /// FILE is ever revealed and only a DIRECTORY is ever opened — the recorded string is never handed to
    /// the shell as-is, so a cache.json that had been tampered with cannot turn this into "run that".
    /// </remarks>
    private void Show(string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                if (File.Exists(path)) { SteamService.RevealInExplorer(path); return; }
                if (Directory.Exists(path)) { SteamService.OpenUrl(path); return; }
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // The shell refused to start. Fall through to the same notice a missing path gets.
        }

        _toast.Show(Resources.Strings.Downloads_Action_ShowInFolder,
            string.Format(CultureInfo.CurrentCulture, Resources.Strings.Downloads_Err_PathMissing,
                path ?? ""), error: true);
    }

    // ── History ──────────────────────────────────────────────────────

    [RelayCommand]
    private void ClearHistory()
    {
        // Deliberately NOT async: MessageBox.Show already blocks and returns a result, and an async command
        // would become an AsyncRelayCommand, which disables itself while running.
        var choice = MessageBox.Show(
            string.Format(CultureInfo.CurrentCulture,
                Resources.Strings.Downloads_ClearHistory_Confirm, _queue.History.Count),
            Resources.Strings.Downloads_Action_ClearHistory,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No); // Enter must not wipe the list

        if (choice == MessageBoxResult.Yes) _queue.ClearHistory();
    }

    /// <summary>
    /// Remove one history row. No confirmation: it deletes a record, not a download, and prompting per row
    /// would be tedious. The bulk Clear above does confirm, because it cannot be undone.
    /// </summary>
    [RelayCommand]
    private void RemoveHistoryEntry(DownloadHistoryEntry entry) => _queue.RemoveHistory(entry);
}
