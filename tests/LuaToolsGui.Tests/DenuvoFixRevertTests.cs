using System.IO;
using System.IO.Compression;
using System.Text;
using AwesomeAssertions;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Pins apply-and-revert for a Denuvo fix: what gets backed up, what gets restored, and — more
/// importantly — what the revert refuses to touch.
///
/// <para>
/// A revert rewrites and deletes files inside the user's game folder, so the interesting cases are all
/// the ones where it must NOT act: a file something else replaced after the fix wrote it, a record entry
/// pointing outside the game folder, and an "added" entry with no hash to verify against.
/// </para>
/// </summary>
public class DenuvoFixRevertTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "luatools-tests", Guid.NewGuid().ToString("N"));

    private readonly string _game;
    private readonly DenuvoFixService _fixes = new();

    private const long AppId = 730;
    private const string FixId = "online-fix:5e01e852";

    public DenuvoFixRevertTests()
    {
        _game = Path.Combine(_dir, "game");
        Directory.CreateDirectory(_game);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Build a fix archive from entry-name → contents.</summary>
    private string Archive(params (string Name, string Body)[] entries)
    {
        string path = Path.Combine(_dir, $"fix-{Guid.NewGuid():N}.zip");
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var (name, body) in entries)
        {
            using var s = zip.CreateEntry(name).Open();
            s.Write(Encoding.UTF8.GetBytes(body));
        }
        return path;
    }

    private string GamePath(string rel) => Path.Combine(_game, rel.Replace('/', Path.DirectorySeparatorChar));

    private void Existing(string rel, string body)
    {
        string p = GamePath(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, body);
    }

    private FixApplyResult Apply(string zip) => _fixes.Apply(zip, AppId, FixId, _game);
    private FixRevertResult Revert() => _fixes.Revert(FixId, _game);

    // ── Apply ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Applying_extracts_the_archive_and_leaves_a_record_that_makes_it_revertable()
    {
        Existing("steam_api64.dll", "original");

        var result = Apply(Archive(("steam_api64.dll", "patched")));

        result.Ok.Should().BeTrue();
        File.ReadAllText(GamePath("steam_api64.dll")).Should().Be("patched");
        DenuvoFixService.IsApplied(_game, FixId).Should().BeTrue();
    }

    [Fact]
    public void A_fix_id_carrying_path_characters_still_produces_a_usable_record_name()
    {
        // "online-fix:5e01e852" has a colon, illegal in a Windows path segment. The raw id stays in the
        // record; only the filename is sanitised.
        Apply(Archive(("a.txt", "x"))).Ok.Should().BeTrue();

        DenuvoFixService.IsApplied(_game, FixId).Should().BeTrue();
    }

    [Fact]
    public void An_entry_naming_its_way_out_of_the_game_folder_is_refused_not_written()
    {
        var result = Apply(Archive((@"..\..\evil.dll", "pwned")));

        result.Failed.Should().Be(1);
        File.Exists(Path.Combine(_dir, "evil.dll")).Should().BeFalse();
    }

    [Fact]
    public void Backups_are_keyed_by_the_full_relative_path_not_just_the_file_name()
    {
        // Fix archives routinely ship the same name in several folders. Keying by name alone collapsed them
        // onto one .bak, so a revert restored one file with the other's contents — or, once the .bak had
        // been consumed, silently restored nothing.
        Existing("bin/steam_api64.dll", "original-bin");
        Existing("lib/steam_api64.dll", "original-lib");

        Apply(Archive(("bin/steam_api64.dll", "patched-bin"), ("lib/steam_api64.dll", "patched-lib")))
            .Ok.Should().BeTrue();
        Revert().Ok.Should().BeTrue();

        File.ReadAllText(GamePath("bin/steam_api64.dll")).Should().Be("original-bin");
        File.ReadAllText(GamePath("lib/steam_api64.dll")).Should().Be("original-lib");
    }

    // ── Revert ────────────────────────────────────────────────────────────────

    [Fact]
    public void Reverting_restores_a_modified_file_and_deletes_one_the_fix_added()
    {
        Existing("steam_api64.dll", "original");

        Apply(Archive(("steam_api64.dll", "patched"), ("extra.dll", "new")));
        var result = Revert();

        result.Ok.Should().BeTrue();
        result.Restored.Should().Be(1);
        result.Deleted.Should().Be(1);
        File.ReadAllText(GamePath("steam_api64.dll")).Should().Be("original");
        File.Exists(GamePath("extra.dll")).Should().BeFalse();
    }

    [Fact]
    public void A_completed_revert_clears_the_record_and_the_backups_so_the_fix_reads_as_unapplied()
    {
        Existing("a.dll", "original");
        Apply(Archive(("a.dll", "patched")));

        Revert().Ok.Should().BeTrue();

        DenuvoFixService.IsApplied(_game, FixId).Should().BeFalse();
        Directory.EnumerateFileSystemEntries(Path.Combine(_game, ".luatools-fix"))
            .Should().BeEmpty();
    }

    [Fact]
    public void Reverting_something_that_was_never_applied_says_so_instead_of_failing_loudly()
    {
        Revert().Outcome.Should().Be(FixRevertOutcome.NoRecord);
    }

    // ── Refusals: the part that protects the user's files ─────────────────────

    [Fact]
    public void A_file_replaced_after_the_fix_wrote_it_is_reported_as_a_conflict_not_overwritten()
    {
        // The usual cause is a second fix layered on top, whose own backup holds our version. Restoring
        // ours over it would throw that away. A game update or a hand-edit lands here too.
        Existing("steam_api64.dll", "original");
        Apply(Archive(("steam_api64.dll", "patched")));
        File.WriteAllText(GamePath("steam_api64.dll"), "someone else's version");

        var result = Revert();

        result.Outcome.Should().Be(FixRevertOutcome.Conflict);
        result.ConflictFile.Should().Be("steam_api64.dll");
        File.ReadAllText(GamePath("steam_api64.dll")).Should().Be("someone else's version");
        DenuvoFixService.IsApplied(_game, FixId).Should().BeTrue(); // still revertable later
    }

    [Fact]
    public void An_added_file_someone_else_replaced_is_left_alone_rather_than_deleted()
    {
        Apply(Archive(("extra.dll", "new")));
        File.WriteAllText(GamePath("extra.dll"), "theirs now");

        Revert().Outcome.Should().Be(FixRevertOutcome.Conflict);
        File.Exists(GamePath("extra.dll")).Should().BeTrue();
    }

    [Fact]
    public void A_revert_retried_after_an_earlier_one_restored_the_file_is_not_treated_as_a_conflict()
    {
        // HashBefore is the second accepted hash for exactly this: a partial revert that already put the
        // original back must not report the restored file as foreign when the user retries.
        Existing("a.dll", "original");
        Apply(Archive(("a.dll", "patched")));
        File.WriteAllText(GamePath("a.dll"), "original"); // as if a previous attempt had restored it

        Revert().Ok.Should().BeTrue();
        File.ReadAllText(GamePath("a.dll")).Should().Be("original");
    }

    [Fact]
    public void A_record_entry_pointing_outside_the_game_folder_is_refused_never_deleted()
    {
        // The record lives in a user-writable folder and a fix archive can plant one. This branch DELETES,
        // so the path gets the same containment check the extract does.
        string outside = Path.Combine(_dir, "precious.txt");
        File.WriteAllText(outside, "keep me");

        Apply(Archive(("a.dll", "x")));
        PlantRecord($$"""
        {
          "appId": {{AppId}},
          "fixId": "{{FixId}}",
          "appliedAt": "2026-01-01T00:00:00Z",
          "files": [
            { "relativePath": "../precious.txt", "action": "added", "hashAfter": "deadbeef" }
          ]
        }
        """);

        var result = Revert();

        result.Outcome.Should().Be(FixRevertOutcome.Partial);
        File.Exists(outside).Should().BeTrue();
    }

    [Fact]
    public void An_added_entry_with_no_hash_is_refused_rather_than_deleted_unverified()
    {
        // AMETHYST HARDENING, stricter than upstream. FileHash.Matches answers "true" to a null expected
        // hash ("nothing to check"), which on a DELETE branch means deleting unverified. Every record this
        // app writes carries a hash, so requiring one costs nothing real and closes the planted-record case.
        Existing("keep.dll", "a real game file");

        Apply(Archive(("a.dll", "x")));
        PlantRecord($$"""
        {
          "appId": {{AppId}},
          "fixId": "{{FixId}}",
          "appliedAt": "2026-01-01T00:00:00Z",
          "files": [
            { "relativePath": "keep.dll", "action": "added" }
          ]
        }
        """);

        var result = Revert();

        result.Outcome.Should().Be(FixRevertOutcome.Partial);
        File.Exists(GamePath("keep.dll")).Should().BeTrue();
    }

    [Fact]
    public void A_modified_entry_whose_backup_is_missing_is_an_error_not_a_silent_success()
    {
        // The original is gone for good and the file is still patched. Reporting success here would tell
        // the user their game is clean when it is not.
        Existing("a.dll", "original");
        Apply(Archive(("a.dll", "patched")));

        string backups = Path.Combine(_game, ".luatools-fix", DenuvoFixServiceKey);
        Directory.Delete(backups, recursive: true);

        var result = Revert();

        result.Outcome.Should().Be(FixRevertOutcome.Partial);
        result.Errors.Should().Be(1);
        DenuvoFixService.IsApplied(_game, FixId).Should().BeTrue(); // record kept, so it stays retryable
    }

    // ── Record plumbing ───────────────────────────────────────────────────────

    /// <summary>The sanitised folder/file key the service derives from <see cref="FixId"/>.</summary>
    private static string DenuvoFixServiceKey =>
        string.Concat(FixId.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));

    /// <summary>Overwrite the record with a hand-built one, to drive cases apply cannot produce.</summary>
    private void PlantRecord(string json) =>
        File.WriteAllText(
            Path.Combine(_game, ".luatools-fix", $"{DenuvoFixServiceKey}.json"), json);
}
