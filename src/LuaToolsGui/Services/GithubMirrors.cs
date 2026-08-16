namespace LuaToolsGui.Services;

/// <summary>
/// The GitHub mirror lists actually in effect, which may be the compiled-in defaults from
/// <see cref="AppConfig"/> or a user override from settings.json.
///
/// <para>
/// The defaults are public third-party proxies (ghproxy.net, ghfast.top, gh.ddlc.top). They exist so the
/// app keeps working where github.com is blocked, but they are anonymous operators who see — and could
/// alter — every byte routed through them. Hash verification is what makes that survivable, and it is why
/// a user who does not want those operators in their supply chain needs a way to say so without rebuilding
/// the app.
/// </para>
///
/// <para>
/// Configure by editing <c>%AppData%\LuaToolsGui\settings.json</c>:
/// <code>
///   "GithubDownloadMirrors": []                              // disable mirrors entirely (direct only)
///   "GithubDownloadMirrors": ["https://my-proxy.example/"]   // use your own
/// </code>
/// Omit a key (or set it to null) to keep the built-in default. Each entry is a PREFIX: the full GitHub URL
/// is appended to it, so it must end with "/".
/// </para>
///
/// <para>
/// Held statically because <see cref="GithubProxy.Candidates"/> is static — Velopack's
/// <see cref="ProxiedFileDownloader"/> calls it from inside the updater, where there is no DI scope to
/// resolve settings from. <see cref="SettingsService"/> pushes the effective lists here once at startup.
/// Reads are lock-free: reference assignment of the array is atomic, and callers only enumerate.
/// </para>
/// </summary>
internal static class GithubMirrors
{
    private static string[] _download = AppConfig.GithubDownloadMirrors;
    private static string[] _api = AppConfig.GithubApiMirrors;

    /// <summary>Mirrors for release-asset binaries (github.com / raw / objects).</summary>
    public static IReadOnlyList<string> Download => _download;

    /// <summary>Mirrors for api.github.com metadata.</summary>
    public static IReadOnlyList<string> Api => _api;

    /// <summary>
    /// Apply user overrides. A null list keeps the compiled-in default; an empty list means "no mirrors,
    /// direct only" and is honoured as such. Entries that aren't absolute https URLs are dropped rather
    /// than trusted, so a typo degrades to fewer mirrors instead of an unexpected destination.
    /// </summary>
    public static void Configure(IReadOnlyList<string>? download, IReadOnlyList<string>? api)
    {
        if (download is not null) _download = Sanitize(download);
        if (api is not null) _api = Sanitize(api);
    }

    private static string[] Sanitize(IReadOnlyList<string> mirrors)
    {
        var result = new List<string>(mirrors.Count);
        foreach (string mirror in mirrors)
        {
            if (string.IsNullOrWhiteSpace(mirror)) continue;

            string trimmed = mirror.Trim();
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) continue;
            if (uri.Scheme != Uri.UriSchemeHttps) continue; // a plain-http mirror is modifiable in transit

            // Candidates() concatenates "<mirror><full github url>", so a missing slash silently produces
            // a malformed URL that just fails every time.
            result.Add(trimmed.EndsWith('/') ? trimmed : trimmed + "/");
        }
        return [.. result];
    }
}
