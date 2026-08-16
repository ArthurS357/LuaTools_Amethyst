using System.Diagnostics;
using System.IO;
using System.Text.Json;
using LuaToolsGui.Models;

namespace LuaToolsGui.Services;

/// <summary>
/// Downloads + launches CloudRedirect.exe — the user-facing CloudRedirect GUI manager (Selectively11). The
/// exe is fetched once from the latest GitHub release (via <see cref="GithubProxy"/>, so it works in blocked
/// regions) and cached under %AppData%\LuaToolsGui\cloudredirect. It self-updates, so we always grab the
/// latest asset and don't track versions. The Mode page's "Manage" button (shown when CloudRedirect is the
/// active mode) calls <see cref="LaunchAsync"/>, which opens its window visibly and returns immediately.
/// (Separate from the silent CloudRedirectCLI.exe the mode-install flow runs.)
/// </summary>
public class CloudRedirectService(GithubProxy gh)
{
    private static readonly string ToolDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LuaToolsGui", "cloudredirect");
    private static string ExePath => Path.Combine(ToolDir, "CloudRedirect.exe");

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Ensure CloudRedirect.exe is on disk (downloads the latest release asset once). Returns its
    /// path, or null if it couldn't be obtained.</summary>
    public async Task<string?> EnsureToolAsync(IProgress<double?>? progress, CancellationToken ct = default)
    {
        if (File.Exists(ExePath)) return ExePath;

        await _gate.WaitAsync(ct);
        try
        {
            if (File.Exists(ExePath)) return ExePath; // won the race

            string url = $"https://api.github.com/repos/{AppConfig.CloudRedirectRepo}/releases/latest";
            using var res = await gh.SendAsync(url, ct);
            if (res is null || !res.IsSuccessStatusCode) return null;

            var release = JsonSerializer.Deserialize<GithubRelease>(await res.Content.ReadAsStringAsync(ct), JsonOpts);
            var asset = release?.Assets.FirstOrDefault(a => a.Name.Equals("CloudRedirect.exe", StringComparison.OrdinalIgnoreCase));
            if (asset is null) return null;

            // This exe is LAUNCHED, and the download may come from a third-party mirror (GithubProxy falls
            // back to public proxies for blocked regions). Only run a binary whose bytes match the digest
            // GitHub's own API published; AssetIntegrity treats a missing digest as a failure.
            Directory.CreateDirectory(ToolDir);

            // Stage under a fresh GUID directory. The previous fixed "<ExePath>.partial" was predictable,
            // which is what makes the verify→move window exploitable by another process running as this
            // user; an unguessable path plus the exclusive handle below closes it (H3).
            string staging = Path.Combine(ToolDir, "staging-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                string staged = Path.Combine(staging, "CloudRedirect.exe");
                await gh.DownloadAsync(asset.DownloadUrl, staged, progress, ct); // single exe — no zip

                // Hash through a handle opened with FileShare.None so nothing can substitute the file
                // between the check and the move.
                using (var stream = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    if (!AssetIntegrity.MatchesStream(stream, asset.Digest)) return null;
                }

                File.Move(staged, ExePath, overwrite: true);
            }
            finally
            {
                try { Directory.Delete(staging, recursive: true); } catch (IOException) { /* temp cleanup */ }
            }

            return File.Exists(ExePath) ? ExePath : null;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
        finally { _gate.Release(); }
    }

    /// <summary>Download (if needed) and launch the CloudRedirect GUI. Fire-and-forget — its window opens and
    /// we don't wait for it. Returns false if the tool couldn't be downloaded or started.</summary>
    public async Task<bool> LaunchAsync(IProgress<double?>? progress, CancellationToken ct = default)
    {
        string? exe = await EnsureToolAsync(progress, ct);
        if (exe is null) return false;

        try
        {
            // GUI app: show its window (UseShellExecute) and don't block our app on it.
            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                WorkingDirectory = ToolDir,
            });
            return true;
        }
        catch { return false; }
    }
}
