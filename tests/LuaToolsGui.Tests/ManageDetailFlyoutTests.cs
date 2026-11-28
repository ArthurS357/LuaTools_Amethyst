using System.IO;
using System.Windows;
using System.Xml.Linq;
using AwesomeAssertions;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Regression guards for <c>ManageDetailFlyout.xaml</c> (hosted by <c>ManageView.xaml</c>):
/// on shorter windows the flyout's Cover, About and Actions sections must scroll together inside a
/// vertical <c>ScrollViewer</c> (keeping the close button pinned at the top), and Developer/Publisher
/// labels must auto-size with a gap so localized labels never collide with the studio name.
/// </summary>
[Collection(ThemeHostCollection.Name)]
public class ManageDetailFlyoutTests(ThemeHost host)
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static string ViewsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LuaToolsGui.sln")))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, "src", "LuaToolsGui", "Views");
    }

    private static T? FindFirst<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindFirst<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private static XDocument LoadView(string fileName) =>
        XDocument.Parse(File.ReadAllText(Path.Combine(ViewsDir(), fileName)));

    [Fact]
    public void Detail_flyout_wraps_about_and_actions_in_vertical_scrollviewer_while_keeping_close_pinned()
    {
        var manageDoc = LoadView("ManageView.xaml");
        manageDoc.Descendants().Should().Contain(e => e.Name.LocalName == "ManageDetailFlyout",
            "ManageView hosts the detail drawer via ManageDetailFlyout");

        var doc = LoadView("ManageDetailFlyout.xaml");
        var scrollers = doc.Descendants(Wpf + "ScrollViewer").ToList();

        scrollers.Should().ContainSingle(
            "the flyout body should use a single outer ScrollViewer instead of a nested inner scroller");

        var scroller = scrollers[0];
        ((string?)scroller.Attribute("Grid.Row")).Should().Be("1",
            "the ScrollViewer must sit in the star row below the pinned close button");
        ((string?)scroller.Attribute("VerticalScrollBarVisibility")).Should().Be("Auto");
        ((string?)scroller.Attribute("HorizontalScrollBarVisibility")).Should().Be("Disabled");

        string scrollerXml = scroller.ToString();
        scrollerXml.Should().Contain("Manage_AboutHeader", "the About section must live inside the ScrollViewer");
        scrollerXml.Should().Contain("Manage_ActionsHeader", "the Actions section must live inside the ScrollViewer");
        scrollerXml.Should().Contain("Manage_RemoveGame", "bottom action buttons must be scrollable and never clipped");
        scrollerXml.Should().NotContain("CloseDetailCommand", "the Close button must remain pinned outside the ScrollViewer");
        scrollerXml.Should().NotContain("MinHeightToVis", "About must stay visible inside the ScrollViewer rather than collapsing on short windows");
    }

    [Fact]
    public void Developer_and_publisher_rows_auto_size_label_column_and_separate_value()
    {
        var doc = LoadView("ManageDetailFlyout.xaml");

        foreach (string labelKey in new[] { "Manage_Info_Developer", "Manage_Info_Publisher" })
        {
            var label = doc.Descendants(Wpf + "TextBlock")
                .Single(tb => ((string?)tb.Attribute("Text"))?.Contains(labelKey, StringComparison.Ordinal) == true);
            var rowGrid = label.Parent!;
            rowGrid.Name.Should().Be(Wpf + "Grid");

            var firstCol = rowGrid.Element(Wpf + "Grid.ColumnDefinitions")!.Elements(Wpf + "ColumnDefinition").First();
            ((string?)firstCol.Attribute("Width")).Should().Be("Auto",
                $"{labelKey} must auto-size so localized labels (e.g. Desenvolvedor) do not overflow into the value");
            ((string?)firstCol.Attribute("SharedSizeGroup")).Should().Be("MetaLabel",
                $"{labelKey} must share its label column width with sibling metadata rows");
            rowGrid.Ancestors().Should().Contain(
                a => string.Equals((string?)a.Attribute("Grid.IsSharedSizeScope"), "True", StringComparison.Ordinal),
                $"{labelKey} requires an ancestor with Grid.IsSharedSizeScope=\"True\" for SharedSizeGroup to align");

            var valueBlock = rowGrid.Elements(Wpf + "TextBlock")
                .Single(tb => (string?)tb.Attribute("Grid.Column") == "1");
            ((string?)valueBlock.Attribute("Margin")).Should().NotBeNullOrWhiteSpace(
                $"{labelKey} value needs a left spacing token so it never touches the label");
        }
    }

    [Fact]
    public void Detail_flyout_measures_and_arranges_cleanly_at_short_window_height()
    {
        host.On(_ =>
        {
            // Shorter than the About + Actions content, so the body has to scroll instead of growing past the window.
            const double height = 200;
            var flyout = new LuaToolsGui.Views.ManageDetailFlyout();
            flyout.Measure(new Size(900, height));
            flyout.Arrange(new Rect(0, 0, 900, height));
            flyout.UpdateLayout();

            var scroller = FindFirst<System.Windows.Controls.ScrollViewer>(flyout);
            scroller.Should().NotBeNull();
            scroller!.ActualHeight.Should().BeGreaterThan(0).And.BeLessThan(height,
                "the close row is pinned above it, so the scroller gets what is left, not its content's height");
            scroller.ExtentHeight.Should().BeGreaterThan(scroller.ViewportHeight,
                "content taller than the viewport must be reachable by scrolling, not clipped");
        });
    }
}
