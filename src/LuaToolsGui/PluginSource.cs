namespace LuaToolsGui;

/// <summary>
/// One GitHub repository the store-page plugin may be installed from — an owner/repo pair that is BOTH
/// where the release metadata is fetched and the pin every asset URL from that metadata is checked against
/// (see <see cref="Services.GithubProxy.IsAssetUrlForRepo"/>).
///
/// <para>
/// The two halves travel together on purpose. Before this type they were separate constants passed
/// individually to the fetch and to each download, so adding a second source would have meant threading
/// two parallel strings through every call and trusting that no site ever paired one source's owner with
/// another's repo — a mismatch that would silently widen the pin instead of failing. Here it cannot be
/// expressed.
/// </para>
/// </summary>
/// <param name="Owner">GitHub account or organisation.</param>
/// <param name="Repo">Repository name under <paramref name="Owner"/>.</param>
public readonly record struct PluginSource(string Owner, string Repo)
{
    /// <summary>"owner/repo" — the form the README's source table and <see cref="Services.DownloadReview"/>
    /// use, and the only form shown to users or written to logs.</summary>
    public string Slug => $"{Owner}/{Repo}";

    /// <summary>The GitHub API endpoint for this source's newest published release.</summary>
    public string LatestReleaseApiUrl =>
        $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

    public override string ToString() => Slug;
}
