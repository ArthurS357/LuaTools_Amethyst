namespace LuaToolsGui.Resources;

/// <summary>One released version and what changed in it.</summary>
/// <param name="Version">Bare version, e.g. "1.5.1" — matches <c>AppVersion.Current</c> for this build.</param>
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
