using AwesomeAssertions;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>A tray icon that records what was asked of it instead of talking to the shell.</summary>
internal sealed class FakeTrayIcon : ITrayIcon
{
    public bool Visible { get; set; }
    public int DisposeCalls { get; private set; }
    public (string Title, string Message, bool Error)? LastBalloon { get; private set; }

    public event EventHandler? OpenRequested;
    public event EventHandler? ExitRequested;

    /// <summary>Tray double-click, or the context menu's "Open".</summary>
    public void RaiseOpen() => OpenRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>The context menu's "Exit".</summary>
    public void RaiseExit() => ExitRequested?.Invoke(this, EventArgs.Empty);

    public void ShowBalloon(string title, string message, bool error) => LastBalloon = (title, message, error);

    public void Dispose() => DisposeCalls++;
}

/// <summary>
/// A window that behaves like the real one on the point that matters: <see cref="CloseWindow"/> re-enters
/// the closing handler, exactly as WPF does when <c>Window.Close()</c> raises <c>Closing</c>. Getting that
/// wrong in the fake would hide the only interesting thing about the Exit path — that it goes back through
/// the same decision rather than around it.
/// </summary>
internal sealed class FakeTrayWindow : ITrayWindow
{
    public int Hides { get; private set; }
    public int Restores { get; private set; }
    public int Shutdowns { get; private set; }

    /// <summary>Set by the test to the service's close handler, standing in for WPF's Closing event.</summary>
    public Func<WindowCloseAction>? OnClose { get; set; }

    public void HideWindow() => Hides++;
    public void RestoreWindow() => Restores++;
    public void CloseWindow() => OnClose?.Invoke();
    public void Shutdown() => Shutdowns++;
}

/// <summary>
/// Covers the rule that decides whether the window's X button quits LuaTools or leaves it running in the
/// notification area.
///
/// <para>
/// It has two ways to be wrong and both are bad. Quitting when the user expected the tray takes the local
/// HTTP bridge down with it, so the Steam store-page integration stops answering with no visible cause.
/// Refusing to quit when the user picked "Exit" is an app that cannot be closed. Before this the rule was
/// three conditions inlined in a <c>Closing</c> handler on <c>MainWindow</c>, reachable only with a real
/// window — which is to say untested.
/// </para>
/// </summary>
public class TrayServiceTests
{
    private readonly FakeTrayIcon _icon = new();
    private readonly FakeTrayWindow _window = new();

    /// <summary>Wire a service whose close-to-tray answer the test controls.</summary>
    private TrayService Build(bool closeToTray)
    {
        var service = new TrayService(_icon, _window, () => closeToTray);
        _window.OnClose = service.HandleCloseRequest;
        return service;
    }

    // ── Closing the window ────────────────────────────────────────────────────

    [Fact]
    public void Closing_the_window_hides_it_to_the_tray_rather_than_quitting()
    {
        var tray = Build(closeToTray: true);

        tray.HandleCloseRequest().Should().Be(WindowCloseAction.HideToTray);

        _window.Hides.Should().Be(1);
        _window.Shutdowns.Should().Be(0, "the app stays running — that is the whole point");
        _icon.Visible.Should().BeTrue("hiding the window with no tray icon leaves no way back");
        _icon.DisposeCalls.Should().Be(0);
    }

    [Fact]
    public void Closing_quits_when_nothing_is_keeping_the_app_resident()
    {
        // The user turned "Minimize to tray" off and this is not a --tray-locked session: X means quit.
        var tray = Build(closeToTray: false);

        tray.HandleCloseRequest().Should().Be(WindowCloseAction.Shutdown);

        _window.Shutdowns.Should().Be(1);
        _window.Hides.Should().Be(0);
    }

    [Fact]
    public void The_setting_is_read_at_each_close_not_captured_once()
    {
        // Both inputs move while the app runs: the user can toggle the setting on the Settings page, and a
        // --tray-locked relaunch flips Program.SessionTrayLock by signal. A value captured in the
        // constructor would answer with whatever was true at startup.
        bool closeToTray = true;
        var tray = new TrayService(_icon, _window, () => closeToTray);

        tray.HandleCloseRequest().Should().Be(WindowCloseAction.HideToTray);

        closeToTray = false;
        tray.HandleCloseRequest().Should().Be(WindowCloseAction.Shutdown);
    }

