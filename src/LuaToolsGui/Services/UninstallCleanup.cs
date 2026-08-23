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
///   <item><b>%AppData%\LuaToolsGui</b>, which holds the DPAPI-protected auth token, the Hubcap API key
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
    /// <param name="appDataDir">%AppData%\LuaToolsGui, or null to keep user data.</param>
    /// <param name="unregisterProtocol">False in tests, so a test never touches the real registry.</param>
    public static CleanupReport Run(string? steamDir, string? appDataDir, bool unregisterProtocol = true)
    {
        var failures = new List<string>();
        bool cdp = false;
        int loaders = 0;
        bool protocolGone = false;
        bool dataGone = false;

        if (!string.IsNullOrWhiteSpace(steamDir) && Directory.Exists(steamDir))
        {
            cdp = TryRemoveCdpMarker(steamDir, failures);
            loaders = RemoveLoaderFiles(steamDir, failures);
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
    /// Delete the CDP marker. It is normally an NTFS junction pointing at a target chosen never to exist,
    /// so removal goes through <see cref="DirectoryJunction.Remove"/>, which severs the link without any
    /// chance of recursing into a target. A plain file or directory left by an older build (or a
    /// partially-failed removal) is handled too, so this cannot be defeated by leftover cruft.
    /// </summary>
    public static bool TryRemoveCdpMarker(string steamDir, List<string>? failures = null)
    {
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
    public static bool TryUnregisterProtocol(List<string>? failures = null)
    {
        try
        {
            using var classes = Registry.CurrentUser.OpenSubKey(@"Software\Classes", writable: true);
            if (classes?.OpenSubKey(ProtocolName) is null) return false;
            classes.DeleteSubKeyTree(ProtocolName, throwOnMissingSubKey: false);
            return true;
        }
        catch (Exception ex)
        {
            failures?.Add($"protocol: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Delete %AppData%\LuaToolsGui. This holds the DPAPI-protected Supabase token and the Hubcap API key,
    /// so removing it on uninstall is a deliberate privacy choice, not just tidiness.
    /// </summary>
    public static bool TryRemoveUserData(string appDataDir, List<string>? failures = null)
    {
        try
        {
            if (!Directory.Exists(appDataDir)) return false;
            Directory.Delete(appDataDir, recursive: true);
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
