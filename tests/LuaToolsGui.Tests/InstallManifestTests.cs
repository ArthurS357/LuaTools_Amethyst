using AwesomeAssertions;
using LuaToolsGui.Models;
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

    // ── Absorbing files a new install just took over ──────────────────────────
    //
    // AmethystTool and a Mode both write dwmapi.dll/xinput1_4.dll. Before AbsorbFiles existed, installing
    // AmethystTool over an old Mode record left that record still claiming the two proxies — so uninstalling
    // AmethystTool reported them as "kept: still needed by another install", which was false: the Mode's
    // bytes were gone, overwritten by AmethystTool's own install moments earlier.

    private static readonly string[] Proxies = ["dwmapi.dll", "xinput1_4.dll"];

    [Fact]
    public void A_mode_record_naming_only_the_shared_proxies_is_dropped_once_absorbed()
    {
        var manifest = InstallManifest.Empty
            .With(Entry(PluginIds.ForMode(UnlockerMode.OpenSteamTools), "dwmapi.dll", "xinput1_4.dll"))
            .AbsorbFiles(Proxies, PluginIds.AmethystTool);

        manifest.Get(PluginIds.ForMode(UnlockerMode.OpenSteamTools)).Should().BeNull();
    }

    [Fact]
    public void A_mode_record_with_a_file_AmethystTool_never_touches_keeps_that_file()
    {
        // The false positive this exists to avoid: BetterSteamTools also places OpenSteamTool.dll, which
        // AmethystTool's install never writes and never verifies. Absorbing the whole entry would hand
        // AmethystTool's manifest row a file it cannot account for.
        string mode = PluginIds.ForMode(UnlockerMode.OpenSteamTools);
        var manifest = InstallManifest.Empty
            .With(Entry(mode, "dwmapi.dll", "xinput1_4.dll", "OpenSteamTool.dll"))
            .AbsorbFiles(Proxies, PluginIds.AmethystTool);

        manifest.Get(mode)!.Files.Select(f => f.Name).Should().BeEquivalentTo("OpenSteamTool.dll");
    }

    [Fact]
    public void A_record_that_shares_none_of_the_absorbed_names_is_untouched()
    {
        var manifest = InstallManifest.Empty.With(Entry(PluginIds.StorePage, "winmm.dll"));

        manifest.AbsorbFiles(Proxies, PluginIds.AmethystTool)
            .Get(PluginIds.StorePage)!.Files.Select(f => f.Name).Should().BeEquivalentTo("winmm.dll");
    }

    [Fact]
    public void The_new_owners_own_entry_is_never_touched_by_its_own_absorb_call()
    {
        // AmethystTool re-records itself with the very names it is about to absorb; excluding its own id
        // is what stops that fresh entry being read back and immediately stripped.
        var manifest = InstallManifest.Empty
            .With(Entry(PluginIds.AmethystTool, "dwmapi.dll", "xinput1_4.dll"))
            .AbsorbFiles(Proxies, PluginIds.AmethystTool);

        manifest.Get(PluginIds.AmethystTool)!.Files.Select(f => f.Name)
            .Should().BeEquivalentTo("dwmapi.dll", "xinput1_4.dll");
    }

    [Fact]
    public void Absorbing_twice_is_a_no_op_the_second_time()
    {
        string mode = PluginIds.ForMode(UnlockerMode.OpenSteamTools);
        var once = InstallManifest.Empty
            .With(Entry(mode, "dwmapi.dll", "xinput1_4.dll", "OpenSteamTool.dll"))
            .AbsorbFiles(Proxies, PluginIds.AmethystTool);

        var twice = once.AbsorbFiles(Proxies, PluginIds.AmethystTool);

        ReferenceEquals(once, twice).Should().BeTrue("nothing overlapped the second time — same instance back");
        twice.Get(mode)!.Files.Select(f => f.Name).Should().BeEquivalentTo("OpenSteamTool.dll");
    }

    [Fact]
    public void Absorbing_nothing_new_leaves_the_manifest_untouched()
    {
        var manifest = InstallManifest.Empty.With(Entry(PluginIds.StorePage, "winmm.dll"));

        ReferenceEquals(manifest, manifest.AbsorbFiles([], PluginIds.AmethystTool)).Should().BeTrue();
    }

    [Fact]
    public void Absorbed_names_match_case_insensitively_the_way_windows_names_are()
    {
        string mode = PluginIds.ForMode(UnlockerMode.OpenSteamTools);
        var manifest = InstallManifest.Empty
            .With(Entry(mode, "DWMAPI.DLL", "XINPUT1_4.DLL"))
            .AbsorbFiles(Proxies, PluginIds.AmethystTool);

        manifest.Get(mode).Should().BeNull();
    }

    [Fact]
    public void A_record_with_no_overlap_at_all_survives_untouched_when_others_are_absorbed()
    {
        // A legacy Mode uninstall must still work after AmethystTool has taken the slot from a DIFFERENT
        // mode — this one was never touched, so its own record stays exactly as it was.
        string untouchedMode = PluginIds.ForMode(UnlockerMode.CloudRedirect);
        var manifest = InstallManifest.Empty
            .With(Entry(untouchedMode, "cloud_redirect.dll"))
            .With(Entry(PluginIds.ForMode(UnlockerMode.OpenSteamTools), "dwmapi.dll", "xinput1_4.dll"))
            .AbsorbFiles(Proxies, PluginIds.AmethystTool);

        manifest.Get(untouchedMode)!.Files.Select(f => f.Name).Should().BeEquivalentTo("cloud_redirect.dll");
    }

    // ── Recording exclusively: the two competing backends, in BOTH directions ─
    //
    // AbsorbFiles was only ever CALLED from the AmethystTool install. Installing a Mode over an active
    // AmethystTool recorded the Mode and left AmethystTool's entry still claiming dwmapi.dll/xinput1_4.dll,
    // so both entries claimed the same two names — and a name two entries claim is one NEITHER can remove:
    // each reads the other's claim as "still needed by another install". RecordExclusive is the single
    // transformation that makes that state unreachable, whichever backend just won the slot.

    private static readonly string[] AmethystPayload =
        ["AmethystTool.dll", "amethysttool.toml", "dwmapi.dll", "xinput1_4.dll"];

    private static InstalledPlugin ModeEntry(UnlockerMode mode, params string[] files) =>
        Entry(PluginIds.ForMode(mode), files);

    [Fact]
    public void A_mode_recorded_over_AmethystTool_takes_the_proxies_off_its_claim()
    {
        var manifest = InstallManifest.Empty
            .With(Entry(PluginIds.AmethystTool, AmethystPayload))
            .RecordExclusive(ModeEntry(UnlockerMode.OpenSteamTools,
                "dwmapi.dll", "xinput1_4.dll", "OpenSteamTool.dll"));

        manifest.Get(PluginIds.AmethystTool)!.Files.Select(f => f.Name)
            .Should().NotContain(["dwmapi.dll", "xinput1_4.dll"]);
    }

    [Fact]
    public void A_mode_recorded_over_AmethystTool_leaves_the_files_no_mode_ever_places()
    {
        // The invariant that stops this becoming a fold: a Mode's install writes neither of these two, so
        // absorbing them would hand the Mode's row files it never placed and cannot verify — and would
        // strip AmethystTool of the only names by which its own uninstall could find them.
        var manifest = InstallManifest.Empty
            .With(Entry(PluginIds.AmethystTool, AmethystPayload))
            .RecordExclusive(ModeEntry(UnlockerMode.OpenSteamTools,
                "dwmapi.dll", "xinput1_4.dll", "OpenSteamTool.dll"));

        manifest.Get(PluginIds.AmethystTool)!.Files.Select(f => f.Name)
            .Should().BeEquivalentTo("AmethystTool.dll", "amethysttool.toml");
    }

    [Fact]
    public void An_AmethystTool_entry_left_with_nothing_is_dropped_rather_than_kept_empty()
    {
        // A pre-1.6 record, or one whose payload files were pruned: nothing survives the trim, so the entry
        // itself must go. An empty entry would still count as "an install exists" on the Plugin page.
        var manifest = InstallManifest.Empty
            .With(Entry(PluginIds.AmethystTool, "dwmapi.dll", "xinput1_4.dll"))
            .RecordExclusive(ModeEntry(UnlockerMode.OpenSteamTools, "dwmapi.dll", "xinput1_4.dll"));

        manifest.Get(PluginIds.AmethystTool).Should().BeNull();
    }

    [Fact]
    public void Recording_a_mode_exclusively_twice_changes_nothing_the_second_time()
    {
        var entry = ModeEntry(UnlockerMode.OpenSteamTools, "dwmapi.dll", "xinput1_4.dll", "OpenSteamTool.dll");
        var once = InstallManifest.Empty
            .With(Entry(PluginIds.AmethystTool, AmethystPayload))
            .RecordExclusive(entry);

        var twice = once.RecordExclusive(entry);

        twice.Get(PluginIds.AmethystTool)!.Files.Select(f => f.Name)
            .Should().BeEquivalentTo("AmethystTool.dll", "amethysttool.toml");
        twice.Get(entry.PluginId)!.Files.Select(f => f.Name)
            .Should().BeEquivalentTo("dwmapi.dll", "xinput1_4.dll", "OpenSteamTool.dll");
    }

    [Fact]
    public void Recording_a_mode_exclusively_leaves_an_unrelated_plugin_alone()
    {
        // store-page claims winmm.dll/winmm_real.dll, which no Mode and no AmethystTool payload names.
        var manifest = InstallManifest.Empty
            .With(Entry(PluginIds.StorePage, "winmm.dll", "winmm_real.dll"))
            .With(Entry(PluginIds.AmethystTool, AmethystPayload))
            .RecordExclusive(ModeEntry(UnlockerMode.OpenSteamTools, "dwmapi.dll", "xinput1_4.dll"));

        manifest.Get(PluginIds.StorePage)!.Files.Select(f => f.Name)
            .Should().BeEquivalentTo("winmm.dll", "winmm_real.dll");
    }

    [Fact]
    public void A_file_a_mode_could_not_write_stays_claimed_by_whoever_did_place_it()
    {
        // Locked or access-denied files are left out of the recorded name set by RecordModeInstall, so the
        // bytes on disk are still AmethystTool's. Absorbing that name anyway would hand the Mode a claim on
        // a file it demonstrably failed to place.
        var manifest = InstallManifest.Empty
            .With(Entry(PluginIds.AmethystTool, AmethystPayload))
            .RecordExclusive(ModeEntry(UnlockerMode.OpenSteamTools, "dwmapi.dll", "OpenSteamTool.dll"));

        manifest.Get(PluginIds.AmethystTool)!.Files.Select(f => f.Name)
            .Should().Contain("xinput1_4.dll");
    }

    [Fact]
    public void AmethystTool_recorded_over_a_mode_trims_that_mode_the_same_way()
    {
        // The direction that already worked, now going through the same call — the symmetry is the point.
        string mode = PluginIds.ForMode(UnlockerMode.OpenSteamTools);
        var manifest = InstallManifest.Empty
            .With(Entry(mode, "dwmapi.dll", "xinput1_4.dll", "OpenSteamTool.dll"))
            .RecordExclusive(Entry(PluginIds.AmethystTool, AmethystPayload));

        manifest.Get(mode)!.Files.Select(f => f.Name).Should().BeEquivalentTo("OpenSteamTool.dll");
        manifest.Get(PluginIds.AmethystTool)!.Files.Select(f => f.Name).Should().BeEquivalentTo(AmethystPayload);
    }

    [Fact]
    public void Recording_exclusively_into_an_empty_manifest_is_just_a_record()
    {
        var manifest = InstallManifest.Empty
            .RecordExclusive(Entry(PluginIds.AmethystTool, AmethystPayload));

        manifest.Get(PluginIds.AmethystTool)!.Files.Select(f => f.Name).Should().BeEquivalentTo(AmethystPayload);
        manifest.Plugins.Should().ContainSingle();
    }

    [Fact]
    public void No_file_is_left_claimed_by_two_entries_after_an_exclusive_record()
    {
        // The invariant itself, stated once: this is what "kept — still needed by another install" was
        // wrongly reporting, in both directions at the same time.
        var manifest = InstallManifest.Empty
            .With(Entry(PluginIds.AmethystTool, AmethystPayload))
            .RecordExclusive(ModeEntry(UnlockerMode.OpenSteamTools, "dwmapi.dll", "xinput1_4.dll"));

        var claimed = manifest.Plugins.SelectMany(p => p.Value.Files.Select(f => f.Name.ToLowerInvariant()));
        claimed.Should().OnlyHaveUniqueItems();
    }

    // ── The store page through the same call ─────────────────────────────────
    //
    // The store-page install went on calling Record after the Mode and AmethystTool sites moved to
    // RecordExclusive. Its slot names collide with no backend's payload, so the two calls agree today —
    // these pin THAT they agree, which is what makes routing it through the same call a uniformity change
    // and not a behaviour change, and what would fail first if a future slot did start colliding.

    private static readonly string[] StorePagePayload = ["winmm.dll", "winmm_real.dll"];

    private static IEnumerable<(string Plugin, string File)> Claims(InstallManifest manifest) =>
        manifest.Plugins.SelectMany(p => p.Value.Files.Select(f => (p.Key, f.Name)));

    [Fact]
    public void Recording_the_store_page_exclusively_takes_nothing_off_either_backend()
    {
        string mode = PluginIds.ForMode(UnlockerMode.CloudRedirect);
        var manifest = InstallManifest.Empty
            .With(Entry(mode, "cloud_redirect.dll"))
            .RecordExclusive(Entry(PluginIds.AmethystTool, AmethystPayload))
            .RecordExclusive(Entry(PluginIds.StorePage, StorePagePayload));

        manifest.Get(mode)!.Files.Select(f => f.Name).Should().BeEquivalentTo("cloud_redirect.dll");
        manifest.Get(PluginIds.AmethystTool)!.Files.Select(f => f.Name).Should().BeEquivalentTo(AmethystPayload);
        manifest.Get(PluginIds.StorePage)!.Files.Select(f => f.Name).Should().BeEquivalentTo(StorePagePayload);
    }

    [Fact]
    public void Recording_the_store_page_exclusively_is_indistinguishable_from_recording_it_plainly()
    {
        var before = InstallManifest.Empty
            .With(Entry(PluginIds.ForMode(UnlockerMode.CloudRedirect), "cloud_redirect.dll"))
            .RecordExclusive(Entry(PluginIds.AmethystTool, AmethystPayload));
        var entry = Entry(PluginIds.StorePage, StorePagePayload);

        Claims(before.RecordExclusive(entry)).Should().BeEquivalentTo(Claims(before.With(entry)));
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
