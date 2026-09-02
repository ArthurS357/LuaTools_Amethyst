using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using AwesomeAssertions;
using LuaToolsGui.Themes;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// A real <see cref="Application"/> on a private STA thread, with the same three merged dictionaries
/// App.xaml declares.
///
/// <para>
/// This is what the older theme tests were missing, and the reason a whole feature shipped inert. They
/// loaded Themes/Colors.xaml as a standalone dictionary — where every brush is mutable — and proved the
/// repaint worked. Attaching that same dictionary to an Application is what makes WPF freeze the brushes,
/// so the tests passed while the app could not change colour at all. Nothing short of a live Application
/// reproduces that.
/// </para>
///
/// <para>
/// One instance per test run: WPF allows a single Application per AppDomain, and every object it owns has
/// thread affinity, so all access is marshalled onto the one STA thread. xUnit does not parallelise within
/// a class, which is why the facts below can share it.
/// </para>
/// </summary>
public sealed class ThemeHost : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Thread _thread;

    public ThemeHost()
    {
        var ready = new ManualResetEventSlim();
        Dispatcher? captured = null;

        _thread = new Thread(() =>
        {
            var app = new Application
            {
                // ShutdownMode matters: without it, Application would try to exit once no window remains.
                ShutdownMode = ShutdownMode.OnExplicitShutdown,
            };
            var resources = new ResourceDictionary();
            resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary
            {
                Theme = Wpf.Ui.Appearance.ApplicationTheme.Dark,
            });
            resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
            resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
                new Uri("/LuaTools;component/Themes/Colors.xaml", UriKind.Relative)));
            app.Resources = resources;

            captured = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        { IsBackground = true };

        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(30));
        _dispatcher = captured ?? throw new InvalidOperationException("theme host never started");
    }

    public T On<T>(Func<Application, T> work) => _dispatcher.Invoke(() => work(Application.Current));

    public void On(Action<Application> work) => _dispatcher.Invoke(() => work(Application.Current));

    /// <summary>Colour of a brush resource, resolved the way a view resolves it.</summary>
    public Color BrushColor(string key) =>
        On(app => ((SolidColorBrush)app.TryFindResource(key)).Color);

    public bool IsFrozen(string key) =>
        On(app => app.TryFindResource(key) is SolidColorBrush { IsFrozen: true });

    public void Apply(AccentPalette palette) => On(_ => LuaToolsGui.App.ApplyAccentPalette(palette));

    public void Dispose() => _dispatcher.InvokeShutdown();
}

/// <summary>
/// Covers the accent switch against a live Application — the only arrangement in which its two real
/// failure modes exist.
/// </summary>
/// <summary>
/// Shares the one <see cref="ThemeHost"/> across every theme test class.
///
/// <para>
/// It has to be a COLLECTION fixture, not a class fixture: WPF allows a single
/// <see cref="Application"/> per AppDomain, so a second test class taking
/// <c>IClassFixture&lt;ThemeHost&gt;</c> builds a second Application and aborts the whole run with
/// "cannot create more than one instance of System.Windows.Application". Any new class that needs a live
/// Application joins this collection instead of declaring its own fixture.
/// </para>
/// </summary>
[CollectionDefinition(ThemeHostCollection.Name)]
public sealed class ThemeHostCollection : ICollectionFixture<ThemeHost>
{
    public const string Name = "theme host";
}

[Collection(ThemeHostCollection.Name)]
public class ThemeLiveSwitchTests(ThemeHost host)
{
    /// <summary>Tokens the palette repaints, one from each family the switch has to reach.</summary>
    public static TheoryData<string> RepaintedTokens() =>
    [
        "SurfaceBaseBrush", "SurfaceRaisedBrush", "SurfaceInsetBrush",
        "TextPrimaryBrush", "TextSecondaryBrush", "TextDimBrush",
        "SurfaceCardBrush", "BorderDefaultBrush", "ScrimBrush",
        "AccentBrush", "AccentSoftBrush", "AccentTintBrush",
        "ApplicationBackgroundBrush", "CardBackgroundFillColorDefaultBrush", "CardBackground",
        "ControlFillColorDefaultBrush", "LayerFillColorDefaultBrush",
    ];

