using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;
using AwesomeAssertions;
using LuaToolsGui.Services;
using LuaToolsGui.Themes;
using LuaToolsGui.ViewModels;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// The glass effect setting (Settings → General): that it persists, that it switches live, when the window
/// actually shows it, and that the translucent shell keeps every shell text token at WCAG AA.
/// </summary>
public class GlassmorphismTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "luatools-tests", Guid.NewGuid().ToString("N"));

    public GlassmorphismTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string SettingsFile => Path.Combine(_dir, "settings.json");

    private JsonElement Stored() => JsonDocument.Parse(File.ReadAllText(SettingsFile)).RootElement;

    // ── Persistence (settings.json) ─────────────────────────────────

    [Fact]
    public void Glass_is_on_by_default_and_writes_nothing_until_chosen()
    {
        new SettingsService(_dir).EnableGlassmorphism.Should().BeTrue();
        File.Exists(SettingsFile).Should().BeFalse("reading a default must not create settings.json");
    }

    [Fact]
    public void Turning_glass_off_survives_a_restart()
    {
        new SettingsService(_dir).EnableGlassmorphism = false;

        Stored().GetProperty("EnableGlassmorphism").GetBoolean().Should().BeFalse();
        new SettingsService(_dir).EnableGlassmorphism.Should().BeFalse();
    }

    [Fact]
    public void Turning_glass_back_on_is_stored_as_an_explicit_choice()
    {
        var settings = new SettingsService(_dir);
        settings.EnableGlassmorphism = false;
        settings.EnableGlassmorphism = true;

        Stored().GetProperty("EnableGlassmorphism").GetBoolean().Should().BeTrue();
        new SettingsService(_dir).EnableGlassmorphism.Should().BeTrue();
    }

    [Fact]
    public void A_glass_choice_alone_is_enough_to_keep_the_settings_file()
    {
        // SaveCore deletes settings.json when every persisted field is null; a field missing from that
        // predicate is silently lost on the next save (it happened to FixesPageSize).
        new SettingsService(_dir).EnableGlassmorphism = false;
        File.Exists(SettingsFile).Should().BeTrue();

        var reloaded = new SettingsService(_dir);
        reloaded.FastFetch = false; // any other save must not drop the glass choice
        new SettingsService(_dir).EnableGlassmorphism.Should().BeFalse();
    }

    // ── View-model ──────────────────────────────────────────────────

    private (SettingsViewModel Vm, List<bool> Applied, SettingsService Store) Build()
    {
        var store = new SettingsService(_dir);
        var vm = new SettingsViewModel(store, new AuthService(), new SteamService(store), new HubcapService());
        var applied = new List<bool>();
        vm.ApplyGlass = applied.Add;
        return (vm, applied, store);
    }

    [Fact]
    public void The_toggle_starts_from_the_saved_value_without_applying_or_saving()
    {
        new SettingsService(_dir).EnableGlassmorphism = false;
        DateTime written = File.GetLastWriteTimeUtc(SettingsFile);

        var (vm, applied, _) = Build();

        vm.EnableGlassmorphism.Should().BeFalse();
        applied.Should().BeEmpty("opening Settings must not re-apply the backdrop");
        File.GetLastWriteTimeUtc(SettingsFile).Should().Be(written);
    }

    [Fact]
    public void Flipping_the_toggle_persists_and_applies_live()
    {
        var (vm, applied, store) = Build();

        vm.EnableGlassmorphism = false;
        vm.EnableGlassmorphism = true;

        applied.Should().Equal(false, true);
        store.EnableGlassmorphism.Should().BeTrue();
        new SettingsService(_dir).EnableGlassmorphism.Should().BeTrue();
    }

    // ── Policy ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(false, true, false, false)]  // the user turned it off
    [InlineData(true, false, false, false)]  // Windows 10: no Mica → solid
    [InlineData(true, true, true, false)]    // high contrast always wins
    public void Glass_shows_only_when_wanted_supported_and_not_high_contrast(
        bool enabled, bool micaSupported, bool highContrast, bool expected)
    {
        GlassPolicy.IsActive(enabled, micaSupported, highContrast).Should().Be(expected);
        GlassPolicy.ShellBrushKey(expected).Should().Be(expected ? "GlassShellBrush" : "SurfaceBaseBrush");
    }

    // ── Palette and contrast ────────────────────────────────────────

    private static readonly ResourceDictionary Palette =
        (ResourceDictionary)Application.LoadComponent(new Uri("/LuaTools;component/Themes/Colors.xaml", UriKind.Relative));

    private static Color Get(string key) => (Color)Palette[key];

    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary><paramref name="top"/> (with its alpha) composited over an opaque <paramref name="under"/>.</summary>
    private static Color Over(Color top, Color under)
    {
        double a = top.A / 255.0;
        byte Mix(byte t, byte u) => (byte)Math.Round(a * t + (1 - a) * u);
        return Color.FromRgb(Mix(top.R, under.R), Mix(top.G, under.G), Mix(top.B, under.B));
    }

    public static TheoryData<string> EveryPalette() => new(AccentPalette.All.Select(p => p.Id));

    [Theory]
    [MemberData(nameof(EveryPalette))]
    public void The_glass_shell_is_the_palettes_window_colour_at_the_policy_alpha(string id)
    {
        var p = AccentPalette.FromId(id);
        var shell = p.SurfaceColors(new DictionaryColors(Palette))[GlassPolicy.GlassShellBrushKey];
        var window = Get(p.Neutrals.SurfaceBaseKey);

        shell.Should().Be(Color.FromArgb(GlassPolicy.ShellAlpha, window.R, window.G, window.B),
            "the accent switch repaints the glass shell with its own window colour, not Amethyst's");
        Palette.Contains(GlassPolicy.GlassShellBrushKey).Should().BeTrue("the repaint needs a brush to write to");
    }

    [Fact]
    public void The_shipped_glass_colour_matches_the_policy()
    {
        var plum = Get("Plum950Color");
        Get("GlassShellColor").Should().Be(Color.FromArgb(GlassPolicy.ShellAlpha, plum.R, plum.G, plum.B));
        ((SolidColorBrush)Palette[GlassPolicy.GlassShellBrushKey]).IsFrozen.Should().BeFalse(
            "a frozen brush silently drops out of the live accent switch");
    }

    [Theory]
    [MemberData(nameof(EveryPalette))]
    public void Shell_text_stays_AA_on_glass_over_the_worst_case_backdrop(string id)
    {
        var p = AccentPalette.FromId(id);
        var n = p.Neutrals;
        var surfaces = p.SurfaceColors(new DictionaryColors(Palette));
        byte g = GlassPolicy.WorstCaseBackdropGrey;
        var shell = Over(surfaces[GlassPolicy.GlassShellBrushKey], Color.FromRgb(g, g, g));

        // On the bare shell: nav labels, the footer's version/"signed in as" line (TextDim), the sign-in
        // wait text (TextMuted), the sign-in error (Danger), accent text.
        foreach (string key in new[] { n.TextPrimaryKey, n.TextSecondaryKey, n.TextMutedKey, n.TextDimKey,
                                       "Danger400Color", p.SoftKey })
            Contrast(Get(key), shell).Should().BeGreaterThanOrEqualTo(4.5, $"'{key}' on the glass shell ({id})");

        // Nav labels also sit on the hover/selected wash, the lightest fill in the rail.
        var hovered = Over(surfaces["SurfaceElevatedBrush"], shell);
        foreach (string key in new[] { n.TextPrimaryKey, n.TextSecondaryKey })
            Contrast(Get(key), hovered).Should().BeGreaterThanOrEqualTo(4.5, $"'{key}' on a hovered nav item ({id})");
    }

    [Fact]
    public void The_worst_case_backdrop_is_brighter_than_measured_Mica()
    {
        // #272727 is what Mica measured on a dark window here; the margin is the point of the constant.
        GlassPolicy.WorstCaseBackdropGrey.Should().BeGreaterThan(0x27);
    }

    // ── Markup ──────────────────────────────────────────────────────

    private static XDocument Load(params string[] path)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LuaToolsGui.sln")))
            dir = dir.Parent;
        string root = dir?.FullName
            ?? throw new DirectoryNotFoundException("LuaToolsGui.sln not found above the test output.");
        return XDocument.Load(Path.Combine([root, "src", "LuaToolsGui", .. path]));
    }

    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace Ui = "http://schemas.lepo.co/wpfui/2022/xaml";

    [Fact]
    public void The_settings_toggle_is_bound_two_way_and_named_for_screen_readers()
    {
        var toggle = Load("Views", "SettingsView.xaml").Descendants(Ui + "ToggleSwitch")
            .Single(t => (string?)t.Attribute("IsChecked") == "{Binding EnableGlassmorphism, Mode=TwoWay}");

        ((string?)toggle.Attribute("AutomationProperties.Name")).Should().Be("{x:Static res:Strings.Settings_Glass}");
    }

    [Fact]
    public void The_shell_grid_takes_its_background_as_a_dynamic_resource()
    {
        // ApplyGlass swaps the key with SetResourceReference; a StaticResource here would hand the grid a
        // brush object nothing could re-key, and an inline colour would be frozen out of the accent switch.
        var shell = Load("MainWindow.xaml").Descendants(Wpf + "Grid")
            .Single(e => (string?)e.Attribute(X + "Name") == "ShellRoot");

        ((string?)shell.Attribute("Background")).Should().StartWith("{DynamicResource ");
    }
}
