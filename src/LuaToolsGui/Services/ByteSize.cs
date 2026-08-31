using System.Globalization;

namespace LuaToolsGui.Services;

/// <summary>
/// Byte counts as text, for disk-budget messages and live download metrics.
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

    /// <summary>
    /// A transfer rate, e.g. "4.2 MB/s". EMPTY when the rate is not yet measurable, so the label hides
    /// itself instead of claiming a download is stalled at 0 B/s before the first sampling window closes.
    /// </summary>
    /// <remarks>
    /// "/s" is a unit symbol, not prose, so it stays unlocalized alongside the KB/MB/GB above. The number
    /// itself still goes through <see cref="Format"/> and so keeps the user's decimal separator.
    /// </remarks>
    public static string Rate(double bytesPerSecond, CultureInfo? culture = null) =>
        double.IsNaN(bytesPerSecond) || bytesPerSecond <= 0
            ? ""
            : Format((long)bytesPerSecond, culture) + "/s";

    /// <summary>
    /// A remaining time, e.g. "1m 12s" / "8s" / "2h 5m". Empty for a non-positive or absurd duration.
    /// </summary>
    /// <remarks>
    /// A day or more is treated as unmeasurable rather than rendered: an ETA that long only ever comes
    /// from a rate sampled during a stall, and "17h 3m remaining" is worse than saying nothing.
    /// </remarks>
    public static string Duration(TimeSpan t)
    {
        if (t <= TimeSpan.Zero || t.TotalDays >= 1) return "";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}m {t.Seconds}s";
        return $"{Math.Max(1, (int)Math.Ceiling(t.TotalSeconds))}s";
    }
}
