using System.IO;
using System.Text.Json;
using AwesomeAssertions;
using LuaToolsGui.Models;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// The install record's FILE, exercised against an isolated directory rather than the one the running app
/// shares with whoever runs the tests.
///
/// <para>
/// What is being pinned is the property the whole uninstall feature leans on: a record on disk is either
/// the complete previous one or the complete new one, never a half. <c>File.WriteAllText</c> truncates
/// first and fills after, so an interruption in that window leaves a file that parses as nothing — and
/// "nothing is recorded" is exactly what makes Uninstall refuse to touch files that are still very much in
/// the Steam root. The atomic swap is what stops a crash from silently disarming the feature.
/// </para>
/// </summary>
public class InstallManifestWriteTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "luatools-tests", Guid.NewGuid().ToString("N"));

    private readonly InstallManifestService _service;

    public InstallManifestWriteTests()
    {
        Directory.CreateDirectory(_dir);
        _service = new InstallManifestService(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private string RecordPath => Path.Combine(_dir, "install-manifest.json");

    private IReadOnlyList<string> LooseFiles() =>
        [.. Directory.EnumerateFiles(_dir).Select(Path.GetFileName).OfType<string>()];

    private static InstalledPlugin Entry(string id, params string[] files) =>
        new(id, "v1.2.3", DateTimeOffset.UnixEpoch,
            [.. files.Select(f => new InstalledFile(f, null))]);

    // ── The write lands, whole ────────────────────────────────────────────────

    [Fact]
    public void Recording_writes_a_record_that_reads_back_intact()
    {
        _service.Record(Entry(PluginIds.AmethystTool, "dwmapi.dll", "AmethystTool.dll")).Should().BeTrue();

        var reloaded = new InstallManifestService(_dir).Load();

        reloaded.Get(PluginIds.AmethystTool)!.Files.Select(f => f.Name)
            .Should().BeEquivalentTo("dwmapi.dll", "AmethystTool.dll");
    }

    [Fact]
    public void The_record_on_disk_is_complete_json_not_a_truncated_prefix()
    {
        _service.Record(Entry(PluginIds.AmethystTool, "dwmapi.dll"));

        var act = () => JsonDocument.Parse(File.ReadAllText(RecordPath));

        act.Should().NotThrow();
    }

    [Fact]
    public void A_successful_write_leaves_no_temporary_file_behind()
    {
        // A leftover .tmp next to the record is both litter in the user's AppData and a sign the swap did
        // not happen — the two things this write path exists to avoid.
        _service.Record(Entry(PluginIds.AmethystTool, "dwmapi.dll"));

        LooseFiles().Should().ContainSingle().Which.Should().Be("install-manifest.json");
    }

    [Fact]
    public void Overwriting_an_existing_record_replaces_it_and_still_leaves_no_temporary()
    {
        // The second write is the one that takes the File.Replace path; the first had nothing to replace.
        _service.Record(Entry(PluginIds.AmethystTool, "old.dll"));
        _service.Record(Entry(PluginIds.AmethystTool, "new.dll"));

        _service.Load().Get(PluginIds.AmethystTool)!.Files.Select(f => f.Name)
            .Should().BeEquivalentTo("new.dll");
        LooseFiles().Should().ContainSingle();
    }

    [Fact]
    public void Repeated_writes_never_accumulate_temporaries()
    {
        for (int i = 0; i < 10; i++) _service.Record(Entry(PluginIds.StorePage, $"slot{i}.dll"));

        LooseFiles().Should().ContainSingle();
    }

    [Fact]
    public void Forgetting_a_plugin_is_persisted()
    {
        _service.Record(Entry(PluginIds.AmethystTool, "dwmapi.dll"));
        _service.Record(Entry(PluginIds.StorePage, "winmm.dll"));

        _service.Forget(PluginIds.AmethystTool).Should().BeTrue();

        var reloaded = new InstallManifestService(_dir).Load();
        reloaded.Get(PluginIds.AmethystTool).Should().BeNull();
        reloaded.Get(PluginIds.StorePage).Should().NotBeNull();
        LooseFiles().Should().ContainSingle();
    }

    [Fact]
    public void Recording_the_store_page_exclusively_persists_and_leaves_the_active_mode_intact()
    {
        // The write-level half of the same guarantee: RecordSteamRootFiles now goes through
        // RecordExclusive, and winmm.dll/winmm_real.dll overlap nothing, so the Mode entry must survive
        // the install untouched rather than being trimmed or dropped.
        string mode = PluginIds.ForMode(UnlockerMode.OpenSteamTools);
        _service.Record(Entry(mode, "dwmapi.dll", "xinput1_4.dll"));

        _service.RecordExclusive(Entry(PluginIds.StorePage, "winmm.dll", "winmm_real.dll"))
            .Should().BeTrue();

        var reloaded = new InstallManifestService(_dir).Load();
        reloaded.Get(PluginIds.StorePage)!.Files.Select(f => f.Name)
            .Should().BeEquivalentTo("winmm.dll", "winmm_real.dll");
        reloaded.Get(mode)!.Files.Select(f => f.Name)
            .Should().BeEquivalentTo("dwmapi.dll", "xinput1_4.dll");
    }

    [Fact]
    public void Absorbing_a_shared_file_is_persisted()
    {
        _service.Record(Entry(PluginIds.ForMode(UnlockerMode.OpenSteamTools), "dwmapi.dll", "xinput1_4.dll"));

        _service.AbsorbFiles(["dwmapi.dll", "xinput1_4.dll"], PluginIds.AmethystTool).Should().BeTrue();

        new InstallManifestService(_dir).Load().Get(PluginIds.ForMode(UnlockerMode.OpenSteamTools))
            .Should().BeNull();
    }

    [Fact]
    public void Absorbing_nothing_that_overlaps_skips_the_write()
    {
        _service.Record(Entry(PluginIds.StorePage, "winmm.dll"));

        _service.AbsorbFiles(["dwmapi.dll"], PluginIds.AmethystTool).Should().BeTrue();

        // No write happened — LooseFiles would show a stray temp file if SaveUnlocked had run and left one.
        LooseFiles().Should().ContainSingle();
    }

    // ── Reading what should not be there ──────────────────────────────────────

    [Fact]
    public void Loading_with_no_record_file_yields_an_empty_manifest()
    {
        _service.Load().Plugins.Should().BeEmpty();
    }

    [Fact]
    public void A_corrupt_record_reads_as_empty_rather_than_throwing()
    {
        // The fail-safe direction: "nothing is recorded" means "remove nothing". A read that threw would
        // take the Plugin page down on a machine whose file was mangled by hand or by a bad shutdown.
        File.WriteAllText(RecordPath, "{ this is not json");

        var act = () => new InstallManifestService(_dir).Load();

        act.Should().NotThrow();
        act().Plugins.Should().BeEmpty();
    }

    [Fact]
    public void A_truncated_record_reads_as_empty_rather_than_throwing()
    {
        _service.Record(Entry(PluginIds.AmethystTool, "dwmapi.dll"));
        string whole = File.ReadAllText(RecordPath);
        File.WriteAllText(RecordPath, whole[..(whole.Length / 2)]);

        new InstallManifestService(_dir).Load().Plugins.Should().BeEmpty();
    }

    [Fact]
    public void A_record_from_a_future_schema_is_ignored_rather_than_guessed_at()
    {
        File.WriteAllText(RecordPath,
            "{\"SchemaVersion\":" + (InstallManifest.CurrentSchemaVersion + 1) + ",\"Plugins\":{}}");

        new InstallManifestService(_dir).Load().Plugins.Should().BeEmpty();
    }

    [Fact]
    public void Writing_creates_the_state_directory_when_it_is_missing()
    {
        string nested = Path.Combine(_dir, "not-created-yet");

        new InstallManifestService(nested).Record(Entry(PluginIds.AmethystTool, "dwmapi.dll"))
            .Should().BeTrue();

        File.Exists(Path.Combine(nested, "install-manifest.json")).Should().BeTrue();
    }
}
