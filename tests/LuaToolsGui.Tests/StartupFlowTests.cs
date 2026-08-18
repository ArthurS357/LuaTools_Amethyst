using System.IO;
using AwesomeAssertions;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>A Steam process that behaves however the test needs it to.</summary>
internal sealed class FakeSteamProcess : ISteamProcess
{
    /// <summary>Whether Kill actually works. False models a process this user cannot terminate.</summary>
    public bool CanBeKilled { get; init; } = true;

    public bool HasExited { get; private set; }
    public int KillRequests { get; private set; }
    public int Waits { get; private set; }

    /// <summary>Drive the process to exited, as a real one does when it honours the shutdown request.</summary>
    public void Exit() => HasExited = true;

    public bool WaitForExit(TimeSpan timeout)
    {
        Waits++;
        return HasExited;
    }

    public void Kill()
    {
        KillRequests++;
        if (!CanBeKilled) throw new InvalidOperationException("access denied");
        HasExited = true;
    }
}

/// <summary>Records that the client was asked to shut down, and optionally makes it obey.</summary>
internal sealed class FakeShutdownRequest(params FakeSteamProcess[] obeying)
{
    public int Calls { get; private set; }

    /// <summary>Set to make the request throw — Steam not where the app thinks it is.</summary>
    public bool Throws { get; init; }

    public void Invoke()
    {
        Calls++;
        if (Throws) throw new InvalidOperationException("steam.exe missing");
        foreach (var p in obeying) p.Exit();
    }
}

/// <summary>
/// Covers how the app stops Steam on launch, and whether it says anything to the user while doing it.
///
/// <para>
/// Both halves used to be untestable and were therefore untested: stopping Steam meant
/// <c>Process.Kill(entireProcessTree: true)</c> against the real client, and the launch sequence was a
/// branch buried in <c>StartupAsync</c>. The policy is now separate from the plumbing, which is the only
/// reason "asks before it forces" is something a build can check rather than something you find out by
/// losing a Steam session.
/// </para>
/// </summary>
public class SteamShutdownTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(50);

    [Fact]
    public void Nothing_running_is_not_a_failure()
    {
        var request = new FakeShutdownRequest();

        SteamShutdown.Stop([], request.Invoke, allowKill: true, Grace)
            .Should().Be(SteamStopOutcome.NotRunning);
        request.Calls.Should().Be(0, "there was nobody to ask");
    }

    [Fact]
    public void An_already_exited_process_counts_as_nothing_running()
    {
        var gone = new FakeSteamProcess();
        gone.Exit();

        SteamShutdown.Stop([gone], new FakeShutdownRequest().Invoke, allowKill: true, Grace)
            .Should().Be(SteamStopOutcome.NotRunning);
    }

    [Fact]
    public void Steam_is_asked_before_it_is_forced()
    {
        // The behaviour change this exists for. Terminating the client denies it a clean shutdown, which
        // is what produces the "Steam did not shut down correctly" scan on the next launch.
        var steam = new FakeSteamProcess();
        var request = new FakeShutdownRequest(steam);

        var outcome = SteamShutdown.Stop([steam], request.Invoke, allowKill: true, Grace);

        outcome.Should().Be(SteamStopOutcome.ClosedGracefully);
        request.Calls.Should().Be(1);
        steam.KillRequests.Should().Be(0, "it exited when asked, so there was nothing to force");
    }

    [Fact]
    public void The_client_is_asked_once_no_matter_how_many_processes_it_has()
    {
        // `-shutdown` addresses Steam as a product and brings its helpers down with it. Issuing it per
        // process would launch duplicate helpers instead of closing anything.
        var a = new FakeSteamProcess();
        var b = new FakeSteamProcess();
        var c = new FakeSteamProcess();
        var request = new FakeShutdownRequest(a, b, c);

        SteamShutdown.Stop([a, b, c], request.Invoke, allowKill: false, Grace)
            .Should().Be(SteamStopOutcome.ClosedGracefully);

        request.Calls.Should().Be(1);
    }

    [Fact]
    public void A_process_that_ignores_the_request_is_killed_when_that_is_allowed()
    {
        var stubborn = new FakeSteamProcess();
        var request = new FakeShutdownRequest();   // asks, nobody obeys

        var outcome = SteamShutdown.Stop([stubborn], request.Invoke, allowKill: true, Grace);

        outcome.Should().Be(SteamStopOutcome.Killed);
        request.Calls.Should().Be(1, "asking still comes first");
        stubborn.KillRequests.Should().Be(1);
    }

    [Fact]
    public void Without_permission_to_kill_a_stubborn_process_is_reported_not_forced()
    {
        // This is what lets the startup flow put the choice to the user instead of taking it. A caller
        // told "StillRunning" must not go on to rewrite files Steam holds open.
        var stubborn = new FakeSteamProcess();

        var outcome = SteamShutdown.Stop(
            [stubborn], new FakeShutdownRequest().Invoke, allowKill: false, Grace);

        outcome.Should().Be(SteamStopOutcome.StillRunning);
        stubborn.KillRequests.Should().Be(0);
    }

    [Fact]
    public void A_kill_that_fails_is_reported_as_still_running_not_as_success()
    {
        // Reporting the attempt rather than the result would send the caller on to edit files that are
        // still locked, and the failure would surface as a corrupt install instead of a refusal.
        var unkillable = new FakeSteamProcess { CanBeKilled = false };

        SteamShutdown.Stop([unkillable], new FakeShutdownRequest().Invoke, allowKill: true, Grace)
            .Should().Be(SteamStopOutcome.StillRunning);
    }

    [Fact]
    public void A_shutdown_request_that_throws_still_escalates()
    {
        // Steam not where the app thinks it is. The request is best-effort; the escalation is what
        // guarantees the caller's precondition.
        var steam = new FakeSteamProcess();
        var broken = new FakeShutdownRequest { Throws = true };

        SteamShutdown.Stop([steam], broken.Invoke, allowKill: true, Grace)
            .Should().Be(SteamStopOutcome.Killed);
        steam.KillRequests.Should().Be(1);
    }

    [Fact]
    public void A_mixed_group_is_only_graceful_if_all_of_them_went()
    {
        var obedient = new FakeSteamProcess();
        var stubborn = new FakeSteamProcess();
        var request = new FakeShutdownRequest(obedient);   // only one honours it

        SteamShutdown.Stop([obedient, stubborn], request.Invoke, allowKill: true, Grace)
            .Should().Be(SteamStopOutcome.Killed);
        obedient.KillRequests.Should().Be(0, "it had already gone");
        stubborn.KillRequests.Should().Be(1);
    }

    [Fact]
    public void A_null_list_is_rejected_rather_than_silently_treated_as_no_steam()
    {
        // "NotRunning" from a null would tell the caller Steam is down when nobody looked.
        Action act = () => SteamShutdown.Stop(null!, new FakeShutdownRequest().Invoke, true, Grace);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void A_null_shutdown_request_is_rejected()
    {
        Action act = () => SteamShutdown.Stop([new FakeSteamProcess()], null!, true, Grace);

        act.Should().Throw<ArgumentNullException>();
    }
}

