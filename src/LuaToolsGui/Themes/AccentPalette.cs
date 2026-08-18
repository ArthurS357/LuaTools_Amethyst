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
/// <param name="Id">Stable identifier persisted in settings.json. Never localised.</param>
/// <param name="SoftKey">300 — accent text on dark; WPF-UI tertiary slot.</param>
/// <param name="PrimaryKey">400 — accent icons/focus ring; WPF-UI PRIMARY slot.</param>
/// <param name="BorderKey">500 — borders and other non-text UI; WPF-UI secondary slot.</param>
/// <param name="FillKey">600 — filled buttons, which pair it with white text; WPF-UI system slot.</param>
public sealed record AccentPalette(string Id, string SoftKey, string PrimaryKey, string BorderKey, string FillKey)
{
    /// <summary>The app's identity colour and the default for anyone who never touches the setting.</summary>
    public static readonly AccentPalette Amethyst =
        new("Amethyst", "Violet300Color", "Violet400Color", "Violet500Color", "Violet600Color");

    public static readonly AccentPalette Green =
        new("Green", "Emerald300Color", "Emerald400Color", "Emerald500Color", "Emerald600Color");

    public static readonly AccentPalette Red =
        new("Red", "Rose300Color", "Rose400Color", "Rose500Color", "Rose600Color");

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
