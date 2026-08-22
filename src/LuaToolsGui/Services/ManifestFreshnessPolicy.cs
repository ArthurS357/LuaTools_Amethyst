using LuaToolsGui.Models;

namespace LuaToolsGui.Services;

/// <summary>
/// Pure decision: does a locally installed Hubcap manifest need updating? No I/O — the adapter
/// (<see cref="CacheService"/> for the local side, <see cref="HubcapService"/> for the remote side)
/// supplies both inputs. See the "KNOWN GAP" note on <see cref="HubcapManifestStatus"/> for why this
/// didn't exist before: nothing persisted what was actually downloaded, so there was nothing here to
/// compare Hubcap's answer against.
/// </summary>
public static class ManifestFreshnessPolicy
{
    /// <summary>
    /// True if the installed copy should be refreshed.
    ///
    /// <para>
    /// Compares Hubcap's <see cref="HubcapManifestStatus.FileModified"/> as an OPAQUE change token —
    /// never parsed as a date. The API sends it with no timezone offset, so any date-math comparison
    /// would silently reinterpret it in whatever zone the machine happens to be in. Detecting "the token
    /// changed" sidesteps that: it doesn't matter WHEN Hubcap rebuilt the manifest, only whether it
    /// rebuilt it since the copy we have was downloaded. It also avoids the trap the removed comment
    /// warned about — comparing a local file's write time, which a reinstall or unrelated re-copy resets
    /// with no change in content, and which would report a current copy as stale.
    /// </para>
    /// </summary>
    /// <param name="installedFileModified">The <c>file_modified</c> token recorded when this appid's
    /// manifest was last downloaded via <see cref="CacheService.SaveInstalledManifestFileModified"/>, or
    /// null if nothing is on record.</param>
    /// <param name="latest">Hubcap's current status answer for the app.</param>
    public static bool IsStale(string? installedFileModified, HubcapManifestStatus latest)
    {
        // Hubcap's own verdict is authoritative when it says its copy moved — trust it even if the local
        // record is missing or happens to match (a match then would mean the record is wrong, not current).
        if (latest.NeedsUpdate) return true;

        // Nothing on record for this appid: never downloaded through a path that recorded it (or recorded
        // before this tracking existed). Not evidence of staleness — claiming "outdated" here would be a
        // guess, and a wrong guess sends the user to re-download something that may already be current.
        if (installedFileModified is null) return false;

        // Hubcap didn't send a marker this time — can't compare, so don't claim to know either way.
        if (latest.FileModified is null) return false;

        return !string.Equals(installedFileModified, latest.FileModified, StringComparison.Ordinal);
    }
}
