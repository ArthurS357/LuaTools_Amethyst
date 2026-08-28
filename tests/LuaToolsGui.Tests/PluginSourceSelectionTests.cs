using System.IO;
using AwesomeAssertions;
using LuaToolsGui;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Covers WHICH plugin source is active — the decision that replaced the automatic fallback. Pure policy
/// (<see cref="PluginSourceSelection"/>) plus the one thing that has to survive a restart: that the choice
/// is written to, and read back from, settings.json.
///
/// <para>
/// Two properties matter here and neither is obvious from reading the happy path. First, the persisted
/// value SELECTS from the compiled-in catalogue and can never introduce a repository — settings.json is a
/// file anything running as the user can write, and the install it governs places a DLL next to
/// steam.exe. Second, a user who already has a source installed keeps it: the fork's repo being the
/// default must not migrate an existing upstream install on the next app update.
/// </para>
/// </summary>
public class PluginSourceSelectionTests
{
    private static readonly PluginSource Amethyst = new("ArthurS357", "Front-end-Amethyst");
    private static readonly PluginSource Upstream = new("madoiscool", "LTSP");
    private static readonly PluginSource[] Catalog = [Amethyst, Upstream];

    // ── Precedence ────────────────────────────────────────────────────────────

    [Fact]
    public void The_users_own_choice_wins() =>
        PluginSourceSelection.Resolve(Catalog, chosen: Upstream.Slug, installed: Amethyst.Slug)
            .Should().Be(Upstream,
                "a choice the user made outranks what happens to be on disk — otherwise selecting a "
              + "source that then failed to install would silently revert");

    [Fact]
    public void With_no_choice_the_installed_source_is_kept() =>
        PluginSourceSelection.Resolve(Catalog, chosen: null, installed: Upstream.Slug)
            .Should().Be(Upstream,
                "an existing install must not be moved to a different creator by an app update");

    [Fact]
    public void With_nothing_chosen_and_nothing_installed_the_default_is_used() =>
        PluginSourceSelection.Resolve(Catalog, chosen: null, installed: null).Should().Be(Amethyst);

    [Fact]
    public void The_default_is_the_first_catalogue_entry() =>
        PluginSourceSelection.Resolve(Catalog, null, null).Should().Be(Catalog[0]);

    // ── Validation on load: a settings file selects, it never names ───────────

    [Theory]
    [InlineData("attacker/payload")]
    [InlineData("ArthurS357/payload")]
    [InlineData("attacker/Front-end-Amethyst")]
    [InlineData("ArthurS357/Front-end-Amethyst-evil")]
    [InlineData("https://github.com/attacker/payload")]
    [InlineData("../../etc")]
    [InlineData("")]
    [InlineData("   ")]
    public void A_persisted_source_outside_the_catalogue_is_ignored(string chosen)
    {
        // The whole point of validating on READ rather than on write: an existing settings.json was never
        // validated by anything, and this is the file an attacker with user-level access edits to redirect
        // what gets loaded by steam.exe.
        PluginSourceSelection.Resolve(Catalog, chosen, installed: null).Should().Be(Amethyst);
        PluginSourceSelection.Find(Catalog, chosen).Should().BeNull();
    }

    [Fact]
    public void An_uninstallable_looking_manifest_source_is_ignored_too() =>
        PluginSourceSelection.Resolve(Catalog, chosen: null, installed: "attacker/payload")
            .Should().Be(Amethyst,
                "the manifest lives in the same user-writable profile as settings.json and gets the same "
              + "treatment");

    [Fact]
    public void Slugs_match_case_insensitively_as_github_does() =>
        PluginSourceSelection.Find(Catalog, "MADOISCOOL/ltsp").Should().Be(Upstream);

    [Fact]
    public void Surrounding_whitespace_in_a_hand_edited_file_is_tolerated() =>
        PluginSourceSelection.Find(Catalog, "  madoiscool/LTSP  ").Should().Be(Upstream);

    [Fact]
    public void IsKnown_gates_on_the_catalogue_the_build_ships()
    {
        PluginSourceSelection.IsKnown(Catalog, Upstream).Should().BeTrue();
        PluginSourceSelection.IsKnown(Catalog, new PluginSource("attacker", "payload")).Should().BeFalse();
    }

    [Fact]
    public void The_real_catalogue_is_what_the_app_actually_offers()
    {
        // Resolve is called with AppConfig.PluginSources everywhere in the app; pin that both shipped
        // entries are selectable, so neither can be silently dropped from the page.
        foreach (var source in AppConfig.PluginSources)
            PluginSourceSelection.Resolve(AppConfig.PluginSources, source.Slug, installed: null)
                .Should().Be(source);
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    [Fact]
    public void A_chosen_source_survives_a_restart()
    {
        string dir = NewSettingsDir();
        try
        {
            new SettingsService(dir).PluginSource = Upstream.Slug;

            // A second instance is what a restart actually is: the file, re-read from scratch.
            new SettingsService(dir).PluginSource.Should().Be(Upstream.Slug);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Never_choosing_persists_nothing()
    {
        string dir = NewSettingsDir();
        try
        {
            // Distinguishable from a choice, which is what lets an existing install keep its own source.
            new SettingsService(dir).PluginSource.Should().BeNull();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_blank_choice_is_stored_as_no_choice_rather_than_as_an_empty_source()
    {
        string dir = NewSettingsDir();
        try
        {
            var settings = new SettingsService(dir) { PluginSource = "   " };
            settings.PluginSource.Should().BeNull();

            // And an empty string must not resolve to something: it names no source, so the default stands.
            PluginSourceSelection.Resolve(Catalog, settings.PluginSource, null).Should().Be(Amethyst);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_settings_file_naming_an_unknown_source_still_yields_a_usable_default()
    {
        string dir = NewSettingsDir();
        try
        {
            // Hand-edited (or written by a newer build that shipped a source this one does not).
            File.WriteAllText(Path.Combine(dir, "settings.json"),
                """{"PluginSource":"attacker/payload"}""");

            var settings = new SettingsService(dir);

            // The raw value round-trips — nothing silently rewrites the user's file...
            settings.PluginSource.Should().Be("attacker/payload");
            // ...but it selects nothing, so the app falls to the default rather than to that repository.
            PluginSourceSelection.Resolve(AppConfig.PluginSources, settings.PluginSource, null)
                .Should().Be(AppConfig.DefaultPluginSource);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>An isolated settings folder — these must never touch the developer's real settings.json.</summary>
    private static string NewSettingsDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "luatools-plugin-source-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }
}
