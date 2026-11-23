using AwesomeAssertions;
using LuaToolsGui.ViewModels;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Default Manage-page order: fully installed games (green badge) first, then A–Z by name inside each
/// group. <see cref="ManageViewModel.SortInstalledFirst"/> is the single place that rule lives; search,
/// refresh and add/remove all funnel through ApplyFilter → SortTiles → this.
/// </summary>
public class ManageViewModelTests
{
    private static LuaTileViewModel Tile(long id, string name, bool installed) =>
        new(id, $"{id}.lua", DateTime.UnixEpoch, name, nameIsPlaceholder: false) { IsInstalled = installed };

    private static string[] Names(IEnumerable<LuaTileViewModel> tiles) => [.. tiles.Select(t => t.Name)];

    [Fact]
    public void Installed_games_come_first_then_alphabetical_within_each_group()
    {
        LuaTileViewModel[] fake =
        [
            Tile(2, "Jogo B", installed: false),
            Tile(3, "Jogo C", installed: true),
            Tile(1, "Jogo A", installed: false),
            Tile(4, "Jogo D", installed: true),
        ];

        Names(ManageViewModel.SortInstalledFirst(fake))
            .Should().Equal("Jogo C", "Jogo D", "Jogo A", "Jogo B");
    }

    [Fact]
    public void Name_order_ignores_case()
    {
        LuaTileViewModel[] fake =
        [
            Tile(1, "banana", installed: true),
            Tile(2, "Apple", installed: true),
            Tile(3, "cherry", installed: true),
        ];

        Names(ManageViewModel.SortInstalledFirst(fake)).Should().Equal("Apple", "banana", "cherry");
    }

    [Fact]
    public void Search_result_keeps_the_same_order()
    {
        // ApplyFilter narrows first and sorts the survivors, so a filtered subset must still be installed-first.
        LuaTileViewModel[] all =
        [
            Tile(1, "Portal Z", installed: false),
            Tile(2, "Portal B", installed: true),
            Tile(3, "Doom", installed: true),
        ];

        Names(ManageViewModel.SortInstalledFirst(all.Where(t => t.Matches("portal"))))
            .Should().Equal("Portal B", "Portal Z");
    }

    [Fact]
    public void Flipping_a_game_to_installed_moves_it_up_on_the_next_sort()
    {
        var a = Tile(1, "A", installed: false);
        var b = Tile(2, "B", installed: false);
        Names(ManageViewModel.SortInstalledFirst([a, b])).Should().Equal("A", "B");

        b.IsInstalled = true; // what RefreshInstallStatesAsync does once the Steam scan lands

        Names(ManageViewModel.SortInstalledFirst([a, b])).Should().Equal("B", "A");
    }

    [Fact]
    public void Installed_first_is_offered_as_a_sort_option()
    {
        ManageViewModel.InstalledFirstOption.Should().Be("Installed first");
    }

    // ── Manage × Depots delete confirmation (#35) ────────────────────────────

    [Fact]
    public void Delete_confirmation_differentiates_games_with_and_without_stored_builds()
    {
        string plain = ManageViewModel.BuildDeleteConfirmBody("Portal", 400, hasStoredBuilds: false);
        string withBuilds = ManageViewModel.BuildDeleteConfirmBody("Portal", 400, hasStoredBuilds: true);

        plain.Should().Be(string.Format(Resources.Strings.Manage_Delete_Body, "Portal", 400));
        withBuilds.Should().Be(string.Format(Resources.Strings.Manage_Delete_BodyWithBuilds, "Portal", 400));
        withBuilds.Should().NotBe(plain);
        withBuilds.Should().Contain("Portal").And.Contain("400").And.Contain("Depots");
    }

    [Fact]
    public void Bulk_delete_confirmation_differentiates_when_any_selected_game_has_stored_builds()
    {
        string plain = ManageViewModel.BuildDeleteManyConfirmBody(3, withStoredBuildsCount: 0);
        string withBuilds = ManageViewModel.BuildDeleteManyConfirmBody(3, withStoredBuildsCount: 2);

        plain.Should().Be(string.Format(Resources.Strings.Manage_DeleteMany_Body, 3));
        withBuilds.Should().Be(string.Format(Resources.Strings.Manage_DeleteMany_BodyWithBuilds, 3, 2));
        withBuilds.Should().NotBe(plain);
        withBuilds.Should().Contain("3").And.Contain("2").And.Contain("Depots");
    }

    [Fact]
    public void LuaVault_HasStoredBuilds_detects_vaulted_variants_and_loose_build_files()
    {
        string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"luavault_k2_{Guid.NewGuid():N}");
        string stPlugIn = System.IO.Path.Combine(tmp, "stplug-in");
        string vaultRoot = System.IO.Path.Combine(tmp, "vault");
        System.IO.Directory.CreateDirectory(stPlugIn);
        System.IO.Directory.CreateDirectory(vaultRoot);

        try
        {
            var vault = new LuaToolsGui.Services.LuaVault(() => stPlugIn, vaultRoot);
            const long appId = 400;

            // Only a live <appid>.lua exists -> no stored builds in vault or loose build files.
            string livePath = System.IO.Path.Combine(stPlugIn, $"{appId}.lua");
            System.IO.File.WriteAllText(livePath, "addappid(400)\n");
            vault.HasStoredBuilds(appId).Should().BeFalse();

            // Capture into the vault -> now has stored builds.
            vault.Capture(appId, livePath, LuaToolsGui.Services.LuaVariantKind.Build, buildId: "123456");
            vault.HasStoredBuilds(appId).Should().BeTrue();

            // Forgetting clears the vault -> back to false.
            vault.ForgetGame(appId);
            vault.HasStoredBuilds(appId).Should().BeFalse();

            // A loose <appid>_<buildid>.lua in stplug-in also counts as a stored build.
            System.IO.File.WriteAllText(System.IO.Path.Combine(stPlugIn, $"{appId}_999999.lua"), "addappid(400)\n");
            vault.HasStoredBuilds(appId).Should().BeTrue();
        }
        finally
        {
            try { System.IO.Directory.Delete(tmp, recursive: true); } catch (System.IO.IOException) { }
        }
    }
}
