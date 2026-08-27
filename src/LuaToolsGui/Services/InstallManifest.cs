using System.IO;
using System.Text.Json;
using LuaToolsGui.Models;

namespace LuaToolsGui.Services;

/// <summary>Stable identifiers for the things this app installs into the Steam root.</summary>
/// <remarks>
/// These are persisted keys, not display names: renaming one orphans every manifest entry already on
/// disk, which turns "Uninstall" back into "nothing is recorded". Add ids, don't rename them.
/// </remarks>
public static class PluginIds
{
    /// <summary>The store-page plugin (<see cref="PluginInstallerService"/>).</summary>
    public const string StorePage = "store-page";

    /// <summary>AmethystTool, the native injection plugin (<see cref="AmethystToolService"/>).</summary>
    public const string AmethystTool = "amethysttool";

    /// <summary>What every Mode's id starts with, so a Mode entry is recognisable without a lookup table.</summary>
    public const string ModePrefix = "mode-";

    /// <summary>
    /// The id a Mode records under (<see cref="UnlockerService"/>).
    ///
    /// <para>
    /// Derived from the enum member, not from the display name: the display name is a brand that has
    /// already been renamed once — <see cref="UnlockerMode.OpenSteamTools"/> is shown as
    /// "BetterSteamTools" — and a rename must never orphan a record.
    /// </para>
    /// </summary>
    /// <remarks>
    /// The result becomes a folder name inside the Steam root (see <see cref="PluginRemoval"/>), so the
    /// separator is a hyphen. A colon would be rejected there as a drive-relative path or an NTFS stream.
    /// </remarks>
    public static string ForMode(UnlockerMode mode) =>
        ModePrefix + mode.ToString().ToLowerInvariant();

    /// <summary>Whether an id belongs to the Mode page. Modes are mutually exclusive, so installing one
    /// consolidates onto a single entry — this is what finds the others to fold in and drop.</summary>
    public static bool IsMode(string pluginId) =>
        pluginId.StartsWith(ModePrefix, StringComparison.OrdinalIgnoreCase);
}

/// <summary>One file an install placed, and what it hashed to at that moment.</summary>
/// <param name="Name">Bare file name, relative to the Steam root.</param>
/// <param name="Sha256">
/// Lowercase hex SHA-256 as installed, or <see langword="null"/> when it could not be computed. Recorded
/// for diagnosis, deliberately NOT used as a removal gate: a plugin that updated itself in place would
/// then be undeletable, and refusing to clean up a file whose bytes moved on is the worse failure.
/// </param>
public sealed record InstalledFile(string Name, string? Sha256);

/// <summary>What one plugin put in the Steam root, and when.</summary>
public sealed record InstalledPlugin(
    string PluginId,
    string? Version,
    DateTimeOffset InstalledAt,
    IReadOnlyList<InstalledFile> Files);

/// <summary>
/// The record of what this app has placed in the Steam root, keyed by plugin.
///
/// <para>
/// This exists so uninstall can be a <b>fact</b> rather than a guess. Before it, nothing on disk said
/// which of the files next to steam.exe were ours: removal would have had to work from a hardcoded list
/// of names, and three of those names (<c>dwmapi.dll</c>, <c>xinput1_4.dll</c>) are also placed by the
/// Mode page and by other tools entirely. Deleting by name is how a working Steam install gets broken by
/// a feature meant to clean up after itself.
/// </para>
/// </summary>
public sealed record InstallManifest(int SchemaVersion, IReadOnlyDictionary<string, InstalledPlugin> Plugins)
{
    /// <summary>Bumped only for a change old readers cannot cope with. Readers ignore versions they do not
    /// recognise rather than guessing at the shape (see <see cref="InstallManifestService.Load"/>).</summary>
    public const int CurrentSchemaVersion = 1;

    public static InstallManifest Empty { get; } =
        new(CurrentSchemaVersion, new Dictionary<string, InstalledPlugin>(StringComparer.OrdinalIgnoreCase));

    public InstalledPlugin? Get(string pluginId) =>
        Plugins.TryGetValue(pluginId, out var entry) ? entry : null;

