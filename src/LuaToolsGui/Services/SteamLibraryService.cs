using System.IO;
using System.Text.RegularExpressions;

namespace LuaToolsGui.Services;

/// <summary>Resolved location and manifest metadata for a Steam app installed in a library.</summary>
public sealed record SteamAppInstallLocation(
    string LibraryRoot,
    string SteamAppsDir,
    string CommonDir,
    string AcfPath,
    string RawInstallDir,
    long? SizeOnDisk,
    IReadOnlyList<long> MountedDepotIds);

/// <summary>
/// Resolves where a Steam game is installed on disk by walking Valve's KeyValues files:
/// registry Steam root → steamapps\libraryfolders.vdf (every library, possibly across drives) →
/// per-library steamapps\appmanifest_&lt;appid&gt;.acf (the game's installdir) → common\&lt;installdir&gt;.
/// Best-effort: returns null if Steam/the game can't be located. Used to apply Denuvo "fix" zips.
/// </summary>
public partial class SteamLibraryService(SteamService steam)
{
    // "path"        "D:\\SteamLibrary"     → the library root (libraryfolders.vdf)
    // "installdir"  "Elden Ring"           → folder under steamapps\common (appmanifest_*.acf)
    // Values are quoted; backslashes are escaped (\\). One key per line.
    [GeneratedRegex(@"""path""\s*""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex PathRegex();

    [GeneratedRegex(@"""installdir""\s*""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex InstallDirRegex();

    /// <summary>
    /// Full path to the game's install folder (…\steamapps\common\&lt;installdir&gt;) if it exists on
    /// disk, else null (game not installed / Steam not found / unreadable).
    /// </summary>
    public string? GetInstallDir(long appId)
    {
        try
        {
            string? steamRoot = steam.EffectivePath;
            if (steamRoot is null) return null;

            foreach (string library in GetLibraryRoots(steamRoot))
            {
                string acf = Path.Combine(library, "steamapps", $"appmanifest_{appId}.acf");
                if (!File.Exists(acf)) continue;

                var m = InstallDirRegex().Match(File.ReadAllText(acf));
                if (!m.Success) continue;

                // The manifest is a file any program can write: a rooted or ".." installdir would point
                // Fix/Steamless/uninstall at a folder outside steamapps\common.
                string installDir = Unescape(m.Groups[1].Value);
                if (FixAnalyzer.IsAbsoluteOrRooted(installDir)
                    || !FixAnalyzer.IsContained(Path.Combine(library, "steamapps", "common"), installDir, out string full))
                    continue;
                if (Directory.Exists(full)) return full;
            }
        }
        catch { /* unreadable VDF/ACF or odd path — treat as not found */ }
        return null;
    }

    /// <summary>
    /// Locate an app's <c>appmanifest_&lt;appid&gt;.acf</c> across all Steam libraries without pre-filtering
    /// <see cref="SteamAppInstallLocation.RawInstallDir"/>, so callers can enforce fail-closed validation.
    /// </summary>
    public SteamAppInstallLocation? FindAppInstall(long appId)
    {
        if (appId <= 0) return null;
        try
        {
            string? steamRoot = steam.EffectivePath;
            if (steamRoot is null) return null;

            foreach (string library in GetLibraryRoots(steamRoot))
            {
                string steamApps = Path.Combine(library, "steamapps");
                string acf = Path.Combine(steamApps, $"appmanifest_{appId}.acf");
                if (!File.Exists(acf)) continue;

                string common = Path.Combine(steamApps, "common");
                if (GameCleanupService.IsLink(acf))
                    return new SteamAppInstallLocation(library, steamApps, common, acf, string.Empty, null, []);

                string? text = ReadSharedText(acf);
                if (text is null)
                    return new SteamAppInstallLocation(library, steamApps, common, acf, string.Empty, null, []);

                var m = InstallDirRegex().Match(text);
                string rawInstallDir = m.Success ? Unescape(m.Groups[1].Value) : string.Empty;
                long? sizeOnDisk = GameCleanupPolicy.ParseAcfSizeOnDisk(text);
                IReadOnlyList<long> depots = GameCleanupPolicy.ParseAcfDepotIds(text);
                return new SteamAppInstallLocation(library, steamApps, common, acf, rawInstallDir, sizeOnDisk, depots);
            }
        }
        catch { /* unreadable VDF/ACF or odd path — treat as not found */ }
        return null;
    }

    /// <summary>
    /// All depot ids declared in <c>appmanifest_*.acf</c> files belonging to games other than
    /// <paramref name="excludeAppId"/>, used to protect shared depots during cleanup.
    /// </summary>
    /// <returns>Null (fail-closed) when libraryfolders.vdf cannot be read, a library it lists is not on disk
    /// (a disconnected drive hides that library's ACFs), a library could not be listed, or any other ACF could
    /// not be read: its depots cannot be proven unshared, so the caller must not delete manifests.</returns>
    public HashSet<long>? GetOtherAppDepotIds(long excludeAppId)
    {
        if (steam.EffectivePath is not { } steamRoot) return null;

        // Libraries come from THIS read of the vdf, not a second one: GetLibraryRoots swallows a failed read
        // (Steam rewriting the file) and would silently scan only the main library.
        var roots = new List<string> { steamRoot };
        string vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
        {
            if (ReadSharedText(vdf) is not { } vdfText) return null;
            foreach (Match m in PathRegex().Matches(vdfText))
            {
                string library = Unescape(m.Groups[1].Value);
                if (!Directory.Exists(Path.Combine(library, "steamapps"))) return null;
                roots.Add(library);
            }
        }

        var manifests = new List<(long AppId, string Path)>();
        try
        {
            foreach (string library in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string steamapps = Path.Combine(library, "steamapps");
                if (!Directory.Exists(steamapps)) continue; // only the main root can reach here
                foreach (string acf in Directory.EnumerateFiles(steamapps, "appmanifest_*.acf"))
                    if (long.TryParse(Path.GetFileNameWithoutExtension(acf).AsSpan("appmanifest_".Length), out long appId))
                        manifests.Add((appId, acf));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }

        return ReadDepotsOfOthers(manifests, excludeAppId);
    }

    private static HashSet<long>? ReadDepotsOfOthers(IEnumerable<(long AppId, string Path)> manifests, long excludeAppId)
    {
        var depots = new HashSet<long>();
        foreach (var (appId, acfPath) in manifests)
        {
            if (appId == excludeAppId) continue;
            // Read even when the ACF is a link: only reading, and claiming MORE depots is the safe direction.
            if (ReadSharedText(acfPath) is not { } text) return null;
            foreach (long depotId in GameCleanupPolicy.ParseAcfDepotIds(text))
                depots.Add(depotId);
        }
        return depots;
    }

    /// <summary>
    /// The resolved install folders of every OTHER app in the same library as <paramref name="install"/>.
    /// Valve ships apps that share one folder (Half-Life 2, its episodes and Lost Coast all use
    /// <c>installdir "Half-Life 2"</c>), so deleting one app's folder can delete another's game and saves.
    /// </summary>
    /// <returns>Null (fail-closed) when the library cannot be listed or any of its ACFs cannot be read.</returns>
    public List<string>? GetOtherInstallDirsInLibrary(SteamAppInstallLocation install, long excludeAppId)
    {
        ArgumentNullException.ThrowIfNull(install);
        try
        {
            var dirs = new List<string>();
            foreach (string acf in Directory.EnumerateFiles(install.SteamAppsDir, "appmanifest_*.acf"))
            {
                string name = Path.GetFileNameWithoutExtension(acf);
                if (long.TryParse(name.AsSpan("appmanifest_".Length), out long appId) && appId == excludeAppId) continue;
                if (ReadSharedText(acf) is not { } text) return null;
                var m = InstallDirRegex().Match(text);
                if (m.Success && Unescape(m.Groups[1].Value) is { Length: > 0 } raw)
                    dirs.Add(Path.Combine(install.CommonDir, raw));
            }
            return dirs;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string? ReadSharedText(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            return reader.ReadToEnd();
        }
        catch { return null; }
    }

    /// <summary>
    /// Every appid Steam has an <c>appmanifest_&lt;appid&gt;.acf</c> for, across all libraries.
    ///
    /// <para>
    /// One directory listing per library, rather than <see cref="GetInstallDir"/>'s re-read of
    /// libraryfolders.vdf per game — the Manage page asks this for a whole page of tiles at once, which the
    /// per-game path turns into hundreds of redundant file reads.
    /// </para>
    ///
    /// <para>
    /// Presence of the manifest, deliberately, rather than <see cref="GetInstallDir"/>'s stricter "and the
    /// common\ folder exists" check. A manifest also exists while a game is still DOWNLOADING, and for the
    /// Play button that is the answer wanted: a game mid-download must not be offered an Install button
    /// that would only re-open a dialog for something Steam is already fetching.
    /// </para>
    /// </summary>
    public HashSet<long> GetInstalledAppIds() => [.. EnumerateManifests().Select(static m => m.AppId)];

    /// <summary>
    /// Appids whose manifest says the game is fully on disk: <c>StateFlags</c> has the "fully installed" bit
    /// (4). Stricter than <see cref="GetInstalledAppIds"/>, which also counts a game still downloading; this
    /// is what the "Installed" badge on a card means. A manifest that cannot be read is simply not listed.
    /// </summary>
    public HashSet<long> GetFullyInstalledAppIds() => [.. ScanFullyInstalled().Games.Select(static g => g.AppId)];

    /// <summary>
    /// The fully installed games with the name their manifest carries, plus whether the library could be
    /// read properly. <c>Failed</c> is true when Steam is not found, <c>libraryfolders.vdf</c> is missing
    /// (extra libraries would be invisible) or the scan threw — an empty <c>Games</c> then means "unknown",
    /// not "none installed". One pass: the ids and the names come from the same manifest read.
    /// </summary>
    public (List<InstalledGame> Games, bool Failed) ScanFullyInstalled()
    {
        var games = new List<InstalledGame>();
        var (manifests, failed) = ScanManifests();
        foreach (var (appId, acf) in manifests)
        {
            try
            {
                string text = File.ReadAllText(acf);
                if (StateFlagsRegex().Match(text) is { Success: true } m
                    && long.TryParse(m.Groups[1].Value, out long flags)
                    && (flags & FullyInstalledFlag) != 0)
                    games.Add(new InstalledGame(appId, NameRegex().Match(text) is { Success: true } n ? Unescape(n.Groups[1].Value) : ""));
            }
            catch (IOException) { /* Steam is rewriting it right now — next refresh will see it */ }
            catch (UnauthorizedAccessException) { }
        }
        return (games, failed);
    }

    private const long FullyInstalledFlag = 4; // StateFlags bit: k_EAppStateFullyInstalled

    [GeneratedRegex(@"""StateFlags""\s*""(\d+)""", RegexOptions.IgnoreCase)]
    private static partial Regex StateFlagsRegex();

    [GeneratedRegex(@"""name""\s*""([^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex NameRegex();

    /// <summary>Every <c>appmanifest_&lt;appid&gt;.acf</c> across the libraries, with its path.</summary>
    private IEnumerable<(long AppId, string Path)> EnumerateManifests() => ScanManifests().Found;

    private (List<(long AppId, string Path)> Found, bool Failed) ScanManifests()
    {
        const string prefix = "appmanifest_";
        var found = new List<(long, string)>();
        try
        {
            if (steam.EffectivePath is not { } steamRoot) return (found, true);
            bool failed = !File.Exists(Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf"));

            foreach (string library in GetLibraryRoots(steamRoot))
            {
                string steamapps = Path.Combine(library, "steamapps");
                if (!Directory.Exists(steamapps)) continue;

                foreach (string acf in Directory.EnumerateFiles(steamapps, $"{prefix}*.acf"))
                {
                    string name = Path.GetFileNameWithoutExtension(acf);
                    if (name.Length > prefix.Length
                        && long.TryParse(name.AsSpan(prefix.Length), out long appId))
                        found.Add((appId, acf));
                }
            }
            return (found, failed);
        }
        catch { return (found, true); } // unreadable library or odd path — report what was collected, flagged
    }

    /// <summary>Every Steam library root (the main install plus any added libraries).</summary>
    private static IEnumerable<string> GetLibraryRoots(string steamRoot)
    {
        // The main install is always a library.
        yield return steamRoot;

        string vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;

        string text;
        try { text = File.ReadAllText(vdf); } catch { yield break; }

        foreach (Match m in PathRegex().Matches(text))
        {
            string path = Unescape(m.Groups[1].Value);
            // The main root often appears here too; harmless duplicate (we just probe each).
            if (!string.Equals(path, steamRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(path))
                yield return path;
        }
    }

    /// <summary>VDF strings escape backslashes as "\\"; collapse to a real path.</summary>
    private static string Unescape(string s) => s.Replace(@"\\", @"\");
}
