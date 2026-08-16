using System.Windows;
using System.Windows.Threading;
using LuaToolsGui.Models;
using LuaToolsGui.Services;
using LuaToolsGui.ViewModels;
using LuaToolsGui.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LuaToolsGui;

public partial class App : Application
{
    private readonly IHost _host;

    // True when the app was cold-started solely to run a silent install AND MinimizeToTray is off —
    // means we auto-exit after the balloon so we don't leave a ghost tray icon behind.
    private bool _exitAfterSilentInstall;

    /// <summary>Where an unhandled exception is recorded, so a crash leaves evidence behind.</summary>
    private static readonly string CrashLogPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LuaToolsGui", "crash.log");

    /// <summary>
    /// Catch-all for exceptions nothing else handles. There was previously NO global handler of any kind:
    /// a throw on the UI thread, a faulted fire-and-forget task, or anything escaping the (async void)
    /// <see cref="OnStartup"/> took the process down with no message, no log and no trace — leaving a user
    /// with an app that simply vanishes on launch and a maintainer with nothing to go on.
    ///
    /// <para>
    /// Dispatcher exceptions are marked handled so a failure in one interaction doesn't kill the whole app;
    /// unobserved task exceptions likewise. Domain-level exceptions can't be cancelled — the process is
    /// already going down — but they are still logged on the way out.
    /// </para>
    /// </summary>
    private void WireGlobalExceptionHandling()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            LogCrash("Dispatcher", e.Exception);
            e.Handled = true; // keep the app alive; the failed action is already lost
            ShowCrashNotice(e.Exception);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogCrash("AppDomain", e.ExceptionObject as Exception);

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            // The codebase fires a lot of `_ = SomethingAsync()`. Without this, a faulted one is silent.
            LogCrash("UnobservedTask", e.Exception);
            e.SetObserved();
        };
    }

    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            // Sanitize before writing. Some exception messages here are built from raw HTTP bodies —
            // AuthService throws $"Token exchange failed ({code}): {body}", and that body is Supabase's
            // token response, so an unsanitized crash log would leave live access/refresh tokens in a
            // plain-text file in the user's roaming profile. See LogSanitizer.
            string detail = LogSanitizer.Sanitize(ex);

            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(CrashLogPath)!);
            System.IO.File.AppendAllText(CrashLogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}: {detail}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { /* the crash logger must never itself throw */ }
    }

    /// <summary>Tell the user something went wrong and where the details are. Best-effort — if the UI is
    /// too far gone to show a toast, the log write above already happened.</summary>
    private void ShowCrashNotice(Exception ex)
    {
        try
        {
            // Sanitized for the same reason as the log — this text is shown on screen and can be
            // screenshotted into a bug report.
            var toast = _host.Services.GetRequiredService<ToastService>();
            toast.Show("LuaTools", $"{ex.GetType().Name}: {LogSanitizer.Sanitize(ex.Message)}", error: true);
        }
        catch { /* no UI available */ }
    }

    public App()
    {
        WireGlobalExceptionHandling();

        _host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<SettingsService>();
                services.AddSingleton<CacheService>();
                services.AddSingleton<SteamService>();
                services.AddSingleton<SteamAppListCache>();
                services.AddSingleton<SteamAppInfoCache>();
                services.AddSingleton<CoverCache>();
                services.AddSingleton<ToastService>();
                services.AddSingleton<InsecureTransportNotice>();
                services.AddSingleton<SteamDepotInfo>();
                services.AddSingleton<LuaVault>();
                services.AddSingleton<Services.AppInfo.LaunchModStore>();
                services.AddSingleton<Services.AppInfo.LaunchOptionsService>();
                services.AddSingleton<LuaInstaller>();
                services.AddSingleton<SteamLibraryService>();
                services.AddSingleton<GithubProxy>();
                services.AddSingleton<HardwareAppIdService>();
                services.AddSingleton<SteamlessService>();
                services.AddSingleton<CloudRedirectService>();
                services.AddSingleton<UnlockerService>();
                services.AddSingleton<PluginInstallerService>();
                services.AddTransient<DropInstallViewModel>(); // one per page (Home, Add)
                services.AddSingleton<AuthService>();
                services.AddSingleton<LuaToolsApiClient>();
                services.AddSingleton<HubcapService>();
                services.AddSingleton<UpdateService>();
                // Hook loader infrastructure
                services.AddSingleton<PluginAddService>();
                services.AddSingleton<HttpServerService>();
                services.AddHostedService(sp => sp.GetRequiredService<HttpServerService>());
                // Also resolvable as a plain singleton (not just IHostedService) so PluginInstallerService
                // can call ReloadPluginFilesAsync() after install/uninstall — same pattern as HttpServerService.
                services.AddSingleton<CefInjectorService>();
                services.AddHostedService(sp => sp.GetRequiredService<CefInjectorService>());
                services.AddSingleton<DownloadViewModel>();
                services.AddSingleton<SettingsViewModel>();
                services.AddSingleton<ManageViewModel>();
                services.AddSingleton<BuildsViewModel>();
                services.AddTransient<LaunchOptionsViewModel>(); // one per dialog
                services.AddSingleton<HomeViewModel>();
                services.AddSingleton<ModeViewModel>();
                services.AddSingleton<FixesViewModel>();
                services.AddSingleton<PluginViewModel>();
                services.AddSingleton<OnboardingViewModel>();
                services.AddSingleton<MainViewModel>();
                // Pages resolved by NavigationView via the DI service provider.
                services.AddSingleton<HomeView>();
                services.AddSingleton<DownloadView>();
                services.AddSingleton<ManageView>();
                services.AddSingleton<BuildsView>();
                services.AddSingleton<ModeView>();
                services.AddSingleton<FixesView>();
                services.AddSingleton<PluginView>();
                services.AddSingleton<SettingsView>();
                services.AddSingleton<MainWindow>();
            })
            .Build();
    }

    private UpdateService Updates => _host.Services.GetRequiredService<UpdateService>();

    // Guards RunUpdateFlowAsync so overlapping triggers (startup + the re-poke a DLL/Steam restart causes)
    // never run it concurrently — a second caller drops out immediately.
    private readonly System.Threading.SemaphoreSlim _updateFlowGate = new(1, 1);

    /// <summary>
    /// Warn when Steam has overwritten launch options we'd applied, and offer to put them back.
    ///
    /// <para>
    /// Steam rebuilds appinfo.vdf from PICS on login, app updates and store browsing — it did so twice
    /// while this feature was being written — so an applied edit is not permanent. Re-applying is offered
    /// but never automatic: it closes Steam, which is not something to do behind the user's back at
    /// startup. Costs nothing when no launch options have been edited (the store short-circuits on empty).
    /// </para>
    /// </summary>
    private async Task CheckLaunchOptionDriftAsync()
    {
        try
        {
            var launch = _host.Services.GetRequiredService<Services.AppInfo.LaunchOptionsService>();
            if (launch.Store.IsEmpty) return;

            // Indexing the ~373 MB cache takes a couple of seconds — never on the UI thread.
            var drifted = await Task.Run(launch.FindDrifted);
            if (drifted.Count == 0) return;

            var toast = _host.Services.GetRequiredService<ToastService>();
            Dispatcher.Invoke(() => toast.ShowAction(
                LuaToolsGui.Resources.Strings.Launch_Drift_Title,
                string.Format(LuaToolsGui.Resources.Strings.Launch_Drift_Body, drifted.Count),
                LuaToolsGui.Resources.Strings.Launch_Drift_Action,
                () => _ = ReapplyDriftedAsync(launch, drifted, toast)));
        }
        catch
        {
            // Cache locked/unreadable — nothing actionable, and this must never block startup.
        }
    }

    /// <summary>
    /// The drift notice's "Re-apply" button: confirm, then write the staged edits back into appinfo.
    ///
    /// <para>
    /// The write runs OFF the UI thread. Unlike the launch-options dialog — which is modal, so its own
    /// synchronous apply merely blocks a window that's already blocking — this fires with the main window
    /// live, and <c>Apply</c> indexes a ~373 MB file, copies a backup and rewrites it. On the UI thread
    /// that's a multi-second freeze of the whole app.
    /// </para>
    /// </summary>
    private static async Task ReapplyDriftedAsync(
        Services.AppInfo.LaunchOptionsService launch, IReadOnlyList<int> drifted, ToastService toast)
    {
        // Same wording as the dialog's own prompt — closing Steam should never read as a different
        // decision depending on where it was triggered from.
        if (MessageBox.Show(
                LuaToolsGui.Resources.Strings.Launch_ApplyNow_Body,
                LuaToolsGui.Resources.Strings.Launch_ApplyNow_Title,
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        var result = await Task.Run(() => launch.Reapply(drifted));

        if (result.Ok)
            toast.Show(LuaToolsGui.Resources.Strings.Launch_Title,
                result.SteamWasRunning
                    ? LuaToolsGui.Resources.Strings.Launch_Applied_Restarted
                    : LuaToolsGui.Resources.Strings.Launch_Applied);
        else
            toast.Show(LuaToolsGui.Resources.Strings.Launch_Title,
                string.Format(LuaToolsGui.Resources.Strings.Launch_ApplyFailed, result.Error), error: true);
    }

    /// <summary>Set by OnStartup to <see cref="RunUpdateFlowAsync"/> so non-UI callers (e.g. the
    /// /check-updates HTTP handler) can run the exact same update flow instead of a divergent one.</summary>
    internal static Func<Task>? RunUpdateFlow;

    /// <summary>The Steam-open update flow (fully silent): update the APP first, unconditionally, before
    /// ever touching the plugin — then, once the running app is guaranteed current, check/apply a plugin
    /// update against it. Called on a loader (--tray-locked) launch and on the Steam-open re-check poke;
    /// safe to call repeatedly.
    /// <para>
    /// App-before-plugin is load-bearing, not just tidy ordering: the app and plugin are NOT independently
    /// safe to update out of order whenever a plugin release changes something the app's own compiled code
    /// depends on (e.g. <see cref="Services.CefInjectorService"/>'s CDP port is a compile-time constant —
    /// an old app build talking to a freshly-updated plugin that moved the port simply can't connect, and
    /// won't self-heal until the app itself happens to update, which is not guaranteed to land in the same
    /// pass: the app and plugin ship from separate repos on separate cadences, so one can succeed while the
    /// other fails/lags). Restarting into the latest app FIRST, before it goes anywhere near a plugin
    /// update, means whatever the plugin changes is always applied by a process that already understands
    /// it.
    /// </para></summary>
    private async Task RunUpdateFlowAsync()
    {
        if (!_updateFlowGate.Wait(0)) return; // another run already in progress
        try
        {
            // 1) Stage + immediately apply any app update, before touching the plugin at all.
            //    ApplyAndRestart() terminates this process; the relaunched instance (launched with
            //    --tray-locked) re-enters this same flow via OnStartup once it's already current, so this
            //    run's job ends here — there is nothing safe left for THIS process to do.
            try { await Updates.CheckAndStageAsync(); } catch { /* offline / not installed */ }
            if (Updates.HasStagedUpdate)
            {
                Dispatcher.Invoke(() => Updates.ApplyAndRestart(new[] { "--minimized", "--tray-locked" }));
                return;
            }

            // 2) No app update pending — safe to check/apply a plugin update against this (already-current) app.
            try
            {
                var installer = _host.Services.GetRequiredService<PluginInstallerService>();
                var st = await installer.GetStatusAsync(force: true);
                if (st.UpdateAvailable)
                {
                    if (!st.DllMatches)
                    {
                        var t = _host.Services.GetRequiredService<ToastService>();
                        Dispatcher.Invoke(() => t.Show("LuaTools", "Updating plugin — Steam will restart."));
                    }
                    await installer.InstallAsync(progress: null);
                }
            }
            catch { /* offline / install error — retry next Steam-open */ }
        }
        finally { _updateFlowGate.Release(); }
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // `async void`: an exception escaping this override cannot be observed by anyone and used to take
        // the process down silently — the app just never appeared. Route it somewhere visible instead.
        try
        {
            await StartupAsync();
        }
        catch (Exception ex)
        {
            LogCrash("OnStartup", ex);
            MessageBox.Show(
                $"LuaTools couldn't start.\n\n{LogSanitizer.Sanitize(ex.Message)}\n\nDetails were written to:\n{CrashLogPath}",
                "LuaTools", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>
    /// Pushes the amethyst accent into WPF-UI's own accent slots (see Themes/Colors.xaml for the palette
    /// and its contrast measurements).
    /// <para>
    /// Themes/Colors.xaml only reaches the markup this app writes itself. Everything drawn by the WPF-UI
    /// control templates — the NavigationView selection pill, Primary buttons, ToggleSwitch, ProgressRing,
    /// text carets and focus visuals — resolves its colour from WPF-UI's four accent resources instead, and
    /// by default those are the Windows personalisation colour, which is usually blue. Without this call
    /// the app would be two-toned: purple where we paint, whatever the user's Windows accent happens to be
    /// everywhere else.
    /// </para>
    /// <para>
    /// The explicit four-colour overload is used rather than the (accent, theme) one so the ramp is ours
    /// and not a runtime tint of a single seed. Order is (systemAccent, primary, secondary, tertiary); on a
    /// dark theme WPF-UI paints accent TEXT and thin strokes with the primary slot, so that slot gets the
    /// light 400 weight (7.2:1 on the app background) while the saturated 600 stays on filled surfaces
    /// where it sits under white text at 5.7:1.
    /// </para>
    /// <para>
    /// The four colours are READ FROM Themes/Colors.xaml rather than written here, so the palette has one
    /// source of truth. They used to be duplicated as hex literals in this method, which meant editing the
    /// dictionary silently left WPF-UI's controls on the old accent.
    /// </para>
    /// </summary>
    private static void ApplyAccentPalette()
    {
        var violet600 = FindColor("Violet600Color");
        var violet400 = FindColor("Violet400Color");
        var violet500 = FindColor("Violet500Color");
        var violet300 = FindColor("Violet300Color");

        // A missing key means the palette dictionary did not load at all. Applying half a ramp would look
        // worse than leaving WPF-UI's default alone, and VerifyAccentApplied() reports it either way.
        if (violet600 is null || violet400 is null || violet500 is null || violet300 is null)
        {
            LogThemeProblem("Themes/Colors.xaml did not provide the Violet*Color primitives — " +
                            "accent not applied.");
            return;
        }

        Wpf.Ui.Appearance.ApplicationAccentColorManager.Apply(
            systemAccent: violet600.Value,     // filled accent surfaces
            primaryAccent: violet400.Value,    // accent text/icons on dark
            secondaryAccent: violet500.Value,  // borders, non-text UI
            tertiaryAccent: violet300.Value);  // softest wash

        PromoteSurfaceOverrides();
    }

    /// <summary>Look up a <see cref="System.Windows.Media.Color"/> resource, or null if absent/wrong type.</summary>
    private static System.Windows.Media.Color? FindColor(string key) =>
        Current?.TryFindResource(key) is System.Windows.Media.Color c ? c : null;

    /// <summary>
    /// Confirms the amethyst accent actually reached WPF-UI, and says so loudly if it did not.
    ///
    /// <para>
    /// WHY THIS EXISTS: the theme depends on WPF-UI's internal resource KEY NAMES
    /// (<c>SystemAccentColorPrimary</c> and the surface keys in Themes/Colors.xaml). Those names are not a
    /// public contract. If a WPF-UI upgrade renames or restructures them, nothing fails to compile and
    /// nothing throws — the overrides simply stop matching and the app quietly reverts to grey chrome with
    /// the user's Windows accent. That is precisely the kind of regression that ships unnoticed, so it is
    /// checked at startup instead.
    /// </para>
    ///
    /// <para>
    /// Cost is one dictionary lookup and one colour comparison, after the window is up.
    /// </para>
    ///
    /// <para>
    /// TO REVALIDATE AFTER A WPF-UI UPGRADE: run the app once. A silent start means the accent still
    /// applies. If the warning fires, re-dump WPF-UI's dictionary to find the new key names — merge
    /// <c>Wpf.Ui.Markup.ThemesDictionary</c> in a throwaway console app, call
    /// <c>ApplicationAccentColorManager.Apply(...)</c> first (enumeration throws on
    /// <c>SystemAccentColorPrimary</c> otherwise), then enumerate every <c>Color</c>/<c>SolidColorBrush</c>
    /// entry — and update <see cref="PromoteSurfaceOverrides"/> plus the Layer 3 block of
    /// Themes/Colors.xaml to match. See docs/CHANGELOG.md.
    /// </para>
    /// </summary>
    private void VerifyAccentApplied()
    {
        // WPF-UI's accent manager writes this key; it is what its templates resolve against on a dark
        // theme, so it is the single most representative thing to assert.
        const string AccentKey = "SystemAccentColorPrimary";

        var expected = FindColor("Violet400Color");
        var actual = FindColor(AccentKey);

        if (expected is not null && actual == expected) return; // theme intact — the normal path

        string detail = actual is null
            ? $"'{AccentKey}' is missing from the merged resources"
            : $"'{AccentKey}' is {actual} but the palette expects {expected?.ToString() ?? "<unresolved>"}";

        LogThemeProblem($"accent override did not apply: {detail}. A WPF-UI upgrade most likely renamed " +
                        "its accent/surface resource keys — see App.VerifyAccentApplied for how to remap.");

        // Non-blocking: a wrong-looking theme must never stop the user from using the app.
        try
        {
            _host.Services.GetRequiredService<ToastService>()
                 .Show(LuaToolsGui.Resources.Strings.Theme_Guard_Title,
                       LuaToolsGui.Resources.Strings.Theme_Guard_Body, error: true);
        }
        catch { /* toast presenter not ready — the crash.log entry is the durable record */ }
    }

    /// <summary>Record a theme problem in crash.log, the file users are already asked to send.</summary>
    private static void LogThemeProblem(string message)
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(CrashLogPath)!);
            System.IO.File.AppendAllText(CrashLogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] THEME: {message}{Environment.NewLine}");
        }
        catch { /* logging a cosmetic problem must never itself break startup */ }
    }

    /// <summary>
    /// Re-homes the WPF-UI surface overrides from Themes/Colors.xaml into the TOP LEVEL of
    /// Application.Resources.
    /// <para>
    /// A resource lookup checks a dictionary's own entries BEFORE its merged dictionaries, so an entry
    /// sitting directly in Application.Resources outranks every merged dictionary — including WPF-UI's.
    /// </para>
    /// <para>
    /// This reaches the card/control/layer fills. Confirmed by arithmetic on a real render rather than by
    /// eye: cards measured #353436 over a #272727 base, which is exactly this palette's #12EDE9FE overlay
    /// (predicted #353536) and not WPF-UI's stock #0DFFFFFF (predicted #323232).
    /// </para>
    /// <para>
    /// It does NOT reach the window shell. ApplicationBackgroundBrush and the NavigationView background
    /// are painted through WPF-UI's own template path, which resolves them inside its dictionary scope at
    /// parse time; no app-level override touches them. Those two are handled by setting the property
    /// directly on markup this app owns — see the root Grid's Background in MainWindow.xaml.
    /// </para>
    /// </summary>
    private static void PromoteSurfaceOverrides()
    {
        var res = Current?.Resources;
        if (res is null) return;

        // Keys that Themes/Colors.xaml deliberately redefines using WPF-UI's own names.
        string[] overriddenKeys =
        [
            "ApplicationBackgroundColor", "ApplicationBackgroundBrush",
            "SolidBackgroundFillColorBase", "SolidBackgroundFillColorBaseBrush",
            "SolidBackgroundFillColorBaseAlt", "SolidBackgroundFillColorBaseAltBrush",
            "SolidBackgroundFillColorSecondary", "SolidBackgroundFillColorSecondaryBrush",
            "SolidBackgroundFillColorTertiary", "SolidBackgroundFillColorTertiaryBrush",
            "SolidBackgroundFillColorQuarternary", "SolidBackgroundFillColorQuarternaryBrush",
            "CardBackgroundFillColorDefault", "CardBackgroundFillColorDefaultBrush",
            "CardBackgroundFillColorSecondary", "CardBackgroundFillColorSecondaryBrush",
            "CardBackground", "CardBackgroundDisabled",
            "CardBackgroundPointerOver", "CardBackgroundPressed",
            "ControlFillColorDefault", "ControlFillColorDefaultBrush",
            "ControlStrokeColorDefault", "ControlStrokeColorDefaultBrush",
            "LayerFillColorDefault", "LayerFillColorDefaultBrush",
            "LayerFillColorAlt", "LayerFillColorAltBrush",
        ];

        // Read the value out of our merged palette, then write it back as a top-level entry.
        // Iterate merged dictionaries in REVERSE: WPF-UI's theme is merged first and defines these same
        // keys, so a forward scan would find WPF-UI's grey and promote that — re-applying the very
        // values this is meant to replace. Last merged (Themes/Colors.xaml) wins, matching WPF's own
        // resolution order.
        var merged = Current!.Resources.MergedDictionaries;
        foreach (var key in overriddenKeys)
        {
            for (int i = merged.Count - 1; i >= 0; i--)
            {
                if (!merged[i].Contains(key)) continue;
                res[key] = merged[i][key];
                break;
            }
        }
    }

    /// <summary>The real startup sequence, split out of the <c>async void</c>
    /// <see cref="OnStartup"/> override so that its failures are actually catchable.</summary>
    private async Task StartupAsync()
    {
        ApplyAccentPalette();

        // Legacy cleanup: older builds staged downloads in ~/Downloads/LuaTools (they now stage in
        // %TEMP% and self-delete). Remove any leftovers from that user-visible folder, best-effort.
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                string legacy = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "LuaTools");
                if (System.IO.Directory.Exists(legacy)) System.IO.Directory.Delete(legacy, recursive: true);
            }
            catch { /* best effort — never block startup on cleanup */ }
        });

        await _host.StartAsync();

        var main = _host.Services.GetRequiredService<MainViewModel>();
        var settingsVm = _host.Services.GetRequiredService<SettingsViewModel>();

        // Changing the language needs a relaunch (x:Static resources resolve at parse time).
        settingsVm.RequestRestart = RelaunchApp;

        var window = _host.Services.GetRequiredService<MainWindow>();

        // Turning off "Minimize to tray" while hidden in the tray → bring the window back.
        settingsVm.RequestShowWindow = () => Dispatcher.Invoke(window.RestoreFromTray);

        // Relaunching the app (single-instance) signals this event → surface the existing window and
        // check for any protocol URL a second instance wrote. AutoReset + executeOnlyOnce:false so it
        // keeps firing for every relaunch.
        if (Program.ShowWindowSignal is not null)
            System.Threading.ThreadPool.RegisterWaitForSingleObject(
                Program.ShowWindowSignal,
                (_, _) => Dispatcher.Invoke(() =>
                {
                    // A silent install relaunch stays headless — don't surface the window for it.
                    string? pending = ProtocolService.TryReadPending();
                    bool silent = pending is not null && ProtocolService.Parse(pending).Silent;
                    if (!silent)
                        window.RestoreFromTray();
                    if (pending is not null)
                        HandleProtocolUrl(pending);
                }),
                null, System.Threading.Timeout.Infinite, executeOnlyOnce: false);

        // A --tray-locked relaunch (the loader) signals this → enable close-to-tray for the session even if
        // this instance was started without the flag. Idempotent; keeps firing for every relaunch.
        if (Program.EnableTrayLockSignal is not null)
            System.Threading.ThreadPool.RegisterWaitForSingleObject(
                Program.EnableTrayLockSignal,
                (_, _) => Program.SessionTrayLock = true,
                null, System.Threading.Timeout.Infinite, executeOnlyOnce: false);

        // A --tray-locked relaunch (the loader on Steam-open) signals this → re-run the update flow so an
        // already-running app still updates when the user opens Steam. Guarded internally against overlap.
        if (Program.RecheckUpdatesSignal is not null)
            System.Threading.ThreadPool.RegisterWaitForSingleObject(
                Program.RecheckUpdatesSignal,
                (_, _) => _ = RunUpdateFlowAsync(),
                null, System.Threading.Timeout.Infinite, executeOnlyOnce: false);

        // Expose the same flow to non-UI callers (the /check-updates HTTP handler).
        RunUpdateFlow = RunUpdateFlowAsync;

        // Ask before LuaTools enables Steam's debug bridge. The service can't show UI itself (it runs on
        // background threads and during silent update), so it calls back here. Marshalled to the UI thread
        // and blocking, because the caller needs the answer before deciding whether to create the marker.
        _host.Services.GetRequiredService<PluginInstallerService>().ConfirmCdpExposure = () =>
            Dispatcher.Invoke(() => MessageBox.Show(
                window,
                LuaToolsGui.Resources.Strings.Cdp_Consent_Body,
                LuaToolsGui.Resources.Strings.Cdp_Consent_Title,
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes);

        // Settings' own "Sign in with Discord" button → browser OAuth (unchanged).
        settingsVm.RequestSignIn = () => main.SignInCommand.ExecuteAsync(null);

        // Guests hitting a protected action on other pages → navigate to Settings with context banner.
        Func<Task> navigateToSignIn = () =>
        {
            Dispatcher.Invoke(() =>
            {
                settingsVm.LoginRequiredMessage = LuaToolsGui.Resources.Strings.Settings_LoginRequired;
                window.NavigateToSettings();
            });
            return Task.CompletedTask;
        };
        _host.Services.GetRequiredService<DownloadViewModel>().RequestSignIn = navigateToSignIn;
        _host.Services.GetRequiredService<FixesViewModel>().RequestSignIn = navigateToSignIn;
        var toast = _host.Services.GetRequiredService<ToastService>();
        toast.Attach(window.RootSnackbar); // wire the presenter before anything can raise a toast

        // Now that a toast can actually be shown, confirm the theme survived whatever version of WPF-UI
        // this build resolved against. Silent when everything is fine.
        VerifyAccentApplied();

        // Language changed → persistent toast offering an immediate relaunch.
        settingsVm.RequestRestartPrompt = () => Dispatcher.Invoke(() =>
            toast.ShowAction(
                LuaToolsGui.Resources.Strings.Lang_Changed_Title,
                LuaToolsGui.Resources.Strings.Lang_Changed_Body,
                LuaToolsGui.Resources.Strings.Lang_Changed_Restart,
                () => settingsVm.RequestRestart?.Invoke()));

        // App updates now apply silently via RunUpdateFlowAsync (restart-on-Steam-open, unconditionally
        // and before any plugin update), so no "Restart" prompt toast.
        var download = _host.Services.GetRequiredService<DownloadViewModel>();

        var manage = _host.Services.GetRequiredService<ManageViewModel>();

        // Manage page "Update" → go to the Add page pre-seeded with that appid.
        manage.NavigateToAdd = appId =>
            Dispatcher.Invoke(() => { window.NavigateToAdd(); download.SeedSearch(appId); });

        // Manage flyout "Manage Build" → go to the Builds page with that game selected.
        var builds = _host.Services.GetRequiredService<BuildsViewModel>();
        manage.NavigateToBuilds = appId =>
            Dispatcher.Invoke(() => { window.NavigateToBuilds(); _ = builds.SelectAppAsync(appId); });

        // Manage flyout "Launch options…" → modal editor over Steam's appinfo cache.
        manage.OpenLaunchOptions = (appId, name) => Dispatcher.Invoke(() =>
        {
            var dialog = new LaunchOptionsDialog(
                _host.Services.GetRequiredService<LaunchOptionsViewModel>(), appId, name)
            { Owner = window };
            dialog.ShowDialog();
        });

        // Steam regenerates appinfo.vdf from PICS, wiping launch edits. Check once at startup and
        // OFFER to re-apply — never silently, since applying closes Steam.
        _ = CheckLaunchOptionDriftAsync();

        // Home "recently added" + Add install banner "Reveal" → go to Manage and open that game's detail.
        Action<long> openInManage = appId =>
            Dispatcher.Invoke(() => { window.NavigateToManage(); _ = manage.OpenDetailForAppIdAsync(appId); });
        var home = _host.Services.GetRequiredService<HomeViewModel>();
        home.NavigateToGame = openInManage;
        download.NavigateToGame = openInManage;
        builds.NavigateToManage = openInManage; // Builds "Manage" button — the reverse of "Manage Build"

        // Dragging a SteamDB / Steam store link onto either drop box installs that appid. Routed through
        // HandleProtocolUrl rather than calling ProtocolInstall directly, so a dropped link and
        // luatools://install/<id> are literally the same path and can't drift apart later.
        // DropInstallViewModel is transient, so Home and Add each hold their own instance.
        Func<long, Task> installByAppId = appId =>
        {
            Dispatcher.Invoke(() => HandleProtocolUrl($"luatools://install/{appId}"));
            return Task.CompletedTask;
        };
        home.Drop.InstallByAppId = installByAppId;
        download.Drop.InstallByAppId = installByAppId;

        // Home dashboard cells → section navigation.
        home.NavigateToPlugin = () => Dispatcher.Invoke(window.NavigateToPlugin);
        home.NavigateToManage = () => Dispatcher.Invoke(window.NavigateToManage);
        home.NavigateToSettings = () => Dispatcher.Invoke(window.NavigateToSettings);
        home.NavigateToMode = () => Dispatcher.Invoke(window.NavigateToMode);

        // Onboarding finished applying its actions → refresh the Home dashboard tiles (mode + plugin status).
        main.Onboarding.RefreshHome = () => Dispatcher.Invoke(() => home.LoadAsync());

        // Any game added (plugin store-page button, drag-drop, Add page, Fixes) → refresh the library views
        // live. LuaInstaller.Installed can fire on a background thread (plugin install), so marshal to UI.
        var luaInstaller = _host.Services.GetRequiredService<LuaInstaller>();
        var appInfo = _host.Services.GetRequiredService<SteamAppInfoCache>();
        luaInstaller.Installed += appId => Dispatcher.InvokeAsync(async () =>
        {
            _ = manage.LoadAsync();            // re-scan so Manage updates too if it's the visible page
            _ = builds.LoadAsync();            // a newly installed lua is a new variant in the vault
            await home.RefreshLibraryAsync();  // game appears (its cover may lag for newer titles)

            // Newer titles have no guessable header URL — the classic CDN path 404s and the real header is
            // a content-hashed store_item_assets URL that only comes from appdetails. Warm that game's
            // details at interactive priority (retries past throttling), then refresh again so its cover
            // fills in instead of staying blank until an app restart.
            if (await appInfo.EnsureFullDetailsAsync(appId))
                await home.RefreshLibraryAsync();
        });

        // Handle a protocol URL from the command line (first launch) or from a temp file left by a
        // second instance that exited before the signal listener was wired up.
        string? url = Program.StartupUrl ?? ProtocolService.TryReadPending();

        // A silent install launch (luatools://install/silent/<id>) runs headless: stay in the tray and
        // never surface the window. The window's Loaded handler (which restores auth) won't fire when we
        // skip Show(), so restore the session explicitly before the install runs.
        bool silentStartup = (url is not null && ProtocolService.Parse(url).Silent) || Program.StartMinimized;

        // Auto-exit after a silent install only when this was a COLD launch for it (StartupUrl came on the
        // command line, not from an already-running second instance) AND the user doesn't keep a tray app
        // around. Otherwise the app was already living somewhere and must stay.
        _exitAfterSilentInstall = silentStartup
            && Program.StartupUrl is not null
            && !settingsVm.MinimizeToTray;

        if (silentStartup)
        {
            window.StartSilent();
            try { await main.InitializeAsync(); } catch { /* offline → install proceeds as guest */ }
        }
        else
        {
            window.Show();

            // First-run onboarding: show the welcome overlay on a fresh install. Skip it (and mark done)
            // when the user is already set up — BetterSteamTools (or Nightly) selected AND the plugin
            // installed — so existing users / dev machines aren't nagged. Marking done here is permanent,
            // so switching mode later never re-triggers onboarding.
            var cache = _host.Services.GetRequiredService<CacheService>();
            if (!cache.OnboardingComplete)
            {
                var unlocker = _host.Services.GetRequiredService<UnlockerService>();
                var installer = _host.Services.GetRequiredService<PluginInstallerService>();
                bool configured =
                    unlocker.SelectedMode is (UnlockerMode.OpenSteamTools or UnlockerMode.OpenSteamToolsNightly)
                    && installer.IsInstalledLocally();
                if (configured) cache.OnboardingComplete = true;
                else main.Onboarding.IsOpen = true;
            }
        }

        if (url is not null)
            HandleProtocolUrl(url);

        // Background, non-blocking Steam-open update flow (app + plugin), but ONLY in the loader context
        // (--tray-locked). A manual / protocol / silent-install launch skips it, so the app never
        // auto-updates or restarts mid-manual-use — it only happens when Steam launches us. (Velopack only
        // updates to a STRICTLY HIGHER version, so every release must bump --packVersion.)
        if (Program.SessionTrayLock)
            _ = RunUpdateFlowAsync();

        // NOTE: startup used to fire two outbound calls here — a Steam decryption-key upload (DonateKeys,
        // on by default, over plain HTTP) and an Umami telemetry ping (no opt-out). Both were removed;
        // see the note in AppConfig. Nothing was put in their place.

        // Warm the hardware-appid blacklist (refreshes from GitHub if the cache is stale). Fire-and-forget.
        _ = _host.Services.GetRequiredService<HardwareAppIdService>().EnsureFreshAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Was `async void`: WPF continued tearing the process down at the first await, so _host.StopAsync()
        // frequently never finished — hosted services (the HTTP listener, the CEF injector) were killed
        // mid-shutdown rather than stopped, and _host.Dispose() could be skipped entirely. Block here
        // instead, with a bound so a wedged service can't hang the exit.
        try
        {
            // If an update was downloaded but not yet applied, stage it for after exit.
            if (Updates.HasStagedUpdate)
                Updates.ApplyOnExit();
        }
        catch (Exception ex) { LogCrash("OnExit/ApplyOnExit", ex); }

        try
        {
            // Deliberate sync-over-async, and the one place it is correct here. OnExit cannot be awaited by
            // WPF, and making it `async void` (what it was) means the framework carries on tearing the
            // process down at the first await — so StopAsync often never finished and Dispose was skipped.
            // Blocking is safe at this point specifically: the dispatcher loop has already ended, so there
            // is no SynchronizationContext left to deadlock against. The 5s bound keeps a wedged hosted
            // service from hanging the exit.
            _host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
        catch (Exception ex) { LogCrash("OnExit/StopAsync", ex); }

        try { _host.Dispose(); } catch (Exception ex) { LogCrash("OnExit/Dispose", ex); }

        base.OnExit(e);
    }

    /// <summary>Relaunch the app (used after a language change). The single-instance mutex is released
    /// only when THIS process exits, so we start the new instance via a short delayed shell command — by
    /// the time it launches the exe, our mutex is free and the new instance won't bow out.</summary>
    private void RelaunchApp()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (exe is not null)
            {
                // cmd: wait ~1.2s for this process's mutex to release, then start the exe detached.
                var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe",
                    $"/c timeout /t 2 /nobreak >nul & start \"\" \"{exe}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                };
                System.Diagnostics.Process.Start(psi);
            }
        }
        catch { /* if relaunch fails, the user can reopen manually */ }
        finally
        {
            Shutdown();
        }
    }

    /// <summary>Route a luatools:// protocol URL to the appropriate page and action.</summary>
    private void HandleProtocolUrl(string url)
    {
        var (action, appId, silent) = ProtocolService.Parse(url);
        if (action is null || appId is null) return;

        var window = _host.Services.GetRequiredService<MainWindow>();
        var download = _host.Services.GetRequiredService<DownloadViewModel>();
        var manage = _host.Services.GetRequiredService<ManageViewModel>();
        var fixes = _host.Services.GetRequiredService<FixesViewModel>();

        switch (action)
        {
            case "game":
                window.NavigateToAdd();
                download.SeedSearch(appId.Value);
                break;
            case "install":
                if (silent)
                {
                    // The luatools:// handler is registered machine-wide under HKCU, so ANY web page can
                    // navigate to luatools://install/silent/<appid>. The silent variant then wrote a lua
                    // into Steam's stplug-in with no window, no prompt and no way for the user to know it
                    // had happened until the balloon fired. Confirm once before doing it — a genuine
                    // install button on the site is one extra click; a drive-by is stopped.
                    if (MessageBox.Show(
                            string.Format(LuaToolsGui.Resources.Strings.Protocol_SilentInstall_Body, appId.Value),
                            LuaToolsGui.Resources.Strings.Protocol_SilentInstall_Title,
                            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                    {
                        // Declined. A cold launch existed only to run this install — don't leave a ghost
                        // process (or tray icon) behind.
                        if (_exitAfterSilentInstall) Shutdown();
                        break;
                    }

                    // Headless: don't navigate or surface; install in the background, then a tray balloon.
                    _ = download.ProtocolInstall(appId.Value,
                        (msg, error) => Dispatcher.Invoke(() =>
                        {
                            window.ShowInstallNotification(msg, error);
                            // Cold launch + no tray app wanted → exit once the balloon has had time to show.
                            if (_exitAfterSilentInstall)
                                _ = Task.Delay(6000).ContinueWith(_ => Dispatcher.Invoke(Shutdown));
                        }));
                }
                else
                {
                    window.NavigateToAdd();
                    _ = download.ProtocolInstall(appId.Value);
                }
                break;
            case "manage":
                window.NavigateToManage();
                _ = manage.OpenDetailForAppIdAsync(appId.Value);
                break;
            case "fix":
                window.NavigateToFixes();
                _ = fixes.OpenForAppIdAsync(appId.Value);
                break;
        }
    }
}
