namespace LuaToolsGui.Themes;

/// <summary>
/// When the main window shows the glass effect, and what the shell is painted with. Pure on purpose: the
/// decision is testable without a window, a DWM, or Windows 11.
///
/// <para>
/// <b>What is glass and what is not.</b> Only the shell: the title bar and the nav rail, which sit directly on
/// the root grid. Pages are NOT translucent: WPF-UI's Left NavigationView template draws the page area as its
/// own opaque, rounded <c>Border</c> filled with <c>NavigationViewContentBackground</c> (= SurfaceBase), so
/// every text token keeps the contrast Themes/Colors.xaml signs off on. That is the Windows 11 Settings
/// layout, and it is a deliberate limit: measured against even the darkest backdrop Windows gives a dark
/// window, the page tokens (TextDim on an elevated card is 4.62:1 solid) cannot lose any contrast and stay
/// AA, so translucent pages would need a ~95% opaque tint, which is no glass at all.
/// </para>
///
/// <para>
/// <b>Mica, not Acrylic.</b> Mica (DWMSBT_MAINWINDOW) is the backdrop Windows specifies for a long-lived main
/// window: it samples the wallpaper once and pins the LUMINANCE to the dark theme's base (measured #272727
/// here), letting only the wallpaper's hue through. Acrylic (DWMSBT_TRANSIENTWINDOW) is a live blur meant for
/// flyouts: it costs GPU on every frame behind the window, which is what the off switch is for, and lets more
/// of a bright desktop's luminance bleed through. A pinned-dark backdrop is what makes the contrast below a
/// guarantee rather than a hope.
/// </para>
///
/// <para>
/// <b>The overlay.</b> The shell is SurfaceBase at <see cref="ShellAlpha"/> (60%) over the backdrop, not black
/// at 20–30%: black that thin over a light backdrop leaves body text near 2:1. 60% of the palette's own window
/// colour keeps every shell text token (nav labels, TextMuted, TextDim footer, Danger) at AA with margin over a
/// #3A3A3A backdrop, well above the measured Mica, in all three palettes (<c>GlassContrastTests</c>), and keeps
/// the shell reading amethyst rather than Mica's neutral grey, which is why the backdrop was turned off before.
/// </para>
/// </summary>
public static class GlassPolicy
{
    /// <summary>The shell fill while glass is active: SurfaceBase at <see cref="ShellAlpha"/>.</summary>
    public const string GlassShellBrushKey = "GlassShellBrush";

    /// <summary>The shell fill otherwise: the solid window colour, exactly as before the glass existed.</summary>
    public const string SolidShellBrushKey = "SurfaceBaseBrush";

    /// <summary>Alpha of SurfaceBase over the Mica backdrop. 0x99 = 60%.</summary>
    public const byte ShellAlpha = 0x99;

    /// <summary>
    /// The backdrop the contrast guarantee is computed against: a dark Mica with headroom. Measured Mica on a
    /// dark-themed window is #272727; #3A3A3A covers a brighter wallpaper's hue bleed and DWM variations.
    /// </summary>
    public const byte WorstCaseBackdropGrey = 0x3A;

    /// <summary>
    /// Glass is shown only when the user wants it, the OS can draw Mica (Windows 11), and Windows is not in
    /// high contrast. Anything else gets the solid window, which is never worse than before this existed.
    /// </summary>
    public static bool IsActive(bool enabled, bool micaSupported, bool highContrast) =>
        enabled && micaSupported && !highContrast;

    /// <summary>The resource key the shell (root grid) paints with.</summary>
    public static string ShellBrushKey(bool active) => active ? GlassShellBrushKey : SolidShellBrushKey;
}
