using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// F2 phase 2: the Fixes catalog is read from LuaTools-Catalog on GitHub first, lua.tools second, the disk
/// copy last; orphan fixes from that catalog are listed beside lua.tools' own and downloaded from the link in
/// <c>catalog.json</c>, fail-closed on the SHA-256.
///
/// <para>
/// Both HTTP clients are replaced by stubs (the private <c>_http</c> and <c>FixDownloadHttp</c>, by
/// reflection, as the other API-client tests do); the production handler chain is not touched.
/// </para>
/// </summary>
public class CatalogFallbackTests : IDisposable
{
    private const string CatalogHost = "raw.githubusercontent.com";
    private const string FixUrl = "https://files.example.com/orphan/fix.zip";
    private static readonly byte[] FixBytes = Encoding.UTF8.GetBytes("PK-pretend-fix-archive");
    private static readonly string FixSha = Convert.ToHexString(SHA256.HashData(FixBytes)).ToLowerInvariant();

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "luatools-tests", Guid.NewGuid().ToString("N"));
    private readonly List<string> _staged = [];

    public CatalogFallbackTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (string path in _staged) try { File.Delete(path); } catch (IOException) { }
        try { Directory.Delete(_dir, true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private static string Listing(params (string AppId, string Name)[] games) =>
        "{\"games\":[" + string.Join(",", games.Select(g =>
            $"{{\"appid\":\"{g.AppId}\",\"name\":\"{g.Name}\",\"header_image\":null,\"fixCount\":1,\"tags\":[]}}")) + "],\"tags\":[]}";

    /// <summary>A catalog.json body: one lua.tools game, one merged game and one orphan-only game.</summary>
    private static string CatalogBody(string sha = "", string url = FixUrl, string extraFixId = "orphan-1")
    {
        sha = sha.Length == 0 ? FixSha : sha;
        string fix(string id) =>
            $"{{\"id\":\"{id}\",\"title\":\"Orphan {id}\",\"description\":null,\"tags\":[],\"hasManifest\":false,\"hasFix\":true," +
            $"\"manifestFilename\":null,\"fixFilename\":\"fix.zip\",\"createdAt\":\"2026-11-20\"," +
            $"\"downloads\":{{\"fix\":{{\"url\":\"{url}\",\"sha256\":\"{sha}\"}}}}}}";
        string game(string appid, string name, string source, int count, string fixes) =>
            $"{{\"appid\":\"{appid}\",\"name\":\"{name}\",\"header_image\":null,\"fixCount\":{count},\"tags\":[],\"source\":\"{source}\"{fixes}}}";
        return "{\"schema_version\":1,\"tags\":[],\"games\":[" +
            game("100", "Official", "lua.tools", 1, "") + "," +
            game("200", "Merged", "merged", 3, $",\"fixes\":[{fix(extraFixId)}]") + "," +
            game("300", "Orphan only", "orphan", 1, $",\"fixes\":[{fix("orphan-1")}]") + "]}";
    }

    /// <summary>Routes by host: the catalog host and lua.tools each get their own responder.</summary>
    private sealed class Router : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Catalog { get; set; } =
            (_, _) => throw new HttpRequestException("GitHub is unreachable");
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> LuaTools { get; set; } =
            (_, _) => throw new HttpRequestException("lua.tools is unreachable");
        public List<Uri> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add(request.RequestUri!);
            return (request.RequestUri!.Host == CatalogHost ? Catalog : LuaTools)(request, ct);
        }
    }

    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Ok(string body) =>
        (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });

    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Status(HttpStatusCode code) =>
        (_, _) => Task.FromResult(new HttpResponseMessage(code));

    private LuaToolsApiClient Api(Router router, TimeSpan? catalogTimeout = null, Func<HttpRequestMessage, HttpResponseMessage>? fixHost = null)
    {
        var api = new LuaToolsApiClient(null!, null!, null!, null!, null!)
        {
            CacheDirectory = Path.Combine(_dir, "cache"),
            CatalogTimeout = catalogTimeout ?? TimeSpan.FromSeconds(15),
        };
        SetField(api, "_http", new HttpClient(router) { BaseAddress = new Uri("https://lua.tools") });
        if (fixHost is not null) SetField(api, "FixDownloadHttp", new HttpClient(new FixHost(fixHost)));
        return api;
    }

    private static void SetField(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!.SetValue(target, value);

    private sealed class FixHost(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    // ── The three tiers ───────────────────────────────────────────────────────

    [Fact]
    public async Task The_catalog_on_GitHub_is_tried_first_and_lua_tools_is_not_called_when_it_answers()
    {
        var router = new Router { Catalog = Ok(CatalogBody()), LuaTools = Ok(Listing(("9", "From lua.tools"))) };

        var result = await Api(router).GetDenuvoListingsAsync();

        result!.IsOffline.Should().BeFalse();
        result.Data.Games.Select(g => g.AppId).Should().Equal("100", "200", "300");
        router.Seen.Should().ContainSingle().Which.ToString().Should().Be(
            "https://raw.githubusercontent.com/ArthurS357/LuaTools-Catalog/main/catalog.json");
        AppConfig.CatalogUrl.Should().Be(router.Seen[0].ToString());
    }

    [Theory]
    [InlineData("exception")]
    [InlineData("404")]
    [InlineData("garbage")]
    public async Task When_the_catalog_fails_lua_tools_is_tried(string failure)
    {
        var router = new Router
        {
            Catalog = failure switch
            {
                "404" => Status(HttpStatusCode.NotFound),
                "garbage" => Ok("<html>not json</html>"),
                _ => (_, _) => throw new HttpRequestException("GitHub is unreachable"),
            },
            LuaTools = Ok(Listing(("9", "From lua.tools"))),
        };

        var result = await Api(router).GetDenuvoListingsAsync();

        result!.IsOffline.Should().BeFalse();
        result.Data.Games.Should().ContainSingle().Which.Name.Should().Be("From lua.tools");
        router.Seen.Select(u => u.Host).Should().Equal(CatalogHost, "lua.tools");
    }

    [Fact]
    public async Task A_catalog_that_hangs_gives_way_to_lua_tools_after_its_own_short_timeout()
    {
        var router = new Router
        {
            Catalog = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new HttpResponseMessage(); },
            LuaTools = Ok(Listing(("9", "From lua.tools"))),
        };

        var result = await Api(router, catalogTimeout: TimeSpan.FromMilliseconds(100)).GetDenuvoListingsAsync();

        result!.Data.Games.Should().ContainSingle().Which.Name.Should().Be("From lua.tools");
    }

    [Fact]
    public async Task When_both_fail_the_disk_copy_from_the_catalog_is_served_offline_with_its_orphan_fixes()
    {
        var router = new Router { Catalog = Ok(CatalogBody()) };
        await Api(router).GetDenuvoListingsAsync(); // saves the catalog copy

        var offline = Api(new Router()); // a new client: nothing in memory, both tiers down
        var result = await offline.GetDenuvoListingsAsync();

        result!.IsOffline.Should().BeTrue();
        result.Data.Games.Should().HaveCount(3);
        (await offline.GetDenuvoFixesAsync("300"))!.Fixes.Should().ContainSingle().Which.Id.Should().Be("orphan-1");
    }

    [Fact]
    public async Task A_lua_tools_answer_never_overwrites_the_catalog_copy_and_the_newest_copy_wins()
    {
        var up = new Router { Catalog = Ok(CatalogBody()) };
        await Api(up).GetDenuvoListingsAsync();
        string catalogCopy = Path.Combine(_dir, "cache", DenuvoListingsCache.CatalogFileName);
        string before = File.ReadAllText(catalogCopy);

        var luaOnly = new Router { Catalog = Status(HttpStatusCode.NotFound), LuaTools = Ok(Listing(("9", "From lua.tools"))) };
        await Api(luaOnly).GetDenuvoListingsAsync();

        File.ReadAllText(catalogCopy).Should().Be(before, "the orphan fixes in it would be lost");
        File.Exists(Path.Combine(_dir, "cache", DenuvoListingsCache.FileName)).Should().BeTrue();

        // Now the lua.tools copy is the newer one; with everything down, the newest copy is what is served.
        File.SetLastWriteTime(catalogCopy, DateTime.Now.AddHours(-2));
        var result = await Api(new Router()).GetDenuvoListingsAsync();
        result!.Data.Games.Should().ContainSingle().Which.Name.Should().Be("From lua.tools");
    }

    // ── Orphan fixes in a game's list ─────────────────────────────────────────

    private static string OfficialFixes(params string[] ids) =>
        "{\"appid\":\"200\",\"name\":\"Official name\",\"header_image\":null,\"fixes\":[" +
        string.Join(",", ids.Select(id => $"{{\"id\":\"{id}\",\"title\":\"Official {id}\",\"hasManifest\":true,\"hasFix\":true}}")) + "]}";

    [Fact]
    public async Task A_games_fixes_are_the_official_ones_then_the_orphan_ones()
    {
        var router = new Router { Catalog = Ok(CatalogBody()), LuaTools = Ok(OfficialFixes("official-a")) };
        var api = Api(router);
        await api.GetDenuvoListingsAsync();

        var fixes = (await api.GetDenuvoFixesAsync("200"))!;

        fixes.Fixes.Select(f => f.Id).Should().Equal("official-a", "orphan-1");
        fixes.Fixes[1].HasFix.Should().BeTrue();
    }

    [Fact]
    public async Task A_repeated_fix_id_keeps_the_official_fix_and_that_orphan_is_not_downloadable()
    {
        var router = new Router { Catalog = Ok(CatalogBody()), LuaTools = Ok(OfficialFixes("orphan-1")) };
        var api = Api(router);
        await api.GetDenuvoListingsAsync();

        var fixes = (await api.GetDenuvoFixesAsync("200"))!;

        fixes.Fixes.Should().ContainSingle().Which.Title.Should().Be("Official orphan-1");
        // Not listed as an orphan fix, so the download takes the lua.tools path (which needs a login; no auth
        // service here): it must NOT go to the orphan link.
        var download = () => api.DownloadDenuvoAsync("orphan-1", "fix", "fix.zip", null, appid: "200");
        await download.Should().ThrowAsync<NullReferenceException>("it reached the authenticated lua.tools path");
    }

    [Fact]
    public async Task A_game_lua_tools_does_not_know_still_lists_its_orphan_fixes()
    {
        foreach (var official in new[] { Status(HttpStatusCode.NotFound), (Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>)((_, _) => throw new HttpRequestException("down")) })
        {
            var api = Api(new Router { Catalog = Ok(CatalogBody()), LuaTools = official });
            await api.GetDenuvoListingsAsync();

            var fixes = (await api.GetDenuvoFixesAsync("300"))!;

            fixes.Name.Should().Be("Orphan only");
            fixes.Fixes.Should().ContainSingle().Which.Id.Should().Be("orphan-1");
        }
    }

    [Fact]
    public async Task A_game_with_no_orphan_fixes_behaves_exactly_as_before()
    {
        var api = Api(new Router { Catalog = Ok(CatalogBody()), LuaTools = Status(HttpStatusCode.NotFound) });
        await api.GetDenuvoListingsAsync();

        (await api.GetDenuvoFixesAsync("100")).Should().BeNull();

        var down = Api(new Router { Catalog = Ok(CatalogBody()) });
        await down.GetDenuvoListingsAsync();
        await down.Invoking(a => a.GetDenuvoFixesAsync("100")).Should().ThrowAsync<HttpRequestException>();
    }

    // ── Orphan downloads ──────────────────────────────────────────────────────

    private async Task<LuaToolsApiClient> ListedAsync(string sha = "", string url = FixUrl,
        Func<HttpRequestMessage, HttpResponseMessage>? fixHost = null)
    {
        var api = Api(new Router { Catalog = Ok(CatalogBody(sha, url)), LuaTools = Status(HttpStatusCode.NotFound) }, fixHost: fixHost);
        await api.GetDenuvoListingsAsync();
        await api.GetDenuvoFixesAsync("300");
        return api;
    }

    private static HttpResponseMessage Bytes(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    [Fact]
    public async Task An_orphan_fix_downloads_from_the_catalog_link_and_matches_its_sha()
    {
        Uri? asked = null;
        var api = await ListedAsync(fixHost: req => { asked = req.RequestUri; return Bytes(FixBytes); });

        using var file = await api.DownloadDenuvoAsync("orphan-1", "fix", "fix.zip", null, appid: "300");
        _staged.Add(file.FilePath);

        asked.Should().Be(new Uri(FixUrl));
        (await File.ReadAllBytesAsync(file.FilePath)).Should().Equal(FixBytes);
    }

    private static string DownloadsRoot => Path.Combine(Path.GetTempPath(), "LuaToolsGui", "downloads");

    /// <summary>Every staged file/folder under the interim downloads root, to compare before and after.</summary>
    private static HashSet<string> StagedEntries() =>
        Directory.Exists(DownloadsRoot)
            ? Directory.EnumerateFileSystemEntries(DownloadsRoot, "*", SearchOption.AllDirectories).ToHashSet()
            : [];

    [Fact]
    public async Task An_orphan_fix_whose_sha_does_not_match_is_discarded_and_refused()
    {
        var before = StagedEntries();
        var api = await ListedAsync(sha: new string('0', 64), fixHost: _ => Bytes(FixBytes));

        var download = () => api.DownloadDenuvoAsync("orphan-1", "fix", "fix.zip", null, appid: "300");

        await download.Should().ThrowAsync<ApiException>().WithMessage(Resources.Strings.Api_Err_FixHashMismatch);
        StagedEntries().Except(before).Should().BeEmpty(
            "the bytes were not the ones the catalog named: neither the file nor its per-download folder may stay");
    }

    // ── Item 6: unique staging folder + the verified file stays pinned ────────────────────────────────

    [Fact]
    public async Task An_orphan_fix_is_staged_in_a_folder_of_its_own_under_its_real_name()
    {
        var api = await ListedAsync(fixHost: _ => Bytes(FixBytes));

        using var first = await api.DownloadDenuvoAsync("orphan-1", "fix", "fix.zip", null, appid: "300");
        using var second = await api.DownloadDenuvoAsync("orphan-1", "fix", "fix.zip", null, appid: "300");
        _staged.Add(first.FilePath);
        _staged.Add(second.FilePath);

        Path.GetFileName(first.FilePath).Should().Be("fix.zip", "the name is build identity and is kept");
        Path.GetDirectoryName(first.FilePath).Should().NotBe(Path.GetDirectoryName(second.FilePath),
            "two downloads of the same name must not share a path");
        Path.GetDirectoryName(Path.GetDirectoryName(first.FilePath)).Should().Be(DownloadsRoot);
        first.OwnedDirectory.Should().Be(Path.GetDirectoryName(first.FilePath));
    }

    [Fact]
    public async Task A_verified_orphan_fix_is_pinned_until_it_is_released()
    {
        var api = await ListedAsync(fixHost: _ => Bytes(FixBytes));
        using var file = await api.DownloadDenuvoAsync("orphan-1", "fix", "fix.zip", null, appid: "300");
        _staged.Add(file.FilePath);

        // Readers (the screening, ZipFile.OpenRead in the apply step) are unaffected...
        (await File.ReadAllBytesAsync(file.FilePath)).Should().Equal(FixBytes);
        using (var reader = ZipLikeOpenRead(file.FilePath)) reader.Length.Should().Be(FixBytes.Length);
        // ...but the checked file cannot be replaced, truncated or deleted from under the handle.
        new Action(() => File.Delete(file.FilePath)).Should().Throw<IOException>();
        new Action(() => File.WriteAllBytes(file.FilePath, [1, 2, 3])).Should().Throw<IOException>();
        File.Exists(file.FilePath).Should().BeTrue();

        file.Discard();

        File.Exists(file.FilePath).Should().BeFalse("Discard releases the pin first, then deletes");
        Directory.Exists(file.OwnedDirectory!).Should().BeFalse();
    }

    // FileShare.Read, which is how ZipFile.OpenRead and File.OpenRead open a file.
    private static FileStream ZipLikeOpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    [Fact]
    public void Discarding_a_file_without_a_pin_or_folder_just_deletes_it()
    {
        string path = Path.Combine(_dir, "plain.zip");
        File.WriteAllText(path, "x");

        new DownloadedFile(path, "plain.zip").Discard();

        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public async Task An_orphan_slot_the_fix_does_not_have_is_refused()
    {
        var api = await ListedAsync(fixHost: _ => Bytes(FixBytes));

        var download = () => api.DownloadDenuvoAsync("orphan-1", "manifest", "m.zip", null, appid: "300");

        await download.Should().ThrowAsync<ApiException>();
    }

    // ── What the parser refuses (the catalog is a repository, not a trusted channel) ──

    [Theory]
    [InlineData("http://files.example.com/fix.zip", FixShaConst)]
    [InlineData("https://127.0.0.1/fix.zip", FixShaConst)]
    [InlineData("https://localhost/fix.zip", FixShaConst)]
    [InlineData(FixUrl, "not-a-sha")]
    [InlineData(FixUrl, "")]
    public void The_parser_drops_a_link_that_is_not_public_https_with_a_valid_sha(string url, string sha)
    {
        string body = CatalogBody(sha: sha.Length == 0 ? "x" : sha, url: url);

        var orphans = OrphanFixCatalog.Parse(body);

        orphans.Should().NotContainKey("300", "a game with no usable fix is not an orphan game at all");
    }

    private const string FixShaConst = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void The_parser_keeps_a_valid_fix_and_reports_only_the_slots_that_passed()
    {
        var orphans = OrphanFixCatalog.Parse(CatalogBody());

        var fix = orphans["300"].Fixes.Should().ContainSingle().Subject;
        fix.Fix.HasFix.Should().BeTrue();
        fix.Fix.HasManifest.Should().BeFalse();
        fix.Downloads["fix"].Sha256.Should().Be(FixSha);
        OrphanFixCatalog.Parse("not json at all").Should().BeEmpty();
    }
}
