using System.IO;
using System.Net.Http;

namespace LuaToolsGui.Services;

/// <summary>
/// Makes GitHub requests resilient in regions where github.com / api.github.com are blocked or throttled
/// (e.g. China). Every GitHub request — API JSON or a release-asset binary — is tried DIRECT first, and
/// on failure (network error or non-success) re-tried through each capability-matched mirror (see
/// <see cref="AppConfig.GithubApiMirrors"/> / <see cref="AppConfig.GithubDownloadMirrors"/>) by prefixing
/// the full GitHub URL: "<mirror>https://github.com/...". The first usable response wins.
///
/// USE THIS FOR ALL GITHUB REQUESTS. Don't call api.github.com / github.com directly — route the URL
/// through <see cref="SendAsync"/> (API/metadata) or <see cref="DownloadAsync"/> (asset binaries) so the
/// mirror fallback applies everywhere automatically.
/// </summary>
public class GithubProxy
{
    // 5-min timeout matches the old UnlockerService client (large asset downloads on slow mirrors).
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };

    /// <summary>Hosts a download may originate from. HTTPS only — a plain-http asset URL would be
    /// modifiable in transit regardless of any later hash check.</summary>
    private static readonly string[] TrustedDownloadHosts =
    [
        "github.com",
        "api.github.com",
        "raw.githubusercontent.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com", // where github.com release links currently redirect
    ];

    /// <summary>
    /// Whether a URL is one we are willing to fetch binaries from.
    ///
    /// <para>
    /// This closes the case where release METADATA and the binary come from the same place. Asset URLs are
    /// read out of the release JSON, and that JSON can itself be served by a mirror
    /// (<see cref="AppConfig.GithubApiMirrors"/>) when api.github.com is unreachable. Without this check a
    /// mirrored response could point <c>browser_download_url</c> at any host of its choosing AND supply the
    /// matching <c>digest</c>, so hash verification would compare the attacker's file against the
    /// attacker's hash and pass.
    /// </para>
    ///
    /// <para>
    /// This is a HOST check only, and a host check alone is not sufficient for anything whose bytes get
    /// executed or loaded: every repository on github.com shares this host, so a hostile metadata source
    /// can still name a repository of its choosing and supply that payload's digest. Callers placing a
    /// binary must use <see cref="IsAssetUrlForRepo"/> / <see cref="DownloadAssetAsync"/> instead, which
    /// additionally pins the owner and repository.
    /// </para>
    /// </summary>
    public static bool IsTrustedDownloadUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;

        foreach (string host in TrustedDownloadHosts)
            if (string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    /// <summary>
    /// Whether <paramref name="url"/> is a release-asset link belonging to
    /// <paramref name="owner"/>/<paramref name="repo"/> — the repository the metadata was fetched from.
    ///
    /// <para>
    /// <see cref="IsTrustedDownloadUrl"/> pins the HOST, which is not enough on its own.
    /// <c>browser_download_url</c> and <c>digest</c> are read out of the SAME release JSON, and that JSON
    /// can be served by an API mirror. A hostile mirror therefore only has to name a DIFFERENT github.com
    /// repository and supply that payload's hash: the host check passes, the hash check passes, and the
    /// bytes it chose are copied into the Steam root where steam.exe loads them (or, for the CloudRedirect
    /// CLI, executed outright). Pinning owner/repo removes the mirror's ability to choose the repository,
    /// leaving it able only to pick among releases the real project actually published.
    /// </para>
    ///
    /// <para>
    /// Only <c>github.com/{owner}/{repo}/releases/download/…</c> is accepted — the exact shape the GitHub
    /// API emits for <c>browser_download_url</c>. The <c>objects</c>/<c>release-assets</c> hosts carry no
    /// owner in their path, but they only ever appear as REDIRECT targets that HttpClient follows on its
    /// own, never as a URL handed to this method, so refusing them here costs nothing. Path segments are
    /// compared raw: GitHub owner and repository names are drawn from <c>[A-Za-z0-9._-]</c> and are never
    /// percent-encoded in a legitimate URL, so decoding first would only add a way to smuggle one name past
    /// as another.
    /// </para>
    /// </summary>
    public static bool IsAssetUrlForRepo(string url, string owner, string repo)
    {
        if (!IsTrustedDownloadUrl(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)) return false;

        string[] segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 5
            && segments[0].Equals(owner, StringComparison.OrdinalIgnoreCase)
            && segments[1].Equals(repo, StringComparison.OrdinalIgnoreCase)
            && segments[2].Equals("releases", StringComparison.OrdinalIgnoreCase)
            && segments[3].Equals("download", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Only github.com / api.github.com URLs get the mirror treatment. Anything else is left as-is.</summary>
    private static bool IsGithub(string url) => IsTrustedDownloadUrl(url);

    /// <summary>Candidate URLs to try in order: the direct URL, then each CAPABILITY-MATCHED mirror-prefixed
    /// variant. api.github.com URLs get the API mirror(s); everything else (github.com downloads, raw,
    /// objects) gets the download mirror(s) — never the wrong class, which would be a guaranteed-wasted hop
    /// (an API mirror 400s a download; a download mirror 403s the API). Public so the Velopack auto-update
    /// downloader can reuse the same direct→mirror fallback.</summary>
    public static IEnumerable<string> Candidates(string url)
    {
        yield return url; // direct first — fastest when GitHub is reachable
        if (!IsGithub(url)) yield break;
        // Effective lists, so a user who overrode (or disabled) the third-party mirrors in settings.json
        // is honoured here. Defaults come from AppConfig — see GithubMirrors.
        var mirrors = url.StartsWith("https://api.github.com/", StringComparison.OrdinalIgnoreCase)
            ? GithubMirrors.Api
            : GithubMirrors.Download;
        foreach (var mirror in mirrors)
            yield return mirror + url;
    }

    /// <summary>
    /// Send a GET (with the standard GitHub headers) trying direct then mirrors. Returns the first
    /// SUCCESSFUL response (caller owns/disposes it), or null if every candidate failed. Use for the
    /// GitHub API (releases JSON, etc.).
    /// </summary>
    public async Task<HttpResponseMessage?> SendAsync(string url, CancellationToken ct = default)
    {
        HttpResponseMessage? lastResponse = null;
        foreach (var candidate in Candidates(url))
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, candidate);
                req.Headers.TryAddWithoutValidation("User-Agent", "LuaToolsGui");
                req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
                var res = await _http.SendAsync(req, ct);
                if (res.IsSuccessStatusCode)
                {
                    // A held-onto failure from an earlier candidate is now dead weight — dispose it here
                    // too, not only on the next failure. Returning early used to leak it (and its pooled
                    // connection) on every request where a mirror succeeded after a direct failure.
                    lastResponse?.Dispose();
                    return res;
                }
                lastResponse?.Dispose();
                lastResponse = res; // remember the last non-success in case all fail (for status info)
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // user cancellation, not a mirror failure — don't keep trying
            }
            catch
            {
                // network error for this candidate — fall through to the next mirror
            }
        }
        return lastResponse; // null if every attempt threw; else the last failed response
    }

    /// <summary>
    /// Download a (GitHub) URL to a file, trying direct then mirrors, streaming with progress. Throws if
    /// every candidate fails. Use for release-asset binaries (.dll/.exe/.zip).
    /// </summary>
    public async Task DownloadAsync(string url, string destPath, IProgress<double?>? progress, CancellationToken ct = default)
    {
        // Refuse before a single byte is fetched. Every caller passes an asset URL taken from release
        // metadata, and that metadata is not always first-party — see IsTrustedDownloadUrl.
        if (!IsTrustedDownloadUrl(url))
            throw new HttpRequestException(
                $"Refusing to download from an untrusted host: {Redact(url)}. Asset downloads are " +
                "restricted to GitHub-owned hosts.");

        Exception? last = null;
        foreach (var candidate in Candidates(url))
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, candidate);
                req.Headers.TryAddWithoutValidation("User-Agent", "LuaToolsGui");
                using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                res.EnsureSuccessStatusCode();

                long? total = res.Content.Headers.ContentLength;
                await using var src = await res.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(destPath);

                var buffer = new byte[81920];
                long written = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                    written += read;
                    progress?.Report(total is > 0 ? (double)written / total.Value : null);
                }
                return; // success
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                try { File.Delete(destPath); } catch { /* partial file from a failed mirror */ }
            }
        }
        throw last ?? new HttpRequestException($"Could not download {url} from GitHub or any mirror.");
    }

    /// <summary>
    /// Download a release asset, refusing before a byte is fetched unless the URL belongs to
    /// <paramref name="owner"/>/<paramref name="repo"/>. Use this — not <see cref="DownloadAsync"/> — for
    /// every asset whose bytes are executed, or placed where another program will load them.
    /// </summary>
    public Task DownloadAssetAsync(string url, string owner, string repo, string destPath,
        IProgress<double?>? progress, CancellationToken ct = default)
    {
        if (!IsAssetUrlForRepo(url, owner, repo))
            throw new HttpRequestException(
                $"Refusing to download {Redact(url)}: the release metadata pointed somewhere other than " +
                $"{owner}/{repo}'s own release assets.");

        return DownloadAsync(url, destPath, progress, ct);
    }

    /// <summary>Scheme + host only, for error text. A rejected URL is attacker-influenced, so its path and
    /// query don't belong in a message that may be logged or shown.</summary>
    private static string Redact(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? $"{uri.Scheme}://{uri.Host}" : "(unparseable URL)";
}
