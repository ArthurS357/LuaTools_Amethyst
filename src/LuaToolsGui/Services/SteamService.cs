using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace LuaToolsGui.Services;

/// <summary>
/// Resolves the Steam install location: auto-detected from the registry, or a user override.
/// Detection confirms the folder actually contains steam.exe.
/// </summary>
public class SteamService(SettingsService settings)
{
    // Known 64-bit Steam registry locations, in priority order.
    private static readonly (RegistryHive Hive, RegistryView View, string SubKey, string Value)[] RegistryLocations =
    [
        (RegistryHive.CurrentUser, RegistryView.Registry64, @"SOFTWARE\Valve\Steam", "SteamPath"),
        (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
        (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Valve\Steam", "InstallPath"),
    ];

    /// <summary>Steam path detected from the registry (confirmed via steam.exe), or null.</summary>
    public string? AutoDetectedPath => DetectFromRegistry();

    /// <summary>The effective path: user override if set, otherwise the auto-detected one.</summary>
    public string? EffectivePath
    {
        get
        {
            string? overridePath = settings.SteamPathOverride;
            return !string.IsNullOrWhiteSpace(overridePath) ? Normalize(overridePath) : AutoDetectedPath;
        }
    }

    public bool IsOverridden => !string.IsNullOrWhiteSpace(settings.SteamPathOverride);

    /// <summary>True when the effective path exists and contains steam.exe.</summary>
    public bool IsValid => EffectivePath is not null && File.Exists(SteamExePathFor(EffectivePath));

    public static string SteamExePathFor(string steamPath) => Path.Combine(steamPath, "steam.exe");

    /// <summary>Full path to config\stplug-in, or null if Steam isn't located.</summary>
    public string? StPlugInDir =>
        EffectivePath is { } p ? Path.Combine(p, "config", "stplug-in") : null;

    /// <summary>Full path to config\depotcache (where .manifest files go), or null if Steam isn't located.</summary>
    public string? DepotCacheDir =>
        EffectivePath is { } p ? Path.Combine(p, "config", "depotcache") : null;

    /// <summary>Open a store/steam URL or file path with the shell (browser, Steam client, Explorer).</summary>
    public static void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    /// <summary>Open Explorer with the given file selected.</summary>
    public static void RevealInExplorer(string filePath) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"") { UseShellExecute = true });

    /// <summary>Kill any running steam.exe (and its tree) and wait for it to exit. Safe to call when
    /// Steam isn't running. Use before changing Steam's files so they aren't locked.</summary>
    /// <summary>True while a Steam client process is running — appinfo.vdf can't be edited under it.</summary>
    public static bool IsSteamRunning()
    {
        var procs = Process.GetProcessesByName("steam");
        try { return procs.Length > 0; }
        finally { foreach (var p in procs) p.Dispose(); }
    }

    public void StopSteam()
    {
        foreach (var proc in Process.GetProcessesByName("steam"))
        {
            try { proc.Kill(entireProcessTree: true); proc.WaitForExit(8000); }
            catch { /* already gone / access denied */ }
            finally { proc.Dispose(); }
        }
    }

    /// <summary>Launch Steam from the effective path. Returns false if it can't be located/launched.</summary>
    public bool StartSteam()
    {
        string? path = EffectivePath;
        if (path is null) return false;
        string exe = SteamExePathFor(path);
        if (!File.Exists(exe)) return false;

        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Kill any running steam.exe and relaunch it from the effective path. lua changes only take
    /// effect after a Steam restart. Returns false if Steam can't be located/launched.
    /// </summary>
    public bool RestartSteam()
    {
        StopSteam();
        return StartSteam();
    }

    private static string? DetectFromRegistry()
    {
        foreach (var (hive, view, subKey, value) in RegistryLocations)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var key = baseKey.OpenSubKey(subKey);
                if (key?.GetValue(value) is not string raw || string.IsNullOrWhiteSpace(raw)) continue;

                string path = Normalize(raw);
                if (File.Exists(SteamExePathFor(path))) return path;
            }
            catch
            {
                // Inaccessible key — try the next one
            }
        }
        return null;
    }

    /// <summary>Registry values vary (forward vs back slashes, casing) — canonicalize to a Windows path.</summary>
    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path.Trim().Replace('/', '\\')); }
        catch { return path.Trim().Replace('/', '\\'); }
    }
}
