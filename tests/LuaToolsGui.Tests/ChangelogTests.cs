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
        AppVersion.Current.Should().Be("1.8.5");
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
    public void The_1_8_3_entry_still_names_what_that_release_was_about()
    {
        string text = string.Join(" ", Changelog.Entries.Single(e => e.Version == "1.8.3").Highlights);

        text.Should().ContainEquivalentOf("DPAPI");                          // fail-closed Hubcap key storage
        text.Should().ContainEquivalentOf("memory for the session only");    // never written to settings.json
        text.Should().ContainEquivalentOf("%USERPROFILE%");                  // profile masking in logs
        text.Should().ContainEquivalentOf("DNS fallback");                   // Auto clears the failure flag
    }

    [Fact]
    public void The_1_8_5_entry_names_what_that_release_was_about()
    {
        string text = string.Join(" ", Changelog.Entries.Single(e => e.Version == "1.8.5").Highlights);

        text.Should().ContainEquivalentOf("refused");                        // LaunchModStore fails closed on I/O
        text.Should().ContainEquivalentOf("junction");                       // uninstall severs the link only
        text.Should().ContainEquivalentOf("five timestamped backups");       // .bak.<ts> cap
    }

    [Fact]
    public void The_1_8_4_entry_names_what_that_release_was_about()
    {
        string text = string.Join(" ", Changelog.Entries.Single(e => e.Version == "1.8.4").Highlights);

        text.Should().ContainEquivalentOf("background thread");              // GetCachedAsync / PreloadAsync
        text.Should().ContainEquivalentOf("app-list cache");                 // SteamAppListCache end to end
        text.Should().ContainEquivalentOf("nothing to migrate");             // no data-folder change
    }

    [Fact]
    public void The_1_8_2_entry_still_names_what_that_release_was_about()
    {
        string text = string.Join(" ", Changelog.Entries.Single(e => e.Version == "1.8.2").Highlights);

        // One anchor per change the release was actually about, so an entry that got copied forward from
        // the previous version fails here rather than shipping a changelog describing the wrong release.
        text.Should().ContainEquivalentOf("LuaTools-Catalog");               // the independent mirror
        text.Should().ContainEquivalentOf("saved offline copy");             // GitHub, lua.tools, then disk
        text.Should().ContainEquivalentOf("Orphan fixes");                   // fixes only the mirror lists
        text.Should().ContainEquivalentOf("SHA-256 matches");                // fail-closed digest
        text.Should().ContainEquivalentOf("hop by hop");                     // redirect validation
        text.Should().ContainEquivalentOf("store.steampowered.com");         // bridge host restriction
        text.Should().ContainEquivalentOf("no longer keep cookies");         // useCookies = false
    }

    [Fact]
    public void The_1_8_1_entry_still_names_what_that_release_was_about()
    {
        string text = string.Join(" ", Changelog.Entries.Single(e => e.Version == "1.8.1").Highlights);

        text.Should().ContainEquivalentOf("Settings > Network");             // where the selector lives
        text.Should().ContainEquivalentOf("Auto (default");                  // the three modes, default first
        text.Should().ContainEquivalentOf("applies immediately");            // no restart
        text.Should().ContainEquivalentOf("Offline mode banner");            // the offline Fixes catalog
        text.Should().ContainEquivalentOf("up to 24 hours");                 // its TTL
        text.Should().ContainEquivalentOf("no new network request");         // the selector adds none
    }

    [Fact]
    public void The_1_8_0_entry_still_names_what_that_release_was_about()
    {
        string text = string.Join(" ", Changelog.Entries.Single(e => e.Version == "1.8.0").Highlights);

        text.Should().ContainEquivalentOf("engine v1.5.3");                  // the engine
        text.Should().ContainEquivalentOf("LuaToolsAmethyst");               // the new data folder
        text.Should().ContainEquivalentOf("never moved");                    // copy, legacy folder stays
        text.Should().ContainEquivalentOf("\"DonateKeys\": false");          // the legacy vaccine
        text.Should().ContainEquivalentOf("Cloudflare DNS-over-HTTPS");      // the new network request
        text.Should().ContainEquivalentOf("until you close the app");        // DoH state is session-wide
        text.Should().ContainEquivalentOf("definitive fallback");            // the button text
        text.Should().ContainEquivalentOf("set in settings.json");           // DnsMode had no UI in 1.8.0
        text.Should().NotContainEquivalentOf("only editable");               // and the claim is no longer made
    }

    [Fact]
    public void The_1_7_13_5_entry_still_names_what_that_release_was_about()
    {
        string text = string.Join(" ", Changelog.Entries.Single(e => e.Version == "1.7.13.5").Highlights);

        text.Should().ContainEquivalentOf("Clear content cache");            // the appinfo rescue
        text.Should().ContainEquivalentOf("backup folder");                  // backed up, never just deleted
        text.Should().ContainEquivalentOf("pattern_migrate.exe");            // the CLIs are kept
        text.Should().ContainEquivalentOf("[manifest] failover");            // the new config keys
        text.Should().ContainEquivalentOf("every redirect hop");             // F-09
    }

    [Fact]
    public void The_1_7_13_3_entry_still_names_what_that_release_was_about()
    {
        string text = string.Join(" ", Changelog.Entries.Single(e => e.Version == "1.7.13.3").Highlights);

        text.Should().ContainEquivalentOf("full LuaTools catalog");          // the whole catalog
        text.Should().ContainEquivalentOf("Installed badge");                // the badge
        text.Should().ContainEquivalentOf("installed games sort first");     // the order
        text.Should().ContainEquivalentOf("have no fix");                    // the banner
        text.Should().ContainEquivalentOf("badges are hidden");              // library failure
        text.Should().ContainEquivalentOf("luatools://fix");                 // link for a non-installed game
    }

    [Fact]
    public void The_1_7_13_2_entry_still_names_what_that_release_was_about()
    {
        string text = string.Join(" ", Changelog.Entries.Single(e => e.Version == "1.7.13.2").Highlights);

        text.Should().ContainEquivalentOf("All launch overrides");           // the list
        text.Should().ContainEquivalentOf("Restore all");                    // the batch restore
        text.Should().ContainEquivalentOf("backed up first");                // mandatory backups
        text.Should().ContainEquivalentOf("Show in folder");                 // opens the game's folder
    }

    [Fact]
    public void The_1_7_13_1_entry_still_names_what_that_release_was_about()
    {
        string text = string.Join(" ", Changelog.Entries.Single(e => e.Version == "1.7.13.1").Highlights);

        text.Should().ContainEquivalentOf("Denuvo Mode (Aliases)");          // the switch on the card
        text.Should().ContainEquivalentOf("denuvo_aliases");                 // the key it writes
        text.Should().ContainEquivalentOf("written atomically");             // AtomicFile
        text.Should().ContainEquivalentOf("keeps the switch on");            // survives an update
    }

    [Fact]
    public void The_1_7_13_entry_still_names_what_that_release_was_about()
    {
        // 1.7.13.1 is a point release on top of it; the big entry must not have been edited away.
        string text = string.Join(" ", Changelog.Entries.Single(e => e.Version == "1.7.13").Highlights);

        text.Should().ContainEquivalentOf("never through a mirror");         // F-04: self-update direct only
        text.Should().ContainEquivalentOf("SHA-256 must match");             // F-04: pinned digest
        text.Should().ContainEquivalentOf("LocalLow");                       // multi-location save backups
        text.Should().ContainEquivalentOf("Installed badge");                // Manage badge
        text.Should().ContainEquivalentOf("telemetry and the DonateKeys");   // Mode card privacy warning
        text.Should().ContainEquivalentOf("less than 5 minutes old");        // backup cleanup age gate
        text.Should().ContainEquivalentOf("all 29 languages");               // translation pass
        text.Should().ContainEquivalentOf("Central Package Management");     // F-17
        text.Should().ContainEquivalentOf("Glass effect");                   // glass shell toggle
        text.Should().ContainEquivalentOf("Acrylic");                        // 1.7.12.9: Mica read as no effect
        text.Should().ContainEquivalentOf("CloudRedirect was removed");       // backend removal
        text.Should().ContainEquivalentOf("decoded at the size");            // performance audit
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
            e.Version.Should().MatchRegex(@"^\d+\.\d+\.\d+(\.\d+)?$");
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
