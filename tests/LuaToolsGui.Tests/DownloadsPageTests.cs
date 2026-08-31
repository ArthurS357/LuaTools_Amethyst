using System.IO;
using System.Reflection;
using AwesomeAssertions;
using LuaToolsGui.Services;
using LuaToolsGui.Services.Downloads;
using LuaToolsGui.ViewModels;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// The Downloads page's contract with its view, and the proof that every download path in the app now
/// goes through the one queue rather than its own inline copy.
/// </summary>
public class DownloadsPageTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LuaToolsGui.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("LuaToolsGui.sln not found above the test output.");
    }

    private static string Source(params string[] parts) =>
        File.ReadAllText(Path.Combine([RepoRoot(), "src", "LuaToolsGui", .. parts]));

    private static string DownloadsViewXaml() => Source("Views", "DownloadsView.xaml");

    private static bool HasPublicMember(Type type, string name) =>
        type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance) is not null
        || type.GetMethod(name, BindingFlags.Public | BindingFlags.Instance) is not null;

    // ── The view's bindings resolve ──────────────────────────────────

    [Theory]
    [InlineData("Queue")]
    [InlineData("HasItems")]
    [InlineData("HasHistory")]
    [InlineData("IsEmpty")]
    [InlineData("CancelCommand")]
    [InlineData("RetryCommand")]
    [InlineData("PauseCommand")]
    [InlineData("ResumeCommand")]
    [InlineData("RemoveCommand")]
    [InlineData("MoveUpCommand")]
    [InlineData("MoveDownCommand")]
    [InlineData("ReviewCommand")]
    [InlineData("CopyAppIdCommand")]
    [InlineData("ShowInFolderCommand")]
    [InlineData("CopyHistoryAppIdCommand")]
    [InlineData("ShowHistoryInFolderCommand")]
    [InlineData("ClearHistoryCommand")]
    [InlineData("RemoveHistoryEntryCommand")]
    public void The_page_exposes_what_the_view_binds(string member)
    {
        DownloadsViewXaml().Should().Contain(member);
        HasPublicMember(typeof(DownloadsViewModel), member).Should().BeTrue();
    }

    [Theory]
    [InlineData("Title")]
    [InlineData("SubTitle")]
    [InlineData("StatusLabel")]
    [InlineData("SizeLabel")]
    [InlineData("RateLabel")]
    [InlineData("EtaLabel")]
    [InlineData("Percent")]
    [InlineData("IsIndeterminate")]
    [InlineData("ShowProgress")]
    [InlineData("NeedsAction")]
    [InlineData("HasDetail")]
    [InlineData("HasMessage")]
    [InlineData("CanPause")]
    [InlineData("CanResume")]
    [InlineData("CanCancel")]
    [InlineData("CanRetry")]
    [InlineData("CanRemove")]
    [InlineData("CanReorder")]
    [InlineData("CanCopyAppId")]
    [InlineData("CanShowInFolder")]
    [InlineData("CoverPath")]
    public void A_queue_row_exposes_what_its_template_binds(string member)
    {
        DownloadsViewXaml().Should().Contain(member);
        HasPublicMember(typeof(DownloadItem), member).Should().BeTrue();
    }

    [Theory]
    [InlineData("Title")]
    [InlineData("SubTitle")]
    [InlineData("StatusLabel")]
    [InlineData("SizeLabel")]
    [InlineData("WhenLabel")]
    [InlineData("Failed")]
    [InlineData("CanCopyAppId")]
    [InlineData("CanShowInFolder")]
    public void A_history_row_exposes_what_its_template_binds(string member)
    {
        DownloadsViewXaml().Should().Contain(member);
        HasPublicMember(typeof(DownloadHistoryEntry), member).Should().BeTrue();
    }

    [Fact]
    public void The_page_has_somewhere_for_the_eye_to_land_in_every_state()
    {
        string xaml = DownloadsViewXaml();

        xaml.Should().Contain("Downloads_Empty");            // nothing queued and nothing in history
        xaml.Should().Contain("Downloads_Section_Active");    // working
        xaml.Should().Contain("Downloads_ActionRequired");    // stalled on the user
        xaml.Should().Contain("Downloads_Section_History");   // finished
        xaml.Should().Contain("DangerBrush");                 // failed, told apart from cancelled
    }

    [Fact]
    public void Every_colour_on_the_page_comes_from_the_theme()
    {
        // Themes/Colors.xaml is the single palette; a literal here is what makes one card stop following
        // the accent when the user changes it.
        string xaml = DownloadsViewXaml();

        System.Text.RegularExpressions.Regex.Matches(xaml, @"(Background|Foreground|BorderBrush)=""#")
            .Should().BeEmpty();
    }

    [Fact]
    public void Every_interactive_button_is_named_for_a_screen_reader()
    {
        string xaml = DownloadsViewXaml();

        // A row of icon-only buttons (pause, move up, move down, remove) is unusable without these:
        // the glyph carries the meaning and there is no text to fall back on.
        int buttons = System.Text.RegularExpressions.Regex.Matches(xaml, @"<ui:Button\b").Count;
        int named = System.Text.RegularExpressions.Regex.Matches(xaml, @"AutomationProperties\.Name=").Count;

        buttons.Should().BeGreaterThan(0);
        named.Should().BeGreaterThanOrEqualTo(buttons);
    }

    [Fact]
    public void The_page_is_reachable_from_the_nav_rail()
    {
        Source("MainWindow.xaml").Should().Contain("views:DownloadsView");
        Source("MainWindow.xaml").Should().Contain("Nav_Downloads");
        HasPublicMember(typeof(MainWindow), "NavigateToDownloads").Should().BeTrue();
    }

    // ── One queue, not four ──────────────────────────────────────────

    [Theory]
    [InlineData("ViewModels", "DownloadViewModel.cs")]           // the Add page
    [InlineData("Services", "PluginAddService.cs")]              // the Steam store plugin
    [InlineData("Services", "HttpServerService.cs")]             // the HTTP bridge
    [InlineData("ViewModels", "BuildsViewModel.DepotDownload.cs")] // the Depots page
    public void Every_download_entry_point_enqueues_instead_of_downloading_inline(string dir, string file)
    {
        string src = Source(dir, file);

        src.Should().Contain("Enqueue(");

        // The three manifest paths had a byte-for-byte copy of the same fetch call and the same zip-sniff
        // install; the depot page had its own run loop. None of them may call those directly any more, or
        // the duplication this phase removed grows straight back.
        src.Should().NotContain("api.DownloadManifestAsync");
        src.Should().NotContain("_api.DownloadManifestAsync");
        src.Should().NotContain("hubcap.DownloadManifestAsync");
        src.Should().NotContain("_hubcap.DownloadManifestAsync");
        src.Should().NotContain("DownloadDepotsAsync");
    }

    [Fact]
    public void The_staging_folder_is_not_copied_a_fourth_time()
    {
        // Two services stage downloads (LuaToolsApiClient and HubcapService) and the depot keys folder is
        // deliberately separate from both. The queue schedules those services; it must not open its own.
        Source("Services", "Downloads", "DownloadQueue.cs").Should().NotContain("GetTempPath");
        Source("Services", "Downloads", "ManifestJobFactory.cs").Should().NotContain("GetTempPath");
    }

    [Fact]
    public void The_queue_carries_no_telemetry_auto_update_or_elevation()
    {
        foreach (string file in new[]
                 {
                     "DownloadQueue.cs", "DownloadItem.cs", "DownloadJob.cs",
                     "DownloadHistory.cs", "DownloadProgress.cs", "ManifestJobFactory.cs",
                 })
        {
            string src = Source("Services", "Downloads", file);
            foreach (string banned in new[]
                     { "Analytics", "Telemetry", "DonateKeys", "runas", "UseShellExecute = true", "SteamAutoCrack" })
                src.Should().NotContain(banned, $"{file} must not reintroduce {banned}");
        }
    }

    [Fact]
    public void The_job_factory_installs_through_LuaInstaller_rather_than_writing_to_Steam_itself()
    {
        string src = Source("Services", "Downloads", "ManifestJobFactory.cs");

        // The queue adds scheduling, not permission: the integrity, staging and quarantine rules all live
        // in the services the delegates call, and a job that wrote files directly would sidestep them.
        src.Should().Contain("installer.InstallZip");
        src.Should().Contain("installer.InstallLua");
        src.Should().NotContain("File.Copy");
        src.Should().NotContain("File.WriteAllBytes");
    }

    [Fact]
    public void A_manifest_job_is_keyed_on_the_game_so_two_sources_cannot_race_one_lua_file()
    {
        // Both sources write the same <appid>.lua into the same folder, so a second one running alongside
        // would race the installer rather than give the user a choice.
        ManifestJobFactory.ManifestKey(730).Should().Be("manifest:730");
        ManifestJobFactory.ManifestKey(730).Should().Be(ManifestJobFactory.ManifestKey(730));
        ManifestJobFactory.ManifestKey(440).Should().NotBe(ManifestJobFactory.ManifestKey(730));
    }

    [Fact]
    public void A_depot_job_and_a_manifest_job_for_one_game_are_not_the_same_download()
    {
        // They write to different places and can honestly run at once.
        ManifestJobFactory.DepotKey(730).Should().NotBe(ManifestJobFactory.ManifestKey(730));
    }

    [Fact]
    public void A_bare_lua_download_is_detected_by_its_bytes_and_not_by_its_name()
    {
        // Every manifest download is saved as "<appid>.zip", but some sources return a bare .lua. Trusting
        // the extension throws "End of Central Directory record could not be found".
        string dir = Path.Combine(Path.GetTempPath(), "luatools_zipsniff_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string zip = Path.Combine(dir, "real.zip");
            File.WriteAllBytes(zip, [0x50, 0x4B, 0x03, 0x04, 0x00]);
            string lua = Path.Combine(dir, "bare.zip");
            File.WriteAllText(lua, "addappid(730)");

            ManifestJobFactory.IsZip(zip).Should().BeTrue();
            ManifestJobFactory.IsZip(lua).Should().BeFalse();
            ManifestJobFactory.IsZip(Path.Combine(dir, "gone.zip")).Should().BeFalse();
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }
    }
}
