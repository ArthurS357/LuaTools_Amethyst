using System.IO;
using AwesomeAssertions;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Lote G split ManageView and BuildsView into controls. The controls have to stay in the folder the ratchet,
/// ViewParseTests and AccessibilityDangerTests scan (Views/*.xaml, not recursive): a control moved elsewhere
/// would take its literals out of the count.
/// </summary>
[Collection(ThemeHostCollection.Name)]
public class ViewSplitTests(ThemeHost host)
{
    private static string ViewsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LuaToolsGui.sln")))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, "src", "LuaToolsGui", "Views");
    }

    [Theory]
    [InlineData("ManageDetailFlyout")]
    [InlineData("BuildsDetailPane")]
    [InlineData("BuildsDepotPicker")]
    public void The_split_controls_sit_in_the_scanned_views_folder(string name)
    {
        var scanned = Directory.GetFiles(ViewsDir(), "*.xaml").Select(Path.GetFileNameWithoutExtension);

        scanned.Should().Contain(name);
        File.Exists(Path.Combine(ViewsDir(), name + ".xaml.cs")).Should().BeTrue();
    }

    [Theory]
    [InlineData("ManageView.xaml")]
    [InlineData("BuildsView.xaml")]
    [InlineData("BuildsDetailPane.xaml")]
    public void No_page_file_creeps_back_to_800_lines(string file) =>
        File.ReadAllLines(Path.Combine(ViewsDir(), file)).Length.Should().BeLessThan(800);

    [Fact]
    public void Every_page_file_that_binds_ElementName_Root_declares_it()
    {
        // The split controls reach the page view model through a "Root" element of their own. A template moved
        // into a file without one binds to nothing, silently: no exception, the buttons just stop working.
        var missing = Directory.GetFiles(ViewsDir(), "*.xaml")
            .Select(f => (File: Path.GetFileName(f), Text: File.ReadAllText(f)))
            .Where(f => f.Text.Contains("ElementName=Root") && !f.Text.Contains("x:Name=\"Root\""))
            .Select(f => f.File);

        missing.Should().BeEmpty();
    }

    [Fact]
    public void The_split_controls_load_against_the_app_resources()
    {
        // Catches a StaticResource key that stayed behind in the parent file: it only throws on load.
        host.On(_ =>
        {
            new LuaToolsGui.Views.ManageDetailFlyout().Should().NotBeNull();
            new LuaToolsGui.Views.BuildsDetailPane().Should().NotBeNull();
            new LuaToolsGui.Views.BuildsDepotPicker().Should().NotBeNull();
        });
    }
}
