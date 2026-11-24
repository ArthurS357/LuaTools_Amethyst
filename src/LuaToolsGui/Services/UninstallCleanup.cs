using System.IO;
using Microsoft.Win32;

namespace LuaToolsGui.Services;

/// <summary>What a cleanup pass actually did. Returned rather than logged so it can be asserted in tests.</summary>
public sealed record CleanupReport
{
    public bool CdpMarkerRemoved { get; init; }
    public int LoaderFilesRemoved { get; init; }
    public bool ProtocolUnregistered { get; init; }
    public bool UserDataRemoved { get; init; }
    public List<string> Failures { get; init; } = [];
}

/// <summary>
/// Removes the traces LuaTools leaves OUTSIDE its own install folder, when the app itself is uninstalled.
///
/// <para>
/// WHY THIS EXISTS: Velopack's uninstaller deletes the app directory and nothing else, and this app
/// deliberately writes into places Velopack knows nothing about — the Steam root and HKCU. Before this
/// hook existed, uninstalling LuaTools left behind:
/// </para>
/// <list type="bullet">
///   <item><b>The <c>.cef-enable-remote-debugging</c> junction next to steam.exe.</b> This is the serious
///   one. Its presence makes Steam open an UNAUTHENTICATED debug port (127.0.0.1:8080) on every launch,
///   giving any local process control of Steam's browser under the user's session. It is created only
///   after an explicit consent prompt (PluginInstallerService.MayEnableCdp), and that consent plainly does
///   not outlive the app that asked for it — yet without this cleanup it survived uninstall forever, with
///   nothing left on the machine to explain or undo it.</item>
///   <item><b>The loader DLL in the Steam root</b> (<c>winmm.dll</c> + <c>winmm_real.dll</c>, plus older
///   slots). Left behind, steam.exe keeps side-loading a proxy DLL that tries to start an application
///   that is no longer installed.</item>
///   <item><b>The <c>luatools://</c> protocol handler</b> in HKCU, pointing at a deleted executable.</item>
///   <item><b>%AppData%\LuaToolsAmethyst</b>, which holds the DPAPI-protected auth token, the Hubcap API key
///   and the caches. Leaving credentials behind after an uninstall is its own privacy problem.</item>
/// </list>
///
/// <para>
/// EVERYTHING HERE IS BEST-EFFORT. An uninstall that fails or hangs is worse than one that leaves a file
/// behind, so every step is individually guarded, nothing throws out of <see cref="Run"/>, and failures
/// are collected into the report instead of propagating. Steam is NOT stopped and no consent is asked —
/// the user already decided by uninstalling.
/// </para>
///
/// <para>
/// The methods take explicit paths rather than resolving them internally, so the whole thing is
/// exercisable against temp directories in tests (see UninstallCleanupTests) without touching a real
/// Steam install or the registry.
/// </para>
/// </summary>
public static class UninstallCleanup
{
    /// <summary>Marker whose mere presence makes Steam self-enable CDP on port 8080.</summary>
    public const string CdpMarkerName = ".cef-enable-remote-debugging";

    /// <summary>
    /// Every loader file this app may have written into the Steam root, across all versions: the current
    /// winmm slot and the abandoned bcrypt/psapi/dbghelp experiments, each with its <c>_real</c> forwarder.
    /// Kept as one list here (rather than shared with PluginInstallerService's install-time slots) because
    /// cleanup must cover slots this build no longer installs but an older one did.
    /// </summary>
    public static readonly string[] LoaderFileNames =
    [
        "winmm.dll", "winmm_real.dll",
        "bcrypt.dll", "bcrypt_real.dll",
        "psapi.dll",
        "dbghelp.dll", "dbghelp_real.dll",
    ];

    private const string ProtocolName = "luatools";

