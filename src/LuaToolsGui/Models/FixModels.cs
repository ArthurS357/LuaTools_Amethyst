using System.Text.Json.Serialization;

namespace LuaToolsGui.Models;

// ── /api/denuvo/listings (public) — the game grid ───────────────────

public class DenuvoListingsResponse
{
    [JsonPropertyName("games")] public List<DenuvoGameListing> Games { get; init; } = [];
    [JsonPropertyName("tags")] public List<DenuvoTag> Tags { get; init; } = [];
}

public class DenuvoGameListing
{
    [JsonPropertyName("appid")] public string AppId { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("header_image")] public string? HeaderImage { get; init; }
    [JsonPropertyName("fixCount")] public int FixCount { get; init; }
    [JsonPropertyName("tags")] public List<DenuvoTag> Tags { get; init; } = [];
}

public class DenuvoTag
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("slug")] public string Slug { get; init; } = "";
    [JsonPropertyName("color")] public string? Color { get; init; }
}

// ── /api/denuvo/fixes?appid= (public) — per-game fix detail ──────────

public class DenuvoFixesResponse
{
    [JsonPropertyName("appid")] public string AppId { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("header_image")] public string? HeaderImage { get; init; }
    [JsonPropertyName("fixes")] public List<DenuvoFix> Fixes { get; init; } = [];
}

public class DenuvoFix
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("tags")] public List<DenuvoTag> Tags { get; init; } = [];
    [JsonPropertyName("hasManifest")] public bool HasManifest { get; init; }
    [JsonPropertyName("hasFix")] public bool HasFix { get; init; }
    [JsonPropertyName("manifestFilename")] public string? ManifestFilename { get; init; }
    [JsonPropertyName("fixFilename")] public string? FixFilename { get; init; }
    [JsonPropertyName("createdAt")] public string? CreatedAt { get; init; }
}

// ── /api/denuvo/download?fix=&slot= (auth) — returns a signed URL ────

public class DenuvoDownloadResponse
{
    [JsonPropertyName("url")] public string Url { get; init; } = "";
}

// ── Fix revert record (written to .luatools-fix/ inside the game folder) ──
//
// Deliberately NOT called a manifest: in this app that word means a Steam depot manifest (depotcache,
// ResolveManifestPath, the Fixes page's own Manifest button), and a second meaning would make "no record
// of this fix" read as "your depot manifests are gone". The JsonPropertyName values below are the on-disk
// contract and must not drift with the type names.
//
// Write-once like the API DTOs above, and for a related reason: a record describes what a fix DID, so
// patching one in place would let it drift from the files on disk. The apply path builds each entry once,
// after the extract, when both hashes are finally knowable — see DenuvoFixService.

/// <summary>
/// What one applied fix changed, written into the game's own folder so it travels with the install.
/// </summary>
/// <remarks>
/// The per-file hashes are what make several fixes on one game safe. Fix B applied over fix A backs up
/// A's file, so the backups CHAIN and must be unwound newest-first. Reverting A first would restore the
/// pre-A original over B's file and leave the game with a mix neither fix expects. Rather than police the
/// order, each entry records the hash of what the fix wrote: if the file on disk no longer matches,
/// something replaced it after us and the revert stops instead of clobbering it. That catches the
/// out-of-order case, and also game updates and hand-edits, which no ordering rule would.
/// </remarks>
public class DenuvoFixRecord
{
    [JsonPropertyName("appId")] public long AppId { get; init; }
    [JsonPropertyName("fixId")] public string FixId { get; init; } = "";
    [JsonPropertyName("appliedAt")] public string AppliedAt { get; init; } = "";
    [JsonPropertyName("files")] public List<DenuvoFixRecordEntry> Files { get; init; } = [];
}

public class DenuvoFixRecordEntry
{
    [JsonPropertyName("relativePath")] public string RelativePath { get; init; } = "";
    [JsonPropertyName("action")] public string Action { get; init; } = ""; // "modified" or "added"
    [JsonPropertyName("backupPath")] public string? BackupPath { get; init; } // relative to .luatools-fix/

    /// <summary>
    /// SHA-256 of the file as the fix left it. Lets a revert prove the file on disk is still the one this
    /// fix wrote before touching it — see <see cref="DenuvoFixRecord"/> for why that matters.
    /// </summary>
    [JsonPropertyName("hashAfter")] public string? HashAfter { get; init; }

    /// <summary>
    /// SHA-256 of the original, pre-fix file. Null for <c>"added"</c> entries (there was no original).
    /// Used to tell an already-restored file apart from a foreign one when a revert is retried.
    /// </summary>
    [JsonPropertyName("hashBefore")] public string? HashBefore { get; init; }
}
