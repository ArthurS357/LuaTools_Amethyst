using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// How an appdetails response is matched back to the appid that was requested. This is load-bearing and
/// it is not obvious: since ~Sep 2026 Steam keys the response by a CHILD app's id (a DLC or a soundtrack)
/// for any app that has one, while the inner data.steam_appid still echoes what was asked for. Before
/// that was handled, GetProperty(appid) threw KeyNotFoundException straight into a bare catch, so every
/// popular game silently failed to resolve and the Manage backfill re-requested it forever.
/// </summary>
/// <remarks>
/// The first nine cases are ported from upstream LuaTools v1.3.2 (commit 0485dec). The rest pin shapes
/// measured against the live API on 2026-09-25, when appids=730 answered under "2678630".
/// </remarks>
public class SteamAppInfoCacheTests
{
    /// <summary>The returned element points into the document's buffer, so clone before it is disposed.</summary>
    private static JsonElement? Find(string json, long appid)
    {
        using var doc = JsonDocument.Parse(json);
        return SteamAppInfoCache.FindAppDetailsEntry(doc.RootElement, appid)?.Clone();
    }

    private static string Name(JsonElement? entry) =>
        entry!.Value.GetProperty("data").GetProperty("name").GetString()!;

    /// <summary>The bug itself: 500 ("Left 4 Dead") answers under 1660740 ("Left 4 Dead - Uncensored").</summary>
    [Fact]
    public void ChildKeyedResponse_IsMatchedByInnerSteamAppid()
    {
        var entry = Find("""{"1660740":{"success":true,"data":{"steam_appid":500,"name":"Left 4 Dead"}}}""", 500);

        Assert.NotNull(entry);
        Assert.Equal("Left 4 Dead", Name(entry));
    }

    /// <summary>Apps with no child apps are still keyed normally, and must keep working unchanged.</summary>
    [Fact]
    public void SameKeyedResponse_StillResolves()
    {
        var entry = Find("""{"10":{"success":true,"data":{"steam_appid":10,"name":"Counter-Strike"}}}""", 10);

        Assert.NotNull(entry);
        Assert.Equal("Counter-Strike", Name(entry));
    }

    /// <summary>
    /// A failed lookup has no "data" to anchor on, so it can only ever be found by its key. It has to be
    /// found: that is what writes the "{}" delisted marker that stops the backfill retrying the app.
    /// </summary>
    [Fact]
    public void FailedLookup_IsStillFoundByItsKey()
    {
        var entry = Find("""{"999999999":{"success":false}}""", 999999999);

        Assert.NotNull(entry);
        Assert.False(entry!.Value.GetProperty("success").GetBoolean());
    }

    /// <summary>
    /// The important refusal. There is exactly one entry and it looks plausible, but it describes a
    /// different app. Taking it would write the wrong game's name and art into details\&lt;appid&gt;.json
    /// permanently, so "no match" is the correct answer even though it resolves nothing.
    /// </summary>
    [Fact]
    public void EntryForAnotherApp_IsRefusedRatherThanGuessed() =>
        Assert.Null(Find("""{"1660740":{"success":true,"data":{"steam_appid":1660740,"name":"Uncensored"}}}""", 500));

    /// <summary>An unprovable payload is refused rather than assumed, in every malformed variant.</summary>
    [Theory]
    [InlineData("{}")]                                                             // empty object
    [InlineData("[]")]                                                             // not an object at all
    [InlineData("""{"1660740":{"success":true,"data":{"name":"L4D"}}}""")]          // no steam_appid
    [InlineData("""{"1660740":{"success":true,"data":{"steam_appid":"500"}}}""")]   // string, not number
    [InlineData("""{"1660740":{"success":true}}""")]                                // success but no data
    [InlineData("""{"1660740":"nonsense"}""")]                                      // entry isn't an object
    public void UnprovablePayloads_AreRefused(string json) => Assert.Null(Find(json, 500));

    /// <summary>
    /// Documents the ordering: the direct key wins over the scan, so the cheap path stays the common path
    /// and a sibling entry can never shadow the app's own.
    /// </summary>
    [Fact]
    public void DirectKey_WinsOverTheScan()
    {
        var entry = Find("""
            {"500":{"success":true,"data":{"steam_appid":500,"name":"Left 4 Dead"}},
             "1660740":{"success":true,"data":{"steam_appid":500,"name":"Uncensored"}}}
            """, 500);

        Assert.Equal("Left 4 Dead", Name(entry));
    }