    /// <summary>
    /// Run the full cleanup. Safe to call when nothing is installed, when <paramref name="steamDir"/> is
    /// null or missing, and more than once.
    /// </summary>
    /// <param name="steamDir">Steam root, or null when it cannot be detected (then Steam-side steps are skipped).</param>
    /// <param name="appDataDir">%AppData%\LuaToolsAmethyst, or null to keep user data.</param>
    /// <param name="unregisterProtocol">False in tests, so a test never touches the real registry.</param>
    public static CleanupReport Run(string? steamDir, string? appDataDir, bool unregisterProtocol = true)
    {
        var failures = new List<string>();
        bool cdp = false;
        int loaders = 0;
        bool protocolGone = false;
        bool dataGone = false;

        if (!string.IsNullOrWhiteSpace(steamDir))
        {
            if (!IsSafeSteamDir(steamDir, appDataDir))
            {
                failures.Add("steam: unsafe or unexpected Steam path refused.");
            }
            else if (Directory.Exists(steamDir))
            {
                cdp = TryRemoveCdpMarker(steamDir, failures);
                loaders = RemoveLoaderFiles(steamDir, failures);
            }
        }

        if (unregisterProtocol) protocolGone = TryUnregisterProtocol(failures);
        if (!string.IsNullOrWhiteSpace(appDataDir)) dataGone = TryRemoveUserData(appDataDir, failures);

        return new CleanupReport
        {
            CdpMarkerRemoved = cdp,
            LoaderFilesRemoved = loaders,
            ProtocolUnregistered = protocolGone,
            UserDataRemoved = dataGone,
            Failures = failures,
        };
    }

    /// <summary>
    /// Validates that <paramref name="steamDir"/> is a safe, fully-qualified directory path and not a drive
    /// root, OS/profile root, or user-data folder.
    /// </summary>
    public static bool IsSafeSteamDir(string? steamDir, string? appDataDir = null)
    {
        if (!TryNormalizeSafeDirectoryPath(steamDir, out string full)) return false;

        if (IsProtectedSystemOrProfileRoot(full)) return false;

        if (!string.IsNullOrWhiteSpace(appDataDir)
            && TryNormalizeSafeDirectoryPath(appDataDir, out string appFull)
            && PathsEqual(full, appFull))
            return false;

        foreach (string sideBySide in DefaultProtectedSideBySideDirs())
            if (PathsEqual(full, sideBySide))
                return false;

        if (TryNormalizeSafeDirectoryPath(AppDataMigrator.CurrentDirectory, out string currentAppData)
            && PathsEqual(full, currentAppData))
            return false;

        return true;
    }

    /// <summary>
    /// Validates that <paramref name="appDataDir"/> is safe to recursively delete as user data: must be a
    /// fully-qualified non-root path, not an OS/profile root, not a reparse point, not a Steam root
    /// (containing <c>steam.exe</c>), and not a folder belonging to a side-by-side installation (such as
    /// legacy <c>%AppData%\LuaToolsGui</c> or <c>%LocalAppData%\LuaTools</c>).
    /// </summary>
    public static bool IsSafeUserDataDir(string? appDataDir, IEnumerable<string>? protectedSideBySideDirs = null) =>
        IsSafeUserDataDir(appDataDir, protectedSideBySideDirs, allowLink: false);

