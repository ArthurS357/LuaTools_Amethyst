using System.Globalization;

namespace LuaToolsGui.Services;

/// <summary>
/// Shared user-facing summaries for <see cref="GameCleanupResult"/> across Manage and Depots.
/// </summary>
public static class GameCleanupMessages
{
    public static string Describe(string gameName, GameCleanupResult result, CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        culture ??= CultureInfo.CurrentCulture;

        if (result.IsBlocked) return DescribeBlock(gameName, result.BlockReason, culture);

        if (result.NothingToRemove)
        {
            if (result.SharedManifestsPreserved > 0)
            {
                return Resources.Strings.Cleanup_Result_Nothing + " " +
                    string.Format(culture, Resources.Strings.Cleanup_Result_SharedPreserved,
                        result.SharedManifestsPreserved);
            }
            return Resources.Strings.Cleanup_Result_Nothing;
        }

        var details = new List<string>();
        if (result.FreedBytes > 0)
            details.Add(string.Format(culture, Resources.Strings.Cleanup_Result_Freed,
                ByteSize.Format(result.FreedBytes, culture)));
        if (result.ManifestsDeleted > 0)
            details.Add(string.Format(culture, Resources.Strings.Cleanup_Result_ManifestsDeleted,
                result.ManifestsDeleted));
        if (result.SharedManifestsPreserved > 0)
            details.Add(string.Format(culture, Resources.Strings.Cleanup_Result_SharedPreserved,
                result.SharedManifestsPreserved));

        string summary = result.HasErrors
            ? string.Format(culture, Resources.Strings.Cleanup_Result_PartialError, gameName, result.Errors[0])
            : string.Format(culture, Resources.Strings.Cleanup_Result_Done, gameName);

        return details.Count == 0 ? summary : $"{summary} ({string.Join(" · ", details)})";
    }

    /// <summary>Why a cleanup was (or would be) refused, in the user's words.</summary>
    public static string DescribeBlock(string gameName, GameCleanupBlockReason reason, CultureInfo? culture = null) =>
        reason switch
        {
            GameCleanupBlockReason.GameRunning =>
                string.Format(culture ?? CultureInfo.CurrentCulture, Resources.Strings.Cleanup_Err_GameRunning, gameName),
            GameCleanupBlockReason.UnsafeInstallPath => Resources.Strings.Cleanup_Err_UnsafePath,
            GameCleanupBlockReason.SymlinkDetected => Resources.Strings.Cleanup_Err_Symlink,
            GameCleanupBlockReason.BackupVaultOverlap => Resources.Strings.Cleanup_Err_BackupOverlap,
            GameCleanupBlockReason.UnreadableSiblingLua => Resources.Strings.Cleanup_Err_UnreadableSibling,
            GameCleanupBlockReason.SharedInstallDir => Resources.Strings.Cleanup_Err_SharedInstallDir,
            GameCleanupBlockReason.SteamNotFound => Resources.Strings.Err_SteamNotFound,
            GameCleanupBlockReason.OperationInProgress =>
                string.Format(culture ?? CultureInfo.CurrentCulture, Resources.Strings.Cleanup_Err_OperationInProgress, gameName),
            _ => Resources.Strings.Cleanup_Err_Generic,
        };
}
