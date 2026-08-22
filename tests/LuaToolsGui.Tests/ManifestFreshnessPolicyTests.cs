using AwesomeAssertions;
using LuaToolsGui.Models;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// <see cref="ManifestFreshnessPolicy"/> — pure, no I/O. Fills the "KNOWN GAP" documented on
/// <see cref="HubcapManifestStatus"/>: before this, the app had no local record of what it had installed,
/// so it could only ever report "available", never "you have an older copy".
/// </summary>
public class ManifestFreshnessPolicyTests
{
    private static HubcapManifestStatus Status(string? fileModified, bool needsUpdate = false) => new()
    {
        Status = "available",
        ManifestFileExists = true,
        FileModified = fileModified,
        NeedsUpdate = needsUpdate,
    };

    // ── Happy path ──────────────────────────────────────────────────

    [Fact]
    public void Stale_when_hubcap_rebuilt_the_manifest_since_our_download()
    {
        bool stale = ManifestFreshnessPolicy.IsStale(
            installedFileModified: "2026-08-01T00:00:00.000000",
            latest: Status("2026-08-12T19:50:56.089457"));

        stale.Should().BeTrue();
    }

    [Fact]
    public void Not_stale_when_the_marker_matches_what_we_have()
    {
        bool stale = ManifestFreshnessPolicy.IsStale(
            installedFileModified: "2026-08-12T19:50:56.089457",
            latest: Status("2026-08-12T19:50:56.089457"));

        stale.Should().BeFalse();
    }

    // ── Hubcap's own verdict is authoritative ──────────────────────────

    [Fact]
    public void Hubcaps_needs_update_flag_wins_even_if_the_marker_looks_unchanged()
    {
        // A marker match while Hubcap itself says "needs_update" would mean OUR record is wrong, not that
        // the copy is current — trust the source of truth over a derived comparison.
        bool stale = ManifestFreshnessPolicy.IsStale(
            installedFileModified: "2026-08-12T19:50:56.089457",
            latest: Status("2026-08-12T19:50:56.089457", needsUpdate: true));

        stale.Should().BeTrue();
    }

    // ── Regression: the bug this policy exists to avoid repeating ──────

    [Fact]
    public void A_reinstall_that_only_changes_the_local_write_time_is_not_reported_stale()
    {
        // This is the exact trap the removed KNOWN GAP comment named: File.SetLastWriteTime marks the
        // moment of the WRITE, not of the manifest Hubcap built, and is reset by any unrelated re-install.
        // The policy never looks at a local timestamp at all — only the opaque `file_modified` token — so
        // a same-content reinstall (any local write time) still reads as current.
        bool stale = ManifestFreshnessPolicy.IsStale(
            installedFileModified: "2026-08-12T19:50:56.089457", // unaffected by any local reinstall
            latest: Status("2026-08-12T19:50:56.089457"));

        stale.Should().BeFalse();
    }

    // ── False positive: legitimate cases that must NOT be flagged ──────

    [Fact]
    public void Nothing_on_record_is_not_reported_stale()
    {
        // Never downloaded through a path that recorded it (or recorded before this tracking existed).
        // Claiming "outdated" here would be a guess — and sending a user to re-download a manifest that
        // may already be current is exactly the false positive this must avoid.
        bool stale = ManifestFreshnessPolicy.IsStale(
            installedFileModified: null,
            latest: Status("2026-08-12T19:50:56.089457"));

        stale.Should().BeFalse();
    }

    [Fact]
    public void A_response_with_no_marker_is_not_reported_stale()
    {
        // The field is optional on the wire (HubcapModelTests: "A_status_response_missing_the_optional_
        // fields_still_parses"). No marker to compare against means no basis to claim staleness.
        bool stale = ManifestFreshnessPolicy.IsStale(
            installedFileModified: "2026-08-12T19:50:56.089457",
            latest: Status(fileModified: null));

        stale.Should().BeFalse();
    }

    [Fact]
    public void Both_sides_missing_the_marker_is_not_reported_stale()
    {
        bool stale = ManifestFreshnessPolicy.IsStale(installedFileModified: null, latest: Status(null));

        stale.Should().BeFalse();
    }

    // ── The comparison is an opaque token, not a parsed date ────────────

    [Fact]
    public void Differently_formatted_but_equal_strings_are_still_compared_ordinally()
    {
        // Deliberately NOT a date-math comparison (see the class doc: the API sends no timezone offset).
        // A byte-for-byte match is "current"; anything else — even a value that would parse to the same
        // instant — is treated as changed, because this policy never parses it at all.
        bool stale = ManifestFreshnessPolicy.IsStale(
            installedFileModified: "2026-08-12T19:50:56.089457",
            latest: Status("2026-08-12T19:50:56.0894570")); // trailing zero — same instant, different string

        stale.Should().BeTrue();
    }
}
