using System.IO;
using AwesomeAssertions;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>The "Installed" badge on a Manage card: the manifest must say the game is fully on disk.</summary>
public sealed class SteamLibraryInstalledTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "luatools-tests", Guid.NewGuid().ToString("N"));
    private readonly SteamLibraryService _library;

    public SteamLibraryInstalledTests()
    {
        string steam = Path.Combine(_dir, "steam");
        Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
        var settings = new SettingsService(Path.Combine(_dir, "settings")) { SteamPathOverride = steam };
        _library = new SteamLibraryService(new SteamService(settings));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private void Manifest(long appId, string body) =>
        File.WriteAllText(Path.Combine(_dir, "steam", "steamapps", $"appmanifest_{appId}.acf"), body);

    private static string Acf(long flags) => $"\"AppState\"\n{{\n\t\"appid\"\t\t\"1\"\n\t\"StateFlags\"\t\t\"{flags}\"\n}}";

    [Fact]
    public void Only_a_manifest_with_the_fully_installed_bit_counts_for_the_badge()
    {
        Manifest(10, Acf(4));       // fully installed
        Manifest(20, Acf(6));       // installed, update queued (4 | 2)
        Manifest(30, Acf(1026));    // being updated: not fully installed right now
        Manifest(40, Acf(1));       // uninstalled shell
        Manifest(50, "not a vdf");  // unreadable content: not listed, not a crash

        _library.GetFullyInstalledAppIds().Should().BeEquivalentTo(new long[] { 10, 20 });
    }

    [Fact]
    public void The_play_button_still_treats_a_downloading_game_as_present()
    {
        Manifest(30, Acf(1026));
        Manifest(40, Acf(4));

        _library.GetInstalledAppIds().Should().BeEquivalentTo(new long[] { 30, 40 });
    }

    [Fact]
    public void The_scan_returns_names_from_the_same_pass_and_flags_a_missing_libraryfolders_vdf()
    {
        Manifest(10, "\"AppState\"\n{\n\t\"appid\"\t\"10\"\n\t\"name\"\t\t\"Half-Life\"\n\t\"StateFlags\"\t\t\"4\"\n}");
        Manifest(20, Acf(4)); // no name in the manifest

        var (games, failed) = _library.ScanFullyInstalled();

        games.Should().BeEquivalentTo(new[] { new InstalledGame(10, "Half-Life"), new InstalledGame(20, "") });
        failed.Should().BeTrue("there is no libraryfolders.vdf, so other libraries could be invisible");

        File.WriteAllText(Path.Combine(_dir, "steam", "steamapps", "libraryfolders.vdf"), "x");
        _library.ScanFullyInstalled().Failed.Should().BeFalse();
    }

    [Fact]
    public void Non_manifest_files_and_non_numeric_ids_are_ignored()
    {
        File.WriteAllText(Path.Combine(_dir, "steam", "steamapps", "appmanifest_abc.acf"), Acf(4));
        File.WriteAllText(Path.Combine(_dir, "steam", "steamapps", "libraryfolders.vdf"), "x");

        _library.GetFullyInstalledAppIds().Should().BeEmpty();
        _library.GetInstalledAppIds().Should().BeEmpty();
    }
}
