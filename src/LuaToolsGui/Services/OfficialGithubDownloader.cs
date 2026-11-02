using System.IO;
using System.Text.Json;
using Velopack.Sources;

namespace LuaToolsGui.Services;

/// <summary>
/// Thrown when a downloaded update package cannot be proven to be what GitHub published for the release.
/// <see cref="UpdateService"/> treats it as final: the update is refused and no other repo is tried.
/// </summary>
public sealed class UpdateIntegrityException(string message) : Exception(message)
{
    /// <summary>The <see cref="UpdateIntegrityException"/> in <paramref name="ex"/>'s chain, if any. Velopack
    /// may wrap what a downloader throws, and an integrity refusal must not be demoted to "this repo is
    /// down, try the next one" just because it arrived inside another exception.</summary>
    public static UpdateIntegrityException? FindIn(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            if (ex is UpdateIntegrityException integrity) return integrity;
            if (ex is AggregateException agg)
                foreach (var inner in agg.InnerExceptions)
                    if (FindIn(inner) is { } found) return found;
        }
        return null;
    }
}

/// <summary>
/// The <see cref="IFileDownloader"/> the app's own auto-updater uses. It talks to the official GitHub
/// repository ONLY and refuses to install a package it cannot verify.
///
/// <para>
/// <b>No mirrors.</b> It used to fall through the third-party download mirrors (ghproxy, ghfast, ddlc)
/// that <see cref="GithubProxy"/> uses for regions that block github.com. That was the wrong trade for
/// this one download: the bytes are an executable that replaces the running app, and the only integrity
/// check was the package hash read out of the release feed — which the same mirror serves. A hostile hop
/// could rewrite the feed and the package together and both checks would agree. Self-update is now
/// direct-to-GitHub or nothing; where github.com is blocked, updating by hand from the release page is
/// still one click away (About page). Every other download in the app keeps its mirror fallback and its
/// own digest checks — this change is scoped to the app's feed and package.
/// </para>
///
/// <para>
/// <b>Hash pinning.</b> Before a package is accepted, its SHA-256 is compared with the <c>digest</c> GitHub
/// publishes for that release asset in the REST API (<c>api.github.com</c>, over TLS, direct). That value
/// is computed by GitHub when the asset is uploaded, so it does not depend on anything the feed or the CDN
/// said. The check is fail-closed, like <see cref="AssetIntegrity"/>: no digest, an unreachable API, an
/// asset the API does not list, or a mismatch all refuse the update, delete the file and log to
/// <see cref="AppLog"/>.
/// </para>
///
/// <para>
/// The downloader is built per repo and pinned to it: a URL naming another repository, another host, a
/// plain-http scheme or a mirror prefix is refused before any request is made.
/// </para>
/// </summary>
public sealed class OfficialGithubDownloader : IFileDownloader
{
    private const string ApiHost = "api.github.com";
    private const string WebHost = "github.com";

    private readonly IFileDownloader _inner;
    private readonly string _owner;
    private readonly string _repo;

