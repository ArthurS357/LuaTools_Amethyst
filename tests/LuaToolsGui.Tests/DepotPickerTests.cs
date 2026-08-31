using System.Globalization;
using System.IO;
using System.Reflection;
using AwesomeAssertions;
using LuaToolsGui.Services;
using LuaToolsGui.ViewModels;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// The Depots page's download picker: what it ticks by default, what it says when a depot cannot be
/// fetched, and that the names <c>BuildsView.xaml</c> binds to actually exist.
/// </summary>
public class DepotPickerTests
{
    private static ContentDepot Depot(string? os = null, string? language = null) =>
        new(1001, 1024, DlcAppId: null, IsShared: false, Os: os, Language: language, PublicManifestId: "555");

    // ── Auto-selection: exclusion, not inclusion ──────────────────────────────

    [Fact]
    public void A_depot_that_declares_nothing_is_picked()
    {
        // The common case by a wide margin: the bulk of a game is one platform- and language-agnostic
        // depot. If the rule were "include only what matches", the default selection would be EMPTY for
        // most titles, which reads as the feature being broken.
        BuildsViewModel.MatchesThisMachine(Depot()).Should().BeTrue();
    }

    [Theory]
    [InlineData("windows")]
    [InlineData("Windows")]
    [InlineData("windows,linux")]
    public void A_depot_for_windows_is_picked(string os) =>
        BuildsViewModel.MatchesThisMachine(Depot(os: os)).Should().BeTrue();

    [Theory]
    [InlineData("linux")]
    [InlineData("macos")]
    [InlineData("macosx")]
    public void A_depot_for_another_platform_is_not_picked(string os) =>
        BuildsViewModel.MatchesThisMachine(Depot(os: os)).Should().BeFalse();

    [Fact]
    public void A_depot_in_the_users_language_is_picked()
    {
        string mine = BuildsViewModel.SteamLanguageName(CultureInfo.CurrentUICulture);

        BuildsViewModel.MatchesThisMachine(Depot(language: mine)).Should().BeTrue();
    }

    [Fact]
    public void A_depot_in_another_language_is_not_picked()
    {
        // Whatever the current UI culture is, one of these two is not it — so the assertion holds on any
        // machine without pinning the test to a locale.
        bool japanesePicked = BuildsViewModel.MatchesThisMachine(Depot(language: "japanese"));
        bool thaiPicked = BuildsViewModel.MatchesThisMachine(Depot(language: "thai"));

        (japanesePicked && thaiPicked).Should().BeFalse();
    }

    [Fact]
    public void Platform_and_language_both_have_to_agree() =>
        BuildsViewModel.MatchesThisMachine(Depot(os: "linux", language: "english")).Should().BeFalse();

    // ── Steam language names ──────────────────────────────────────────────────

    [Theory]
    [InlineData("en-US", "english")]
    [InlineData("en-GB", "english")]
    [InlineData("pt-BR", "brazilian")]
    [InlineData("pt-PT", "portuguese")]
    [InlineData("zh-Hans", "schinese")]
    [InlineData("zh-Hant", "tchinese")]
    [InlineData("ko-KR", "koreana")]
    [InlineData("ja-JP", "japanese")]
    [InlineData("de-DE", "german")]
    [InlineData("nb-NO", "norwegian")]
    public void Maps_a_culture_to_the_name_steam_uses(string culture, string expected) =>
        BuildsViewModel.SteamLanguageName(new CultureInfo(culture)).Should().Be(expected);

    [Fact]
    public void An_unmapped_language_falls_back_to_english() =>
        // English is what a depot list uses when a game ships one language, so it is the useful default
        // rather than simply the safe one.
        BuildsViewModel.SteamLanguageName(new CultureInfo("sw-KE")).Should().Be("english");

    // ── Destination folder ────────────────────────────────────────────────────

    [Fact]
    public void The_downloads_folder_resolves_to_a_real_directory()
    {
        // Read from FOLDERID_Downloads rather than assumed to be %USERPROFILE%\Downloads: anyone who moved
        // Downloads onto a bigger drive is exactly the person about to fetch tens of GB.
        string folder = BuildsViewModel.DownloadsFolder();

        folder.Should().NotBeNullOrWhiteSpace();
        Path.IsPathRooted(folder).Should().BeTrue();
    }

    // ── Byte formatting ───────────────────────────────────────────────────────

