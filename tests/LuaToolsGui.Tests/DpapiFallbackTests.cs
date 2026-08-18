using System.IO;
using System.Security.Cryptography;
using AwesomeAssertions;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Covers what <see cref="SettingsService"/> does when DPAPI is unavailable.
///
/// <para>
/// The Hubcap key is normally stored DPAPI-protected under a <c>dpapi:</c> prefix. If
/// <c>ProtectedData.Protect</c> throws — a broken or roaming profile — the service keeps the key rather
/// than losing it, which is the right trade: an unreadable key means the feature simply stops working for
/// a user who did nothing wrong. But it took that trade in complete silence, and because
/// <c>Unprotect</c> reads a missing prefix as a pre-migration plaintext key, the downgrade was permanent
/// and left no trace anywhere. Nobody could find out it had happened.
/// </para>
///
/// <para>
/// These tests drive that path through the constructor's encryptor seam, since DPAPI works perfectly well
/// on the machine running them.
/// </para>
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

    /// <summary>Shaped like a real key so a leak would be unmistakable in the assertions below.</summary>
    private const string Key = "smm_" + "5f3a9c2e8b1d47a6f0c3e9b5d2a8f14c7e6b0d39a5c82f4e1b7d6a03c9e5f28b"
                                      + "1a4d7c0e3b6f9285d1c4a7e0b3f6982c5a8d1e4b7f0c3a69";

    // ── The warning ──────────────────────────────────────────────────

    [Fact]
    public void An_unavailable_DPAPI_is_reported_instead_of_silently_downgrading()
    {
        var log = new List<string>();
        var settings = Failing(log);

        settings.HubcapApiKey = Key;

        log.Should().NotBeEmpty("a silent downgrade to plaintext is the whole problem being fixed");
        string all = string.Join("\n", log);
        all.Should().Contain("DPAPI");
        all.Should().Contain("UNENCRYPTED");
    }

    [Fact]
    public void The_warning_never_contains_the_key()
    {
        var log = new List<string>();
        var settings = Failing(log);

        settings.HubcapApiKey = Key;

        string all = string.Join("\n", log);
        all.Should().NotContain(Key, "a log written BECAUSE a secret is unprotected must not carry it");
        all.Should().NotContain("smm_", "not even a fragment, and not a redacted-looking prefix");
    }

    [Fact]
    public void The_warning_names_the_failure_type_so_the_cause_is_diagnosable()
    {
        var log = new List<string>();
        var settings = Failing(log, new CryptographicException("Key not valid for use in specified state."));

        settings.HubcapApiKey = Key;

        string.Join("\n", log).Should().Contain(nameof(CryptographicException));
    }

    [Fact]
    public void A_working_DPAPI_logs_nothing()
    {
        // The warning must stay rare enough to mean something. Real DPAPI here, no seam.
        var log = new List<string>();
        var settings = new SettingsService(_dir, log: log.Add);

        settings.HubcapApiKey = Key;

        log.Should().BeEmpty();
        File.ReadAllText(SettingsFile).Should().NotContain(Key);
    }

    // ── The trade itself, pinned ─────────────────────────────────────

    [Fact]
    public void The_key_is_kept_and_still_readable_rather_than_lost()
    {
        // Losing the key would be worse than storing it unprotected: the user did nothing wrong and would
        // have to go find their key again. This asserts the trade deliberately, so it can't be "fixed"
        // into a data-losing failure without a test turning red.
        var settings = Failing([]);

        settings.HubcapApiKey = Key;

        settings.HubcapApiKey.Should().Be(Key);
        new SettingsService(_dir).HubcapApiKey.Should().Be(Key, "and it survives a reload");
    }

    [Fact]
    public void The_stored_value_is_unprefixed_which_is_what_makes_the_downgrade_permanent()
    {
        // Documents the mechanism the warning exists to surface: without "dpapi:", a later load treats the
        // value as a legacy plaintext key and re-protects it — but only if DPAPI has since recovered.
        var settings = Failing([]);

        settings.HubcapApiKey = Key;

        string onDisk = File.ReadAllText(SettingsFile);
        onDisk.Should().Contain(Key);
        onDisk.Should().NotContain("dpapi:");
    }

    [Fact]
    public void A_recovered_DPAPI_re_protects_the_downgraded_key_on_the_next_load()
    {
        // The upside of the unprefixed form: the existing migration picks it up once DPAPI works again.
        Failing([]).HubcapApiKey = Key;
        File.ReadAllText(SettingsFile).Should().Contain(Key);

        var recovered = new SettingsService(_dir); // real DPAPI

        recovered.HubcapApiKey.Should().Be(Key);
        File.ReadAllText(SettingsFile).Should().NotContain(Key).And.Contain("dpapi:");
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private SettingsService Failing(List<string> log, Exception? failure = null) =>
        new(_dir,
            encrypt: _ => throw (failure ?? new CryptographicException("DPAPI unavailable.")),
            log: log.Add);
}