    /// <param name="allowLink">Accept a reparse point at <paramref name="appDataDir"/> itself: for a caller that
    /// only ever severs the link, never recurses through it.</param>
    private static bool IsSafeUserDataDir(string? appDataDir, IEnumerable<string>? protectedSideBySideDirs, bool allowLink)
    {
        if (!TryNormalizeSafeDirectoryPath(appDataDir, out string full)) return false;

        if (IsProtectedSystemOrProfileRoot(full)) return false;

        var protectedDirs = protectedSideBySideDirs ?? DefaultProtectedSideBySideDirs();
        foreach (string candidate in protectedDirs)
        {
            if (TryNormalizeSafeDirectoryPath(candidate, out string candFull) && PathsEqual(full, candFull))
                return false;
        }

        try
        {
            var dirInfo = new DirectoryInfo(full);
            if (dirInfo.Exists)
            {
                if (!allowLink && (dirInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                    return false;
                if (File.Exists(Path.Combine(full, "steam.exe")))
                    return false;
            }
        }
        catch
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// True when the <c>luatools://</c> protocol command registered in HKCU belongs to the current
    /// installation (<paramref name="currentExePath"/>) rather than another side-by-side installation
    /// (such as upstream <c>%LocalAppData%\LuaTools\current\LuaTools.exe</c>).
    /// </summary>
    public static bool IsProtocolOwnedByInstall(string? registeredCommand, string? currentExePath)
    {
        if (string.IsNullOrWhiteSpace(registeredCommand)) return true;
        if (string.IsNullOrWhiteSpace(currentExePath)) return false;

        if (!TryExtractCommandExecutable(registeredCommand, out string registeredExe))
            return false;

        if (!TryNormalizeSafeFilePath(registeredExe, out string regFull)
            || !TryNormalizeSafeFilePath(currentExePath, out string curFull))
            return false;

        if (PathsEqual(regFull, curFull)) return true;

        string? regRoot = GetInstallRootDirectory(regFull);
        string? curRoot = GetInstallRootDirectory(curFull);
        if (regRoot is null || curRoot is null) return false;

        return PathsEqual(regRoot, curRoot);
    }

    private static bool TryExtractCommandExecutable(string command, out string exePath)
    {
        exePath = string.Empty;
        string trimmed = command.Trim();
        if (trimmed.Length == 0) return false;

        if (trimmed[0] == '"')
        {
            int close = trimmed.IndexOf('"', 1);
            if (close <= 1) return false;
            exePath = trimmed[1..close].Trim();
            return exePath.Length > 0;
        }

        // Unquoted (hand-written or by an older tool): a path with spaces must not be cut at the first space,
        // or this install's own registration reads as a third party's and survives the uninstall.
        int exe = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0 && (exe + 4 == trimmed.Length || char.IsWhiteSpace(trimmed[exe + 4])))
        {
            exePath = trimmed[..(exe + 4)];
            return true;
        }

        int space = trimmed.IndexOfAny([' ', '\t']);
        exePath = (space > 0 ? trimmed[..space] : trimmed).Trim();
        return exePath.Length > 0;
    }

    private static string? GetInstallRootDirectory(string fullExePath)
    {
        string? dir = Path.GetDirectoryName(fullExePath)?.TrimEnd('\\', '/');
        if (string.IsNullOrWhiteSpace(dir)) return null;

        string leaf = Path.GetFileName(dir);
        if (leaf.Equals("current", StringComparison.OrdinalIgnoreCase)
            || leaf.StartsWith("app-", StringComparison.OrdinalIgnoreCase))
        {
            string? parent = Path.GetDirectoryName(dir)?.TrimEnd('\\', '/');
            if (!string.IsNullOrWhiteSpace(parent) && !IsProtectedSystemOrProfileRoot(parent))
                return parent;
        }

        return IsProtectedSystemOrProfileRoot(dir) ? null : dir;
    }

    private static IEnumerable<string> DefaultProtectedSideBySideDirs()
    {
        if (!string.IsNullOrWhiteSpace(AppDataMigrator.LegacyDirectory))
            yield return AppDataMigrator.LegacyDirectory;

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData))
        {
            yield return Path.Combine(appData, "LuaToolsGui");
            yield return Path.Combine(appData, "LuaTools");
        }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return Path.Combine(localAppData, "LuaTools");
            yield return Path.Combine(localAppData, "LuaToolsGui");
        }
    }

    private static bool TryNormalizeSafeDirectoryPath(string? rawPath, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(rawPath)) return false;
        if (rawPath.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;
        if (!Path.IsPathFullyQualified(rawPath)) return false;
        if (HasTraversalSegment(rawPath)) return false;

        try
        {
            string full = Path.GetFullPath(rawPath).TrimEnd('\\', '/');
            string? root = Path.GetPathRoot(full)?.TrimEnd('\\', '/');
            if (string.IsNullOrWhiteSpace(full) || string.IsNullOrWhiteSpace(root)) return false;
            if (PathsEqual(full, root)) return false; // never allow a drive/UNC root
            normalized = full;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryNormalizeSafeFilePath(string? rawPath, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(rawPath)) return false;
        if (rawPath.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;
        if (!Path.IsPathFullyQualified(rawPath)) return false;
        if (HasTraversalSegment(rawPath)) return false;

        try
        {
            string full = Path.GetFullPath(rawPath).TrimEnd('\\', '/');
            string? root = Path.GetPathRoot(full)?.TrimEnd('\\', '/');
            if (string.IsNullOrWhiteSpace(full) || string.IsNullOrWhiteSpace(root)) return false;
            if (PathsEqual(full, root)) return false;
            normalized = full;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasTraversalSegment(string rawPath)
    {
        foreach (string part in rawPath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
            if (part == "..")
                return true;
        return false;
    }

    private static bool IsProtectedSystemOrProfileRoot(string normalizedFullPath)
    {
        string? root = Path.GetPathRoot(normalizedFullPath)?.TrimEnd('\\', '/');
        if (!string.IsNullOrWhiteSpace(root) && PathsEqual(normalizedFullPath, root))
            return true;

        Environment.SpecialFolder[] specialRoots =
        [
            Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolder.CommonApplicationData,
            Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolder.Windows,
            Environment.SpecialFolder.System,
            Environment.SpecialFolder.SystemX86,
            Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.Desktop,
            Environment.SpecialFolder.MyDocuments,
        ];

        foreach (var folder in specialRoots)
        {
            string candidate = Environment.GetFolderPath(folder);
            if (!string.IsNullOrWhiteSpace(candidate) && PathsEqual(normalizedFullPath, candidate.TrimEnd('\\', '/')))
                return true;
        }

        string tempRoot = Path.GetTempPath().TrimEnd('\\', '/');
        if (!string.IsNullOrWhiteSpace(tempRoot) && PathsEqual(normalizedFullPath, tempRoot))
            return true;

        return false;
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Delete the CDP marker. It is normally an NTFS junction pointing at a target chosen never to exist,
    /// so removal goes through <see cref="DirectoryJunction.Remove"/>, which severs the link without any
    /// chance of recursing into a target. A plain file or directory left by an older build (or a
    /// partially-failed removal) is handled too, so this cannot be defeated by leftover cruft.
    /// </summary>
    public static bool TryRemoveCdpMarker(string steamDir, List<string>? failures = null)
    {
        if (!IsSafeSteamDir(steamDir))
        {
            failures?.Add($"{CdpMarkerName}: unsafe or unexpected Steam path refused.");
            return false;
        }

        string path = Path.Combine(steamDir, CdpMarkerName);
        try
        {
            // A junction with a missing target still reports Directory.Exists == true (verified on .NET 8,
            // re-verified on .NET 10 for the TFM move), so this check does catch the real-world case.
            if (Directory.Exists(path))
            {
                DirectoryJunction.Remove(path);
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true); // non-junction leftover
                return !Directory.Exists(path);
            }
            if (File.Exists(path)) // stale plain-file marker from before the junction fix
            {
                File.Delete(path);
                return true;
            }
            return false; // nothing there — not a failure
        }
        catch (Exception ex)
        {
            failures?.Add($"{CdpMarkerName}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Delete every known loader DLL from the Steam root. Returns how many were removed.</summary>
    public static int RemoveLoaderFiles(string steamDir, List<string>? failures = null)
    {
        if (!IsSafeSteamDir(steamDir))
        {
            failures?.Add("loaders: unsafe or unexpected Steam path refused.");
            return 0;
        }

        int removed = 0;
        foreach (string name in LoaderFileNames)
        {
            string path = Path.Combine(steamDir, name);
            try
            {
                if (!File.Exists(path)) continue;
                File.Delete(path);
                removed++;
            }
            catch (Exception ex)
            {
                // Most likely Steam is running and holding the DLL. Report, don't abort: the remaining
                // files should still go, and a locked one is exactly what the README checklist covers.
                failures?.Add($"{name}: {ex.Message}");
            }
        }
        return removed;
    }

    /// <summary>Remove the HKCU <c>luatools://</c> registration, which would otherwise point at a deleted exe.</summary>
    public static bool TryUnregisterProtocol(
        List<string>? failures = null,
        string? currentExePath = null,
        string classesSubKeyPath = @"Software\Classes")
    {
        try
        {
            using var classes = Registry.CurrentUser.OpenSubKey(classesSubKeyPath, writable: true);
            using var protocolKey = classes?.OpenSubKey(ProtocolName);
            if (protocolKey is null) return false;

            using var cmdKey = protocolKey.OpenSubKey(@"shell\open\command");
            string? registeredCommand = cmdKey?.GetValue(null) as string;
            string? effectiveExePath = currentExePath ?? Environment.ProcessPath;

            if (!IsProtocolOwnedByInstall(registeredCommand, effectiveExePath))
                return false;

            classes!.DeleteSubKeyTree(ProtocolName, throwOnMissingSubKey: false);
            return true;
        }
        catch (Exception ex)
        {
            failures?.Add($"protocol: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Delete %AppData%\LuaToolsAmethyst. This holds the DPAPI-protected Supabase token and the Hubcap API key,
    /// so removing it on uninstall is a deliberate privacy choice, not just tidiness.
    /// </summary>
    public static bool TryRemoveUserData(
        string appDataDir,
        List<string>? failures = null,
        IEnumerable<string>? protectedSideBySideDirs = null)
    {
        try
        {
            if (!IsSafeUserDataDir(appDataDir, protectedSideBySideDirs, allowLink: true))
            {
                failures?.Add("appdata: unsafe or side-by-side path refused.");
                return false;
            }

            if (!Directory.Exists(appDataDir)) return false;

            var dir = new DirectoryInfo(appDataDir);
            if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                // The user moved the data folder and left a junction/symlink here: sever the link only. Following
                // it would delete whatever it points at, which nothing here validated. The sign-in data stays at
                // the target, so this is reported and NOT counted as user data removed.
                bool severed = DirectoryJunction.Remove(appDataDir);
                failures?.Add(severed
                    ? "appdata: was a link; only the link was removed, the folder it pointed to (with sign-in data) was left in place."
                    : "appdata: was a link and the link could not be removed.");
                return false;
            }

            // Never walks into a nested link either (a junction inside is detached, its target untouched).
            Backups.BackupService.DeleteTree(dir);
            return true;
        }
        catch (Exception ex)
        {
            failures?.Add($"appdata: {ex.Message}");
            return false;
        }
    }

    /// <summary>Steam root from the registry, without needing SteamService (which requires DI).</summary>
    public static string? DetectSteamDir()
    {
        (RegistryHive hive, RegistryView view, string subKey, string value)[] locations =
        [
            (RegistryHive.CurrentUser, RegistryView.Registry64, @"SOFTWARE\Valve\Steam", "SteamPath"),
            (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
            (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Valve\Steam", "InstallPath"),
        ];

        foreach (var (hive, view, subKey, value) in locations)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var key = baseKey.OpenSubKey(subKey);
                if (key?.GetValue(value) is not string raw || string.IsNullOrWhiteSpace(raw)) continue;

                string path = raw.Replace('/', '\\').TrimEnd('\\');
                if (File.Exists(Path.Combine(path, "steam.exe"))) return path;
            }
            catch { /* try the next location */ }
        }
        return null;
    }

}
