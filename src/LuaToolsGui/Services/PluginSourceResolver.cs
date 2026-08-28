using LuaToolsGui.Models;

namespace LuaToolsGui.Services;

/// <summary>Why a plugin source cannot serve an install, for the log line and the Plugin page's per-source
/// status text.</summary>
public enum PluginSourceRejection
{
    /// <summary>Nothing was rejected — this source is usable.</summary>
    None = 0,
    /// <summary>No release came back at all: the repo publishes none, or GitHub (and every mirror) was
    /// unreachable for it. Indistinguishable from here, and both mean "this source cannot install now".</summary>
    NoRelease,
    /// <summary>A release exists but carries no tag, so there is nothing to record or compare against.</summary>
    NoTag,
    /// <summary>The release is missing one of the assets an install needs.</summary>
    MissingAsset,
    /// <summary>An asset's <c>browser_download_url</c> does not belong to this source's own repository.
    /// The metadata may have come through an API mirror, so this is a redirect attempt, not a typo.</summary>
    ForeignAssetUrl,
    /// <summary>An asset carries no parseable sha256 digest, so its bytes could never be proven.</summary>
    NoDigest,
}

/// <summary>One source's release, established as installable.</summary>
/// <param name="Source">The repository it came from, and the pin its downloads are checked against.</param>
/// <param name="Release">The release metadata, already known to carry every required asset.</param>
public sealed record PluginReleaseChoice(PluginSource Source, GithubRelease Release);

/// <summary>Why one source cannot be installed from right now.</summary>
/// <param name="Source">The repository that failed.</param>
/// <param name="Reason">Why. Never <see cref="PluginSourceRejection.None"/>.</param>
/// <param name="AssetName">The asset that failed, when the reason is about one.</param>
public sealed record PluginSourceProblem(PluginSource Source, PluginSourceRejection Reason, string? AssetName);

/// <summary>
/// Decides WHETHER a plugin source may be installed from, and it is the whole of that decision — no
/// network, no disk, no clock. <see cref="PluginInstallerService"/> fetches the metadata and hands it here.
///
/// <para>
/// <b>There is no ordering and no automatic fallback here, deliberately.</b> An earlier build tried the
/// configured sources in order and silently used the next one when the first could not deliver. That is
/// the kind of thing that quietly becomes a security downgrade: anyone able to make the chosen source
/// fail — a block, a rate limit, an outage — also chose what got installed instead, without the user ever
/// being asked. The active source is now the user's own persisted choice
/// (see <see cref="PluginSourceSelection"/>), and this type answers exactly one question about exactly
/// one source: can it be installed from, yes or no. A "no" is an error the user is shown, never a reason
/// to reach for a different repository.
/// </para>
///
/// <para>
/// The gate itself is unchanged and stays fail-closed: a source is usable only when its release carries
/// every required asset, every asset URL is pinned to that same source's repository, and every asset
/// publishes a sha256. A source that fails is refused whole — its assets are never mixed with another's,
/// so one repository's digest can never be presented for another repository's bytes.
/// </para>
///
/// <para>
/// The pin check belongs HERE, ahead of the download, rather than only inside
/// <see cref="GithubProxy.DownloadAssetAsync"/>. Both refuse the same URLs; doing it at selection time is
/// what turns redirected metadata into a named error before a single byte is fetched.
/// </para>
/// </summary>
public static class PluginSourceResolver
{
    /// <summary>
    /// Whether one source's release is installable, and if not, why. Null means usable.
    ///
    /// <para>
    /// The single gate, and the single implementation of it. Check order is deliberate: cheapest and most
    /// general first, so the reported reason is the most useful one rather than whichever check happened
    /// to run.
    /// </para>
    /// </summary>
    public static PluginSourceProblem? Verify(
        PluginSource source, GithubRelease? release, IReadOnlyList<string> requiredAssets)
    {
        ArgumentNullException.ThrowIfNull(requiredAssets);

        if (release is null)
            return new PluginSourceProblem(source, PluginSourceRejection.NoRelease, null);

        // A tag is what the manifest records and what update detection compares. Without one, "installed"
        // and "latest" can never be told apart and the plugin would reinstall on every check.
        if (string.IsNullOrWhiteSpace(release.TagName))
            return new PluginSourceProblem(source, PluginSourceRejection.NoTag, null);

        foreach (string name in requiredAssets)
        {
            var asset = release.Assets.FirstOrDefault(
                a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

            if (asset is null)
                return new PluginSourceProblem(source, PluginSourceRejection.MissingAsset, name);

            // Pinned to THIS source — not merely to a GitHub host. The metadata can arrive through an API
            // mirror, which would otherwise be free to name any github.com repository's payload and supply
            // that payload's matching digest.
            if (!GithubProxy.IsAssetUrlForRepo(asset.DownloadUrl, source.Owner, source.Repo))
                return new PluginSourceProblem(source, PluginSourceRejection.ForeignAssetUrl, name);

            // Fail-closed, same rule as AssetIntegrity: an absent digest is a refusal, not a skipped check.
            if (AssetIntegrity.ParseDigest(asset.Digest) is null)
                return new PluginSourceProblem(source, PluginSourceRejection.NoDigest, name);
        }

        return null;
    }

    /// <summary>Convenience over <see cref="Verify"/> for callers that only need the yes/no.</summary>
    public static bool IsUsable(PluginSource source, GithubRelease? release, IReadOnlyList<string> requiredAssets) =>
        Verify(source, release, requiredAssets) is null;
}
