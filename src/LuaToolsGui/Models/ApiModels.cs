using System.Text.Json.Serialization;

namespace LuaToolsGui.Models;

// Every DTO here is `init`-only. They are deserialization targets: System.Text.Json fills them once and
// nothing in the app has any business writing to one afterwards. The conversion from `set` found zero
// call sites to fix, which is the point — the property was open for no reason, and a later "just patch the
// field before passing it on" would have been silently allowed against an object the app treats as a
// snapshot of what a server said.
//
// `init` on a class, deliberately NOT `record`. A record would also change equality from reference to
// structural, and these types are held in caches, compared and put in collections — none of which asked
// for value semantics. Immutability is the property worth having; value equality is a separate decision
// with its own failure modes, and it is not being made here as a side effect of a syntax cleanup.
//
// ── lua.tools API DTOs ──────────────────────────────────────────────

public class SteamSearchResult
{
    public long AppId { get; init; }
    public string Name { get; init; } = "";
    public string? Icon { get; init; }
}

// Steam's public store-search response (called directly by the app)
public class SteamStoreSearchResponse
{
    [JsonPropertyName("items")] public List<SteamStoreItem> Items { get; init; } = [];
}

public class SteamStoreItem
{
    [JsonPropertyName("id")] public long Id { get; init; }
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("tiny_image")] public string? TinyImage { get; init; }
}

// ── Steam featuredcategories (Add page "Featured" strips) ───────────
// Only the two app-list categories we surface are modeled. Each item carries everything we render
// (appid + name + wide capsule art), so no per-app lookup is needed.
public class SteamFeaturedResponse
{
    [JsonPropertyName("top_sellers")] public SteamFeaturedCategory? TopSellers { get; init; }
    [JsonPropertyName("new_releases")] public SteamFeaturedCategory? NewReleases { get; init; }
}

public class SteamFeaturedCategory
{
    [JsonPropertyName("items")] public List<SteamFeaturedItem> Items { get; init; } = [];
}

public class SteamFeaturedItem
{
    [JsonPropertyName("id")] public long Id { get; init; }
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    // 616×353 capsule — the nice wide art for a featured card.
    [JsonPropertyName("large_capsule_image")] public string? LargeCapsuleImage { get; init; }
    [JsonPropertyName("type")] public int Type { get; init; } // 0 = game; non-zero are bundles/etc. — skip
}

public class GameDetails
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("appid")] public long AppId { get; init; }
    [JsonPropertyName("type")] public string Type { get; init; } = "";
    [JsonPropertyName("baseAppId")] public string? BaseAppId { get; init; }
    [JsonPropertyName("genres")] public List<string> Genres { get; init; } = [];
    [JsonPropertyName("headerImage")] public string? HeaderImage { get; init; }
    [JsonPropertyName("releaseDate")] public string? ReleaseDate { get; init; }

    [JsonIgnore] public bool IsDlc => string.Equals(Type, "dlc", StringComparison.OrdinalIgnoreCase);
}

public class DlcDepot
{
    [JsonPropertyName("depotId")] public string DepotId { get; init; } = "";
    [JsonPropertyName("language")] public string? Language { get; init; }
    [JsonPropertyName("oslist")] public string? OsList { get; init; }
    [JsonPropertyName("included")] public bool Included { get; init; }

    [JsonIgnore]
    public string Meta
    {
        get
        {
            var parts = new List<string> { Language ?? "default" };
            if (!string.IsNullOrEmpty(OsList)) parts.Add(OsList);
            return string.Join(" · ", parts);
        }
    }
}

public class DlcInfo
{
    [JsonPropertyName("appid")] public string AppId { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("type")] public string Type { get; init; } = "";
    [JsonPropertyName("depotCount")] public int DepotCount { get; init; }
    [JsonPropertyName("haveCount")] public int HaveCount { get; init; }
    [JsonPropertyName("missingCount")] public int MissingCount { get; init; }
    [JsonPropertyName("depots")] public List<DlcDepot> Depots { get; init; } = [];
}

/// <summary>
/// Hubcap (hubcapmanifest.com) <c>/api/v1/user/stats</c> response — usage for the user's own key.
///
/// <para>
/// The live response also carries <c>user_id</c> and <c>username</c>, which are the user's Discord
/// identity. Both are deliberately NOT mapped: the app has never had a use for either, and binding them
/// would park a durable identifier in a property of a view-model that lives for the whole process. Not
/// mapping a field does not stop it arriving in the JSON, but it does stop the app retaining it.
/// </para>
/// </summary>
public class HubcapStats
{
    [JsonPropertyName("daily_usage")] public int DailyUsage { get; init; }
    [JsonPropertyName("daily_limit")] public int DailyLimit { get; init; }
    [JsonPropertyName("can_make_requests")] public bool CanMakeRequests { get; init; }
    [JsonPropertyName("api_key_expires_at")] public string? ApiKeyExpiresAt { get; init; }
}

