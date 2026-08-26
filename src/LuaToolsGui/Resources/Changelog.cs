namespace LuaToolsGui.Resources;

/// <summary>One released version and what changed in it.</summary>
/// <param name="Version">Bare version, e.g. "1.5.4" — matches <c>AppVersion.Current</c> for this build.</param>
/// <param name="Released">Release date, ISO-formatted for display.</param>
/// <param name="Summary">One line on the theme of the release.</param>
/// <param name="Highlights">The handful of changes worth surfacing in-app.</param>
public sealed record ChangelogEntry(
    string Version, string Released, string Summary, IReadOnlyList<string> Highlights);

/// <summary>
/// The in-app changelog shown on the About page.
///
/// <para>
/// Compiled in rather than read from <c>docs/CHANGELOG.md</c> or fetched at runtime. The markdown file is
/// the full engineering record and is not guaranteed to ship next to the binary; the network is not
/// available to a user who is offline, behind a proxy, or simply checking what they just installed. A
/// static list has neither failure mode — there is nothing to parse, nothing to download, and nothing
/// that can render as an error where a version history should be.
/// </para>
///
/// <para>
/// Deliberately a SUMMARY: a few lines per release, newest first. The exhaustive per-change record stays
/// in docs/CHANGELOG.md, which this must not try to replace.
/// </para>
/// </summary>
public static class Changelog
{
    /// <summary>Newest first. Keep in step with docs/CHANGELOG.md and the csproj &lt;Version&gt;.</summary>
    public static IReadOnlyList<ChangelogEntry> Entries { get; } =
    [
        new("1.6.0", "2026-08-26",
            "One backend can be active at a time, AmethystTool leads the Mode list, and SteamTools is retired.",
            [
                "Fixed AmethystTool and a Mode both showing ACTIVE at once — installing one now always demotes the other, from a single stored slot instead of two flags that could disagree.",
                "AmethystTool is now the first card on the Mode page, with a description explaining what the fork actually does: auto-update off, nothing reported back.",
                "SteamTools is retired from the Mode page — upstream stopped updating it. Anyone still running it keeps their card and their Uninstall button.",
                "Installing AmethystTool over an old Mode now cleans up that Mode's stale claim on the shared proxy DLLs, so uninstalling AmethystTool no longer reports files as \"still needed\" when they are not.",
            ]),

        new("1.5.4", "2026-08-22",
            "Play a game straight from Manage, and the whole app moves to .NET 10 LTS.",
            [
                "Every game in Manage now has a Play button. If the game is not on disk it says Install and opens Steam's download for it instead.",
                "Steam is started for you when it is not already up, and the game is only handed over once it can accept it.",
                "Moved to .NET 10 LTS, supported until November 2028 — .NET 8 stops getting security fixes in November 2026.",
                "Internal: the whole solution is checked by `dotnet format` in CI, and code signing for releases is documented and wired up.",
            ]),

        new("1.5.2", "2026-08-19",
            "Closing the window sends LuaTools to the tray instead of killing it, plus the follow-ups that were outstanding.",
            [
                "The window's X now hides LuaTools in the system tray and leaves it running — the Steam plugin's local bridge no longer dies with the window.",
                "Quit from the tray icon's Exit; double-click or Open restores the window. Turn the whole behaviour off under Settings → Startup.",
                "A silent install triggered by a web page still exits when it is done, so it leaves no process behind you never started.",
                "Hubcap no longer refuses a key by naming a key format it does not actually require.",
                "The Home greeting and the navigation rail now name this build correctly and read every label from the translations.",
                "Internal: the tray rule moved out of the window into a tested service, and API models are immutable once parsed.",
            ]),

        new("1.5.1", "2026-08-18",
            "A launch sequence that explains itself, a working Discord sign-in, and an accent switch that repaints.",
            [
                "Startup is a sequence now: Steam closes first, setup runs only if there is any, then you are offered Steam back.",
                "Steam is asked to close before it is forced, so the client gets the clean shutdown it expects.",
                "Fixed Discord sign-in: the app sent an OAuth parameter that broke the redirect back, so it always timed out.",
                "Accent colour applies when you choose Apply, and the switch now actually repaints — every palette brush was frozen.",
                "A colour retints the whole app — window, cards, borders and text — not only the highlights.",
                "Games can be removed from the Depots list, clearing their lua files and stored builds.",
                "Hubcap: a warning before the key expires, a masked key field, and a looser key-format check.",
            ]),

        new("1.5.0", "2026-08-18",
            "Hubcap integration follow-ups, plus a choice of accent colour.",
            [
                "Accent colour is now selectable: Amethyst, Green or Red, applied without a restart.",
                "Hubcap failure messages are translatable instead of hardcoded English.",
                "Outbound requests identify the app and retire pooled connections, so DNS changes are picked up.",
                "This changelog, readable from the About page.",
            ]),

        new("1.4.0", "2026-08-16",
            "Post-audit hardening. No new features; the theme is removing attack surface.",
            [
                "Store-page plugin auto-update is now OFF by default — it replaced a DLL in Steam's root without asking.",
                "Pre-install disclosure showing source, version, hash and which checks passed.",
                "Native directory junctions, replacing the shell-out that could be redirected.",
                "The Hubcap API key is stored DPAPI-protected rather than as plain text.",
            ]),

        new("1.3.0", "2026-08-16",
            "The Amethyst fork: private build, own identity, telemetry removed.",
            [
                "Renamed to LuaTools Amethyst, with an About page describing what this build is.",
                "Amethyst palette replacing ~376 scattered hex literals, checked for WCAG contrast.",
                "Telemetry and the DonateKeys upload removed.",
                "A startup guard that says so when the theme fails to apply.",
            ]),
    ];
}
