using System.IO;
using System.Text.Json;
using AwesomeAssertions;
using LuaToolsGui.Models;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Covers the v1.8.0 Config Guard:
/// <list type="bullet">
///   <item><see cref="AppDataMigrator"/> copying user data from <c>%AppData%\LuaToolsGui</c> to
///   <c>%AppData%\LuaToolsAmethyst</c>, leaving the legacy folder untouched.</item>
///   <item>The <c>"DonateKeys": false</c> vaccine stamped by <see cref="SettingsService"/>.</item>
///   <item>Safe recovery of missing or unrecognized <c>SelectedMode</c> values in
///   <see cref="ActiveBackendPolicy.RecoverSelectedMode"/>.</item>
///   <item><see cref="DnsMode"/> persistence in <c>settings.json</c>.</item>
/// </list>
/// </summary>
public sealed class ConfigGuardTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "luatools-config-guard", Guid.NewGuid().ToString("N"));

    public ConfigGuardTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    // ── AppData migration ────────────────────────────────────────────────────

    [Fact]
    public void Migration_copies_legacy_settings_and_subdirs_into_new_folder_and_keeps_legacy_folder()
    {
        string legacyDir = Path.Combine(_root, "LuaToolsGui");
        string newDir = Path.Combine(_root, "LuaToolsAmethyst");
        Directory.CreateDirectory(legacyDir);
        Directory.CreateDirectory(Path.Combine(legacyDir, "plugin", "public"));

        File.WriteAllText(Path.Combine(legacyDir, "settings.json"), """{"SelectedMode":"AmethystTool"}""");
        File.WriteAllText(Path.Combine(legacyDir, "plugin", "public", "luatools.js"), "// plugin");

        var logs = new List<string>();
        bool migrated = AppDataMigrator.MigrateIfNeeded(legacyDir, newDir, logs.Add);

        migrated.Should().BeTrue();
        Directory.Exists(newDir).Should().BeTrue();
        File.ReadAllText(Path.Combine(newDir, "settings.json")).Should().Contain("AmethystTool");
        File.ReadAllText(Path.Combine(newDir, "plugin", "public", "luatools.js")).Should().Be("// plugin");
        // Copied, not moved: upstream still reads the legacy folder and must find every byte where it was.
        File.ReadAllText(Path.Combine(legacyDir, "settings.json")).Should().Contain("AmethystTool");
        File.ReadAllText(Path.Combine(legacyDir, "plugin", "public", "luatools.js")).Should().Be("// plugin");
        Directory.Exists(newDir + ".migrating").Should().BeFalse();
        logs.Should().ContainSingle(l => l.Contains("AppDataMigrator: copied"));
    }

    [Fact]
    public void Migration_still_runs_when_the_new_folder_holds_only_a_log()
    {
        // AppLog writes into the new folder, including a failed attempt's own message. That must not read as
        // "already migrated", or one failure would strand the user's settings in the legacy folder for good.
        string legacyDir = Path.Combine(_root, "LuaToolsGui");
        string newDir = Path.Combine(_root, "LuaToolsAmethyst");
        Directory.CreateDirectory(legacyDir);
        Directory.CreateDirectory(newDir);
        File.WriteAllText(Path.Combine(legacyDir, "settings.json"), """{"ManagePageSize":48}""");
        File.WriteAllText(Path.Combine(newDir, "plugin-backend.log"), "earlier failure");

        AppDataMigrator.MigrateIfNeeded(legacyDir, newDir, _ => { }).Should().BeTrue();

        File.ReadAllText(Path.Combine(newDir, "settings.json")).Should().Contain("48");
        File.ReadAllText(Path.Combine(newDir, "plugin-backend.log")).Should().Be("earlier failure");
    }

    [Fact]
    public void A_staging_folder_left_by_a_crashed_attempt_is_discarded_and_redone()
    {
        string legacyDir = Path.Combine(_root, "LuaToolsGui");
        string newDir = Path.Combine(_root, "LuaToolsAmethyst");
        Directory.CreateDirectory(legacyDir);
        Directory.CreateDirectory(newDir + ".migrating");
        File.WriteAllText(Path.Combine(newDir + ".migrating", "half.json"), "partial");
        File.WriteAllText(Path.Combine(legacyDir, "settings.json"), "{}");

        AppDataMigrator.MigrateIfNeeded(legacyDir, newDir, _ => { }).Should().BeTrue();

        File.Exists(Path.Combine(newDir, "settings.json")).Should().BeTrue();
        File.Exists(Path.Combine(newDir, "half.json")).Should().BeFalse();
        Directory.Exists(newDir + ".migrating").Should().BeFalse();
    }

    [Fact]
    public void Migration_copies_files_when_new_folder_exists_but_is_empty()
    {
        string legacyDir = Path.Combine(_root, "LuaToolsGui");
        string newDir = Path.Combine(_root, "LuaToolsAmethyst");
        Directory.CreateDirectory(legacyDir);
        Directory.CreateDirectory(newDir); // exists, empty

        File.WriteAllText(Path.Combine(legacyDir, "settings.json"), """{"ManagePageSize":48}""");

        var logs = new List<string>();
        bool migrated = AppDataMigrator.MigrateIfNeeded(legacyDir, newDir, logs.Add);

        migrated.Should().BeTrue();
        File.Exists(Path.Combine(newDir, "settings.json")).Should().BeTrue();
        File.ReadAllText(Path.Combine(newDir, "settings.json")).Should().Contain("48");
        Directory.Exists(legacyDir).Should().BeTrue();
        logs.Should().NotBeEmpty();
    }

    [Fact]
    public void Migration_keeps_new_folder_and_ignores_legacy_when_both_have_content()
    {
        string legacyDir = Path.Combine(_root, "LuaToolsGui");
        string newDir = Path.Combine(_root, "LuaToolsAmethyst");
        Directory.CreateDirectory(legacyDir);
        Directory.CreateDirectory(newDir);

        File.WriteAllText(Path.Combine(legacyDir, "settings.json"), """{"SelectedMode":"OpenSteamTools"}""");
        File.WriteAllText(Path.Combine(newDir, "settings.json"), """{"SelectedMode":"AmethystTool"}""");

        var logs = new List<string>();
        bool migrated = AppDataMigrator.MigrateIfNeeded(legacyDir, newDir, logs.Add);

        migrated.Should().BeFalse();
        File.ReadAllText(Path.Combine(newDir, "settings.json")).Should().Contain("AmethystTool");
        File.ReadAllText(Path.Combine(legacyDir, "settings.json")).Should().Contain("OpenSteamTools");
        logs.Should().BeEmpty();
    }

    [Fact]
    public void Migration_is_a_no_op_when_legacy_folder_does_not_exist()
    {
        string legacyDir = Path.Combine(_root, "LuaToolsGui");
        string newDir = Path.Combine(_root, "LuaToolsAmethyst");

        bool migrated = AppDataMigrator.MigrateIfNeeded(legacyDir, newDir, _ => { });

        migrated.Should().BeFalse();
        Directory.Exists(newDir).Should().BeFalse();
    }

    // ── All-or-nothing copy, fallback, read-only ─────────────────────────────

    [Fact]
    public void A_file_that_cannot_be_copied_aborts_the_migration_and_leaves_no_new_folder()
    {
        string legacyDir = Path.Combine(_root, "LuaToolsGui");
        string newDir = Path.Combine(_root, "LuaToolsAmethyst");
        Directory.CreateDirectory(legacyDir);
        File.WriteAllText(Path.Combine(legacyDir, "settings.json"), "{\"ManagePageSize\":48}");
        string locked = Path.Combine(legacyDir, "auth.dat");
        File.WriteAllText(locked, "token");

        var logs = new List<string>();
        using (File.Open(locked, FileMode.Open, FileAccess.Read, FileShare.None)) // upstream holding it open
        {
            AppDataMigrator.MigrateIfNeeded(legacyDir, newDir, logs.Add).Should().BeFalse();

            Directory.Exists(newDir).Should().BeFalse("a half-filled folder would read as already migrated");
            Directory.Exists(newDir + ".migrating").Should().BeFalse();
            // The app keeps its configuration by staying on the legacy folder this session.
            AppDataMigrator.EffectiveDirectory(legacyDir, newDir).Should().Be(legacyDir);
            logs.Should().Contain(l => l.Contains("aborted"));
        }
        File.ReadAllText(Path.Combine(legacyDir, "settings.json")).Should().Contain("48");

        // Next start, nothing is holding it: the retry goes through and the new folder takes over.
        AppDataMigrator.MigrateIfNeeded(legacyDir, newDir, _ => { }).Should().BeTrue();
        File.Exists(Path.Combine(newDir, "auth.dat")).Should().BeTrue();
        AppDataMigrator.EffectiveDirectory(legacyDir, newDir).Should().Be(newDir);
    }

    [Fact]
    public void The_copy_stops_at_the_first_locked_file_in_the_root()
    {
        string legacyDir = Path.Combine(_root, "LuaToolsGui");
        Directory.CreateDirectory(legacyDir);
        string a = Path.Combine(legacyDir, "a.dat");
        string b = Path.Combine(legacyDir, "b.dat");
        File.WriteAllText(a, "1");
        File.WriteAllText(b, "2");

        var logs = new List<string>();
        using (File.Open(a, FileMode.Open, FileAccess.Read, FileShare.None))
        using (File.Open(b, FileMode.Open, FileAccess.Read, FileShare.None))
            AppDataMigrator.MigrateIfNeeded(legacyDir, Path.Combine(_root, "LuaToolsAmethyst"), logs.Add)
                .Should().BeFalse();

        logs.Count(l => l.Contains("could not copy")).Should().Be(1, "the second locked file is never attempted");
    }

    [Fact]
    public void The_copy_stops_at_the_first_failing_subfolder()
    {
        string legacyDir = Path.Combine(_root, "LuaToolsGui");
        string a = Path.Combine(legacyDir, "subA", "a.dat");
        string b = Path.Combine(legacyDir, "subB", "b.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(a)!);
        Directory.CreateDirectory(Path.GetDirectoryName(b)!);
        File.WriteAllText(a, "1");
        File.WriteAllText(b, "2");

        var logs = new List<string>();
        using (File.Open(a, FileMode.Open, FileAccess.Read, FileShare.None))
        using (File.Open(b, FileMode.Open, FileAccess.Read, FileShare.None))
            AppDataMigrator.MigrateIfNeeded(legacyDir, Path.Combine(_root, "LuaToolsAmethyst"), logs.Add)
                .Should().BeFalse();

        logs.Count(l => l.Contains("could not copy")).Should().Be(1, "the sibling folder is never entered");
    }

    [Fact]
    public void Promotion_into_an_existing_stateless_folder_is_all_or_nothing()
    {
        // The new folder exists with only AppLog's log, and that log is held open. Folding it into staging
        // fails, so NOTHING may be promoted: a per-entry merge would have moved settings.json and plugin\ in
        // around the locked log and left a folder that reads as "already migrated" (HasState) with the rest
        // of the legacy data missing.
        string legacyDir = Path.Combine(_root, "LuaToolsGui");
        string newDir = Path.Combine(_root, "LuaToolsAmethyst");
        Directory.CreateDirectory(Path.Combine(legacyDir, "plugin"));
        Directory.CreateDirectory(newDir);
        File.WriteAllText(Path.Combine(legacyDir, "settings.json"), """{"ManagePageSize":48}""");
        File.WriteAllText(Path.Combine(legacyDir, "plugin", "luatools.js"), "// plugin");
        File.WriteAllText(Path.Combine(legacyDir, "plugin-backend.log"), "legacy log");
        string log = Path.Combine(newDir, "plugin-backend.log");
        File.WriteAllText(log, "current log");

        using (File.Open(log, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            AppDataMigrator.MigrateIfNeeded(legacyDir, newDir, _ => { }).Should().BeFalse();

            File.Exists(Path.Combine(newDir, "settings.json")).Should().BeFalse();
            Directory.Exists(Path.Combine(newDir, "plugin")).Should().BeFalse();
            Directory.Exists(newDir + ".migrating").Should().BeFalse();
            AppDataMigrator.EffectiveDirectory(legacyDir, newDir).Should().Be(legacyDir, "still owed, so retried");
        }

        AppDataMigrator.MigrateIfNeeded(legacyDir, newDir, _ => { }).Should().BeTrue();
        File.ReadAllText(Path.Combine(newDir, "settings.json")).Should().Contain("48");
        File.Exists(Path.Combine(newDir, "plugin", "luatools.js")).Should().BeTrue();
        File.ReadAllText(log).Should().Be("current log", "the current log wins over the legacy copy");
        Directory.Exists(newDir + ".migrating").Should().BeFalse();
    }

    [Fact]
    public void The_effective_folder_is_the_new_one_unless_a_migration_is_still_owed()
    {
        string legacyDir = Path.Combine(_root, "LuaToolsGui");
        string newDir = Path.Combine(_root, "LuaToolsAmethyst");

        AppDataMigrator.EffectiveDirectory(legacyDir, newDir).Should().Be(newDir, "fresh install: nothing legacy");

        Directory.CreateDirectory(legacyDir);
        AppDataMigrator.EffectiveDirectory(legacyDir, newDir).Should().Be(newDir, "an empty legacy folder is nothing");

        File.WriteAllText(Path.Combine(legacyDir, "settings.json"), "{}");
        AppDataMigrator.EffectiveDirectory(legacyDir, newDir).Should().Be(legacyDir);

        Directory.CreateDirectory(newDir);
        File.WriteAllText(Path.Combine(newDir, "plugin-backend.log"), "just a log");
        AppDataMigrator.EffectiveDirectory(legacyDir, newDir).Should().Be(legacyDir, "a log is not state");

        File.WriteAllText(Path.Combine(newDir, "settings.json"), "{}");
        AppDataMigrator.EffectiveDirectory(legacyDir, newDir).Should().Be(newDir);
    }

    [Fact]
    public void Program_takes_the_single_instance_mutex_before_it_migrates()
    {
        // Main cannot be run from a test, so this pins the ORDER in the source: a second process must have
        // exited (the isFirst check) before anything may migrate, or two processes copy at once.
        // Comment lines are dropped first: a note that merely MENTIONS one of these calls must not satisfy (or
        // break) the order.
        string program = string.Join('\n',
            File.ReadLines(Path.Combine(RepoRoot(), "src", "LuaToolsGui", "Program.cs"))
                .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        int mutex = program.IndexOf("new Mutex(", StringComparison.Ordinal);
        int secondInstanceExit = program.IndexOf("if (!isFirst)", StringComparison.Ordinal);
        int leaves = program.IndexOf("return;", secondInstanceExit, StringComparison.Ordinal);
        int migrate = program.IndexOf("AppDataMigrator.MigrateIfNeeded()", StringComparison.Ordinal);
        int culture = program.IndexOf("ApplyUiCulture();", StringComparison.Ordinal);

        mutex.Should().BeGreaterThan(0);
        secondInstanceExit.Should().BeGreaterThan(mutex);
        leaves.Should().BeGreaterThan(secondInstanceExit, "the second instance must leave through a return");
        migrate.Should().BeGreaterThan(leaves, "the migration sits after the second instance's return");
        culture.Should().BeGreaterThan(migrate, "the UI culture is read from the migrated settings.json");
        program.Split("AppDataMigrator.MigrateIfNeeded()").Length.Should().Be(2, "exactly one call site");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LuaToolsGui.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("LuaToolsGui.sln not found above the test output.");
    }

    [Fact]
    public void A_stale_staging_folder_with_a_read_only_file_is_still_cleared()
    {
        string legacyDir = Path.Combine(_root, "LuaToolsGui");
        string newDir = Path.Combine(_root, "LuaToolsAmethyst");
        Directory.CreateDirectory(legacyDir);
        File.WriteAllText(Path.Combine(legacyDir, "settings.json"), "{}");
        Directory.CreateDirectory(newDir + ".migrating");
        string stale = Path.Combine(newDir + ".migrating", "stale.json");
        File.WriteAllText(stale, "x");
        File.SetAttributes(stale, FileAttributes.ReadOnly);

        AppDataMigrator.MigrateIfNeeded(legacyDir, newDir, _ => { }).Should().BeTrue();

        Directory.Exists(newDir + ".migrating").Should().BeFalse();
        File.Exists(Path.Combine(newDir, "stale.json")).Should().BeFalse();
    }

    // ── Legacy settings.json vaccine ─────────────────────────────────────────

    [Fact]
    public void A_hand_edited_legacy_settings_json_with_comments_and_trailing_commas_is_still_vaccinated()
    {
        string legacyDir = Path.Combine(_root, "LuaToolsGui");
        Directory.CreateDirectory(legacyDir);
        string legacy = Path.Combine(legacyDir, "settings.json");
        File.WriteAllText(legacy, "{\n  // my note\n  \"ManagePageSize\": 48,\n}");

        AppDataMigrator.MigrateIfNeeded(legacyDir, Path.Combine(_root, "LuaToolsAmethyst"), _ => { })
            .Should().BeTrue();

        using var doc = JsonDocument.Parse(File.ReadAllText(legacy));
        doc.RootElement.GetProperty("DonateKeys").ValueKind.Should().Be(JsonValueKind.False);
        doc.RootElement.GetProperty("ManagePageSize").GetInt32().Should().Be(48);
    }

    [Theory]
    [InlineData("""{"SelectedMode":"OpenSteamTools","ManagePageSize":48}""")]              // key absent
    [InlineData("""{"SelectedMode":"OpenSteamTools","ManagePageSize":48,"DonateKeys":true}""")] // explicit true
    public void Migration_vaccinates_the_legacy_settings_json_and_keeps_its_other_keys(string legacyJson)
    {
        string legacyDir = Path.Combine(_root, "LuaToolsGui");
        string newDir = Path.Combine(_root, "LuaToolsAmethyst");
        Directory.CreateDirectory(legacyDir);
        File.WriteAllText(Path.Combine(legacyDir, "settings.json"), legacyJson);

        AppDataMigrator.MigrateIfNeeded(legacyDir, newDir, _ => { }).Should().BeTrue();

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(legacyDir, "settings.json")));
        doc.RootElement.GetProperty("DonateKeys").ValueKind.Should().Be(JsonValueKind.False,
            "upstream reads this file and treats an absent key as true, which leaks Steam decryption keys");
        doc.RootElement.GetProperty("SelectedMode").GetString().Should().Be("OpenSteamTools");
        doc.RootElement.GetProperty("ManagePageSize").GetInt32().Should().Be(48);
        File.Exists(Path.Combine(newDir, "settings.json")).Should().BeTrue("the copy still happens");
    }

    [Fact]
    public void Migration_does_not_rewrite_a_legacy_settings_json_that_is_already_vaccinated()
    {
        string legacyDir = Path.Combine(_root, "LuaToolsGui");
        Directory.CreateDirectory(legacyDir);
        string legacy = Path.Combine(legacyDir, "settings.json");
        const string original = """{"DonateKeys":false,"X":1}""";
        File.WriteAllText(legacy, original);

        AppDataMigrator.MigrateIfNeeded(legacyDir, Path.Combine(_root, "LuaToolsAmethyst"), _ => { });

        File.ReadAllText(legacy).Should().Be(original, "no write, so the compact formatting survives");
    }

    [Fact]
    public void Migration_never_creates_a_legacy_settings_json()
    {
        string legacyDir = Path.Combine(_root, "LuaToolsGui");
        Directory.CreateDirectory(Path.Combine(legacyDir, "plugin"));
        File.WriteAllText(Path.Combine(legacyDir, "plugin", "luatools.js"), "// plugin");

        AppDataMigrator.MigrateIfNeeded(legacyDir, Path.Combine(_root, "LuaToolsAmethyst"), _ => { })
            .Should().BeTrue();

        File.Exists(Path.Combine(legacyDir, "settings.json")).Should().BeFalse();
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("[1,2,3]")] // valid JSON, not an object
    public void A_bad_legacy_settings_json_is_left_untouched_and_the_migration_still_runs(string legacyJson)
    {
        string legacyDir = Path.Combine(_root, "LuaToolsGui");
        string newDir = Path.Combine(_root, "LuaToolsAmethyst");
        Directory.CreateDirectory(legacyDir);
        string legacy = Path.Combine(legacyDir, "settings.json");
        File.WriteAllText(legacy, legacyJson);
        File.WriteAllText(Path.Combine(legacyDir, "other.json"), "{}");

        var logs = new List<string>();
        AppDataMigrator.MigrateIfNeeded(legacyDir, newDir, logs.Add).Should().BeTrue();

        File.ReadAllText(legacy).Should().Be(legacyJson);
        File.Exists(Path.Combine(newDir, "other.json")).Should().BeTrue();
        logs.Should().Contain(l => l.Contains("DonateKeys") || l.Contains("vaccinate"));
    }

    // ── DonateKeys vaccine ───────────────────────────────────────────────────

    [Fact]
    public void Saving_settings_always_stamps_DonateKeys_false_in_json()
    {
        string dir = Path.Combine(_root, "vaccine");
        var settings = new SettingsService(dir) { SelectedMode = ActiveBackendPolicy.AmethystToolToken };

        string json = File.ReadAllText(Path.Combine(dir, "settings.json"));
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.TryGetProperty("DonateKeys", out var prop).Should().BeTrue(
            "upstream treats an absent DonateKeys key as true and leaks Steam decryption keys over plain HTTP");
        prop.ValueKind.Should().Be(JsonValueKind.False);
    }

    [Fact]
    public void Saving_settings_overwrites_a_legacy_DonateKeys_true_with_false()
    {
        string dir = Path.Combine(_root, "vaccine-overwrite");
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, "settings.json"),
            """{"SelectedMode":"AmethystTool","DonateKeys":true}""");

        var settings = new SettingsService(dir);
        settings.ManagePageSize = 48; // triggers SaveCore

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "settings.json")));
        doc.RootElement.TryGetProperty("DonateKeys", out var prop).Should().BeTrue();
        prop.ValueKind.Should().Be(JsonValueKind.False);
    }

    // ── Backend recovery (SelectedMode) ──────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Bst")]
    [InlineData("UnknownUpstreamMode")]
    [InlineData("CloudRedirect")]
    public void Null_or_unrecognized_SelectedMode_restores_AmethystTool_when_installed_locally(string? persisted)
    {
        ActiveBackendPolicy.RecoverSelectedMode(persisted, amethystInstalledLocally: true)
            .Should().Be(ActiveBackendPolicy.AmethystToolToken);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Bst")]
    [InlineData("UnknownUpstreamMode")]
    public void Null_or_unrecognized_SelectedMode_returns_null_when_nothing_installed_so_onboarding_runs_cleanly(string? persisted)
    {
        ActiveBackendPolicy.RecoverSelectedMode(persisted, amethystInstalledLocally: false)
            .Should().BeNull();
    }

    [Theory]
    [InlineData("AmethystTool")]
    [InlineData("OpenSteamTools")]
    [InlineData("OpenSteamToolsNightly")]
    [InlineData("SteamTools")]
    public void Valid_SelectedMode_is_never_overwritten_by_recovery(string persisted)
    {
        ActiveBackendPolicy.RecoverSelectedMode(persisted, amethystInstalledLocally: true)
            .Should().Be(persisted);
        ActiveBackendPolicy.RecoverSelectedMode(persisted, amethystInstalledLocally: false)
            .Should().Be(persisted);
    }

    // ── DnsMode persistence ──────────────────────────────────────────────────

    [Fact]
    public void DnsMode_defaults_to_Auto_and_round_trips_as_json_string()
    {
        string dir = Path.Combine(_root, "dns-mode");

        var fresh = new SettingsService(dir);
        fresh.DnsMode.Should().Be(DnsMode.Auto);

        fresh.DnsMode = DnsMode.Always;
        string json = File.ReadAllText(Path.Combine(dir, "settings.json"));
        using (var doc = JsonDocument.Parse(json))
        {
            doc.RootElement.GetProperty("DnsMode").GetString().Should().Be("Always");
        }
        new SettingsService(dir).DnsMode.Should().Be(DnsMode.Always);

        fresh.DnsMode = DnsMode.Never;
        new SettingsService(dir).DnsMode.Should().Be(DnsMode.Never);
    }

    [Fact]
    public void Unrecognized_DnsMode_string_in_settings_json_falls_back_to_Auto_without_corrupting_file()
    {
        string dir = Path.Combine(_root, "dns-mode-invalid");
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, "settings.json"),
            """{"SelectedMode":"AmethystTool","DnsMode":"not-a-valid-mode"}""");

        var settings = new SettingsService(dir);

        settings.SelectedMode.Should().Be("AmethystTool");
        settings.DnsMode.Should().Be(DnsMode.Auto);
    }
}
