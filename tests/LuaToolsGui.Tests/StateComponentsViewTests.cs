using System.IO;
using AwesomeAssertions;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>Lote D: the views consume the state components and the closable InfoBar. Read as text, like ViewParseTests.</summary>
public class StateComponentsViewTests
{
    private static string View(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LuaToolsGui.sln")))
            dir = dir.Parent;
        return File.ReadAllText(Path.Combine(dir!.FullName, "src", "LuaToolsGui", "Views", name));
    }

    [Fact]
    public void The_orphan_banner_is_a_closable_InfoBar_bound_two_way()
    {
        string xaml = View("FixesView.xaml");

        xaml.Should().Contain("<ui:InfoBar");
        xaml.Should().Contain("IsClosable=\"True\"");
        xaml.Should().Contain("IsOpen=\"{Binding ShowBanner, Mode=TwoWay}\"");
        xaml.Should().NotContain("<Expander", "the banner's names now sit in the InfoBar message");
    }

    [Fact]
    public void Downloads_history_empty_state_uses_EmptyStateStyle_and_keeps_its_text()
    {
        string xaml = View("DownloadsView.xaml");

        xaml.Should().Contain("EmptyStateStyle");
        xaml.Should().Contain("Downloads_Empty");
        xaml.Should().Contain("<ProgressBar", "the queue rows' progress bars are untouched");
    }

    [Fact]
    public void The_add_page_shows_a_loading_state_while_searching()
    {
        string xaml = View("DownloadView.xaml");

        xaml.Should().Contain("LoadingStateStyle");
        xaml.Should().Contain("Binding IsSearching");
    }
}
