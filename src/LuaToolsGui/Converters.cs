using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LuaToolsGui;

/// <summary>
/// Loads an image path/URL into a BitmapImage with CacheOption=OnLoad, so cached cover files on
/// disk aren't left locked (allowing later refresh/clear). Handles local paths and http(s) URLs.
///
/// <para>
/// <b>ConverterParameter = decode width in pixels</b> (optional). Covers are Steam's 460×215 header.jpg
/// (newer capsules are larger) and were decoded at full size even for a 92×43 row thumbnail: every visible
/// row then held the full bitmap. With a width, WPF decodes straight to that size, keeping the aspect ratio.
/// Pick it at the call site from the drawn width ×2 (crisp at 200% scaling) and never above 460, or a
/// header.jpg would be UPSCALED on decode and cost more, not less. No parameter = full size, as before.
/// </para>
/// </summary>
public class ImagePathToSourceConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s || string.IsNullOrWhiteSpace(s)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            if (DecodeWidth(parameter) is { } width) bmp.DecodePixelWidth = width;
            bmp.UriSource = new Uri(s, UriKind.Absolute);
            bmp.EndInit();

            // A local file is fully decoded by now (OnLoad) and can be frozen: no change tracking, and safe to
            // share. An http(s) cover is still downloading and cannot be frozen until it finishes; leave it.
            if (bmp.CanFreeze) bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null; // bad path/URL — show nothing
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    /// <summary>The decode width from a ConverterParameter (int or numeric string), or null for full size.
    /// Anything unparseable, zero or negative means full size: a bad parameter must never blank a cover.</summary>
    internal static int? DecodeWidth(object? parameter) => parameter switch
    {
        int i when i > 0 => i,
        string str when int.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out int w) && w > 0 => w,
        _ => null,
    };
}

public class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null or "" ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Collapse a block unless there is something to show AND enough room to show a useful amount of it.
/// Bind [0] = a has-content bool, [1] = the AVAILABLE height, with the minimum in px as
/// ConverterParameter.
///
/// <para>
/// [1] must be the height of the CONTAINER, never of the element being hidden. Binding a control's own
/// Visibility to its own ActualHeight is circular: collapsing drives the height to 0, which fails the
/// test the other way and flips it back, and WPF oscillates.
/// </para>
/// </summary>
public class MinHeightToVisibilityConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        bool hasContent = values.Length > 0 && values[0] is true;
        double available = values.Length > 1 && values[1] is double h ? h : 0;
        double min = double.TryParse(parameter?.ToString(), NumberStyles.Any,
                                     CultureInfo.InvariantCulture, out double m) ? m : 0;

        return hasContent && available >= min ? Visibility.Visible : Visibility.Collapsed;
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Multi-binding equality: true when all bound values are equal. Used by the pager to mark the
/// active page button (binds the button's page number + the current page).</summary>
public class EqualityMultiConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is null || values.Length < 2 || values.Any(v => v is null)) return false;
        var first = System.Convert.ToInt64(values[0]);
        return values.Skip(1).All(v => System.Convert.ToInt64(v) == first);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Maps a Manage filter/sort option's stored value (an English key like "Recently added" or
/// "Any") to its localized DISPLAY label, leaving the stored value itself unchanged so all the
/// switch/equality logic in ManageViewModel keeps working. Unknown values (Steam genres/types/years)
/// pass through verbatim — they're data, not UI chrome.</summary>
public class FilterOptionDisplayConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s ? s switch
        {
            "Any" => Resources.Strings.Manage_Opt_Any,
            "Free" => Resources.Strings.Manage_Opt_Free,
            "Paid" => Resources.Strings.Manage_Opt_Paid,
            "Hide adult" => Resources.Strings.Manage_Content_HideAdult,
            "Adult only" => Resources.Strings.Manage_Content_AdultOnly,
            "Installed first" => Resources.Strings.Manage_Sort_InstalledFirst,
            "Recently added" => Resources.Strings.Manage_Sort_RecentlyAdded,
            "Name (A–Z)" => Resources.Strings.Manage_Sort_NameAZ,
            "Release date (newest)" => Resources.Strings.Manage_Sort_ReleaseNewest,
            "Metacritic" => Resources.Strings.Manage_Sort_Metacritic,
            "Most reviewed" => Resources.Strings.Manage_Sort_MostReviewed,
            "All" => Resources.Strings.Manage_PageSize_All, // page-size dropdown
            _ => s, // genre/type/year/page-number data — show as-is
        } : value;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Resolves a theme resource KEY (e.g. "SuccessTextBrush") to the live brush from Themes/Colors.xaml.
/// <para>
/// A few view-models have to vary a colour at runtime (plugin status, Hubcap key status). They used to do
/// it by holding a literal "#22c55e" string that WPF's type converter turned into a brush at binding time,
/// which put presentation colours inside the view-model and left them invisible to the theme. They now
/// hold a resource key instead, so the palette stays the single source of truth and re-skinning the app
/// never means grepping the ViewModels folder.
/// </para>
/// <para>Falls back to <c>TextMutedBrush</c>, then to transparent, if a key is missing — a mistyped key
/// should render dimly, not throw inside a binding (where the exception would be swallowed silently).</para>
/// </summary>
public class ResourceKeyToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string key && !string.IsNullOrWhiteSpace(key)
            && Application.Current?.TryFindResource(key) is Brush brush)
            return brush;

        return Application.Current?.TryFindResource("TextMutedBrush") as Brush
               ?? (Brush)Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Source status → badge color (mirrors the website's StatusBadge).</summary>
public class StatusToBrushConverter : IValueConverter
{
    // Resolved lazily from the theme dictionary (Themes/Colors.xaml) rather than built from literals here,
    // so these badges follow the palette like everything else.
    private static Brush Resolve(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.Transparent;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        "available" => Resolve("SuccessBrush"),
        "unavailable" => Resolve("TextDimBrush"),
        _ => Resolve("AlertBrush"),
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
