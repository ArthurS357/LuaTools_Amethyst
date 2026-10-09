using System.Windows;
using System.Windows.Media;
using AwesomeAssertions;
using LuaToolsGui.Themes;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// WPF-UI's accent-DERIVED brushes — the Primary button, the toggle, the input focus border, links. They
/// are bound to the accent colours, but a switch does not reach one that has already been instantiated, so
/// the central content stayed on the old accent while the nav rail (whose brushes are repainted by key)
/// changed. <see cref="ThemeLiveSwitchTests"/> never saw it: it only covers the keys the palette names.
/// </summary>
[Collection(ThemeHostCollection.Name)]
public class WpfUiDerivedAccentTests(ThemeHost host)
{
    public static TheoryData<string> DerivedKeys() =>
    [
        "AccentButtonBackground", "ToggleSwitchFillOn", "TextControlFocusedBorderBrush",
        "HyperlinkButtonForeground", "ProgressBarForeground", "ComboBoxBorderBrushFocused",
    ];

    [Theory]
    [MemberData(nameof(DerivedKeys))]
    public void A_derived_brush_follows_the_switch_once_it_has_been_used(string key)
    {
        host.Apply(AccentPalette.Amethyst);
        var amethyst = host.BrushColor(key); // resolving it instantiates it, which is what a template does

        host.Apply(AccentPalette.Green);
        var green = host.BrushColor(key);

        green.Should().NotBe(amethyst, $"'{key}' stayed on the previous accent");
        host.On(app => new[]
            {
                AccentPalette.Green.SoftKey, AccentPalette.Green.PrimaryKey,
                AccentPalette.Green.BorderKey, AccentPalette.Green.FillKey,
            }
            .Select(k => (Color)app.TryFindResource(k))
            .Any(c => c.R == green.R && c.G == green.G && c.B == green.B))
            .Should().BeTrue($"'{key}' left the green ramp");

        host.Apply(AccentPalette.Red);
        host.BrushColor(key).Should().NotBe(green, $"'{key}' froze on the second switch");

        host.Apply(AccentPalette.Amethyst);
        host.BrushColor(key).Should().Be(amethyst, $"'{key}' did not come back");
    }
}
