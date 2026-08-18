using System.IO;
using AwesomeAssertions;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Covers taking a game off the Depots list for good.
///
/// <para>
/// The bug this exists for: the list is the UNION of three independent on-disk sources — the live
/// <c>&lt;appid&gt;.lua</c>, loose <c>&lt;appid&gt;_&lt;buildid&gt;.lua</c> files, and the vault folder of
/// captured variants. Clearing any one of them leaves the game on screen, which is why deleting a game on
/// the Manage page did not remove it here: the vault survived and put it straight back. Every test below
/// asserts on all three, because passing with two of them purged is exactly the failure that shipped.
/// </para>
///
/// <para>
/// Persistence is the filesystem, so "survives a restart" is tested the honest way: build a SECOND vault
/// over the same directories and ask it what it can see.
/// </para>
/// </summary>
public class GameRemovalTests : IDisposable
{
    private const long AppId = 386940;
    private const long OtherAppId = 271590;

    private readonly string _tmp = Path.Combine(Path.GetTempPath(), $"luaremovetest_{Guid.NewGuid():N}");
    private readonly string _plugIn;
    private readonly string _vaultRoot;
    private readonly LuaVault _vault;

    private const string Lua = """
        addappid(386940)
        addappid(228983,0,"aabb")
        """;

    public GameRemovalTests()
    {
        _plugIn = Path.Combine(_tmp, "stplug-in");
        _vaultRoot = Path.Combine(_tmp, "luavault");
        Directory.CreateDirectory(_plugIn);
        _vault = new LuaVault(() => _plugIn, _vaultRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string LivePath(long appId) => Path.Combine(_plugIn, $"{appId}.lua");
    private string LoosePath(long appId, string buildId) => Path.Combine(_plugIn, $"{appId}_{buildId}.lua");
    private string VaultDir(long appId) => Path.Combine(_vaultRoot, appId.ToString());

    /// <summary>A game present in all three sources — the state an accidental add actually leaves.</summary>
    private void SeedFullyPresent(long appId)
    {
        File.WriteAllText(LivePath(appId), Lua);
        File.WriteAllText(LoosePath(appId, "18234567"), Lua);
        _vault.SyncDefaultFromLive(appId);   // captures the live bytes into the vault
        _vault.AdoptLooseBuildLuas(appId);   // captures the loose build too

        Directory.Exists(VaultDir(appId)).Should().BeTrue("the fixture must actually seed a vault");
    }

    // ── Removing a game that is there ─────────────────────────────────────────

    [Fact]
    public void Removing_a_game_purges_all_three_sources()
    {
        SeedFullyPresent(AppId);

        var result = _vault.ForgetGame(AppId);

        result.Should().BeOfType<GameRemoval.Removed>();
        var removed = (GameRemoval.Removed)result;
        removed.Live.Should().BeTrue();
        removed.LooseBuilds.Should().Be(1);
        removed.Vault.Should().BeTrue();

        File.Exists(LivePath(AppId)).Should().BeFalse();
        File.Exists(LoosePath(AppId, "18234567")).Should().BeFalse();
        Directory.Exists(VaultDir(AppId)).Should().BeFalse();
    }

    [Fact]
    public void The_game_stops_appearing_in_every_list_the_page_is_built_from()
    {
        // The three reads BuildsViewModel.LoadCoreAsync unions. Asserting on the files alone would pass
        // even if one of these kept reporting the game.
        SeedFullyPresent(AppId);

        _vault.ForgetGame(AppId);

        _vault.AppsWithVariants().Should().NotContain(AppId);
        _vault.EnumerateLooseBuildLuas().Select(l => l.AppId).Should().NotContain(AppId);
        _vault.HasVariants(AppId).Should().BeFalse();
        _vault.GetActiveHash(AppId).Should().BeNull();
    }

    [Fact]
    public void Only_the_named_game_is_touched()
    {
        SeedFullyPresent(AppId);
        SeedFullyPresent(OtherAppId);

        _vault.ForgetGame(AppId);

        File.Exists(LivePath(OtherAppId)).Should().BeTrue();
        File.Exists(LoosePath(OtherAppId, "18234567")).Should().BeTrue();
        Directory.Exists(VaultDir(OtherAppId)).Should().BeTrue();
        _vault.AppsWithVariants().Should().Contain(OtherAppId);
    }

    [Fact]
    public void A_game_present_in_only_one_source_is_still_removed()
    {
        // Vault-only: the live lua was deleted on the Manage page, which is exactly how a game ends up
        // lingering on the Depots list with nothing else to explain it.
        File.WriteAllText(LivePath(AppId), Lua);
        _vault.SyncDefaultFromLive(AppId);
        File.Delete(LivePath(AppId));

        var result = _vault.ForgetGame(AppId);

        result.Should().BeOfType<GameRemoval.Removed>();
        ((GameRemoval.Removed)result).Live.Should().BeFalse();
        ((GameRemoval.Removed)result).Vault.Should().BeTrue();
        _vault.AppsWithVariants().Should().NotContain(AppId);
    }

    [Fact]
    public void Every_stored_build_goes_not_just_the_inactive_ones()
    {
        // Single-variant Delete refuses to remove the one Steam is using, on purpose. Removing the whole
        // game has no such reservation — there is no live lua left for it to orphan.
        File.WriteAllText(LivePath(AppId), Lua);
        _vault.SyncDefaultFromLive(AppId);
        string activeHash = _vault.GetActiveHash(AppId)!;

        _vault.Delete(AppId, activeHash).Should().BeFalse("the active variant is protected");

        _vault.ForgetGame(AppId).Should().BeOfType<GameRemoval.Removed>();
        _vault.GetVariants(AppId).Should().BeEmpty();
    }

    // ── Removing a game that is not there ─────────────────────────────────────

    [Fact]
    public void Removing_a_game_that_does_not_exist_reports_it_rather_than_throwing()
    {
        var result = _vault.ForgetGame(999999);

        result.Should().BeOfType<GameRemoval.NothingToRemove>(
            "a stale row is not an error, but it must not be reported as a removal either");
    }

    [Fact]
    public void Removing_twice_is_safe()
    {
        SeedFullyPresent(AppId);

        _vault.ForgetGame(AppId).Should().BeOfType<GameRemoval.Removed>();
        _vault.ForgetGame(AppId).Should().BeOfType<GameRemoval.NothingToRemove>();
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    [Fact]
    public void The_removal_survives_a_restart()
    {
        SeedFullyPresent(AppId);
        _vault.ForgetGame(AppId);

        // A fresh vault over the same directories — the closest thing to relaunching the app.
        var reopened = new LuaVault(() => _plugIn, _vaultRoot);

        reopened.AppsWithVariants().Should().NotContain(AppId);
        reopened.HasVariants(AppId).Should().BeFalse();
        reopened.GetVariants(AppId).Should().BeEmpty();
        reopened.EnumerateLooseBuildLuas().Select(l => l.AppId).Should().NotContain(AppId);
    }

    // ── Notification ──────────────────────────────────────────────────────────

    [Fact]
    public void A_removal_announces_itself_so_the_page_can_refresh()
    {
        SeedFullyPresent(AppId);
        var seen = new List<long>();
        _vault.VaultChanged += seen.Add;

        _vault.ForgetGame(AppId);

        seen.Should().Contain(AppId);
    }

    [Fact]
    public void A_no_op_removal_does_not_announce_a_change()
    {
        var seen = new List<long>();
        _vault.VaultChanged += seen.Add;

        _vault.ForgetGame(999999);

        seen.Should().BeEmpty("nothing changed, so nothing should redraw");
    }
}

/// <summary>
/// Pins the affordance itself. The service tests above prove the removal works; nothing there would
/// notice if the button that reaches it were dropped from the row template, and the command would then be
/// unreachable while every test still passed.
///
/// <para>
/// A static read of the markup rather than a rendered control, matching <see cref="ViewParseTests"/>:
/// constructing the Builds page needs eight services, and what is worth pinning here is which command the
/// row is wired to.
/// </para>
/// </summary>
public class BuildsRemoveGameMarkupTests
{
    private static string BuildsViewXaml()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LuaToolsGui.sln")))
            dir = dir.Parent;
        string root = dir?.FullName
            ?? throw new DirectoryNotFoundException("LuaToolsGui.sln not found above the test output.");
        return File.ReadAllText(Path.Combine(root, "src", "LuaToolsGui", "Views", "BuildsView.xaml"));
    }

    [Fact]
    public void Every_game_row_offers_the_removal()
    {
        string xaml = BuildsViewXaml();

        xaml.Should().Contain("DataContext.RemoveGameCommand",
            "the command lives on the page's view-model, not on the row");
        xaml.Should().Contain("CommandParameter=\"{Binding}\"",
            "the row's own game is what gets removed");
    }

    [Fact]
    public void The_removal_is_reachable_without_a_mouse()
    {
        // The button is held at 0 opacity until the row is hovered. Focus has to reveal it as well, or it
        // is tab-reachable and invisible at the same time — worse than not being focusable at all.
        BuildsViewXaml().Should().Contain("IsKeyboardFocusWithin");
    }
}
