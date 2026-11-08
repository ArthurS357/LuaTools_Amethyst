using System.Globalization;
using System.IO;
using AwesomeAssertions;
using LuaToolsGui.Models;
using LuaToolsGui.Resources;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>The Fixes tab lists the whole catalog, installed games first, and warns about installed games it lacks.</summary>
public sealed class InstalledFixesTests
{
    // The dev machine runs pt-BR: pin the UI culture the assertions assume.
    public InstalledFixesTests() => CultureInfo.CurrentUICulture = new CultureInfo("en");

    private static DenuvoGameListing G(long id, string name) => new() { AppId = id.ToString(), Name = name, FixCount = 1 };
    private static InstalledGame I(long id, string name = "") => new(id, name);
    private static readonly HashSet<long> Nothing = [];

    // ── JoinCatalogWithInstalled ─────────────────────────────────────

    [Fact]
    public void Mixed_catalog_puts_installed_first_then_alphabetical_within_each_group()
    {
        var catalog = new[] { G(1, "delta"), G(2, "Bravo"), G(3, "alpha"), G(4, "Charlie") };

        var joined = InstalledFixes.JoinCatalogWithInstalled(catalog, new HashSet<long> { 4, 2 });

        joined.Select(c => c.Name).Should().Equal("Bravo", "Charlie", "alpha", "delta");
        joined.Select(c => c.IsInstalled).Should().Equal(true, true, false, false);
    }

    [Fact]
    public void Only_installed_games_are_all_flagged_and_sorted_by_name()
    {
        var joined = InstalledFixes.JoinCatalogWithInstalled([G(2, "B"), G(1, "A")], new HashSet<long> { 1, 2 });
        joined.Select(c => (c.Name, c.IsInstalled)).Should().Equal(("A", true), ("B", true));
    }

    [Fact]
    public void Only_uninstalled_games_keep_the_whole_catalog_unflagged()
    {
        var joined = InstalledFixes.JoinCatalogWithInstalled([G(2, "B"), G(1, "A")], Nothing);
        joined.Select(c => (c.Name, c.IsInstalled)).Should().Equal(("A", false), ("B", false));
    }

    [Fact]
    public void An_empty_catalog_joins_to_nothing() =>
        InstalledFixes.JoinCatalogWithInstalled([], new HashSet<long> { 1 }).Should().BeEmpty();

    [Fact]
    public void A_non_numeric_catalog_appid_stays_listed_but_never_installed()
    {
        var bad = new DenuvoGameListing { AppId = "abc", Name = "Bad" };
        InstalledFixes.JoinCatalogWithInstalled([bad], new HashSet<long> { 1 })
            .Should().ContainSingle().Which.IsInstalled.Should().BeFalse();
    }

    // ── FindInstalledWithoutFix ──────────────────────────────────────

    [Fact]
    public void An_installed_game_missing_from_the_catalog_is_reported_and_one_with_a_fix_is_not()
    {
        var without = InstalledFixes.FindInstalledWithoutFix([I(1, "Has"), I(2, "zeta"), I(3, "Alpha")], new HashSet<long> { 1 });
        without.Select(g => g.AppId).Should().Equal(3L, 2L); // by name, A before z
    }

    [Fact]
    public void No_installed_games_reports_nothing() =>
        InstalledFixes.FindInstalledWithoutFix([], new HashSet<long> { 1 }).Should().BeEmpty();

    // ── The four-way matrix ──────────────────────────────────────────

    [Fact]
    public void Catalog_ok_and_library_ok_shows_list_badges_and_the_banner_when_something_is_missing()
    {
        var s = InstalledFixes.Resolve(catalogFailed: false, catalogCount: 5, libraryFailed: false, installedWithoutFixCount: 2);
        s.Should().Be(new FixesViewState(FixesEmptyState.None, ShowBadges: true, ShowBanner: true, ShowLibraryWarning: false));
    }

    [Fact]
    public void Catalog_ok_and_library_ok_with_every_installed_game_covered_has_no_banner()
    {
        var s = InstalledFixes.Resolve(false, 5, false, 0);
        s.ShowBadges.Should().BeTrue();
        s.ShowBanner.Should().BeFalse();
    }

    [Fact]
    public void Catalog_ok_and_library_failed_keeps_the_list_without_badges_or_banner_and_warns()
    {
        var s = InstalledFixes.Resolve(false, 5, libraryFailed: true, installedWithoutFixCount: 3);
        s.Should().Be(new FixesViewState(FixesEmptyState.None, ShowBadges: false, ShowBanner: false, ShowLibraryWarning: true));
        Strings.Fixes_Warn_LibraryFailed.Should().Contain("Steam library");
    }

    [Fact]
    public void An_empty_catalog_that_loaded_fine_is_the_existing_empty_message()
    {
        var s = InstalledFixes.Resolve(false, 0, false, 4);
        s.Should().Be(new FixesViewState(FixesEmptyState.NoCatalog, false, false, false));
        InstalledFixes.EmptyMessage(s.Empty).Should().Be(Strings.Fixes_Empty_None);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 3)]
    [InlineData(false, 5)]
    public void A_failed_catalog_suppresses_everything_whatever_the_library_says(bool libraryFailed, int withoutFix)
    {
        var s = InstalledFixes.Resolve(catalogFailed: true, catalogCount: 5, libraryFailed, withoutFix);

        s.Should().Be(new FixesViewState(FixesEmptyState.LoadFailed, false, false, false));
        InstalledFixes.EmptyMessage(s.Empty).Should().Be(Strings.Fixes_Err_Load);
    }

    [Fact]
    public void The_banner_text_carries_the_count()
    {
        string.Format(Strings.Fixes_Warn_InstalledWithoutFix, 7).Should().StartWith("7 of your installed games");
    }

    // ── Load exclusion (the VM cannot be built here: its API client is hard-wired to lua.tools) ──

    [Fact]
    public void LoadAsync_runs_one_at_a_time_behind_a_semaphore_and_always_releases_it()
    {
        string src = File.ReadAllText(Path.Combine(Root(), "src/LuaToolsGui/ViewModels/FixesViewModel.cs"));

        src.Should().Contain("SemaphoreSlim _loadGate = new(1, 1)");
        src.Should().Contain("_loadGate.WaitAsync(ct)");
        src.Should().Contain("_loadGate.Release()");
        src.IndexOf("_loadGate.Release()", StringComparison.Ordinal)
            .Should().BeGreaterThan(src.IndexOf("finally", src.IndexOf("public async Task LoadAsync", StringComparison.Ordinal), StringComparison.Ordinal));
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LuaToolsGui.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("solution root not found");
    }
}
