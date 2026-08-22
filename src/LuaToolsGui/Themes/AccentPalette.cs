using System.Windows;
using System.Windows.Media;

namespace LuaToolsGui.Themes;

/// <summary>
/// One selectable accent ramp, named by the four primitive keys it reads out of Themes/Colors.xaml.
///
/// <para>
/// The palette carries KEY NAMES rather than colours so Colors.xaml stays the single source of truth for
/// every hex in the app — the same arrangement <c>App.ApplyAccentPalette</c> already relied on for the
/// violet ramp. Those names are effectively API: renaming one in the XAML without updating the palette
/// here drops the accent back to the Windows default, silently, and only the startup theme guard notices.
/// </para>
///
/// <para>
/// Four weights, matching WPF-UI's four accent slots. On a dark surface the 400 weight is what paints
/// accent text and thin strokes, which is why it — not the saturated 600 — goes into the primary slot.
/// </para>
/// </summary>
/// <summary>
/// The tonal half of a palette: the neutral ramp that paints the window, the cards, the dialogs and the
/// body text, named by the keys it reads out of Themes/Colors.xaml.
///
/// <para>
/// Before this existed a palette described only its four accent weights, so choosing green repainted the
/// buttons and left every surface and every line of text on the violet axis. The app read as "a purple
/// app with green buttons" — which is what made the setting feel like it had not taken effect, and why
/// users assumed it needed a restart to finish.
/// </para>
///
/// <para>
/// Every step of every ramp carries the same RELATIVE LUMINANCE as the Plum step it stands in for (see
/// the derivation note in Colors.xaml), so contrast ratios are a property of the STEP, not of the
/// palette. That is what lets the accessibility floors be asserted once and hold for all three.
/// </para>
/// </summary>
/// <param name="DeepKey">Below the window background — WPF-UI's BaseAlt shell fill.</param>
/// <param name="SurfaceBaseKey">The window background.</param>
/// <param name="SurfaceRaisedKey">Cards and panels.</param>
/// <param name="SurfaceOverlayKey">Dialogs, flyouts, popups.</param>
/// <param name="SurfaceInsetKey">Inset chips and selected rows.</param>
/// <param name="TextDisabledKey">Disabled text.</param>
/// <param name="TextDimKey">Dim text.</param>
/// <param name="TextMutedKey">Muted text.</param>
/// <param name="TextSecondaryKey">Secondary text.</param>
/// <param name="TextPrimaryKey">Primary text.</param>
/// <param name="ElevationTintKey">The near-white the translucent surface and border weights are alpha
/// blends of, so a "lighter" surface drifts toward the palette hue instead of washing out to grey.</param>
public sealed record NeutralKeys(
    string DeepKey,
    string SurfaceBaseKey,
    string SurfaceRaisedKey,
    string SurfaceOverlayKey,
    string SurfaceInsetKey,
    string TextDisabledKey,
    string TextDimKey,
    string TextMutedKey,
    string TextSecondaryKey,
    string TextPrimaryKey,
    string ElevationTintKey)
{
    /// <summary>Build a ramp from a shared prefix — every ramp uses the same step suffixes, so naming the
    /// family is enough and a typo in one of eleven keys becomes impossible.</summary>
    public static NeutralKeys Ramp(string family) => new(
        $"{family}975Color", $"{family}950Color", $"{family}900Color", $"{family}850Color",
        $"{family}800Color", $"{family}500Color", $"{family}400Color", $"{family}300Color",
        $"{family}100Color", $"{family}50Color", $"{family}TintColor");
}

