using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using LuaToolsGui.Models;

namespace LuaToolsGui.Services;

/// <summary>Outcome of a Steamless run over a game's executables.</summary>
public record SteamlessResult(int Patched, int Unchanged, int Total, string? Error)
{
    public bool Failed => Error is not null;
}

/// <summary>
/// Runs <c>Steamless.CLI.exe</c> (atom0s/Steamless) to strip SteamStub DRM from a game's executable(s).
/// The tool is downloaded once from GitHub (via <see cref="GithubProxy"/>, so it works in blocked regions)
/// and cached under %AppData%\LuaToolsAmethyst\steamless. The target exe(s) are resolved precisely from Steam's
/// own launch config (<see cref="SteamDepotInfo"/> → config.launch); if that's unavailable we fall back to a
/// filtered recursive scan of the install folder. Each patched exe is backed up to <c>&lt;exe&gt;.bak</c>
/// first. Steamless silently no-ops on exes without DRM, so over-selection is harmless.
/// </summary>
public class SteamlessService(GithubProxy gh, SteamLibraryService library, SteamDepotInfo depots)
{
    private static readonly string ToolDir =
        Path.Combine(AppDataMigrator.CurrentDirectory, "steamless");
    private static string CliPath => Path.Combine(ToolDir, "Steamless.CLI.exe");

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // Redists/launchers/anti-cheat/etc. to ignore in the fallback folder scan (mirrors the reference script).
    private static readonly string[] SkipPatterns =
    [
        "redist", "vcredist", "directx", "dxsetup", "vc_redist", "dotnet", "setup", "install", "uninstall",
        "crashreport", "crashhandler", "bugsplat", "easyanticheat", "battleye", "be_service", "launch",
        "_launcher", "bootstrap", "cefprocess", "cef_process", "steamwebhelper", "uplay",
    ];

    private readonly SemaphoreSlim _toolGate = new(1, 1);

    /// <summary>Ensure Steamless.CLI.exe is on disk (downloads + extracts once). Returns its path, or null
    /// if the tool couldn't be obtained.</summary>
    public async Task<string?> EnsureToolAsync(IProgress<double?>? progress, CancellationToken ct = default)
    {
        if (File.Exists(CliPath)) return CliPath;

        await _toolGate.WaitAsync(ct);
        try
        {
            if (File.Exists(CliPath)) return CliPath; // won the race elsewhere

            // Latest release → the single distributable .zip asset.
            string url = $"https://api.github.com/repos/{AppConfig.SteamlessRepo}/releases/latest";
            using var res = await gh.SendAsync(url, ct);
            if (res is null || !res.IsSuccessStatusCode) return null;

            var release = JsonSerializer.Deserialize<GithubRelease>(await res.Content.ReadAsStringAsync(ct), JsonOpts);
            var asset = release?.Assets.FirstOrDefault(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            if (asset is null) return null;

            // What comes out of this zip is EXECUTED, and the bytes can arrive from a third-party mirror
            // (GithubProxy falls back to public proxies in blocked regions), so nothing runs unverified.
            string? expected = ResolveExpectedDigest(asset);
            if (expected is null) return null; // still fail-closed — see ResolveExpectedDigest

            Directory.CreateDirectory(ToolDir);

            // Stage under a fresh GUID directory rather than a fixed "steamless.zip". A predictable staging
            // path is what makes the verify→extract window exploitable: another process running as this
            // user can swap the file in between. An unguessable path plus the exclusive handle below closes
            // that (H3).
            string staging = Path.Combine(ToolDir, "staging-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                string zipPath = Path.Combine(staging, "steamless.zip");
                // Pinned to atom0s/Steamless, not merely to a GitHub host: this zip is EXECUTED, and the URL
                // comes from release JSON an API mirror may have served. See GithubProxy.IsAssetUrlForRepo.
                string[] repo = AppConfig.SteamlessRepo.Split('/');
                await gh.DownloadAssetAsync(asset.DownloadUrl, repo[0], repo[1], zipPath, progress, ct);

                // Open ONCE with FileShare.None, hash through that handle, and extract through the SAME
                // handle. Nothing can substitute the file between the check and the use, because nothing
                // else can open it at all while we hold it.
                using var stream = new FileStream(
                    zipPath, FileMode.Open, FileAccess.Read, FileShare.None);

                if (!AssetIntegrity.MatchesStream(stream, expected)) return null;

                stream.Position = 0;
                using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
                archive.ExtractToDirectory(ToolDir, overwriteFiles: true);
            }
            finally
            {
                try { Directory.Delete(staging, recursive: true); } catch (IOException) { /* temp cleanup */ }
            }

            return File.Exists(CliPath) ? CliPath : null;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
        finally { _toolGate.Release(); }
    }

    /// <summary>Strip SteamStub DRM from a game's executable(s). Best-effort; never throws out of the loop.</summary>
    public async Task<SteamlessResult> PatchGameAsync(long appId, IProgress<double?>? progress, CancellationToken ct = default)
    {
        string? installDir = library.GetInstallDir(appId);
        if (installDir is null)
            return new SteamlessResult(0, 0, 0, "no-install");

        string? cli = await EnsureToolAsync(progress, ct);
        if (cli is null)
            return new SteamlessResult(0, 0, 0, "tool");

        var exes = await ResolveExesAsync(appId, installDir, ct);
        if (exes.Count == 0)
            return new SteamlessResult(0, 0, 0, "no-exe");

        int patched = 0, unchanged = 0;
        foreach (string exe in exes)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await RunCliAsync(cli, exe, ct);

                string unpacked = exe + ".unpacked.exe";
                if (File.Exists(unpacked))
                {
                    // Back the original up (only once — never clobber a known-good .bak), then swap in.
                    string bak = exe + ".bak";
                    if (!File.Exists(bak)) File.Copy(exe, bak);
                    File.Delete(exe);
                    File.Move(unpacked, exe);
                    patched++;
                }
                else
                {
                    unchanged++; // no SteamStub DRM, or Steamless declined — leave it as-is
                }
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                unchanged++;
                try { File.Delete(exe + ".unpacked.exe"); } catch { /* stray partial */ }
            }
        }
        return new SteamlessResult(patched, unchanged, exes.Count, null);
    }

