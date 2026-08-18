using System.Windows;
using System.Windows.Media;
using AwesomeAssertions;
using LuaToolsGui.Themes;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Covers the mechanism that makes an accent change take effect immediately.
///
/// <para>
/// The claim "no restart needed" rests on one specific fact: a view that bound a brush with
/// <c>{StaticResource}</c> resolved it ONCE and is holding the brush OBJECT, so the only way to repaint it
/// is to mutate that same instance. Replacing the dictionary entry with a new brush compiles, runs, throws
/// nothing, and changes nothing on screen until the app is relaunched — which is precisely the behaviour
/// being fixed here. Every test below therefore asserts on instance identity, not on the dictionary value.
/// </para>
/// </summary>
public class ThemeRepaintTests
{
    private static readonly Color Violet = Color.FromRgb(0xA7, 0x8B, 0xFA);
    private static readonly Color Emerald = Color.FromRgb(0x34, 0xD3, 0x99);

    private static ResourceDictionary WithBrush(string key, Color color) =>
        new() { [key] = new SolidColorBrush(color) };

    // ── Live repaint ──────────────────────────────────────────────────────────

    [Fact]
    public void A_brush_is_repainted_in_place_so_bound_views_follow_without_reloading()
    {
        var resources = WithBrush("SurfaceBaseBrush", Violet);

        // Exactly what a view holds once {StaticResource} has resolved: the instance, not the key.
        var whatTheViewHolds = (SolidColorBrush)resources["SurfaceBaseBrush"];

        int applied = ThemeRepaint.Apply(resources, new Dictionary<string, Color>
        {
            ["SurfaceBaseBrush"] = Emerald,
        });

        applied.Should().Be(1);
        whatTheViewHolds.Color.Should().Be(Emerald,
            "the view holds this instance and never looks the key up again");
        resources["SurfaceBaseBrush"].Should().BeSameAs(whatTheViewHolds,
            "swapping the entry for a new brush would repaint nothing");
    }

    [Fact]
    public void A_frozen_brush_is_left_alone_rather_than_throwing()
    {
        // Freezing a palette brush silently drops it out of the live switch. It must not take the whole
        // repaint down with it — a wrong-looking theme is survivable, a crash on the settings page is not.
        var frozen = new SolidColorBrush(Violet);
        frozen.Freeze();
        var resources = new ResourceDictionary
        {
            ["AccentBrush"] = frozen,
            ["SurfaceBaseBrush"] = new SolidColorBrush(Violet),
        };

        int applied = ThemeRepaint.Apply(resources, new Dictionary<string, Color>
        {
            ["AccentBrush"] = Emerald,
            ["SurfaceBaseBrush"] = Emerald,
        });

        applied.Should().Be(1, "only the mutable brush counts as applied");
        frozen.Color.Should().Be(Violet);
        ((SolidColorBrush)resources["SurfaceBaseBrush"]).Color.Should().Be(Emerald);
    }

    [Fact]
    public void A_key_the_dictionary_does_not_have_is_skipped_and_shows_in_the_count()
    {
        // The count is the diagnostic. A palette naming a token that no longer exists is a half-switched
        // theme, and it is otherwise completely silent.
        var resources = WithBrush("SurfaceBaseBrush", Violet);

        int applied = ThemeRepaint.Apply(resources, new Dictionary<string, Color>
        {
            ["SurfaceBaseBrush"] = Emerald,
            ["BrushThatWasRenamed"] = Emerald,
        });

        applied.Should().Be(1);
    }

    // ── Colour resources ──────────────────────────────────────────────────────

    [Fact]
    public void A_colour_resource_is_rewritten_at_the_top_level()
    {
        // Colors are structs, so anything that already resolved one copied the value and cannot be
        // repainted. Rewriting the entry is what makes popups and windows opened LATER agree with the
        // rest of the app.
        var resources = new ResourceDictionary { ["ApplicationBackgroundColor"] = Violet };

        ThemeRepaint.Apply(resources, new Dictionary<string, Color>
        {
            ["ApplicationBackgroundColor"] = Emerald,
        }).Should().Be(1);

        resources["ApplicationBackgroundColor"].Should().Be(Emerald);
    }

