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
        // The junction, if a test made one, must be severed rather than recursively deleted.
        string marker = Path.Combine(_steam, UninstallCleanup.CdpMarkerName);
        if (Directory.Exists(marker)) DirectoryJunction.Remove(marker);
        try { if (Directory.Exists(_tmp)) Directory.Delete(_tmp, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    /// <summary>Create the marker the way the app does: an NTFS junction to a target that never exists.</summary>
    private static bool TryCreateBrokenJunction(string path) =>
        DirectoryJunction.Create(path, @"C:\luatools\target\that\never\exists");

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

    [Fact]
    public void A_user_data_folder_that_is_a_junction_loses_only_the_link_and_reports_the_target_left_behind()
    {
        string target = Path.Combine(_tmp, "MovedData");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "auth.dat"), "elsewhere");
        string link = Path.Combine(_tmp, "LuaToolsAmethystLinked");
        Assert.True(DirectoryJunction.Create(link, target));
        Assert.False(UninstallCleanup.IsSafeUserDataDir(link));   // the public check still refuses a link

        var failures = new List<string>();

        // Not counted as removed: the sign-in data is still at the target, and the report says so.
        Assert.False(UninstallCleanup.TryRemoveUserData(link, failures));

        Assert.False(Directory.Exists(link));
        Assert.True(File.Exists(Path.Combine(target, "auth.dat")));
        Assert.Contains(failures, f => f.Contains("only the link was removed", StringComparison.Ordinal));
    }

    [Fact]
    public void A_junction_inside_the_user_data_folder_is_detached_without_deleting_its_target()
    {
        string outside = Path.Combine(_tmp, "Outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "not ours");
        File.WriteAllText(Path.Combine(_appData, "auth.dat"), "dpapi-blob");
        Assert.True(DirectoryJunction.Create(Path.Combine(_appData, "covers"), outside));

        Assert.True(UninstallCleanup.TryRemoveUserData(_appData));

        Assert.False(Directory.Exists(_appData));
        Assert.True(File.Exists(Path.Combine(outside, "keep.txt")));
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

    // ── Side-by-side & invalid path protections (Lote K1) ───────────────────

    [Fact]
    public void TryRemoveUserData_RefusesProtectedSideBySideDirectories()
    {
        string sideBySideLegacy = Path.Combine(_tmp, "SideBySide_LuaToolsGui");
        Directory.CreateDirectory(sideBySideLegacy);
        string authFile = Path.Combine(sideBySideLegacy, "auth.dat");
        File.WriteAllText(authFile, "upstream-token");

        var failures = new List<string>();
        bool removed = UninstallCleanup.TryRemoveUserData(
            sideBySideLegacy,
            failures,
            protectedSideBySideDirs: [sideBySideLegacy]);

        Assert.False(removed);
        Assert.Single(failures);
        Assert.True(Directory.Exists(sideBySideLegacy));
        Assert.True(File.Exists(authFile));

        // Also refuses the real AppDataMigrator.LegacyDirectory (%AppData%\LuaToolsGui) by default.
        Assert.False(UninstallCleanup.IsSafeUserDataDir(AppDataMigrator.LegacyDirectory));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relative\\path")]
    [InlineData(@"C:\")]
    [InlineData(@"C:\Users\..\Windows")]
    public void PathGuards_RejectInvalidRootOrTraversalPaths(string? invalidPath)
    {
        Assert.False(UninstallCleanup.IsSafeUserDataDir(invalidPath));
        Assert.False(UninstallCleanup.IsSafeSteamDir(invalidPath));
    }

    [Fact]
    public void PathGuards_RejectSystemRootsAndSteamDirectoryAsUserData()
    {
        string appDataRoot = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string localAppDataRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string tempRoot = Path.GetTempPath();

        Assert.False(UninstallCleanup.IsSafeUserDataDir(appDataRoot));
        Assert.False(UninstallCleanup.IsSafeUserDataDir(localAppDataRoot));
        Assert.False(UninstallCleanup.IsSafeUserDataDir(tempRoot));
        Assert.False(UninstallCleanup.IsSafeSteamDir(appDataRoot));
        Assert.False(UninstallCleanup.IsSafeSteamDir(tempRoot));

        // A folder containing steam.exe must never be accepted as appDataDir.
        File.WriteAllText(Path.Combine(_steam, "steam.exe"), "stub");
        Assert.False(UninstallCleanup.IsSafeUserDataDir(_steam));
        Assert.False(UninstallCleanup.TryRemoveUserData(_steam));
        Assert.True(File.Exists(Path.Combine(_steam, "steam.exe")));

        // steamDir equal to appDataDir is rejected.
        Assert.False(UninstallCleanup.IsSafeSteamDir(_appData, _appData));
    }

    [Fact]
    public void IsProtocolOwnedByInstall_DistinguishesCurrentFromSideBySideInstall()
    {
        const string amethystCurrent = @"C:\Users\Alice\AppData\Local\LuaToolsAmethyst\current\LuaTools.exe";
        const string amethystVersioned = @"C:\Users\Alice\AppData\Local\LuaToolsAmethyst\app-1.8.4\LuaTools.exe";
        const string amethystUpdate = @"C:\Users\Alice\AppData\Local\LuaToolsAmethyst\Update.exe";
        const string upstreamLegacy = @"C:\Users\Alice\AppData\Local\LuaTools\current\LuaTools.exe";
        const string otherPortable = @"D:\Tools\OtherLuaTools\LuaTools.exe";

        string amethystCmd = $"\"{amethystCurrent}\" \"%1\"";
        string upstreamCmd = $"\"{upstreamLegacy}\" \"%1\"";
        string portableCmd = $"\"{otherPortable}\" \"%1\"";

        // Owned by this install (same exe or same Velopack LuaToolsAmethyst root).
        Assert.True(UninstallCleanup.IsProtocolOwnedByInstall(amethystCmd, amethystCurrent));
        Assert.True(UninstallCleanup.IsProtocolOwnedByInstall(amethystCmd, amethystVersioned));
        Assert.True(UninstallCleanup.IsProtocolOwnedByInstall(amethystCmd, amethystUpdate));

        // Owned by a side-by-side upstream or portable install -> must NOT unregister.
        Assert.False(UninstallCleanup.IsProtocolOwnedByInstall(upstreamCmd, amethystCurrent));
        Assert.False(UninstallCleanup.IsProtocolOwnedByInstall(portableCmd, amethystCurrent));
        Assert.False(UninstallCleanup.IsProtocolOwnedByInstall(amethystCmd, null));
    }

    [Fact]
    public void An_unquoted_command_with_spaces_is_read_up_to_the_exe_not_the_first_space()
    {
        const string ours = @"C:\Users\Ana Maria\AppData\Local\LuaToolsAmethyst\current\LuaTools.exe";

        Assert.True(UninstallCleanup.IsProtocolOwnedByInstall($"{ours} %1", ours));
        Assert.True(UninstallCleanup.IsProtocolOwnedByInstall(ours, ours));
        Assert.False(UninstallCleanup.IsProtocolOwnedByInstall(@"C:\Program Files\Other Tool\other.exe %1", ours));
        Assert.False(UninstallCleanup.IsProtocolOwnedByInstall(@"C:\Users\Ana Maria\AppData\Local\LuaTools\current\LuaTools.exe %1", ours));
    }

    [Fact]
    public void TryUnregisterProtocol_PreservesKeyWhenOwnedBySideBySideInstall_AndRemovesWhenOwned()
    {
        string testSubKey = $@"Software\LuaToolsAmethyst_Test_{Guid.NewGuid():N}";
        const string amethystExe = @"C:\Users\Alice\AppData\Local\LuaToolsAmethyst\current\LuaTools.exe";
        const string upstreamExe = @"C:\Users\Alice\AppData\Local\LuaTools\current\LuaTools.exe";

        try
        {
            using (var cmdKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"{testSubKey}\luatools\shell\open\command"))
            {
                cmdKey.SetValue(null, $"\"{upstreamExe}\" \"%1\"");
            }

            // 1. Owned by upstream side-by-side install -> preserved.
            var failures = new List<string>();
            bool removedSideBySide = UninstallCleanup.TryUnregisterProtocol(failures, amethystExe, testSubKey);
            Assert.False(removedSideBySide);
            Assert.Empty(failures);
            using (var check = Microsoft.Win32.Registry.CurrentUser.OpenSubKey($@"{testSubKey}\luatools"))
            {
                Assert.NotNull(check);
            }

            // 2. Owned by current LuaToolsAmethyst install -> removed.
            using (var cmdKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"{testSubKey}\luatools\shell\open\command"))
            {
                cmdKey.SetValue(null, $"\"{amethystExe}\" \"%1\"");
            }

            bool removedOwned = UninstallCleanup.TryUnregisterProtocol(failures, amethystExe, testSubKey);
            Assert.True(removedOwned);
            Assert.Empty(failures);
            using (var checkAfter = Microsoft.Win32.Registry.CurrentUser.OpenSubKey($@"{testSubKey}\luatools"))
            {
                Assert.Null(checkAfter);
            }
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(testSubKey, throwOnMissingSubKey: false);
        }
    }
}
