using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using AwesomeAssertions;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// docs/code-audit-2026-11-02.md M3: the whole <c>SteamAppListCache.LoadAsync</c> recovery chain (fresh cache →
/// Morrenus → Steam → stale cache) end to end, through the internal seam: a temp cache file and a stub
/// handler routed by host. No %AppData%, no network.
/// </summary>
public sealed class SteamAppListCacheLoadTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "luatools-applist-" + Guid.NewGuid().ToString("N"));
    private string CacheFile => Path.Combine(_dir, "steam-applist.json");

    public SteamAppListCacheLoadTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* best effort */ }
    }

    private const string MorrenusHost = "applist.morrenus.xyz";
    private const string SteamHost = "api.steampowered.com";

    private const string MorrenusBody = """[{"appid":480,"name":"Spacewar"},{"appid":730,"name":"Counter-Strike 2"}]""";
    private const string SteamBody = """{"applist":{"apps":[{"appid":10,"name":"Counter-Strike"}]}}""";

    /// <summary>Answers per host and counts what was asked. A null body means "500".</summary>
    private sealed class HostStub(string? morrenus, string? steam) : HttpMessageHandler
    {
        public List<string> Hosts { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string host = request.RequestUri!.Host;
            Hosts.Add(host);
            string? body = host == MorrenusHost ? morrenus : host == SteamHost ? steam : null;
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) });
        }
    }

    private void WriteCache(string json, TimeSpan age)
    {
        File.WriteAllText(CacheFile, json);
        File.SetLastWriteTimeUtc(CacheFile, DateTime.UtcNow - age);
    }

    [Fact]
    public async Task A_fresh_cache_is_used_and_nothing_is_downloaded()
    {
        WriteCache("""{"480":"Spacewar"}""", TimeSpan.FromDays(1));
        var stub = new HostStub(MorrenusBody, SteamBody);
        var cache = new SteamAppListCache(CacheFile, stub);

        await cache.EnsureLoadedAsync();

        cache.GetName(480).Should().Be("Spacewar");
        cache.GetName(730).Should().BeNull("the fresh file is the whole answer");
        stub.Hosts.Should().BeEmpty();
    }

    [Fact]
    public async Task With_no_cache_Morrenus_is_downloaded_and_persisted()
    {
        var stub = new HostStub(MorrenusBody, SteamBody);
        var cache = new SteamAppListCache(CacheFile, stub);

        await cache.EnsureLoadedAsync();

        cache.GetName(730).Should().Be("Counter-Strike 2");
        stub.Hosts.Should().Equal(MorrenusHost);
        (await SteamAppListCache.ReadListAsync(CacheFile)).Should()
            .BeEquivalentTo(new Dictionary<long, string> { [480] = "Spacewar", [730] = "Counter-Strike 2" });
    }

    [Fact]
    public async Task An_expired_cache_is_replaced_by_a_fresh_download()
    {
        WriteCache("""{"1":"old"}""", TimeSpan.FromDays(15));
        var cache = new SteamAppListCache(CacheFile, new HostStub(MorrenusBody, SteamBody));

        await cache.EnsureLoadedAsync();

        cache.GetName(480).Should().Be("Spacewar");
        (await SteamAppListCache.ReadListAsync(CacheFile)).Should().NotContainKey(1);
    }

    [Fact]
    public async Task When_Morrenus_fails_Steam_answers_with_its_nested_shape()
    {
        var stub = new HostStub(morrenus: null, steam: SteamBody);
        var cache = new SteamAppListCache(CacheFile, stub);

        await cache.EnsureLoadedAsync();

        cache.GetName(10).Should().Be("Counter-Strike");
        stub.Hosts.Should().Equal(MorrenusHost, SteamHost);
        (await SteamAppListCache.ReadListAsync(CacheFile)).Should().ContainKey(10);
    }

    [Fact]
    public async Task When_both_sources_fail_the_stale_cache_is_still_used()
    {
        WriteCache("""{"480":"Spacewar"}""", TimeSpan.FromDays(30));
        var stub = new HostStub(null, null);
        var cache = new SteamAppListCache(CacheFile, stub);

        await cache.EnsureLoadedAsync();

        cache.GetName(480).Should().Be("Spacewar");
        stub.Hosts.Should().Equal(MorrenusHost, SteamHost);
    }

    [Fact]
    public async Task When_both_sources_fail_and_there_is_no_cache_the_list_is_just_empty()
    {
        var cache = new SteamAppListCache(CacheFile, new HostStub(null, null));

        await cache.EnsureLoadedAsync(); // must not throw

        cache.GetName(480).Should().BeNull();
        File.Exists(CacheFile).Should().BeFalse("nothing was loaded, so nothing is written");
    }

    [Fact]
    public async Task A_corrupt_fresh_cache_falls_through_to_the_download()
    {
        WriteCache("{ this is not json", TimeSpan.FromDays(1));
        var stub = new HostStub(MorrenusBody, SteamBody);
        var cache = new SteamAppListCache(CacheFile, stub);

        await cache.EnsureLoadedAsync();

        cache.GetName(480).Should().Be("Spacewar");
        stub.Hosts.Should().Equal(MorrenusHost);
        (await SteamAppListCache.ReadListAsync(CacheFile)).Should().ContainKey(480, "the corrupt file was replaced");
    }

    [Fact]
    public async Task A_corrupt_cache_with_both_sources_down_leaves_an_empty_list_without_throwing()
    {
        WriteCache("{ this is not json", TimeSpan.FromDays(30));
        var cache = new SteamAppListCache(CacheFile, new HostStub(null, null));

        await cache.EnsureLoadedAsync();

        cache.GetName(480).Should().BeNull();
    }

    [Fact]
    public async Task A_corrupt_Morrenus_body_falls_back_to_Steam()
    {
        var cache = new SteamAppListCache(CacheFile, new HostStub("[{\"appid\":480,", SteamBody));

        await cache.EnsureLoadedAsync();

        cache.GetName(10).Should().Be("Counter-Strike");
        cache.GetName(480).Should().BeNull("a truncated list never lands half-loaded");
    }

    [Fact]
    public async Task A_malformed_row_is_skipped_and_the_rest_of_the_list_still_loads()
    {
        const string body = """[{"appid":480,"name":"Spacewar"},{"appid":1},{"name":"no id"},{"appid":"x","name":"bad id"},{"appid":2,"name":7},"junk",{"appid":730,"name":"Counter-Strike 2"}]""";
        var cache = new SteamAppListCache(CacheFile, new HostStub(body, SteamBody));

        await cache.EnsureLoadedAsync();

        cache.GetName(480).Should().Be("Spacewar");
        cache.GetName(730).Should().Be("Counter-Strike 2", "rows after the bad ones are not lost");
        cache.GetName(1).Should().BeNull();
        cache.GetName(10).Should().BeNull("Morrenus answered, so Steam was never asked");
    }

    [Fact]
    public async Task A_source_with_the_wrong_shape_falls_back_to_the_stale_cache()
    {
        WriteCache("""{"480":"Spacewar"}""", TimeSpan.FromDays(30));
        var cache = new SteamAppListCache(CacheFile, new HostStub("""{"not":"an array"}""", null));

        await cache.EnsureLoadedAsync();

        cache.GetName(480).Should().Be("Spacewar");
    }

    [Fact]
    public async Task Repeated_calls_load_once()
    {
        var stub = new HostStub(MorrenusBody, SteamBody);
        var cache = new SteamAppListCache(CacheFile, stub);

        await Task.WhenAll(cache.EnsureLoadedAsync(), cache.EnsureLoadedAsync());
        await cache.EnsureLoadedAsync();

        stub.Hosts.Should().Equal(MorrenusHost);
    }
}