    // ── Merged-dictionary resolution ──────────────────────────────────────────

    [Fact]
    public void A_brush_living_in_a_merged_dictionary_is_found_and_repainted()
    {
        // The real arrangement: Application.Resources is nearly empty and the palette is merged into it.
        var palette = WithBrush("SurfaceBaseBrush", Violet);
        var app = new ResourceDictionary();
        app.MergedDictionaries.Add(palette);

        var held = (SolidColorBrush)palette["SurfaceBaseBrush"];

        ThemeRepaint.Apply(app, new Dictionary<string, Color> { ["SurfaceBaseBrush"] = Emerald })
            .Should().Be(1);

        held.Color.Should().Be(Emerald);
    }

    [Fact]
    public void The_last_merged_dictionary_wins_the_way_wpf_resolves_it()
    {
        // The WPF-UI theme is merged BEFORE the app palette and defines several of the same keys. A
        // forward scan finds the WPF-UI grey and repaints the wrong object, leaving the one the views
        // actually hold untouched: the app stays grey and nothing reports a problem.
        var wpfUi = WithBrush("CardBackground", Colors.Gray);
        var ours = WithBrush("CardBackground", Violet);
        var app = new ResourceDictionary();
        app.MergedDictionaries.Add(wpfUi);
        app.MergedDictionaries.Add(ours);

        ThemeRepaint.Apply(app, new Dictionary<string, Color> { ["CardBackground"] = Emerald });

        ((SolidColorBrush)ours["CardBackground"]).Color.Should().Be(Emerald,
            "ours is merged last, so it is what resolves");
        ((SolidColorBrush)wpfUi["CardBackground"]).Color.Should().Be(Colors.Gray,
            "the shadowed WPF-UI copy must not be touched");
    }

    // ── End to end: a real palette switch ─────────────────────────────────────

    [Fact]
    public void Switching_palettes_repaints_every_token_the_app_paints_with()
    {
        // The whole feature in one assertion: load the shipped dictionary, apply Green, and check the
        // instances the views hold all moved — surfaces and body text included, not just accents.
        var shipped = (ResourceDictionary)Application.LoadComponent(
            new Uri("/LuaTools;component/Themes/Colors.xaml", UriKind.Relative));
        var resolve = new DictionaryColors(shipped);

        var window = (SolidColorBrush)shipped["SurfaceBaseBrush"];
        var bodyText = (SolidColorBrush)shipped["TextPrimaryBrush"];
        var card = (SolidColorBrush)shipped["SurfaceCardBrush"];
        var accent = (SolidColorBrush)shipped["AccentBrush"];

        var before = (window.Color, bodyText.Color, card.Color, accent.Color);

        var green = AccentPalette.Green;
        int applied = ThemeRepaint.Apply(shipped, green.BrushColors(resolve))
                    + ThemeRepaint.Apply(shipped, green.SurfaceColors(resolve))
                    + ThemeRepaint.Apply(shipped, green.ShellColors(resolve));

        applied.Should().BeGreaterThan(50, "the switch covers the whole surface, not eight accent tokens");

        window.Color.Should().NotBe(before.Item1);
        bodyText.Color.Should().NotBe(before.Item2);
        card.Color.Should().NotBe(before.Item3);
        accent.Color.Should().NotBe(before.Item4);

        window.Color.Should().Be(resolve.Color(green.Neutrals.SurfaceBaseKey));
        bodyText.Color.Should().Be(resolve.Color(green.Neutrals.TextPrimaryKey));

        // And back again with no reload in between — the switch has to be reversible, not one-way.
        var amethyst = AccentPalette.Amethyst;
        ThemeRepaint.Apply(shipped, amethyst.BrushColors(resolve));
        ThemeRepaint.Apply(shipped, amethyst.SurfaceColors(resolve));
        ThemeRepaint.Apply(shipped, amethyst.ShellColors(resolve));

        (window.Color, bodyText.Color, card.Color, accent.Color).Should().Be(before);
    }
}