    // The culture is passed in rather than assigned to CultureInfo.CurrentCulture: these tests run on
    // pooled threads in parallel with everything else, and a culture change there outlives the test.

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(-1L, "0 B")]
    [InlineData(512L, "512 B")]
    [InlineData(1024L, "1 KB")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(1048576L, "1 MB")]
    [InlineData(4823449600L, "4.49 GB")]
    public void Formats_a_byte_count(long bytes, string expected) =>
        ByteSize.Format(bytes, CultureInfo.InvariantCulture).Should().Be(expected);

    [Fact]
    public void A_size_is_formatted_for_the_users_locale() =>
        // Sizes are user-facing text, so they follow the user's separators rather than the invariant ones.
        ByteSize.Format(1536, new CultureInfo("pt-BR")).Should().Be("1,5 KB");

    [Fact]
    public void Zero_is_a_real_answer_here_unlike_the_depot_meta_line() =>
        // "0 B free" has to be sayable. The meta-line formatter returns "" for zero instead, because there
        // zero means "size unknown" and would collapse the separator dots around it.
        ByteSize.Format(0, CultureInfo.InvariantCulture).Should().Be("0 B");

    // ── Failure messages ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(DepotFailure.ToolNotPinned)]
    [InlineData(DepotFailure.ToolUnavailable)]
    [InlineData(DepotFailure.SteamNotFound)]
    [InlineData(DepotFailure.NoKeys)]
    [InlineData(DepotFailure.NoKeyForDepot)]
    [InlineData(DepotFailure.BadKey)]
    [InlineData(DepotFailure.NoManifest)]
    [InlineData(DepotFailure.SignInRequired)]
    [InlineData(DepotFailure.NotEnoughSpace)]
    [InlineData(DepotFailure.DownloaderFailed)]
    public void Every_failure_has_a_message(DepotFailure failure)
    {
        string text = DepotErrorText.Describe(failure, 1001, "detail");

        text.Should().NotBeNullOrWhiteSpace();
        // A resource lookup that misses returns the KEY, which would ship a "Depot_Err_…" string to the user.
        text.Should().NotStartWith("Depot_Err_");
    }

    [Fact]
    public void Success_has_no_message() =>
        DepotErrorText.Describe(DepotFailure.None, null, null).Should().BeEmpty();

    [Fact]
    public void A_missing_pin_says_the_feature_is_off_rather_than_that_something_failed()
    {
        string text = DepotErrorText.Describe(DepotFailure.ToolNotPinned, null, null);

        text.Should().NotBe(DepotErrorText.Describe(DepotFailure.ToolUnavailable, null, null));
    }

    [Fact]
    public void The_space_message_renders_both_figures()
    {
        // Both figures, in the right units and the right order. The decimal SEPARATOR is the user's, so the
        // assertion is on the units and the digits rather than on a literal "4.49".
        string text = DepotErrorText.Describe(DepotFailure.NotEnoughSpace, null, "4823449600/1048576");

        text.Should().Contain("GB").And.Contain("MB");
        text.Should().MatchRegex(@"4[.,]49\s*GB");
        text.Should().MatchRegex(@"1\s*MB");
        text.Should().NotContain("—"); // the degraded form, which would mean the pair failed to parse
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("1/2/3")]
    [InlineData("abc/def")]
    public void A_malformed_space_detail_degrades_instead_of_showing_a_slash(string? detail)
    {
        string text = DepotErrorText.Describe(DepotFailure.NotEnoughSpace, null, detail);

        text.Should().NotBeNullOrWhiteSpace();
        text.Should().NotContain("/");
    }

    [Fact]
    public void The_depot_id_reaches_the_message() =>
        DepotErrorText.Describe(DepotFailure.NoKeyForDepot, 228990, null).Should().Contain("228990");

    // ── Bindings: WPF fails these silently ────────────────────────────────────

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LuaToolsGui.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("LuaToolsGui.sln not found above the test output.");
    }

    private static string BuildsViewXaml() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "src", "LuaToolsGui", "Views", "BuildsView.xaml"));

    private static bool HasPublicMember(Type type, string name) =>
        type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance) is not null;

    [Theory]
    [InlineData("DepotPicks")]
    [InlineData("IsDepotPickerOpen")]
    [InlineData("HasDepotPicks")]
    [InlineData("IsDepotDownloadAvailable")]
    [InlineData("DepotOutDir")]
    [InlineData("SpaceLabel")]
    [InlineData("HasEnoughSpace")]
    [InlineData("DepotConfirmLabel")]
    [InlineData("IsDepotStripVisible")]
    // No IsDepotDownloading / DepotProgress / DepotStatus row here: depot downloads moved to the app-wide
    // queue in 1.7.0, so this page no longer owns a live download to describe. Its strip reports only the
    // handoff, and the progress it used to draw is asserted on the Downloads page instead.
    [InlineData("OpenDownloadsCommand")]
    [InlineData("DepotDownloadError")]
    [InlineData("HasDepotError")]
    [InlineData("DepotDownloadDone")]
    [InlineData("HasDepotResult")]
    [InlineData("StartDepotDownloadCommand")]
    [InlineData("ConfirmDepotDownloadCommand")]
    [InlineData("CancelDepotDownloadCommand")]
    [InlineData("ChangeDepotFolderCommand")]
    [InlineData("SelectAllDepotsCommand")]
    [InlineData("SelectNoDepotsCommand")]
    [InlineData("OpenDepotFolderCommand")]
    public void The_page_exposes_what_the_view_binds(string member)
    {
        BuildsViewXaml().Should().Contain(member);
        HasPublicMember(typeof(BuildsViewModel), member).Should().BeTrue();
    }

    [Theory]
    [InlineData("IsSelected")]
    [InlineData("CanPick")]
    [InlineData("BlockedReason")]
    [InlineData("Title")]
    [InlineData("Meta")]
    public void The_pick_row_exposes_what_its_template_binds(string member)
    {
        BuildsViewXaml().Should().Contain(member);
        HasPublicMember(typeof(DepotPickRow), member).Should().BeTrue();
    }

    [Fact]
    public void The_picker_offers_a_way_out_of_every_state()
    {
        string xaml = BuildsViewXaml();

        // Empty, refused and handed-over each need somewhere for the eye to land. Since 1.7.0 the running
        // state is no longer one of this page's: the selection is queued and the Downloads page owns it
        // from there, so "sent" has to name where it went or the click looks like it did nothing.
        xaml.Should().Contain("Builds_Depot_NoCandidates");   // empty
        xaml.Should().Contain("DepotDownloadError");          // refused before queueing
        xaml.Should().Contain("DepotDownloadDone");           // handed over
        xaml.Should().Contain("OpenDownloadsCommand");        // ...and a way to follow it
        xaml.Should().Contain("CancelDepotDownloadCommand");  // always a way out of the picker
    }
}
