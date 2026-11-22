using System.IO;
using System.Net;
using System.Net.Http;
using AwesomeAssertions;
using LuaToolsGui.Services;
using LuaToolsGui.Views;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Security batch 2 (MEDIUM): text from the catalog, lua.tools or api.steamcmd.net used as a link, a path or
/// a download source. No network; the HTTP side runs against stub handlers.
/// </summary>
public sealed class DownloadHardeningTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "luatools-tests", Guid.NewGuid().ToString("N"));

    public DownloadHardeningTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    // ── 1. Links in a fix description: only absolute http(s) reaches ShellExecute ────────────────────

    [Theory]
    [InlineData("https://store.steampowered.com/app/391540/")]
    [InlineData("http://example.com/a?b=c")]
    [InlineData("HTTPS://EXAMPLE.COM/x")]
    public void A_web_link_is_opened(string url)
    {
        FixesView.TryGetWebUrl(url, out string absolute).Should().BeTrue();
        absolute.Should().StartWith("http");
    }

    [Theory]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData(@"\\attacker\share\payload.exe")]
    [InlineData("//attacker/share/payload.exe")]
    [InlineData("ms-msdt:/id PCWDiagnostic /skip force")]
    [InlineData("steam://store/1")]
    [InlineData("javascript:alert(1)")]
    [InlineData("mailto:someone@example.com")]
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    [InlineData("relative/page.html")]
    [InlineData("   ")]
    [InlineData("")]
    public void Anything_that_is_not_a_web_link_is_refused(string url) =>
        FixesView.TryGetWebUrl(url, out _).Should().BeFalse();

    [Fact]
    public void A_missing_link_is_refused() => FixesView.TryGetWebUrl(null, out _).Should().BeFalse();

    // ── 2. Steamless: launch executables must live inside the install folder ─────────────────────────

    private string Touch(string relative)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "MZ");
        return path;
    }

    [Fact]
    public void Launch_executables_inside_the_install_folder_are_kept()
    {
        string install = Path.Combine(_root, "Game");
        string main = Touch(@"Game\game.exe");
        string nested = Touch(@"Game\bin\game64.exe");

        // "GAME.EXE" is the same file by case: dropped as a duplicate, order of the rest is kept.
        SteamlessService.ContainedLaunchExes(install, [@"game.exe", @"bin\game64.exe", "GAME.EXE"])
            .Should().Equal([main, nested]);
    }

    [Fact]
    public void A_launch_entry_that_escapes_the_install_folder_is_dropped()
    {
        string install = Path.Combine(_root, "Game");
        Touch(@"Game\game.exe");
        string outside = Touch(@"Other\evil.exe");
        string sibling = Touch(@"Game2\x.exe"); // shares the "Game" prefix: must not count as inside

        SteamlessService.ContainedLaunchExes(install,
            [@"..\Other\evil.exe", @"..\Game2\x.exe", outside, sibling, @"C:\Windows\System32\cmd.exe", @"\Windows\x.exe", @"\\host\share\x.exe"])
            .Should().BeEmpty();
    }

    [Fact]
    public void A_launch_entry_that_does_not_exist_is_dropped_so_the_folder_scan_can_take_over()
    {
        Touch(@"Game\game.exe");

        SteamlessService.ContainedLaunchExes(Path.Combine(_root, "Game"), [@"missing.exe"]).Should().BeEmpty();
    }

    // ── 3. Signed/catalog download URLs: same policy as the DoH handler ──────────────────────────────

    [Theory]
    [InlineData("https://pub-0123abcd.r2.dev/fix.zip")]
    [InlineData("https://github.com/ArthurS357/LuaTools-Catalog/releases/download/f2-test/undertale-f2-test.zip")]
    [InlineData("https://bucket.account.r2.cloudflarestorage.com/key.zip?X-Amz-Signature=abc&X-Amz-Expires=300")]
    [InlineData("https://files.example.com/orphan/fix.zip")]
    public void A_public_named_https_host_is_followed(string url) =>
        LuaToolsApiClient.IsFollowableSignedUrl(url).Should().BeTrue();

    [Theory]
    [InlineData("https://printer.local/fix.zip")]
    [InlineData("https://NAS.LOCAL./fix.zip")]
    [InlineData("https://intranet/fix.zip")]
    [InlineData("https://fileserver/share/fix.zip")]
    [InlineData("https://localhost/fix.zip")]
    [InlineData("https://app.localhost/fix.zip")]
    [InlineData("https://127.0.0.1/fix.zip")]
    [InlineData("https://192.168.1.5/fix.zip")]
    [InlineData("https://[::1]/fix.zip")]
    [InlineData("http://files.example.com/fix.zip")]
    [InlineData("file:///C:/fix.zip")]
    public void A_local_single_label_ip_or_non_https_target_is_refused(string url) =>
        LuaToolsApiClient.IsFollowableSignedUrl(url).Should().BeFalse();

    // ── 4. Covers: screened URL, screened redirects, size cap ────────────────────────────────────────

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    private static byte[] Jpeg(int length)
    {
        var bytes = new byte[length];
        bytes[0] = 0xFF; bytes[1] = 0xD8; bytes[2] = 0xFF;
        return bytes;
    }

    private static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private static HttpResponseMessage Redirect(string to)
    {
        var res = new HttpResponseMessage(HttpStatusCode.Found);
        res.Headers.Location = new Uri(to);
        return res;
    }

    private const string CoverUrl = "https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/391540/header.jpg?t=1";

    [Fact]
    public async Task A_valid_jpeg_cover_from_a_public_cdn_is_accepted()
    {
        var jpeg = Jpeg(20_000);
        var stub = new Stub(_ => Ok(jpeg));

        (await new CoverCache(stub).FetchAsync(CoverUrl, CancellationToken.None)).Should().Equal(jpeg);
    }

    [Fact]
    public async Task A_cover_of_exactly_the_cap_is_accepted_and_one_byte_more_is_refused()
    {
        var atCap = Jpeg(CoverCache.MaxCoverBytes);
        var overCap = Jpeg(CoverCache.MaxCoverBytes + 1);

        (await new CoverCache(new Stub(_ => Ok(atCap))).FetchAsync(CoverUrl, CancellationToken.None))
            .Should().HaveCount(CoverCache.MaxCoverBytes);
        (await new CoverCache(new Stub(_ => Ok(overCap))).FetchAsync(CoverUrl, CancellationToken.None))
            .Should().BeNull();
    }

    [Fact]
    public async Task A_body_without_a_content_length_is_still_capped_while_it_is_read()
    {
        // Chunked responses have no Content-Length to check up front: the cap must hold while streaming.
        var stub = new Stub(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(Jpeg(CoverCache.MaxCoverBytes + 4096))),
        });

        (await new CoverCache(stub).FetchAsync(CoverUrl, CancellationToken.None)).Should().BeNull();
    }

    [Theory]
    [InlineData("http://shared.akamai.steamstatic.com/h.jpg")]
    [InlineData("https://127.0.0.1/h.jpg")]
    [InlineData("https://192.168.0.10/h.jpg")]
    [InlineData("https://localhost/h.jpg")]
    [InlineData("https://intranet/h.jpg")]
    [InlineData("file:///C:/h.jpg")]
    [InlineData("not a url")]
    public async Task A_cover_url_that_is_not_a_public_https_host_is_refused_before_any_request(string url)
    {
        var stub = new Stub(_ => Ok(Jpeg(5_000)));

        (await new CoverCache(stub).FetchAsync(url, CancellationToken.None)).Should().BeNull();
        stub.Seen.Should().BeEmpty("nothing may be requested from a refused address");
    }

    [Fact]
    public async Task A_redirect_to_a_public_host_is_followed()
    {
        var jpeg = Jpeg(3_000);
        var stub = new Stub(req => req.RequestUri!.Host == "cdn2.example.com" ? Ok(jpeg) : Redirect("https://cdn2.example.com/h.jpg"));

        (await new CoverCache(stub).FetchAsync(CoverUrl, CancellationToken.None)).Should().Equal(jpeg);
        stub.Seen.Select(u => u.Host).Should().Equal("shared.akamai.steamstatic.com", "cdn2.example.com");
    }

    [Theory]
    [InlineData("https://127.0.0.1/h.jpg")]
    [InlineData("http://cdn2.example.com/h.jpg")]
    [InlineData("https://intranet/h.jpg")]
    public async Task A_redirect_to_a_loopback_lan_or_plain_http_target_is_refused_without_following_it(string target)
    {
        var stub = new Stub(_ => Redirect(target));

        var act = () => new CoverCache(stub).FetchAsync(CoverUrl, CancellationToken.None);

        await act.Should().ThrowAsync<ApiException>();
        stub.Seen.Should().ContainSingle("the refused hop is never requested");
    }

    [Fact]
    public async Task Bodies_that_are_not_covers_are_refused()
    {
        (await new CoverCache(new Stub(_ => Ok("<html>error</html>"u8.ToArray().Concat(new byte[600]).ToArray())))
            .FetchAsync(CoverUrl, CancellationToken.None)).Should().BeNull("not a JPEG");
        (await new CoverCache(new Stub(_ => Ok(Jpeg(100)))).FetchAsync(CoverUrl, CancellationToken.None))
            .Should().BeNull("truncated");
        (await new CoverCache(new Stub(_ => new HttpResponseMessage(HttpStatusCode.NotFound)))
            .FetchAsync(CoverUrl, CancellationToken.None)).Should().BeNull("404");
    }

    // ── 5. Pinned staging: why it is safe on Windows, and that the installer keeps it ─────────────────

    [Fact]
    public void A_pinned_file_can_still_be_read_and_copied_by_path_but_not_replaced_or_deleted()
    {
        // The premise the Unlocker change rests on: later readers (hash, extract, File.Copy into the Steam
        // root) keep working while the pin is held, and nothing can swap the file under it.
        string staged = Touch(@"stage\dwmapi.dll");
        string dest = Path.Combine(_root, "steam-root-dwmapi.dll");

        using (AssetIntegrity.OpenPinned(staged))
        {
            File.Copy(staged, dest, overwrite: true);
            AssetIntegrity.Sha256OfFile(staged).Should().NotBeNullOrEmpty();
            new Action(() => File.Delete(staged)).Should().Throw<IOException>();
            new Action(() => File.WriteAllText(staged, "swapped")).Should().Throw<IOException>();
            new Action(() => File.Move(staged, staged + ".moved")).Should().Throw<IOException>();
        }

        File.Exists(dest).Should().BeTrue();
        File.Delete(staged); // released: deletable again
    }

    private static string UnlockerSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LuaToolsGui.sln"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine(dir!.FullName, "src", "LuaToolsGui", "Services", "UnlockerService.cs"));
    }

    [Fact]
    public void The_unlocker_pins_each_download_before_checking_it_and_releases_the_pins_before_deleting_staging()
    {
        // InstallAsync needs a Steam root, a GitHub release and a user at the review dialog, so the order is
        // pinned structurally: pin → verify (zip and loose files), pin the extracted files, and in the
        // finally block dispose the pins BEFORE the staging folder is deleted.
        string src = UnlockerSource();

        src.IndexOf("pins.Add(AssetIntegrity.OpenPinned(zipPath))", StringComparison.Ordinal)
            .Should().BePositive().And.BeLessThan(src.IndexOf("AssetIntegrity.Matches(zipPath", StringComparison.Ordinal));
        src.IndexOf("pins.Add(AssetIntegrity.OpenPinned(dest))", StringComparison.Ordinal)
            .Should().BePositive().And.BeLessThan(src.IndexOf("AssetIntegrity.Matches(dest", StringComparison.Ordinal));
        src.Should().Contain("pins.AddRange(staged.Values.Select(AssetIntegrity.OpenPinned))");
        src.IndexOf("foreach (var pin in pins) pin.Dispose()", StringComparison.Ordinal)
            .Should().BePositive().And.BeLessThan(src.IndexOf("Directory.Delete(staging", StringComparison.Ordinal));
    }
}