    /// <summary>Every file name claimed by a plugin OTHER than <paramref name="pluginId"/>.</summary>
    public IReadOnlySet<string> FilesClaimedByOthers(string pluginId)
    {
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, entry) in Plugins)
        {
            if (id.Equals(pluginId, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var file in entry.Files) claimed.Add(file.Name);
        }
        return claimed;
    }

    public InstallManifest With(InstalledPlugin entry)
    {
        var next = new Dictionary<string, InstalledPlugin>(Plugins, StringComparer.OrdinalIgnoreCase)
        {
            [entry.PluginId] = entry,
        };
        return this with { SchemaVersion = CurrentSchemaVersion, Plugins = next };
    }

    public InstallManifest Without(string pluginId)
    {
        var next = new Dictionary<string, InstalledPlugin>(Plugins, StringComparer.OrdinalIgnoreCase);
        next.Remove(pluginId);
        return this with { SchemaVersion = CurrentSchemaVersion, Plugins = next };
    }

    /// <summary>
    /// Strip <paramref name="fileNames"/> out of every OTHER plugin's claim, for a new install that just
    /// verified and wrote those exact names itself. An entry left with no files is dropped; an entry that
    /// still names files the new install did NOT touch keeps those, unreduced.
    ///
    /// <para>
    /// Trims rather than folding the whole entry in, unlike <c>UnlockerService.RecordModeInstall</c>'s
    /// Mode-to-Mode handoff. That difference is deliberate: AmethystTool's payload is a fixed four names,
    /// never <c>OpenSteamTool.dll</c> or <c>cloud_redirect.dll</c>, so absorbing an entire superseded Mode
    /// entry would hand AmethystTool's manifest row a file it never placed and cannot verify. Only the
    /// names actually just overwritten stop being that entry's to claim.
    /// </para>
    /// </summary>
    /// <param name="fileNames">Names the new install just placed — <see cref="AmethystInstallStep.FileName"/>.</param>
    /// <param name="newOwnerPluginId">The id those files now belong to; its own entry is left untouched.</param>
    public InstallManifest AbsorbFiles(IReadOnlyCollection<string> fileNames, string newOwnerPluginId)
    {
        ArgumentNullException.ThrowIfNull(fileNames);
        ArgumentException.ThrowIfNullOrWhiteSpace(newOwnerPluginId);
        if (fileNames.Count == 0) return this;

        var taken = new HashSet<string>(fileNames, StringComparer.OrdinalIgnoreCase);
        var next = new Dictionary<string, InstalledPlugin>(Plugins, StringComparer.OrdinalIgnoreCase);
        bool changed = false;

        foreach (var (id, entry) in Plugins)
        {
            if (id.Equals(newOwnerPluginId, StringComparison.OrdinalIgnoreCase)) continue;

            var kept = entry.Files.Where(f => !taken.Contains(f.Name)).ToList();
            if (kept.Count == entry.Files.Count) continue; // nothing of this entry overlapped

            changed = true;
            if (kept.Count == 0) next.Remove(id);
            else next[id] = entry with { Files = kept };
        }

        return changed ? this with { SchemaVersion = CurrentSchemaVersion, Plugins = next } : this;
    }

    /// <summary>
    /// Record <paramref name="entry"/> and make its claim on those names exclusive in one step:
    /// <see cref="With"/> followed by <see cref="AbsorbFiles"/> over the entry's own file names.
    ///
    /// <para>
    /// This is the shape every backend competing for the proxy DLLs next to <c>steam.exe</c> needs, and the
    /// reason it is one transformation rather than two calls at each site: a backend that has recorded
    /// itself but not yet absorbed leaves BOTH entries claiming <c>dwmapi.dll</c>, and a name two entries
    /// claim is a name NEITHER can remove — each reads the other's claim as "still needed by another
    /// install". Done in one step, that state is never reachable.
    /// </para>
    ///
    /// <para>
    /// Trims, never folds: an entry that also names a file <paramref name="entry"/> did not place keeps it.
    /// See <see cref="AbsorbFiles"/>.
    /// </para>
    /// </summary>
    public InstallManifest RecordExclusive(InstalledPlugin entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return With(entry).AbsorbFiles([.. entry.Files.Select(f => f.Name)], entry.PluginId);
    }
}

/// <summary>
/// Loads and saves <see cref="InstallManifest"/> at
/// <c>%AppData%\LuaToolsGui\install-manifest.json</c>.
///
/// <para>
/// Its own file on purpose. <c>settings.json</c> is user-editable configuration with a documented shape;
/// this is app-owned bookkeeping that the app rewrites on every install, and mixing the two would mean a
/// hand-edit to a preference could race an install, or a corrupt install record could cost the user their
/// settings. Nothing here changes the settings format.
/// </para>
///
/// <para>
/// <b>Reads never throw.</b> A truncated or hand-mangled file resolves to <see cref="InstallManifest.Empty"/>,
/// which degrades to "nothing is recorded, so remove nothing" — the safe direction. A read that threw
/// would take the Plugin page down with it.
/// </para>
///
/// <para>
/// <b>Writes are atomic.</b> The record is serialised to a sibling temp file and only then swapped into
/// place, so a reader either sees the whole previous record or the whole new one. A plain
/// <c>File.WriteAllText</c> truncates first and fills after: a crash, a power cut, or antivirus stepping in
/// between leaves a half-written file that reads back as empty — which here means "nothing is recorded",
/// which means Uninstall silently stops working for files that are very much still in the Steam root.
/// </para>
/// </summary>
public sealed class InstallManifestService
{
    private static readonly JsonSerializerOptions ReadOpts = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    private const string ManifestFileName = "install-manifest.json";

    /// <summary>Serialises read-modify-write. Install and uninstall run on background threads and the
    /// Plugin page reads on the UI thread, so a lost update here would silently orphan files.</summary>
    private readonly Lock _gate = new();

    private readonly string _directory;
    private readonly string _filePath;

