using System.Text.Json;
using AwesomeAssertions;
using LuaToolsGui.Models;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Pins what the app takes from Hubcap's responses — and, just as deliberately, what it leaves behind.
///
/// <para>
/// The bodies below are the shapes the live API actually returned during the integration audit, not
/// invented fixtures, so a field renamed upstream shows up here as a failing test rather than as a
/// silently defaulted value.
/// </para>
/// </summary>
public class HubcapModelTests
{
    private static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Verbatim from the live <c>/api/v1/user/stats</c>, with the account identifiers replaced.</summary>
    private const string LiveStatsBody = """
        {"user_id":"000000000000000000","username":"someone","api_key_usage_count":0,
         "api_key_expires_at":"2026-08-24T22:17:22.453394","daily_usage":3,"daily_limit":25,
         "role_daily_limit":25,"custom_api_limit":null,"using_custom_api_limit":false,
         "auto_update_enabled":true,"can_make_requests":true,"timestamp":"2026-08-17T22:26:36.631537"}
        """;

    /// <summary>Verbatim from the live <c>/api/v1/status/730</c>.</summary>
    private const string LiveStatusBody = """
        {"app_id":"730","game_name":"Counter-Strike 2","status":"available","manifest_file_exists":true,
         "auto_update_enabled":true,"update_in_progress":false,"timestamp":"2026-08-17T22:26:36.859547",
         "file_size":2902742,"file_modified":"2026-08-12T19:50:56.089457","file_age_days":5.108,
         "needs_update":false,"update_reason":"manifest_current"}
        """;

    // ── Privacy ──────────────────────────────────────────────────────

    [Fact]
    public void Stats_does_not_expose_the_Discord_identity_the_API_sends()
    {
        // /user/stats returns the user's Discord user_id and username. The app has never had a use for
        // either, and binding them parked a durable identifier in a process-lifetime view-model property.
        // Not mapping them doesn't stop them arriving in the JSON — it stops the app keeping them.
        var props = typeof(HubcapStats).GetProperties().Select(p => p.Name).ToList();

        props.Should().NotContain("UserId");
        props.Should().NotContain("Username");
    }

    [Fact]
    public void Parsing_a_real_stats_response_ignores_the_identity_fields_without_failing()
    {
        // Unmapped members must not throw — the response carries several fields the app doesn't model.
        var stats = JsonSerializer.Deserialize<HubcapStats>(LiveStatsBody, Opts);

        stats.Should().NotBeNull();
        stats!.DailyUsage.Should().Be(3);
        stats.DailyLimit.Should().Be(25);
        stats.CanMakeRequests.Should().BeTrue();
        stats.ApiKeyExpiresAt.Should().Be("2026-08-24T22:17:22.453394");
    }

    // ── Newly modelled fields ────────────────────────────────────────

    [Fact]
    public void A_real_status_response_yields_the_fields_the_app_was_throwing_away()
    {
        var status = JsonSerializer.Deserialize<HubcapManifestStatus>(LiveStatusBody, Opts);

        status.Should().NotBeNull();
        status!.ManifestFileExists.Should().BeTrue();
        status.Status.Should().Be("available");
        status.GameName.Should().Be("Counter-Strike 2");
        status.FileSize.Should().Be(2_902_742);
        status.FileModified.Should().Be("2026-08-12T19:50:56.089457");
        status.NeedsUpdate.Should().BeFalse();
        status.UpdateReason.Should().Be("manifest_current");
    }

    [Fact]
    public void A_status_response_missing_the_optional_fields_still_parses()
    {
        // The endpoint is not contractually guaranteed to send them; absence must read as "unknown", not
        // as a parse failure that would take the whole availability check down with it.
        var status = JsonSerializer.Deserialize<HubcapManifestStatus>(
            """{"status":"available","manifest_file_exists":true}""", Opts);

        status.Should().NotBeNull();
        status!.ManifestFileExists.Should().BeTrue();
        status.GameName.Should().BeNull();
        status.FileSize.Should().BeNull();
        status.FileModified.Should().BeNull();
        status.UpdateReason.Should().BeNull();
        status.NeedsUpdate.Should().BeFalse();
    }

    [Fact]
    public void FileModified_stays_a_string_rather_than_a_reinterpreted_timestamp()
    {
        // The API sends no timezone offset. Parsing it into a DateTime here would silently reinterpret it
        // in whatever zone the machine happens to sit in, which is worse than handing back what was sent.
        typeof(HubcapManifestStatus).GetProperty(nameof(HubcapManifestStatus.FileModified))!
            .PropertyType.Should().Be<string>();
    }
}
