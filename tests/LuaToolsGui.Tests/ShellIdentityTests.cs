using System.IO;
using System.Xml.Linq;
using AwesomeAssertions;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Covers the two things the very first screen says about itself: which product this is, and whether the
/// navigation rail speaks the user's language.
///
/// <para>
/// Both were wrong in a way no build or click-through would report. The Home greeting read "Welcome to
/// LuaTools" while the window title, the About page and the README all say LuaTools Amethyst — the README
/// tells users to identify their build from exactly those surfaces, so the first line of the first screen
/// was quietly contradicting the fork's own identity check. And the rail's Plugin entry was a hardcoded
/// English literal sitting between eight localized ones, so a translated UI had one English word in the
/// first thing anyone looks at.
/// </para>
/// </summary>
public class ShellIdentityTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LuaToolsGui.sln")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new DirectoryNotFoundException("LuaToolsGui.sln not found above the test output.");
    }

    private static string MainWindowXaml() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "src", "LuaToolsGui", "MainWindow.xaml"));

    private static IEnumerable<(string File, string Value)> ValuesOf(string key)
    {
        string dir = Path.Combine(RepoRoot(), "src", "LuaToolsGui", "Resources");
        foreach (string path in Directory.EnumerateFiles(dir, "Strings*.resx"))
        {
            string? value = XDocument.Load(path)
                .Root!.Elements("data")
                .FirstOrDefault(d => (string?)d.Attribute("name") == key)
                ?.Element("value")?.Value;

            if (value is not null) yield return (Path.GetFileName(path), value);
        }
    }

    // ── Product identity ──────────────────────────────────────────────────────

    [Fact]
    public void The_home_greeting_names_this_fork_in_every_language()
    {
        // "Amethyst" is the product name and is not translated, so it survives every localization —
        // which is what makes this checkable across all thirty files rather than only in English.
        var greetings = ValuesOf("Home_Welcome").ToList();

        greetings.Should().HaveCountGreaterThan(1);
        greetings.Should().OnlyContain(g => g.Value.Contains("Amethyst", StringComparison.Ordinal),
            "the README tells users to identify their build from what the app calls itself");
    }

    [Fact]
    public void The_window_title_is_the_display_name_resource()
    {
        // Not a literal: MainViewModel.WindowTitle is Strings.App_DisplayName, and the title bar and the
        // window both bind to it. A literal here is how the two start disagreeing.
        MainWindowXaml().Should().Contain("Title=\"{Binding WindowTitle}\"");
    }

    // ── Navigation rail ───────────────────────────────────────────────────────

    [Fact]
    public void No_navigation_item_carries_a_hardcoded_label()
    {
        // Every rail Content= must be a resource reference. Catches the next one added in a hurry.
        var literals = MainWindowXaml()
            .Split('\n')
            .Where(l => l.Contains("NavigationViewItem", StringComparison.Ordinal))
            .Where(l => l.Contains("Content=\"", StringComparison.Ordinal))
            .Where(l => !l.Contains("Content=\"{", StringComparison.Ordinal))
            .Select(l => l.Trim())
            .ToList();

        literals.Should().BeEmpty("a rail of translated labels with one English word in it looks broken");
    }

    [Fact]
    public void The_plugin_item_reads_its_label_from_resources() =>
        MainWindowXaml().Should().Contain("Content=\"{x:Static res:Strings.Nav_Plugin}\"");

    [Fact]
    public void The_new_nav_key_exists_and_has_an_accessor()
    {
        // Strings.Get falls back to returning the key NAME when the key is missing, so a binding to a
        // non-existent key renders "Nav_Plugin" in the rail instead of throwing.
        ValuesOf("Nav_Plugin").Should().NotBeEmpty();
        LuaToolsGui.Resources.Strings.Nav_Plugin.Should().Be("Plugin");
    }
}