/// <summary>
/// docs/code-audit-2026-11-02.md M2: <c>GetCachedAsync</c>, <c>PreloadAsync</c>, <c>GetOverviewAsync</c>,
/// <c>ResolveGameDetailsAsync</c>, and <c>GetFilterData</c> run against an isolated temp details directory
/// via the internal seam — zero reads or writes in the machine's real <c>%AppData%</c>.
/// </summary>
public sealed class SteamAppInfoCacheAsyncTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "luatools_appinfo_" + Guid.NewGuid().ToString("N"));
    private string DetailsDir => Path.Combine(_dir, "details");

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private SteamAppInfoCache NewCache(HttpMessageHandler? handler = null) =>
        new(new CacheService(_dir), handler ?? new NeverCalled(), DetailsDir);

    private sealed class NeverCalled : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException("the async disk read must not touch the network");
    }

    private sealed class StaticJsonHandler(string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) });
        }
    }

    private string WriteBlob(SteamAppInfoCache cache, long appid, string json)
    {
        string path = cache.DetailsPath(appid);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Details_path_is_isolated_to_the_injected_directory()
    {
        var cache = NewCache();
        string path = cache.DetailsPath(9_999_999_800);

        Path.GetFullPath(path).Should().StartWith(Path.GetFullPath(DetailsDir) + Path.DirectorySeparatorChar);
    }

    [Fact]
    public async Task A_miss_is_read_from_the_blob_and_then_memoized()
    {
        const long id = 9_999_999_801;
        var cache = NewCache();
        string path = WriteBlob(cache, id, """{"name":"Spacewar","header_image":"https://x/h.jpg"}""");

        var info = await cache.GetCachedAsync(id);

        info.Should().Be(new SteamAppInfo("Spacewar", "https://x/h.jpg"));
        File.Delete(path);
        cache.GetCached(id).Should().Be(info, "the sync path is now a RAM hit, with no disk read");
        (await cache.GetCachedAsync(id)).Should().Be(info);
    }

    [Fact]
    public async Task A_missing_or_corrupt_blob_resolves_to_null_without_throwing()
    {
        const long corrupt = 9_999_999_802, missing = 9_999_999_803, delisted = 9_999_999_804;
        var cache = NewCache();
        WriteBlob(cache, corrupt, "{ not json");
        WriteBlob(cache, delisted, "{}");

        (await cache.GetCachedAsync(corrupt)).Should().BeNull();
        (await cache.GetCachedAsync(missing)).Should().BeNull();
        (await cache.GetCachedAsync(delisted)).Should().BeNull();
    }

    [Fact]
    public async Task Concurrent_lookups_of_one_appid_all_get_the_same_answer()
    {
        const long id = 9_999_999_805;
        var cache = NewCache();
        WriteBlob(cache, id, """{"name":"Portal"}""");

        var results = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => cache.GetCachedAsync(id)));

        results.Should().OnlyContain(r => r != null && r.Name == "Portal");
    }

    [Fact]
    public async Task Preload_warms_the_sync_path_for_a_whole_batch()
    {
        const long a = 9_999_999_806, b = 9_999_999_807;
        var cache = NewCache();
        string pathA = WriteBlob(cache, a, """{"name":"A"}""");
        string pathB = WriteBlob(cache, b, """{"name":"B"}""");

        await cache.PreloadAsync([a, b, a]);
        File.Delete(pathA);
        File.Delete(pathB);

        cache.GetCached(a)!.Name.Should().Be("A");
        cache.GetCached(b)!.Name.Should().Be("B");
    }

    [Fact]
    public async Task A_cancelled_token_stops_the_off_UI_read()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var cache = NewCache();

        Func<Task> miss = () => cache.GetCachedAsync(9_999_999_808, cts.Token);
        Func<Task> preload = () => cache.PreloadAsync([9_999_999_808], cts.Token);
        Func<Task> overview = () => cache.GetOverviewAsync(9_999_999_808, cts.Token);

        await miss.Should().ThrowAsync<OperationCanceledException>();
        await preload.Should().ThrowAsync<OperationCanceledException>();
        await overview.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GetOverviewAsync_reads_from_isolated_blob_and_memoizes()
    {
        const long id = 9_999_999_809;
        var cache = NewCache();
        string path = WriteBlob(cache, id, """
            {"short_description":"Build &amp; explore","developers":["Studio A"],"publishers":["Pub B"],
             "genres":[{"description":"RPG"}]}
            """);

        var overview = await cache.GetOverviewAsync(id);

        overview.Should().NotBeNull();
        overview!.ShortDescription.Should().Be("Build & explore");
        overview.DeveloperLabel.Should().Be("Studio A");
        overview.HasDistinctPublisher.Should().BeTrue();
        File.Delete(path);
        (await cache.GetOverviewAsync(id)).Should().BeSameAs(overview, "subsequent calls hit RAM");
    }

    [Fact]
    public async Task ResolveGameDetailsAsync_reads_cached_blob_off_thread_without_network()
    {
        const long id = 9_999_999_810;
        var cache = NewCache();
        WriteBlob(cache, id, """{"steam_appid":9999999810,"name":"Half-Life","type":"game","genres":[{"description":"Action"}]}""");

        var details = await cache.ResolveGameDetailsAsync(id);

        details.Should().NotBeNull();
        details!.Name.Should().Be("Half-Life");
        details.AppId.Should().Be(id);
    }

    [Fact]
    public async Task GetFilterData_negative_memo_is_invalidated_when_EnsureFullDetailsAsync_saves_the_blob()
    {
        const long id = 9_999_999_811;
        var stub = new StaticJsonHandler($$"""
            {"{{id}}":{"success":true,"data":{"steam_appid":{{id}},"type":"game","name":"Test",
              "is_free":true,"genres":[{"description":"Indie"}]} } }
            """);
        var cache = NewCache(stub);

        cache.GetFilterData(id).Should().BeNull("no blob on disk yet; memoizes miss");
        (await cache.EnsureFullDetailsAsync(id)).Should().BeTrue();

        var data = cache.GetFilterData(id);
        data.Should().NotBeNull("saving the blob invalidated the negative filter memo");
        data!.IsFree.Should().BeTrue();
        data.Genres.Should().Equal("Indie");
        stub.Calls.Should().Be(1);
    }
}