    /// <summary>The exe(s) to patch: precise from Steam's launch config, else a filtered folder scan.</summary>
    private async Task<IReadOnlyList<string>> ResolveExesAsync(long appId, string installDir, CancellationToken ct)
    {
        // Primary: exact executables Steam launches (config.launch), resolved to existing files on disk.
        var info = await depots.GetAsync(appId, ct);
        if (info is { LaunchExes.Count: > 0 })
        {
            var resolved = info.LaunchExes
                .Select(rel => Path.Combine(installDir, rel))
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (resolved.Count > 0) return resolved;
        }

        // Fallback: scan every .exe, minus redists/launchers/etc. (game manifests sometimes omit launch).
        try
        {
            return Directory.EnumerateFiles(installDir, "*.exe", SearchOption.AllDirectories)
                .Where(p => !SkipPatterns.Any(s => Path.GetFileName(p).Contains(s, StringComparison.OrdinalIgnoreCase)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch { return []; }
    }

    /// <summary>
    /// The SHA-256 this download must match, or <see langword="null"/> to refuse the download entirely.
    ///
    /// <para>
    /// The asset named in <see cref="AppConfig.SteamlessPinnedAssetName"/> is checked against the digest
    /// compiled into this build, ALWAYS — never against the one in the release JSON. That JSON may have been
    /// served by an API mirror, and a mirror that also names its own file can supply a matching digest, so a
    /// published digest is not a trust anchor there. (v3.1.0.5 also predates GitHub's <c>digest</c> field.)
    /// </para>
    ///
    /// <para>
    /// Any other asset must carry a well-formed published digest; absent or malformed means refuse.
    /// </para>
    /// </summary>
    internal static string? ResolveExpectedDigest(GithubAsset asset) =>
        asset.Name.Equals(AppConfig.SteamlessPinnedAssetName, StringComparison.OrdinalIgnoreCase)
            ? AssetIntegrity.ParseDigest(AppConfig.SteamlessPinnedSha256)
            : AssetIntegrity.ParseDigest(asset.Digest);

    private static async Task RunCliAsync(string cliPath, string exePath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(cliPath, $"\"{exePath}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(cliPath) ?? Environment.CurrentDirectory,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start Steamless.");
        await proc.WaitForExitAsync(ct);
    }
}