    /// <summary>
    /// If Steam ever keys an entry by our appid but fills it with another app's data, the key is not
    /// trusted either — data.steam_appid is the anchor, not the key.
    /// </summary>
    [Fact]
    public void DirectKeyDescribingAnotherApp_IsRefused() =>
        Assert.Null(Find("""{"500":{"success":true,"data":{"steam_appid":1660740,"name":"Uncensored"}}}""", 500));

    // ── Shapes measured against the live API (2026-09-25) ─────────────

    /// <summary>
    /// The exact shape that broke 1.7.5 in the field: appids=730 answered under "2678630". Trimmed from a
    /// real response, with the fields the app actually reads, so the matched entry is proven to carry the
    /// whole blob that gets persisted — not just a name.
    /// </summary>
    [Fact]
    public void CounterStrike2_AnsweredUnderAChildKey_ResolvesWithItsFullData()
    {
        var entry = Find("""
            {"2678630":{"success":true,"data":{
              "type":"game","name":"Counter-Strike 2","steam_appid":730,"is_free":true,
              "short_description":"For over two decades, Counter-Strike has offered an elite competitive experience.",
              "header_image":"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/730/header.jpg",
              "developers":["Valve"],"publishers":["Valve"],
              "platforms":{"windows":true,"mac":false,"linux":true},
              "genres":[{"id":"1","description":"Action"},{"id":"37","description":"Free To Play"}],
              "release_date":{"coming_soon":false,"date":"21 Aug, 2012"}}}}
            """, 730);

        Assert.NotNull(entry);
        var data = entry!.Value.GetProperty("data");
        Assert.Equal(730, data.GetProperty("steam_appid").GetInt64());
        Assert.Equal("Counter-Strike 2", data.GetProperty("name").GetString());
        Assert.EndsWith("/730/header.jpg", data.GetProperty("header_image").GetString());
        Assert.Equal(2, data.GetProperty("genres").GetArrayLength());
    }

    /// <summary>
    /// The same child key queried on its own must not be mistaken for the parent. Asking for 2678630 when
    /// the only entry is CS2's (steam_appid 730) resolves nothing — otherwise a DLC row would be named, and
    /// its details file written, as the base game.
    /// </summary>
    [Fact]
    public void ChildId_DoesNotResolveToItsParentsData() =>
        Assert.Null(Find("""{"2678630":{"success":true,"data":{"steam_appid":730,"name":"Counter-Strike 2"}}}""", 2678630));

    /// <summary>
    /// Every child-keyed pair seen live, one entry each. If Steam goes back to keying by the requested id,
    /// <see cref="SameKeyedResponse_StillResolves"/> already covers it; this pins the current shape.
    /// </summary>
    [Theory]
    [InlineData(730, 2678630)]
    [InlineData(500, 1660740)]
    [InlineData(620, 323180)]
    [InlineData(570, 2120612)]
    [InlineData(1091500, 2441600)]
    [InlineData(1245620, 2855530)]
    public void MeasuredChildKeys_AllResolveToTheRequestedApp(long appid, long childKey)
    {
        // Spaced closing braces: $$ raw strings allow at most one literal brace in a row.
        var entry = Find($$"""{"{{childKey}}":{"success":true,"data":{"steam_appid":{{appid}},"name":"x"} } }""", appid);

        Assert.NotNull(entry);
        Assert.Equal(appid, entry!.Value.GetProperty("data").GetProperty("steam_appid").GetInt64());
    }
}

/// <summary>
/// GetCached memoizes "no blob on disk" as null so grid renders don't re-read the disk. ResolveAsync used
/// to take that memo as an answer, so <c>GetCached(id) ?? await ResolveAsync(id)</c> — the pattern every
/// name/cover path uses — never fetched a game added this session: blank until the app restarted.
/// </summary>
public sealed class SteamAppInfoCacheNegativeMemoTests : IDisposable
{
    // No details\<appid>.json will ever exist for this id, and the stub answers 500, so nothing is read
    // from or written to the real %AppData% details dir.
    private const long AppId = 9_999_999_999;

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "luatools_appinfo_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
    }

    [Fact]
    public async Task NegativeMemo_WithNoBlobOnDisk_StillHitsTheNetwork()
    {
        var stub = new StubHandler();
        var appInfo = new SteamAppInfoCache(new CacheService(_dir), stub);

        Assert.Null(appInfo.GetCached(AppId)); // memoizes the miss

        await appInfo.ResolveAsync(AppId);

        Assert.Equal(1, stub.Calls);
    }
}
