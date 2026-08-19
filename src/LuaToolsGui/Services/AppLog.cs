using System.IO;

namespace LuaToolsGui.Services;

/// <summary>
/// Tiny thread-safe file logger, and the only diagnostic sink a shipped build has — there is no console.
///
/// <para>
/// It was called <c>PluginLog</c>, from when the Steam-plugin HTTP bridge was the only thing writing to
/// it. It has not been that for a long time: the app-update resolver, the fix/manifest safety screen, the
/// DPAPI fallback warning, the privacy notice for the cleartext lookup and the About page's manual update
/// check all log here. The old name said "this is the plugin's log", which is how a maintainer decides not
/// to look in it for an update or a settings problem.
/// </para>
///
/// <para>
/// The FILE keeps its name (<c>%AppData%\LuaToolsGui\plugin-backend.log</c>) deliberately. It is what the
/// README and the support answers tell people to send, and what the rotated <c>.1</c> generation on disk is
/// already called; renaming it would orphan every existing log and every instruction that points at one,
/// for no gain a user can see.
/// </para>
/// </summary>
public static class AppLog
{
    private static readonly object _lock = new();

    public static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LuaToolsGui", "plugin-backend.log");

    /// <summary>Roll the log once it passes this size. Without a cap the file grew for the lifetime of the
    /// install — every bridge call appends a line, and the store-page frontend now logs through here too.
    /// One previous generation is kept as .1 so a just-reproduced problem isn't rolled away.</summary>
    private const long MaxBytes = 2 * 1024 * 1024;

    public static void Log(string msg)
    {
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                RollIfTooLarge();
                // Sanitized: this sink also receives request bodies from the store-page bridge, which are
                // not guaranteed to be free of credentials.
                File.AppendAllText(
                    FilePath,
                    $"[{DateTime.Now:HH:mm:ss.fff}] {LogSanitizer.Sanitize(msg)}{Environment.NewLine}");
            }
        }
        catch { /* logging must never throw */ }
    }

    /// <summary>Caller holds <see cref="_lock"/>.</summary>
    private static void RollIfTooLarge()
    {
        try
        {
            var info = new FileInfo(FilePath);
            if (!info.Exists || info.Length < MaxBytes) return;
            File.Move(FilePath, FilePath + ".1", overwrite: true);
        }
        catch { /* can't roll (locked / denied) — keep appending rather than lose the message */ }
    }
}
