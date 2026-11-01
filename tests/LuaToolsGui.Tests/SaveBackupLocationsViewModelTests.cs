using System.IO;
using System.IO.Compression;
using AwesomeAssertions;
using LuaToolsGui.Services;
using LuaToolsGui.Services.Backups;
using LuaToolsGui.ViewModels;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>The dialog's three outcomes after detection: nothing found (offer the folder picker), one or more
/// found (back up the ticked ones), none ticked (say so, write nothing).</summary>
public sealed class SaveBackupLocationsViewModelTests : IDisposable
{
    private const long App = 1145360;
    private const string Game = "Hades";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "luatools-tests", Guid.NewGuid().ToString("N"));
    private readonly string _docs;
    private readonly List<string> _custom = [];
    private readonly List<string> _asked = [];
    private readonly SaveBackupService _svc;
    private readonly SaveBackupViewModel _vm;
    private string? _picked;

    public SaveBackupLocationsViewModelTests()
    {
        string profile = Path.Combine(_dir, "profile");
        _docs = Path.Combine(profile, "Documents");
        Directory.CreateDirectory(_docs);
        _svc = new SaveBackupService(() => Path.Combine(_dir, "steam"), Path.Combine(_dir, "backups"),
            roots: new SaveRoots(_docs, profile, Path.Combine(profile, "AppData", "Local"), Path.Combine(profile, "AppData", "Roaming")),
            customFolders: _ => [.. _custom], addCustomFolder: (_, path) => { _custom.Add(path); return true; });
        _vm = new SaveBackupViewModel(_svc, new ToastService(), null!)
        {
            Confirm = (body, _) => { _asked.Add(body); return Task.FromResult(true); },
            PickFolder = () => _picked,
        };
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string Put(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "save");
        return path;
    }

    private static string[] Entries(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        return [.. zip.Entries.Select(e => e.FullName)];
    }

    [Fact]
    public void Detected_locations_are_listed_with_their_friendly_name_and_start_ticked()
    {
        Put(Path.Combine(_docs, "Saved Games", Game, "a.sav"));

        _vm.Load(App, Game);

        var row = _vm.Locations.Should().ContainSingle().Subject;
        row.Title.Should().Be(@"Documents\Saved Games\Hades");
        row.IsSelected.Should().BeTrue();
        _vm.HasNoLocations.Should().BeFalse();
    }

    [Fact]
    public async Task With_nothing_found_the_user_is_offered_a_folder_and_the_backup_uses_it()
    {
        string slot = Put(Path.Combine(_dir, "Exotic", "a.sav"));
        _picked = Path.GetDirectoryName(slot);
        _vm.Load(App, Game);
        _vm.HasNoLocations.Should().BeTrue();

        await _vm.CreateCommand.ExecuteAsync(null);

        _asked.Should().Contain(Resources.Strings.Saves_Confirm_NoneFound);
        _custom.Should().ContainSingle().Which.Should().Be(_picked);
        var zip = _svc.ListBackups(App).Should().ContainSingle().Subject;
        Entries(zip.FullPath).Should().ContainSingle().Which.Should().StartWith("saves/Custom_Exotic_");
    }

    [Fact]
    public async Task Declining_the_folder_offer_writes_nothing()
    {
        _vm.Confirm = (body, _) => { _asked.Add(body); return Task.FromResult(false); };
        _vm.Load(App, Game);

        await _vm.CreateCommand.ExecuteAsync(null);

        _custom.Should().BeEmpty();
        _svc.ListBackups(App).Should().BeEmpty();
    }

    [Fact]
    public async Task Cancelling_the_picker_writes_nothing()
    {
        _picked = null;
        _vm.Load(App, Game);

        await _vm.CreateCommand.ExecuteAsync(null);

        _custom.Should().BeEmpty();
        _svc.ListBackups(App).Should().BeEmpty();
    }

    [Fact]
    public async Task Only_the_ticked_rows_are_backed_up()
    {
        Put(Path.Combine(_docs, "Saved Games", Game, "a.sav"));
        Put(Path.Combine(_dir, "profile", "AppData", "Local", Game, "b.ini"));
        _vm.Load(App, Game);
        _vm.Locations.Single(r => r.Location.Kind == SaveLocationKind.LocalAppData).IsSelected = false;

        await _vm.CreateCommand.ExecuteAsync(null);

        Entries(_svc.ListBackups(App).Single().FullPath).Should().ContainSingle()
            .Which.Should().Be("saves/Documents_SavedGames_Hades/a.sav");
    }

    [Fact]
    public async Task With_every_row_unticked_nothing_is_written()
    {
        Put(Path.Combine(_docs, "Saved Games", Game, "a.sav"));
        _vm.Load(App, Game);
        _vm.Locations.Single().IsSelected = false;

        await _vm.CreateCommand.ExecuteAsync(null);

        _svc.ListBackups(App).Should().BeEmpty();
        _asked.Should().NotContain(Resources.Strings.Saves_Confirm_NewFolder, "the user is told to tick something before anything else is asked");
    }
}
