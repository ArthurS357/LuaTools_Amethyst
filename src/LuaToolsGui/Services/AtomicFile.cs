using System.IO;

namespace LuaToolsGui.Services;

/// <summary>
/// Write a file so a crash, a power cut or an antivirus scan mid-write can never leave a truncated
/// destination: the bytes go to a sibling temp file first and replace the destination with one rename.
/// </summary>
/// <remarks>
/// A truncated manifest in <c>depotcache</c> is "sticky" (the installer skips an existing destination), and
/// a truncated <c>details\&lt;appid&gt;.json</c> reads as already cached forever. Settings, cache and the vault
/// already do this; this is the shared form for the rest.
///
/// <para>
/// The rename is refused when another process holds the destination open without FILE_SHARE_DELETE (Steam
/// may have a lua or manifest open), while the old in-place write would have succeeded. In that case this
/// falls back to the old in-place write, so it is never worse than before — atomic when it can be.
/// </para>
/// </remarks>
internal static class AtomicFile
{
    /// <param name="path">The destination.</param>
    /// <param name="fill">Writes the full content to the path it is given.</param>
    public static void Write(string path, Action<string> fill)
    {
        string tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            fill(tmp);
            try
            {
                File.Move(tmp, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Destination held open: fall through to the in-place write.
            }

            fill(path);
        }
        finally
        {
            try { File.Delete(tmp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* swept later */ }
        }
    }

    public static void WriteAllText(string path, string contents) =>
        Write(path, p => File.WriteAllText(p, contents));

    public static void WriteAllBytes(string path, byte[] bytes) =>
        Write(path, p => File.WriteAllBytes(p, bytes));
}
