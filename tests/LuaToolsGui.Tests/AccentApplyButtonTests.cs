using System.IO;
using AwesomeAssertions;
using LuaToolsGui.Services;
using LuaToolsGui.Themes;
using LuaToolsGui.ViewModels;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Covers the staged accent flow: picking a colour changes nothing, Apply changes everything.
///
/// <para>
/// Selecting used to apply and persist on the spot, so brushing against the dropdown was enough to
/// repaint the app with no way back but remembering the old choice. The gap between "what is picked" and
/// "what is painted" is now explicit, and it is what drives the Apply button — these tests pin both ends
/// of that gap, including the one that is invisible in a screenshot: that settings.json is not written
/// until the user confirms.
/// </para>
/// </summary>
public class AccentApplyButtonTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "luatools-tests", Guid.NewGuid().ToString("N"));

    public AccentApplyButtonTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private SettingsService Settings() => new(_dir);

    /// <summary>A view-model over a throwaway settings directory, plus the palettes it was asked to
    /// paint. The paint action is captured rather than performed — a live repaint needs an Application,
    /// and what matters here is WHETHER it is called, not what it draws (see ThemeLiveSwitchTests).</summary>
    private (SettingsViewModel Vm, List<AccentPalette> Painted, SettingsService Store) Build()
    {
        var store = Settings();
        var vm = new SettingsViewModel(store, new AuthService(), new SteamService(store), new HubcapService());
        var painted = new List<AccentPalette>();
        vm.ApplyAccentPalette = painted.Add;
        return (vm, painted, store);
    }

    private static void Pick(SettingsViewModel vm, AccentPalette palette) =>
        vm.SelectedAccent = vm.AccentOptions.First(o => o.Id == palette.Id);

    // ── Nothing happens until Apply ───────────────────────────────────────────

    [Fact]
    public void Picking_a_colour_does_not_paint_it()
    {
        var (vm, painted, _) = Build();

        Pick(vm, AccentPalette.Green);

        painted.Should().BeEmpty("selecting stages a choice, it does not apply one");
        vm.AppliedAccentId.Should().Be(AccentPalette.Amethyst.Id);
    }

    [Fact]
    public void Picking_a_colour_does_not_persist_it()
    {
        var (vm, _, _) = Build();

        Pick(vm, AccentPalette.Red);

        // Read through a SECOND SettingsService over the same directory — the closest thing to relaunching.
        Settings().AccentColor.Should().NotBe(AccentPalette.Red.Id);
    }

    [Fact]
    public void An_unconfirmed_pick_leaves_the_previous_colour_active()
    {
        // The scenario the button exists for: change your mind, walk away, and the app is still the colour
        // it was. Navigating between pages does not touch the view-model, so the staged pick simply sits
        // there while the painted accent stays put.
        var (vm, painted, _) = Build();
        Pick(vm, AccentPalette.Green);
        vm.ApplyAccentChangeCommand.Execute(null);
        painted.Clear();

        Pick(vm, AccentPalette.Red);   // considered, never confirmed

        painted.Should().BeEmpty();
        vm.AppliedAccentId.Should().Be(AccentPalette.Green.Id);
        Settings().AccentColor.Should().Be(AccentPalette.Green.Id);
    }

    // ── Apply ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Apply_paints_and_persists_the_staged_colour()
    {
        var (vm, painted, _) = Build();
        Pick(vm, AccentPalette.Green);

        vm.ApplyAccentChangeCommand.Execute(null);

        painted.Should().ContainSingle().Which.Should().Be(AccentPalette.Green);
        vm.AppliedAccentId.Should().Be(AccentPalette.Green.Id);
        Settings().AccentColor.Should().Be(AccentPalette.Green.Id);
    }

    [Fact]
    public void An_applied_colour_survives_a_restart()
    {
        var (vm, _, _) = Build();
        Pick(vm, AccentPalette.Red);
        vm.ApplyAccentChangeCommand.Execute(null);

        // Rebuild the whole view-model over the same directory, as a relaunch would.
        var (reopened, _, _) = Build();

        reopened.SelectedAccent.Id.Should().Be(AccentPalette.Red.Id);
        reopened.AppliedAccentId.Should().Be(AccentPalette.Red.Id);
        reopened.HasPendingAccentChange.Should().BeFalse("what is saved is what is painted");
    }

    // ── Button state ──────────────────────────────────────────────────────────

    [Fact]
    public void Apply_is_disabled_when_there_is_nothing_to_apply()
    {
        var (vm, _, _) = Build();

        vm.HasPendingAccentChange.Should().BeFalse();
        vm.ApplyAccentChangeCommand.CanExecute(null).Should().BeFalse(
            "a button that does nothing must not look like it will");
    }

    [Fact]
    public void Apply_enables_as_soon_as_the_pick_differs()
    {
        var (vm, _, _) = Build();

        Pick(vm, AccentPalette.Green);

        vm.HasPendingAccentChange.Should().BeTrue();
        vm.ApplyAccentChangeCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void Apply_disables_itself_again_once_applied()
    {
        var (vm, _, _) = Build();
        Pick(vm, AccentPalette.Green);

        vm.ApplyAccentChangeCommand.Execute(null);

        vm.HasPendingAccentChange.Should().BeFalse();
        vm.ApplyAccentChangeCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void Picking_back_the_active_colour_is_not_a_pending_change()
    {
        // Green, then back to Amethyst without applying: there is nothing to do, so the button must go
        // quiet again rather than offering to "apply" the colour already on screen.
        var (vm, _, _) = Build();
        Pick(vm, AccentPalette.Green);
        vm.HasPendingAccentChange.Should().BeTrue();

        Pick(vm, AccentPalette.Amethyst);

        vm.HasPendingAccentChange.Should().BeFalse();
    }

    [Fact]
    public void The_command_reports_its_state_change_so_the_button_redraws()
    {
        // CanExecute alone is not enough: WPF only re-queries when CanExecuteChanged fires. Without the
        // notification the button stays greyed out after a pick and the feature looks broken.
        var (vm, _, _) = Build();
        int notifications = 0;
        vm.ApplyAccentChangeCommand.CanExecuteChanged += (_, _) => notifications++;

        Pick(vm, AccentPalette.Green);

        notifications.Should().BeGreaterThan(0);
    }

    // ── Applying twice ────────────────────────────────────────────────────────

    [Fact]
    public void A_second_apply_is_a_second_paint()
    {
        // Guards the view-model half of the freeze bug: whatever the renderer does, the command must keep
        // handing over each new palette rather than going quiet after the first.
        var (vm, painted, _) = Build();

        Pick(vm, AccentPalette.Green);
        vm.ApplyAccentChangeCommand.Execute(null);
        Pick(vm, AccentPalette.Red);
        vm.ApplyAccentChangeCommand.Execute(null);

        painted.Should().Equal(AccentPalette.Green, AccentPalette.Red);
        Settings().AccentColor.Should().Be(AccentPalette.Red.Id);
    }
}
