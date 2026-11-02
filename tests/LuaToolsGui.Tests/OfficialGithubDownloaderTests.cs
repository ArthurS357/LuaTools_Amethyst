using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using LuaToolsGui.Services;
using Velopack.Sources;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Pins the contract of <see cref="OfficialGithubDownloader"/>, the transport behind the app's
/// self-update (F-04): direct GitHub only, and a package is accepted only if it hashes to the digest
/// GitHub's API published for it. Every "can't prove it" outcome must refuse and leave no file behind.
/// </summary>
public class OfficialGithubDownloaderTests : IDisposable
{
    private const string Owner = "ArthurS357";
    private const string Repo = "LuaTools_Amethyst";
    private const string PkgUrl = "https://github.com/ArthurS357/LuaTools_Amethyst/releases/download/v1.8.0/LuaTools-1.8.0-full.nupkg";
    private const string ReleaseApi = "https://api.github.com/repos/ArthurS357/LuaTools_Amethyst/releases/tags/v1.8.0";
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("pretend this is a nupkg");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "luatools-tests", Guid.NewGuid().ToString("N"));

    public OfficialGithubDownloaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private string Target => Path.Combine(_dir, "pkg.partial");

    private static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static string ReleaseJson(string? digest, string name = "LuaTools-1.8.0-full.nupkg") =>
        digest is null
            ? $$"""{"assets":[{"name":"{{name}}"}]}"""
            : $$"""{"assets":[{"name":"other.zip","digest":"sha256:{{new string('0', 64)}}"},{"name":"{{name}}","digest":"{{digest}}"}]}""";

    /// <summary>Scripted transport: records every URL it is asked for and never touches the network.</summary>
    private sealed class FakeTransport : IFileDownloader
    {
        public readonly List<string> Requests = [];
        public Func<string, string> OnString = _ => throw new HttpRequestException("no API scripted");
        public Func<byte[]> FileBytes = () => Payload;
        public Exception? FileFailure;

        public Task DownloadFile(string url, string targetFile, Action<int> progress,
            IDictionary<string, string>? headers = null, double timeout = 30, CancellationToken cancelToken = default)
        {
            Requests.Add(url);
            if (FileFailure is not null) throw FileFailure;
            File.WriteAllBytes(targetFile, FileBytes());
            return Task.CompletedTask;
        }

        public Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers = null, double timeout = 30)
        {
            Requests.Add(url);
            return Task.FromResult(Payload);
        }

        public Task<string> DownloadString(string url, IDictionary<string, string>? headers = null, double timeout = 30)
        {
            Requests.Add(url);
            return Task.FromResult(OnString(url));
        }
    }

    private static OfficialGithubDownloader Make(FakeTransport t) => new(Owner, Repo, t);

    // ── Accepts exactly what GitHub published ─────────────────────────────────

    [Fact]
    public async Task A_package_matching_the_published_digest_is_accepted()
    {
        var t = new FakeTransport { OnString = _ => ReleaseJson("sha256:" + Sha256(Payload)) };

        await Make(t).DownloadFile(PkgUrl, Target, _ => { });

        File.Exists(Target).Should().BeTrue();
        t.Requests.Should().Equal(ReleaseApi, PkgUrl); // digest first, then the package — direct both times
    }

    [Fact]
    public async Task The_asset_api_url_form_is_verified_the_same_way()
    {
        const string assetApi = "https://api.github.com/repos/ArthurS357/LuaTools_Amethyst/releases/assets/12345";
        var t = new FakeTransport { OnString = _ => $$"""{"name":"x.nupkg","digest":"sha256:{{Sha256(Payload)}}"}""" };

        await Make(t).DownloadFile(assetApi, Target, _ => { });

        File.Exists(Target).Should().BeTrue();
        t.Requests.Should().Equal(assetApi, assetApi);
    }

    // ── Fail-closed ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_digest_mismatch_refuses_the_package_and_deletes_it()
    {
        var t = new FakeTransport { OnString = _ => ReleaseJson("sha256:" + new string('a', 64)) };

        var act = () => Make(t).DownloadFile(PkgUrl, Target, _ => { });

        await act.Should().ThrowAsync<UpdateIntegrityException>();
        File.Exists(Target).Should().BeFalse("an unverified package must never be left for Velopack to pick up");
    }

    [Theory]
    [InlineData(null)]                    // asset listed, no digest field (release predates digests)
    [InlineData("sha256:")]               // empty
    [InlineData("sha256:not-hex")]        // malformed
    [InlineData("md5:d41d8cd98f00b204e9800998ecf8427e")]
    public async Task A_missing_or_unusable_digest_refuses_without_downloading(string? digest)
    {
        var t = new FakeTransport { OnString = _ => ReleaseJson(digest) };

        var act = () => Make(t).DownloadFile(PkgUrl, Target, _ => { });

        await act.Should().ThrowAsync<UpdateIntegrityException>();
        t.Requests.Should().NotContain(PkgUrl, "no digest means no bandwidth spent on a package we cannot verify");
        File.Exists(Target).Should().BeFalse();
    }

    [Fact]
    public async Task An_asset_the_release_does_not_list_refuses()
    {
        var t = new FakeTransport { OnString = _ => ReleaseJson("sha256:" + Sha256(Payload), name: "something-else.nupkg") };

        var act = () => Make(t).DownloadFile(PkgUrl, Target, _ => { });

        await act.Should().ThrowAsync<UpdateIntegrityException>();
        t.Requests.Should().NotContain(PkgUrl);
    }

    [Fact]
    public async Task An_unreachable_api_refuses_and_downloads_nothing()
    {
        var t = new FakeTransport(); // OnString throws HttpRequestException

        var act = () => Make(t).DownloadFile(PkgUrl, Target, _ => { });

        await act.Should().ThrowAsync<HttpRequestException>();
        t.Requests.Should().Equal(ReleaseApi);
        File.Exists(Target).Should().BeFalse();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"assets":"nope"}""")]
    public async Task A_garbled_api_answer_refuses(string body)
    {
        var t = new FakeTransport { OnString = _ => body };

        var act = () => Make(t).DownloadFile(PkgUrl, Target, _ => { });

        await act.Should().ThrowAsync<UpdateIntegrityException>();
        t.Requests.Should().NotContain(PkgUrl);
    }

    [Fact]
    public async Task A_failed_package_download_is_not_masked_as_an_integrity_verdict()
    {
        // Transport trouble must stay distinguishable so UpdateService can still try a backup repo.
        var t = new FakeTransport
        {
            OnString = _ => ReleaseJson("sha256:" + Sha256(Payload)),
            FileFailure = new HttpRequestException("CDN down"),
        };

        var act = () => Make(t).DownloadFile(PkgUrl, Target, _ => { });

        await act.Should().ThrowAsync<HttpRequestException>();
        File.Exists(Target).Should().BeFalse();
    }

    [Fact]
    public async Task There_is_no_mirror_fallback_when_the_direct_download_fails()
    {
        var t = new FakeTransport
        {
            OnString = _ => ReleaseJson("sha256:" + Sha256(Payload)),
            FileFailure = new HttpRequestException("blocked"),
        };

        try { await Make(t).DownloadFile(PkgUrl, Target, _ => { }); } catch (HttpRequestException) { }

        t.Requests.Where(u => u != ReleaseApi).Should().Equal(PkgUrl); // exactly one attempt, at GitHub
        t.Requests.Should().OnlyContain(u => u.StartsWith("https://github.com/") || u.StartsWith("https://api.github.com/"));
    }

    // ── URL policy: nothing but this repo on github.com ───────────────────────

    [Theory]
    [InlineData("https://ghproxy.net/https://github.com/ArthurS357/LuaTools_Amethyst/releases/download/v1/x.nupkg")]
    [InlineData("https://ghfast.top/https://github.com/ArthurS357/LuaTools_Amethyst/releases/download/v1/x.nupkg")]
    [InlineData("https://lua.tools/api/gh/https://api.github.com/repos/ArthurS357/LuaTools_Amethyst/releases")]
    [InlineData("http://github.com/ArthurS357/LuaTools_Amethyst/releases/download/v1/x.nupkg")]
    [InlineData("https://github.com/madoiscool/LuaTools/releases/download/v1/x.nupkg")]
    [InlineData("https://github.com/ArthurS357/Some_Other_Repo/releases/download/v1/x.nupkg")]
    [InlineData("https://api.github.com/repos/madoiscool/LuaTools/releases")]
    [InlineData("https://objects.githubusercontent.com/whatever")]
    [InlineData("https://evil.example/ArthurS357/LuaTools_Amethyst/releases/download/v1/x.nupkg")]
    [InlineData("https://github.com.evil.example/ArthurS357/LuaTools_Amethyst/releases/download/v1/x.nupkg")]
    [InlineData("not a url")]
    public async Task Urls_outside_the_official_repo_are_refused_before_any_request(string url)
    {
        var t = new FakeTransport();
        var d = Make(t);

        await ((Func<Task>)(() => d.DownloadFile(url, Target, _ => { }))).Should().ThrowAsync<UpdateIntegrityException>();
        await ((Func<Task>)(() => d.DownloadString(url))).Should().ThrowAsync<UpdateIntegrityException>();
        await ((Func<Task>)(() => d.DownloadBytes(url))).Should().ThrowAsync<UpdateIntegrityException>();

        t.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task The_release_feed_endpoints_are_allowed_but_stay_direct()
    {
        var t = new FakeTransport { OnString = _ => "[]" };
        var d = Make(t);

        await d.DownloadString("https://api.github.com/repos/ArthurS357/LuaTools_Amethyst/releases?per_page=10&page=1");
        await d.DownloadString("https://github.com/ArthurS357/LuaTools_Amethyst/releases/download/v1.8.0/releases.win.json");
        await d.DownloadBytes("https://github.com/ArthurS357/LuaTools_Amethyst/releases/download/v1.8.0/releases.win.json");

        t.Requests.Should().HaveCount(3);
    }

    [Fact]
    public async Task The_repo_pin_is_case_insensitive_like_github()
    {
        var t = new FakeTransport { OnString = _ => "[]" };

        await Make(t).DownloadString("https://api.github.com/repos/arthurs357/luatools_amethyst/releases");

        t.Requests.Should().HaveCount(1);
    }

    // ── The log ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_refusal_is_written_to_the_app_log()
    {
        var t = new FakeTransport { OnString = _ => ReleaseJson("sha256:" + new string('a', 64)) };

        try { await Make(t).DownloadFile(PkgUrl, Target, _ => { }); } catch (UpdateIntegrityException) { }

        File.ReadAllText(AppLog.FilePath).Should().Contain("app-update: REFUSED package").And.Contain("does not match");
    }

    // ── Digest extraction ─────────────────────────────────────────────────────

    [Fact]
    public void ExtractDigest_picks_the_named_asset_not_a_neighbour()
    {
        string json = ReleaseJson("sha256:" + new string('b', 64));

        OfficialGithubDownloader.ExtractDigest(json, "LuaTools-1.8.0-full.nupkg")
            .Should().Be("sha256:" + new string('b', 64));
    }

    [Fact]
    public void ExtractDigest_matches_asset_names_exactly()
    {
        OfficialGithubDownloader.ExtractDigest(ReleaseJson("sha256:" + new string('b', 64)), "luatools-1.8.0-full.nupkg")
            .Should().BeNull();
    }

    // ── UpdateService helpers ─────────────────────────────────────────────────

    [Fact]
    public void FindIn_sees_through_wrapping()
    {
        var inner = new UpdateIntegrityException("bad hash");
        var wrapped = new InvalidOperationException("outer", new AggregateException(inner));

        UpdateIntegrityException.FindIn(wrapped).Should().BeSameAs(inner);
        UpdateIntegrityException.FindIn(new HttpRequestException("offline")).Should().BeNull();
    }

    [Fact]
    public void OwnerRepoOf_round_trips_the_normalised_urls_Resolve_produces()
    {
        var resolved = AppUpdateSources.Resolve([AppConfig.ForkRepositoryUrl + ".git/"], []);

        var (owner, repo) = AppUpdateSources.OwnerRepoOf(resolved.Repos.Single());

        (owner, repo).Should().Be((Owner, Repo));
    }
}
