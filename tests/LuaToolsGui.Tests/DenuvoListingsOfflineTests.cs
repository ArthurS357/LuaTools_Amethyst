using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using AwesomeAssertions;
using LuaToolsGui.Services;
using LuaToolsGui.ViewModels;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// The Fixes catalog survives lua.tools being down: a good answer is saved to disk, any later failure
/// falls back to that copy, and the page says it is showing the copy instead of looking broken.
///
/// <para>
/// The HTTP layer is replaced by a stub, never by a real request. The client is built with null services
/// (as the other API-client tests do) and its private <c>_http</c> is swapped by reflection, so the
/// production handler chain — DnsFallbackHttpHandler included — is not touched.
/// </para>
/// </summary>
public class DenuvoListingsOfflineTests : IDisposable
{
    private const string Catalog =
        """{"games":[{"appid":"1","name":"Alpha","fixCount":2,"tags":[]},{"appid":"2","name":"Beta","fixCount":1,"tags":[]}],"tags":[]}""";

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "luatools-tests", Guid.NewGuid().ToString("N"));

    private string CacheDir => Path.Combine(_dir, "cache");
    private string CacheFile => Path.Combine(CacheDir, DenuvoListingsCache.FileName);

    public DenuvoListingsOfflineTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    /// <summary>Answers each request to lua.tools from <see cref="Respond"/>; flipping it flips "the network".
    /// The LuaTools-Catalog host always says 404 here, so these tests keep exercising the lua.tools tier and the
    /// disk copy; the catalog tier has its own tests in CatalogFallbackTests.</summary>
    private sealed class Stub : HttpMessageHandler
    {
        public Func<HttpResponseMessage> Respond { get; set; } = () => new(HttpStatusCode.OK)
        {
            Content = new StringContent(Catalog),
        };
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (request.RequestUri!.Host == "raw.githubusercontent.com")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            Calls++;
            return Task.FromResult(Respond());
        }
    }

    private static HttpResponseMessage Down() => throw new HttpRequestException("lua.tools is unreachable");

    private LuaToolsApiClient Api(Stub stub)
    {
        var api = new LuaToolsApiClient(null!, null!, null!, null!, null!) { CacheDirectory = CacheDir };
        typeof(LuaToolsApiClient).GetField("_http", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(api, new HttpClient(stub) { BaseAddress = new Uri("https://lua.tools") });
        return api;
    }

    // ── The API client ────────────────────────────────────────────────────────

    [Fact]
    public async Task A_good_answer_is_live_and_is_saved_to_disk()
    {
        var result = await Api(new Stub()).GetDenuvoListingsAsync();

        result!.IsOffline.Should().BeFalse();
        result.Data.Games.Should().HaveCount(2);
        File.Exists(CacheFile).Should().BeTrue("the next failure needs something to fall back to");
    }

    [Fact]
    public async Task When_the_network_fails_the_saved_catalog_is_returned_as_offline()
    {
        var stub = new Stub();
        var api = Api(stub);
        await api.GetDenuvoListingsAsync();

        stub.Respond = Down;
        var result = await api.GetDenuvoListingsAsync();

        result!.IsOffline.Should().BeTrue();
        result.CachedAt.Should().BeCloseTo(DateTime.Now, TimeSpan.FromMinutes(1));
        result.Data.Games.Select(g => g.Name).Should().Equal("Alpha", "Beta");
    }

    [Fact]
    public async Task A_server_error_such_as_a_503_also_falls_back_to_the_saved_catalog()
    {
        var stub = new Stub();
        var api = Api(stub);
        await api.GetDenuvoListingsAsync();

        stub.Respond = () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        var result = await api.GetDenuvoListingsAsync();

        result!.IsOffline.Should().BeTrue();
    }

    [Fact]
    public async Task With_nothing_saved_a_network_failure_still_throws_as_it_always_did()
    {
        var api = Api(new Stub { Respond = Down });

        await api.Invoking(a => a.GetDenuvoListingsAsync()).Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task With_nothing_saved_a_bad_status_is_still_null()
    {
        var api = Api(new Stub { Respond = () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) });

        (await api.GetDenuvoListingsAsync()).Should().BeNull();
    }

    [Fact]
    public async Task A_copy_older_than_a_day_is_not_used()
    {
        var stub = new Stub();
        var api = Api(stub);
        await api.GetDenuvoListingsAsync();
        File.SetLastWriteTime(CacheFile, DateTime.Now - DenuvoListingsCache.MaxAge - TimeSpan.FromMinutes(5));

        stub.Respond = () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

        (await api.GetDenuvoListingsAsync()).Should().BeNull();
    }

    [Fact]
    public async Task A_corrupt_copy_is_ignored_rather_than_crashing()
    {
        Directory.CreateDirectory(CacheDir);
        File.WriteAllText(CacheFile, "{ not json");
        var api = Api(new Stub { Respond = () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) });

        (await api.GetDenuvoListingsAsync()).Should().BeNull();
    }

    [Fact]
    public async Task An_empty_catalog_does_not_overwrite_a_good_copy()
    {
        var stub = new Stub();
        var api = Api(stub);
        await api.GetDenuvoListingsAsync();

        stub.Respond = () => new(HttpStatusCode.OK) { Content = new StringContent("""{"games":[],"tags":[]}""") };
        await api.GetDenuvoListingsAsync();

        File.ReadAllText(CacheFile).Should().Contain("Alpha");
    }

    [Fact]
    public async Task A_cancelled_call_is_not_a_failure_and_never_reads_the_copy()
    {
        var stub = new Stub();
        var api = Api(stub);
        await api.GetDenuvoListingsAsync();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await api.Invoking(a => a.GetDenuvoListingsAsync(cts.Token)).Should().ThrowAsync<OperationCanceledException>();
    }

    // ── The Fixes page ────────────────────────────────────────────────────────

    private FixesViewModel Page(Stub stub)
    {
        string steamRoot = Path.Combine(_dir, "steam");
        Directory.CreateDirectory(Path.Combine(steamRoot, "config", "stplug-in"));
        var settings = new SettingsService(_dir) { SteamPathOverride = steamRoot };
        var cache = new CacheService(Path.Combine(_dir, "appcache"));
        var steam = new SteamService(settings);
        var vault = new LuaVault(() => steam.StPlugInDir, Path.Combine(_dir, "vault"));
        return new FixesViewModel(
            Api(stub), new AuthService(), new LuaInstaller(steam, settings, cache, vault), steam,
            new SteamLibraryService(steam), new CoverCache(), new ToastService(), settings, new DenuvoFixService());
    }

    [Fact]
    public async Task The_banner_shows_only_while_the_grid_is_the_saved_copy()
    {
        var stub = new Stub();
        var vm = Page(stub);
        await vm.LoadAsync();
        vm.ShowOfflineBanner.Should().BeFalse("a live catalog needs no excuse");

        stub.Respond = Down;
        await vm.LoadAsync(force: true);

        vm.ShowOfflineBanner.Should().BeTrue();
        vm.OfflineBannerText.Should().Be(string.Format(Resources.Strings.Fixes_OfflineMode,
            File.GetLastWriteTime(CacheFile).ToString("g")));
        vm.Items.Should().HaveCount(2, "the app is working, it just has no internet");

        stub.Respond = () => new(HttpStatusCode.OK) { Content = new StringContent(Catalog) };
        await vm.LoadAsync(force: true); // what the Refresh button does once the network is back

        vm.ShowOfflineBanner.Should().BeFalse();
    }

    [Fact]
    public async Task No_banner_when_there_is_no_copy_either()
    {
        var vm = Page(new Stub { Respond = Down });

        await vm.LoadAsync();

        vm.ShowOfflineBanner.Should().BeFalse("nothing is being shown, so there is no copy to announce");
        vm.Items.Should().BeEmpty();
    }
}
