using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuaToolsGui.Services;
using LuaToolsGui.Services.AppInfo;

namespace LuaToolsGui.ViewModels;

/// <summary>One game in the "All launch overrides" list.</summary>
public sealed record LaunchOverrideRow(long AppId, string Name, string Overrides);

/// <summary>
/// Every game whose launch options this app has edited, read from <see cref="LaunchModStore"/>. It shows
/// what the app asked for, not what's live in appinfo: reading that means indexing a ~373 MB file, and the
/// startup drift check already reports when Steam has overwritten an edit.
/// </summary>
public sealed partial class LaunchOverridesViewModel : ObservableObject
{
    private readonly LaunchOptionsService _launch;
    private readonly ToastService _toast;
    private readonly Func<long, string?> _nameOf;
    private readonly Func<long, string?> _installDir;
    private readonly Func<string, string, bool> _confirm;
    private readonly Action<string> _openFolder;

    public ObservableCollection<LaunchOverrideRow> Rows { get; } = [];

    [ObservableProperty] private bool _hasRows;

    public LaunchOverridesViewModel(LaunchOptionsService launch, ToastService toast,
                                    SteamAppListCache appList, SteamLibraryService library)
        : this(launch, toast, appList.GetName, library.GetInstallDir, ConfirmWithMessageBox, SteamService.OpenFolder)
    { }

    /// <summary>Test seam: name and folder lookups, the confirmation box and Explorer.</summary>
    internal LaunchOverridesViewModel(LaunchOptionsService launch, ToastService toast,
                                      Func<long, string?> nameOf, Func<long, string?> installDir,
                                      Func<string, string, bool> confirm, Action<string> openFolder)
    {
        _launch = launch;
        _toast = toast;
        _nameOf = nameOf;
        _installDir = installDir;
        _confirm = confirm;
        _openFolder = openFolder;
        Reload();
    }

    private void Reload()
    {
        Rows.Clear();
        var rows = _launch.Store.All
            .Select(m => new LaunchOverrideRow(
                m.AppId,
                _nameOf(m.AppId) ?? string.Format(Resources.Strings.Common_AppFallback, m.AppId),
                string.Join("  |  ", m.Desired.Select(o => o.Summary))))
            .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase);
        foreach (var row in rows) Rows.Add(row);
        HasRows = Rows.Count > 0;
    }

    /// <summary>Off the UI thread: this closes Steam, copies and rewrites a ~373 MB file.</summary>
    [RelayCommand]
    private async Task RestoreAllAsync()
    {
        string body = string.Format(Resources.Strings.Launch_RestoreAll_Body, Rows.Count);
        if (!_confirm(body, Resources.Strings.Launch_RestoreAll_Title)) return;

        var result = await Task.Run(() => _launch.RestoreAll());
        Reload();

        string title = Resources.Strings.Launch_Title;
        if (result.Error is { } error)
            _toast.Show(title, string.Format(Resources.Strings.Launch_RestoreAll_Failed, error), error: true);
        else if (result.Failed.Count > 0)
            _toast.Show(title, string.Format(Resources.Strings.Launch_RestoreAll_Partial, result.Restored.Count,
                string.Join(", ", result.Failed.Select(f => f.AppId))), error: true);
        else
            _toast.Show(title, string.Format(Resources.Strings.Launch_RestoreAll_Done, result.Restored.Count));
    }

    /// <summary>Open the game's folder (steamapps\common\&lt;installdir&gt;) in Explorer.</summary>
    [RelayCommand]
    private void ShowInFolder(LaunchOverrideRow row)
    {
        if (_installDir(row.AppId) is { } dir) _openFolder(dir);
        else _toast.Show(Resources.Strings.Launch_Title, Resources.Strings.Manage_Steamless_NoInstall, error: true);
    }

    private static bool ConfirmWithMessageBox(string body, string title) =>
        MessageBox.Show(body, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;
}
