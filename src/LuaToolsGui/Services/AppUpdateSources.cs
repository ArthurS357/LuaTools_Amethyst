namespace LuaToolsGui.Services;

/// <summary>Why a configured update repo was refused. Ordered from "typo" to "this would undo the fork".</summary>
public enum UpdateSourceRejection
{
    /// <summary>Blank or whitespace entry.</summary>
    Empty,

    /// <summary>Not an absolute URL, or not <c>https</c>.</summary>
    NotHttps,

    /// <summary>Host is not github.com.</summary>
    NotGitHub,

    /// <summary>Not an <c>owner/repo</c> path.</summary>
    MalformedPath,

    /// <summary>Points at a repo that publishes the OFFICIAL build — see <see cref="AppConfig.UpstreamReleaseRepos"/>.</summary>
    UpstreamRepo,
}

/// <summary>A configured entry that was refused, with the reason.</summary>
/// <param name="Value">The entry exactly as it appeared in settings.json.</param>
/// <param name="Reason">Why it was refused.</param>
public readonly record struct RejectedUpdateSource(string Value, UpdateSourceRejection Reason)
{
    /// <summary>One-line explanation for the log. Says what to do, not just what was wrong.</summary>
    public string Describe() => Reason switch
    {
        UpdateSourceRejection.Empty => $"'{Value}': empty entry",
        UpdateSourceRejection.NotHttps => $"'{Value}': must be an absolute https:// URL",
        UpdateSourceRejection.NotGitHub => $"'{Value}': only github.com repos are supported",
        UpdateSourceRejection.MalformedPath => $"'{Value}': expected https://github.com/<owner>/<repo>",
        UpdateSourceRejection.UpstreamRepo =>
            $"'{Value}': REFUSED — this is an official LuaTools release repo. Installing from it would " +
            "restore Umami telemetry and the DonateKeys decryption-key upload that this fork removes.",
        _ => $"'{Value}': rejected",
    };
}

/// <summary>The outcome of resolving the configured update feed.</summary>
/// <param name="Repos">Repo URLs that passed validation, in the configured priority order.</param>
/// <param name="Rejected">Entries that were refused, with reasons. Callers should log these.</param>
public sealed record UpdateSourceResolution(IReadOnlyList<string> Repos, IReadOnlyList<RejectedUpdateSource> Rejected)
{
    /// <summary>True when no usable repo survived — self-update is off and no request will be made.</summary>
    public bool IsDisabled => Repos.Count == 0;
}

/// <summary>
/// Turns the user's <c>AppUpdateRepos</c> setting into the list of feeds the auto-updater may use.
///
/// <para>
/// Kept as a pure static function, separate from <see cref="UpdateService"/>, for two reasons. First,
/// this is the security boundary for the whole fork: it is the single place that decides whether the app
/// may download a build of itself, and the one thing standing between a mistyped config and a silent
/// reinstatement of telemetry + the DonateKeys key upload. That deserves to be exhaustively testable
/// without Velopack, network, or a settings file. Second, <see cref="UpdateService"/> is only concerned
/// with orchestrating Velopack; validating URLs is a different job (see csharp-architecture-api on keeping
/// policy out of the service that performs the I/O).
/// </para>
///
/// <para>
/// The default is EMPTY = disabled. An unconfigured fork build contacts nothing.
/// </para>
/// </summary>
public static class AppUpdateSources
{
    /// <summary>
    /// Validate and filter configured update repos.
    /// </summary>
    /// <param name="configured">
    /// Entries from settings.json (<c>AppUpdateRepos</c>), or null when unset. Null and empty both mean
    /// "self-update disabled".
    /// </param>
    /// <param name="blockedRepos">
    /// <c>owner/repo</c> identifiers to refuse. Defaults to <see cref="AppConfig.UpstreamReleaseRepos"/>;
    /// injectable so tests don't depend on the shipped list.
    /// </param>
    public static UpdateSourceResolution Resolve(
        IEnumerable<string>? configured, IEnumerable<string>? blockedRepos = null)
    {
        var accepted = new List<string>();
        var rejected = new List<RejectedUpdateSource>();

        if (configured is null) return new UpdateSourceResolution(accepted, rejected);

        var blocked = new HashSet<string>(
            blockedRepos ?? AppConfig.UpstreamReleaseRepos, StringComparer.OrdinalIgnoreCase);

        foreach (string entry in configured)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                rejected.Add(new(entry ?? string.Empty, UpdateSourceRejection.Empty));
                continue;
            }

            string trimmed = entry.Trim();

            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
                !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                // http:// is refused too, not just non-URLs: an update feed decides which executable
                // replaces this one, so it is never allowed over a transport that can be rewritten.
                rejected.Add(new(trimmed, UpdateSourceRejection.NotHttps));
                continue;
            }

            if (!IsGitHubHost(uri.Host))
            {
                rejected.Add(new(trimmed, UpdateSourceRejection.NotGitHub));
                continue;
            }

            if (TryParseOwnerRepo(uri, out string ownerRepo) is false)
            {
                rejected.Add(new(trimmed, UpdateSourceRejection.MalformedPath));
                continue;
            }

            if (blocked.Contains(ownerRepo))
            {
                rejected.Add(new(trimmed, UpdateSourceRejection.UpstreamRepo));
                continue;
            }

            // Normalised form (no trailing slash, no .git) — Velopack's GithubSource wants the repo URL.
            string normalised = $"https://github.com/{ownerRepo}";
            if (!accepted.Contains(normalised, StringComparer.OrdinalIgnoreCase))
                accepted.Add(normalised);
        }

        return new UpdateSourceResolution(accepted, rejected);
    }

    private static bool IsGitHubHost(string host) =>
        host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Extract <c>owner/repo</c> from a GitHub URL, tolerating a trailing slash and a <c>.git</c> suffix.
    /// Normalising before the blocklist check is what stops
    /// <c>https://github.com/madoiscool/LuaTools.git/</c> from sneaking past a plain string compare.
    /// </summary>
    private static bool TryParseOwnerRepo(Uri uri, out string ownerRepo)
    {
        ownerRepo = string.Empty;

        string[] segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2) return false;

        string owner = segments[0];
        string repo = segments[1];
        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            repo = repo[..^4];

        if (owner.Length == 0 || repo.Length == 0) return false;

        ownerRepo = $"{owner}/{repo}";
        return true;
    }
}
