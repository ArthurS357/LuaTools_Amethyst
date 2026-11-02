using System.IO;
using System.Reflection;
using AwesomeAssertions;
using LuaToolsGui.Models;
using LuaToolsGui.Services;
using LuaToolsGui.ViewModels;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// CloudRedirect was removed: its repository and account are gone, and it is outside this fork's privacy and
/// trust policy. These pin the removal so it cannot creep back piecemeal: no code path, string, button or
/// source repository for it. The <see cref="UnlockerMode.CloudRedirect"/> enum member is the one thing that
/// stays, as a tombstone (see <c>ActiveBackendTests</c>).
/// </summary>
public class CloudRedirectRemovalTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LuaToolsGui.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("LuaToolsGui.sln not found above the test output.");
    }

    private static string Src(params string[] path) => Path.Combine([RepoRoot(), "src", "LuaToolsGui", .. path]);

    [Fact]
    public void No_service_downloads_or_launches_it()
    {
        typeof(UnlockerService).Assembly.GetTypes()
            .Select(t => t.Name)
            .Should().NotContain(n => n.Contains("CloudRedirect", StringComparison.OrdinalIgnoreCase));
        File.Exists(Src("Services", "CloudRedirectService.cs")).Should().BeFalse();
    }

    [Fact]
    public void No_source_repository_is_configured_for_it()
    {
        typeof(AppConfig).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => f.Name)
            .Should().NotContain(n => n.Contains("CloudRedirect", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_mode_list_and_the_mode_page_carry_no_trace_of_it()
    {
        UnlockerService.AllModes.Select(d => d.Mode).Should().NotContain(UnlockerMode.CloudRedirect);

        // No command, toggle or panel property on the page or the cards.
        typeof(ModeViewModel).GetMembers().Concat(typeof(ModeCardViewModel).GetMembers())
            .Select(m => m.Name)
            .Should().NotContain(n => n.Contains("CloudRedirect", StringComparison.OrdinalIgnoreCase)
                                      || n == "ShowManage");

        // Nothing hidden in the markup either (a collapsed panel is still shipped UI).
        File.ReadAllText(Src("Views", "ModeView.xaml")).Should().NotContainEquivalentOf("CloudRedirect");
    }

    [Fact]
    public void No_resource_string_mentions_it_in_any_language()
    {
        foreach (string file in Directory.GetFiles(Src("Resources"), "Strings*.resx")
                     .Append(Src("Resources", "Strings.Designer.cs")))
            File.ReadAllText(file).Should().NotContainEquivalentOf("CloudRedirect", Path.GetFileName(file));
    }

    [Fact]
    public void The_mode_kind_that_existed_only_for_its_installer_is_gone()
    {
        // The CLI flow (download an exe and RUN it) served CloudRedirect alone. With it gone, no Mode
        // executes a downloaded binary.
        Enum.GetNames<ModeKind>().Should().BeEquivalentTo(["Loose", "Zip"]);
    }
}
