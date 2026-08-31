using System.IO;
using System.Text.Json;
using AwesomeAssertions;
using LuaToolsGui.Services;
using LuaToolsGui.Services.Downloads;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// What the download history keeps, where it keeps it, and what it must never keep.
/// </summary>
/// <remarks>
/// The history is the only part of a download that outlives the session, so it is also the only part that
/// can leak one. It goes into cache.json — app bookkeeping — rather than settings.json, whose already
/// distributed shape must not change.
/// </remarks>
public sealed class DownloadHistoryTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "luatools_history_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static DownloadHistoryRecord Record(
        string id = "a", string title = "Test Game", string? message = "installed",
        string status = nameof(DownloadStatus.Completed), long appId = 730, string? reveal = null) =>
        new(id, nameof(DownloadKind.Manifest), appId, title, "Test Source", 4096, status, message,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), reveal);

    [Fact]
    public void History_round_trips_through_cache_json()
    {
        new CacheService(_dir).SaveDownloadHistory([Record(reveal: @"C:\Steam\730.lua")]);

        var reloaded = new CacheService(_dir).GetDownloadHistory();

        reloaded.Should().ContainSingle();
        reloaded[0].Title.Should().Be("Test Game");
        reloaded[0].Bytes.Should().Be(4096);
        reloaded[0].RevealPath.Should().Be(@"C:\Steam\730.lua");
    }

    [Fact]
    public void History_lands_in_cache_json_and_not_in_settings_json()
    {
        new CacheService(_dir).SaveDownloadHistory([Record()]);

        File.Exists(Path.Combine(_dir, "cache.json")).Should().BeTrue();
        File.Exists(Path.Combine(_dir, "settings.json")).Should().BeFalse();
    }

    [Fact]
    public void A_cache_written_before_this_feature_existed_still_loads_with_an_empty_history()
    {
        Directory.CreateDirectory(_dir);
        // No DownloadHistory property at all — exactly what an existing install's cache.json looks like.
        File.WriteAllText(Path.Combine(_dir, "cache.json"),
            """{ "DepotDownloaderVersion": "v1.2.3", "OnboardingComplete": true }""");

        var cache = new CacheService(_dir);

        cache.GetDownloadHistory().Should().BeEmpty();
        cache.DepotDownloaderVersion.Should().Be("v1.2.3"); // and nothing else was disturbed
    }

    [Fact]
    public void Clearing_the_history_removes_it_from_disk()
    {
        var cache = new CacheService(_dir);
        cache.SaveDownloadHistory([Record()]);

        cache.SaveDownloadHistory([]);

        new CacheService(_dir).GetDownloadHistory().Should().BeEmpty();
    }

    [Fact]
    public void Removing_one_row_leaves_the_others_on_disk()
    {
        var cache = new CacheService(_dir);
        cache.SaveDownloadHistory([Record(id: "a"), Record(id: "b"), Record(id: "c")]);

        cache.SaveDownloadHistory([Record(id: "a"), Record(id: "c")]);

        new CacheService(_dir).GetDownloadHistory().Select(r => r.Id).Should().Equal("a", "c");
    }

    [Fact]
    public void A_failure_message_is_sanitized_before_it_is_written_to_disk()
    {
        // A failed job's message can be an exception built from an HTTP body (AuthService throws
        // $"Token exchange failed ({code}): {body}"), and cache.json is plain text in the roaming profile.
        // The crash log already gets this treatment; so does anything persisted here.
        var leaky = Record(message: "Token exchange failed: {\"access_token\":\"eyJhbGciOiJIUzI1NiJ9.abc.def\"}");

        new CacheService(_dir).SaveDownloadHistory([leaky]);

        string raw = File.ReadAllText(Path.Combine(_dir, "cache.json"));
        raw.Should().NotContain("eyJhbGciOiJIUzI1NiJ9.abc.def");
        new CacheService(_dir).GetDownloadHistory()[0].Message
            .Should().NotContain("eyJhbGciOiJIUzI1NiJ9.abc.def");
    }

    [Fact]
    public void Sanitizing_a_record_leaves_the_fields_that_are_not_free_text_alone()
    {
        var sanitized = Record(reveal: @"C:\Steam\config\stplug-in\730.lua").Sanitized();

        // The reveal path is the one filesystem path kept, because "Show in folder" has nothing to open
        // without it. It is composed by the app, not by a server.
        sanitized.RevealPath.Should().Be(@"C:\Steam\config\stplug-in\730.lua");
        sanitized.AppId.Should().Be(730);
        sanitized.Bytes.Should().Be(4096);
        sanitized.Status.Should().Be(nameof(DownloadStatus.Completed));
    }

    [Fact]
    public void A_record_with_no_message_stays_null_rather_than_becoming_empty_text()
    {
        Record(message: null).Sanitized().Message.Should().BeNull();
    }

    // ── Entry projection ─────────────────────────────────────────────

    [Fact]
    public void An_unrecognised_status_reads_as_completed_rather_than_throwing()
    {
        // A cache.json written by a newer build could name a status this one has never heard of. Falling
        // back beats failing to load the whole history.
        new DownloadHistoryEntry(Record(status: "SomethingNewer")).Status
            .Should().Be(DownloadStatus.Completed);
    }

    [Fact]
    public void A_failed_entry_is_marked_so_the_row_can_colour_itself()
    {
        var failed = new DownloadHistoryEntry(Record(status: nameof(DownloadStatus.Failed)));
        var cancelled = new DownloadHistoryEntry(Record(status: nameof(DownloadStatus.Cancelled)));

        failed.Failed.Should().BeTrue();
        cancelled.Failed.Should().BeFalse(); // a cancel is a choice, not an error
    }

    [Fact]
    public void A_zero_byte_entry_shows_no_size_rather_than_zero()
    {
        var entry = new DownloadHistoryEntry(Record() with { Bytes = 0 });

        entry.SizeLabel.Should().BeEmpty();
    }

    [Fact]
    public void Show_in_folder_is_only_offered_when_a_path_was_recorded()
    {
        new DownloadHistoryEntry(Record(reveal: null)).CanShowInFolder.Should().BeFalse();
        new DownloadHistoryEntry(Record(reveal: @"C:\x")).CanShowInFolder.Should().BeTrue();
    }

    [Fact]
    public void A_record_serializes_to_the_flat_shape_the_cache_expects()
    {
        // The record is deliberately primitives only: a DownloadJob holds delegates and cannot be
        // serialized, so history describes what happened rather than anything resumable.
        string json = JsonSerializer.Serialize(Record(reveal: @"C:\x"));
        var back = JsonSerializer.Deserialize<DownloadHistoryRecord>(json)!;

        back.Should().Be(Record(reveal: @"C:\x") with { CompletedAtMs = back.CompletedAtMs });
    }
}
