using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LuaToolsGui.Services;

/// <summary>
/// One-time first-boot migration of user data from <c>%AppData%\LuaToolsGui</c> (shared with upstream
/// LuaTools) to <c>%AppData%\LuaToolsAmethyst</c>.
///
/// <para>
/// Upstream LuaTools v1.3.3 writes to <c>%AppData%\LuaToolsGui</c>, drops fork-specific keys, clears
/// unknown <c>SelectedMode</c> values such as <c>"AmethystTool"</c>, and defaults <c>DonateKeys</c> to
/// <c>true</c>. Moving this fork's state to its own roaming directory isolates the two installs when they
/// coexist on the same machine.
/// </para>
///
/// <para>
/// <b>Copy, never move.</b> The legacy folder keeps every file, so an upstream install that still reads it
/// keeps working; the only change is <c>"DonateKeys": false</c> in its <c>settings.json</c> (see
/// <see cref="VaccinateLegacySettings"/>). The copy goes to a sibling staging folder first and is renamed into place
/// only when complete: a copy interrupted half-way (crash, power loss) leaves no partial
/// <c>LuaToolsAmethyst</c> that the next boot would mistake for "already migrated".
/// </para>
/// </summary>
public static class AppDataMigrator
{
    public const string LegacyFolderName = "LuaToolsGui";
    public const string CurrentFolderName = "LuaToolsAmethyst";

    /// <summary>Legacy roaming folder shared with upstream (<c>%AppData%\LuaToolsGui</c>).</summary>
    public static string LegacyDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), LegacyFolderName);

    /// <summary>Current roaming folder owned by LuaTools Amethyst (<c>%AppData%\LuaToolsAmethyst</c>). The
    /// single source of the app's data path: every state file is placed relative to this.</summary>
    public static string CurrentDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), CurrentFolderName);

    /// <summary>
    /// Copy <see cref="LegacyDirectory"/> into <see cref="CurrentDirectory"/> if the legacy folder has content
    /// and the new folder does not exist yet (or exists but is empty). Never modifies the legacy folder.
    /// </summary>
    /// <returns><c>true</c> if migration ran; <c>false</c> if skipped (nothing to migrate or already migrated).</returns>
    public static bool MigrateIfNeeded() => MigrateIfNeeded(LegacyDirectory, CurrentDirectory, AppLog.Log);

    /// <summary>Testable seam accepting explicit source/destination directories and a log sink.</summary>
    internal static bool MigrateIfNeeded(string legacyDir, string currentDir, Action<string>? log = null)
    {
        string? staging = null;
        try
        {
            if (string.IsNullOrWhiteSpace(legacyDir) || string.IsNullOrWhiteSpace(currentDir))
                return false;

            string legacyFull = Path.GetFullPath(legacyDir).TrimEnd('\\', '/');
            string currentFull = Path.GetFullPath(currentDir).TrimEnd('\\', '/');
            if (string.Equals(legacyFull, currentFull, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!Directory.Exists(legacyFull) || !Directory.EnumerateFileSystemEntries(legacyFull).Any())
                return false;

            // If the new directory already holds state, it wins and the legacy folder is ignored.
            if (HasState(currentFull))
                return false;

            staging = currentFull + ".migrating";
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); // a previous attempt died

            var (copied, failed) = CopyTree(legacyFull, staging, log);

            if (!Directory.Exists(currentFull))
            {
                Directory.Move(staging, currentFull);
            }
            else
            {
                // Present but stateless (empty, or only a log a failed earlier attempt wrote): merge in, keeping
                // what is already there. Same volume, so these are renames.
                foreach (string entry in Directory.EnumerateFileSystemEntries(staging).ToList())
                {
                    string target = Path.Combine(currentFull, Path.GetFileName(entry));
                    if (Directory.Exists(entry) && !Directory.Exists(target)) Directory.Move(entry, target);
                    else if (File.Exists(entry) && !File.Exists(target)) File.Move(entry, target);
                }
                Directory.Delete(staging, recursive: true);
            }
            staging = null;

            VaccinateLegacySettings(legacyFull, log);

            SafeLog(log,
                $"AppDataMigrator: copied {copied} file(s) from '{legacyFull}' to '{currentFull}'"
                + (failed > 0 ? $" ({failed} skipped due to I/O errors)" : "")
                + "; legacy directory kept (only settings.json gets DonateKeys=false).");

            return copied > 0;
        }
        catch (Exception ex)
        {
            SafeLog(log, $"AppDataMigrator: migration failed, will retry next start: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        finally
        {
            if (staging is not null)
                try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Writes <c>"DonateKeys": false</c> into the LEGACY <c>settings.json</c>. Upstream LuaTools reads that file
    /// and treats an absent key as <c>true</c>, which uploads Steam decryption keys over plain HTTP; this keeps
    /// the user safe even if they open the upstream app. The one deliberate write into the legacy folder.
    ///
    /// <para>
    /// Parsed as a <see cref="JsonNode"/> so every property the upstream owns survives. Never creates the file,
    /// leaves a file that is not a JSON object untouched, and never throws: a bad upstream file must not stop
    /// this app's own migration.
    /// </para>
    /// </summary>
    private static void VaccinateLegacySettings(string legacyDir, Action<string>? log)
    {
        string path = Path.Combine(legacyDir, "settings.json");
        try
        {
            if (!File.Exists(path)) return;

            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root)
            {
                SafeLog(log, "AppDataMigrator: legacy settings.json is not a JSON object; DonateKeys not vaccinated.");
                return;
            }

            if (root["DonateKeys"] is JsonValue v && v.TryGetValue<bool>(out bool current) && !current)
                return; // already false: no write, no mtime bump

            root["DonateKeys"] = false;
            AtomicFile.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            SafeLog(log, "AppDataMigrator: legacy settings.json vaccinated with DonateKeys=false.");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            SafeLog(log, $"AppDataMigrator: could not vaccinate legacy settings.json (left as is): {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Whether <paramref name="dir"/> holds real app state. The log does not count: <see cref="AppLog"/> writes
    /// into this folder, including this migrator's own failure message, and counting it would make one failed
    /// attempt look like a finished migration forever.
    /// </summary>
    private static bool HasState(string dir) =>
        Directory.Exists(dir)
        && Directory.EnumerateFileSystemEntries(dir).Any(e =>
            !Path.GetFileName(e).StartsWith("plugin-backend.log", StringComparison.OrdinalIgnoreCase));

    /// <summary>Recursive copy. A file that cannot be read (locked by a running upstream, say) is skipped and
    /// logged rather than aborting the whole migration.</summary>
    private static (int Copied, int Failed) CopyTree(string sourceDir, string destinationDir, Action<string>? log)
    {
        int copied = 0, failed = 0;
        Directory.CreateDirectory(destinationDir);

        foreach (string file in Directory.EnumerateFiles(sourceDir))
        {
            try
            {
                File.Copy(file, Path.Combine(destinationDir, Path.GetFileName(file)), overwrite: false);
                copied++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
                SafeLog(log, $"AppDataMigrator: could not copy '{Path.GetFileName(file)}': {ex.GetType().Name}: {ex.Message}");
            }
        }

        foreach (string subDir in Directory.EnumerateDirectories(sourceDir))
        {
            var (c, f) = CopyTree(subDir, Path.Combine(destinationDir, Path.GetFileName(subDir)), log);
            copied += c;
            failed += f;
        }

        return (copied, failed);
    }

    private static void SafeLog(Action<string>? log, string message)
    {
        try { (log ?? AppLog.Log)(message); } catch { /* logging must never break startup */ }
    }
}