/// <param name="Id">Stable identifier persisted in settings.json. Never localised.</param>
/// <param name="SoftKey">300 — accent text on dark; WPF-UI tertiary slot.</param>
/// <param name="PrimaryKey">400 — accent icons/focus ring; WPF-UI PRIMARY slot.</param>
/// <param name="BorderKey">500 — borders and other non-text UI; WPF-UI secondary slot.</param>
/// <param name="FillKey">600 — filled buttons, which pair it with white text; WPF-UI system slot.</param>
public sealed record AccentPalette(
    string Id, string SoftKey, string PrimaryKey, string BorderKey, string FillKey, NeutralKeys Neutrals)
{
    /// <summary>The app's identity colour and the default for anyone who never touches the setting.</summary>
    public static readonly AccentPalette Amethyst =
        new("Amethyst", "Violet300Color", "Violet400Color", "Violet500Color", "Violet600Color",
            NeutralKeys.Ramp("Plum"));

    public static readonly AccentPalette Green =
        new("Green", "Emerald300Color", "Emerald400Color", "Emerald500Color", "Emerald600Color",
            NeutralKeys.Ramp("Moss"));

    public static readonly AccentPalette Red =
        new("Red", "Rose300Color", "Rose400Color", "Rose500Color", "Rose600Color",
            NeutralKeys.Ramp("Wine"));

    /// <summary>Every selectable palette, in the order the settings picker shows them.</summary>
    public static IReadOnlyList<AccentPalette> All { get; } = [Amethyst, Green, Red];

    /// <summary>Resolve a persisted id. Falls back to <see cref="Amethyst"/> for null, blank, or a value
    /// written by a newer build — a settings file from the future must not leave the app unthemed.</summary>
    public static AccentPalette FromId(string? id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Amethyst;

    /// <summary>
    /// The eight app-authored accent brushes, mapped to the colour each should take under this palette.
    ///
    /// <para>
    /// The washes are the 400 weight at fixed alphas and the badge is the 600 weight at its own, so a
    /// palette is fully described by its four primitives — adding one is four hex values, not twelve.
    /// The alphas are the ones the violet palette shipped with, kept so a re-skin changes hue only.
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<string, Color> BrushColors(IResolveColor resolve)
    {
        Color primary = resolve.Color(PrimaryKey);
        Color fill = resolve.Color(FillKey);

        return new Dictionary<string, Color>(StringComparer.Ordinal)
        {
            ["AccentBrush"] = primary,
            ["AccentSoftBrush"] = resolve.Color(SoftKey),
            ["AccentTintBrush"] = At(primary, 0x14),
            ["AccentTintHoverBrush"] = At(primary, 0x1A),
            ["AccentTintStrongBrush"] = At(primary, 0x26),
            ["AccentTintActiveBrush"] = At(primary, 0x33),
            ["AccentOutlineBrush"] = At(primary, 0x40),
            ["AccentBadgeBrush"] = At(fill, 0x20),
        };
    }

    /// <summary>
    /// Every semantic surface, border and text brush the app's own markup paints with, mapped to the
    /// colour it should take under this palette.
    ///
    /// <para>
    /// The alpha weights are the ones Colors.xaml shipped with, kept verbatim: a re-skin moves hue only,
    /// and elevation must keep reading the same. What changes per palette is the tint the translucent
    /// weights are blends of, and the solid colours behind them.
    /// </para>
    ///
    /// <para>
    /// The scrims are alpha over the palette's OWN window colour rather than black, so a dialog backdrop
    /// reads as "the app dimmed" instead of a grey film laid over it.
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<string, Color> SurfaceColors(IResolveColor resolve)
    {
        Color surfaceBase = resolve.Color(Neutrals.SurfaceBaseKey);
        Color raised = resolve.Color(Neutrals.SurfaceRaisedKey);
        Color overlay = resolve.Color(Neutrals.SurfaceOverlayKey);
        Color inset = resolve.Color(Neutrals.SurfaceInsetKey);
        Color tint = resolve.Color(Neutrals.ElevationTintKey);

        return new Dictionary<string, Color>(StringComparer.Ordinal)
        {
            // Solid surfaces
            ["SurfaceBaseBrush"] = surfaceBase,
            ["SurfaceRaisedBrush"] = raised,
            ["SurfaceOverlayBrush"] = overlay,
            ["SurfaceInsetBrush"] = inset,

            // Elevation overlays — alpha over the palette tint
            ["SurfaceGhostBrush"] = At(tint, 0x0A),
            ["SurfaceCardBrush"] = At(tint, 0x12),
            ["SurfaceCardHoverBrush"] = At(tint, 0x16),
            ["SurfaceElevatedBrush"] = At(tint, 0x20),
            ["SurfaceActiveBrush"] = At(tint, 0x2A),
            ["SurfacePressedBrush"] = At(tint, 0x30),
            ["SurfaceTintBrush"] = At(tint, 0x1A),
            ["SurfaceStrongBrush"] = At(tint, 0x26),

            // Borders
            ["BorderSubtleBrush"] = At(tint, 0x18),
            ["BorderDefaultBrush"] = At(tint, 0x1A),
            ["BorderStrongBrush"] = At(tint, 0x26),
            ["BorderProminentBrush"] = At(tint, 0x3D),

            // Text
            ["TextPrimaryBrush"] = resolve.Color(Neutrals.TextPrimaryKey),
            ["TextSecondaryBrush"] = resolve.Color(Neutrals.TextSecondaryKey),
            ["TextMutedBrush"] = resolve.Color(Neutrals.TextMutedKey),
            ["TextDimBrush"] = resolve.Color(Neutrals.TextDimKey),
            ["TextDisabledBrush"] = resolve.Color(Neutrals.TextDisabledKey),

            // Scrims
            ["ScrimBrush"] = At(surfaceBase, 0xB2),
            ["ScrimSoftBrush"] = At(surfaceBase, 0x8C),
            ["ScrimLightBrush"] = At(surfaceBase, 0x73),
            ["ScrimPanelBrush"] = At(raised, 0xCC),
        };
    }

    /// <summary>
    /// WPF-UI's OWN surface keys, which Themes/Colors.xaml deliberately redefines.
    ///
    /// <para>
    /// These are what colour the window shell and every templated control — cards, flyouts, list rows,
    /// text boxes. Left alone they are neutral grey, so without this half the pixels on screen ignore the
    /// palette entirely. Both the <c>Color</c> and the <c>*Brush</c> spelling of each key is listed: the
    /// brush is what repaints live, the colour is what anything resolving later will read.
    /// </para>
    ///
    /// <para>
    /// The key NAMES are WPF-UI internals verified against 4.3.0, not a public contract — the version is
    /// pinned exactly for that reason, and the startup theme guard is what notices if an upgrade moves
    /// them.
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<string, Color> ShellColors(IResolveColor resolve)
    {
        Color deep = resolve.Color(Neutrals.DeepKey);
        Color surfaceBase = resolve.Color(Neutrals.SurfaceBaseKey);
        Color raised = resolve.Color(Neutrals.SurfaceRaisedKey);
        Color overlay = resolve.Color(Neutrals.SurfaceOverlayKey);
        Color inset = resolve.Color(Neutrals.SurfaceInsetKey);
        Color tint = resolve.Color(Neutrals.ElevationTintKey);

        var map = new Dictionary<string, Color>(StringComparer.Ordinal);

        // Keys that exist in both spellings.
        void Pair(string key, Color color)
        {
            map[key] = color;
            map[key + "Brush"] = color;
        }

        Pair("ApplicationBackgroundColor", surfaceBase);
        Pair("SolidBackgroundFillColorBase", surfaceBase);
        Pair("SolidBackgroundFillColorBaseAlt", deep);
        Pair("SolidBackgroundFillColorSecondary", raised);
        Pair("SolidBackgroundFillColorTertiary", overlay);
        Pair("SolidBackgroundFillColorQuarternary", inset);
        Pair("CardBackgroundFillColorDefault", At(tint, 0x12));
        Pair("CardBackgroundFillColorSecondary", At(tint, 0x0A));
        Pair("ControlFillColorDefault", At(tint, 0x14));
        Pair("ControlStrokeColorDefault", At(tint, 0x18));
        Pair("LayerFillColorAlt", At(tint, 0x12));

        // The layer fill is alpha over the INSET surface, not over the tint — it is a scrim behind
        // popups, so it darkens toward the palette rather than lightening away from it.
        Pair("LayerFillColorDefault", At(inset, 0x4C));

        // Brush-only keys: WPF-UI declares no Color counterpart for these.
        map["CardBackground"] = At(tint, 0x12);
        map["CardBackgroundDisabled"] = At(tint, 0x0B);
        map["CardBackgroundPointerOver"] = At(tint, 0x1A);
        map["CardBackgroundPressed"] = At(tint, 0x0A);

        // ApplicationBackgroundColor has no "…ColorBrush" spelling; the pair above produced
        // "ApplicationBackgroundColorBrush", which is not a key. The real one is this.
        map.Remove("ApplicationBackgroundColorBrush");
        map["ApplicationBackgroundBrush"] = surfaceBase;

        // NavigationView's own content background — a distinct key from ApplicationBackgroundBrush above
        // (see the comment on it in Colors.xaml). Without this an accent switch reached every surface
        // except the one behind the nav rail and pages, which stayed on whichever accent applied first.
        map["NavigationViewContentBackground"] = surfaceBase;
        map["NavigationViewItemForeground"] = resolve.Color(Neutrals.TextPrimaryKey);

        return map;
    }

    private static Color At(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);
}

/// <summary>Looks a <see cref="Color"/> up by resource key. Exists so palette maths can be exercised
/// against a plain dictionary in tests instead of requiring a live <see cref="Application"/>.</summary>
public interface IResolveColor
{
    Color Color(string key);
}

/// <summary>Resolves against a <see cref="ResourceDictionary"/>.</summary>
public sealed class DictionaryColors(ResourceDictionary source) : IResolveColor
{
    public Color Color(string key) => source[key] is Color c
        ? c
        : throw new KeyNotFoundException($"Themes/Colors.xaml has no Color named '{key}'.");
}
