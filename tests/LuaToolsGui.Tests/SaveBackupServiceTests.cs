using System.IO;
using System.IO.Compression;
using AwesomeAssertions;
using LuaToolsGui.Services;
using LuaToolsGui.Services.Backups;
using Xunit;

namespace LuaToolsGui.Tests;

public sealed class SaveBackupServiceTests : IDisposable
{
    private const long App = 730;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "luatools-tests", Guid.NewGuid().ToString("N"));
    private readonly string _steam;
    private readonly string _backups;
    private readonly SaveBackupService _svc;

    public SaveBackupServiceTests()
    {
        _steam = Path.Combine(_dir, "steam");
        _backups = Path.Combine(_dir, "backups");
        Directory.CreateDirectory(Path.Combine(_steam, "userdata"));
        _svc = new SaveBackupService(() => _steam, _backups);
    }

    public void Dispose()
    {
        // Detach junctions first: a recursive delete must never walk into the folder they point at.
        if (Directory.Exists(_dir))
            foreach (var j in Directory.EnumerateDirectories(_dir, "*", SearchOption.AllDirectories)
                         .Where(DirectoryJunction.Exists).ToList())
                DirectoryJunction.Remove(j);
        try { Directory.Delete(_dir, true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string Save(string account, long app, string rel, string content)
    {
        string path = Path.Combine(_steam, "userdata", account, app.ToString(), rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>A zip named like one of our backups, with exactly the given entries.</summary>
    private string CraftZip(long app, params (string Name, string Body)[] entries)
    {
        Directory.CreateDirectory(_backups);
        string path = Path.Combine(_backups, $"{app}_20260101-000000.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, body) in entries)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open());
            w.Write(body);
        }
        return path;
    }

    [Fact]
    public async Task Backup_zips_each_account_folder_of_the_game_and_nothing_else()
    {
        Save("111", App, @"remote\slot1.sav", "a");
        Save("222", App, "config.vdf", "b");
        Save("0", App, "anon.sav", "never");          // Steam's anonymous placeholder
        Save("333", 440, "other.sav", "other game");

        var r = await _svc.BackupSavesAsync(App);

        r.Outcome.Should().Be(SaveBackupOutcome.Ok);
        r.Files.Should().Be(2);
        using var zip = ZipFile.OpenRead(r.Path!);
        zip.Entries.Select(e => e.FullName).Should().BeEquivalentTo("111/730/remote/slot1.sav", "222/730/config.vdf");
    }

    [Fact]
    public async Task Backup_with_no_game_folder_says_so_and_writes_nothing()
    {
        var r = await _svc.BackupSavesAsync(App);

        r.Outcome.Should().Be(SaveBackupOutcome.NoSaves);
        Directory.Exists(_backups).Should().BeFalse();
    }

    [Fact]
    public async Task Backup_without_a_steam_install_reports_it()
    {
        var svc = new SaveBackupService(() => null, _backups);

        (await svc.BackupSavesAsync(App)).Outcome.Should().Be(SaveBackupOutcome.SteamNotFound);
    }

    [Fact]
    public async Task Restore_puts_the_files_back_and_keeps_a_copy_of_what_it_overwrote()
    {
        string file = Save("111", App, @"remote\slot1.sav", "good");
        var made = await _svc.BackupSavesAsync(App);
        File.WriteAllText(file, "corrupted");

        var r = await _svc.RestoreBackupAsync(made.Path!, App);

        r.Outcome.Should().Be(SaveBackupOutcome.Ok);
        File.ReadAllText(file).Should().Be("good");
        var all = _svc.ListBackups(App);
        all.Should().HaveCount(2, "the corrupted state was zipped before it was overwritten");
        using var safety = ZipFile.OpenRead(all.Single(e => e.FullPath != made.Path).FullPath);
        new StreamReader(safety.Entries.Single().Open()).ReadToEnd().Should().Be("corrupted");
    }

    [Theory]
    [InlineData("111/730/../../../escaped.txt")]       // classic zip-slip
    [InlineData("111/730/sub/../../../escaped.txt")]
    [InlineData(@"111\730\..\..\..\escaped.txt")]       // backslash separators
    [InlineData("C:/Windows/escaped.txt")]              // drive-qualified
    [InlineData("/escaped.txt")]                        // rooted
    [InlineData("//server/share/escaped.txt")]          // UNC
    [InlineData("111/730/save.dat:evil")]               // NTFS alternate data stream
    [InlineData("111/731/other-game.sav")]              // a different game
    [InlineData("0/730/anon.sav")]                      // the anonymous account
    [InlineData("abc/730/x.sav")]                       // not an account id
    [InlineData("111/730")]                             // no file part
    [InlineData("111/730/.")]                           // resolves to the folder itself
    public async Task Restore_refuses_the_whole_archive_when_one_entry_is_unsafe(string badName)
    {
        string fine = Save("111", App, "keep.sav", "original");
        string zip = CraftZip(App, ("111/730/keep.sav", "overwritten?"), (badName, "evil"));

        var r = await _svc.RestoreBackupAsync(zip, App);

        r.Outcome.Should().Be(SaveBackupOutcome.Refused);
        File.ReadAllText(fine).Should().Be("original", "validation runs before any entry is extracted");
        File.Exists(Path.Combine(_dir, "escaped.txt")).Should().BeFalse();
        File.Exists(Path.Combine(_steam, "escaped.txt")).Should().BeFalse();
        _svc.ListBackups(App).Should().HaveCount(1, "no safety copy for a refused restore");
    }

    [Fact]
    public async Task Restore_refuses_to_write_through_a_junction_inside_the_game_folder()
    {
        string outside = Path.Combine(_dir, "outside");
        Directory.CreateDirectory(outside);
        Save("111", App, "keep.sav", "x");
        string link = Path.Combine(_steam, "userdata", "111", "730", "remote");
        DirectoryJunction.Create(link, outside).Should().BeTrue();
        string zip = CraftZip(App, ("111/730/remote/slot1.sav", "evil"));

        var r = await _svc.RestoreBackupAsync(zip, App);

        r.Outcome.Should().Be(SaveBackupOutcome.Refused);
        Directory.GetFiles(outside).Should().BeEmpty();
    }

    [Fact]
    public async Task Restore_and_delete_only_touch_files_named_like_our_backups_for_that_game()
    {
        Save("111", App, "a.sav", "a");
        string outside = Path.Combine(_dir, "730_20260101-000000.zip");   // right name, wrong folder
        File.WriteAllText(outside, "not a zip");
        Directory.CreateDirectory(_backups);
        string otherGame = Path.Combine(_backups, "731_20260101-000000.zip");
        File.WriteAllText(otherGame, "x");
        string stray = Path.Combine(_backups, "notes.txt");
        File.WriteAllText(stray, "x");

        (await _svc.RestoreBackupAsync(outside, App)).Outcome.Should().Be(SaveBackupOutcome.Refused);
        (await _svc.RestoreBackupAsync(otherGame, App)).Outcome.Should().Be(SaveBackupOutcome.Refused);
        _svc.DeleteBackup(outside, App).Should().BeFalse();
        _svc.DeleteBackup(otherGame, App).Should().BeFalse();
        _svc.DeleteBackup(stray, App).Should().BeFalse();
        File.Exists(outside).Should().BeTrue();
        File.Exists(otherGame).Should().BeTrue();
        File.Exists(stray).Should().BeTrue();
    }

    [Fact]
    public async Task Restore_without_room_for_the_whole_archive_touches_nothing()
    {
        string live = Save("111", App, "slot1.sav", "current");
        string zip = CraftZip(App, ("111/730/slot1.sav", "restored"), ("111/730/slot2.sav", "restored"));
        var full = new SaveBackupService(() => _steam, _backups, freeBytes: _ => 1);

        var r = await full.RestoreBackupAsync(zip, App);

        r.Outcome.Should().Be(SaveBackupOutcome.Failed);
        File.ReadAllText(live).Should().Be("current");
        File.Exists(Path.Combine(Path.GetDirectoryName(live)!, "slot2.sav")).Should().BeFalse();
        full.ListBackups(App).Should().ContainSingle("no safety copy is taken for a restore that will not run");
    }

    [Fact]
    public void Required_space_is_the_raw_size_plus_ten_percent_plus_50_MB()
    {
        const long MB = 1024 * 1024;

        SaveBackupService.RequiredFreeBytes(0).Should().Be(50 * MB);
        SaveBackupService.RequiredFreeBytes(100 * MB).Should().Be(160 * MB);
    }

    [Fact]
    public async Task Backup_asks_the_backups_drive_and_refuses_one_byte_short_of_the_margin()
    {
        Save("111", App, "slot1.sav", new string('x', 1000));
        long required = SaveBackupService.RequiredFreeBytes(1000);
        string? asked = null;
        var tight = new SaveBackupService(() => _steam, _backups, freeBytes: dir => { asked = dir; return required - 1; });

        var r = await tight.BackupSavesAsync(App);

        r.Outcome.Should().Be(SaveBackupOutcome.NoSpace);
        r.Bytes.Should().Be(required);
        asked.Should().Be(Path.GetFullPath(_backups), "the zip lands in the backups folder, not under userdata");
        tight.ListBackups(App).Should().BeEmpty();
    }

    [Fact]
    public async Task Backup_runs_when_the_drive_holds_exactly_the_margin()
    {
        Save("111", App, "slot1.sav", new string('x', 1000));
        long required = SaveBackupService.RequiredFreeBytes(1000);
        var exact = new SaveBackupService(() => _steam, _backups, freeBytes: _ => required);

        (await exact.BackupSavesAsync(App)).Outcome.Should().Be(SaveBackupOutcome.Ok);
    }

    [Fact]
    public async Task Restore_does_not_start_when_the_safety_copy_has_no_room()
    {
        string live = Save("111", App, "slot1.sav", "current");
        string zip = CraftZip(App, ("111/730/slot1.sav", "restored"));
        // Room for the extraction, none for the 50 MB margin the safety copy needs.
        var tight = new SaveBackupService(() => _steam, _backups, freeBytes: _ => 1_000_000);

        (await tight.RestoreBackupAsync(zip, App)).Outcome.Should().Be(SaveBackupOutcome.Failed);

        File.ReadAllText(live).Should().Be("current");
    }

    [Fact]
    public async Task Has_saves_or_backups_covers_a_restore_after_the_folder_was_deleted()
    {
        _svc.HasSavesOrBackups(App).Should().BeFalse();

        string live = Save("111", App, "slot1.sav", "a");
        _svc.HasSavesOrBackups(App).Should().BeTrue();

        (await _svc.BackupSavesAsync(App)).Outcome.Should().Be(SaveBackupOutcome.Ok);
        Directory.Delete(Path.GetDirectoryName(live)!, recursive: true);
        _svc.HasSavesOrBackups(App).Should().BeTrue();
        _svc.HasSavesOrBackups(440).Should().BeFalse();
    }

    [Fact]
    public async Task List_is_per_game_newest_first_and_delete_removes_the_file()
    {
        Save("111", 1, "a.sav", "a");
        Save("111", 10, "a.sav", "b");   // "10_…" must not show up under app 1
        var one = await _svc.BackupSavesAsync(1);
        await _svc.BackupSavesAsync(10);
        File.SetLastWriteTimeUtc(one.Path!, DateTime.UtcNow.AddDays(-1));
        var second = await _svc.BackupSavesAsync(1);

        _svc.ListBackups(1).Select(e => e.FullPath).Should().Equal(second.Path, one.Path);
        _svc.ListBackups(10).Should().HaveCount(1);

        _svc.DeleteBackup(one.Path!, 1).Should().BeTrue();
        File.Exists(one.Path).Should().BeFalse();
        _svc.ListBackups(1).Should().HaveCount(1);
    }
}

/// <summary>The Steam-already-running decision, with the process lookup and the dialog faked out.</summary>
public class StartupGuardTests
{
    [Fact]
    public async Task A_steam_that_is_not_running_is_never_asked_about()
    {
        bool asked = false;

        var outcome = await StartupGuard.RunAsync(() => false, () => { asked = true; return Task.FromResult(StartupGuardChoice.Exit); });

        outcome.Should().Be(StartupGuardOutcome.SteamNotRunning);
        asked.Should().BeFalse();
    }

    [Theory]
    [InlineData(StartupGuardChoice.Restart, StartupGuardOutcome.Restart)]
    [InlineData(StartupGuardChoice.Exit, StartupGuardOutcome.Exit)]
    [InlineData(StartupGuardChoice.ContinueAnyway, StartupGuardOutcome.ContinueAnyway)]
    public async Task A_running_steam_follows_the_users_choice(StartupGuardChoice choice, StartupGuardOutcome expected)
    {
        (await StartupGuard.RunAsync(() => true, () => Task.FromResult(choice))).Should().Be(expected);
    }

    [Fact]
    public async Task The_users_choice_is_logged()
    {
        await StartupGuard.RunAsync(() => true, () => Task.FromResult(StartupGuardChoice.ContinueAnyway));

        TestLogRedirect.ReadLog().Should().Contain("startup guard: Steam already running, user chose ContinueAnyway");
    }

    [Fact]
    public async Task An_unrecognised_choice_quits_rather_than_continuing_into_a_broken_hook()
    {
        (await StartupGuard.RunAsync(() => true, () => Task.FromResult((StartupGuardChoice)99)))
            .Should().Be(StartupGuardOutcome.Exit);
    }
}
