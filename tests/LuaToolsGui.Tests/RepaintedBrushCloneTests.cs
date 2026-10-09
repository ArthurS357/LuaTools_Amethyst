using System.Windows.Media;
using AwesomeAssertions;
using LuaToolsGui.Themes;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// A template trigger (hover, selection) CLONES an unfrozen brush it uses — that is how the crash on
/// hovering in Depots surfaced: Freezable.CloneCore threw InvalidCastException (Color → Expression) on a
/// brush whose colour had been set with SetCurrentValue over its {DynamicResource}. A repainted brush must
/// clone cleanly, carry the current colour, and still never be freezable.
/// </summary>
[Collection(ThemeHostCollection.Name)]
public class RepaintedBrushCloneTests(ThemeHost host)
{
    public static TheoryData<string> Keys() =>
        ["SurfaceBaseBrush", "TextDimBrush", "AccentBrush", "TextFillColorPrimaryBrush", "AccentButtonBackground"];

    [Theory]
    [MemberData(nameof(Keys))]
    public void A_repainted_brush_clones_like_a_template_trigger_clones_it(string key)
    {
        host.Apply(AccentPalette.Amethyst);
        host.Apply(AccentPalette.Green);

        var (current, cloned) = host.On(app =>
        {
            var brush = (SolidColorBrush)app.TryFindResource(key);
            brush.Opacity.Should().Be(1.0, "the keep-mutable reference must not dim the brush");
            return (brush.Color, brush.Clone().Color);
        });

        cloned.Should().Be(current);
        host.IsFrozen(key).Should().BeFalse();
        host.Apply(AccentPalette.Amethyst);
    }
}
