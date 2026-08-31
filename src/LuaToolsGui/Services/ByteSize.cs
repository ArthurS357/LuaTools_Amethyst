using System.Globalization;

namespace LuaToolsGui.Services;

/// <summary>
/// Byte counts as text, for disk-budget messages.
/// </summary>
/// <remarks>
/// Deliberately separate from <c>BuildsViewModel.FormatSize</c>, which formats the "id · size · os · lang"
/// meta line and returns an EMPTY string for zero so the separator dots don't collapse around a "0 B" that
/// means "size unknown". Here zero is a real answer — "you have 0 B free" has to be sayable — so the two
/// contracts cannot be merged without breaking one of them.
/// </remarks>
internal static class ByteSize
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>
    /// A byte count at three significant figures, e.g. "4.49 GB", "812 MB", "0 B" — in the user's locale,
    /// so a Brazilian build says "4,49 GB".
    /// </summary>
    /// <param name="culture">
    /// Defaults to the current culture, which is what every caller wants. It is a parameter only so a test
    /// can pin the separator without mutating <see cref="CultureInfo.CurrentCulture"/> on a pooled thread,
    /// where the change outlives the test and can reach whatever runs there next.
    /// </param>
    public static string Format(long bytes, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        if (bytes <= 0) return "0 B";

        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        // Whole bytes never need a decimal; above that, two places up to 10 and one beyond keeps the width
        // stable enough for a label that sits next to a progress bar.
        string formatted = unit == 0
            ? value.ToString("0", culture)
            : value.ToString(value < 10 ? "0.##" : "0.#", culture);

        return $"{formatted} {Units[unit]}";
    }
}
