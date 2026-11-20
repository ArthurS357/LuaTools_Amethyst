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
/// The last good Fixes catalog body, kept under <c>%AppData%\LuaToolsAmethyst\cache</c> so the Fixes tab still
/// has a catalog when the network is down. Two files, one per source: <see cref="FileName"/> holds lua.tools'
/// <c>/api/denuvo/listings</c> body and <see cref="CatalogFileName"/> holds the LuaTools-Catalog
/// <c>catalog.json</c> (which also carries the orphan fixes). They are kept apart so a lua.tools answer, served
/// while the catalog repository is unreachable, can never overwrite a copy that has orphan fixes in it. A file on
/// disk and nothing else: no request of its own, and its age is the file's last-write time.
/// </summary>
internal static class DenuvoListingsCache
{
    internal const string FileName = "denuvo_listings.json";
    internal const string CatalogFileName = "catalog.json";

    /// <summary>Older than this and the copy is ignored: a day-old catalog is a hint, a week-old one is a lie.</summary>
    internal static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    /// <summary>Atomic (temp + rename), best effort: a cache that cannot be written must never fail the load.</summary>
    internal static void Save(string directory, string json, string fileName = FileName)
    {
        try
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, fileName);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Log($"Fixes: could not save the offline catalog: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>The newest usable saved copy (and its raw body, for the orphan fixes), or null when neither
    /// file exists, is within <see cref="MaxAge"/>, or can be read (a corrupt copy is no better than a missing one).</summary>
    internal static (DenuvoListingsResult Result, string Json)? TryLoadNewest(
        string directory, JsonSerializerOptions options, DateTime? now = null)
    {
        (DenuvoListingsResult Result, string Json)? newest = null;
        foreach (string fileName in new[] { CatalogFileName, FileName })
        {
            if (TryLoad(directory, fileName, options, now) is { } candidate
                && (newest is null || candidate.Result.CachedAt > newest.Value.Result.CachedAt))
                newest = candidate;
        }
        return newest;
    }

    private static (DenuvoListingsResult Result, string Json)? TryLoad(
        string directory, string fileName, JsonSerializerOptions options, DateTime? now)
    {
        try
        {
            string path = Path.Combine(directory, fileName);
            if (!File.Exists(path)) return null;

            var savedAt = File.GetLastWriteTime(path);
            if ((now ?? DateTime.Now) - savedAt > MaxAge) return null;

            string json = File.ReadAllText(path);
            var data = JsonSerializer.Deserialize<DenuvoListingsResponse>(json, options);
            return data is null ? null : (new DenuvoListingsResult(data, savedAt), json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