    // ── Exit ──────────────────────────────────────────────────────────────────

    [Fact]
    public void The_trays_exit_item_quits_for_real_even_with_close_to_tray_on()
    {
        var tray = Build(closeToTray: true);

        _icon.RaiseExit();

        tray.ExitRequested.Should().BeTrue();
        _window.Shutdowns.Should().Be(1, "otherwise the app has no way to be closed at all");
        _window.Hides.Should().Be(0);
    }

    [Fact]
    public void Exit_disposes_the_tray_icon()
    {
        // An undisposed NotifyIcon leaves a dead entry in the notification area until the user hovers over
        // it — the app is gone and the icon is still there.
        var tray = Build(closeToTray: true);

        _icon.RaiseExit();

        _icon.DisposeCalls.Should().Be(1);
        tray.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public void A_shutdown_close_disposes_the_icon_too()
    {
        var tray = Build(closeToTray: false);

        tray.HandleCloseRequest();

        _icon.DisposeCalls.Should().Be(1);
    }

    [Fact]
    public void Disposing_twice_does_not_dispose_the_icon_twice()
    {
        var tray = Build(closeToTray: false);

        tray.HandleCloseRequest();
        tray.Dispose();

        _icon.DisposeCalls.Should().Be(1);
    }

    // ── Restoring ─────────────────────────────────────────────────────────────

    [Fact]
    public void Double_clicking_the_tray_icon_restores_the_window()
    {
        var tray = Build(closeToTray: true);
        tray.HandleCloseRequest();

        _icon.RaiseOpen();

        _window.Restores.Should().Be(1);
        _icon.Visible.Should().BeFalse("the icon belongs in the tray only while the window is away");
    }

    [Fact]
    public void The_open_menu_item_and_a_double_click_do_the_same_thing()
    {
        // Both are wired to the same event on ITrayIcon, which is the point: one restore path, not two.
        var tray = Build(closeToTray: true);
        tray.HandleCloseRequest();

        tray.Restore();

        _window.Restores.Should().Be(1);
        _icon.Visible.Should().BeFalse();
    }

    [Fact]
    public void Restoring_does_not_count_as_asking_to_exit()
    {
        var tray = Build(closeToTray: true);

        tray.HandleCloseRequest();
        _icon.RaiseOpen();
        tray.HandleCloseRequest();

        tray.ExitRequested.Should().BeFalse();
        _window.Shutdowns.Should().Be(0, "a restore-then-close cycle must keep hiding, not start quitting");
    }

    // ── Headless launch ───────────────────────────────────────────────────────

    [Fact]
    public void A_silent_launch_shows_the_icon_without_touching_the_window()
    {
        var tray = Build(closeToTray: true);

        tray.ShowIconOnly();

        _icon.Visible.Should().BeTrue();
        _window.Restores.Should().Be(0, "a headless install must never surface the window");
        _window.Hides.Should().Be(0);
    }

    [Fact]
    public void A_balloon_makes_the_icon_visible_first()
    {
        // A balloon has nowhere to come from if the icon is hidden — the report would be silently dropped.
        var tray = Build(closeToTray: true);

        tray.ShowBalloon("LuaTools Amethyst", "Installed.", error: false);

        _icon.Visible.Should().BeTrue();
        _icon.LastBalloon.Should().Be(("LuaTools Amethyst", "Installed.", false));
    }

    // ── The rule on its own ───────────────────────────────────────────────────

    [Theory]
    [InlineData(false, true, WindowCloseAction.HideToTray)]  // ordinary close, close-to-tray on
    [InlineData(false, false, WindowCloseAction.Shutdown)]   // ordinary close, nothing keeping it alive
    [InlineData(true, true, WindowCloseAction.Shutdown)]     // tray "Exit" overrides close-to-tray
    [InlineData(true, false, WindowCloseAction.Shutdown)]    // tray "Exit", close-to-tray already off
    public void The_decision_table_is_exhaustive(
        bool exitRequested, bool closeToTrayEnabled, WindowCloseAction expected) =>
        TrayService.Decide(exitRequested, closeToTrayEnabled).Should().Be(expected);
}
