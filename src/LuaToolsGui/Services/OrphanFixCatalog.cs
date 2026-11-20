using System.Text.Json;
using LuaToolsGui.Models;

namespace LuaToolsGui.Services;

/// <summary>One downloadable slot of an orphan fix: a plain public https link and the SHA-256 the file must have.</summary>
public sealed record OrphanDownload(string Url, string Sha256);

/// <summary>A fix that exists only in LuaTools-Catalog. <see cref="Downloads"/> is keyed by slot ("manifest"/"fix").</summary>
public sealed record OrphanFix(DenuvoFix Fix, IReadOnlyDictionary<string, OrphanDownload> Downloads);

/// <summary>The orphan fixes of one game, plus the game's own name and image (for a game lua.tools lacks).</summary>
public sealed record OrphanGame(string AppId, string Name, string? HeaderImage, IReadOnlyList<OrphanFix> Fixes);

/// <summary>
/// Reads the <c>fixes[]</c> of a LuaTools-Catalog <c>catalog.json</c> body — the part <see cref="DenuvoListingsResponse"/>
/// does not model and must not (the GUI's listing type stays as it is).
///
/// <para>
/// The catalog is fetched from a repository, so what it says is checked again here instead of trusted because
/// the sync script already did: a slot only survives with an <see cref="LuaToolsApiClient.IsFollowableSignedUrl"/>
/// link and a parseable SHA-256, a fix only with a slot that survived, a game only with a fix that did. Anything
/// that does not parse is dropped, never repaired — a missing orphan fix is a smaller catalog, a wrong one is a
/// file written into a game folder.
/// </para>
/// </summary>
internal static class OrphanFixCatalog
{
    private static readonly string[] Slots = ["manifest", "fix"];

    /// <summary>appid → orphan fixes. Empty for a body with none (lua.tools' own listing, say) or one that is not JSON.</summary>
    internal static IReadOnlyDictionary<string, OrphanGame> Parse(string body)
    {
        var games = new Dictionary<string, OrphanGame>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("games", out var list)
                || list.ValueKind != JsonValueKind.Array)
                return games;

            foreach (var raw in list.EnumerateArray())
            {
                if (ParseGame(raw) is { } game) games[game.AppId] = game;
            }
        }
        catch (JsonException ex)
        {
            AppLog.Log($"Fixes: catalog orphan fixes unreadable: {ex.Message}");
            games.Clear();
        }
        return games;
    }

    private static OrphanGame? ParseGame(JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object
            || !raw.TryGetProperty("fixes", out var fixes) || fixes.ValueKind != JsonValueKind.Array
            || Text(raw, "appid") is not { Length: > 0 } appId)
            return null;

        var parsed = new List<OrphanFix>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawFix in fixes.EnumerateArray())
        {
            if (ParseFix(rawFix) is { } fix && ids.Add(fix.Fix.Id)) parsed.Add(fix);
        }
        return parsed.Count == 0
            ? null
            : new OrphanGame(appId, Text(raw, "name") ?? appId, Text(raw, "header_image"), parsed);
    }

    private static OrphanFix? ParseFix(JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object || Text(raw, "id") is not { Length: > 0 } id) return null;

        var downloads = new Dictionary<string, OrphanDownload>();
        if (raw.TryGetProperty("downloads", out var slots) && slots.ValueKind == JsonValueKind.Object)
        {
            foreach (string slot in Slots)
            {
                if (slots.TryGetProperty(slot, out var item) && item.ValueKind == JsonValueKind.Object
                    && Text(item, "url") is { } url && LuaToolsApiClient.IsFollowableSignedUrl(url)
                    && AssetIntegrity.ParseDigest(Text(item, "sha256")) is { } sha256)
                    downloads[slot] = new OrphanDownload(url, sha256);
            }
        }
        if (downloads.Count == 0) return null;

        var tags = new List<DenuvoTag>();
        if (raw.TryGetProperty("tags", out var rawTags) && rawTags.ValueKind == JsonValueKind.Array)
        {
            foreach (var tag in rawTags.EnumerateArray())
                if (tag.ValueKind == JsonValueKind.Object && Text(tag, "id") is { } tagId)
                    tags.Add(new DenuvoTag
                    {
                        Id = tagId,
                        Name = Text(tag, "name") ?? "",
                        Slug = Text(tag, "slug") ?? "",
                        Color = Text(tag, "color"),
                    });
        }

        // The flags describe what survived the checks above, not what the file claimed.
        var fix = new DenuvoFix
        {
            Id = id,
            Title = Text(raw, "title") ?? id,
            Description = Text(raw, "description"),
            Tags = tags,
            HasManifest = downloads.ContainsKey("manifest"),
            HasFix = downloads.ContainsKey("fix"),
            ManifestFilename = downloads.ContainsKey("manifest") ? Text(raw, "manifestFilename") : null,
            FixFilename = downloads.ContainsKey("fix") ? Text(raw, "fixFilename") : null,
            CreatedAt = Text(raw, "createdAt"),
        };
        return new OrphanFix(fix, downloads);
    }

    private static string? Text(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
