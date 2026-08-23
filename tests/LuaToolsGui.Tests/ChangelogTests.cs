using AwesomeAssertions;
using LuaToolsGui;
using LuaToolsGui.Resources;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Keeps the in-app changelog honest about the build it ships in.
///
/// <para>
/// The csproj <c>&lt;Version&gt;</c> has drifted before — it sat at 1.1.3 through the whole 1.2.8
/// release, so the footer told every user the wrong version. A changelog is a second thing that can say
/// something untrue about the running build, and it is worse than no changelog when it does.
/// </para>
/// </summary>
public class ChangelogTests
{
    [Fact]
    public void The_assembly_reports_the_version_this_release_claims()
    {
        AppVersion.Current.Should().Be("1.5.4");
    }

    [Fact]
    public void The_newest_entry_is_the_version_actually_running()
    {
        // The check that catches a bump made in the csproj but not here, or the reverse.
        Changelog.Entries.Should().NotBeEmpty();
        Changelog.Entries[0].Version.Should().Be(AppVersion.Current);
    }

    [Fact]
    public void The_release_this_build_is_lists_what_changed_in_it()
    {
        var current = Changelog.Entries[0];

        current.Released.Should().Be("2026-08-22");
        current.Summary.Should().NotBeNullOrWhiteSpace();
        current.Highlights.Should().HaveCountGreaterThanOrEqualTo(3);
        current.Highlights.Should().AllSatisfy(h => h.Should().NotBeNullOrWhiteSpace());
    }

    [Fact]
    public void The_current_entry_names_the_changes_the_release_was_about()
    {
        string text = string.Join(" ", Changelog.Entries[0].Highlights);

        text.Should().ContainEquivalentOf("Play button");
        text.Should().ContainEquivalentOf("Steam's download");
        text.Should().ContainEquivalentOf(".NET 10");
    }

    [Fact]
    public void Entries_run_newest_first()
    {
        // The About page renders them in order and does no sorting of its own.
        var versions = Changelog.Entries.Select(e => Version.Parse(e.Version)).ToList();

        versions.Should().BeInDescendingOrder();
    }

    [Fact]
    public void Every_entry_is_complete_enough_to_render()
    {
        // A blank field would show as an empty row rather than as an error, which is the kind of defect
        // that survives review precisely because nothing throws.
        Changelog.Entries.Should().AllSatisfy(e =>
        {
            e.Version.Should().MatchRegex(@"^\d+\.\d+\.\d+$");
            e.Released.Should().MatchRegex(@"^\d{4}-\d{2}-\d{2}$");
            e.Summary.Should().NotBeNullOrWhiteSpace();
            e.Highlights.Should().NotBeEmpty();
        });
    }

    [Fact]
    public void Versions_are_not_repeated()
    {
        Changelog.Entries.Select(e => e.Version).Should().OnlyHaveUniqueItems();
    }
}
