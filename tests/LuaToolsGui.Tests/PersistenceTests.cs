using System.IO;
using System.Text.Json;
using AwesomeAssertions;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Covers <see cref="SettingsService"/> and <see cref="CacheService"/> persistence: concurrency, atomic
/// writes, corruption handling, and encryption of the Hubcap key.
///
/// <para>
/// Both are DI singletons written from the UI thread and from background work at the same time (the
/// plugin's headless add, the hardware-appid refresh, mode install/detect). Before the audit they had no
/// lock and no guard around the write, so overlapping saves threw <see cref="IOException"/> out of what
/// looks to callers like a plain property assignment — with no global handler to catch it.
/// </para>
///
/// <para>
/// Each test gets its own temp directory through the internal test constructor. That seam exists because
/// both classes previously hardcoded <c>%AppData%</c> in static fields, which made this logic untestable
/// without trampling the developer's real settings.
/// </para>
/// </summary>
public sealed class PersistenceTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "luatools-tests", Guid.NewGuid().ToString("N"));

    public PersistenceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private string SettingsFile => Path.Combine(_dir, "settings.json");
    private string CacheFile => Path.Combine(_dir, "cache.json");

    // ── SettingsService: round-trip ───────────────────────────────────────────

    [Fact]
    public void Settings_survive_a_reload()
    {
        new SettingsService(_dir) { SelectedMode = "OpenSteamTools", ManagePageSize = 48 };

        var reloaded = new SettingsService(_dir);

        reloaded.SelectedMode.Should().Be("OpenSteamTools");
        reloaded.ManagePageSize.Should().Be(48);
    }

    [Fact]
    public void Defaults_apply_when_nothing_was_ever_set()
    {
        var settings = new SettingsService(_dir);

        settings.AutoUpdateApps.Should().BeTrue("installs default to not pinning manifests");
        settings.ManagePageSize.Should().Be(24);
        settings.BuildsPageSize.Should().Be(10);
        settings.MinimizeToTray.Should().BeFalse();
        settings.FastFetch.Should().BeFalse();
    }

    [Fact]
    public void No_file_is_left_behind_when_every_setting_is_cleared()
    {
        var settings = new SettingsService(_dir) { SelectedMode = "SteamTools" };
        File.Exists(SettingsFile).Should().BeTrue();

        settings.SelectedMode = null;

        File.Exists(SettingsFile).Should().BeFalse();
    }

    [Fact]
    public void FixesPageSize_is_persisted()
    {
        // Regression: FixesPageSize was once missing from the "is everything empty?" check, so a user
        // whose only change was that setting had the whole file deleted on the next save.
        new SettingsService(_dir) { FixesPageSize = 96 };

        new SettingsService(_dir).FixesPageSize.Should().Be(96);
    }

    // ── SettingsService: corruption ───────────────────────────────────────────

    [Fact]
    public void A_corrupt_settings_file_is_preserved_not_overwritten()
    {
        File.WriteAllText(SettingsFile, "{ this is not json");

        var settings = new SettingsService(_dir);
        settings.SelectedMode = "SteamTools"; // triggers a save that must not clobber the original

        File.Exists(SettingsFile + ".corrupt").Should().BeTrue("the user's data must survive for recovery");
        File.ReadAllText(SettingsFile + ".corrupt").Should().Be("{ this is not json");
    }

    [Fact]
    public void A_corrupt_settings_file_falls_back_to_the_backup()
    {
        new SettingsService(_dir) { SelectedMode = "OpenSteamTools" };
        new SettingsService(_dir) { ManagePageSize = 12 }; // second save produces the .bak
        File.WriteAllText(SettingsFile, "{{{ truncated");

        new SettingsService(_dir).SelectedMode.Should().Be("OpenSteamTools");
    }

    [Fact]
    public void No_temp_file_is_left_behind_after_a_save()
    {
        new SettingsService(_dir) { SelectedMode = "SteamTools" };

        File.Exists(SettingsFile + ".tmp").Should().BeFalse("the temp file is renamed over the target");
    }

    // ── SettingsService: Hubcap key encryption ────────────────────────────────

    [Fact]
    public void Hubcap_key_round_trips_but_is_not_stored_in_plain_text()
    {
        const string key = "smm_" + "0123456789abcdef";

        new SettingsService(_dir) { HubcapApiKey = key };

        new SettingsService(_dir).HubcapApiKey.Should().Be(key);
        File.ReadAllText(SettingsFile).Should().NotContain(key,
            "the key authenticates paid downloads and settings.json is a readable roaming-profile file");
    }

    [Fact]
    public void A_plaintext_hubcap_key_from_an_older_build_is_migrated_on_load()
    {
        const string key = "smm_legacyplaintextkey";
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(new { HubcapApiKey = key }));

        // Construction performs the migration; the value must still be readable afterwards.
        new SettingsService(_dir).HubcapApiKey.Should().Be(key);

        File.ReadAllText(SettingsFile).Should().NotContain(key);
        new SettingsService(_dir).HubcapApiKey.Should().Be(key, "the migrated form must still decrypt");
    }

    [Fact]
    public void Clearing_the_hubcap_key_removes_it()
    {
        var settings = new SettingsService(_dir) { HubcapApiKey = "smm_something" };

        settings.HubcapApiKey = null;

        new SettingsService(_dir).HubcapApiKey.Should().BeNull();
    }

    // ── Concurrency ───────────────────────────────────────────────────────────

    [Fact]
    public void Concurrent_settings_writes_do_not_throw()
    {
        // Reproduces the original defect: several threads calling Save() at once raced on the shared temp
        // file and the File.Move. Without the lock this throws IOException from a property setter.
        var settings = new SettingsService(_dir);

        var act = () => Parallel.For(0, 200, i => settings.ManagePageSize = i % 50 + 1);

        act.Should().NotThrow();
        new SettingsService(_dir).ManagePageSize.Should().BeInRange(1, 50);
    }

    [Fact]
    public void Concurrent_cache_writes_do_not_throw()
    {
        var cache = new CacheService(_dir);

        var act = () => Parallel.For(0, 200, i =>
        {
            cache.SaveLoadedAppIds([i, i + 1]);
            _ = cache.GetLoadedAppIds();          // reading while others write must not throw either
        });

        act.Should().NotThrow();
    }

    [Fact]
    public void Cache_collection_getters_return_a_snapshot()
    {
        // The getters used to hand back the live List<T>, so a caller enumerating one while a background
        // writer swapped it produced "Collection was modified during enumeration".
        var cache = new CacheService(_dir);
        cache.SaveLoadedAppIds([1, 2, 3]);

        var first = cache.GetLoadedAppIds();
        cache.SaveLoadedAppIds([9, 9, 9]);

        // The snapshot taken before the second write must be unaffected by it.
        first.Should().Equal(1L, 2L, 3L);
    }

    // ── CacheService ──────────────────────────────────────────────────────────

    [Fact]
    public void Cache_survives_a_reload()
    {
        var cache = new CacheService(_dir);
        cache.SaveHardwareAppIds([480, 570], fetchedAtMs: 1_700_000_000_000);
        cache.OnboardingComplete = true;

        var reloaded = new CacheService(_dir);

        reloaded.GetHardwareAppIds().Should().Equal(480L, 570L);
        reloaded.OnboardingComplete.Should().BeTrue();
    }

    [Fact]
    public void Cache_deduplicates_loaded_app_ids()
    {
        var cache = new CacheService(_dir);

        cache.SaveLoadedAppIds([480, 480, 570]);

        cache.GetLoadedAppIds().Should().Equal(480, 570);
    }

    [Fact]
    public void Removing_a_loaded_app_id_persists()
    {
        var cache = new CacheService(_dir);
        cache.SaveLoadedAppIds([480, 570]);

        cache.RemoveLoadedAppId(480);

        new CacheService(_dir).GetLoadedAppIds().Should().Equal(570);
    }

    [Fact]
    public void A_corrupt_cache_file_falls_back_to_defaults_without_throwing()
    {
        File.WriteAllText(CacheFile, "not json at all");

        var act = () => new CacheService(_dir);

        act.Should().NotThrow();
        new CacheService(_dir).GetHardwareAppIds().Should().BeEmpty();
    }

    [Fact]
    public void Cache_write_is_atomic_and_leaves_no_temp_file()
    {
        var cache = new CacheService(_dir);

        cache.SaveHardwareAppIds([1, 2, 3], fetchedAtMs: 1_700_000_000_000);

        File.Exists(CacheFile + ".tmp").Should().BeFalse();
        new CacheService(_dir).GetHardwareAppIdsFetchedAt().Should().Be(1_700_000_000_000);
    }

    [Fact]
    public void Settings_and_cache_do_not_share_a_file()
    {
        var settings = new SettingsService(_dir) { SelectedMode = "SteamTools" };
        var cache = new CacheService(_dir);
        cache.OnboardingComplete = true;

        new SettingsService(_dir).SelectedMode.Should().Be("SteamTools");
        new CacheService(_dir).OnboardingComplete.Should().BeTrue();
    }
}