    [Theory]
    [MemberData(nameof(RepaintedTokens))]
    public void No_repainted_brush_is_frozen_once_the_application_owns_the_dictionary(string key)
    {
        // THE bug. WPF freezes a Freezable when its dictionary is attached to Application.Resources, and a
        // frozen brush is skipped by the repaint — silently, because nothing throws and the build is clean.
        // Colors.xaml keeps these mutable by declaring each Colour through {DynamicResource}, which makes
        // the Freezable non-freezable. Anyone "tidying" that back to a literal reintroduces a dead feature.
        host.IsFrozen(key).Should().BeFalse(
            $"'{key}' must stay mutable or the accent switch cannot repaint it");
    }

    [Fact]
    public void Status_colours_stay_frozen_because_nothing_repaints_them()
    {
        // The flip side: status hues do not follow the accent, so they are left as ordinary literals. If
        // this ever starts failing, someone converted more of the palette than the switch actually drives.
        host.IsFrozen("SuccessBrush").Should().BeTrue();
        host.IsFrozen("DangerBrush").Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(RepaintedTokens))]
    public void Applying_a_palette_repaints_the_token(string key)
    {
        host.Apply(AccentPalette.Amethyst);
        var amethyst = host.BrushColor(key);

        host.Apply(AccentPalette.Green);
        var green = host.BrushColor(key);

        green.Should().NotBe(amethyst, $"'{key}' did not follow the palette");

        host.Apply(AccentPalette.Amethyst);
        host.BrushColor(key).Should().Be(amethyst, $"'{key}' did not come back");
    }

    [Theory]
    [MemberData(nameof(RepaintedTokens))]
    public void The_second_switch_works_as_well_as_the_first(string key)
    {
        // The other bug, and the nastier one: PromoteSurfaceOverrides used to copy brushes into
        // Application.Resources, and assigning a Freezable there FREEZES it. The first switch worked, and
        // every switch after it silently did nothing to those surfaces. Two switches in a row is the
        // smallest test that catches it.
        host.Apply(AccentPalette.Amethyst);
        host.Apply(AccentPalette.Green);
        var green = host.BrushColor(key);

        host.Apply(AccentPalette.Red);
        var red = host.BrushColor(key);

        red.Should().NotBe(green, $"'{key}' froze after the first switch");

        host.Apply(AccentPalette.Green);
        host.BrushColor(key).Should().Be(green, $"'{key}' stopped tracking the palette");
    }

    [Fact]
    public void The_window_background_follows_the_palette()
    {
        // MainWindow paints its root Grid with SurfaceBaseBrush, so this token IS the window colour. It is
        // called out separately because "the app still looks purple" is what the user reports, and this is
        // the single brush that claim is really about.
        host.Apply(AccentPalette.Green);
        host.BrushColor("SurfaceBaseBrush").Should().Be(
            host.On(app => (Color)app.TryFindResource(AccentPalette.Green.Neutrals.SurfaceBaseKey)));

        host.Apply(AccentPalette.Red);
        host.BrushColor("SurfaceBaseBrush").Should().Be(
            host.On(app => (Color)app.TryFindResource(AccentPalette.Red.Neutrals.SurfaceBaseKey)));

        host.Apply(AccentPalette.Amethyst);
    }

    [Fact]
    public void The_wpf_ui_accent_slot_follows_the_palette_too()
    {
        // The templated half of the UI: nav pill, Primary buttons, toggles. It comes from WPF-UI's own
        // accent manager, not from our brushes, so it is a separate thing that can break on its own.
        host.Apply(AccentPalette.Green);
        host.On(app => app.TryFindResource("SystemAccentColorPrimary"))
            .Should().Be(host.On(app => app.TryFindResource(AccentPalette.Green.PrimaryKey)));

        host.Apply(AccentPalette.Amethyst);
        host.On(app => app.TryFindResource("SystemAccentColorPrimary"))
            .Should().Be(host.On(app => app.TryFindResource(AccentPalette.Amethyst.PrimaryKey)));
    }
}
