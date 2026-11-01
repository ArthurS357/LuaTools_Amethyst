using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace LuaToolsGui.Services.Backups;

public enum SaveLocationKind
{
    /// <summary><c>&lt;Steam&gt;\userdata\&lt;account&gt;\&lt;appid&gt;</c>.</summary>
    Userdata,
    Documents,
    MyGames,
    SavedGamesDocuments,
    SavedGames,
    LocalAppData,
    LocalLow,
    RoamingAppData,
    /// <summary>A folder the user picked by hand and that is registered in settings for this appid.</summary>
    Custom,
}

/// <summary>One place a game keeps saves.</summary>
/// <param name="Key">Folder name under <c>saves/</c> in the zip. Deterministic from the game, so a restore
/// recomputes it instead of trusting a path read from the archive.</param>
/// <param name="Path">Full path of the folder.</param>
/// <param name="DisplayName">Friendly path for the UI (<c>Documents\Saved Games\Hades</c>); never contains the user name.</param>
/// <param name="Exists">True when the folder is a real directory (no link) holding at least one file.</param>
/// <param name="SizeBytes">Total bytes of its files; 0 when detection ran without measuring.</param>
/// <param name="FileCount">Number of files; 1 when detection ran without measuring (only "any file" was checked).</param>
/// <param name="Anchor">Directory ABOVE <see cref="Path"/> from which no link may sit on the way down. Never written to.</param>
/// <param name="Account">Steam account id, for <see cref="SaveLocationKind.Userdata"/> only.</param>
public sealed record SaveLocation(
    SaveLocationKind Kind, string Key, string Path, string DisplayName,
    bool Exists, long SizeBytes, int FileCount, string Anchor, string? Account = null);

/// <summary>The Windows folders saves are looked for under. A null root is skipped.</summary>
public sealed record SaveRoots(string? Documents, string? UserProfile, string? LocalAppData, string? RoamingAppData)
{
    /// <summary>No roots at all: only <c>userdata</c> and custom folders are searched (used by hermetic tests).</summary>
    public static SaveRoots None { get; } = new(null, null, null, null);

    public static SaveRoots Current() => new(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
}

/// <summary>
/// Where a game's saves can live. Exact paths only: <c>&lt;root&gt;\&lt;game name&gt;</c> for each known root,
/// <c>userdata</c> by appid, and the user's own folders. There is no fuzzy name search — a wrong guess here
/// is a restore into the wrong folder.
/// </summary>
internal static partial class SaveLocationCatalog
{
    /// <summary>Folder names that exist under the roots for reasons that have nothing to do with a game. A game
    /// named like one of them must not make us back up (or restore into) the folder of the same name.</summary>
    private static readonly HashSet<string> GenericNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Temp", "Packages", "Programs", "Google", "Mozilla", "Steam", "Discord", "Adobe", "NVIDIA",
        "Low", "LocalLow", "My Games", "Saved Games", "Publishers", "Comms", "CrashDumps", "D3DSCache", "History",
        "Apps", "Downloads", "Pictures", "Music", "Videos", "Desktop",
    };

    [GeneratedRegex(@"^Userdata_(?<acct>[0-9]+)_(?<app>[0-9]+)\z")]
    private static partial Regex UserdataKeyRegex();

    private static SaveLocation Make(SaveLocationKind kind, string key, string path, string display, string anchor, string? account = null) =>
        new(kind, key, path, display, Exists: false, SizeBytes: 0, FileCount: 0, anchor, account);

    private static string? Join(string? root, params string[] parts) =>
        root is null ? null : Path.Combine([root, .. parts]);

    private static string? Rooted(string? root) =>
        !string.IsNullOrWhiteSpace(root) && Path.IsPathFullyQualified(root) ? root.TrimEnd('\\') : null;

    internal static SaveLocation Userdata(string userdataDir, string account, long appId)
    {
        string app = appId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Make(SaveLocationKind.Userdata, $"Userdata_{account}_{app}", Path.Combine(userdataDir, account, app),
            $@"Steam\userdata\{account}\{app}", userdataDir, account);
    }

