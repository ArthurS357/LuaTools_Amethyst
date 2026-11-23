using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using LuaToolsGui.Models;

namespace LuaToolsGui.Services;

public sealed partial class DepotDownloaderService
{
    private static readonly string ToolDir = Path.Combine(
        AppDataMigrator.CurrentDirectory, "depotdownloader");

    private static string ExePath => Path.Combine(ToolDir, "DepotDownloaderMod.exe");

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>How long an up-to-date check is trusted before GitHub is asked again.</summary>
    private static readonly TimeSpan ToolCheckInterval = TimeSpan.FromHours(6);

    private readonly SemaphoreSlim _toolGate = new(1, 1);

    /// <summary>
    /// Whether the compiled-in pin is present and well-formed. False disables the whole feature — the tool
    /// is never downloaded and never run.
    /// </summary>
    /// <remarks>
    /// This is the switch that makes an un-pinned build safe rather than merely un-updated. Upstream has no
    /// equivalent: it will happily fetch and execute whatever <c>releases/latest</c> hands back.
    /// </remarks>
    public static bool PinIsUsable =>
        AssetIntegrity.ParseDigest(AppConfig.DepotDownloaderPinnedSha256) is not null
        && AppConfig.DepotDownloaderPinnedAssetName.Length > 0
        && AppConfig.DepotDownloaderPinnedTag.Length > 0;

    // ── Tool acquisition ─────────────────────────────────────────────

    private static bool CheckedRecently(long lastMs) =>
        lastMs > 0
        && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - lastMs < (long)ToolCheckInterval.TotalMilliseconds;

