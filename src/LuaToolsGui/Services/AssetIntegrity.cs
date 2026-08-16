using System.IO;
using System.Security.Cryptography;

namespace LuaToolsGui.Services;

/// <summary>
/// Verifies that a downloaded file is byte-for-byte what GitHub published for a release asset.
///
/// <para>
/// This matters because <see cref="GithubProxy"/> deliberately falls back to THIRD-PARTY download mirrors
/// so the app keeps working in regions that block github.com. Those mirrors can return anything, and what
/// the app does with the bytes is severe: copy a DLL into the Steam root (steam.exe loads it) or execute
/// an .exe. The published sha256 digest — which comes from the GitHub API response, not from the mirror —
/// is what ties the delivered bytes back to the release.
/// </para>
///
/// <para>
/// The verification is deliberately <b>fail-closed</b>: an absent or unparseable digest returns false
/// rather than "nothing to check, carry on". The previous form was
/// <c>if (ParseDigest(a.Digest) is { } want &amp;&amp; sha != want) fail;</c>, which silently skipped the
/// check whenever a release carried no digest — so a digest-less API response was, by itself, enough to get
/// unverified code placed next to Steam. See <see cref="Matches"/>.
/// </para>
///
/// <para>
/// Centralised here because four services had their own private copies of the same two helpers
/// (SteamlessService, CloudRedirectService, UnlockerService, PluginInstallerService). Duplicated security
/// checks drift: three of the four had the fail-open bug and one did no verification at all.
/// </para>
/// </summary>
internal static class AssetIntegrity
{
    /// <summary>
    /// The bare lowercase hex digest from a GitHub asset's <c>digest</c> field (e.g.
    /// <c>"sha256:AB12…"</c> becomes <c>"ab12…"</c>), or <see langword="null"/> when the field is absent,
    /// blank, or doesn't contain a plausible SHA-256.
    /// </summary>
    public static string? ParseDigest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest)) return null;

        int colon = digest.IndexOf(':');
        string hex = (colon >= 0 ? digest[(colon + 1)..] : digest).Trim();

        // Only accept something that actually looks like a SHA-256. A truncated or malformed value would
        // otherwise be compared literally and never match, which is safe but reports the wrong reason;
        // worse, a future caller could mistake "parsed" for "usable".
        return IsSha256Hex(hex) ? hex.ToLowerInvariant() : null;
    }

    /// <summary>SHA-256 of a file as lowercase hex.</summary>
    public static string Sha256OfFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>
    /// Whether the file at <paramref name="path"/> matches <paramref name="publishedDigest"/>.
    /// Returns <see langword="false"/> when the digest is missing/malformed, when the file is missing, or
    /// when it can't be read — every "can't prove it's correct" outcome is a failure, never a pass.
    /// </summary>
    public static bool Matches(string path, string? publishedDigest)
    {
        if (ParseDigest(publishedDigest) is not { } want) return false;
        if (!File.Exists(path)) return false;

        try
        {
            return Sha256OfFile(path).Equals(want, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false; // locked mid-verification — cannot prove it, so it doesn't pass
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether an already-open stream hashes to <paramref name="publishedDigest"/>, leaving the stream open
    /// for the caller to go on using.
    ///
    /// <para>
    /// This is the time-of-check/time-of-use safe form. <see cref="Matches(string, string?)"/> hashes a
    /// path and then the caller re-opens that path to use it — between those two steps another process
    /// running as the same user can substitute the file, so what was verified is not what gets used. Open
    /// the file once with <c>FileShare.None</c>, pass the stream here, rewind, and consume the same handle.
    /// </para>
    ///
    /// <para>Hashing consumes the stream, so callers must reset <c>Position</c> before reusing it.</para>
    /// </summary>
    public static bool MatchesStream(Stream stream, string? publishedDigest)
    {
        if (ParseDigest(publishedDigest) is not { } want) return false;

        if (stream.CanSeek) stream.Position = 0;
        string actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();

        return actual.Equals(want, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSha256Hex(ReadOnlySpan<char> value)
    {
        if (value.Length != 64) return false;
        foreach (char c in value)
        {
            bool hex = c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
            if (!hex) return false;
        }
        return true;
    }
}
