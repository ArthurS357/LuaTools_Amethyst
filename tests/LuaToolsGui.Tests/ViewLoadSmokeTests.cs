using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Markup;
using AwesomeAssertions;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Lotes H1-H2: parses the migrated pages and dialogs against the live app resources. A property set to a type it cannot take
/// (FixesView's InfoBar.Message was a ScrollViewer, which throws on load) or a resource key that only fails at
/// runtime passes the text-based tests and the build, and surfaces as a crash on the first visit.
/// The pages need view models to construct, so the XAML is read as a loose document: x:Class and the event
/// handlers (which belong to the code-behind) are dropped, everything else is loaded as written.
/// </summary>
[Collection(ThemeHostCollection.Name)]
public class ViewLoadSmokeTests(ThemeHost host)
{
    [Theory]
    [InlineData("SettingsView")]
    [InlineData("SettingsBackupsSection")]
    [InlineData("SettingsManifestBackupSection")]
    [InlineData("FixesView")]
    [InlineData("ModeView")]
    [InlineData("PluginView")]
    [InlineData("OnboardingView")]
    [InlineData("ConfirmOverlay")]
    [InlineData("DropZone")]
    [InlineData("LaunchOptionsDialog")]
    [InlineData("LaunchOverridesDialog")]
    [InlineData("SaveBackupDialog")]
    [InlineData("GameCleanupDialog")]
    [InlineData("RemoveGameDialog")]
    [InlineData("ViewModeToggle")]
    [InlineData("ManageListView")]
    [InlineData("BuildsListView")]
    public void The_page_parses_measures_and_arranges_against_the_app_resources(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LuaToolsGui.sln")))
            dir = dir.Parent;
        string xaml = File.ReadAllText(Path.Combine(dir!.FullName, "src", "LuaToolsGui", "Views", name + ".xaml"));
        xaml = Regex.Replace(xaml, @"\s+x:Class=""[^""]+""", "");
        xaml = Regex.Replace(xaml, @"\s+(?:Click|Checked|Loaded|DataContextChanged|KeyDown|MouseLeftButtonUp|PreviewKeyDown|PreviewMouseWheel|DragEnter|DragLeave|DragOver|Drop)=""[^""]+""", "");
        // A loose document has no "local assembly" to resolve clr-namespaces against.
        xaml = Regex.Replace(xaml, @"clr-namespace:(LuaToolsGui\.\w+)""", "clr-namespace:$1;assembly=LuaTools\"");

        host.On(_ =>
        {
            var page = (FrameworkElement)XamlReader.Parse(xaml);
            page.Measure(new Size(900, 900));
            page.Arrange(new Rect(0, 0, 900, 900));
            // A Window is sized by its own chrome, not by Measure; for it, parsing is the check.
            if (page is not Window) page.DesiredSize.Height.Should().BeGreaterThan(0);
        });
    }
}