    /// <param name="owner">Owner of the one repository this downloader may touch.</param>
    /// <param name="repo">Name of that repository.</param>
    /// <param name="inner">Transport. Defaults to Velopack's stock client; injectable for tests.</param>
    public OfficialGithubDownloader(string owner, string repo, IFileDownloader? inner = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);
        _owner = owner;
        _repo = repo;
        _inner = inner ?? new HttpClientFileDownloader();
    }

    // ── IFileDownloader ───────────────────────────────────────────────────────

    public async Task DownloadFile(string url, string targetFile, Action<int> progress,
        IDictionary<string, string>? headers = null, double timeout = 30, CancellationToken cancelToken = default)
    {
        bool downloadStarted = false;
        try
        {
            RequireOfficial(url);

            // Resolve what GitHub published BEFORE fetching the package: a release that carries no usable
            // digest is refused without spending the bandwidth.
            string expected = await ResolveDigestAsync(url, timeout, cancelToken).ConfigureAwait(false);

            downloadStarted = true;
            await _inner.DownloadFile(url, targetFile, progress, headers, timeout, cancelToken).ConfigureAwait(false);

            if (!AssetIntegrity.Matches(targetFile, "sha256:" + expected))
                throw new UpdateIntegrityException(
                    $"downloaded package does not match the SHA-256 GitHub published for it ({Describe(url)})");
        }
        catch (OperationCanceledException) when (cancelToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Fail-closed in every branch: whatever went wrong, nothing unverified is left for Velopack to
            // pick up. A transport failure (API or CDN unreachable) is rethrown as-is so UpdateService can
            // still fall through to a backup repo; UpdateIntegrityException means "refuse", full stop.
            if (downloadStarted) DeleteQuietly(targetFile);
            AppLog.Log($"app-update: REFUSED package {Describe(url)} — {ex.Message}");
            throw;
        }
    }

    public Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers = null, double timeout = 30)
    {
        RequireOfficial(url);
        return _inner.DownloadBytes(url, headers, timeout);
    }

    public Task<string> DownloadString(string url, IDictionary<string, string>? headers = null, double timeout = 30)
    {
        RequireOfficial(url);
        return _inner.DownloadString(url, headers, timeout);
    }

    // ── URL policy ────────────────────────────────────────────────────────────

    /// <summary>
    /// Refuse anything that is not an https URL on github.com / api.github.com naming this downloader's own
    /// repository. A mirror-prefixed URL fails on the host, so a stale mirror setting can never route the
    /// updater anywhere.
    /// </summary>
    private void RequireOfficial(string url)
    {
        if (!TryParse(url, out var kind, out var segments) || kind == Kind.Other)
            throw new UpdateIntegrityException(
                $"refusing non-official update URL: {GithubProxy.Redact(url)} (only https://github.com/{_owner}/{_repo} is allowed)");

        bool ours = kind switch
        {
            // https://api.github.com/repos/{owner}/{repo}/...
            Kind.Api => segments.Length >= 3 && segments[0].Equals("repos", StringComparison.OrdinalIgnoreCase)
                        && Is(segments[1], _owner) && Is(segments[2], _repo),
            // https://github.com/{owner}/{repo}/...
            _ => segments.Length >= 2 && Is(segments[0], _owner) && Is(segments[1], _repo),
        };

        if (!ours)
            throw new UpdateIntegrityException(
                $"refusing update URL for a different repository: {GithubProxy.Redact(url)} (pinned to {_owner}/{_repo})");
    }

    private static bool Is(string segment, string expected) =>
        segment.Equals(expected, StringComparison.OrdinalIgnoreCase);

    private enum Kind { Other, Api, Web }

    private static bool TryParse(string url, out Kind kind, out string[] segments)
    {
        kind = Kind.Other;
        segments = [];
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;

        if (uri.Host.Equals(ApiHost, StringComparison.OrdinalIgnoreCase)) kind = Kind.Api;
        else if (uri.Host.Equals(WebHost, StringComparison.OrdinalIgnoreCase)) kind = Kind.Web;
        else return false;

        segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return true;
    }

    private static string Describe(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? $"{uri.Host}{uri.AbsolutePath}" : "(unparseable URL)";

    // ── Digest lookup ─────────────────────────────────────────────────────────

    private static readonly Dictionary<string, string> ApiHeaders = new()
    {
        ["Accept"] = "application/vnd.github+json",
    };

    /// <summary>
    /// The lowercase SHA-256 GitHub's API publishes for the asset <paramref name="assetUrl"/> points at.
    /// Throws <see cref="UpdateIntegrityException"/> when the API answers without one.
    /// </summary>
    private async Task<string> ResolveDigestAsync(string assetUrl, double timeout, CancellationToken ct)
    {
        string? apiUrl = DigestLookupUrl(assetUrl, out string? assetName);
        if (apiUrl is null)
            throw new UpdateIntegrityException(
                $"cannot tell which release asset {Describe(assetUrl)} is, so there is no digest to check it against");

        ct.ThrowIfCancellationRequested();

        // No try/catch: if the API cannot be reached, the exception propagates and the package is never
        // downloaded — fail-closed, but still a plain transport error rather than an integrity verdict.
        string json = await _inner.DownloadString(apiUrl, ApiHeaders, timeout).ConfigureAwait(false);

        string? digest = ExtractDigest(json, assetName);
        return AssetIntegrity.ParseDigest(digest)
            ?? throw new UpdateIntegrityException(
                "the GitHub API published no usable SHA-256 digest for this release asset");
    }

    /// <summary>
    /// The API URL that describes <paramref name="assetUrl"/>, plus the asset name to pick out of it (null
    /// when the URL is itself the asset's API endpoint). Null when the URL is not a recognised release-asset
    /// link.
    /// <list type="bullet">
    /// <item><c>github.com/{o}/{r}/releases/download/{tag}/{name}</c> → <c>GET /repos/{o}/{r}/releases/tags/{tag}</c></item>
    /// <item><c>api.github.com/repos/{o}/{r}/releases/assets/{id}</c> → the same URL (returns the asset)</item>
    /// </list>
    /// </summary>
    internal string? DigestLookupUrl(string assetUrl, out string? assetName)
    {
        assetName = null;
        if (!TryParse(assetUrl, out var kind, out var s)) return null;

        if (kind == Kind.Web && s.Length == 6
            && s[2].Equals("releases", StringComparison.OrdinalIgnoreCase)
            && s[3].Equals("download", StringComparison.OrdinalIgnoreCase))
        {
            string tag = Uri.UnescapeDataString(s[4]);
            assetName = Uri.UnescapeDataString(s[5]);
            return $"https://{ApiHost}/repos/{_owner}/{_repo}/releases/tags/{Uri.EscapeDataString(tag)}";
        }

        if (kind == Kind.Api && s.Length == 6
            && s[0].Equals("repos", StringComparison.OrdinalIgnoreCase)
            && s[3].Equals("releases", StringComparison.OrdinalIgnoreCase)
            && s[4].Equals("assets", StringComparison.OrdinalIgnoreCase)
            && s[5].All(char.IsAsciiDigit))
        {
            return $"https://{ApiHost}/repos/{_owner}/{_repo}/releases/assets/{s[5]}";
        }

        return null;
    }

    /// <summary>
    /// Pull the <c>digest</c> out of either a release (find <paramref name="assetName"/> among its assets)
    /// or a single asset object. Null for anything else, malformed JSON included — callers treat null as
    /// "not published".
    /// </summary>
    internal static string? ExtractDigest(string json, string? assetName)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            if (assetName is null) return DigestOf(root);

            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.ValueKind == JsonValueKind.Object
                    && asset.TryGetProperty("name", out var name)
                    && name.ValueKind == JsonValueKind.String
                    && string.Equals(name.GetString(), assetName, StringComparison.Ordinal))
                    return DigestOf(asset);
            }
            return null;
        }
        catch (JsonException) { return null; }
    }

    private static string? DigestOf(JsonElement asset) =>
        asset.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;

    private static void DeleteQuietly(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
