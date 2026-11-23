using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using AwesomeAssertions;
using Wpf.Ui.Controls;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// The design-system foundation (Themes/Typography.xaml, Spacing.xaml, Controls.xaml, 1.8.3) and the ratchet
/// that keeps the page migration (Lotes C-H) moving in one direction.
///
/// <para>
/// The static half reads the sources, like <see cref="ViewParseTests"/>: a missing token or a bad icon name
/// compiles and only throws when first used, and nothing consumes these styles yet, so no page load would
/// catch it. The live half builds every style inside the shared <see cref="ThemeHost"/> Application, which is
/// where a BasedOn that does not resolve or a broken template actually fails.
/// </para>
/// </summary>
[Collection(ThemeHostCollection.Name)]
public class DesignFoundationTests(ThemeHost host)
{
    private static readonly string[] Foundation = ["Typography.xaml", "Spacing.xaml", "Controls.xaml"];

    /// <summary>The foundation files plus App.xaml, whose top level holds the components BasedOn WPF-UI styles.</summary>
    private static readonly string[] Sources = [.. Foundation, "App.xaml"];

    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static readonly Regex ResourceRef =
        new(@"\{(?:Static|Dynamic)Resource\s+(?<key>[A-Za-z0-9_]+)\s*\}", RegexOptions.Compiled);

    private static readonly Regex SymbolRef = new(@"Symbol=""(?<name>[A-Za-z0-9_]+)""", RegexOptions.Compiled);

