using System.IO;
using System.IO.Compression;
using AwesomeAssertions;
using LuaToolsGui.Services;
using LuaToolsGui.Services.Backups;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Saves that live outside Steam's <c>userdata</c> (Documents\Saved Games\Hades, AppData\…). Detection is by
/// exact path, the zip keeps one <c>saves/&lt;key&gt;/</c> folder per location, and a restore writes only to
/// folders it computes itself — never to a path read from the archive.
/// </summary>
public sealed class SaveLocationTests : IDisposable
{
    private const long App = 1145360;
    private const string Game = "Hades";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "luatools-tests", Guid.NewGuid().ToString("N"));
    private readonly string _steam;
    private readonly string _backups;
    private readonly string _docs;
    private readonly string _profile;
    private readonly SaveRoots _roots;
    private readonly List<string> _custom = [];

    public SaveLocationTests()
    {
        _steam = Path.Combine(_dir, "steam");
        _backups = Path.Combine(_dir, "backups");
        _profile = Path.Combine(_dir, "profile");
        _docs = Path.Combine(_profile, "Documents");
        Directory.CreateDirectory(Path.Combine(_steam, "userdata"));
        Directory.CreateDirectory(_docs);
        _roots = new SaveRoots(_docs, _profile, Path.Combine(_profile, "AppData", "Local"), Path.Combine(_profile, "AppData", "Roaming"));
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

    private SaveBackupService Service() => new(() => _steam, _backups, roots: _roots,
        customFolders: _ => [.. _custom], addCustomFolder: (_, path) => { _custom.Add(path); return true; });

    private string Put(string path, string content = "save")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private string SavedGames(string rel = "Profile1.sav") => Path.Combine(_docs, "Saved Games", Game, rel);
    private string LocalApp(string rel = "settings.ini") => Path.Combine(_profile, "AppData", "Local", Game, rel);
    private string Userdata(string rel = "remote/cloud.sav") => Path.Combine(_steam, "userdata", "111", App.ToString(), rel);

    private string CraftZip(params (string Name, string Body)[] entries)
    {
        Directory.CreateDirectory(_backups);
        string path = Path.Combine(_backups, $"{App}_20260101-000000.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, body) in entries)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open());
            w.Write(body);
        }
        return path;
    }

    // ── Detection ─────────────────────────────────────────────────────

    [Fact]
    public void Finds_the_exact_folders_the_table_lists()
    {
        Put(SavedGames());
        Put(Path.Combine(_profile, "Saved Games", Game, "legacy.sav"));
        Put(LocalApp());
        Put(Path.Combine(_profile, "AppData", "LocalLow", Game, "low.sav"));
        Put(Path.Combine(_profile, "AppData", "Roaming", Game, "roam.sav"));
        Put(Path.Combine(_docs, Game, "doc.sav"));
        Put(Path.Combine(_docs, "My Games", Game, "mine.sav"));
        Put(Userdata());

        var found = Service().DetectSaveLocations(Game, App, _steam);

        found.Select(l => l.Kind).Should().BeEquivalentTo(
        [
            SaveLocationKind.Userdata, SaveLocationKind.Documents, SaveLocationKind.MyGames, SaveLocationKind.SavedGamesDocuments,
            SaveLocationKind.SavedGames, SaveLocationKind.LocalAppData, SaveLocationKind.LocalLow, SaveLocationKind.RoamingAppData,
        ]);
        found.Should().OnlyContain(l => l.Exists && l.FileCount == 1 && l.SizeBytes == 4);
        found.Single(l => l.Kind == SaveLocationKind.SavedGamesDocuments).Key.Should().Be("Documents_SavedGames_Hades");
        found.Single(l => l.Kind == SaveLocationKind.SavedGamesDocuments).DisplayName.Should().Be(@"Documents\Saved Games\Hades");
        found.Single(l => l.Kind == SaveLocationKind.LocalAppData).Key.Should().Be("AppData_Local_Hades");
    }

    [Fact]
    public void A_folder_that_only_looks_like_a_game_is_not_a_save()
    {
        Put(Path.Combine(_docs, "Projetos", "notes.txt"));                  // somebody's own folder
        Put(Path.Combine(_docs, "Hades Wallpapers", "img.png"));            // contains the name, is not the name
        Directory.CreateDirectory(Path.Combine(_docs, "Saved Games", Game)); // the right folder, but empty
        Put(Path.Combine(_profile, "AppData", "Local", "Microsoft", "x.dat"));

        Service().DetectSaveLocations(Game, App, _steam).Should().BeEmpty();
        // A game whose name matches a system folder must not drag that folder in.
        Service().DetectSaveLocations("Microsoft", App, _steam).Should().BeEmpty();
    }

    [Fact]
    public void Detection_never_follows_a_junction()
    {
        string outside = Path.Combine(_dir, "outside");
        Put(Path.Combine(outside, "secret.sav"));
        Directory.CreateDirectory(Path.Combine(_docs, "Saved Games"));
        DirectoryJunction.Create(Path.Combine(_docs, "Saved Games", Game), outside).Should().BeTrue();

        Service().DetectSaveLocations(Game, App, _steam).Should().BeEmpty("the game folder itself is a link");
    }

    [Fact]
    public void Detection_never_follows_a_junction_above_the_game_folder()
    {
        string outside = Path.Combine(_dir, "outside");
        Put(Path.Combine(outside, Game, "secret.sav"));
        DirectoryJunction.Create(Path.Combine(_docs, "Saved Games"), outside).Should().BeTrue();

        Service().DetectSaveLocations(Game, App, _steam).Should().BeEmpty("'Saved Games' is a link, so what is under it is not in Documents");
    }

    [Fact]
    public void A_registered_folder_is_detected_and_an_unregistered_one_is_not()
    {
        string exotic = Put(Path.Combine(_dir, "Exotic", "Slots", "a.sav"));
        Service().DetectSaveLocations("Obscure Game", App, _steam).Should().BeEmpty();

        _custom.Add(Path.GetDirectoryName(Path.GetDirectoryName(exotic))!);

        var found = Service().DetectSaveLocations("Obscure Game", App, _steam).Should().ContainSingle().Subject;
        found.Kind.Should().Be(SaveLocationKind.Custom);
        found.Key.Should().StartWith("Custom_Exotic_");
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"relative\path")]
    [InlineData("")]
    public void A_drive_root_or_a_relative_path_cannot_be_registered(string path) =>
        SaveLocationCatalog.IsAcceptableCustomFolder(path).Should().BeFalse();

    [Fact]
    public void The_windows_folder_cannot_be_registered() =>
        SaveLocationCatalog.IsAcceptableCustomFolder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32")).Should().BeFalse();

    // ── Backup ────────────────────────────────────────────────────────

    [Fact]
    public async Task One_zip_holds_a_saves_subfolder_per_location()
    {
        Put(Userdata(), "steam");
        Put(SavedGames(), "docs");

        var r = await Service().BackupSavesAsync(App, Game);

        r.Outcome.Should().Be(SaveBackupOutcome.Ok);
        r.Files.Should().Be(2);
        using var zip = ZipFile.OpenRead(r.Path!);
        zip.Entries.Select(e => e.FullName).Should().BeEquivalentTo(
            "saves/Userdata_111_1145360/remote/cloud.sav", "saves/Documents_SavedGames_Hades/Profile1.sav");
    }

    [Fact]
    public async Task Only_the_ticked_locations_go_into_the_zip()
    {
        Put(SavedGames());
        Put(LocalApp());
        var svc = Service();
        var chosen = svc.DetectSaveLocations(Game, App, _steam).Where(l => l.Kind == SaveLocationKind.LocalAppData).ToList();

        var r = await svc.BackupSavesAsync(App, Game, chosen);

        using var zip = ZipFile.OpenRead(r.Path!);
        zip.Entries.Select(e => e.FullName).Should().ContainSingle().Which.Should().Be("saves/AppData_Local_Hades/settings.ini");
    }

    [Fact]
    public async Task A_hand_built_location_cannot_aim_the_backup_at_another_folder()
    {
        Put(Path.Combine(_dir, "private", "diary.txt"));
        var forged = new SaveLocation(SaveLocationKind.Documents, "Documents_Hades", Path.Combine(_dir, "private"),
            "x", true, 1, 1, _dir);

        var r = await Service().BackupSavesAsync(App, Game, [forged]);

        r.Outcome.Should().Be(SaveBackupOutcome.NoSaves, "the key is looked up again and the path in the record is ignored");
        Directory.Exists(_backups).Should().BeFalse();
    }

    [Fact]
    public async Task A_game_with_saves_only_in_documents_needs_no_steam_install()
    {
        Put(SavedGames());
        var svc = new SaveBackupService(() => null, _backups, roots: _roots);

        (await svc.BackupSavesAsync(App, Game)).Outcome.Should().Be(SaveBackupOutcome.Ok);
    }

    // ── Restore ───────────────────────────────────────────────────────

    [Fact]
    public async Task Restore_puts_each_subfolder_back_where_it_came_from()
    {
        Put(Userdata(), "steam");
        Put(SavedGames(), "docs");
        Put(LocalApp(), "local");
        var svc = Service();
        var zip = (await svc.BackupSavesAsync(App, Game)).Path!;
        File.WriteAllText(SavedGames(), "changed");
        File.Delete(LocalApp());
        File.Delete(Userdata());

        var r = await svc.RestoreBackupAsync(zip, App, Game);

        r.Outcome.Should().Be(SaveBackupOutcome.Ok);
        r.Files.Should().Be(3);
        File.ReadAllText(Userdata()).Should().Be("steam");
        File.ReadAllText(SavedGames()).Should().Be("docs");
        File.ReadAllText(LocalApp()).Should().Be("local");
    }

    [Fact]
    public async Task Restore_takes_a_safety_copy_of_the_locations_it_overwrites()
    {
        Put(SavedGames(), "old");
        var svc = Service();
        var zip = (await svc.BackupSavesAsync(App, Game)).Path!;
        File.WriteAllText(SavedGames(), "newer");

        (await svc.RestoreBackupAsync(zip, App, Game)).Outcome.Should().Be(SaveBackupOutcome.Ok);

        svc.ListBackups(App).Should().HaveCount(2, "the saves that were about to be overwritten were zipped first");
    }

    [Fact]
    public async Task Restore_into_a_wiped_folder_recreates_it()
    {
        Put(SavedGames(), "docs");
        var svc = Service();
        var zip = (await svc.BackupSavesAsync(App, Game)).Path!;
        Directory.Delete(Path.Combine(_docs, "Saved Games"), recursive: true);

        (await svc.RestoreBackupAsync(zip, App, Game)).Outcome.Should().Be(SaveBackupOutcome.Ok);

        File.ReadAllText(SavedGames()).Should().Be("docs");
    }

    [Fact]
    public async Task A_registered_folder_is_backed_up_and_restored()
    {
        string slot = Put(Path.Combine(_dir, "Exotic", "Slots", "a.sav"), "exotic");
        _custom.Add(Path.GetDirectoryName(slot)!);
        var svc = Service();
        var zip = (await svc.BackupSavesAsync(App, "Obscure Game")).Path!;
        File.Delete(slot);

        (await svc.RestoreBackupAsync(zip, App, "Obscure Game")).Outcome.Should().Be(SaveBackupOutcome.Ok);

        File.ReadAllText(slot).Should().Be("exotic");
    }

    // ── Restore security ──────────────────────────────────────────────

    [Theory]
    [InlineData("saves/Documents_SavedGames_Hades/../../evil.txt")]
    [InlineData(@"saves\Documents_SavedGames_Hades\..\..\evil.txt")]
    [InlineData("saves/Documents_SavedGames_Hades/C:/evil.txt")]
    [InlineData("saves/Documents_SavedGames_Hades//evil.txt")]
    [InlineData("saves/Documents_SavedGames_Hades/save.dat:stream")]
    [InlineData("saves/../evil.txt")]
    [InlineData("saves/..%5Cevil/x.txt")]
    public async Task A_traversal_out_of_the_allowed_root_refuses_the_whole_archive(string evil)
    {
        string fine = Put(SavedGames(), "current");
        var zip = CraftZip(("saves/Documents_SavedGames_Hades/Profile1.sav", "restored"), (evil, "pwn"));

        var r = await Service().RestoreBackupAsync(zip, App, Game);

        r.Outcome.Should().Be(SaveBackupOutcome.Refused);
        File.ReadAllText(fine).Should().Be("current", "validation runs over every entry before anything is extracted");
        File.Exists(Path.Combine(_docs, "evil.txt")).Should().BeFalse();
        File.Exists(Path.Combine(_dir, "evil.txt")).Should().BeFalse();
    }

    [Theory]
    [InlineData("saves/Documents_SavedGames_OtherGame/a.sav")]   // another game's folder
    [InlineData("saves/Userdata_111_440/a.sav")]                 // another game's userdata
    [InlineData("saves/Userdata_0_1145360/a.sav")]               // Steam's anonymous placeholder
    [InlineData("saves/Custom_Exotic_DEADBEEF/a.sav")]           // a custom folder nobody registered
    [InlineData("saves/Windows/System32/a.dll")]                 // a folder that is not a location at all
    [InlineData("111/1145360/a.sav")]                            // userdata layout mixed into a saves/ archive
    public async Task A_folder_that_is_not_an_allowed_location_of_this_game_is_refused(string entry)
    {
        var zip = CraftZip(("saves/Documents_SavedGames_Hades/ok.sav", "ok"), (entry, "x"));

        (await Service().RestoreBackupAsync(zip, App, Game)).Outcome.Should().Be(SaveBackupOutcome.Refused);

        File.Exists(SavedGames("ok.sav")).Should().BeFalse();
    }

    [Fact]
    public async Task Restore_refuses_to_write_through_a_junction_below_a_known_root()
    {
        string outside = Path.Combine(_dir, "outside");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(Path.Combine(_docs, "Saved Games", Game));
        DirectoryJunction.Create(Path.Combine(_docs, "Saved Games", Game, "Slots"), outside).Should().BeTrue();
        var zip = CraftZip(("saves/Documents_SavedGames_Hades/Slots/a.sav", "pwn"));

        (await Service().RestoreBackupAsync(zip, App, Game)).Outcome.Should().Be(SaveBackupOutcome.Refused);

        Directory.GetFiles(outside).Should().BeEmpty();
    }

    [Fact]
    public async Task Restore_refuses_when_the_game_folder_itself_became_a_junction()
    {
        string outside = Path.Combine(_dir, "outside");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(Path.Combine(_docs, "Saved Games"));
        DirectoryJunction.Create(Path.Combine(_docs, "Saved Games", Game), outside).Should().BeTrue();
        var zip = CraftZip(("saves/Documents_SavedGames_Hades/a.sav", "pwn"));

        (await Service().RestoreBackupAsync(zip, App, Game)).Outcome.Should().Be(SaveBackupOutcome.Refused);

        Directory.GetFiles(outside).Should().BeEmpty();
    }

    [Fact]
    public async Task A_custom_folder_that_was_unregistered_is_no_longer_a_restore_target()
    {
        string slot = Put(Path.Combine(_dir, "Exotic", "a.sav"), "exotic");
        _custom.Add(Path.GetDirectoryName(slot)!);
        var svc = Service();
        var zip = (await svc.BackupSavesAsync(App, "Obscure Game")).Path!;
        File.Delete(slot);
        _custom.Clear(); // the user removed it in Settings, or somebody edited settings.json

        (await svc.RestoreBackupAsync(zip, App, "Obscure Game")).Outcome.Should().Be(SaveBackupOutcome.Refused);

        File.Exists(slot).Should().BeFalse();
    }

    [Fact]
    public async Task A_zip_of_another_game_cannot_be_imported_over_this_one()
    {
        Put(SavedGames(), "hades");
        var theirs = new SaveBackupService(() => _steam, _backups, roots: _roots);
        var zip = (await theirs.BackupSavesAsync(App, Game)).Path!;
        string other = Put(Path.Combine(_docs, "Saved Games", "Celeste", "keep.sav"), "keep");

        (await Service().ImportSaveAsync(zip, 504230, "Celeste")).Outcome.Should().Be(SaveBackupOutcome.Refused);

        File.ReadAllText(other).Should().Be("keep");
    }

    // ── Settings ──────────────────────────────────────────────────────

    [Fact]
    public void Custom_save_folders_survive_a_restart_and_removing_the_last_one_leaves_no_file()
    {
        string folder = Path.Combine(_dir, "Exotic");
        string settingsDir = Path.Combine(_dir, "settings");
        var settings = new SettingsService(settingsDir);

        settings.AddCustomSaveFolder(App, folder).Should().BeTrue();
        settings.AddCustomSaveFolder(App, folder.ToUpperInvariant() + @"\").Should().BeTrue("the same folder again is not a second entry");
        settings.AddCustomSaveFolder(App, @"C:\").Should().BeFalse();

        new SettingsService(settingsDir).GetCustomSaveFolders(App).Should().Equal(folder);

        settings.RemoveCustomSaveFolder(App, folder);

        settings.GetCustomSaveFolders(App).Should().BeEmpty();
        Directory.GetFiles(settingsDir).Should().BeEmpty("nothing left worth persisting");
    }

    // ── Logs ──────────────────────────────────────────────────────────

    [Fact]
    public void Logs_do_not_name_the_windows_user()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        string logged = LogSanitizer.SanitizePath($@"found {profile}\Documents\Saved Games\Hades");

        logged.Should().Contain("%USERPROFILE%").And.NotContain(profile);
    }
}
