using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AwesomeAssertions;
using LuaToolsGui.Services;
using LuaToolsGui.Services.Downloads;
using LuaToolsGui.Themes;
using LuaToolsGui.ViewModels;
using LuaToolsGui.Views;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Covers persisted Grid/List view mode switching for Manage & Depots, GPU-friendly reduced-motion
/// animations and container-recycling resets, decoded cover bitmap caching, zero-allocation filter
/// helpers, and WPF virtualization/smoke guarantees.
/// </summary>
[Collection(ThemeHostCollection.Name)]
public sealed class LibraryViewModeAndPerformanceTests(ThemeHost host) : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "luatools-viewmode-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private sealed class CountingOfflineHandler : HttpMessageHandler
    {
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class Env
    {
        public string Root { get; }
        public string SteamRoot { get; }
        public string PlugDir { get; }
        public string SettingsDir { get; }
        public string DetailsDir { get; }
        public CountingOfflineHandler Handler { get; } = new();
        public SettingsService Settings { get; }
        public CacheService Cache { get; }
        public SteamService Steam { get; }
        public SteamLibraryService Library { get; }
        public LuaVault Vault { get; }
        public SteamAppInfoCache AppInfo { get; }
        public CoverCache Covers { get; }
        public ManageViewModel ManageVm { get; }
        public BuildsViewModel BuildsVm { get; }

        public Env(string root, ThemeHost host)
        {
            Root = root;
            SteamRoot = Path.Combine(root, "Steam");
            PlugDir = Path.Combine(SteamRoot, "config", "stplug-in");
            SettingsDir = Path.Combine(root, "settings");
            DetailsDir = Path.Combine(root, "details");
            string depotDir = Path.Combine(SteamRoot, "depotcache");

            Directory.CreateDirectory(PlugDir);
            Directory.CreateDirectory(depotDir);
            Directory.CreateDirectory(SettingsDir);
            Directory.CreateDirectory(DetailsDir);

            Settings = new SettingsService(SettingsDir) { SteamPathOverride = SteamRoot };
            Cache = new CacheService(Path.Combine(root, "cache"));
            Steam = new SteamService(Settings);
            Library = new SteamLibraryService(Steam);
            Vault = new LuaVault(() => Steam.StPlugInDir, Path.Combine(root, "vault"));
            var toast = new ToastService();

            var cleanup = new GameCleanupService(
                () => Steam.StPlugInDir,
                () => Steam.DepotCacheDir,
                Library,
                Vault,
                Cache,
                () => Settings.ManifestBackupFolder,
                () => null,
                () => false);

            var appList = new SteamAppListCache(Path.Combine(root, "applist.json"), Handler);
            AppInfo = new SteamAppInfoCache(Cache, Handler, DetailsDir);
            Covers = new CoverCache(Handler);
            var depotInfo = new SteamDepotInfo(Handler);
            var gh = new GithubProxy(Handler);
            var auth = new AuthService(Handler, Path.Combine(root, "auth.dat"));
            var notice = new InsecureTransportNotice(Settings, toast);
            var api = new LuaToolsApiClient(auth, AppInfo, Covers, notice, Settings);
            var hubcap = new HubcapService(Handler, TimeSpan.FromSeconds(5));
            var installer = new LuaInstaller(Steam, Settings, Cache, Vault);
            var depotTool = new DepotDownloaderService(gh, Steam, auth, Cache, api, installer);
            var backupSvc = new Services.Backups.ManifestBackupService(Settings, Steam, installer, appList, toast);
            var queue = host.On(_ => new DownloadQueue(Cache));
            var jobs = new ManifestJobFactory(api, hubcap, Settings, installer, Cache, Covers, depotTool, toast, backupSvc);
            var steamless = new SteamlessService(gh, Library, depotInfo);
            var launcher = new SteamGameLauncher(Steam, Library);

            ManageVm = new ManageViewModel(
                Steam, appList, AppInfo, Covers, toast, Settings, steamless, launcher, Library, Vault, cleanup)
            {
                OfferRestartSteamOverride = () => { },
            };

            BuildsVm = new BuildsViewModel(
                Steam, Vault, appList, AppInfo, Covers, depotInfo, depotTool, toast, Settings, queue, jobs)
            {
                OfferRestartSteamOverride = () => { },
            };
        }

        public void WriteLua(long appId, string name) =>
            File.WriteAllText(Path.Combine(PlugDir, $"{appId}.lua"), $"-- Created by Test\n-- {name}\naddappid({appId})\n");
    }

    // ── SettingsService persistence ───────────────────────────────────────────

    [Fact]
    public void Settings_ViewMode_DefaultsToGrid_AndPersistsManageAndDepotsIndependently()
    {
        string dir = Path.Combine(_root, "settings-rt");
        Directory.CreateDirectory(dir);
        string settingsPath = Path.Combine(dir, "settings.json");

        var initial = new SettingsService(dir);
        initial.ManageViewMode.Should().Be(LibraryViewMode.Grid);
        initial.DepotsViewMode.Should().Be(LibraryViewMode.Grid);
        File.Exists(settingsPath).Should().BeFalse("default Grid values should not create a settings file");

        initial.ManageViewMode = LibraryViewMode.List;
        var afterManage = new SettingsService(dir);
        afterManage.ManageViewMode.Should().Be(LibraryViewMode.List);
        afterManage.DepotsViewMode.Should().Be(LibraryViewMode.Grid);

        afterManage.DepotsViewMode = LibraryViewMode.List;
        afterManage.ManageViewMode = LibraryViewMode.Grid;
        var afterSwap = new SettingsService(dir);
        afterSwap.ManageViewMode.Should().Be(LibraryViewMode.Grid);
        afterSwap.DepotsViewMode.Should().Be(LibraryViewMode.List);

        afterSwap.DepotsViewMode = LibraryViewMode.Grid;
        File.Exists(settingsPath).Should().BeFalse("reverting both modes to default Grid should keep settings.json clean");
    }

    [Theory]
    [InlineData(null, LibraryViewMode.Grid)]
    [InlineData("", LibraryViewMode.Grid)]
    [InlineData("   ", LibraryViewMode.Grid)]
    [InlineData("99", LibraryViewMode.Grid)]
    [InlineData("corrupt_value", LibraryViewMode.Grid)]
    [InlineData("Grid", LibraryViewMode.Grid)]
    [InlineData("List", LibraryViewMode.List)]
    [InlineData(" list ", LibraryViewMode.List)]
    public void Settings_ViewMode_CorruptOrUnknownValues_SafelyFallBackToGrid(string? jsonValue, LibraryViewMode expected)
    {
        string dir = Path.Combine(_root, "settings-corrupt", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        // A sibling setting proves the lenient parse kept the file (a reset would drop BuildsPageSize too).
        string json = jsonValue is null
            ? """{ "ManageViewMode": null, "DepotsViewMode": null, "BuildsPageSize": 48 }"""
            : $$"""{ "ManageViewMode": "{{jsonValue}}", "DepotsViewMode": "{{jsonValue}}", "BuildsPageSize": 48 }""";
        File.WriteAllText(Path.Combine(dir, "settings.json"), json);

        var loaded = new SettingsService(dir);
        loaded.ManageViewMode.Should().Be(expected);
        loaded.DepotsViewMode.Should().Be(expected);
        loaded.BuildsPageSize.Should().Be(48);
    }

    // ── ViewModels & Zero-I/O Switching ───────────────────────────────────────

    [Fact]
    public async Task ManageVm_SetViewMode_PreservesStateSelectionAndTriggersZeroExtraHttpOrReloads()
    {
        var env = new Env(_root, host);
        env.WriteLua(400, "Portal");
        env.WriteLua(620, "Portal 2");
        env.WriteLua(730, "Counter-Strike 2");

        await env.ManageVm.LoadAsync();
        env.ManageVm.CancelBackgroundPrefetch();
        await env.ManageVm.PrefetchTask;

        env.ManageVm.SearchText = "Portal";
        env.ManageVm.Items.Should().HaveCount(2);
        env.ManageVm.Items[0].IsSelected = true;

        int requestsBeforeSwitch = env.Handler.Requests;
        var itemsBefore = env.ManageVm.Items.Select(static t => t.AppId).ToList();

        env.ManageVm.ViewMode = LibraryViewMode.List;

        env.ManageVm.ViewMode.Should().Be(LibraryViewMode.List);
        env.ManageVm.IsListMode.Should().BeTrue();
        env.ManageVm.IsGridMode.Should().BeFalse();
        env.ManageVm.SearchText.Should().Be("Portal");
        env.ManageVm.SelectedCount.Should().Be(1);
        env.ManageVm.Items.Select(static t => t.AppId).Should().Equal(itemsBefore);
        env.Handler.Requests.Should().Be(requestsBeforeSwitch, "switching between Grid and List must perform zero network calls");
        new SettingsService(env.SettingsDir).ManageViewMode.Should().Be(LibraryViewMode.List);

        env.ManageVm.ViewMode = LibraryViewMode.Grid;
        env.ManageVm.IsGridMode.Should().BeTrue();
        new SettingsService(env.SettingsDir).ManageViewMode.Should().Be(LibraryViewMode.Grid);
    }

    [Fact]
    public async Task BuildsVm_SetViewMode_PreservesFilterAndDeepLink_WithZeroNetworkCalls()
    {
        var env = new Env(_root, host);
        env.WriteLua(400, "Portal");
        env.Vault.SaveText(400, "addappid(400)\naddappid(401, 1, \"k\")", "PresetOne");
        env.WriteLua(620, "Portal 2");

        await env.BuildsVm.LoadAsync();
        env.BuildsVm.QuickFilter = BuildsQuickFilter.WithStoredBuilds;
        env.BuildsVm.Items.Should().ContainSingle(g => g.AppId == 400);

        int requestsBeforeSwitch = env.Handler.Requests;
        env.BuildsVm.ViewMode = LibraryViewMode.List;

        env.BuildsVm.IsListMode.Should().BeTrue();
        env.BuildsVm.IsGridMode.Should().BeFalse();
        env.BuildsVm.QuickFilter.Should().Be(BuildsQuickFilter.WithStoredBuilds);
        env.BuildsVm.Items.Should().ContainSingle(g => g.AppId == 400);
        env.Handler.Requests.Should().Be(requestsBeforeSwitch);
        new SettingsService(env.SettingsDir).DepotsViewMode.Should().Be(LibraryViewMode.List);

        // Selecting a game in List mode opens Detail seamlessly and BackToGrid restores List mode.
        await env.BuildsVm.SelectAppAsync(620);
        env.BuildsVm.IsShowingDetail.Should().BeTrue();
        env.BuildsVm.ActiveGame!.AppId.Should().Be(620);

        env.BuildsVm.BackToGridCommand.Execute(null);
        env.BuildsVm.IsShowingGrid.Should().BeTrue();
        env.BuildsVm.IsListMode.Should().BeTrue("Depots List mode stays active after leaving Detail view");
    }

    // ── Cover Decoding Cache & Allocation-Free Tile Helpers ───────────────────

    [Fact]
    public void CoverCache_GetOrDecode_CachesFrozenBitmapAcrossMultipleLookups()
    {
        Directory.CreateDirectory(_root);
        string jpgPath = Path.Combine(_root, "sample_cover.jpg");

        host.On(_ =>
        {
            var bmp = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgr32, null, new byte[4 * 4 * 4], 4 * 4);
            var encoder = new JpegBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(jpgPath);
            encoder.Save(fs);
        });

        var covers = new CoverCache(new CountingOfflineHandler());
        var first = covers.GetOrDecode(400, jpgPath);
        var second = covers.TryGetDecoded(400);

        first.Should().NotBeNull();
        first!.IsFrozen.Should().BeTrue();
        ReferenceEquals(first, second).Should().BeTrue("decoded frozen covers must be reused directly from RAM");
    }

    [Fact]
    public void LuaTileViewModel_CachesAppIdTextAndReleaseLabelAcrossFilters()
    {
        var added = new DateTime(2026, 2, 14, 0, 0, 0, DateTimeKind.Utc);
        var tile = new LuaTileViewModel(400, "400.lua", added, "Portal", nameIsPlaceholder: false);

        tile.AppIdText.Should().Be("400");
        tile.Matches("400").Should().BeTrue();
        tile.Matches("port").Should().BeTrue();
        tile.Matches("999").Should().BeFalse();

        var filterData = new AppFilterData("game", ["Action"], true, false, false, 2022, new DateTime(2022, 2, 24), "24 Feb, 2022", false, null, null, false);
        tile.UpdateReleaseLabel(filterData);
        string firstLabel = tile.ReleaseLabel;
        firstLabel.Should().Be("Released 24 Feb, '22");

        tile.UpdateReleaseLabel(filterData);
        ReferenceEquals(firstLabel, tile.ReleaseLabel).Should().BeTrue("identical cached metadata must not reallocate ReleaseLabel");
    }

    // ── GPU Animation & Recycling Safety ──────────────────────────────────────

    [Fact]
    public void Motion_AnimateEntrance_RespectsReducedMotion_AndResetsRecycledHover()
    {
        host.On(_ =>
        {
            var original = Motion.AnimationsEnabledProvider;
            try
            {
                var panel = new Grid { Opacity = 0.2 };
                Motion.AnimationsEnabledProvider = static () => false;
                Motion.AnimateEntrance(panel, fromX: 20, fromY: 12);

                panel.Opacity.Should().Be(1);
                var transform = panel.RenderTransform.Should().BeOfType<TranslateTransform>().Subject;
                transform.X.Should().Be(0);
                transform.Y.Should().Be(0);

                // Enabled path uses GPU-only TranslateTransform + Opacity without exceptions.
                Motion.AnimationsEnabledProvider = static () => true;
                Motion.AnimateEntrance(panel, fromX: -18, duration: Motion.ViewTransitionDuration);
                panel.RenderTransform.Should().BeOfType<TranslateTransform>();

                // Recycling reset clears stale hover opacity immediately.
                var rowHost = new Grid();
                NameScope.SetNameScope(rowHost, new NameScope());
                var overlay = new Border { Name = "HoverOverlay", Opacity = 0.95 };
                rowHost.RegisterName("HoverOverlay", overlay);
                Motion.SetEnableSmoothHover(rowHost, true);

                Motion.ResetSmoothHover(rowHost);
                overlay.Opacity.Should().Be(0);
            }
            finally
            {
                Motion.AnimationsEnabledProvider = original;
            }
        });
    }

    // ── Virtualization & Live ViewMode Hosting ────────────────────────────────

    [Fact]
    public void Views_DeclareTruePixelRecyclingVirtualization_AndSwitcherMarkup()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LuaToolsGui.sln")))
            dir = dir.Parent;
        string viewsDir = Path.Combine(dir!.FullName, "src", "LuaToolsGui", "Views");

        foreach (string listFile in new[] { "ManageListView.xaml", "BuildsListView.xaml" })
        {
            string xaml = File.ReadAllText(Path.Combine(viewsDir, listFile));
            xaml.Should().Contain("VirtualizingPanel.IsVirtualizing=\"True\"", listFile);
            xaml.Should().Contain("VirtualizingPanel.VirtualizationMode=\"Recycling\"", listFile);
            xaml.Should().Contain("VirtualizingPanel.ScrollUnit=\"Pixel\"", listFile);
            xaml.Should().Contain("ScrollViewer.CanContentScroll=\"True\"", listFile);
        }

        foreach (string hostFile in new[] { "ManageView.xaml", "BuildsView.xaml" })
        {
            string xaml = File.ReadAllText(Path.Combine(viewsDir, hostFile));
            xaml.Should().Contain("VirtualizingPanel.VirtualizationMode=\"Recycling\"", hostFile);
            xaml.Should().Contain("views:ViewModeToggle", hostFile);
        }
    }

    [Fact]
    public async Task LiveViews_ManageViewAndBuildsView_SwitchBetweenGridAndList_UnderThemeHost()
    {
        var env = new Env(_root, host);
        env.WriteLua(400, "Portal");
        env.WriteLua(620, "Portal 2");
        await env.ManageVm.LoadAsync();
        env.ManageVm.CancelBackgroundPrefetch();
        await env.ManageVm.PrefetchTask;
        await env.BuildsVm.LoadAsync();

        var (manageView, manageWindow) = host.On(_ =>
        {
            var view = new ManageView(env.ManageVm);
            var win = MakeOffscreenWindow(view);
            win.Show();
            win.UpdateLayout();
            Flush(); // run Loaded → LoadAsync now, so WaitUntilNotLoadingAsync below really waits for it
            return (view, win);
        });
        try
        {
            await WaitUntilNotLoadingAsync(env.ManageVm);
            env.ManageVm.CancelBackgroundPrefetch();
            await env.ManageVm.PrefetchTask;

            host.On(_ =>
            {
                manageWindow.UpdateLayout();
                manageView.TileScroller.ItemsSource.Should().NotBeNull();
                manageView.GameListView.GameList.ItemsSource.Should().BeNull();
                var gridScroll = FindDescendant<ScrollViewer>(manageView.TileScroller);
                gridScroll.Should().NotBeNull();
                gridScroll!.CanContentScroll.Should().BeTrue();

                env.ManageVm.ViewMode = LibraryViewMode.List;
                manageWindow.UpdateLayout();
                manageView.TileScroller.ItemsSource.Should().BeNull();
                manageView.GameListView.GameList.ItemsSource.Should().NotBeNull();
                manageView.GameListView.GameList.ReadLocalValue(FrameworkElement.StyleProperty).Should().Be(DependencyProperty.UnsetValue);
                var listScroll = FindDescendant<ScrollViewer>(manageView.GameListView.GameList);
                listScroll.Should().NotBeNull();
                listScroll!.CanContentScroll.Should().BeTrue();

                env.ManageVm.ViewMode = LibraryViewMode.Grid;
                manageWindow.UpdateLayout();
                manageView.TileScroller.ItemsSource.Should().NotBeNull();
                manageView.GameListView.GameList.ItemsSource.Should().BeNull();
            });
        }
        finally
        {
            host.On(_ => manageWindow.Close());
            env.ManageVm.CancelBackgroundPrefetch();
            await env.ManageVm.PrefetchTask;
        }

        var (buildsView, buildsWindow) = host.On(_ =>
        {
            var view = new BuildsView(env.BuildsVm);
            var win = MakeOffscreenWindow(view);
            win.Show();
            win.UpdateLayout();
            Flush(); // run Loaded → LoadAsync now, so WaitUntilNotLoadingAsync below really waits for it
            return (view, win);
        });
        try
        {
            await WaitUntilNotLoadingAsync(env.BuildsVm);

            host.On(_ =>
            {
                buildsWindow.UpdateLayout();
                buildsView.GameGrid.ItemsSource.Should().NotBeNull();
                buildsView.GameListView.GameList.ItemsSource.Should().BeNull();
                var gridScroll = FindDescendant<ScrollViewer>(buildsView.GameGrid);
                gridScroll.Should().NotBeNull();
                gridScroll!.CanContentScroll.Should().BeTrue();

                env.BuildsVm.ViewMode = LibraryViewMode.List;
                buildsWindow.UpdateLayout();
                buildsView.GameGrid.ItemsSource.Should().BeNull();
                buildsView.GameListView.GameList.ItemsSource.Should().NotBeNull();
                buildsView.GameListView.GameList.ReadLocalValue(FrameworkElement.StyleProperty).Should().Be(DependencyProperty.UnsetValue);
                var listScroll = FindDescendant<ScrollViewer>(buildsView.GameListView.GameList);
                listScroll.Should().NotBeNull();
                listScroll!.CanContentScroll.Should().BeTrue();

                env.BuildsVm.ViewMode = LibraryViewMode.Grid;
                buildsWindow.UpdateLayout();
                buildsView.GameGrid.ItemsSource.Should().NotBeNull();
                buildsView.GameListView.GameList.ItemsSource.Should().BeNull();
            });
        }
        finally
        {
            host.On(_ => buildsWindow.Close());
        }
    }

    [Fact]
    public void ViewModeToggle_exposes_the_active_mode_to_UI_Automation_in_both_modes()
    {
        host.On(_ =>
        {
            var toggle = new ViewModeToggle();
            var other = new ViewModeToggle();
            static bool Selected(UIElement e) =>
                ((System.Windows.Automation.Provider.ISelectionItemProvider)System.Windows.Automation.Peers.UIElementAutomationPeer
                    .CreatePeerForElement(e)
                    .GetPattern(System.Windows.Automation.Peers.PatternInterface.SelectionItem)).IsSelected;

            toggle.GridButton.IsChecked.Should().BeTrue();
            Selected(toggle.GridButton).Should().BeTrue();
            Selected(toggle.ListButton).Should().BeFalse();

            toggle.ViewMode = LibraryViewMode.List; // binding / settings → radios
            toggle.ListButton.IsChecked.Should().BeTrue();
            toggle.GridButton.IsChecked.Should().BeFalse();
            Selected(toggle.ListButton).Should().BeTrue();
            Selected(toggle.GridButton).Should().BeFalse();

            toggle.GridButton.IsChecked = true; // user picks a radio → ViewMode
            toggle.ViewMode.Should().Be(LibraryViewMode.Grid);
            toggle.ListButton.IsChecked.Should().BeFalse();

            other.ViewMode.Should().Be(LibraryViewMode.Grid, "Manage's and Depots' toggles never share a radio group");
            other.GridButton.IsChecked.Should().BeTrue();
        });
    }

    // ── Audit regressions: hover lifecycle, row click/keyboard isolation, transforms, cover cache ──

    [Fact]
    public async Task ManageList_RealTemplate_HoverResyncsAfterTabSwitch_AndRowControlsNeverOpenDetail()
    {
        var env = new Env(_root, host);
        env.WriteLua(400, "Portal");
        env.WriteLua(620, "Portal 2");
        await env.ManageVm.LoadAsync();
        env.ManageVm.CancelBackgroundPrefetch();
        await env.ManageVm.PrefetchTask;
        env.ManageVm.ViewMode = LibraryViewMode.List;

        Window? window = null;
        ManageView? view = null;
        var originalMotion = Motion.AnimationsEnabledProvider;
        try
        {
            host.On(_ =>
            {
                Motion.AnimationsEnabledProvider = static () => false;
                view = new ManageView(env.ManageVm);
                window = MakeOffscreenWindow(view);
                window.Show();
                window.UpdateLayout();
                Flush();
            });
            await WaitUntilNotLoadingAsync(env.ManageVm);
            env.ManageVm.CancelBackgroundPrefetch();
            await env.ManageVm.PrefetchTask;

            host.On(_ =>
            {
                var list = view!.GameListView.GameList;
                window!.UpdateLayout();
                var row0 = RowRoot(list, 0);

                // FindName resolves the overlay inside the real DataTemplate (not a synthetic NameScope).
                row0.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
                var hovered = Named<FrameworkElement>(row0, "HoverOverlay");
                hovered.Opacity.Should().Be(1);

                // Tab switch: the view leaves and re-enters the tree without any MouseLeave.
                window.Content = null;
                Flush();
                window.Content = view;
                window.UpdateLayout();
                Flush();
                // Whether WPF reuses this container or realizes a fresh one, neither may come back lit.
                hovered.Opacity.Should().Be(0, "a card hovered when the tab switched must not come back stuck lit");
                Named<FrameworkElement>(RowRoot(list, 0), "HoverOverlay").Opacity.Should().Be(0);

                // Clicking the row's CheckBox / Remove button never opens the detail flyout.
                var row = RowRoot(list, 0);
                RaiseMouseUp(Descendant<CheckBox>(row));
                RaiseMouseUp(Descendant<Wpf.Ui.Controls.Button>(row));
                env.ManageVm.IsDetailOpen.Should().BeFalse();

                // Space/Enter act on the focused row, not on whatever row is SelectedItem.
                list.SelectedIndex = 0;
                var tile0 = (LuaTileViewModel)list.Items[0];
                var tile1 = (LuaTileViewModel)list.Items[1];
                var item1 = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(1);
                RaiseKey(item1, Key.Space);
                tile1.IsSelected.Should().BeTrue();
                tile0.IsSelected.Should().BeFalse();
                tile1.IsSelected = false;

                RaiseKey(item1, Key.Enter);
                env.ManageVm.SelectedTile.Should().BeSameAs(tile1);
                env.ManageVm.CloseDetailCommand.Execute(null);

                // A plain click on the row body still opens it.
                RaiseMouseUp(Descendant<TextBlock>(row, t => t.Text == tile0.Name));
                env.ManageVm.SelectedTile.Should().BeSameAs(tile0);
                env.ManageVm.CloseDetailCommand.Execute(null);
            });
        }
        finally
        {
            host.On(_ =>
            {
                window?.Close();
                Motion.AnimationsEnabledProvider = originalMotion;
            });
            env.ManageVm.CancelBackgroundPrefetch();
            await env.ManageVm.PrefetchTask;
        }
    }

    [Fact]
    public async Task BuildsList_RemoveButtonNeverOpensDetail_AndEnterOpensFocusedRow()
    {
        var env = new Env(_root, host);
        env.WriteLua(400, "Portal");
        env.WriteLua(620, "Portal 2");
        await env.BuildsVm.LoadAsync();
        env.BuildsVm.ViewMode = LibraryViewMode.List;

        Window? window = null;
        BuildsView? view = null;
        try
        {
            host.On(_ =>
            {
                view = new BuildsView(env.BuildsVm);
                window = MakeOffscreenWindow(view);
                window.Show();
                window.UpdateLayout();
                Flush();
            });
            await WaitUntilNotLoadingAsync(env.BuildsVm);

            host.On(_ =>
            {
                window!.UpdateLayout();
                var list = view!.GameListView.GameList;
                RaiseMouseUp(Descendant<Wpf.Ui.Controls.Button>(RowRoot(list, 0)));
                env.BuildsVm.IsShowingDetail.Should().BeFalse();

                list.SelectedIndex = 0;
                var game1 = (LuaTileViewModel)list.Items[1];
                RaiseKey((ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(1), Key.Enter);
                env.BuildsVm.ActiveGame.Should().BeSameAs(game1);
            });
        }
        finally
        {
            host.On(_ => window?.Close());
        }
    }

    [Fact]
    public void Motion_AnimateEntrance_KeepsForeignTransform_AndRapidRestartsNeverStrandOpacity()
    {
        host.On(_ =>
        {
            var original = Motion.AnimationsEnabledProvider;
            try
            {
                var scale = new ScaleTransform(1.5, 1.5);
                var scaled = new Grid { RenderTransform = scale };
                Motion.AnimationsEnabledProvider = static () => true;
                Motion.AnimateEntrance(scaled, fromX: 20, fromY: 8);
                scaled.RenderTransform.Should().BeSameAs(scale, "an existing transform is never replaced");
                scaled.GetAnimationBaseValue(UIElement.OpacityProperty).Should().Be(1.0);

                var offset = new TranslateTransform(0, 4);
                var nudged = new Grid { RenderTransform = offset };
                Motion.AnimateEntrance(nudged, fromY: 8);
                nudged.RenderTransform.Should().BeSameAs(offset);
                offset.Y.Should().Be(4, "a caller's own translate offset is never zeroed");

                var panel = new Grid();
                for (int i = 0; i < 5; i++)
                    Motion.AnimateEntrance(panel, fromY: 8, duration: Motion.ModeSwitchDuration);
                var translate = panel.RenderTransform.Should().BeOfType<TranslateTransform>().Subject;
                panel.GetAnimationBaseValue(UIElement.OpacityProperty).Should().Be(1.0);
                translate.GetAnimationBaseValue(TranslateTransform.YProperty).Should().Be(0.0);

                Motion.AnimationsEnabledProvider = static () => false;
                Motion.AnimateEntrance(panel, fromY: 8);
                panel.HasAnimatedProperties.Should().BeFalse("reduced motion leaves no clock behind");
                translate.HasAnimatedProperties.Should().BeFalse();
                panel.Opacity.Should().Be(1);
            }
            finally
            {
                Motion.AnimationsEnabledProvider = original;
            }
        });
    }

    [Fact]
    public async Task CoverCache_ConcurrentDecodeAndLookup_StaysBounded_AndNeverThrows()
    {
        Directory.CreateDirectory(_root);
        var paths = Enumerable.Range(0, 3).Select(i => WriteJpeg($"c{i}.jpg", 4 + i, 4)).ToArray();
        var covers = new CoverCache(new CountingOfflineHandler());

        await Parallel.ForEachAsync(Enumerable.Range(0, 1200), async (i, _) =>
        {
            long appid = i % 300;
            var image = i % 2 == 0
                ? covers.GetOrDecode(appid, paths[appid % 3])
                : covers.TryGetDecoded(appid) ?? covers.GetOrDecode(appid, paths[appid % 3]);
            image.Should().NotBeNull();
            image!.IsFrozen.Should().BeTrue();
            await Task.Yield();
        });

        covers.StrongDecodedCount.Should().BeLessThanOrEqualTo(CoverCache.MaxStrongDecodedEntries);
    }

    [Fact]
    public void CoverCache_CorruptFile_IsNull_HandleIsReleased_AndRewrittenPathDecodesFresh()
    {
        Directory.CreateDirectory(_root);
        var covers = new CoverCache(new CountingOfflineHandler());

        string corrupt = Path.Combine(_root, "corrupt.jpg");
        File.WriteAllBytes(corrupt, [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10]);
        covers.GetOrDecode(1, corrupt).Should().BeNull();
        covers.GetOrDecode(2, Path.Combine(_root, "missing.jpg")).Should().BeNull();

        string path = WriteJpeg("swap.jpg", 4, 4);
        var square = (BitmapSource)covers.GetOrDecode(3, path)!;
        File.Delete(path); // OnLoad must have closed the stream
        WriteJpeg("swap.jpg", 8, 4);
        var wide = (BitmapSource)covers.GetOrDecode(4, path)!;
        wide.PixelHeight.Should().BeLessThan(square.PixelHeight, "the rewritten file is decoded, not WPF's URI-cached copy");
    }

    [Theory]
    [InlineData(0.5, 248)]
    [InlineData(1.0, 248)]
    [InlineData(1.25, 310)]
    [InlineData(1.5, 372)]
    [InlineData(3.0, 460)]
    public void CoverCache_DecodeWidth_FollowsDpi_CappedAtSourceWidth(double scale, int expected) =>
        CoverCache.DecodeWidthForScale(scale).Should().Be(expected);

    private string WriteJpeg(string name, int width, int height)
    {
        string path = Path.Combine(_root, name);
        host.On(_ =>
        {
            var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, new byte[width * height * 4], width * 4);
            var encoder = new JpegBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(path);
            encoder.Save(fs);
        });
        return path;
    }

    /// <summary>Drain queued Loaded/layout work on the current (UI) dispatcher.</summary>
    private static void Flush() =>
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(static () => { }, System.Windows.Threading.DispatcherPriority.Background);

    private static FrameworkElement RowRoot(ListBox list, int index)
    {
        var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(index);
        container.Should().NotBeNull($"row {index} should be realized");
        return Named<FrameworkElement>(container, "RowRoot");
    }

    private static T Named<T>(DependencyObject root, string name) where T : FrameworkElement =>
        Descendant<T>(root, e => e.Name == name);

    private static T Descendant<T>(DependencyObject root, Func<T, bool>? match = null) where T : DependencyObject
    {
        var queue = new Queue<DependencyObject>([root]);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (node is T hit && !ReferenceEquals(node, root) && (match is null || match(hit))) return hit;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                queue.Enqueue(VisualTreeHelper.GetChild(node, i));
        }
        throw new InvalidOperationException($"no {typeof(T).Name} under {root}");
    }

    /// <summary>Bubbling MouseUp, as real input routes it: WPF derives the Direct MouseLeftButtonUp on each
    /// element of the route from it (raising MouseLeftButtonUp itself would only hit <paramref name="source"/>).</summary>
    private static void RaiseMouseUp(UIElement source) =>
        source.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.MouseUpEvent });

    /// <summary>Real keyboard input order: tunnelling PreviewKeyDown, then bubbling KeyDown unless handled.</summary>
    private static void RaiseKey(UIElement source, Key key)
    {
        var preview = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(source), 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        source.RaiseEvent(preview);
        if (!preview.Handled)
            source.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(source), 0, key) { RoutedEvent = Keyboard.KeyDownEvent });
    }

    private static Window MakeOffscreenWindow(UIElement content) => new()
    {
        Width = 1200,
        Height = 900,
        WindowStartupLocation = WindowStartupLocation.Manual,
        Left = -20000,
        Top = -20000,
        ShowActivated = false,
        ShowInTaskbar = false,
        Content = content,
    };

    private static async Task WaitUntilNotLoadingAsync(PagedListViewModel<LuaTileViewModel> vm)
    {
        if (!vm.IsLoading) return;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PropertyChangedEventHandler handler = (_, e) =>
        {
            if (e.PropertyName == nameof(PagedListViewModel<LuaTileViewModel>.IsLoading) && !vm.IsLoading)
                tcs.TrySetResult();
        };

        vm.PropertyChanged += handler;
        try
        {
            if (vm.IsLoading)
                await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            vm.PropertyChanged -= handler;
        }
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } found) return found;
        }
        return null;
    }
}
