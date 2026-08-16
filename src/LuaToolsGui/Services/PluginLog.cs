using System.IO;

namespace LuaToolsGui.Services;

/// <summary>Tiny thread-safe file logger for the Steam-plugin HTTP backend, so we can diagnose the
/// add flow without a console. Writes to %AppData%\LuaToolsGui\plugin-backend.log.</summary>
public static class PluginLog
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
