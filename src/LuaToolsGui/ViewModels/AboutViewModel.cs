using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuaToolsGui.Services;

namespace LuaToolsGui.ViewModels;

/// <summary>
/// Backs the About page: what this build is, which version it is, and where its updates come from.
///
/// <para>
/// The update information is read from the LIVE <see cref="UpdateService"/> resolution rather than
/// re-derived from settings. That matters: the resolver silently drops entries it refuses (an upstream
/// repo, a non-https URL), so a page that re-read settings.json would confidently show a source the
/// updater is actually ignoring. Showing the resolved list means what the page says is what the app does.
/// </para>
/// </summary>
public partial class AboutViewModel(MainViewModel main, UpdateService updates, ToastService toast)
    : ObservableObject
{
    /// <summary>Product name, e.g. "LuaTools Amethyst".</summary>
    public string ProductName => Resources.Strings.App_DisplayName;

    /// <summary>Version label reused from the nav footer, e.g. "v1.3.0", so the two can never disagree.</summary>
    public string Version => main.VersionLabel;

    /// <summary>Where this build's updates come from, or a placeholder when self-update is off.</summary>
    public string UpdateSource => updates.Sources.IsDisabled
        ? "—"
        : string.Join(Environment.NewLine, updates.Sources.Repos);

    /// <summary>The project's home, shown even when self-update is disabled so manual updating is possible.</summary>
    public string RepositoryUrl =>
        updates.Sources.Repos.FirstOrDefault() ?? AppConfig.GithubReleasesRepos.FirstOrDefault() ?? "";

    public bool AutoUpdateEnabled => !updates.Sources.IsDisabled;

    public string UpdateStateText => AutoUpdateEnabled
        ? Resources.Strings.About_Updates_Enabled
        : Resources.Strings.About_Updates_Disabled;

    /// <summary>Folder holding settings.json, shown so the user can find the file to edit.</summary>
    public string SettingsFolder => SettingsService.DefaultDirectory;

    // ── Changelog ───────────────────────────────────────────────────

    /// <summary>Recent releases, newest first. Compiled in — see <see cref="Resources.Changelog"/> for
    /// why this is not read from docs/CHANGELOG.md or fetched.</summary>
    public IReadOnlyList<Resources.ChangelogEntry> Changelog => Resources.Changelog.Entries;

    /// <summary>Collapsed by default: the page's job is to say what this build is, and a version history
    /// unrolled by default would push that below the fold.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChangelogToggleText))]
    private bool _isChangelogOpen;

    public string ChangelogToggleText => IsChangelogOpen
        ? Resources.Strings.About_Changelog_Hide
        : Resources.Strings.About_Changelog_Show;

    [RelayCommand]
    private void ToggleChangelog() => IsChangelogOpen = !IsChangelogOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isChecking;

    public bool IsIdle => !IsChecking;

    /// <summary>Result of the last manual check. Null hides the line.</summary>
    [ObservableProperty] private string? _checkResult;

    /// <summary>
    /// Manual "check for updates". Uses the same <see cref="UpdateService"/> the automatic flow uses, so
    /// this button cannot reach a source the background check would not.
    /// </summary>
    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (IsChecking) return;

        if (!AutoUpdateEnabled)
        {
            CheckResult = Resources.Strings.About_Check_Disabled;
            return;
        }

        IsChecking = true;
        CheckResult = Resources.Strings.About_Check_Checking;
        try
        {
            await updates.CheckAndStageAsync();
            CheckResult = updates.HasStagedUpdate
                ? Resources.Strings.About_Check_Found
                : Resources.Strings.About_Check_UpToDate;
        }
        catch (Exception ex)
        {
            // Offline, repo gone, rate-limited — all the same to the user: it didn't work, and why.
            AppLog.Log($"about: manual update check failed — {ex.Message}");
            CheckResult = Resources.Strings.About_Check_Failed;
        }
        finally
        {
            IsChecking = false;
        }
    }

    [RelayCommand]
    private void OpenRepository() => OpenExternal(RepositoryUrl);

    [RelayCommand]
    private void OpenSettingsFolder() => OpenExternal(SettingsFolder);

    /// <summary>
    /// Hand a URL or folder to the shell. Only ever called with values this app itself produced (the
    /// resolved repo URL, which is validated https+github by <see cref="AppUpdateSources"/>, or the app's
    /// own settings directory) — never with anything a downloaded file could influence.
    /// </summary>
    private void OpenExternal(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return;
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            toast.Show(ProductName, ex.Message, error: true);
        }
    }
}
