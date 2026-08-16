namespace LuaToolsGui.Services;

/// <summary>
/// The facts about one verified artifact, assembled for the user immediately before it is applied.
///
/// <para>
/// Everything the Mode and Plugin pages install lands somewhere consequential — the Steam root, where
/// steam.exe loads it, or straight into a process. Those installs are gated by real checks (repository
/// pinning, fail-closed SHA-256, and archive screening for zips), but until now all of that happened
/// silently: a user had no way to see WHERE a binary came from or WHAT was verified about it. This record
/// is that disclosure, and it is deliberately built only from facts already established — it never
/// re-decides anything.
/// </para>
/// </summary>
/// <param name="Owner">GitHub owner the asset URL was pinned to.</param>
/// <param name="Repo">GitHub repository the asset URL was pinned to.</param>
/// <param name="Tag">Release tag, or null when the source publishes untagged/rolling assets.</param>
/// <param name="AssetName">The primary artifact's file name.</param>
/// <param name="Sha256">Lowercase hex SHA-256 actually computed over the staged bytes.</param>
/// <param name="FileCount">How many files this install places (1 for a single asset).</param>
/// <param name="ArchiveScreened">True when <see cref="FixAnalyzer"/> also screened the archive.</param>
public sealed record DownloadReview(
    string Owner,
    string Repo,
    string? Tag,
    string AssetName,
    string Sha256,
    int FileCount = 1,
    bool ArchiveScreened = false)
{
    /// <summary>"owner/repo", the form the README's source table uses.</summary>
    public string Source => $"{Owner}/{Repo}";

    /// <summary>Release tag for display; an em dash when the source publishes none.</summary>
    public string Version => string.IsNullOrWhiteSpace(Tag) ? "—" : Tag;

    /// <summary>
    /// First 16 hex characters of the digest. A full 64-character hash does not fit a toast and nobody
    /// reads it; 16 characters is still far past the point where two artifacts collide by accident, which
    /// is all this line is for — letting a user match what was installed against what a release publishes.
    /// </summary>
    public string ShortHash => Sha256.Length > 16 ? Sha256[..16] : Sha256;

    /// <summary>Which gates this artifact passed, as a single localized line.</summary>
    public string Checks
    {
        get
        {
            var parts = new List<string>(3)
            {
                Resources.Strings.Download_Notice_Check_Pinned,
                Resources.Strings.Download_Notice_Check_Digest,
            };
            if (ArchiveScreened) parts.Add(Resources.Strings.Download_Notice_Check_Archive);
            return string.Join(" · ", parts);
        }
    }

    public string Title => string.Format(Resources.Strings.Download_Notice_Title, Source);

    public string Body => string.Format(
        Resources.Strings.Download_Notice_Body, AssetName, Version, FileCount, ShortHash, Checks);
}

/// <summary>
/// Surfaces a <see cref="DownloadReview"/> and gives the user a window in which to abort.
///
/// <para>
/// A callback rather than a dialog, for the same reason as
/// <see cref="PluginInstallerService.ConfirmCdpExposure"/>: installs run on background threads, during
/// silent auto-update, and from the HTTP bridge, none of which can show a window. <c>App</c> wires
/// <see cref="Present"/> to a toast carrying a Cancel button.
/// </para>
///
/// <para>
/// <b>This is advisory and fails OPEN</b>, which is the opposite of every other check in this codebase and
/// is deliberate. It is a disclosure, not a gate: the decisions that actually protect the user — repository
/// pinning, the fail-closed digest comparison, archive screening — have already run and already refused
/// anything they could not prove. Letting a missing or broken notice block an install would turn a UI
/// detail into a functional outage without making anything safer. No presenter (headless, silent update)
/// therefore means "proceed", not "deny".
/// </para>
/// </summary>
public sealed class DownloadNotice
{
    /// <summary>
    /// How long the user gets to press Cancel before the install continues on its own. A Cancel button with
    /// no window to press it in is decoration, so some wait is required for the control to mean anything;
    /// this is short enough not to feel like a prompt.
    /// </summary>
    public static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(6);

    /// <summary>
    /// Shows the review and resolves to false only if the user actively cancelled within the grace period.
    /// Left null in headless contexts, where installs proceed unannounced.
    /// </summary>
    public Func<DownloadReview, TimeSpan, Task<bool>>? Present { get; set; }

    /// <summary>True to go ahead with the install.</summary>
    public async Task<bool> ReviewAsync(DownloadReview review, CancellationToken ct = default)
    {
        if (Present is not { } present) return true;
        if (ct.IsCancellationRequested) return false;

        try
        {
            return await present(review, GracePeriod);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return true; // see the fail-open note on this type
        }
    }
}
