using System.Windows;
using System.Windows.Media;

namespace LuaToolsGui.Themes;

/// <summary>
/// Pushes a set of colours into live resources, in place.
///
/// <para>
/// This is what makes an accent change take effect without restarting. Views bind with
/// <c>{StaticResource}</c>, which resolves ONCE at load and never looks the key up again — so replacing a
/// dictionary entry with a new brush changes nothing on screen. What a view is holding is the brush
/// OBJECT, and mutating that object's <c>Color</c> repaints every consumer of it immediately. None of the
/// app's brushes are frozen, which is the only reason that door is open; freezing one would silently
/// remove it from the live switch.
/// </para>
///
/// <para>
/// <c>Color</c> resources cannot work that way — a <see cref="Color"/> is a struct, and anything that
/// resolved one already copied the value. Those are rewritten as dictionary entries instead, which is
/// enough for whatever resolves them later (popups, flyouts, windows opened after the change) and is the
/// reason a handful of WPF-UI surfaces still need a relaunch to fully catch up. Every key the app itself
/// paints with is a brush, so the visible switch is complete.
/// </para>
///
/// <para>
/// Split out of <c>App</c> so the mechanism is testable: a plain <see cref="ResourceDictionary"/> stands
/// in for <c>Application.Resources</c>, and a test can assert the SAME brush instance changed colour —
/// which is precisely the claim "no restart needed" rests on.
/// </para>
/// </summary>
public static class ThemeRepaint
{
    /// <summary>
    /// Apply <paramref name="colors"/> to <paramref name="target"/> and report how many keys landed.
    ///
    /// <para>
    /// The count is the diagnostic: a palette that names a key the dictionary does not have is a
    /// half-switched theme, and it fails silently otherwise. Keys that are absent, frozen, or of some
    /// other type are skipped rather than thrown over — a wrong-looking theme must never stop the app.
    /// </para>
    /// </summary>
    public static int Apply(ResourceDictionary target, IReadOnlyDictionary<string, Color> colors)
    {
        int applied = 0;

        foreach (var (key, color) in colors)
        {
            switch (Lookup(target, key))
            {
                case SolidColorBrush { IsFrozen: false } brush:
                    Paint(brush, color);
                    applied++;
                    break;

                // Written at the TOP level on purpose: a top-level entry outranks every merged
                // dictionary, so this wins over both WPF-UI's value and our own XAML default.
                case Color:
                    target[key] = color;
                    applied++;
                    break;
            }
        }

        return applied;
    }

    /// <summary>
    /// Set the colour as a plain local value, and keep the brush UNFREEZABLE with a dynamic reference on
    /// Opacity instead. The assignment drops the brush's <c>{DynamicResource}</c> on Color, and a brush with
    /// no expression is freezable — the next <c>Style</c> using it in a setter (27 in the views) freezes it
    /// when sealed, and every later switch skips it. NOT <c>SetCurrentValue</c>: a template trigger clones
    /// the brush on hover, and Freezable.CloneCore throws InvalidCastException (Color → Expression) on a
    /// current value laid over an expression. The key never exists, so Opacity resolves to its default 1.
    /// </summary>
    private static void Paint(SolidColorBrush brush, Color color)
    {
        brush.Color = color;
        if (!DependencyPropertyHelper.GetValueSource(brush, Brush.OpacityProperty).IsExpression)
            brush.SetValue(Brush.OpacityProperty, new DynamicResourceExtension(KeepMutableKey).ProvideValue(null!));
    }

    private const string KeepMutableKey = "LuaTools.ThemeRepaint.KeepMutable";

    /// <summary>
    /// Repaint every unfrozen brush under <paramref name="target"/> that still holds a ramp colour of ANY
    /// palette, keeping its alpha. For WPF-UI's own derived brushes (<c>AccentButtonBackground</c>,
    /// <c>ToggleSwitchFillOn</c>, <c>TextControlFocusedBorderBrush</c>, <c>HyperlinkButtonForeground</c>…):
    /// they are bound to the accent colours with <c>{DynamicResource}</c>, but once a control template has
    /// instantiated one, that binding no longer follows a switch — measured, ~38 of them. Matching on the
    /// colour rather than listing the keys covers the whole family, and any a WPF-UI upgrade adds.
    /// </summary>
    public static int Retint(ResourceDictionary target, IReadOnlyDictionary<(byte R, byte G, byte B), Color> map)
    {
        int applied = 0;

        foreach (object key in target.Keys.Cast<object>().ToList())
        {
            object? value;
            try { value = target[key]; }
            catch (Exception) { continue; } // an entry that cannot materialise is not worth a half-themed app

            if (value is SolidColorBrush { IsFrozen: false } brush &&
                map.TryGetValue((brush.Color.R, brush.Color.G, brush.Color.B), out var to))
            {
                Paint(brush, Color.FromArgb(brush.Color.A, to.R, to.G, to.B));
                applied++;
            }
        }

        foreach (var merged in target.MergedDictionaries) applied += Retint(merged, map);
        return applied;
    }

    /// <summary>
    /// Find a resource the way WPF resolves one: this dictionary first, then merged dictionaries in
    /// REVERSE order.
    ///
    /// <para>
    /// The reverse walk is not a detail. WPF-UI's theme is merged before the app's palette and defines
    /// several of the same keys, so a forward scan finds WPF-UI's grey and hands back the very value the
    /// override exists to replace.
    /// </para>
    /// </summary>
    private static object? Lookup(ResourceDictionary dictionary, string key)
    {
        if (dictionary.Contains(key)) return dictionary[key];

        var merged = dictionary.MergedDictionaries;
        for (int i = merged.Count - 1; i >= 0; i--)
            if (Lookup(merged[i], key) is { } found) return found;

        return null;
    }
}