/// <summary>
/// Covers the launch sequence decision: close Steam, show setup, offer Steam back.
/// </summary>
public class StartupPlanTests
{
    [Fact]
    public void A_returning_user_with_steam_up_just_gets_steam_closed_and_offered_back()
    {
        // The headline case. Everything installed, been through setup — the whole launch should be
        // "Steam went down, do you want it back", with no prompts in between.
        var plan = StartupPlan.Decide(steamRunning: true, setupAlreadyDone: true, toolsInstalled: true);

        plan.Should().Be(new StartupPlan(CloseSteam: true, ShowSetup: false, OfferReopen: true));
    }

    [Fact]
    public void A_first_run_with_steam_up_closes_it_and_shows_setup()
    {
        var plan = StartupPlan.Decide(steamRunning: true, setupAlreadyDone: false, toolsInstalled: false);

        plan.CloseSteam.Should().BeTrue();
        plan.ShowSetup.Should().BeTrue();
    }

    [Fact]
    public void Setup_never_pairs_with_an_offer_to_reopen()
    {
        // The setup flow launches Steam itself once its installs finish. Offering as well means two things
        // racing to start Steam, which is how you get the "Steam is already running" dialog.
        var plan = StartupPlan.Decide(steamRunning: true, setupAlreadyDone: false, toolsInstalled: false);

        plan.OfferReopen.Should().BeFalse();
    }

    [Fact]
    public void Setup_is_skipped_for_someone_whose_tools_are_already_installed()
    {
        // A reinstall, a second machine, a dev box: never been through setup here, but there is nothing to
        // set up. Showing the installer would be the nag this replaces.
        var plan = StartupPlan.Decide(steamRunning: true, setupAlreadyDone: false, toolsInstalled: true);

        plan.ShowSetup.Should().BeFalse();
        plan.OfferReopen.Should().BeTrue();
    }

