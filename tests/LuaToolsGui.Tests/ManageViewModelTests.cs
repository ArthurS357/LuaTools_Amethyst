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
}
