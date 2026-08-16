using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Tests for the disclosure rule behind <see cref="InsecureTransportNotice"/>, and for parsing the
/// <c>InsecureMetadataNotice</c> setting.
///
/// <para>
/// The notice exists because one call — the source-availability lookup — is still cleartext, and a privacy
/// cost the user cannot see is one they cannot weigh. What has to stay true: it fires by DEFAULT, the
/// default does not become wallpaper (the lookup runs on essentially every page of search results),
/// "always" really means every call for people auditing the app's traffic, and a malformed setting fails
/// toward MORE disclosure rather than silently switching it off.
/// </para>
/// </summary>
public class InsecureTransportNoticeTests
{
    // ── Disclosure cadence ──────────────────────────────────────────────────

    [Fact]
    public void Once_NotifiesTheFirstTime()
    {
        Assert.True(InsecureTransportNotice.ShouldNotify(
            InsecureMetadataNoticeMode.Once, alreadyWarnedForKey: false));
    }

    [Fact]
    public void Once_DoesNotNotifyAgainForTheSameEndpoint()
    {
        Assert.False(InsecureTransportNotice.ShouldNotify(
            InsecureMetadataNoticeMode.Once, alreadyWarnedForKey: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Always_NotifiesEveryCall_RegardlessOfHistory(bool alreadyWarned)
    {
        Assert.True(InsecureTransportNotice.ShouldNotify(
            InsecureMetadataNoticeMode.Always, alreadyWarned));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Off_StaysSilent(bool alreadyWarned)
    {
        // Only the disclosure is suppressed; the request is unchanged either way. Stopping the call
        // itself is a different setting (EnableSourceAvailabilityChecks).
        Assert.False(InsecureTransportNotice.ShouldNotify(
            InsecureMetadataNoticeMode.Off, alreadyWarned));
    }

    // ── Setting parsing ─────────────────────────────────────────────────────

    [Fact]
    public void DefaultsToOnce_WhenUnset()
    {
        Assert.Equal(InsecureMetadataNoticeMode.Once,
            InsecureMetadataNoticeModeExtensions.ParseNoticeMode(null));
    }

    [Theory]
    [InlineData("always", InsecureMetadataNoticeMode.Always)]
    [InlineData("ALWAYS", InsecureMetadataNoticeMode.Always)]
    [InlineData("  every  ", InsecureMetadataNoticeMode.Always)]
    [InlineData("off", InsecureMetadataNoticeMode.Off)]
    [InlineData("never", InsecureMetadataNoticeMode.Off)]
    [InlineData("once", InsecureMetadataNoticeMode.Once)]
    public void ParsesRecognisedValues_CaseAndWhitespaceInsensitively(string raw, InsecureMetadataNoticeMode expected)
    {
        Assert.Equal(expected, InsecureMetadataNoticeModeExtensions.ParseNoticeMode(raw));
    }

    [Theory]
    [InlineData("nver")]     // typo for "never"
    [InlineData("false")]    // wrong type, plausible mistake
    [InlineData("disabled")]
    [InlineData("")]
    public void UnrecognisedValue_FallsBackToOnce_NotOff(string raw)
    {
        // Failing toward MORE disclosure is the safe direction: a typo must never silently switch a
        // privacy notice off.
        Assert.Equal(InsecureMetadataNoticeMode.Once,
            InsecureMetadataNoticeModeExtensions.ParseNoticeMode(raw));
    }

    // ── Legacy setting migration ────────────────────────────────────────────

    [Fact]
    public void LegacyWarnFalse_StillMeansOff_WhenNewSettingUnset()
    {
        // Anyone who set "WarnOnInsecureMetadata": false before the mode setting existed should not
        // suddenly start getting notices again.
        Assert.Equal(InsecureMetadataNoticeMode.Off,
            InsecureMetadataNoticeModeExtensions.ParseNoticeMode(null, legacyWarnEnabled: false));
    }

    [Fact]
    public void LegacyWarnTrue_MeansOnce()
    {
        Assert.Equal(InsecureMetadataNoticeMode.Once,
            InsecureMetadataNoticeModeExtensions.ParseNoticeMode(null, legacyWarnEnabled: true));
    }

    [Fact]
    public void NewSettingWins_OverLegacy()
    {
        Assert.Equal(InsecureMetadataNoticeMode.Always,
            InsecureMetadataNoticeModeExtensions.ParseNoticeMode("always", legacyWarnEnabled: false));
    }
}