    /// <summary>The locations that are named after the game, plus the user's registered folders. Says nothing
    /// about whether they exist — a restore needs them even when the saves were wiped.</summary>
    internal static IEnumerable<SaveLocation> Named(string? gameName, SaveRoots roots, IReadOnlyList<string> custom)
    {
        string name = SaveBackupService.SanitizeFolderName(gameName);
        if (name.Length >= 2 && !GenericNames.Contains(name))
        {
            string? docs = Rooted(roots.Documents), profile = Rooted(roots.UserProfile);
            string? local = Rooted(roots.LocalAppData), roaming = Rooted(roots.RoamingAppData);

            (SaveLocationKind Kind, string Prefix, string Display, string? Root, string? Base)[] table =
            [
                (SaveLocationKind.Documents, "Documents", "Documents", docs, docs),
                (SaveLocationKind.MyGames, "Documents_MyGames", @"Documents\My Games", docs, Join(docs, "My Games")),
                (SaveLocationKind.SavedGamesDocuments, "Documents_SavedGames", @"Documents\Saved Games", docs, Join(docs, "Saved Games")),
                (SaveLocationKind.SavedGames, "SavedGames", "Saved Games", profile, Join(profile, "Saved Games")),
                (SaveLocationKind.LocalAppData, "AppData_Local", @"AppData\Local", local, local),
                (SaveLocationKind.LocalLow, "AppData_LocalLow", @"AppData\LocalLow", profile, Join(profile, "AppData", "LocalLow")),
                (SaveLocationKind.RoamingAppData, "AppData_Roaming", @"AppData\Roaming", roaming, roaming),
            ];
            foreach (var (kind, prefix, display, root, basePath) in table)
                if (root is not null && basePath is not null)
                    yield return Make(kind, $"{prefix}_{name}", Path.Combine(basePath, name), $@"{display}\{name}", root);
        }

        foreach (string path in custom)
            if (Custom(path) is { } loc) yield return loc;
    }

    /// <summary>The location a zip subfolder name stands for, for THIS game, or null. Anything that is not a
    /// key we would have written ourselves — a traversal, another game's key, a stale custom folder — is null.</summary>
    internal static SaveLocation? Resolve(
        string key, long appId, string? gameName, string? steamRoot, SaveRoots roots, IReadOnlyList<string> custom)
    {
        if (UserdataKeyRegex().Match(key) is { Success: true } m)
        {
            string acct = m.Groups["acct"].Value;
            if (steamRoot is null || acct == "0" || m.Groups["app"].Value != appId.ToString(System.Globalization.CultureInfo.InvariantCulture))
                return null;
            return Userdata(Path.Combine(steamRoot, "userdata"), acct, appId);
        }
        return Named(gameName, roots, custom).FirstOrDefault(l => string.Equals(l.Key, key, StringComparison.Ordinal));
    }

    internal static SaveLocation? Custom(string? path)
    {
        if (!IsAcceptableCustomFolder(path)) return null;
        string full = System.IO.Path.GetFullPath(path!).TrimEnd('\\');
        string leaf = SaveBackupService.SanitizeFolderName(System.IO.Path.GetFileName(full));
        if (leaf.Length == 0) leaf = "folder";
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full.ToUpperInvariant())))[..8];
        return Make(SaveLocationKind.Custom, $"Custom_{leaf}_{hash}", full, full, System.IO.Path.GetDirectoryName(full)!);
    }

    /// <summary>A folder the user may register: a full path that is not a drive root and not part of Windows
    /// or Program Files. settings.json is writable by any process of the user, so this runs again on every use.</summary>
    internal static bool IsAcceptableCustomFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathFullyQualified(path)) return false;
        try
        {
            string full = System.IO.Path.GetFullPath(path).TrimEnd('\\');
            if (System.IO.Path.GetDirectoryName(full) is null) return false; // "C:\" or a UNC share root
            foreach (var forbidden in new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            {
                string dir = Environment.GetFolderPath(forbidden).TrimEnd('\\');
                if (dir.Length > 0 && (full.Equals(dir, StringComparison.OrdinalIgnoreCase)
                        || full.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase))) return false;
            }
            return true;
        }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
    }
}
