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
