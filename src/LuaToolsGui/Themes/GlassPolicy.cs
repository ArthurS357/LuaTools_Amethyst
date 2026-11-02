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
/// layout, and it is a deliberate limit: the page tokens (TextDim on an elevated card is 4.62:1 solid) cannot
/// lose any contrast and stay AA, so translucent pages would need a ~95% opaque tint, which is no glass at all.
/// </para>
///
/// <para>
/// <b>Acrylic, not Mica (1.7.12.9).</b> 1.7.12.8 shipped Mica (DWMSBT_MAINWINDOW), and on a dark window it
/// reads as no effect at all: it does not blur, it paints one flat colour sampled from the wallpaper, and in
/// dark mode that is a neutral grey (measured #202020 over white, black and yellow alike). Under the shell
/// overlay it was indistinguishable from the solid window. Acrylic (DWMSBT_TRANSIENTWINDOW) is a real blur of
/// what is behind the window. The price: it costs GPU while the window is composited, which is what the off
/// switch is for, and it lets a bright desktop's luminance through.
/// </para>
///
/// <para>
/// <b>The overlay.</b> The shell is SurfaceBase at <see cref="ShellAlpha"/> (65%, 1.7.12.10) over the backdrop.
/// Dark Acrylic measured #929292 over a white window and #989800 over yellow (luminance 0.287 / 0.291), so the
/// worst-case backdrop is <see cref="WorstCaseBackdropGrey"/> = #989898, which covers both. Against that, 50%
/// leaves accent text at 2.8:1, 70% at 4.4:1 and 80% is the lowest step that is AA for every shell text token
/// in all three palettes. 80% shipped in 1.7.12.9 and the effect was real (the shell colour followed the
/// desktop behind the window: #2B2627 over white, #191415 over black) but read as a solid window, so 1.7.12.10
/// trades contrast for looks on purpose: 65% keeps the texts readable (<see cref="MinShellTextContrast"/>) but
/// no longer guarantees AA over a bright desktop. Over a dark desktop it stays AA (<c>GlassmorphismTests</c>).
/// </para>
/// </summary>
public static class GlassPolicy
{
    /// <summary>The shell fill while glass is active: SurfaceBase at <see cref="ShellAlpha"/>.</summary>
    public const string GlassShellBrushKey = "GlassShellBrush";

    /// <summary>The shell fill otherwise: the solid window colour, exactly as before the glass existed.</summary>
    public const string SolidShellBrushKey = "SurfaceBaseBrush";

    /// <summary>Alpha of SurfaceBase over the Acrylic backdrop. 0xA6 = 65%.</summary>
    public const byte ShellAlpha = 0xA6;

    /// <summary>
    /// Floor for shell text contrast over <see cref="WorstCaseBackdropGrey"/>. Below AA (4.5:1) on purpose,
    /// see the overlay note above; it is the large-text / UI-component level, so nothing becomes unreadable.
    /// </summary>
    public const double MinShellTextContrast = 3.0;

    /// <summary>A typical dark desktop under dark Acrylic (measured #363636 over black), where AA still holds.</summary>
    public const byte DarkDesktopBackdropGrey = 0x36;

    /// <summary>
    /// The backdrop the contrast guarantee is computed against: dark Acrylic over the brightest desktop.
    /// Measured on Windows 11 26200: #929292 over white, #989800 over yellow, #363636 over black.
    /// </summary>
    public const byte WorstCaseBackdropGrey = 0x98;

    /// <summary>
    /// Glass is shown only when the user wants it, the OS is Windows 11 (<paramref name="micaSupported"/>:
    /// Mica support is the Windows 11 test, since WPF-UI reports Acrylic as supported back to Windows 7, where
    /// DWMSBT does not exist), and Windows is not in high contrast. Anything else gets the solid window, which
    /// is never worse than before this existed.
    /// </summary>
    public static bool IsActive(bool enabled, bool micaSupported, bool highContrast) =>
        enabled && micaSupported && !highContrast;

    /// <summary>The resource key the shell (root grid) paints with.</summary>
    public static string ShellBrushKey(bool active) => active ? GlassShellBrushKey : SolidShellBrushKey;
}
