using System.IO;
using System.Text.Json;
using LuaToolsGui.Models;

namespace LuaToolsGui.Services;

/// <summary>
/// The Fixes catalog and where it came from. <see cref="CachedAt"/> is null for a fresh network answer and
/// the time the copy was saved for an offline one, so "is this stale?" and "since when?" are one value.
/// </summary>
public sealed record DenuvoListingsResult(DenuvoListingsResponse Data, DateTime? CachedAt)
{
    public bool IsOffline => CachedAt is not null;
}

/// <summary>
/// The last good <c>/api/denuvo/listings</c> body, kept at <c>%AppData%\LuaToolsAmethyst\cache\denuvo_listings.json</c>
/// so the Fixes tab still has a catalog when lua.tools (or the user's connection) is down. A file on disk and
/// nothing else: no request of its own, no index, and its age is the file's last-write time.
/// </summary>
internal static class DenuvoListingsCache
{
    internal const string FileName = "denuvo_listings.json";

    /// <summary>Older than this and the copy is ignored: a day-old catalog is a hint, a week-old one is a lie.</summary>
    internal static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    /// <summary>Atomic (temp + rename), best effort: a cache that cannot be written must never fail the load.</summary>
    internal static void Save(string directory, string json)
    {
        try
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, FileName);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Log($"Fixes: could not save the offline catalog: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>The saved catalog, or null when there is none, it is older than <see cref="MaxAge"/>, or it
    /// cannot be read (a corrupt copy is no better than a missing one).</summary>
    internal static DenuvoListingsResult? TryLoad(string directory, JsonSerializerOptions options, DateTime? now = null)
    {
        try
        {
            string path = Path.Combine(directory, FileName);
            if (!File.Exists(path)) return null;

            var savedAt = File.GetLastWriteTime(path);
            if ((now ?? DateTime.Now) - savedAt > MaxAge) return null;

            var data = JsonSerializer.Deserialize<DenuvoListingsResponse>(File.ReadAllText(path), options);
            return data is null ? null : new DenuvoListingsResult(data, savedAt);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
