using System.IO;
using System.Security.Cryptography;

namespace LuaToolsGui.Services;

/// <summary>
/// SHA-256 of a file, as lowercase hex, or null when it cannot be read.
/// </summary>
/// <remarks>
/// <para>
/// Used to prove a file on disk is still the one a fix wrote. SHA-256 rather than something faster
/// because it is in the BCL (no dependency), hardware-accelerated on every CPU this app targets, and
/// only ever runs over the handful of files a fix actually touches — never the whole game. Disk read
/// dominates, so a cheaper hash would buy nothing measurable.
/// </para>
/// <para>
/// Null on failure, never an exception: a locked or vanished file must degrade to "cannot verify" and
/// let the caller decide, not abort a revert halfway.
/// </para>
/// <para>
/// <b>Not <see cref="AssetIntegrity"/>, on purpose.</b> That one is the fail-closed gate for downloaded
/// binaries and throws rather than return an unverified answer. This is a change-detection helper for
/// files the user already owns, where "cannot read it" is an outcome the caller has to weigh, not a
/// refusal. Keeping them separate stops the fail-closed one from growing a null-returning overload.
/// </para>
/// </remarks>
public static class FileHash
{
    /// <summary>SHA-256 as lowercase hex, or null if the file is missing or unreadable.</summary>
    public static string? Sha256(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            using var fs = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.SequentialScan);
            return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        }
        catch { return null; }
    }

    /// <summary>
    /// True when <paramref name="path"/> hashes to <paramref name="expected"/>.
    /// </summary>
    /// <returns>
    /// <b>True when <paramref name="expected"/> is null or empty</b> — an absent hash means the record
    /// predates hashing, which is "nothing to check", not "check failed". This keeps records written by
    /// an older build (or by upstream LuaTools, which shares the <c>.luatools-fix</c> layout) revertable.
    ///
    /// <para>
    /// Callers on a DESTRUCTIVE branch must not rely on that default — see
    /// <see cref="DenuvoFixService"/>, which requires a real hash before it will delete a file.
    /// </para>
    /// </returns>
    public static bool Matches(string? path, string? expected)
    {
        if (string.IsNullOrEmpty(expected)) return true;
        return string.Equals(Sha256(path), expected, StringComparison.OrdinalIgnoreCase);
    }
}
