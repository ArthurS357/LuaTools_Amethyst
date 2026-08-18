using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuaToolsGui.Services;
using LuaToolsGui.Models;
using Microsoft.Win32;

namespace LuaToolsGui.ViewModels;

/// <summary>A selectable UI language. <see cref="Tag"/> is the BCP-47 tag ("en", "zh-Hans") or null for
/// "follow the system display language".</summary>
public record LanguageOption(string Display, string? Tag);

/// <summary>A selectable accent ramp. <see cref="Id"/> matches <c>Themes.AccentPalette.Id</c> and is what
/// settings.json persists; <see cref="Display"/> is localised and must never be stored.</summary>
public record AccentOption(string Display, string Id);

public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly AuthService _auth;
    private readonly SteamService _steam;
    private readonly HubcapService _hubcap;

    [ObservableProperty] private string? _displayName;
    [ObservableProperty] private string? _email;
    [ObservableProperty] private string? _avatarUrl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRealUser))]
    private bool _isGuest = true;

    public bool IsRealUser => !IsGuest;

    // ── Login-required redirect banner ─────────────────────────────
    /// <summary>Set by App when navigating here from a protected action. Null = banner hidden.</summary>
    [ObservableProperty] private string? _loginRequiredMessage;

    [RelayCommand]
    private void DismissLoginRequired() => LoginRequiredMessage = null;

    // ── Bot-provisioned account re-link banner ──────────────────────
    /// <summary>True when the signed-in session is a Discord bot placeholder (@bot.lua.tools).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBotLinkBanner))]
    private bool _isBotProvisioned;

    /// <summary>Session-only — resets next launch so the banner re-checks on every startup.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBotLinkBanner))]
    private bool _botBannerDismissed;

    public bool ShowBotLinkBanner => false;

    // ── Steam location ──────────────────────────────────────────────
    [ObservableProperty] private string _steamPath = "";
    [ObservableProperty] private bool _isSteamOverridden;
    [ObservableProperty] private string _steamSource = "";
    [ObservableProperty] private string? _steamWarning;

    // ── Install behavior ────────────────────────────────────────────
    /// <summary>Auto Update Apps (Don't Lock Manifests). Persisted via SettingsService.</summary>
    [ObservableProperty] private bool _autoUpdateApps;

    partial void OnAutoUpdateAppsChanged(bool value) => _settings.AutoUpdateApps = value;

    /// <summary>FastFetch: auto-download from the first available source. Same persisted setting the Add
    /// screen's toggle drives (both read/write SettingsService.FastFetch).</summary>
    [ObservableProperty] private bool _fastFetch;

    partial void OnFastFetchChanged(bool value) => _settings.FastFetch = value;

    // ── Startup behavior ────────────────────────────────────────────
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "LuaTools";

    /// <summary>Launch the app on Windows sign-in (writes HKCU …\Run). Persisted via SettingsService.</summary>
    [ObservableProperty] private bool _startWithWindows;

    partial void OnStartWithWindowsChanged(bool value)
    {
        _settings.StartWithWindows = value;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null) return;
            if (value)
                // --minimized: start silently in the tray on sign-in so it just serves the
                // local backend (127.0.0.1:6767) for the Steam plugin without stealing focus.
                key.SetValue(RunValueName, $"\"{Environment.ProcessPath}\" --minimized");
            else
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
        catch { /* registry write blocked — setting is still saved, just not applied this run */ }
    }

    /// <summary>Minimize to the system tray instead of the taskbar. Persisted via SettingsService.</summary>
    [ObservableProperty] private bool _minimizeToTray;

    partial void OnMinimizeToTrayChanged(bool value)
    {
        _settings.MinimizeToTray = value;
        if (!value) RequestShowWindow?.Invoke(); // turning it off restores the window if it's hidden in the tray
    }

    /// <summary>Set by App: restore the main window from the tray (used when Minimize-to-tray is turned off).</summary>
    public Action? RequestShowWindow { get; set; }

    // ── Language ────────────────────────────────────────────────────
    /// <summary>Available UI languages (native endonyms, matching Steam's list). "System default"
    /// (null tag) follows Windows. Languages whose .resx isn't present fall back to English.</summary>
    public ObservableCollection<LanguageOption> LanguageOptions { get; } =
    [
        new(Resources.Strings.Settings_Language_SystemDefault, null),
        new("English", "en"),
        new("简体中文", "zh-Hans"),
        new("繁體中文", "zh-Hant"),
        new("日本語", "ja"),
        new("한국어", "ko"),
        new("Español (España)", "es"),
        new("Español (Latinoamérica)", "es-419"),
        new("Português (Brasil)", "pt-BR"),
        new("Português (Portugal)", "pt-PT"),
        new("Français", "fr"),
        new("Deutsch", "de"),
        new("Italiano", "it"),
        new("Nederlands", "nl"),
        new("Polski", "pl"),
        new("Русский", "ru"),
        new("Українська", "uk"),
        new("العربية", "ar"),
        new("Čeština", "cs"),
        new("Magyar", "hu"),
        new("Română", "ro"),
        new("Türkçe", "tr"),
        new("Ελληνικά", "el"),
        new("Български", "bg"),
        new("ไทย", "th"),
        new("Tiếng Việt", "vi"),
        new("Bahasa Indonesia", "id"),
        new("Dansk", "da"),
        new("Suomi", "fi"),
        new("Norsk", "nb"),
        new("Svenska", "sv"),
    ];

    [ObservableProperty] private LanguageOption _selectedLanguage = null!;

    private bool _suppressLanguagePrompt; // true during ctor init so we don't prompt on first bind

    partial void OnSelectedLanguageChanged(LanguageOption value)
    {
        if (value is null || _suppressLanguagePrompt) return;
        _settings.Language = value.Tag; // null = follow system
        // The whole UI is built with parse-time x:Static resources, so a relaunch is needed to re-read it.
        RequestRestartPrompt?.Invoke();
    }

    // ── Accent colour ───────────────────────────────────────────────

    /// <summary>Selectable accent ramps. <see cref="AccentOption.Id"/> is what settings.json stores;
    /// the display name is localised, the id never is.</summary>
    public ObservableCollection<AccentOption> AccentOptions { get; } =
    [
        new(Resources.Strings.Settings_Accent_Amethyst, Themes.AccentPalette.Amethyst.Id),
        new(Resources.Strings.Settings_Accent_Green, Themes.AccentPalette.Green.Id),
        new(Resources.Strings.Settings_Accent_Red, Themes.AccentPalette.Red.Id),
    ];

    /// <summary>
    /// What the picker is showing. Changing it repaints NOTHING on its own.
    ///
    /// <para>
    /// Selecting used to apply and persist immediately, which made every accidental brush against the
    /// dropdown a theme change with no way back except picking the old one again from memory. The choice
    /// is now staged here and only takes effect through <see cref="ApplyAccentChangeCommand"/>, so a
    /// selection the user does not confirm leaves the running theme — and settings.json — untouched, even
    /// if they navigate away.
    /// </para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingAccentChange))]
    [NotifyCanExecuteChangedFor(nameof(ApplyAccentChangeCommand))]
    private AccentOption _selectedAccent = null!;

    /// <summary>The accent actually painted right now. Tracked separately from the picker because the gap
    /// between the two IS the pending change, and the Apply button is driven by that gap.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingAccentChange))]
    [NotifyCanExecuteChangedFor(nameof(ApplyAccentChangeCommand))]
    private string _appliedAccentId = Themes.AccentPalette.Amethyst.Id;

    /// <summary>True when the picker shows something other than what is painted — the Apply button is
    /// enabled exactly here, so "nothing to apply" is visible rather than something the user discovers by
    /// clicking and seeing no effect.</summary>
    public bool HasPendingAccentChange =>
        SelectedAccent is { } option &&
        !string.Equals(option.Id, AppliedAccentId, StringComparison.OrdinalIgnoreCase);

    /// <summary>Set by App to the real repaint. Left null in tests, which is what lets the staging and
    /// persistence logic below be exercised without a live Application.</summary>
    public Action<Themes.AccentPalette>? ApplyAccentPalette { get; set; }

    /// <summary>Paint the staged accent and remember it. Disabled while there is nothing staged.</summary>
    [RelayCommand(CanExecute = nameof(HasPendingAccentChange))]
    private void ApplyAccentChange()
    {
        if (SelectedAccent is not { } option) return;

        var palette = Themes.AccentPalette.FromId(option.Id);

        // Persist BEFORE painting. If the repaint throws, the user has still chosen, and the next launch
        // comes up in the colour they picked rather than silently reverting.
        _settings.AccentColor = palette.Id;
        AppliedAccentId = palette.Id;
        ApplyAccentPalette?.Invoke(palette);
    }

    // ── Hubcap API key ──────────────────────────────────────────────
    /// <summary>The key the user is typing/pasting. Starts blank — the saved key is never shown back.</summary>
    [ObservableProperty] private string _hubcapKeyInput = "";

    /// <summary>Status line under the key box (usage/expiry on success, or an error). Null = hidden.</summary>
    [ObservableProperty] private string? _hubcapKeyStatus;

    /// <summary>Colour for the status line: red on error, green on success. Holds a theme resource KEY
    /// (resolved by ResourceKeyToBrushConverter), not a literal — see Themes/Colors.xaml.</summary>
    [ObservableProperty] private string _hubcapKeyStatusColor = "SuccessBrush";
    /// <summary>True when a key is saved in settings (drives the "Clear" button + "configured" label).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHubcapStats))]
    [NotifyPropertyChangedFor(nameof(HubcapStatsPending))]
    [NotifyPropertyChangedFor(nameof(HubcapStatsText))]
    private bool _hubcapIsKeyConfigured;

    /// <summary>True while validating (disables the buttons via <see cref="CanEditHubcapKey"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditHubcapKey))]
    private bool _isValidatingHubcapKey;

    public bool CanEditHubcapKey => !IsValidatingHubcapKey;

    [ObservableProperty] private bool _isRefreshingHubcapStats;

    /// <summary>Latest stats for the saved key. Null = not loaded / no key / error.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HubcapStatsDisplay))]
    [NotifyPropertyChangedFor(nameof(HubcapStatsText))]
    [NotifyPropertyChangedFor(nameof(HubcapStatsPending))]
    [NotifyPropertyChangedFor(nameof(HubcapUsagePercent))]
    [NotifyPropertyChangedFor(nameof(ShowHubcapStats))]
    [NotifyPropertyChangedFor(nameof(HubcapExpiryWarning))]
    [NotifyPropertyChangedFor(nameof(HubcapExpiryWarningColor))]
    private HubcapStats? _hubcapStats;

    /// <summary>"12 / 25 requests today" or "12 / 25 requests today · expires 2026-08-15".</summary>
    public string? HubcapStatsDisplay =>
        HubcapStats is not null ? FormatHubcapStats(HubcapStats) : null;

    /// <summary>Progress bar fill 0.0–1.0.</summary>
    public double HubcapUsagePercent =>
        HubcapStats is { DailyLimit: > 0 } ? (double)HubcapStats.DailyUsage / HubcapStats.DailyLimit : 0;

    /// <summary>Show the usage card whenever a key is configured. It renders a loading state until stats
    /// arrive — never blank — so the section can't look broken while the fetch is in flight or after a
    /// transient failure (the key input is collapsed once configured, so this card is the only content).</summary>
    public bool ShowHubcapStats => HubcapIsKeyConfigured;

    /// <summary>Stats not yet loaded for a configured key → show the "Loading…" placeholder + indeterminate bar.</summary>
    public bool HubcapStatsPending => HubcapIsKeyConfigured && HubcapStats is null;

    /// <summary>Placeholder text shown until real stats load.</summary>
    public string HubcapStatsText => HubcapStatsDisplay ?? Resources.Strings.Common_Loading;

    /// <summary>
    /// Expiry state of the saved key, or null when there is nothing to say.
    ///
    /// <para>
    /// Null covers three distinct situations that all warrant the same silence: no key, a stats call that
    /// has not landed (or failed, which leaves <see cref="HubcapStats"/> null), and a key Hubcap reports
    /// no expiry for. A warning about a key that is actually fine is worse than no warning at all — it
    /// teaches the user to ignore the one that matters — so anything short of a parsed date shows nothing.
    /// </para>
    /// </summary>
    private HubcapKeyExpiry? Expiry =>
        HubcapStats is { } stats ? HubcapKeyExpiry.Evaluate(stats.ApiKeyExpiresAt, DateTimeOffset.UtcNow) : null;

    /// <summary>The unprompted expiry warning, or null to show nothing. Drives both the wording and, via
    /// NullToCollapsed, whether the row is on screen at all — one property, so the two cannot disagree.</summary>
    public string? HubcapExpiryWarning => Expiry is { IsWarning: true } e ? DescribeExpiry(e) : null;

    /// <summary>Theme resource KEY for the warning row (resolved by ResourceKeyToBrushConverter, same as
    /// <see cref="HubcapKeyStatusColor"/>). Red once the key is dead, amber while it still works.</summary>
    public string HubcapExpiryWarningColor =>
        Expiry is { HasExpired: true } ? "DangerBrush" : "WarningBrush";

    /// <summary>Set by App so the guest "Sign in" button can run the Discord flow.</summary>
    public Func<Task>? RequestSignIn { get; set; }

    /// <summary>Set by App: actually relaunch the app (used after a language change).</summary>
    public Action? RequestRestart { get; set; }

    /// <summary>Show the "language changed — restart now?" toast. Wired here so the VM stays UI-agnostic;
    /// App provides the toast + restart action.</summary>
    public Action? RequestRestartPrompt { get; set; }

    public SettingsViewModel(SettingsService settings, AuthService auth, SteamService steam, HubcapService hubcap)
    {
        _settings = settings;
        _auth = auth;
        _steam = steam;
        _hubcap = hubcap;
        _auth.AuthStateChanged += RefreshAccount;
        RefreshAccount();
        RefreshSteam();
        _autoUpdateApps = settings.AutoUpdateApps; // init from saved value (default ON) without triggering Save
        _fastFetch = settings.FastFetch;
        _startWithWindows = settings.StartWithWindows; // default OFF — init without triggering the registry write
        _minimizeToTray = settings.MinimizeToTray;
        _hubcapIsKeyConfigured = !string.IsNullOrEmpty(settings.HubcapApiKey);

        // Select the saved language (or "System default") without firing the restart prompt.
        _suppressLanguagePrompt = true;
        _selectedLanguage = LanguageOptions.FirstOrDefault(o => o.Tag == settings.Language) ?? LanguageOptions[0];
        _suppressLanguagePrompt = false;

        // Same shape: reflect the saved accent without writing it straight back out.
        // The saved accent is both what is painted and what the picker starts on, so there is no pending
        // change at startup and Apply comes up disabled. No suppression flag is needed any more: selecting
        // has no side effect to suppress.
        string activeAccent = Themes.AccentPalette.FromId(settings.AccentColor).Id;
        _selectedAccent = AccentOptions.First(o => o.Id == activeAccent);
        _appliedAccentId = activeAccent;
    }

    private void RefreshSteam()
    {
        string? path = _steam.EffectivePath;
        IsSteamOverridden = _steam.IsOverridden;
        SteamPath = path ?? Resources.Strings.Settings_SteamNotFound;
        SteamSource = IsSteamOverridden ? Resources.Strings.Settings_SteamSource_Custom
            : path is null ? Resources.Strings.Settings_SteamSource_NotFound
            : Resources.Strings.Settings_SteamSource_Auto;
        SteamWarning = path is not null && !_steam.IsValid ? Resources.Strings.Settings_SteamWarning_NoExe : null;
    }

    public void RefreshAccount()
    {
        IsGuest = _auth.IsGuest;
        DisplayName = _auth.DisplayName;
        Email = _auth.Email;
        AvatarUrl = _auth.AvatarUrl;
        IsBotProvisioned = _auth.IsBotProvisioned;
        if (!IsGuest) LoginRequiredMessage = null;
    }

    /// <summary>Hide the re-link banner for this session (returns next launch if still a bot account).</summary>
    [RelayCommand]
    private void DismissBotBanner() => BotBannerDismissed = true;

    [RelayCommand]
    private async Task SignInAsync()
    {
        if (RequestSignIn is not null) await RequestSignIn();
    }

    // ── Discord bot code sign-in ────────────────────────────────────

    /// <summary>The 6-char code the user typed from the Discord <c>/login</c> DM.</summary>
    [ObservableProperty] private string _codeInput = "";

    /// <summary>True while redeeming — disables the Redeem button via <see cref="CanRedeemCode"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRedeemCode))]
    private bool _isRedeemingCode;

    /// <summary>Error shown under the code box (expired/invalid/server). Null = hidden.</summary>
    [ObservableProperty] private string? _codeError;

    public bool CanRedeemCode => !IsRedeemingCode;

    /// <summary>Redeem the Discord bot code for a session (no browser needed).</summary>
    [RelayCommand]
    private async Task SignInWithCodeAsync()
    {
        string code = CodeInput.Trim();
        if (code.Length != 6) return;

        IsRedeemingCode = true;
        CodeError = null;
        try
        {
            await _auth.SignInWithCodeAsync(code);
            CodeInput = "";
        }
        catch (Exception ex)
        {
            CodeError = ex.Message;
        }
        finally
        {
            IsRedeemingCode = false;
        }
    }

    [RelayCommand]
    private void OverrideSteamFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = Resources.Strings.Settings_ChooseSteamFolder,
            InitialDirectory = _steam.EffectivePath ?? "",
        };
        if (dialog.ShowDialog() == true)
        {
            _settings.SteamPathOverride = dialog.FolderName;
            RefreshSteam();
        }
    }

    [RelayCommand]
    private void ClearSteamOverride()
    {
        _settings.SteamPathOverride = null;
        RefreshSteam();
    }

    [RelayCommand]
    private void OpenSteamFolder()
    {
        string? path = _steam.EffectivePath;
        if (path is not null && System.IO.Directory.Exists(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    [RelayCommand]
    private void OpenWebsite() =>
        Process.Start(new ProcessStartInfo(AppConfig.ApiBaseUrl) { UseShellExecute = true });

    [RelayCommand]
    private void OpenHubcap() =>
        Process.Start(new ProcessStartInfo(AppConfig.HubcapBaseUrl) { UseShellExecute = true });

    [RelayCommand]
    private void SignOut() => _auth.SignOut();

    // ── Hubcap key management ───────────────────────────────────────

    /// <summary>Called by the View when loaded — auto-refreshes stats if a key is saved.</summary>
    public void OnViewLoaded()
    {
        if (HubcapIsKeyConfigured)
            RefreshHubcapStatsCommand.Execute(null);
        // Re-sync FastFetch in case the Add screen's toggle changed it this session (both are singletons
        // that only read the setting at construction). No-op if unchanged; a real change writes back the
        // same value, so no feedback loop.
        FastFetch = _settings.FastFetch;
    }

    /// <summary>Re-fetch usage stats for the saved key. Silent no-op if no key is saved.</summary>
    [RelayCommand]
    private async Task RefreshHubcapStatsAsync(CancellationToken ct)
    {
        string? key = _settings.HubcapApiKey;
        if (string.IsNullOrEmpty(key)) return;

        IsRefreshingHubcapStats = true;
        try
        {
            // A refresh that fails leaves the previous numbers up rather than blanking the card: stale
            // usage is more useful than none, and the card is the only content once a key is configured.
            var result = await _hubcap.GetStatsAsync(key, ct);
            if (result is HubcapResult<HubcapStats>.Ok ok) HubcapStats = ok.Value;
        }
        catch (OperationCanceledException)
        {
            // Command re-run or cancelled — keep whatever stats are on screen rather than blanking them.
        }
        catch
        {
            HubcapStats = null;
        }
        finally
        {
            IsRefreshingHubcapStats = false;
        }
    }

    /// <summary>Validate the typed key (format first, then a live stats call) and, if good, save it.</summary>
    [RelayCommand]
    private async Task ValidateAndSaveHubcapKeyAsync(CancellationToken ct)
    {
        string key = HubcapKeyInput.Trim();

        if (!HubcapService.IsValidKeyFormat(key))
        {
            ShowHubcapStatus(Resources.Strings.Settings_HubcapKeyBad, isError: true);
            return;
        }

        IsValidatingHubcapKey = true;
        try
        {
            var result = await _hubcap.GetStatsAsync(key, ct);
            if (result is not HubcapResult<HubcapStats>.Ok ok)
            {
                // The reason the result type exists. Saying "invalid key" for what was actually a dropped
                // connection is what made users delete a working credential and go hunting for a new one.
                ShowHubcapStatus(DescribeKeyFailure(result), isError: true);
                return;
            }

            _settings.HubcapApiKey = key;
            HubcapIsKeyConfigured = true;
            HubcapKeyInput = "";
            HubcapKeyStatus = null;
            HubcapStats = ok.Value;
        }
        catch (OperationCanceledException)
        {
            // Cancelled mid-validation — nothing was saved, so just drop back to the input with no message.
        }
        finally
        {
            IsValidatingHubcapKey = false;
        }
    }

    /// <summary>Forget the saved key and reset the status line.</summary>
    [RelayCommand]
    private void ClearHubcapKey()
    {
        _settings.HubcapApiKey = null;
        // Availability answers were given for the key being removed. Leaving them cached would let the
        // next key — or no key at all — inherit the previous one's view of what's downloadable.
        _hubcap.ClearStatusCache();
        HubcapIsKeyConfigured = false;
        HubcapKeyInput = "";
        HubcapKeyStatus = null;
        HubcapStats = null;
    }

    /// <summary>Why a key check failed, in the user's language. Only reached for non-Ok results, so the
    /// Ok arm is unreachable rather than merely unused — hence its throw.</summary>
    private static string DescribeKeyFailure(HubcapResult<HubcapStats> result) => result switch
    {
        HubcapResult<HubcapStats>.Unauthorized => Resources.Strings.Settings_HubcapKeyRejected,
        HubcapResult<HubcapStats>.Offline => Resources.Strings.Settings_HubcapKeyOffline,
        HubcapResult<HubcapStats>.RateLimited => Resources.Strings.Settings_HubcapKeyRateLimited,
        // NotFound and Failed both mean Hubcap answered but not with stats — the key itself is not
        // implicated, so the wording stays the old non-committal one rather than blaming it.
        _ => Resources.Strings.Settings_HubcapKeyError,
    };

    private void ShowHubcapStatus(string text, bool isError)
    {
        HubcapKeyStatus = text;
        HubcapKeyStatusColor = isError ? "DangerBrush" : "SuccessBrush";
    }

    /// <summary>Wording for a key that is expired or nearly so. Only reached for <c>IsWarning</c> states,
    /// so the "plenty of time left" case has no arm here rather than an unused one.</summary>
    private static string DescribeExpiry(HubcapKeyExpiry expiry) => expiry switch
    {
        { HasExpired: true } => string.Format(Resources.Strings.Settings_HubcapExpiryExpired, expiry.ExpiresOn),
        // Under 24 hours left. "expires in 0 days" is what the plural arm would produce, which is why this
        // is a separate string rather than a special case of it.
        { DaysRemaining: 0 } => string.Format(Resources.Strings.Settings_HubcapExpiryToday, expiry.ExpiresOn),
        _ => string.Format(Resources.Strings.Settings_HubcapExpirySoon, expiry.DaysRemaining, expiry.ExpiresOn),
    };

    /// <summary>"4/10 today" — appends "· expires yyyy-MM-dd" when the key has an expiry.
    ///
    /// <para>
    /// Goes through <see cref="HubcapKeyExpiry"/> rather than parsing the timestamp itself. It used to call
    /// <c>DateTimeOffset.TryParse</c> with default styles on a value Hubcap sends without an offset, so the
    /// date rendered here could differ by a day from the date the warning row is reasoning about.
    /// </para>
    /// </summary>
    private static string FormatHubcapStats(HubcapStats stats) =>
        HubcapKeyExpiry.Evaluate(stats.ApiKeyExpiresAt, DateTimeOffset.UtcNow) is { } expiry
            ? string.Format(Resources.Strings.Settings_HubcapKeyOkExpiry,
                stats.DailyUsage, stats.DailyLimit, expiry.ExpiresOn)
            : string.Format(Resources.Strings.Settings_HubcapKeyOk, stats.DailyUsage, stats.DailyLimit);
}
