using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AwesomeAssertions;
using LuaToolsGui.Themes;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// A hot swap must land exactly where a cold start with the same palette lands — including for brushes a
/// view has already put in a <c>Style</c> setter. Sealing a style freezes its setter values, so a brush
/// the repaint had made freezable stuck on the first palette from the second switch on: the shell, the
/// page text and the hint lines stayed violet while the rest went green.
/// </summary>
[Collection(ThemeHostCollection.Name)]
public class NeutralRampTests(ThemeHost host)
{
    /// <summary>What startup paints for <paramref name="palette"/>: the same maps App.ApplyAccentPalette
    /// applies, read before any view exists.</summary>
    private Dictionary<string, Color> ColdStart(AccentPalette palette) => host.On(app =>
    {
        var resolve = new DictionaryColors(app.Resources);
        var all = new Dictionary<string, Color>();
        foreach (var map in new[] { palette.SurfaceColors(resolve), palette.ShellColors(resolve), palette.BrushColors(resolve) })
            foreach (var (key, color) in map) all[key] = color;
        return all;
    });

    private Color Resolved(string key) => host.On(app => app.TryFindResource(key) switch
    {
        SolidColorBrush b => b.Color,
        Color c => c,
        var other => throw new InvalidOperationException($"'{key}' resolved to {other}"),
    });

    private void SealStyleUsing(string key) => host.On(app =>
        new Style(typeof(TextBlock)) { Setters = { new Setter(TextBlock.ForegroundProperty, app.TryFindResource(key)) } }
            .Seal());

    public static TheoryData<string> StyledKeys() =>
    [
        "SurfaceBaseBrush", "SurfaceGhostBrush", "BorderSubtleBrush",
        "TextPrimaryBrush", "TextDimBrush", "TextFillColorPrimaryBrush", "AccentBrush",
    ];

    [Theory]
    [MemberData(nameof(StyledKeys))]
    public void A_brush_a_sealed_style_holds_follows_every_switch(string key)
    {
        host.Apply(AccentPalette.Amethyst);
        host.Apply(AccentPalette.Green);   // the first repaint is what used to make it freezable
        SealStyleUsing(key);               // ...and this is what then froze it

        host.IsFrozen(key).Should().BeFalse($"'{key}' was frozen by the style that uses it");
        Resolved(key).Should().Be(ColdStart(AccentPalette.Green)[key]);

        host.Apply(AccentPalette.Red);
        Resolved(key).Should().Be(ColdStart(AccentPalette.Red)[key], $"'{key}' missed the second switch");

        host.Apply(AccentPalette.Amethyst);
        Resolved(key).Should().Be(ColdStart(AccentPalette.Amethyst)[key], $"'{key}' did not come back");
    }

    [Fact]
    public void A_derived_wpf_ui_brush_a_sealed_style_holds_follows_every_switch()
    {
        host.Apply(AccentPalette.Amethyst);
        host.Apply(AccentPalette.Green);
        var green = Resolved("AccentButtonBackground");
        SealStyleUsing("AccentButtonBackground");

        host.Apply(AccentPalette.Red);
        Resolved("AccentButtonBackground").Should().NotBe(green, "Retint's repaint must not leave it freezable either");
        host.Apply(AccentPalette.Amethyst);
    }

    [Fact]
    public void Two_hot_swaps_in_a_row_match_a_cold_start_on_every_repainted_key()
    {
        host.Apply(AccentPalette.Amethyst);
        host.Apply(AccentPalette.Green);
        host.Apply(AccentPalette.Red);

        foreach (var (key, expected) in ColdStart(AccentPalette.Red))
            Resolved(key).Should().Be(expected, $"'{key}' after Amethyst → Green → Red");

        host.Apply(AccentPalette.Amethyst);
    }
}
