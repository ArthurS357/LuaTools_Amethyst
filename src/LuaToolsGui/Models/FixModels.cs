using System.Text.Json.Serialization;

namespace LuaToolsGui.Models;

// ── /api/denuvo/listings (public) — the game grid ───────────────────

public class DenuvoListingsResponse
{
    [JsonPropertyName("games")] public List<DenuvoGameListing> Games { get; set; } = [];
    [JsonPropertyName("tags")] public List<DenuvoTag> Tags { get; set; } = [];
}

public class DenuvoGameListing
{
    [JsonPropertyName("appid")] public string AppId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("header_image")] public string? HeaderImage { get; set; }
    [JsonPropertyName("fixCount")] public int FixCount { get; set; }
    [JsonPropertyName("tags")] public List<DenuvoTag> Tags { get; set; } = [];
}

public class DenuvoTag
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("slug")] public string Slug { get; set; } = "";
    [JsonPropertyName("color")] public string? Color { get; set; }
}

// ── /api/denuvo/fixes?appid= (public) — per-game fix detail ──────────

public class DenuvoFixesResponse
{
    [JsonPropertyName("appid")] public string AppId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("header_image")] public string? HeaderImage { get; set; }
    [JsonPropertyName("fixes")] public List<DenuvoFix> Fixes { get; set; } = [];
}

public class DenuvoFix
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("tags")] public List<DenuvoTag> Tags { get; set; } = [];
    [JsonPropertyName("hasManifest")] public bool HasManifest { get; set; }
    [JsonPropertyName("hasFix")] public bool HasFix { get; set; }
    [JsonPropertyName("manifestFilename")] public string? ManifestFilename { get; set; }
    [JsonPropertyName("fixFilename")] public string? FixFilename { get; set; }
    [JsonPropertyName("createdAt")] public string? CreatedAt { get; set; }
}

// ── /api/denuvo/download?fix=&slot= (auth) — returns a signed URL ────

public class DenuvoDownloadResponse
{
    [JsonPropertyName("url")] public string Url { get; set; } = "";
}
