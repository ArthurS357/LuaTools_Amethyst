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
        var window = Get(p.Neutrals.SurfaceBaseKey);

        // Against the palette own window colour, not a fixed violet one. Each palette now brings its own
        // neutral ramp, so measuring green accents on the plum background would be checking a combination
        // that never appears on screen.
        Contrast(Get(p.SoftKey), window).Should().BeGreaterThanOrEqualTo(4.5);
        Contrast(Get(p.PrimaryKey), window).Should().BeGreaterThanOrEqualTo(4.5);
    }

    [Theory]
    [MemberData(nameof(EveryPalette))]
    public void The_border_weight_clears_the_non_text_floor(string id)
    {
        // WCAG 1.4.11: non-text UI (borders, focus rings) needs 3:1, not 4.5:1.
        var p = AccentPalette.FromId(id);
        Contrast(Get(p.BorderKey), Get(p.Neutrals.SurfaceBaseKey)).Should().BeGreaterThanOrEqualTo(3.0);
    }

    // -- Tonal neutrals ----------------------------------------------

    [Theory]
    [MemberData(nameof(EveryPalette))]
    public void Every_neutral_step_exists_in_the_shipped_dictionary(string id)
    {
        // Read by name when the accent is applied, same as the accent primitives: a rename here leaves
        // that step on the previous palette colour and nothing throws.
        var n = AccentPalette.FromId(id).Neutrals;

        foreach (string key in new[]
                 { n.DeepKey, n.SurfaceBaseKey, n.SurfaceRaisedKey, n.SurfaceOverlayKey, n.SurfaceInsetKey,
                   n.TextDisabledKey, n.TextDimKey, n.TextMutedKey, n.TextSecondaryKey, n.TextPrimaryKey,
                   n.ElevationTintKey })
            Palette.Contains(key).Should().BeTrue($"'{key}' is read by name when the accent is applied");
    }

    [Theory]
    [MemberData(nameof(EveryPalette))]
    public void Every_neutral_step_carries_the_same_luminance_as_the_amethyst_step_it_replaces(string id)
    {
        // THE load-bearing property of the tonal system. Contrast is computed from relative luminance, so
        // holding luminance constant across palettes means every ratio signed off against Amethyst
        // transfers to green and red unchanged - the accessibility work is done once, not three times.
        // Derive the ramps any other way (equal HSL lightness is the obvious trap) and this fails: green
        // at matched lightness is markedly brighter, which pushed Danger-on-inset down to 3.92:1.
        var a = AccentPalette.Amethyst.Neutrals;
        var n = AccentPalette.FromId(id).Neutrals;

        var pairs = new[]
        {
            (a.DeepKey, n.DeepKey), (a.SurfaceBaseKey, n.SurfaceBaseKey),
            (a.SurfaceRaisedKey, n.SurfaceRaisedKey), (a.SurfaceOverlayKey, n.SurfaceOverlayKey),
            (a.SurfaceInsetKey, n.SurfaceInsetKey), (a.TextDisabledKey, n.TextDisabledKey),
            (a.TextDimKey, n.TextDimKey), (a.TextMutedKey, n.TextMutedKey),
            (a.TextSecondaryKey, n.TextSecondaryKey), (a.TextPrimaryKey, n.TextPrimaryKey),
            (a.ElevationTintKey, n.ElevationTintKey),
        };

        foreach (var (reference, step) in pairs)
            Luminance(Get(step)).Should().BeApproximately(Luminance(Get(reference)), 0.005,
                $"'{step}' stands in for '{reference}' and must be equally bright");
    }

    [Theory]
    [MemberData(nameof(EveryPalette))]
    public void Body_text_clears_its_floors_on_the_palettes_own_surfaces(string id)
    {
        var n = AccentPalette.FromId(id).Neutrals;
        var window = Get(n.SurfaceBaseKey);
        var inset = Get(n.SurfaceInsetKey);   // lightest surface text is drawn on - the worst case

        // Primary body text clears AAA; the rest clear AA on both the window and an inset chip.
        Contrast(Get(n.TextPrimaryKey), window).Should().BeGreaterThanOrEqualTo(7.0);
        foreach (string key in new[] { n.TextSecondaryKey, n.TextMutedKey, n.TextDimKey })
        {
            Contrast(Get(key), window).Should().BeGreaterThanOrEqualTo(4.5);
            Contrast(Get(key), inset).Should().BeGreaterThanOrEqualTo(4.5);
        }

        // Disabled text is exempt from WCAG, but it still has to read as text.
        Contrast(Get(n.TextDisabledKey), window).Should().BeGreaterThanOrEqualTo(3.0);
    }

    [Theory]
    [MemberData(nameof(EveryPalette))]
    public void Status_colours_stay_legible_on_every_palettes_surfaces(string id)
    {
        // Status hues deliberately do NOT follow the accent, so re-tinting the surfaces underneath them is
        // exactly how a "this failed" colour could quietly stop being readable.
        var n = AccentPalette.FromId(id).Neutrals;

        foreach (string status in new[]
                 { "Success500Color", "Success400Color", "Warning400Color", "Warning500Color",
                   "Warning300Color", "Orange500Color", "Danger400Color", "Info300Color" })
        {
            Contrast(Get(status), Get(n.SurfaceBaseKey)).Should().BeGreaterThanOrEqualTo(4.5,
                $"'{status}' is drawn on the window");
            Contrast(Get(status), Get(n.SurfaceInsetKey)).Should().BeGreaterThanOrEqualTo(4.5,
                $"'{status}' is drawn on inset chips");
        }
    }

    [Theory]
    [MemberData(nameof(EveryPalette))]
    public void The_whole_visible_surface_is_covered_not_just_the_buttons(string id)
    {
        // The complaint this answers: picking a colour repainted the accent tokens and left every surface
        // and every line of text alone. Naming the tokens explicitly is what stops a future refactor
        // quietly narrowing the switch back to buttons.
        var p = AccentPalette.FromId(id);
        var surfaces = p.SurfaceColors(new DictionaryColors(Palette));

        surfaces.Keys.Should().Contain(
        [
            "SurfaceBaseBrush", "SurfaceRaisedBrush", "SurfaceOverlayBrush", "SurfaceInsetBrush",
            "SurfaceGhostBrush", "SurfaceCardBrush", "SurfaceCardHoverBrush", "SurfaceElevatedBrush",
            "SurfaceActiveBrush", "SurfacePressedBrush", "SurfaceTintBrush", "SurfaceStrongBrush",
            "BorderSubtleBrush", "BorderDefaultBrush", "BorderStrongBrush", "BorderProminentBrush",
            "TextPrimaryBrush", "TextSecondaryBrush", "TextMutedBrush", "TextDimBrush", "TextDisabledBrush",
            "ScrimBrush", "ScrimSoftBrush", "ScrimLightBrush", "ScrimPanelBrush",
        ]);

        // Every one must name a token the shipped dictionary defines, or that surface stays on the
        // previous palette.
        foreach (string key in surfaces.Keys)
            Palette.Contains(key).Should().BeTrue($"'{key}' is repainted by name");
    }

    [Theory]
    [MemberData(nameof(EveryPalette))]
    public void The_wpf_ui_shell_keys_are_all_ones_the_palette_actually_overrides(string id)
    {
        // These are WPF-UI internals, not a public contract. A misspelling silently leaves that surface
        // grey, which is the most common way this theme breaks.
        var shell = AccentPalette.FromId(id).ShellColors(new DictionaryColors(Palette));

        shell.Keys.Should().Contain(["ApplicationBackgroundBrush", "SolidBackgroundFillColorBaseBrush",
                                     "CardBackgroundFillColorDefaultBrush", "LayerFillColorDefaultBrush"]);
        shell.Should().NotContainKey("ApplicationBackgroundColorBrush", "that key does not exist in WPF-UI");

        foreach (string key in shell.Keys)
            Palette.Contains(key).Should().BeTrue(
                $"'{key}' must be redefined in Themes/Colors.xaml, or the repaint has nothing to write to");
    }

    [Theory]
    [MemberData(nameof(EveryPalette))]
    public void Two_palettes_never_produce_the_same_surfaces(string id)
    {
        // A palette whose neutrals were left pointing at another ramp would look applied in the buttons
        // and identical everywhere else - the exact bug, reintroduced.
        var mine = AccentPalette.FromId(id).SurfaceColors(new DictionaryColors(Palette));

        foreach (var other in AccentPalette.All.Where(o => o.Id != id))
        {
            var theirs = other.SurfaceColors(new DictionaryColors(Palette));
            mine["SurfaceBaseBrush"].Should().NotBe(theirs["SurfaceBaseBrush"]);
            mine["TextPrimaryBrush"].Should().NotBe(theirs["TextPrimaryBrush"]);
            mine["SurfaceCardBrush"].Should().NotBe(theirs["SurfaceCardBrush"]);
        }
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
