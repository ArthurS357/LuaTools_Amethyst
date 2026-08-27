using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using AwesomeAssertions;
using Wpf.Ui.Controls;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Checks that every theme token the views name actually exists.
///
/// <para>
/// XAML is not fully verified at build time: a <c>{StaticResource}</c> naming a token that does not exist
/// compiles perfectly and throws the first time the page is constructed. This release hit exactly that —
/// the new About markup reached for <c>TextTertiaryBrush</c>, which reads entirely plausibly and is not a
/// real token (it is <c>TextMutedBrush</c>). Nothing but running the page would have said so.
/// </para>
///
/// <para>
/// This is a static check over the view sources rather than a WPF page load: constructing a view needs
/// its view-model, and standing up the whole DI graph to prove a colour key exists is a poor trade. It
/// catches the missing-token case, which is the one that actually recurs.
/// </para>
///
/// <para>
/// The same is true of an icon name. <c>Symbol="Foo24"</c> and <c>{ui:SymbolIcon Foo24}</c> are parsed out
/// of BAML when the page is first shown, so a name that is not a real <see cref="SymbolRegular"/> member
/// is a plausible-looking string that throws only there — the identical failure mode as the missing token,
/// and worth the identical check.
/// </para>
/// </summary>
public class ViewParseTests
{
    private static readonly Regex TokenRef =
        new(@"\{StaticResource\s+(?<key>[A-Za-z0-9_]+)\s*\}", RegexOptions.Compiled);

    /// <summary>Both spellings of an icon: the property and the markup extension.</summary>
    private static readonly Regex SymbolRef =
        new(@"Symbol=""(?<name>[A-Za-z0-9_]+)""|\{ui:SymbolIcon\s+(?<name>[A-Za-z0-9_]+)\s*\}",
            RegexOptions.Compiled);

    /// <summary>Walks up from the test binaries to the repo, so this works from any run directory.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LuaToolsGui.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("LuaToolsGui.sln not found above the test output.");
    }

    private static string ViewsDir() => Path.Combine(RepoRoot(), "src", "LuaToolsGui", "Views");

    /// <summary>Keys defined by the shipped palette — the source of the app's own tokens.</summary>
    private static IReadOnlySet<string> PaletteKeys()
    {
        var dict = (ResourceDictionary)Application.LoadComponent(
            new Uri("/LuaTools;component/Themes/Colors.xaml", UriKind.Relative));
        return dict.Keys.Cast<object>().Select(k => k.ToString()!).ToHashSet(StringComparer.Ordinal);
    }

    public static TheoryData<string> ViewFiles()
    {
        var data = new TheoryData<string>();
        foreach (string f in Directory.GetFiles(ViewsDir(), "*.xaml"))
            data.Add(Path.GetFileName(f));
        return data;
    }

    [Theory]
    [MemberData(nameof(ViewFiles))]
    public void Every_palette_token_a_view_names_is_one_the_palette_defines(string file)
    {
        string xaml = File.ReadAllText(Path.Combine(ViewsDir(), file));
        var palette = PaletteKeys();

        // Only tokens that LOOK like palette entries are checked. A view also references its own local
        // styles, App.xaml converters and WPF-UI's keys, none of which live in Colors.xaml — flagging
        // those would make this fail constantly and get deleted, which is worse than not having it.
        var suspects = TokenRef.Matches(xaml)
            .Select(m => m.Groups["key"].Value)
            .Where(k => k.EndsWith("Brush", StringComparison.Ordinal))
            .Where(k => AppOwnedPrefixes.Any(p => k.StartsWith(p, StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var missing = suspects.Where(k => !palette.Contains(k)).ToList();

        missing.Should().BeEmpty(
            $"{file} names {{StaticResource}} tokens that Themes/Colors.xaml does not define, which throws "
          + "when the page is first shown");
    }

    /// <summary>
    /// Prefixes that belong to THIS app's palette, so a miss is a real miss.
    ///
    /// <para>
    /// The status colours are here for the same reason the first four were: they are defined in
    /// Colors.xaml, nothing else supplies them, and the Home dashboard paints Steam's running state and
    /// the self-update posture with them. WPF-UI's own keys (<c>SystemFillColor…</c>,
    /// <c>CardBackgroundFillColor…</c>) deliberately match none of these — flagging those would make the
    /// check fail constantly and get deleted, which is worse than not having it.
    /// </para>
    /// </summary>
    private static readonly string[] AppOwnedPrefixes =
        ["Text", "Accent", "Surface", "Border", "Success", "Warning", "Danger", "Info", "Alert"];

    [Theory]
    [MemberData(nameof(ViewFiles))]
    public void Every_icon_a_view_names_is_a_real_symbol(string file)
    {
        string xaml = File.ReadAllText(Path.Combine(ViewsDir(), file));

        var unknown = SymbolRef.Matches(xaml)
            .Select(m => m.Groups["name"].Value)
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Where(n => !Enum.IsDefined(typeof(SymbolRegular), n))
            .ToList();

        unknown.Should().BeEmpty(
            $"{file} names icons that Wpf.Ui's SymbolRegular does not define, which throws when the page "
          + "is first shown");
    }

    [Fact]
    public void The_status_tokens_the_home_dashboard_paints_with_all_exist()
    {
        // Home reports Steam open/closed and whether self-update is off. Both read as a colour before they
        // read as a word, so a renamed token would take the meaning with it.
        var palette = PaletteKeys();

        foreach (string key in new[] { "SuccessTextBrush", "SuccessTintBrush", "SurfaceTintBrush",
                                       "AccentTintBrush", "AccentOutlineBrush", "DangerBrush" })
        {
            palette.Should().Contain(key);
        }
    }

    [Fact]
    public void The_accent_tokens_the_new_markup_paints_with_all_exist()
    {
        // The changelog card and the accent picker bind these by name; each is also repainted by
        // App.RepaintAccentBrushes, so a rename would break the colour switch as well as the page.
        var palette = PaletteKeys();

        foreach (string key in new[]
                 { "AccentBrush", "AccentSoftBrush", "AccentBadgeBrush", "AccentOutlineBrush",
                   "TextMutedBrush", "TextSecondaryBrush", "SurfaceCardBrush", "BorderStrongBrush" })
        {
            palette.Should().Contain(key);
        }
    }
}
