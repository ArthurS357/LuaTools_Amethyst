using System.IO;
using System.Windows;
using System.Windows.Media;
using AwesomeAssertions;
using LuaToolsGui.Services;
using LuaToolsGui.Themes;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Covers the selectable accent ramps: their contrast, their derivation, and the fact that picking one
/// survives a restart.
///
/// <para>
/// The contrast checks read the REAL Themes/Colors.xaml rather than a copy of the hex values, because a
/// copy would keep passing after someone edited the shipped palette. That matters more here than usual:
/// the theme is the one part of this app where a clean build proves nothing — an accent override that
/// stops matching WPF-UI's resource keys throws nothing and simply has no visual effect.
/// </para>
///
/// <para>
/// The obvious green failed this bar. Tailwind green-600 #16A34A reaches only 3.3:1 against white, so a
/// filled button in it would fail AA outright; the ramp uses a deeper emerald instead. These tests are
/// what stop that being "simplified" back.
/// </para>
/// </summary>
public class AccentPaletteTests
{
    private const string SurfaceBase = "#0E0B14";
    private const string White = "#FFFFFF";

    private static readonly ResourceDictionary Palette = LoadShippedPalette();

    /// <summary>The dictionary the app actually ships, loaded from the built assembly.</summary>
    private static ResourceDictionary LoadShippedPalette() =>
        (ResourceDictionary)Application.LoadComponent(
            new Uri("/LuaTools;component/Themes/Colors.xaml", UriKind.Relative));

    private static Color Get(string key) => (Color)Palette[key];

    /// <summary>WCAG 2.1 relative luminance.</summary>
    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;

    public static TheoryData<string> EveryPalette() =>
        new(AccentPalette.All.Select(p => p.Id));

    // ── Contrast ─────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(EveryPalette))]
    public void Text_weights_clear_AA_against_the_base_surface(string id)
    {
        var p = AccentPalette.FromId(id);

        // 300 and 400 carry text and icons, so they need the 4.5:1 text floor.
        Contrast(Get(p.SoftKey), Hex(SurfaceBase)).Should().BeGreaterThanOrEqualTo(4.5);
        Contrast(Get(p.PrimaryKey), Hex(SurfaceBase)).Should().BeGreaterThanOrEqualTo(4.5);
    }

    [Theory]
    [MemberData(nameof(EveryPalette))]
    public void The_border_weight_clears_the_non_text_floor(string id)
    {
        // WCAG 1.4.11: non-text UI (borders, focus rings) needs 3:1, not 4.5:1.
        var p = AccentPalette.FromId(id);
        Contrast(Get(p.BorderKey), Hex(SurfaceBase)).Should().BeGreaterThanOrEqualTo(3.0);
    }

    [Theory]
    [MemberData(nameof(EveryPalette))]
    public void The_filled_weight_clears_AA_against_white_text(string id)
    {
        // Filled accent buttons pair this weight with white text — the check the naive green failed.
        var p = AccentPalette.FromId(id);
        Contrast(Get(p.FillKey), Hex(White)).Should().BeGreaterThanOrEqualTo(4.5);
    }

    [Theory]
    [MemberData(nameof(EveryPalette))]
    public void No_accent_is_mistakable_for_a_status_colour(string id)
    {
        // An accent identical to SuccessText or Danger would make "highlighted" and "something happened"
        // the same colour. This is why the ramps are emerald and rose rather than the plain green/red.
        var p = AccentPalette.FromId(id);
        var accent = Get(p.PrimaryKey);

        accent.Should().NotBe(Get("Success400Color"));
        accent.Should().NotBe(Get("Danger400Color"));
    }

    // ── Derivation ───────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(EveryPalette))]
    public void Every_accent_brush_the_app_paints_with_gets_a_colour(string id)
    {
        var colors = AccentPalette.FromId(id).BrushColors(new DictionaryColors(Palette));

        // Missing one would leave that brush stuck on the previous palette — a half-switched theme.
        colors.Keys.Should().BeEquivalentTo(
        [
            "AccentBrush", "AccentSoftBrush", "AccentTintBrush", "AccentTintHoverBrush",
            "AccentTintStrongBrush", "AccentTintActiveBrush", "AccentOutlineBrush", "AccentBadgeBrush",
        ]);
    }

