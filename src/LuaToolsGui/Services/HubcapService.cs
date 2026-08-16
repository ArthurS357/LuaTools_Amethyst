using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using LuaToolsGui.Models;

namespace LuaToolsGui.Services;

/// <summary>
/// Talks to Hubcap (hubcapmanifest.com) DIRECTLY with the user's own API key — no lua.tools proxy.
/// Stats and manifest downloads authenticate via <c>?api_key={key}</c>; the free status check uses a
/// <c>Bearer</c> header (per the Hubcap API). Stats/status calls never throw — they return null on any
/// failure so the UI degrades gracefully; only the explicit download surfaces errors to the caller.
/// </summary>
public partial class HubcapService
{
    // Hubcap-keyed downloads can be large manifest zips; allow a generous timeout like the lua.tools client.
    private readonly HttpClient _http = new()
    {
        BaseAddress = new Uri(AppConfig.HubcapBaseUrl),
        Timeout = TimeSpan.FromMinutes(5),
    };

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // Interim staging destination, mirroring LuaToolsApiClient (downloads land here before install,
    // then are deleted once installed). Under %TEMP% so it never pollutes the user's Downloads folder.
    private static readonly string InterimDownloadsFolder =
        Path.Combine(Path.GetTempPath(), "LuaToolsGui", "downloads");

    [GeneratedRegex("^smm_[0-9a-f]{96}$")]
    private static partial Regex KeyFormatRegex();

    /// <summary>Local format check — Hubcap keys are "smm_" followed by 96 lowercase hex chars.</summary>
    public static bool IsValidKeyFormat(string? key) => key is not null && KeyFormatRegex().IsMatch(key);

    /// <summary>Usage stats for a key. Null on any network/auth failure — never throws.</summary>
    public async Task<HubcapStats?> GetStatsAsync(string key, CancellationToken ct = default)
    {
        try
        {
            var res = await _http.GetAsync($"/api/v1/user/stats?api_key={Uri.EscapeDataString(key)}", ct);
            if (!res.IsSuccessStatusCode) return null;
            return await ReadJsonAsync<HubcapStats>(res, ct);
        }
        catch { return null; }
    }

    /// <summary>Whether a manifest exists for an app (free, no usage count). Null on failure.</summary>
    public async Task<HubcapManifestStatus?> CheckStatusAsync(string key, string appid, CancellationToken ct = default)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/status/{Uri.EscapeDataString(appid)}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            var res = await _http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return null;
            return await ReadJsonAsync<HubcapManifestStatus>(res, ct);
        }
        catch { return null; }
    }

    /// <summary>Download the manifest zip for an app directly from Hubcap (counts toward the key's daily
    /// limit). Throws <see cref="ApiException"/> on failure so the download flow can report it.</summary>
    public async Task<DownloadedFile> DownloadManifestAsync(
        string appid, string key, IProgress<double?>? progress, CancellationToken ct = default)
    {
        var url = $"/api/v1/manifest/{Uri.EscapeDataString(appid)}?api_key={Uri.EscapeDataString(key)}";
        using var res = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
        {
            string message = res.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "Your Hubcap key is invalid or expired.",
                HttpStatusCode.TooManyRequests => "Your Hubcap daily limit has been reached.",
                HttpStatusCode.NotFound => "No Hubcap manifest is available for this app.",
                _ => $"Hubcap download failed ({(int)res.StatusCode}).",
            };
            throw new ApiException(message, res.StatusCode);
        }
        return await SaveResponseAsync(res, $"{appid}.zip", progress, ct);
    }

    // ── Plumbing ────────────────────────────────────────────────────

    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage res, CancellationToken ct) =>
        JsonSerializer.Deserialize<T>(await res.Content.ReadAsStringAsync(ct), JsonOpts);

    private static async Task<DownloadedFile> SaveResponseAsync(
        HttpResponseMessage res, string fallbackName, IProgress<double?>? progress, CancellationToken ct)
    {
        string fileName = res.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? fallbackName;
        foreach (char c in Path.GetInvalidFileNameChars()) fileName = fileName.Replace(c, '_');

        Directory.CreateDirectory(InterimDownloadsFolder);
        string filePath = Path.Combine(InterimDownloadsFolder, fileName);

        long? total = res.Content.Headers.ContentLength;
        await using var src = await res.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(filePath);

        var buffer = new byte[81920];
        long written = 0;
        int read;
        while ((read = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct);
            written += read;
            progress?.Report(total is > 0 ? (double)written / total.Value : null);
        }

        return new DownloadedFile(filePath, fileName);
    }
}
