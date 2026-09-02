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
        new("1.7.2", "2026-09-02",
            "Finishes the accent fix 1.7.1 only half made: the colour now reaches the buttons, toggles and accent text inside the pages, not just the window around them.",
            [
                "Choosing an accent now repaints the accent-coloured controls in the middle of the window. The primary buttons, toggles, focus rings and accent text kept whichever colour was active when their page first loaded, so picking Red gave a wine window and rail with violet buttons still sitting inside it.",
                "The cause was that those controls are painted by the UI library, which REPLACES its accent brushes on every change instead of recolouring them. Anything already on screen went on using the brush it had picked up at load. The app now owns those brushes and recolours them in place, the same way it already did for every surface.",
                "It still takes effect immediately, with no restart, and picking the same accent twice does nothing visible - the second switch used to be the one that quietly stopped working.",
                "Body text, fixed in 1.7.1, is unchanged. This is the other half of the same seam.",
            ]),

        new("1.7.1", "2026-09-02",
            "The accent now reaches the text inside the pages, not just the rail around them, and DLC unlocks go through the same download queue as everything else.",
            [
                "Switching accent repaints the text in the central container. Every page sets its body text from a WPF-UI key that shipped as untokenised pure white, so the rail followed the accent while the pages inside it stayed stock white — the seam that read as \"the accent stopped applying\". That key is now part of the theme like every other colour.",
                "A DLC unlock now appears on the Downloads page. It was the last path that still downloaded and installed inline, so it was invisible from the queue and could run at the same time as a manifest install writing the same file for that game. The two now exclude each other.",
                "The Add page no longer draws its own bar under a source. It went blank the moment the queue took over the bytes, so it was a second, emptier copy of the real one; the row now reads \"Queued\" and links to the Downloads page, where the size, speed and controls actually are.",
                "Removed the download-and-install code left stranded by that move, including a field that was written and never read again — a build failure waiting for the next warning sweep.",
            ]),

        new("1.7.0", "2026-08-30",
            "Downloads are now one queue with its own page: manifests and depot content share it, they survive leaving the page they were started from, and finished ones are kept in a history.",
            [
                "New Downloads page in the nav rail. Every download the app runs shows up there with its size, speed and time remaining, and can be cancelled or retried from one place. Previously each page drew its own bar and a download that was started on one page was invisible from every other.",
                "Depot downloads can be paused and resumed. They are the only kind whose partial work survives being interrupted — the bytes are already on disk — so Resume picks up from the first depot that had not finished instead of re-fetching tens of GB.",
                "A download no longer dies when you navigate away. The Depots page hands its selection to the queue and says so, rather than holding a multi-hour transfer open inside the page's own command.",
                "Finished downloads are kept in a history that survives a restart, with per-row and bulk clearing. It is stored in cache.json, so nothing about the settings file changes; failure messages are sanitized on the way in, the same as the crash log.",
                "The Add page, the Steam store plugin and the store-page bridge now share one download-and-install path. All three had their own copy of it, and they had drifted — the same download reported its result differently depending on where it was started.",
                "Starting the same game twice joins the download already running instead of racing it. That check used to be per-page, so the app window and the store plugin could each start one over the same file.",
            ]),
        new("1.6.3", "2026-08-30",
            "Installing AmethystTool no longer leaves BetterSteamTools' engine hooked into Steam beside it, and the Mode page stops claiming the wrong backend is the installed one.",
            [
                "Installing AmethystTool over BetterSteamTools now moves that tool's OpenSteamTool.dll and opensteamtool.toml into the backup folder first. They were the one pair the install did not overwrite, and the forked loader could still pick the DLL up — leaving two engines hooked into one Steam. They are MOVED, not deleted, and the card names the folder they went to.",
                "Installing a Mode over AmethystTool no longer leaves the AmethystTool card reporting \"up to date\" next to the Mode card holding the ACTIVE badge. Two of the four files are shared, so their names alone could never tell the two apart; the card now reads the one slot that says who actually owns the proxy DLLs.",
                "The Mode cards no longer read as installed while AmethystTool holds those DLLs, which used to offer an Update button that would silently hand the slot back.",
                "First-run detection now abstains on a Steam folder that is AmethystTool's rather than adopting BetterSteamTools. The fork's proxy DLLs can be byte-identical to the ones it forked from, so with no choice stored yet the hash check put the ACTIVE badge on the wrong card.",
                "AmethystTool's Uninstall button stays available when a Mode has taken the slot, so its two leftover files can still be removed from inside the app.",
                "The Plugin page's 20 source-picker strings are registered as awaiting translation, so the language check reports them instead of failing CI.",
            ]),

        new("1.6.2", "2026-08-27",
            "The Plugin page now lists every creator who publishes the plugin and lets you pick which one is installed — and never switches for you.",
            [
                "The Plugin page shows one card per creator, laid out like the Mode page's cards, with the active one outlined. Press \"Use this source\" to switch; it runs a full install of that repository and only records your choice once that has succeeded.",
                "There is no automatic fallback any more. If the source you are on publishes nothing installable, the page says exactly why and the install fails — it does not quietly install the other creator's build instead.",
                "Each source is checked on its own and fail-closed: its release must carry every asset, point each one at its own repository, and publish a SHA-256 for it. One creator's hashes are never used to judge another's files.",
                "Anyone already installed from madoiscool/LTSP stays there. The fork's own frontend is only the default for a fresh install, so an app update never moves you.",
                "The loader row no longer reports \"Up to date\" or \"Out of date\" when it could not reach a release to compare against — offline, or a broken source, now reads as simply \"Installed\".",
            ]),

        new("1.6.1", "2026-08-27",
            "A fix for a manifest deadlock between backends, a Home page that finally shows what state everything is in, and more translations.",
            [
                "Fixed a case where AmethystTool and a Mode could each end up claiming the same proxy DLLs in the install record, which made Uninstall refuse to remove them from either. Recording an install now always takes those names off whichever entry held them before.",
                "Home now shows whether Steam is open or closed, which backend actually holds the proxy DLLs (including AmethystTool, which used to read as \"no mode selected\"), this build's version, and whether self-update is on — with a Refresh button and quick actions to Mode, Plugin and About.",
                "The store-page plugin's install record now goes through the same exclusive-claim write as the other two backends, for consistency; its files never overlapped so nothing about it actually changes.",
                "12 more strings translated across all 29 languages, plus two diacritics fixed in Polish and Romanian.",
            ]),

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
