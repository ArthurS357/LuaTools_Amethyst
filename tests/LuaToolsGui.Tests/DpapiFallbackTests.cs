using System.IO;
using System.Security.Cryptography;
using AwesomeAssertions;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Covers what <see cref="SettingsService"/> does when DPAPI is unavailable: FAIL CLOSED (1.8.3, M1).
///
/// <para>
/// The Hubcap key is normally stored DPAPI-protected under a <c>dpapi:</c> prefix. If
/// <c>ProtectedData.Protect</c> throws, the key is kept in memory for the current session only and is never
/// written to <c>settings.json</c> in clear text (the pre-1.8.3 fallback did, permanently and silently).
/// The warning is logged and surfaced through <see cref="SettingsService.HubcapKeyIsSessionOnly"/>.
/// </para>
///
/// <para>These tests drive the path through the constructor's encryptor seam; DPAPI works on the test machine.</para>
/// </summary>
public sealed class DpapiFallbackTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "luatools-tests", Guid.NewGuid().ToString("N"));

    public DpapiFallbackTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private string SettingsFile => Path.Combine(_dir, "settings.json");

    private string DiskText => File.Exists(SettingsFile) ? File.ReadAllText(SettingsFile) : "";

    /// <summary>Shaped like a real key so a leak would be unmistakable in the assertions below.</summary>
    private const string Key = "smm_" + "5f3a9c2e8b1d47a6f0c3e9b5d2a8f14c7e6b0d39a5c82f4e1b7d6a03c9e5f28b"
                                      + "1a4d7c0e3b6f9285d1c4a7e0b3f6982c5a8d1e4b7f0c3a69";

    // ── The warning ──────────────────────────────────────────────────

    [Fact]
    public void An_unavailable_DPAPI_is_reported()
    {
        var log = new List<string>();
        var settings = Failing(log);

        settings.HubcapApiKey = Key;

        string all = string.Join("\n", log);
        all.Should().Contain("DPAPI").And.Contain("memory").And.Contain("NOT written");
        settings.HubcapKeyIsSessionOnly.Should().BeTrue("the UI reads this to show the notice");
    }

    [Fact]
    public void The_warning_never_contains_the_key()
    {
        var log = new List<string>();
        Failing(log).HubcapApiKey = Key;

        string all = string.Join("\n", log);
        all.Should().NotContain(Key);
        all.Should().NotContain("smm_");
    }

    [Fact]
    public void The_warning_names_the_failure_type_so_the_cause_is_diagnosable()
    {
        var log = new List<string>();
        Failing(log, new CryptographicException("Key not valid for use in specified state.")).HubcapApiKey = Key;

        string.Join("\n", log).Should().Contain(nameof(CryptographicException));
    }

    [Fact]
    public void A_working_DPAPI_logs_nothing_and_is_not_session_only()
    {
        var log = new List<string>();
        var settings = new SettingsService(_dir, log: log.Add);

        settings.HubcapApiKey = Key;

        log.Should().BeEmpty();
        settings.HubcapKeyIsSessionOnly.Should().BeFalse();
        DiskText.Should().NotContain(Key).And.Contain("dpapi:");
        new SettingsService(_dir).HubcapApiKey.Should().Be(Key, "a healthy DPAPI round-trips across restarts");
    }

    // ── Fail-closed contract ─────────────────────────────────────────

    [Fact]
    public void The_key_is_never_written_to_disk_in_clear_text_when_DPAPI_fails()
    {
        Failing([]).HubcapApiKey = Key;

        DiskText.Should().NotContain(Key);
        DiskText.Should().NotContain("smm_");
    }

    [Fact]
    public void The_key_stays_usable_in_memory_for_the_current_session()
    {
        var settings = Failing([]);

        settings.HubcapApiKey = Key;
        settings.HubcapApiKey = settings.HubcapApiKey; // an unrelated Save()-style round trip keeps it

        settings.HubcapApiKey.Should().Be(Key);
        settings.DnsMode = DnsMode.Never;              // Save() with other settings must not persist or drop it
        settings.HubcapApiKey.Should().Be(Key);
        DiskText.Should().NotContain(Key);
    }

    [Fact]
    public void A_restart_does_not_have_the_session_only_key()
    {
        Failing([]).HubcapApiKey = Key;

        var reloaded = new SettingsService(_dir);

        reloaded.HubcapApiKey.Should().BeNull();
        reloaded.HubcapKeyIsSessionOnly.Should().BeFalse();
    }

    [Fact]
    public void Clearing_the_key_drops_the_session_copy()
    {
        var settings = Failing([]);
        settings.HubcapApiKey = Key;

        settings.HubcapApiKey = null;

        settings.HubcapApiKey.Should().BeNull();
        settings.HubcapKeyIsSessionOnly.Should().BeFalse();
    }

    // ── Legacy plaintext migration ───────────────────────────────────

    [Fact]
    public void A_legacy_plaintext_key_is_migrated_when_DPAPI_is_healthy()
    {
        File.WriteAllText(SettingsFile, $"{{\"HubcapApiKey\":\"{Key}\"}}");

        var settings = new SettingsService(_dir);

        settings.HubcapApiKey.Should().Be(Key);
        settings.HubcapKeyIsSessionOnly.Should().BeFalse();
        DiskText.Should().NotContain(Key).And.Contain("dpapi:");
    }

    [Fact]
    public void A_legacy_plaintext_key_is_scrubbed_from_disk_and_kept_in_memory_when_DPAPI_fails()
    {
        File.WriteAllText(SettingsFile, $"{{\"HubcapApiKey\":\"{Key}\"}}");
        var log = new List<string>();

        var settings = Failing(log);

        settings.HubcapApiKey.Should().Be(Key);
        settings.HubcapKeyIsSessionOnly.Should().BeTrue();
        DiskText.Should().NotContain(Key);
        log.Should().NotBeEmpty();
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private SettingsService Failing(List<string> log, Exception? failure = null) =>
        new(_dir,
            encrypt: _ => throw (failure ?? new CryptographicException("DPAPI unavailable.")),
            log: log.Add);
}

/// <summary>Log lines mask the user profile (1.8.3, #2) and Auto clears the DNS failure flag (#24).</summary>
public sealed class Release183HardeningTests
{
    [Fact]
    public void AppLog_masks_the_user_profile_folder()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string marker = "profile-" + Guid.NewGuid().ToString("N");

        AppLog.Log($"{marker} {profile}\\AppData\\x.txt");

        string line = TestLogRedirect.ReadLogLines().Single(l => l.Contains(marker));
        line.Should().Contain("%USERPROFILE%\\AppData\\x.txt").And.NotContain(profile);
    }

    [Fact]
    public void Returning_DnsMode_to_Auto_clears_the_session_dns_failure()
    {
        string dir = Path.Combine(Path.GetTempPath(), "luatools-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var shared = DnsFallbackSession.Shared;
        var (mode, failed) = (shared.Mode, shared.IsDnsFailed);
        try
        {
            var settings = new SettingsService(dir, publishGlobal: true);
            settings.DnsMode = DnsMode.Never;
            shared.MarkDnsFailed();

            settings.DnsMode = DnsMode.Always;
            shared.IsDnsFailed.Should().BeTrue("only a return to Auto is a clean slate");

            settings.DnsMode = DnsMode.Auto;
            shared.IsDnsFailed.Should().BeFalse();
            shared.Mode.Should().Be(DnsMode.Auto);
        }
        finally
        {
            shared.Mode = mode;
            if (failed) shared.MarkDnsFailed(); else shared.Reset();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