    [Theory]
    [MemberData(nameof(EveryPalette))]
    public void The_washes_are_the_primary_weight_at_the_original_alphas(string id)
    {
        var p = AccentPalette.FromId(id);
        var colors = p.BrushColors(new DictionaryColors(Palette));
        var primary = Get(p.PrimaryKey);

        // A re-skin must change hue only — the transparency levels are a separate design decision that
        // was tuned once against the violet palette and should not move with the colour.
        colors["AccentTintBrush"].Should().Be(Color.FromArgb(0x14, primary.R, primary.G, primary.B));
        colors["AccentOutlineBrush"].Should().Be(Color.FromArgb(0x40, primary.R, primary.G, primary.B));
        colors["AccentBadgeBrush"].A.Should().Be(0x20);
    }

    [Fact]
    public void The_default_palette_reproduces_the_violet_values_the_app_shipped_with()
    {
        // Amethyst is the identity colour; the picker must not have quietly restyled the default.
        var colors = AccentPalette.Amethyst.BrushColors(new DictionaryColors(Palette));

        colors["AccentBrush"].Should().Be(Hex("#A78BFA"));
        colors["AccentSoftBrush"].Should().Be(Hex("#C4B5FD"));
        colors["AccentTintBrush"].Should().Be(Hex("#14A78BFA"));
        colors["AccentTintHoverBrush"].Should().Be(Hex("#1AA78BFA"));
        colors["AccentTintStrongBrush"].Should().Be(Hex("#26A78BFA"));
        colors["AccentTintActiveBrush"].Should().Be(Hex("#33A78BFA"));
        colors["AccentOutlineBrush"].Should().Be(Hex("#40A78BFA"));
        colors["AccentBadgeBrush"].Should().Be(Hex("#207C3AED"));
    }

    [Theory]
    [MemberData(nameof(EveryPalette))]
    public void Every_ramp_primitive_exists_in_the_shipped_dictionary(string id)
    {
        // The key names are effectively API: renaming one in the XAML drops the accent silently.
        var p = AccentPalette.FromId(id);
        foreach (string key in new[] { p.SoftKey, p.PrimaryKey, p.BorderKey, p.FillKey })
            Palette.Contains(key).Should().BeTrue($"'{key}' is read by name at startup");
    }

    // ── Selection ────────────────────────────────────────────────────

    [Fact]
    public void An_unknown_or_missing_id_falls_back_to_the_default_rather_than_leaving_it_unthemed()
    {
        AccentPalette.FromId(null).Should().Be(AccentPalette.Amethyst);
        AccentPalette.FromId("").Should().Be(AccentPalette.Amethyst);
        AccentPalette.FromId("Chartreuse").Should().Be(AccentPalette.Amethyst); // written by a newer build
    }

    [Fact]
    public void Ids_are_matched_case_insensitively()
    {
        AccentPalette.FromId("green").Should().Be(AccentPalette.Green);
        AccentPalette.FromId("RED").Should().Be(AccentPalette.Red);
    }

    // ── Persistence ──────────────────────────────────────────────────

    [Fact]
    public void A_chosen_accent_survives_a_restart()
    {
        string dir = Path.Combine(Path.GetTempPath(), "luatools-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            new SettingsService(dir).AccentColor = AccentPalette.Green.Id;

            new SettingsService(dir).AccentColor.Should().Be(AccentPalette.Green.Id);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void An_accent_choice_alone_is_enough_to_keep_the_settings_file()
    {
        // SettingsService deletes settings.json when every persisted field is null. A field left out of
        // that check is silently lost on the next save — which has already happened once, to FixesPageSize.
        string dir = Path.Combine(Path.GetTempPath(), "luatools-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            new SettingsService(dir).AccentColor = AccentPalette.Red.Id;

            File.Exists(Path.Combine(dir, "settings.json")).Should().BeTrue();

            var reloaded = new SettingsService(dir);
            reloaded.MinimizeToTray = false; // any other save must not drop the accent
            reloaded.AccentColor.Should().Be(AccentPalette.Red.Id);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
