using System.Globalization;
using AwesomeAssertions;
using LuaToolsGui.Services;
using LuaToolsGui.Services.Downloads;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// The size/speed/ETA figures a download row shows. All pure: no dispatcher, no I/O, injected clock.
/// </summary>
public class DownloadMetricsTests
{
    private static DownloadJob AnyJob(DownloadKind kind = DownloadKind.Manifest, long appId = 730) =>
        new(kind, "k", appId, "Game", "Source", null,
            (_, _, _) => throw new NotSupportedException(),
            (_, _, _) => throw new NotSupportedException());

    /// <summary>An item on a clock the test drives, so a 3-second rate window costs no real seconds.</summary>
    private static (DownloadItem Item, Action<double> Advance) OnAClock(DownloadKind kind = DownloadKind.Manifest)
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var item = new DownloadItem(AnyJob(kind)) { UtcNow = () => now };
        return (item, seconds => now = now.AddSeconds(seconds));
    }

    // ── ByteSize.Rate / Duration ─────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void An_unmeasurable_rate_renders_as_nothing_rather_than_as_zero(double rate)
    {
        // An empty string lets the label hide itself. "0 B/s" would claim the download had stalled during
        // the opening half-second, before the sampling window has anything honest to say.
        ByteSize.Rate(rate).Should().BeEmpty();
    }

    [Fact]
    public void A_rate_is_a_size_per_second_in_the_users_own_number_format()
    {
        ByteSize.Rate(4_400_000, CultureInfo.GetCultureInfo("en-US")).Should().Be("4.2 MB/s");
        ByteSize.Rate(4_400_000, CultureInfo.GetCultureInfo("pt-BR")).Should().Be("4,2 MB/s");
    }

    [Theory]
    [InlineData(8, "8s")]
    [InlineData(72, "1m 12s")]
    [InlineData(7500, "2h 5m")]
    public void A_remaining_duration_is_rendered_at_the_coarsest_useful_unit(int seconds, string expected)
    {
        ByteSize.Duration(TimeSpan.FromSeconds(seconds)).Should().Be(expected);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(90000)] // a day or more
    public void An_absurd_or_finished_duration_renders_as_nothing(int seconds)
    {
        // An ETA past a day only ever comes from a rate sampled during a stall. Saying nothing beats
        // telling the user their download has 17 hours left.
        ByteSize.Duration(TimeSpan.FromSeconds(seconds)).Should().BeEmpty();
    }

    [Fact]
    public void A_sub_second_remainder_still_rounds_up_to_one_second()
    {
        ByteSize.Duration(TimeSpan.FromMilliseconds(200)).Should().Be("1s");
    }

    // ── Rate sampling ────────────────────────────────────────────────

    [Fact]
    public void The_first_reading_publishes_no_rate_because_there_is_nothing_to_measure_against()
    {
        var (item, _) = OnAClock();

        item.ApplySample(1024, 4096);

        item.BytesRead.Should().Be(1024);
        item.BytesPerSecond.Should().Be(0);
        item.Eta.Should().BeNull();
    }

    [Fact]
    public void Two_readings_a_second_apart_give_the_rate_and_the_eta()
    {
        var (item, advance) = OnAClock();

        item.ApplySample(0, 4_000_000);
        advance(2);
        item.ApplySample(2_000_000, 4_000_000);

        item.BytesPerSecond.Should().BeApproximately(1_000_000, 1);
        item.Eta!.Value.TotalSeconds.Should().BeApproximately(2, 0.01);
    }

    [Fact]
    public void Two_readings_milliseconds_apart_do_not_publish_a_spike()
    {
        var (item, advance) = OnAClock(DownloadKind.Depot);

        item.ApplySample(0, 10_000_000_000);
        advance(0.01); // depot progress arrives once per completed FILE, so bursts are normal
        item.ApplySample(8_000_000_000, 10_000_000_000);

        // Dividing an 8 GB jump by 10ms is what used to make the speed read in GB/s.
        item.BytesPerSecond.Should().Be(0);
    }

    [Fact]
    public void A_long_silent_stretch_still_measures_against_the_last_reading_rather_than_freezing()
    {
        var (item, advance) = OnAClock(DownloadKind.Depot);

        item.ApplySample(0, 20_000_000_000);
        advance(200); // a single multi-GB file can report nothing for minutes
        item.ApplySample(8_300_000_000, 20_000_000_000);

        // The window keeps at least two samples, so a gap longer than the window does not trim everything
        // down to one reading and leave the speed stuck at a stale value forever.
        item.BytesPerSecond.Should().BeApproximately(41_500_000, 1);
    }

    [Fact]
    public void A_completed_transfer_reports_no_eta()
    {
        var (item, advance) = OnAClock();

        item.ApplySample(0, 1000);
        advance(1);
        item.ApplySample(1000, 1000);

        item.Eta.Should().BeNull();
        item.Percent.Should().Be(100);
    }

    [Fact]
    public void Resetting_metrics_clears_the_window_so_a_retry_does_not_inherit_the_last_attempt()
    {
        var (item, advance) = OnAClock();
        item.ApplySample(0, 4000);
        advance(1);
        item.ApplySample(2000, 4000);
        item.BytesPerSecond.Should().BeGreaterThan(0);

        item.ResetMetrics();

        item.BytesRead.Should().Be(0);
        item.TotalBytes.Should().BeNull();
        item.BytesPerSecond.Should().Be(0);
        item.Eta.Should().BeNull();
    }

    // ── Labels ───────────────────────────────────────────────────────

    [Fact]
    public void An_unknown_total_length_shows_what_has_arrived_and_an_indeterminate_bar()
    {
        var (item, _) = OnAClock();
        item.ApplySample(2048, null);

        item.IsIndeterminate.Should().BeTrue();
        item.Percent.Should().Be(0);
        item.SizeLabel.Should().Be(ByteSize.Format(2048));
        item.SizeLabel.Should().NotContain("of");
    }

    [Fact]
    public void A_known_total_length_shows_both_halves()
    {
        var (item, _) = OnAClock();
        item.ApplySample(1024, 4096);

        item.IsIndeterminate.Should().BeFalse();
        item.Percent.Should().Be(25);
        item.SizeLabel.Should().Contain(ByteSize.Format(1024)).And.Contain(ByteSize.Format(4096));
    }

    [Fact]
    public void Nothing_measured_yet_renders_no_size_label_at_all()
    {
        var (item, _) = OnAClock();
        item.SizeLabel.Should().BeEmpty();
    }

    [Fact]
    public void Rate_and_eta_labels_only_show_while_bytes_are_actually_moving()
    {
        var (item, advance) = OnAClock();
        item.ApplySample(0, 4_000_000);
        advance(2);
        item.ApplySample(2_000_000, 4_000_000);

        item.Status = DownloadStatus.Downloading;
        item.RateLabel.Should().NotBeEmpty();
        item.EtaLabel.Should().NotBeEmpty();

        // An installing or paused row keeps the last speed it saw internally, but showing it would say the
        // download is still moving when it is not.
        item.Status = DownloadStatus.Installing;
        item.RateLabel.Should().BeEmpty();
        item.EtaLabel.Should().BeEmpty();
    }

    // ── State predicates the view binds ──────────────────────────────

    [Theory]
    [InlineData(DownloadStatus.Queued, true, false)]
    [InlineData(DownloadStatus.Downloading, true, false)]
    [InlineData(DownloadStatus.AwaitingConfirmation, true, false)]
    [InlineData(DownloadStatus.Installing, true, false)]
    [InlineData(DownloadStatus.Paused, true, false)]
    [InlineData(DownloadStatus.Verifying, true, false)]
    [InlineData(DownloadStatus.Completed, false, false)]
    [InlineData(DownloadStatus.Failed, false, true)]
    [InlineData(DownloadStatus.Cancelled, false, true)]
    public void Active_and_retryable_follow_the_status(DownloadStatus status, bool active, bool retry)
    {
        var (item, _) = OnAClock();
        item.Status = status;

        // Paused and Verifying count as active on purpose: the row keeps its Cancel button, is never
        // auto-dismissed, and is recorded as cancelled if the app shuts down while it sits there.
        item.IsActive.Should().Be(active);
        item.CanCancel.Should().Be(active);
        item.CanRemove.Should().Be(!active);
        item.CanRetry.Should().Be(retry);
    }

    [Theory]
    [InlineData(DownloadKind.Depot, DownloadStatus.Downloading, true)]
    [InlineData(DownloadKind.Depot, DownloadStatus.Verifying, true)]
    [InlineData(DownloadKind.Depot, DownloadStatus.Queued, false)]
    [InlineData(DownloadKind.Manifest, DownloadStatus.Downloading, false)]
    public void Only_a_running_depot_download_can_be_paused(DownloadKind kind, DownloadStatus status, bool canPause)
    {
        var (item, _) = OnAClock(kind);
        item.Status = status;

        // A manifest download has nothing on disk to come back to, so offering Pause would silently mean
        // Cancel. A depot's bytes are already written and the tool can re-validate them.
        item.CanPause.Should().Be(canPause);
    }

    [Fact]
    public void Copying_the_app_id_is_only_offered_when_there_is_one()
    {
        new DownloadItem(AnyJob(appId: 730)).CanCopyAppId.Should().BeTrue();
        new DownloadItem(AnyJob(appId: 0)).CanCopyAppId.Should().BeFalse();
    }

    [Fact]
    public void A_depot_job_can_be_revealed_from_the_moment_it_is_created()
    {
        var job = AnyJob(DownloadKind.Depot) with { OutputPath = @"C:\Depots\730" };
        var item = new DownloadItem(job);

        // The output folder is known up front, so "Show in folder" works while the download is running —
        // not only once something has been installed.
        item.CanShowInFolder.Should().BeTrue();
        item.RevealPath.Should().Be(@"C:\Depots\730");
    }

    [Fact]
    public void An_installed_path_takes_over_from_the_output_folder_but_a_blank_one_does_not()
    {
        var item = new DownloadItem(AnyJob(DownloadKind.Depot) with { OutputPath = @"C:\Depots\730" });

        item.RecordInstalledPath("   ");
        item.RevealPath.Should().Be(@"C:\Depots\730"); // blanking the fallback would offer a dead path

        item.RecordInstalledPath(@"C:\Steam\config\stplug-in\730.lua");
        item.RevealPath.Should().Be(@"C:\Steam\config\stplug-in\730.lua");
    }
}
