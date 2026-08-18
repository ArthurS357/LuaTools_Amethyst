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
                    brush.Color = color;
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
