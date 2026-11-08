using System.IO;
using System.Text.RegularExpressions;

namespace LuaToolsGui.Services;

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

                string installDir = Unescape(m.Groups[1].Value);
                string full = Path.Combine(library, "steamapps", "common", installDir);
                if (Directory.Exists(full)) return full;
            }
        }
        catch { /* unreadable VDF/ACF or odd path — treat as not found */ }
        return null;
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