    /// <summary>
    /// Ensure the pinned tool is on disk and verified. Null when it could not be obtained at all.
    /// </summary>
    /// <remarks>
    /// Every path here is fail-closed. The release must carry the pinned tag, the asset must have the
    /// pinned name, the digest GitHub publishes must equal the pinned digest, and the delivered bytes must
    /// hash to it as well. A failure never falls back to "run whatever is on disk from an older pin"
    /// either: the recorded version has to match the pin before an existing exe is reused.
    /// </remarks>
    public async Task<string?> EnsureToolAsync(IProgress<double?>? progress, CancellationToken ct = default)
    {
        if (!PinIsUsable)
        {
            AppLog.Log("DepotDownloader: no usable pin in AppConfig — the depot downloader is disabled.");
            return null;
        }

        if (HaveVerifiedTool() && CheckedRecently(cache.DepotDownloaderCheckedAtMs)) return ExePath;

        await _toolGate.WaitAsync(ct);
        try
        {
            if (HaveVerifiedTool() && CheckedRecently(cache.DepotDownloaderCheckedAtMs)) return ExePath; // won the race

            string url = $"https://api.github.com/repos/{AppConfig.DepotDownloaderRepo}" +
                         $"/releases/tags/{AppConfig.DepotDownloaderPinnedTag}";
            using var res = await gh.SendAsync(url, ct);
            if (res is null || !res.IsSuccessStatusCode)
            {
                AppLog.Log($"DepotDownloader: release lookup failed ({res?.StatusCode.ToString() ?? "no response"}).");
                return Fallback();
            }

            var release = JsonSerializer.Deserialize<GithubRelease>(await res.Content.ReadAsStringAsync(ct), JsonOpts);
            var asset = release?.Assets.FirstOrDefault(a =>
                string.Equals(a.Name, AppConfig.DepotDownloaderPinnedAssetName, StringComparison.Ordinal));
            if (asset is null)
            {
                AppLog.Log($"DepotDownloader: release {AppConfig.DepotDownloaderPinnedTag} has no asset named " +
                           $"{AppConfig.DepotDownloaderPinnedAssetName}.");
                return Fallback();
            }

            // The digest the API advertises has to agree with the pin BEFORE anything is fetched. This is
            // what turns "verified against the same response that chose the URL" into "verified against
            // this build".
            if (AssetIntegrity.ParseDigest(asset.Digest) is not { } published
                || !published.Equals(AssetIntegrity.ParseDigest(AppConfig.DepotDownloaderPinnedSha256),
                                     StringComparison.Ordinal))
            {
                AppLog.Log("DepotDownloader: published digest does not match the pinned digest — refusing. " +
                           "Bump AppConfig.DepotDownloaderPinned* together after verifying the new release.");
                return Fallback();
            }

            if (HaveVerifiedTool())
            {
                cache.DepotDownloaderCheckedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                return ExePath; // already on the pinned build; skip the ~37 MB download
            }

            Directory.CreateDirectory(ToolDir);
            string zipPath = Path.Combine(ToolDir, "depotdownloader.zip");
            try
            {
                // Repo-scoped: refuses before a byte is fetched if the release metadata points anywhere
                // other than this repo's own release assets.
                await gh.DownloadAssetAsync(asset.DownloadUrl, AppConfig.DepotDownloaderOwner,
                    AppConfig.DepotDownloaderRepoName, zipPath, progress, ct);

                // Verify and extract through ONE held handle, so what was hashed is what gets extracted.
                //
                // Hashing a path and then re-opening it to use it leaves a window in which another process
                // running as this user can substitute the file — and the bytes that end up as a running
                // .exe are then not the bytes whose digest was checked. AssetIntegrity.OpenPinned takes
                // FileShare.Read WITHOUT FileShare.Delete, so the file cannot be replaced, truncated or
                // renamed while this handle is open.
                using (var pinned = AssetIntegrity.OpenPinned(zipPath))
                {
                    // Fail-closed, against the PIN rather than the response. AssetIntegrity treats an
                    // absent or malformed digest as a failure, so there is no digest-less path through here.
                    if (!AssetIntegrity.MatchesStream(pinned, AppConfig.DepotDownloaderPinnedSha256))
                    {
                        AppLog.Log("DepotDownloader: delivered bytes do not match the pinned digest — discarding.");
                        return Fallback();
                    }

                    // Extract the WHOLE zip: the exe needs SteamKit2.dll and friends beside it.
                    // ExtractToDirectory validates that no entry escapes the destination, so a crafted
                    // archive cannot write outside ToolDir.
                    pinned.Position = 0;
                    using var archive = new ZipArchive(pinned, ZipArchiveMode.Read, leaveOpen: true);
                    archive.ExtractToDirectory(ToolDir, overwriteFiles: true);
                }
            }
            finally
            {
                try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch (IOException) { /* harmless */ }
            }

            if (!File.Exists(ExePath))
            {
                AppLog.Log("DepotDownloader: the pinned archive did not contain DepotDownloaderMod.exe at its root.");
                return null;
            }

            cache.DepotDownloaderVersion = AppConfig.DepotDownloaderPinnedTag;
            cache.DepotDownloaderCheckedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return ExePath;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or HttpRequestException or JsonException
                                      or UnauthorizedAccessException or InvalidDataException)
        {
            AppLog.Log($"DepotDownloader: obtaining the tool failed — {ex.Message}");
            return Fallback();
        }
        finally { _toolGate.Release(); }

        // A failed lookup still counts as "we looked": EnsureToolAsync runs once per depot, so an offline
        // ten-depot game would otherwise walk every GithubProxy mirror ten times. Backing off costs at most
        // one interval of update latency, and it never resurrects a tool that isn't on the pinned build.
        string? Fallback()
        {
            bool have = HaveVerifiedTool();
            if (have) cache.DepotDownloaderCheckedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return have ? ExePath : null;
        }
    }

    /// <summary>
    /// True when the exe on disk is the one this build pins. The recorded version is what makes an existing
    /// install reusable — an exe left over from a previous pin is treated as absent, not as good enough.
    /// </summary>
    private bool HaveVerifiedTool() =>
        File.Exists(ExePath)
        && string.Equals(cache.DepotDownloaderVersion, AppConfig.DepotDownloaderPinnedTag, StringComparison.Ordinal);
}
