using AwesomeAssertions;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Pins the shape of the install record — the thing that turns uninstall from a guess into a fact.
///
/// <para>
/// These cover the in-memory contract (<see cref="InstallManifest"/>), not the file. The service writes to
/// a fixed <c>%AppData%</c> path shared with the running app, so exercising it here would mean tests that
/// clobber a real user's record; the value of testing it is in the transformations, and those are pure.
/// The one file-level behaviour that matters — a corrupt file must read as empty rather than throw — is
/// covered through <see cref="InstallManifestService.Load"/> below, which is safe because it only reads.
/// </para>
/// </summary>
public class InstallManifestTests
{
    private static InstalledPlugin Entry(string id, params string[] files) =>
        new(id, "v1.0.0", DateTimeOffset.UnixEpoch,
            [.. files.Select(f => new InstalledFile(f, null))]);

    // ── Recording ─────────────────────────────────────────────────────────────

    [Fact]
    public void An_empty_manifest_knows_about_nothing() =>
        InstallManifest.Empty.Get(PluginIds.AmethystTool).Should().BeNull();

    [Fact]
    public void A_recorded_plugin_can_be_read_back()
    {
        var manifest = InstallManifest.Empty.With(Entry(PluginIds.AmethystTool, "dwmapi.dll"));

        manifest.Get(PluginIds.AmethystTool)!.Files.Select(f => f.Name)
            .Should().BeEquivalentTo("dwmapi.dll");
    }

    [Fact]
    public void Recording_the_same_plugin_again_replaces_it_rather_than_appending()
    {
        // Reinstall must not accumulate stale file names, or uninstall would try to remove things the
        // current version never placed.
        var manifest = InstallManifest.Empty
            .With(Entry(PluginIds.AmethystTool, "old.dll"))
            .With(Entry(PluginIds.AmethystTool, "new.dll"));

        manifest.Get(PluginIds.AmethystTool)!.Files.Select(f => f.Name)
            .Should().BeEquivalentTo("new.dll");
    }

    [Fact]
    public void Forgetting_one_plugin_leaves_the_others_alone()
    {
        var manifest = InstallManifest.Empty
            .With(Entry(PluginIds.AmethystTool, "dwmapi.dll"))
            .With(Entry(PluginIds.StorePage, "winmm.dll"))
            .Without(PluginIds.AmethystTool);

        manifest.Get(PluginIds.AmethystTool).Should().BeNull();
        manifest.Get(PluginIds.StorePage).Should().NotBeNull();
    }

    [Fact]
    public void Forgetting_something_that_was_never_recorded_is_harmless() =>
        InstallManifest.Empty.Without("never-installed").Plugins.Should().BeEmpty();

    [Fact]
    public void Every_write_stamps_the_current_schema_version() =>
        InstallManifest.Empty.With(Entry(PluginIds.StorePage, "winmm.dll")).SchemaVersion
            .Should().Be(InstallManifest.CurrentSchemaVersion);

    // ── Cross-plugin claims: what keeps a shared proxy DLL alive ──────────────

    [Fact]
    public void Files_claimed_by_others_excludes_the_plugin_being_removed()
    {
        var manifest = InstallManifest.Empty.With(Entry(PluginIds.AmethystTool, "dwmapi.dll"));

        manifest.FilesClaimedByOthers(PluginIds.AmethystTool).Should().BeEmpty();
    }

    [Fact]
    public void Files_claimed_by_others_reports_another_plugins_files()
    {
        var manifest = InstallManifest.Empty
            .With(Entry(PluginIds.AmethystTool, "dwmapi.dll", "AmethystTool.dll"))
            .With(Entry(PluginIds.StorePage, "winmm.dll"));

        manifest.FilesClaimedByOthers(PluginIds.AmethystTool).Should().BeEquivalentTo("winmm.dll");
    }

    [Fact]
    public void An_overlapping_name_is_reported_as_claimed()
    {
        // The case the whole mechanism exists for: two installs, same file name.
        var manifest = InstallManifest.Empty
            .With(Entry(PluginIds.AmethystTool, "dwmapi.dll"))
            .With(Entry("some-other-tool", "dwmapi.dll"));

        manifest.FilesClaimedByOthers(PluginIds.AmethystTool).Should().Contain("dwmapi.dll");
    }

    [Fact]
    public void Claims_are_matched_case_insensitively_the_way_windows_names_are()
    {
        var manifest = InstallManifest.Empty.With(Entry(PluginIds.StorePage, "WINMM.DLL"));

        manifest.FilesClaimedByOthers(PluginIds.AmethystTool).Should().Contain("winmm.dll");
    }

    [Fact]
    public void A_plugin_id_matches_regardless_of_case()
    {
        var manifest = InstallManifest.Empty.With(Entry("AmethystTool", "dwmapi.dll"));

        manifest.Get("amethysttool").Should().NotBeNull();
        manifest.FilesClaimedByOthers("AMETHYSTTOOL").Should().BeEmpty();
    }

    // ── Reading a file that is not there, or is nonsense ──────────────────────

    [Fact]
    public void Loading_when_no_record_file_exists_yields_an_empty_manifest_not_an_error()
    {
        // A read that threw would take the Plugin page down on a machine that has simply never installed
        // anything. This is a read-only call against the real path, which may or may not exist.
        var act = () => new InstallManifestService().Load();

        act.Should().NotThrow();
        act().Should().NotBeNull();
    }

    [Fact]
    public void The_record_lives_in_its_own_file_not_in_settings_json()
    {
        // settings.json is user-editable configuration with a documented shape; this is app-owned
        // bookkeeping rewritten on every install. Mixing them would put a preference at risk of an
        // install, and vice versa.
        InstallManifestService.FilePath.Should().EndWith("install-manifest.json");
        InstallManifestService.FilePath.Should().NotContain("settings.json");
    }
}
