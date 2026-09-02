using System.Linq;
using System.Windows.Media;
using AwesomeAssertions;
using LuaToolsGui.Themes;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Covers the half of the accent switch that WPF-UI owns, and that shipped broken through 1.7.1.
///
/// <para>
/// <c>ApplicationAccentColorManager.Apply</c> does not repaint its accent brushes. It builds nine fresh
/// <see cref="SolidColorBrush"/> objects and assigns them at the top level of
/// <c>Application.Resources</c>, where WPF freezes them on arrival. The COLOURS were therefore always
/// correct if you asked the dictionary — which is exactly why this went unnoticed — while every control
/// that had resolved one through <c>{StaticResource}</c> kept holding the object from the PREVIOUS
/// palette and kept painting it.
/// </para>
///
/// <para>
/// The visible result was an app whose shell followed the accent (those brushes are mutated in place)
/// while the middle of the window did not: with Red selected the window and nav rail went wine, and the
/// primary buttons, toggles and accent text inside the content area stayed violet.
/// </para>
///
/// <para>
/// So asserting the colour is not enough — a colour assertion passed throughout the bug. Identity is the
/// property that actually carries the repaint, and it is what these assert.
/// </para>
/// </summary>
[Collection(ThemeHostCollection.Name)]
public class AccentBrushIdentityTests(ThemeHost host)
{
    /// <summary>Every accent brush WPF-UI 4.3.0's accent manager rewrites. Kept in step with
    /// <c>App.WpfUiAccentBrushKeys</c>, which is the list the drop actually iterates.</summary>
    private static readonly string[] Keys =
    [
        "SystemAccentBrush",
        "SystemFillColorAttentionBrush",
        "AccentTextFillColorPrimaryBrush",
        "AccentTextFillColorSecondaryBrush",
        "AccentTextFillColorTertiaryBrush",
        "AccentFillColorSelectedTextBackgroundBrush",
        "AccentFillColorDefaultBrush",
        "AccentFillColorSecondaryBrush",
        "AccentFillColorTertiaryBrush",
    ];

    public static TheoryData<string> AccentBrushes() => [.. Keys];

    [Theory]
    [MemberData(nameof(AccentBrushes))]
    public void The_brush_a_view_is_holding_survives_a_palette_switch(string key)
    {
        // THE regression. A view resolves {StaticResource} once and holds the object forever, so a switch
        // that hands back a different object repaints nothing that was already on screen.
        host.Apply(AccentPalette.Amethyst);
        object? before = host.On(app => app.TryFindResource(key));

        host.Apply(AccentPalette.Red);
        object? after = host.On(app => app.TryFindResource(key));

        before.Should().NotBeNull($"'{key}' must resolve, or nothing paints with it");
        after.Should().BeSameAs(before,
            $"'{key}' must stay the SAME brush object across a switch, or every control that already " +
            "resolved it keeps painting the old accent");
    }

    [Theory]
    [MemberData(nameof(AccentBrushes))]
    public void The_brush_stays_mutable_once_the_application_owns_it(string key)
    {
        host.Apply(AccentPalette.Amethyst);

        // A frozen brush cannot be repainted, and freezing is what assigning one into Application.Resources
        // does. Colors.xaml keeps these unfrozen by taking the Colour through {DynamicResource}; anyone
        // "tidying" that to a literal turns the switch back off without failing the build.
        host.IsFrozen(key).Should().BeFalse($"'{key}' must stay mutable to follow the accent");
    }

    [Theory]
    [MemberData(nameof(AccentBrushes))]
    public void That_same_brush_actually_changes_colour(string key)
    {
        // Identity alone would also be satisfied by a brush that never moves. Both halves are required.
        host.Apply(AccentPalette.Amethyst);
        Color violet = host.BrushColor(key);

        host.Apply(AccentPalette.Red);
        Color rose = host.BrushColor(key);

        rose.Should().NotBe(violet, $"'{key}' must follow the palette, not just keep its identity");
    }

    [Fact]
    public void No_accent_brush_is_left_shadowing_the_palette_at_the_top_level()
    {
        // The manager rewrites these on EVERY apply, so dropping them once at startup is not enough —
        // App.DropAccentBrushOverrides has to run after each one. This is what catches it being moved
        // out of that path, which would otherwise show up only as the second switch failing.
        host.Apply(AccentPalette.Red);

        foreach (var key in Keys)
        {
            // Keys, not Contains: ResourceDictionary.Contains walks merged dictionaries, so it is true
            // for the Colors.xaml copy that SHOULD be answering. Only the top-level entry matters here.
            host.On(app => app.Resources.Keys.Cast<object>().Contains(key)).Should().BeFalse(
                $"'{key}' must resolve from Themes/Colors.xaml, not from a frozen top-level copy");
        }
    }
}
