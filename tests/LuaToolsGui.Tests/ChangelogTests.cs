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
        AppVersion.Current.Should().Be("1.7.0");
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

        // Deliberately NOT a hardcoded date. Pinning one here means every bump has to remember to edit a
        // test that is not about the date, and it passes or fails for a reason unrelated to what this test
        // is checking — 1.6.2 only stayed green because it happened to ship the same day as 1.6.1. Shape
        // is covered by Every_entry_is_complete_enough_to_render; ordering by Release_dates_never_go_backwards.
        current.Summary.Should().NotBeNullOrWhiteSpace();
        current.Highlights.Should().HaveCountGreaterThanOrEqualTo(3);
        current.Highlights.Should().AllSatisfy(h => h.Should().NotBeNullOrWhiteSpace());
    }

    [Fact]
    public void The_current_entry_names_the_changes_the_release_was_about()
    {
        string text = string.Join(" ", Changelog.Entries[0].Highlights);

        // One anchor per change the release was actually about, so an entry that got copied forward from
        // the previous version fails here rather than shipping a changelog describing the wrong release.
        text.Should().ContainEquivalentOf("Downloads page"); // the new page and the queue behind it
        text.Should().ContainEquivalentOf("Resume");         // pause/resume, depot downloads only
        text.Should().ContainEquivalentOf("history");        // persisted, with per-row and bulk clearing
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
    public void Release_dates_never_go_backwards()
    {
        // Replaces the hardcoded date that used to sit in the current-release test. This checks the thing
        // that would actually be wrong — a new entry dated before the one it supersedes — instead of
        // needing an edit on every bump. Equal dates are allowed: two releases can ship the same day.
        var dates = Changelog.Entries
            .Select(e => DateOnly.ParseExact(e.Released, "yyyy-MM-dd"))
            .ToList();

        dates.Should().BeInDescendingOrder();
    }

    [Fact]
    public void Versions_are_not_repeated()
    {
        Changelog.Entries.Select(e => e.Version).Should().OnlyHaveUniqueItems();
    }
}