    private static readonly Regex HexColour = new(@"#[0-9A-Fa-f]{3,8}\b", RegexOptions.Compiled);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LuaToolsGui.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("LuaToolsGui.sln not found above the test output.");
    }

    private static string AppDir() => Path.Combine(RepoRoot(), "src", "LuaToolsGui");

    private static string ThemeSource(string file) =>
        File.ReadAllText(file == "App.xaml" ? Path.Combine(AppDir(), file) : Path.Combine(AppDir(), "Themes", file));

    /// <summary>The element whose children are the file's resources: the root, or App.xaml's resource dictionary.</summary>
    private static XElement ResourceRoot(string file)
    {
        var root = XDocument.Parse(ThemeSource(file)).Root!;
        return file == "App.xaml" ? root.Element(Wpf + "Application.Resources")!.Element(Wpf + "ResourceDictionary")! : root;
    }

    /// <summary>Keys a dictionary declares at its top level (what other files can resolve).</summary>
    private static IEnumerable<string> DeclaredKeys(string file) =>
        ResourceRoot(file).Elements()
            .Select(e => (string?)e.Attribute(X + "Key"))
            .Where(k => k is not null)!;

    public static TheoryData<string> FoundationFiles() => new(Foundation);

    public static TheoryData<string> SourceFiles() => new(Sources);

    // ── Static checks ───────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(SourceFiles))]
    public void Every_style_is_keyed_so_merging_the_file_restyles_nothing(string file)
    {
        // An implicit (unkeyed) top-level style would apply to every page at once and break the rollout rule:
        // zero visual change until a page opts in. Styles scoped inside a template's own resources are fine.
        var unkeyed = ResourceRoot(file).Elements(Wpf + "Style")
            .Where(s => s.Attribute(X + "Key") is null)
            .Select(s => (string?)s.Attribute("TargetType"))
            .ToList();

        unkeyed.Should().BeEmpty($"{file} must not declare implicit styles");
    }

    [Theory]
    [MemberData(nameof(SourceFiles))]
    public void No_colour_literal_lives_outside_the_palette(string file)
    {
        HexColour.Matches(ThemeSource(file)).Select(m => m.Value).Should().BeEmpty(
            $"{file} must take every colour from Themes/Colors.xaml so the accent switch and the contrast table cover it");
    }

    [Fact]
    public void No_foundation_key_shadows_a_palette_or_sibling_key()
    {
        // Merged after Colors.xaml, a duplicate key here would silently WIN over the palette (WPF searches
        // merged dictionaries in reverse) and drop that token out of the accent switch.
        var all = new[] { "Colors.xaml" }.Concat(Sources).SelectMany(DeclaredKeys).ToList();
        all.GroupBy(k => k, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key)
            .Should().BeEmpty("each key must be declared exactly once across the app's dictionaries");
    }

    [Fact]
    public void No_new_key_redefines_a_WPF_UI_key()
    {
        // Colors.xaml overrides WPF-UI keys ON PURPOSE; nothing else may, or it silently takes over a control.
        var mine = Sources.SelectMany(DeclaredKeys).ToList();
        host.On(app =>
        {
            var wpfUi = app.Resources.MergedDictionaries
                .Where(d => d is global::Wpf.Ui.Markup.ThemesDictionary or global::Wpf.Ui.Markup.ControlsDictionary).ToList();
            mine.Where(k => wpfUi.Any(d => d.Contains(k))).Should().BeEmpty("these keys already belong to WPF-UI");
        });
    }

    [Theory]
    [MemberData(nameof(SourceFiles))]
    public void Every_resource_a_foundation_file_names_exists(string file)
    {
        // DynamicResource never throws on a missing key, it just paints nothing — the silent version of the
        // failure ViewParseTests guards for StaticResource in the views.
        var known = new[] { "Colors.xaml" }.Concat(Sources).SelectMany(DeclaredKeys)
            .ToHashSet(StringComparer.Ordinal);

        // A WPF-UI key (DefaultControlFocusVisualStyle) is legitimate too, as long as WPF-UI really defines it.
        var unknown = ResourceRef.Matches(ThemeSource(file)).Select(m => m.Groups["key"].Value)
            .Where(k => !known.Contains(k)).Distinct().ToList();
        host.On(app => unknown.Where(k => !app.Resources.MergedDictionaries.Any(d => d.Contains(k))).ToList())
            .Should().BeEmpty($"{file} references keys neither the app nor WPF-UI declares");
    }

    [Theory]
    [MemberData(nameof(SourceFiles))]
    public void Every_icon_a_foundation_file_names_is_a_real_symbol(string file)
    {
        SymbolRef.Matches(ThemeSource(file)).Select(m => m.Groups["name"].Value)
            .Where(n => !Enum.IsDefined(typeof(SymbolRegular), n))
            .Should().BeEmpty($"{file} names icons Wpf.Ui's SymbolRegular does not define");
    }

    [Fact]
    public void The_type_ramp_has_seven_roles_and_nothing_below_11()
    {
        string[] roles = ["TextCaption", "TextBody", "TextBodyStrong", "TextSubtitle", "TextTitle", "TextHeader", "TextDisplay"];
        DeclaredKeys("Typography.xaml").Where(k => k.StartsWith("Text", StringComparison.Ordinal))
            .Should().BeEquivalentTo(roles);

        var sizes = host.On(app => DeclaredKeys("Typography.xaml")
            .Where(k => k.StartsWith("FontSize", StringComparison.Ordinal))
            .ToDictionary(k => k, k => (double)app.FindResource(k)));
        sizes.Values.Should().OnlyContain(s => s >= 11, "11 is the redesign's minimum legible size");
        sizes.Should().Contain("FontSizeCaption", 11).And.Contain("FontSizeBody", 13)
            .And.Contain("FontSizeSubtitle", 16).And.Contain("FontSizeTitle", 20)
            .And.Contain("FontSizeHeader", 28).And.Contain("FontSizeDisplay", 42);
    }

    [Fact]
    public void Spacing_sits_on_the_4_unit_grid_and_radii_are_three()
    {
        var (thicknesses, radii) = host.On(app =>
        {
            var keys = DeclaredKeys("Spacing.xaml").ToList();
            return (keys.Select(k => app.FindResource(k)).OfType<Thickness>().ToList(),
                    keys.Select(k => app.FindResource(k)).OfType<CornerRadius>().ToList());
        });

        thicknesses.SelectMany(t => new[] { t.Left, t.Top, t.Right, t.Bottom })
            .Should().OnlyContain(v => v % 4 == 0, "every spacing token is on the 4-unit grid");
        radii.Select(r => r.TopLeft).Should().Equal(4, 8, 12);
    }

    // ── Live checks (real Application, App.xaml's merge order) ─────

    public static TheoryData<string> FoundationStyles() =>
        new(Sources.SelectMany(f => ResourceRoot(f).Elements(Wpf + "Style")
            .Select(s => (string)s.Attribute(X + "Key")!)));

    [Theory]
    [MemberData(nameof(FoundationStyles))]
    public void Every_style_builds_and_templates_against_the_live_application(string key)
    {
        host.On(app =>
        {
            // FindResource builds the deferred Style here, which is where a BasedOn on a WPF-UI or sibling key
            // that does not resolve throws. ApplyTemplate then instantiates the template's parts.
            var style = (Style)app.FindResource(key);
            var element = (FrameworkElement)Activator.CreateInstance(style.TargetType)!;
            element.Style = style;
            if (element is Control control) control.ApplyTemplate().Should().BeTrue($"'{key}' should template");
            element.Measure(new Size(400, 400));
        });
    }

    [Theory]
    [InlineData("IconButtonStyle")]
    [InlineData("DangerButtonStyle")]
    [InlineData("ListItemStyle")]
    public void Every_focusable_component_shows_a_keyboard_focus_ring(string key)
    {
        host.On(app =>
        {
            var style = (Style)app.FindResource(key);
            var element = (Control)Activator.CreateInstance(style.TargetType)!;
            element.Style = style;
            // Not just "not null": the DP's default is an empty sealed Style that draws nothing.
            element.FocusVisualStyle.Should().BeSameAs(app.FindResource("DefaultControlFocusVisualStyle"),
                $"'{key}' must keep WPF-UI's keyboard focus ring");
        });
    }

    [Theory]
    [InlineData("IconButtonStyle", typeof(global::Wpf.Ui.Controls.Button))]
    [InlineData("DangerButtonStyle", typeof(global::Wpf.Ui.Controls.Button))]
    [InlineData("ListItemStyle", typeof(ListBoxItem))]
    public void WPF_UI_derived_components_are_based_on_WPF_UIs_own_style(string key, Type control)
    {
        // The failure this pins: declared in a Source-merged dictionary, BasedOn {x:Type ListBoxItem} resolved
        // to the CLASSIC Windows style without any error, and {x:Type ui:Button} threw on first use.
        host.On(app =>
        {
            // Both sides could resolve to the system theme style if WPF-UI stopped shipping one: pin the source.
            app.Resources.MergedDictionaries.OfType<global::Wpf.Ui.Markup.ControlsDictionary>().Single().Contains(control)
                .Should().BeTrue($"WPF-UI must still define the {control.Name} style '{key}' builds on");
            ((Style)app.FindResource(key)).BasedOn.Should().BeSameAs(app.FindResource(control),
                $"'{key}' must inherit WPF-UI's template, states and focus ring");
        });
    }

    [Fact]
    public void An_icon_button_takes_its_accessible_name_from_its_ToolTip()
    {
        host.On(app =>
        {
            var button = new global::Wpf.Ui.Controls.Button { Style = (Style)app.FindResource("IconButtonStyle"), ToolTip = "Next page" };
            System.Windows.Automation.AutomationProperties.GetName(button).Should().Be("Next page");
        });
    }

    [Fact]
    public void The_state_blocks_and_page_header_never_take_focus()
    {
        host.On(app =>
        {
            foreach (string key in new[] { "PageHeaderStyle", "EmptyStateStyle", "ErrorStateStyle", "LoadingStateStyle" })
            {
                var style = (Style)app.FindResource(key);
                var element = (Control)Activator.CreateInstance(style.TargetType)!;
                element.Style = style;
                element.Focusable.Should().BeFalse(key);
                element.IsTabStop.Should().BeFalse(key);
            }
        });
    }

    [Fact]
    public void The_pager_template_loads()
    {
        host.On(app =>
        {
            var template = (DataTemplate)app.FindResource("PagerTemplate");
            template.LoadContent().Should().BeOfType<StackPanel>();
        });
    }

    // ── Ratchet ─────────────────────────────────────────────────────

    /// <summary>
    /// Literal values still hardcoded in the pages, measured on 2026-11-23 (Lote B, before any page moved).
    /// LOWER these as each lote migrates a page onto Typography.xaml / Spacing.xaml. They never go up.
    /// </summary>
    private const int FontSizeCeiling = 395;
    private const int MarginPaddingCeiling = 701;
    private const int CornerRadiusCeiling = 100;

    private static readonly (string Name, Regex Pattern, int Ceiling)[] Literals =
    [
        ("FontSize", new Regex(@"\bFontSize=""[0-9.]+""", RegexOptions.Compiled), FontSizeCeiling),
        ("Margin/Padding", new Regex(@"\b(?:Margin|Padding)=""[0-9., -]+""", RegexOptions.Compiled), MarginPaddingCeiling),
        ("CornerRadius", new Regex(@"\bCornerRadius=""[0-9., ]+""", RegexOptions.Compiled), CornerRadiusCeiling),
    ];

    public static TheoryData<string> LiteralKinds() => new(Literals.Select(l => l.Name));

    [Theory]
    [MemberData(nameof(LiteralKinds))]
    public void Hardcoded_page_literals_only_ever_go_down(string kind)
    {
        var (_, pattern, ceiling) = Literals.Single(l => l.Name == kind);
        var pages = Directory.GetFiles(Path.Combine(AppDir(), "Views"), "*.xaml")
            .Append(Path.Combine(AppDir(), "MainWindow.xaml"));
        int count = pages.Sum(p => pattern.Matches(File.ReadAllText(p)).Count);

        count.Should().BeLessThanOrEqualTo(ceiling,
            $"a page added {count - ceiling} literal {kind} value(s); use the Typography.xaml / Spacing.xaml tokens instead");
        count.Should().Be(ceiling,
            $"{kind} literals dropped to {count}: lower the ceiling in DesignFoundationTests to {count} so the gain cannot be lost");
    }
}