    public InstallManifestService() : this(StateDirectory) { }

    /// <summary>Test seam: an isolated directory, so the write path can be exercised without touching the
    /// record belonging to whoever is running the tests.</summary>
    internal InstallManifestService(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
        _filePath = Path.Combine(directory, ManifestFileName);
    }

    /// <summary>The app's own state folder — the same one every other local manifest lives in.</summary>
    public static string StateDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LuaToolsGui");

    /// <summary>Single source of truth for where the record lives; nothing else spells this path.</summary>
    public static string FilePath => Path.Combine(StateDirectory, ManifestFileName);

    public InstallManifest Load()
    {
        lock (_gate) return LoadUnlocked();
    }

    private InstallManifest LoadUnlocked()
    {
        try
        {
            if (!File.Exists(_filePath)) return InstallManifest.Empty;

            var loaded = JsonSerializer.Deserialize<InstallManifest>(File.ReadAllText(_filePath), ReadOpts);
            if (loaded is null) return InstallManifest.Empty;

            // A file written by a newer build may mean something different by the same field names.
            // Treating it as empty loses nothing that can be recovered by guessing, and the next install
            // rewrites it at the current version.
            if (loaded.SchemaVersion != InstallManifest.CurrentSchemaVersion) return InstallManifest.Empty;

            // Deserialization produces a case-SENSITIVE dictionary; every lookup here is a Windows file
            // name, so rebuild it under the comparer the rest of this type promises.
            var plugins = new Dictionary<string, InstalledPlugin>(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, entry) in loaded.Plugins)
                if (entry is not null && !string.IsNullOrWhiteSpace(id))
                    plugins[id] = entry;

            return loaded with { Plugins = plugins };
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException
                                       or NotSupportedException)
        {
            return InstallManifest.Empty;
        }
    }

    /// <summary>Record (or replace) what a plugin installed. Returns false if it could not be persisted —
    /// the install itself already happened, so callers report this rather than failing the install.</summary>
    public bool Record(InstalledPlugin entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate) return SaveUnlocked(LoadUnlocked().With(entry));
    }

    /// <summary>
    /// Record what a plugin installed AND drop every other entry's claim on those same names, as a single
    /// read-modify-write. See <see cref="InstallManifest.RecordExclusive"/> for why the two halves must not
    /// be separate writes. Returns false if it could not be persisted — the install itself already
    /// happened, so callers report this rather than failing the install.
    /// </summary>
    public bool RecordExclusive(InstalledPlugin entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate) return SaveUnlocked(LoadUnlocked().RecordExclusive(entry));
    }

    /// <summary>Forget a plugin — called after its files are gone.</summary>
    public bool Forget(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        lock (_gate) return SaveUnlocked(LoadUnlocked().Without(pluginId));
    }

    /// <summary>
    /// Apply <see cref="InstallManifest.AbsorbFiles"/> under the same read-modify-write lock as every other
    /// write here, so an absorb racing a install/uninstall never loses the other's change. A no-op when
    /// nothing overlapped skips the write entirely — same reasoning as <see cref="Record"/> never writing
    /// an unchanged file.
    /// </summary>
    public bool AbsorbFiles(IReadOnlyCollection<string> fileNames, string newOwnerPluginId)
    {
        lock (_gate)
        {
            var before = LoadUnlocked();
            var after = before.AbsorbFiles(fileNames, newOwnerPluginId);
            return ReferenceEquals(before, after) || SaveUnlocked(after);
        }
    }

    /// <summary>
    /// Write the record so that a reader never sees a partial one: serialise to a temp file <b>in the same
    /// directory</b> (a cross-volume move is a copy, and a copy is not atomic), flush it, then swap.
    /// </summary>
    /// <remarks>
    /// <see cref="File.Replace(string, string, string?)"/> is the swap that also carries the destination's
    /// ACLs and attributes over, but it requires the destination to already exist — on the very first write
    /// there is nothing to replace, so that case moves the temp file into place instead. Both are a single
    /// directory-entry operation.
    /// <para>
    /// A failure leaves the PREVIOUS record intact and removes the temp file. Returning false rather than
    /// throwing keeps the contract callers already rely on: the install itself has happened by the time
    /// this runs, so a failure here is reported, not raised.
    /// </para>
    /// </remarks>
    private bool SaveUnlocked(InstallManifest manifest)
    {
        // Distinct per call: two services sharing one temp name would race even under this instance's lock,
        // because the lock is per-instance and the file is not.
        string tempPath = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(tempPath, JsonSerializer.Serialize(manifest, WriteOpts));

            if (File.Exists(_filePath)) File.Replace(tempPath, _filePath, destinationBackupFileName: null);
            else File.Move(tempPath, _filePath);

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
        finally
        {
            // Both swaps consume the temp file, so this only ever fires on a failed write — no residue is
            // left behind next to the record for a user to wonder about.
            try { if (File.Exists(tempPath)) File.Delete(tempPath); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* best effort */ }
        }
    }
}
