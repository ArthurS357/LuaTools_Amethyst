using AwesomeAssertions;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>A burst of dispatcher exceptions must surface as ONE toast, not a queue of identical ones.</summary>
public class CrashNoticeThrottleTests
{
    [Fact]
    public void A_burst_shows_one_notice_and_the_next_burst_shows_another()
    {
        // Far-future clock: the throttle is process-wide, so stay clear of anything a real crash wrote.
        var t0 = new DateTime(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        LuaToolsGui.App.ClaimCrashNotice(t0).Should().BeTrue();
        LuaToolsGui.App.ClaimCrashNotice(t0.AddMilliseconds(100)).Should().BeFalse();
        LuaToolsGui.App.ClaimCrashNotice(t0.AddSeconds(9)).Should().BeFalse();

        LuaToolsGui.App.ClaimCrashNotice(t0 + LuaToolsGui.App.CrashNoticeQuiet).Should().BeTrue();
    }
}
