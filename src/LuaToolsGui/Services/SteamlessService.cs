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

                // Open ONCE, pinned (FileShare.Read: readers may look, nobody can write, replace or delete),
                // hash through that handle, screen the archive, and extract through the SAME handle. Nothing
                // can substitute the file between the check and the use.
                using var stream = AssetIntegrity.OpenPinned(zipPath);

                if (!AssetIntegrity.MatchesStream(stream, expected)) return null;

                // The hash proves it is the release we pinned, not that the archive is sane: refuse a
                // decompression bomb, an absurd entry count, a path escape or a duplicate destination before
                // anything is written into the tool folder. Executables are expected here (not blocking).
                if (FixAnalyzer.AnalyzeArchive(zipPath, ToolDir, luaProfile: LuaScreeningProfile.ApplicationCode)
                    is { Blocked: true } analysis)
                {
                    AppLog.Log($"Steamless: archive refused before extraction: {analysis.BlockReason}");
                    return null;
                }

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
        catch (Exception ex)
        {
            AppLog.Log($"Steamless: could not install the tool: {ex.GetType().Name}: {LogSanitizer.SanitizePath(ex.Message)}");
            return null;
        }
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
            string unpacked = exe + ".unpacked.exe";
            try
            {
                // A leftover from a killed run must not pass for this run's output below.
                File.Delete(unpacked);
                await RunCliAsync(cli, exe, ct);

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
            catch (OperationCanceledException)
            {
                try { File.Delete(unpacked); } catch { /* stray partial */ }
                throw;
            }
            catch (Exception ex)
            {
                if (ex is TimeoutException)
                    AppLog.Log($"Steamless: {ex.Message} ({LogSanitizer.SanitizePath(exe)}); left unchanged.");
                unchanged++;
                try { File.Delete(unpacked); } catch { /* stray partial */ }
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
            var resolved = ContainedLaunchExes(installDir, info.LaunchExes);
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
    /// The launch executables that really are inside <paramref name="installDir"/> and exist. The entries
    /// come from api.steamcmd.net, so a rooted path or a <c>..</c> escape would make Steamless patch (and
    /// replace) an executable anywhere the user can write; those are dropped, never repaired.
    /// </summary>
    internal static List<string> ContainedLaunchExes(string installDir, IEnumerable<string> launchExes) =>
        launchExes
            .Where(rel => !FixAnalyzer.IsAbsoluteOrRooted(rel))
            .Select(rel => FixAnalyzer.IsContained(installDir, rel, out string full) ? full : null)
            .OfType<string>()
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

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
        var psi = new ProcessStartInfo(cliPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(cliPath) ?? Environment.CurrentDirectory,
        };
        psi.ArgumentList.Add(exePath); // one argument, quoted by the runtime — not a hand-built command line
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start Steamless.");
        await WaitOrKillAsync(proc, CliTimeout, ct);
    }

    /// <summary>One exe takes Steamless seconds; past this it is wedged, not working.</summary>
    private static readonly TimeSpan CliTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Wait for the child, killing its tree on cancellation or after <paramref name="timeout"/>.
    /// <c>WaitForExitAsync</c> alone only stops WAITING when cancelled: the child kept running and kept the
    /// game's exe open, and with no timeout a hung Steamless pinned the Manage page busy for good. A timeout
    /// surfaces as <see cref="TimeoutException"/> — that exe counts as unchanged and the run moves on — while
    /// the caller's own cancel still throws <see cref="OperationCanceledException"/> and stops the run.
    /// </summary>
    internal static async Task WaitOrKillAsync(Process proc, TimeSpan timeout, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            await proc.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                proc.Kill(entireProcessTree: true);
                // Kill only starts the termination: wait for the handles to close, or the caller's cleanup
                // of the half-written .unpacked.exe hits a sharing violation.
                proc.WaitForExit(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException) { /* already gone */ }
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException($"Steamless did not finish within {timeout.TotalMinutes:0} minutes.");
        }
    }
}
