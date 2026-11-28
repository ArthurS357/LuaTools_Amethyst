using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Markup;
using AwesomeAssertions;
using LuaToolsGui.Services;
using LuaToolsGui.Services.Downloads;
using LuaToolsGui.ViewModels;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Covers the Manage 4-scope confirmation ViewModel, the Depots full-width 2-state ViewModel
/// and 3-scope removal flow, regression protection against resurrecting a deleted live .lua,
/// and loose-XAML resource/layout smoke checks for the redesigned Depots and cleanup views.
/// </summary>
[Collection(ThemeHostCollection.Name)]
public sealed class GameCleanupAndDepotsUiTests(ThemeHost host) : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "luatools-ui-cleanup-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private sealed class StubOfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private sealed class Env : IDisposable
    {
        public string Dir { get; }
        public string SteamRoot { get; }
        public string PlugDir { get; }
        public string DepotDir { get; }
        public string CommonDir { get; }
        public string BackupDir { get; }

        public SettingsService Settings { get; }
        public CacheService Cache { get; }
        public SteamService Steam { get; }
        public SteamLibraryService Library { get; }
        public LuaVault Vault { get; }
        public ToastService Toast { get; }
        public GameCleanupService Cleanup { get; }
        public ManageViewModel ManageVm { get; }
        public BuildsViewModel BuildsVm { get; }

        public Env(string root, ThemeHost host)
        {
            Dir = root;
            SteamRoot = Path.Combine(root, "Steam");
            PlugDir = Path.Combine(SteamRoot, "config", "stplug-in");
            DepotDir = Path.Combine(SteamRoot, "depotcache");
            CommonDir = Path.Combine(SteamRoot, "steamapps", "common");
            BackupDir = Path.Combine(root, "BackupManifest");

            Directory.CreateDirectory(PlugDir);
            Directory.CreateDirectory(DepotDir);
            Directory.CreateDirectory(CommonDir);
            Directory.CreateDirectory(BackupDir);

            Settings = new SettingsService(Path.Combine(root, "settings"))
            {
                SteamPathOverride = SteamRoot,
                ManifestBackupFolder = BackupDir,
            };
            Cache = new CacheService(Path.Combine(root, "cache"));
            Steam = new SteamService(Settings);
            Library = new SteamLibraryService(Steam);
            Vault = new LuaVault(() => Steam.StPlugInDir, Path.Combine(root, "vault"));
            Toast = new ToastService();

            Cleanup = new GameCleanupService(
                () => Steam.StPlugInDir,
                () => Steam.DepotCacheDir,
                Library,
                Vault,
                Cache,
                () => Settings.ManifestBackupFolder,
                () => null,
                () => false);

            var handler = new StubOfflineHandler();
            var appList = new SteamAppListCache(Path.Combine(root, "applist.json"), handler);
            var appInfo = new SteamAppInfoCache(Cache, handler, Path.Combine(root, "details"));
            var covers = new CoverCache(handler);
            var depotInfo = new SteamDepotInfo(handler);
            var gh = new GithubProxy(handler);
            var auth = new AuthService(handler, Path.Combine(root, "auth.dat"));
            var notice = new InsecureTransportNotice(Settings, Toast);
            var api = new LuaToolsApiClient(auth, appInfo, covers, notice, Settings);
            var hubcap = new HubcapService(handler, TimeSpan.FromSeconds(5));
            var installer = new LuaInstaller(Steam, Settings, Cache, Vault);
            var depotTool = new DepotDownloaderService(gh, Steam, auth, Cache, api, installer);
            var backupSvc = new Services.Backups.ManifestBackupService(Settings, Steam, installer, appList, Toast);
            var queue = host.On(_ => new DownloadQueue(Cache));
            var jobs = new ManifestJobFactory(api, hubcap, Settings, installer, Cache, covers, depotTool, Toast, backupSvc);

            var steamless = new SteamlessService(gh, Library, depotInfo);
            var launcher = new SteamGameLauncher(Steam, Library);

            ManageVm = new ManageViewModel(
                Steam, appList, appInfo, covers, Toast, Settings, steamless, launcher, Library, Vault, Cleanup)
            {
                OfferRestartSteamOverride = () => { },
            };
            BuildsVm = new BuildsViewModel(
                Steam, Vault, appList, appInfo, covers, depotInfo, depotTool, Toast, Settings, queue, jobs)
            {
                OfferRestartSteamOverride = () => { },
            };
        }

        public void WriteLua(long appId, string text) =>
            File.WriteAllText(Path.Combine(PlugDir, $"{appId}.lua"), text);

        public void WriteAcf(long appId, string installDir, long sizeOnDisk, params long[] depots)
        {
            string steamApps = Path.Combine(SteamRoot, "steamapps");
            Directory.CreateDirectory(steamApps);
            string depotsBlock = string.Join(
                "\n",
                depots.Select(d => $"\t\t\"{d}\"\n\t\t{{\n\t\t\t\"manifest\"\t\t\"111\"\n\t\t}}"));
            File.WriteAllText(
                Path.Combine(steamApps, $"appmanifest_{appId}.acf"),
                $"\"AppState\"\n{{\n\t\"appid\"\t\t\"{appId}\"\n\t\"StateFlags\"\t\t\"4\"\n\t\"installdir\"\t\t\"{installDir}\"\n\t\"SizeOnDisk\"\t\t\"{sizeOnDisk}\"\n\t\"InstalledDepots\"\n\t{{\n{depotsBlock}\n\t}}\n}}");
            Directory.CreateDirectory(Path.Combine(CommonDir, installDir));
        }

        public void Dispose() { }
    }

    private Env NewEnv() => new(_root, host);

    // ── GameCleanupDialogViewModel ────────────────────────────────────────────

    [Fact]
    public async Task DialogVm_ManageMode_Enables4ScopesWhenInstalled_AndDisablesGameOnlyWhenNotInstalled()
    {
        using var env = NewEnv();
        env.WriteLua(400, "addappid(400)\naddappid(401, 1, \"k\")");
        env.WriteAcf(400, "Portal", 1_048_576, 401);

        var vm = new GameCleanupDialogViewModel(env.Cleanup);
        await vm.LoadAsync(400, "Portal", GameCleanupDialogMode.Manage);

        vm.ShowGameOnlyOption.Should().BeTrue();
        vm.ShowEverythingOption.Should().BeTrue();
        vm.ShowManifestsOnlyOption.Should().BeFalse();
        vm.CanSelectGameOnly.Should().BeTrue();
        vm.CanSelectLuaOnly.Should().BeTrue();
        vm.CanSelectLuaAndManifests.Should().BeTrue();
        vm.CanSelectEverything.Should().BeTrue();
        vm.SelectedScope.Should().Be(GameCleanupScope.LuaOnly);
        vm.ShowSizeLine.Should().BeTrue();
        vm.SizeLabel.Should().NotBeNullOrEmpty();

        // Now inspect an uninstalled game with only an active lua
        env.WriteLua(620, "addappid(620)");
        await vm.LoadAsync(620, "Portal 2", GameCleanupDialogMode.Manage);
        vm.CanSelectGameOnly.Should().BeFalse();
        vm.CanSelectLuaOnly.Should().BeTrue();
        vm.ShowSizeLine.Should().BeFalse();
    }

    [Fact]
    public async Task DialogVm_DepotsMode_NoActiveLua_DefaultsToManifestsOnly_AndBlocksStayOpenWithError()
    {
        using var env = NewEnv();
        env.Vault.SaveText(400, "addappid(400)\naddappid(401, 1, \"k\")", "SavedBuild");

        var vm = new GameCleanupDialogViewModel(env.Cleanup);
        await vm.LoadAsync(400, "Portal", GameCleanupDialogMode.Depots);

        vm.ShowManifestsOnlyOption.Should().BeTrue();
        vm.ShowGameOnlyOption.Should().BeFalse();
        vm.CanSelectLuaOnly.Should().BeFalse();
        vm.CanSelectManifestsOnly.Should().BeTrue();
        vm.SelectedScope.Should().Be(GameCleanupScope.ManifestsOnly);

        // Lock a sibling lua so CleanAsync returns UnreadableSiblingLua (blocked)
        string locked = Path.Combine(env.PlugDir, "999.lua");
        File.WriteAllText(locked, "addappid(999)");
        using var lockStream = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        bool? closedWith = null;
        vm.CloseWith = ok => closedWith = ok;
        await vm.ConfirmCleanupCommand.ExecuteAsync(null);

        closedWith.Should().BeNull("a blocked cleanup must keep the confirmation dialog open with explanation");
        vm.ErrorMessage.Should().Be(Resources.Strings.Cleanup_Err_UnreadableSibling);
        vm.IsRemoving.Should().BeFalse();
    }

    [Fact]
    public async Task DialogVm_SuccessfulCleanup_ReleasesRemovingLockBeforeClosing()
    {
        // Regression: CloseWith(true) used to run while IsRemoving was still true, and the window's Closing
        // handler cancels every close in that state — a finished cleanup left its dialog stuck open.
        using var env = NewEnv();
        env.WriteLua(400, "addappid(400)");

        var vm = new GameCleanupDialogViewModel(env.Cleanup);
        await vm.LoadAsync(400, "Portal", GameCleanupDialogMode.Manage);

        bool? removingWhenClosed = null;
        bool? closedWith = null;
        vm.CloseWith = ok => (closedWith, removingWhenClosed) = (ok, vm.IsRemoving);
        await vm.ConfirmCleanupCommand.ExecuteAsync(null);

        closedWith.Should().BeTrue();
        removingWhenClosed.Should().BeFalse("the dialog's Closing handler refuses to close while IsRemoving is true");
        vm.Result!.LuaDeleted.Should().BeTrue();
    }

    // ── ManageViewModel ───────────────────────────────────────────────────────

    [Fact]
    public async Task ManageVm_ApplyCleanupResult_UpdatesTileOrRemovesIt_AndLockPreventsClosingDetail()
    {
        using var env = NewEnv();
        env.WriteLua(400, "addappid(400)");
        env.WriteAcf(400, "Portal", 128);

        await env.ManageVm.LoadAsync();
        env.ManageVm.Items.Should().ContainSingle(t => t.AppId == 400);
        var tile = env.ManageVm.Items[0];
        tile.IsInstalled = true;

        bool restarted = false;
        env.ManageVm.OfferRestartSteamOverride = () => restarted = true;

        // While IsRemoving is true, CloseDetailCommand cannot close the flyout.
        await env.ManageVm.OpenDetailCommand.ExecuteAsync(tile);
        env.ManageVm.IsRemoving = true;
        env.ManageVm.CloseDetailCommand.CanExecute(null).Should().BeFalse();
        env.ManageVm.IsRemoving = false;

        // Deleting only game files removes the ACF first, then ApplyCleanupResult clears install state without removing the tile.
        var gameResult = await env.Cleanup.CleanAsync(400, GameCleanupScope.GameFilesOnly);
        await env.ManageVm.ApplyCleanupResultAsync(tile, gameResult);
        tile.IsInstalled.Should().BeFalse();
        tile.InstallState.Should().Be(SteamGameInstallState.NotInstalled);
        env.ManageVm.Items.Should().Contain(tile);
        restarted.Should().BeTrue();

        // Deleting lua removes the tile from Manage.
        var luaResult = await env.Cleanup.CleanAsync(400, GameCleanupScope.LuaOnly);
        await env.ManageVm.ApplyCleanupResultAsync(tile, luaResult);
        env.ManageVm.Items.Should().BeEmpty();
    }

    // ── BuildsViewModel (Depots Redesign + Cleanup Regression) ────────────────

    [Fact]
    public void BuildsVm_GridAndDetailNavigation_Filters_AndLegacyPageSize10Normalization()
    {
        using var env = NewEnv();
        env.Settings.BuildsPageSize.Should().Be(10);
        env.BuildsVm.SelectedPageSize.Should().Be("24");
        env.BuildsVm.PageSizeOptions.Should().Contain("All");
        env.Settings.BuildsPageSize.Should().Be(10, "normalizing in memory must not overwrite persisted settings on construction");

        var activeGame = new LuaTileViewModel(400, Path.Combine(env.PlugDir, "400.lua"), DateTime.UtcNow, "Portal", false)
        {
            HasActiveLua = true,
            HasStoredBuilds = true,
            SavedBuildsOnly = false,
            VariantCount = 2,
        };
        var vaultOnlyGame = new LuaTileViewModel(620, Path.Combine(env.PlugDir, "620.lua"), DateTime.UtcNow, "Portal 2", false)
        {
            HasActiveLua = false,
            HasStoredBuilds = true,
            SavedBuildsOnly = true,
            VariantCount = 1,
        };
        var luaOnlyGame = new LuaTileViewModel(730, Path.Combine(env.PlugDir, "730.lua"), DateTime.UtcNow, "Counter-Strike 2", false)
        {
            HasActiveLua = true,
            HasStoredBuilds = false,
            SavedBuildsOnly = false,
            VariantCount = 0,
        };
        var games = new[] { activeGame, vaultOnlyGame, luaOnlyGame };

        BuildsViewModel.FilterGames(games, "", BuildsQuickFilter.All).Should().HaveCount(3);
        BuildsViewModel.FilterGames(games, "", BuildsQuickFilter.WithStoredBuilds).Should().Equal(activeGame, vaultOnlyGame);
        BuildsViewModel.FilterGames(games, "", BuildsQuickFilter.WithoutActiveLua).Should().Equal(vaultOnlyGame);
        BuildsViewModel.FilterGames(games, "620", BuildsQuickFilter.All).Should().Equal(vaultOnlyGame);

        env.BuildsVm.IsShowingGrid.Should().BeTrue();
        env.BuildsVm.IsShowingDetail.Should().BeFalse();

        env.BuildsVm.OpenGameCommand.Execute(activeGame);
        env.BuildsVm.IsShowingGrid.Should().BeFalse();
        env.BuildsVm.IsShowingDetail.Should().BeTrue();

        env.BuildsVm.IsRemoving = true;
        env.BuildsVm.BackToGridCommand.CanExecute(null).Should().BeFalse();
        env.BuildsVm.IsRemoving = false;

        env.BuildsVm.BackToGridCommand.Execute(null);
        env.BuildsVm.IsShowingGrid.Should().BeTrue();
        env.BuildsVm.IsShowingDetail.Should().BeFalse();
    }

    [Fact]
    public async Task BuildsVm_SelectAppAsync_OpensDetailEvenWhenFiltered_AndRefreshDoesNotResurrectDeletedLua()
    {
        using var env = NewEnv();
        env.WriteLua(400, "addappid(400)\naddappid(401, 1, \"k\")");
        env.Vault.SaveText(400, "addappid(400)\naddappid(401, 1, \"k\")", "PreservedPreset");

        await env.BuildsVm.LoadAsync();
        env.BuildsVm.SearchText = "unmatched-filter-query";
        env.BuildsVm.QuickFilter = BuildsQuickFilter.WithoutActiveLua;

        await env.BuildsVm.SelectAppAsync(400);
        env.BuildsVm.IsShowingDetail.Should().BeTrue();
        env.BuildsVm.ActiveGame.Should().NotBeNull();
        env.BuildsVm.ActiveGame!.AppId.Should().Be(400);
        env.BuildsVm.SearchText.Should().BeEmpty();
        env.BuildsVm.QuickFilter.Should().Be(BuildsQuickFilter.All);

        // Regression test: removing only the .lua must NOT let RefreshVariants/ApplyBuildIfNothingLive recreate 400.lua.
        var luaResult = await env.Cleanup.CleanAsync(400, GameCleanupScope.LuaOnly);
        luaResult.LuaDeleted.Should().BeTrue();

        bool buildsRestartOffered = false;
        env.BuildsVm.OfferRestartSteamOverride = () => buildsRestartOffered = true;
        await env.BuildsVm.ApplyCleanupResultAsync(env.BuildsVm.ActiveGame!, luaResult);
        buildsRestartOffered.Should().BeTrue();
        File.Exists(Path.Combine(env.PlugDir, "400.lua")).Should().BeFalse(
            "RefreshVariants must not resurrect a deliberately deleted live .lua from LuaVault");
        env.BuildsVm.ActiveGame!.SavedBuildsOnly.Should().BeTrue();
        env.BuildsVm.ActiveGame!.HasStoredBuilds.Should().BeTrue();
    }

    [Fact]
    public async Task BuildsVm_RemoveGame_ConfirmsThroughDarkDialogSeam_AndNeverTouchesTheBackupVault()
    {
        using var env = NewEnv();
        env.WriteLua(400, "addappid(400)");
        string loose = Path.Combine(env.PlugDir, "400_777.lua");
        File.WriteAllText(loose, "addappid(400)\n-- build 777");
        env.Vault.SaveText(400, "addappid(400)\n-- saved", "Saved");

        string backupZip = Path.Combine(env.BackupDir, "400", "400.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(backupZip)!);
        File.WriteAllBytes(backupZip, [1, 2, 3, 4]);
        string vaultAppDir = Path.Combine(env.Dir, "vault", "400");

        await env.BuildsVm.LoadAsync();
        var tile = env.BuildsVm.Items.Single(g => g.AppId == 400);

        (string Name, long AppId)? asked = null;
        env.BuildsVm.ConfirmRemoveGame = (name, appId) => { asked = (name, appId); return false; };
        env.BuildsVm.RemoveGameCommand.Execute(tile);

        asked.Should().Be((tile.Name, 400L));
        File.Exists(Path.Combine(env.PlugDir, "400.lua")).Should().BeTrue("Cancel must delete nothing");
        File.Exists(loose).Should().BeTrue();
        Directory.Exists(vaultAppDir).Should().BeTrue();

        env.BuildsVm.ConfirmRemoveGame = (_, _) => true;
        env.BuildsVm.RemoveGameCommand.Execute(tile);

        File.Exists(Path.Combine(env.PlugDir, "400.lua")).Should().BeFalse();
        File.Exists(loose).Should().BeFalse();
        Directory.Exists(vaultAppDir).Should().BeFalse();
        File.ReadAllBytes(backupZip).Should().Equal([1, 2, 3, 4]);
        env.BuildsVm.Items.Should().NotContain(g => g.AppId == 400);
    }

    [Fact]
    public void ForgetGame_JunctionInsideVaultFolder_NeverReachesTheBackupVault()
    {
        using var env = NewEnv();
        env.Vault.SaveText(400, "addappid(400)", "Saved");
        string backupZip = Path.Combine(env.BackupDir, "400", "400.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(backupZip)!);
        File.WriteAllBytes(backupZip, [9, 8, 7]);
        string trap = Path.Combine(env.Dir, "vault", "400", "trap");
        DirectoryJunction.Create(trap, env.BackupDir).Should().BeTrue();

        try
        {
            // Today Windows refuses the recursive delete at the junction (Failed, the safe direction). Whatever
            // the outcome, nothing behind the link may be touched.
            env.Vault.ForgetGame(400);
            File.ReadAllBytes(backupZip).Should().Equal([9, 8, 7]);
        }
        finally
        {
            if (DirectoryJunction.Exists(trap)) DirectoryJunction.Remove(trap);
        }
    }

    [Fact]
    public async Task BuildsVm_RemoveGame_WithoutConfirmSeam_DeletesNothing()
    {
        using var env = NewEnv();
        env.WriteLua(400, "addappid(400)");
        await env.BuildsVm.LoadAsync();

        env.BuildsVm.RemoveGameCommand.Execute(env.BuildsVm.Items.Single(g => g.AppId == 400));

        File.Exists(Path.Combine(env.PlugDir, "400.lua")).Should().BeTrue();
    }

    // ── Live WPF Resource & Layout Smoke Tests ────────────────────────────────

    [Theory]
    [InlineData("BuildsView")]
    [InlineData("BuildsDetailPane")]
    [InlineData("GameCleanupDialog")]
    [InlineData("RemoveGameDialog")]
    public void Redesigned_Depots_and_cleanup_XAML_parse_measure_and_arrange_against_app_resources(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LuaToolsGui.sln")))
            dir = dir.Parent;
        string xaml = File.ReadAllText(Path.Combine(dir!.FullName, "src", "LuaToolsGui", "Views", name + ".xaml"));
        xaml = Regex.Replace(xaml, @"\s+x:Class=""[^""]+""", "");
        xaml = Regex.Replace(
            xaml,
            @"\s+(?:Click|Loaded|DataContextChanged|MouseLeftButtonUp|PreviewMouseWheel|DragEnter|DragLeave|DragOver|Drop)=""[^""]+""",
            "");
        xaml = Regex.Replace(xaml, @"clr-namespace:(LuaToolsGui\.\w+)""", "clr-namespace:$1;assembly=LuaTools\"");

        host.On(_ =>
        {
            var element = (FrameworkElement)XamlReader.Parse(xaml);
            element.Measure(new Size(1100, 900));
            element.Arrange(new Rect(0, 0, 1100, 900));
            if (element is not Window)
                element.DesiredSize.Height.Should().BeGreaterThan(0);
        });
    }
}
