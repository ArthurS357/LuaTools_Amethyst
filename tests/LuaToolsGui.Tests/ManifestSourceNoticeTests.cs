using System.IO;
using AwesomeAssertions;
using LuaToolsGui;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Pins the in-app notice about Steam's 2026-09 change to how depot manifests are obtained.
///
/// <para>
/// The notice is deliberately temporary (<see cref="AppConfig.ShowManifestSourceNotice"/>), and the
/// instructions for removing it name exactly where it lives. These checks keep that removal recipe true,
/// and catch the one failure mode a static card actually has: a mistyped resource key.
/// </para>
/// </summary>
public class ManifestSourceNoticeTests
{
    private static readonly string[] Keys =
    [
        nameof(Resources.Strings.Notice_Manifests_Title),
        nameof(Resources.Strings.Notice_Manifests_Source),
        nameof(Resources.Strings.Notice_Manifests_Lock),
        nameof(Resources.Strings.Notice_Manifests_Retry),
    ];

    /// <summary>Walks up from the test binaries to the repo, so this works from any run directory.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LuaToolsGui.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("LuaToolsGui.sln not found above the test output.");
    }

    private static string View(string name) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "src", "LuaToolsGui", "Views", name));

    [Fact]
    public void Every_notice_string_resolves_to_real_text()
    {
        // Strings.Get returns the KEY NAME when the resource is missing, so a typo renders as
        // "Notice_Manifests_Title" in the UI and nothing fails. That is the whole reason for this test:
        // the card has no logic, so a bad key is the only way it can break.
        foreach (string key in Keys)
        {
            string value = Resources.Strings.Get(key);
            value.Should().NotBe(key, "'{0}' must exist in Strings.resx, not fall back to its own name", key);
            value.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void The_notice_is_placed_exactly_where_removing_it_is_documented()
    {
        // AppConfig.ShowManifestSourceNotice tells whoever removes this to delete "its two usages". A
        // third placement would silently make those instructions wrong, and a deleted one would make the
        // notice quietly stop appearing where it was meant to.
        View("DownloadView.xaml").Should().Contain("<views:ManifestSourceNotice",
            "the Add page is where the source is chosen");
        View("BuildsView.xaml").Should().Contain("<views:ManifestSourceNotice",
            "the depot panel is where a manifest that cannot be fetched surfaces as the failure");

        int placements = Directory
            .EnumerateFiles(Path.Combine(RepoRoot(), "src", "LuaToolsGui", "Views"), "*.xaml")
            .Count(f => File.ReadAllText(f).Contains("<views:ManifestSourceNotice", StringComparison.Ordinal));

        placements.Should().Be(2, "AppConfig documents two usages; update that remark if this changes");
    }

    [Fact]
    public void The_card_carries_no_view_model_binding_so_it_can_be_dropped_anywhere()
    {
        // It is hosted by two pages whose DataContexts have nothing in common. A {Binding} would resolve
        // against whichever page it landed in and silently render blank on at least one of them.
        string xaml = View("ManifestSourceNotice.xaml");

        xaml.Should().NotContain("{Binding", "the notice must not depend on a host page's DataContext");
    }

    [Fact]
    public void The_switch_is_on_so_the_notice_actually_ships()
    {
        // Guards against the constant being flipped for a local experiment and committed that way.
        // Delete this test at the same time as the notice.
        AppConfig.ShowManifestSourceNotice.Should().BeTrue();
    }
}
