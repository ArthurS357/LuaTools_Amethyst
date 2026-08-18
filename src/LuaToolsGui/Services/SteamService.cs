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

    /// <summary>
    /// Remembered result of <see cref="DetectFromRegistry"/>. Detection opens up to three registry keys
    /// and stats a file, and it was being redone on EVERY read of a Steam path.
    ///
    /// <para>
    /// That is not a rare call. <c>StPlugInDir</c> and <c>EffectivePath</c> are read from 28 places, and
    /// the ones that matter are per-game: refreshing the Depots list resolves a path once per title, so a
    /// 200-game library did roughly six hundred registry opens to answer a question whose answer is the
    /// same every time. Steam does not move while the app is running.
    /// </para>
    ///
    /// <para>
    /// Not locked. Two threads racing both compute the same value and the reference assignment is atomic,
    /// so the worst case is one wasted detection — cheaper than serialising every path read behind a lock.
    /// </para>
    /// </summary>
    private string? _detected;

    /// <summary>Steam path detected from the registry (confirmed via steam.exe), or null.</summary>
    public string? AutoDetectedPath
    {
        get
        {
            // Revalidated, not blindly trusted: the cache is only reused while it still points at a real
            // steam.exe, so an install that is moved or removed mid-session is picked up on the next read
            // instead of pinning the app to a path that no longer exists. A null is never cached — that is
            // the "Steam not installed yet" case, and it must notice an install appearing.
            if (_detected is { } cached && File.Exists(SteamExePathFor(cached))) return cached;

            return _detected = DetectFromRegistry();
        }
    }

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

    /// <summary>Adapts a real <see cref="Process"/> to the seam the shutdown policy is written against.
    /// <c>Kill(entireProcessTree)</c> matters: Steam spawns helpers that keep its files open, and killing
    /// only the parent leaves them behind.</summary>
    private sealed class SteamProcess(Process process) : ISteamProcess, IDisposable
    {
        public bool HasExited => process.HasExited;
        public bool WaitForExit(TimeSpan timeout) => process.WaitForExit((int)timeout.TotalMilliseconds);
        public void Kill() => process.Kill(entireProcessTree: true);
        public void Dispose() => process.Dispose();
    }

    /// <summary>
    /// Steam's own clean-exit command.
    ///
    /// <para>
    /// NOT <c>CloseMainWindow</c>. Closing Steam's window minimises it to the tray by default, so the
    /// process survives with every file still open — the graceful path would always appear to fail and
    /// the user would be asked to force-kill a Steam that was behaving normally. <c>-shutdown</c> asks the
    /// running client to exit, which is the flush-and-quit this whole path exists to get.
    /// </para>
    /// </summary>
    private void RequestSteamShutdown()
    {
        if (EffectivePath is not { } path) return;
        string exe = SteamExePathFor(path);
        if (!File.Exists(exe)) return;

        using var _ = Process.Start(new ProcessStartInfo(exe, "-shutdown")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }

    /// <summary>
    /// Stop Steam, asking before forcing.
    ///
    /// <para>
    /// This used to go straight to <c>Kill(entireProcessTree: true)</c>. Terminating the Steam client
    /// denies it the chance to flush its config, which is what produces the "Steam did not shut down
    /// correctly" scan on the next launch — so it is now the fallback rather than the opening move. The
    /// behaviour callers depended on is unchanged by default: with <paramref name="allowKill"/> true this
    /// still guarantees Steam is down, it just gets there politely when it can.
    /// </para>
    /// </summary>
    /// <param name="allowKill">False turns this advisory: Steam is ASKED to close and the result reports
    /// <see cref="SteamStopOutcome.StillRunning"/> if it refuses, instead of forcing. That is what lets
    /// the startup flow put the choice to the user rather than terminating their session unannounced.</param>
    /// <param name="grace">How long Steam gets to close itself.</param>
    public SteamStopOutcome StopSteamGraceful(bool allowKill = true, TimeSpan? grace = null)
    {
        var processes = Process.GetProcessesByName("steam");
        var wrapped = processes.Select(p => new SteamProcess(p)).ToList();
        try
        {
            return SteamShutdown.Stop(
                wrapped, RequestSteamShutdown, allowKill, grace ?? SteamShutdown.DefaultGrace);
        }
        finally
        {
            foreach (var p in wrapped) p.Dispose();
        }
    }

    /// <summary>Stop Steam, forcing if it will not close. Kept as the shape every existing caller uses —
    /// installers and mode switches are about to rewrite files Steam holds open, so for them "Steam is
    /// down" is not negotiable.</summary>
    public void StopSteam() => StopSteamGraceful(allowKill: true);

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