/// <summary>
/// Hubcap <c>/api/v1/status/{appid}</c> response — whether a manifest exists (free, no usage count).
///
/// <para>
/// Hubcap regenerates manifests on its own, so this endpoint reports more than mere existence: how big
/// the file is, when it last changed, and whether Hubcap itself considers it stale. Those were being
/// discarded, which is why the app could only ever say "available" and never "you have an older copy".
/// </para>
///
/// <para>
/// KNOWN GAP: <see cref="FileModified"/> and <see cref="NeedsUpdate"/> are parsed and stored, and nothing
/// reads them yet — the Manage page still shows only "available". Wiring them up is a feature, not a
/// cleanup, and the missing half is local: the app records no install timestamp or source version for a
/// manifest, so there is nothing on this side to compare Hubcap's date against. <c>LuaInstaller</c> stamps
/// <c>File.SetLastWriteTime</c> on what it writes, but that is the time of the WRITE, not of the manifest
/// Hubcap built, and it is rewritten by any unrelated re-install — comparing it would report "outdated" for
/// a current copy and "current" for a stale one. Doing this properly means persisting the source's
/// <c>file_modified</c> alongside each installed manifest, and a timezone decision on a value the API sends
/// with no offset. Deliberately not attempted as part of a low-risk pass.
/// </para>
/// </summary>
public class HubcapManifestStatus
{
    [JsonPropertyName("status")] public string Status { get; init; } = "";
    [JsonPropertyName("manifest_file_exists")] public bool ManifestFileExists { get; init; }

    /// <summary>Hubcap's own name for the app. Useful as a cross-check against the Steam-side name.</summary>
    [JsonPropertyName("game_name")] public string? GameName { get; init; }

    /// <summary>Size of the manifest zip in bytes. Lets a download show real progress from the first byte
    /// instead of waiting on a Content-Length that may never arrive.</summary>
    [JsonPropertyName("file_size")] public long? FileSize { get; init; }

    /// <summary>When Hubcap last rebuilt this manifest. Kept as the raw string, matching
    /// <see cref="HubcapStats.ApiKeyExpiresAt"/> — the API sends no timezone offset, so parsing it here
    /// would silently reinterpret it in whatever zone the machine happens to be in.</summary>
    [JsonPropertyName("file_modified")] public string? FileModified { get; init; }

    /// <summary>Hubcap's own verdict on whether its copy is out of date.</summary>
    [JsonPropertyName("needs_update")] public bool NeedsUpdate { get; init; }

    /// <summary>Why <see cref="NeedsUpdate"/> reads as it does, e.g. "manifest_current".</summary>
    [JsonPropertyName("update_reason")] public string? UpdateReason { get; init; }
}

public class ApiError
{
    [JsonPropertyName("error")] public string? Error { get; init; }
}

/// <summary>The lua.tools standard daily download usage (counted from user_downloads, limit 25/day).</summary>
public record StandardUsage(int Used, int Limit);

public class SupporterStatus
{
    [JsonPropertyName("isSupporter")] public bool IsSupporter { get; init; }
}

/// <summary>Response from /api/auth/code/redeem — a Discord bot login code exchanged for a magic-link token.</summary>
public class CodeRedeemResponse
{
    [JsonPropertyName("user_id")] public string UserId { get; init; } = "";
    [JsonPropertyName("token")] public string Token { get; init; } = "";
}

// ── Supabase auth DTOs ──────────────────────────────────────────────

public class SupabaseSession
{
    [JsonPropertyName("access_token")] public string AccessToken { get; init; } = "";
    [JsonPropertyName("refresh_token")] public string RefreshToken { get; init; } = "";
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; init; }
    [JsonPropertyName("user")] public SupabaseUser? User { get; init; }
}

public class SupabaseUser
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("email")] public string? Email { get; init; }
    [JsonPropertyName("user_metadata")] public UserMetadata? Metadata { get; init; }
}

public class UserMetadata
{
    [JsonPropertyName("full_name")] public string? FullName { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("avatar_url")] public string? AvatarUrl { get; init; }
    [JsonPropertyName("custom_claims")] public CustomClaims? CustomClaims { get; init; }
}

public class CustomClaims
{
    [JsonPropertyName("global_name")] public string? GlobalName { get; init; }
}

/// <summary>Persisted (DPAPI-encrypted) auth state.</summary>
public class StoredAuth
{
    public string RefreshToken { get; init; } = "";
    public string AccessToken { get; init; } = "";
    public DateTimeOffset ExpiresAt { get; init; }
    public string? DisplayName { get; init; }
    public string? Email { get; init; }
    public string? AvatarUrl { get; init; }
}

/// <summary>Per-source UI metadata, mirroring src/lib/source-meta.ts on the website.</summary>
public static class SourceMeta
{
    public record Meta(string? DisplayName = null, string? DiscordUrl = null, bool RequiresUserKey = false);

    public static readonly Dictionary<string, Meta> All = new()
    {
        ["Ryuu"] = new(DiscordUrl: "https://discord.gg/manifests"),
        ["TwentyTwo Cloud"] = new(DiscordUrl: "https://discord.gg/RrukXPyv5b"),
        ["Sushi"] = new(DiscordUrl: "https://discord.gg/hMdv5dQhcN"),
        ["Skyflare"] = new(DiscordUrl: "https://discord.gg/luatools"),
        ["Sadie (Morrenus)"] = new(DisplayName: "Sadie (Hubcap)", DiscordUrl: "https://discord.gg/hubcapsmanifest", RequiresUserKey: true),
    };

    public static Meta Get(string name) => All.TryGetValue(name, out var m) ? m : new Meta();
}
