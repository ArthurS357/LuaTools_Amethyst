using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace LuaToolsGui.Services;

/// <summary>
/// Bulk appid → name lookup. Primary source is the Morrenus app list (a single static JSON,
/// ~341k entries incl. delisted apps, no rate limit); falls back to Steam's GetAppList.
/// Cached to disk and refreshed periodically. Used for game names; covers come from elsewhere.
/// </summary>
public class SteamAppListCache
{
    private const string MorrenusUrl = "https://applist.morrenus.xyz/";
    private const string SteamUrl = "https://api.steampowered.com/ISteamApps/GetAppList/v2/";

    private static readonly string CacheFile =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LuaToolsGui", "steam-applist.json");
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly ConcurrentDictionary<long, string> _names = new();
    private Task? _loadTask;

    public string? GetName(long appid) => _names.TryGetValue(appid, out var n) ? n : null;

    /// <summary>Ensure the name list is loaded (from disk, or downloaded once). Safe to call repeatedly.</summary>
    public Task EnsureLoadedAsync() => _loadTask ??= LoadAsync();

    private async Task LoadAsync()
    {
        // Fresh disk cache → use it.
        try
        {
            if (File.Exists(CacheFile) && DateTime.UtcNow - File.GetLastWriteTimeUtc(CacheFile) < MaxAge)
            {
                var map = await ReadCacheFileAsync();
                if (map is { Count: > 0 })
                {
                    foreach (var (k, v) in map) _names[k] = v;
                    return;
                }
            }
        }
        catch { /* fall through to download */ }

        // Download fresh: Morrenus first (reliable, includes delisted), then Steam as a fallback.
        if (await TryDownloadAsync(MorrenusUrl, flatArray: true) ||
            await TryDownloadAsync(SteamUrl, flatArray: false))
        {
            await SaveAsync();
            return;
        }

        // Both sources failed — fall back to a stale cache if we have one.
        try
        {
            if (_names.IsEmpty && File.Exists(CacheFile))
            {
                var map = await ReadCacheFileAsync();
                if (map is not null) foreach (var (k, v) in map) _names[k] = v;
            }
        }
        catch (Exception ex) { AppLog.Log($"SteamAppList: cache read failed: {ex.GetType().Name}: {ex.Message}"); /* give up — names fall back to lua-parsed / appid */ }
    }

    /// <summary>Download one source. Morrenus is a flat [{appid,name}]; Steam nests under applist.apps.</summary>
    private async Task<bool> TryDownloadAsync(string url, bool flatArray)
    {
        try
        {
            await using var stream = await _http.GetStreamAsync(url);
            using var doc = await JsonDocument.ParseAsync(stream);
            var apps = flatArray
                ? doc.RootElement
                : doc.RootElement.GetProperty("applist").GetProperty("apps");

            foreach (var app in apps.EnumerateArray())
            {
                long id = app.GetProperty("appid").GetInt64();
                string? name = app.GetProperty("name").GetString();
                if (!string.IsNullOrWhiteSpace(name)) _names[id] = name;
            }
            return !_names.IsEmpty;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Deserialize the on-disk list straight from the file. It holds ~341k names (well over 10 MB of JSON):
    /// the old <c>ReadAllTextAsync</c> first materialised all of it as one UTF-16 string, twice the file's
    /// size in memory, only to hand it to the parser and drop it. Streaming skips that copy entirely.
    /// </summary>
    private static Task<Dictionary<long, string>?> ReadCacheFileAsync() => ReadListAsync(CacheFile);

    internal static async Task<Dictionary<long, string>?> ReadListAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, useAsync: true);
        return await JsonSerializer.DeserializeAsync<Dictionary<long, string>>(stream);
    }

    /// <summary>
    /// Serialize straight into a temp file, then move it over the cache: no whole-file string in memory, and
    /// a kill mid-write leaves the previous list intact instead of a truncated one (which used to cost the
    /// next launch a full re-download).
    /// </summary>
    private async Task SaveAsync()
    {
        try { await WriteListAsync(CacheFile, _names); }
        catch (Exception ex) // best effort, as before: a failed save must not fault the load task
        {
            AppLog.Log($"SteamAppList: cache write failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal static async Task WriteListAsync(string path, IReadOnlyDictionary<long, string> names)
    {
        string tmp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None,
                             bufferSize: 64 * 1024, useAsync: true))
                await JsonSerializer.SerializeAsync(stream, names);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { /* best effort */ }
        }
    }
}