    [Fact]
    public void Steam_that_was_not_running_is_never_offered_back()
    {
        // Starting a Steam the user had deliberately quit is the app deciding something it was not asked
        // to decide.
        var plan = StartupPlan.Decide(steamRunning: false, setupAlreadyDone: true, toolsInstalled: true);

        plan.CloseSteam.Should().BeFalse();
        plan.OfferReopen.Should().BeFalse();
    }

    [Fact]
    public void A_fully_configured_launch_with_no_steam_says_nothing_at_all()
    {
        var plan = StartupPlan.Decide(steamRunning: false, setupAlreadyDone: true, toolsInstalled: true);

        plan.IsSilent.Should().BeTrue("the app should come up quietly rather than announce that it did nothing");
    }

    [Fact]
    public void A_first_run_without_steam_still_shows_setup()
    {
        var plan = StartupPlan.Decide(steamRunning: false, setupAlreadyDone: false, toolsInstalled: false);

        plan.ShowSetup.Should().BeTrue();
        plan.IsSilent.Should().BeFalse();
        plan.CloseSteam.Should().BeFalse();
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void Closing_steam_is_decided_by_steam_alone(bool setupDone, bool tools)
    {
        // Whether Steam gets stopped must not depend on setup state: every install path rewrites files
        // Steam holds open, so the stop is unconditional whenever Steam is up.
        StartupPlan.Decide(steamRunning: true, setupDone, tools).CloseSteam.Should().BeTrue();
        StartupPlan.Decide(steamRunning: false, setupDone, tools).CloseSteam.Should().BeFalse();
    }
}

/// <summary>
/// Covers Steam path resolution now that detection is memoised.
///
/// <para>
/// The optimisation is invisible — the same answer, computed less often — so the only thing worth testing
/// is the way it could go wrong: a cache that shadows the user override, or one that pins a stale path.
/// </para>
/// </summary>
public class SteamPathResolutionTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "luatools-tests", Guid.NewGuid().ToString("N"));

    public SteamPathResolutionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    /// <summary>A directory that looks enough like a Steam install to be accepted.</summary>
    private string FakeSteamInstall(string name)
    {
        string path = Path.Combine(_dir, name);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "steam.exe"), "");
        return path;
    }

    [Fact]
    public void An_override_is_used_immediately_and_is_never_shadowed_by_the_cache()
    {
        // The regression the memo could introduce: caching detection and then answering path reads from
        // the cache even when the user has pointed the app somewhere else.
        var settings = new SettingsService(_dir);
        var steam = new SteamService(settings);

        _ = steam.EffectivePath;   // warm whatever detection finds first

        string chosen = FakeSteamInstall("steam-a");
        settings.SteamPathOverride = chosen;

        steam.EffectivePath.Should().Be(chosen);
        steam.IsOverridden.Should().BeTrue();
        steam.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Changing_the_override_is_picked_up_without_a_restart()
    {
        var settings = new SettingsService(_dir);
        var steam = new SteamService(settings);

        string first = FakeSteamInstall("steam-first");
        settings.SteamPathOverride = first;
        steam.EffectivePath.Should().Be(first);

        string second = FakeSteamInstall("steam-second");
        settings.SteamPathOverride = second;

        steam.EffectivePath.Should().Be(second, "the override is read every time, not remembered");
    }

    [Fact]
    public void Clearing_the_override_falls_back_to_detection()
    {
        var settings = new SettingsService(_dir);
        var steam = new SteamService(settings);
        settings.SteamPathOverride = FakeSteamInstall("steam-temp");

        settings.SteamPathOverride = null;

        // Whatever detection returns on this machine, it must equal the auto-detected value rather than
        // the override that was just removed.
        steam.EffectivePath.Should().Be(steam.AutoDetectedPath);
        steam.IsOverridden.Should().BeFalse();
    }

    [Fact]
    public void Repeated_reads_agree_with_each_other()
    {
        // The point of the memo: the answer must not depend on how many times it was asked.
        var steam = new SteamService(new SettingsService(_dir));

        string? first = steam.AutoDetectedPath;

        for (int i = 0; i < 50; i++)
            steam.AutoDetectedPath.Should().Be(first);
    }

    [Fact]
    public void The_derived_paths_hang_off_the_effective_path()
    {
        var settings = new SettingsService(_dir);
        var steam = new SteamService(settings);
        string install = FakeSteamInstall("steam-derived");
        settings.SteamPathOverride = install;

        steam.StPlugInDir.Should().Be(Path.Combine(install, "config", "stplug-in"));
        steam.DepotCacheDir.Should().Be(Path.Combine(install, "config", "depotcache"));
    }
}
