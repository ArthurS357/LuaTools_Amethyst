using System.Diagnostics;
using System.IO;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Tests for <see cref="UninstallCleanup"/>, which runs from Velopack's OnBeforeUninstall hook.
///
/// <para>
/// Why these matter more than most: this code path executes exactly once, on a machine the developer
/// never sees, while the app is being deleted. There is no UI to notice a failure and — since the
/// uninstall must never be blocked — every step swallows its own errors. Left untested, a regression here
/// is completely silent, and the thing it would silently stop removing is the CDP marker that keeps
/// Steam's unauthenticated debug port open forever.
/// </para>
///
/// <para>
/// Everything runs against temp directories. <c>unregisterProtocol: false</c> is passed throughout so a
/// test never touches HKCU.
/// </para>
/// </summary>
public class UninstallCleanupTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), $"uninstallcleanup_{Guid.NewGuid():N}");
    private readonly string _steam;
    private readonly string _appData;

    public UninstallCleanupTests()
    {
        _steam = Path.Combine(_tmp, "Steam");
        _appData = Path.Combine(_tmp, "LuaToolsGui");
        Directory.CreateDirectory(_steam);
        Directory.CreateDirectory(_appData);
    }

    public void Dispose()
    {
        // The junction, if a test made one, must be severed with rmdir rather than a recursive delete.
        string marker = Path.Combine(_steam, UninstallCleanup.CdpMarkerName);
        if (Directory.Exists(marker)) TryRmdir(marker);
        try { if (Directory.Exists(_tmp)) Directory.Delete(_tmp, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static void TryRmdir(string path)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c rmdir \"{path}\"")
            { UseShellExecute = false, CreateNoWindow = true });
            p?.WaitForExit(5000);
        }
        catch { }
    }

    /// <summary>Create the marker the way the app does: an NTFS junction to a target that never exists.</summary>
    private static bool TryCreateBrokenJunction(string path)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(
                "cmd.exe", $"/c mklink /j \"{path}\" \"C:\\luatools\\target\\that\\never\\exists\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
            p?.WaitForExit(5000);
            return Directory.Exists(path);
        }
        catch { return false; }
    }

    // ── The CDP marker: the security-relevant one ────────────────────────────

    [Fact]
    public void RemovesCdpMarker_WhenItIsABrokenJunction()
    {
        string marker = Path.Combine(_steam, UninstallCleanup.CdpMarkerName);
        // Junction creation needs NTFS; on anything else there is nothing meaningful to assert.
        // (xUnit 2.x has no runtime skip, so this returns rather than reporting a false pass/fail.)
        if (!TryCreateBrokenJunction(marker)) return;

        bool removed = UninstallCleanup.TryRemoveCdpMarker(_steam);

        Assert.True(removed);
        Assert.False(Directory.Exists(marker));
    }

    [Fact]
    public void RemovesCdpMarker_WhenAnOlderBuildLeftAPlainFile()
    {
        // Builds before the junction fix wrote a plain file. Uninstall must still clear it.
        string marker = Path.Combine(_steam, UninstallCleanup.CdpMarkerName);
        File.WriteAllText(marker, "");

        Assert.True(UninstallCleanup.TryRemoveCdpMarker(_steam));
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public void CdpMarkerRemoval_IsIdempotentAndQuietWhenAbsent()
    {
        // Nothing to remove is a normal outcome (CDP was never consented to), not a failure.
        var failures = new List<string>();
        Assert.False(UninstallCleanup.TryRemoveCdpMarker(_steam, failures));
        Assert.Empty(failures);
    }

    // ── Loader DLLs ─────────────────────────────────────────────────────────

    [Fact]
    public void RemovesEveryKnownLoaderFile_IncludingAbandonedSlots()
    {
        foreach (string name in UninstallCleanup.LoaderFileNames)
            File.WriteAllText(Path.Combine(_steam, name), "stub");

        int removed = UninstallCleanup.RemoveLoaderFiles(_steam);

        Assert.Equal(UninstallCleanup.LoaderFileNames.Length, removed);
        foreach (string name in UninstallCleanup.LoaderFileNames)
            Assert.False(File.Exists(Path.Combine(_steam, name)));
    }

    [Fact]
    public void LeavesUnrelatedSteamFilesAlone()
    {
        // The Steam root is not ours to tidy — deleting anything beyond our own loader files would be
        // a far worse bug than leaving one behind.
        string bystander = Path.Combine(_steam, "steam.exe");
        File.WriteAllText(bystander, "not ours");
        File.WriteAllText(Path.Combine(_steam, "winmm.dll"), "ours");

        Assert.Equal(1, UninstallCleanup.RemoveLoaderFiles(_steam));
        Assert.True(File.Exists(bystander));
    }

    [Fact]
    public void LockedLoaderFile_IsReportedButDoesNotStopTheOthers()
    {
        // Steam still running is the common real-world case: one DLL is locked, the rest must still go.
        File.WriteAllText(Path.Combine(_steam, "winmm.dll"), "locked");
        File.WriteAllText(Path.Combine(_steam, "bcrypt.dll"), "free");

        var failures = new List<string>();
        using (File.Open(Path.Combine(_steam, "winmm.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            int removed = UninstallCleanup.RemoveLoaderFiles(_steam, failures);
            Assert.Equal(1, removed);                                  // bcrypt went
            Assert.Single(failures);                                   // winmm reported
            Assert.Contains("winmm.dll", failures[0]);
        }
        Assert.False(File.Exists(Path.Combine(_steam, "bcrypt.dll")));
    }

    // ── User data (auth token, API key, caches) ─────────────────────────────

    [Fact]
    public void RemovesUserDataDirectory_IncludingStoredCredentials()
    {
        File.WriteAllText(Path.Combine(_appData, "auth.dat"), "dpapi-blob");
        File.WriteAllText(Path.Combine(_appData, "settings.json"), "{}");
        Directory.CreateDirectory(Path.Combine(_appData, "covers"));

        Assert.True(UninstallCleanup.TryRemoveUserData(_appData));
        Assert.False(Directory.Exists(_appData));
    }

    // ── Whole-pass behaviour ────────────────────────────────────────────────

    [Fact]
    public void Run_ClearsSteamSideAndUserData_AndReportsWhatItDid()
    {
        File.WriteAllText(Path.Combine(_steam, "winmm.dll"), "x");
        File.WriteAllText(Path.Combine(_steam, "winmm_real.dll"), "x");
        File.WriteAllText(Path.Combine(_appData, "auth.dat"), "x");

        var report = UninstallCleanup.Run(_steam, _appData, unregisterProtocol: false);

        Assert.Equal(2, report.LoaderFilesRemoved);
        Assert.True(report.UserDataRemoved);
        Assert.Empty(report.Failures);
        Assert.False(Directory.Exists(_appData));
    }

    [Fact]
    public void Run_SurvivesAnUndetectableSteamInstall()
    {
        // Steam not installed, or the registry lookup failed → Steam-side steps are skipped, and the
        // rest of the uninstall still has to complete.
        var report = UninstallCleanup.Run(steamDir: null, appDataDir: _appData, unregisterProtocol: false);

        Assert.Equal(0, report.LoaderFilesRemoved);
        Assert.False(report.CdpMarkerRemoved);
        Assert.True(report.UserDataRemoved);
        Assert.Empty(report.Failures);
    }

    [Fact]
    public void Run_SurvivesAMissingSteamDirectory()
    {
        var report = UninstallCleanup.Run(
            Path.Combine(_tmp, "nope"), appDataDir: null, unregisterProtocol: false);

        Assert.Empty(report.Failures);
        Assert.False(report.UserDataRemoved);
    }

    [Fact]
    public void Run_IsSafeToRepeat()
    {
        File.WriteAllText(Path.Combine(_steam, "winmm.dll"), "x");

        var first = UninstallCleanup.Run(_steam, _appData, unregisterProtocol: false);
        var second = UninstallCleanup.Run(_steam, _appData, unregisterProtocol: false);

        Assert.Equal(1, first.LoaderFilesRemoved);
        Assert.Equal(0, second.LoaderFilesRemoved);
        Assert.Empty(second.Failures);
    }
}
