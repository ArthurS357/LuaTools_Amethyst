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
