using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using AwesomeAssertions;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Security batch 3 (LOW / hygiene): GitHub redirects, mirror list, staged names, log and toast text, the
/// Steam library manifest, the Steamless archive screening, the relaunch command. No network.
/// </summary>
public sealed class HygieneHardeningTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "luatools-tests", Guid.NewGuid().ToString("N"));

    public HygieneHardeningTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LuaToolsGui.sln"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine([dir!.FullName, .. parts]));
    }

    // ── 3. GithubProxy: redirects are followed by hand, hop by hop ───────────────────────────────────

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(Uri Url, string UserAgent, string Accept)> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add((request.RequestUri!,
                request.Headers.TryGetValues("User-Agent", out var ua) ? string.Join(" ", ua) : "",
                request.Headers.TryGetValues("Accept", out var accept) ? string.Join(" ", accept) : ""));
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Redirect(string to)
    {
        var res = new HttpResponseMessage(HttpStatusCode.Found);
        res.Headers.Location = new Uri(to);
        return res;
    }

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private const string AssetUrl = "https://github.com/atom0s/Steamless/releases/download/v3.1.0.5/x.zip";

    [Fact]
    public async Task GitHubs_own_release_redirect_is_followed_and_every_hop_carries_the_app_user_agent()
    {
        var stub = new Stub(req => req.RequestUri!.Host == "github.com"
            ? Redirect("https://release-assets.githubusercontent.com/blob/abc?sig=1")
            : Ok("payload"));
        string dest = Path.Combine(_root, "x.zip");

        await new GithubProxy(stub).DownloadAsync(AssetUrl, dest, progress: null);

        (await File.ReadAllTextAsync(dest)).Should().Be("payload");
        stub.Seen.Select(s => s.Url.Host).Should().Equal("github.com", "release-assets.githubusercontent.com");
        stub.Seen.Should().OnlyContain(s => s.UserAgent == AppVersion.UserAgent);
    }

    [Theory]
    [InlineData("http://release-assets.githubusercontent.com/x")]
    [InlineData("https://127.0.0.1/x")]
    [InlineData("https://192.168.1.5/x")]
    [InlineData("https://intranet/x")]
    [InlineData("https://printer.local/x")]
    [InlineData("file:///C:/x.zip")]
    public async Task A_redirect_to_plain_http_loopback_lan_or_local_is_refused_without_being_requested(string target)
    {
        var stub = new Stub(_ => Redirect(target));

        var act = () => new GithubProxy(stub).DownloadAsync(AssetUrl, Path.Combine(_root, "x.zip"), progress: null);

        await act.Should().ThrowAsync<Exception>();
        stub.Seen.Select(s => s.Url).Should().NotContain(new Uri(target), "a refused hop is never requested");
        File.Exists(Path.Combine(_root, "x.zip")).Should().BeFalse();
    }

    [Fact]
    public async Task A_redirect_loop_stops_at_the_hop_limit()
    {
        int n = 0;
        var stub = new Stub(_ => Redirect($"https://cdn{n++}.example.com/next"));

        var act = () => new GithubProxy(stub).DownloadAsync(AssetUrl, Path.Combine(_root, "x.zip"), progress: null);

        await act.Should().ThrowAsync<Exception>();
        // Each candidate (direct + the download mirrors) gets its own budget of hops, never more.
        stub.Seen.Count.Should().BeLessThanOrEqualTo(GithubProxy.Candidates(AssetUrl).Count() * (LuaToolsApiClient.MaxRedirectHops + 1));
    }

    [Fact]
    public async Task A_metadata_request_sends_the_app_user_agent_and_the_github_accept_header()
    {
        var stub = new Stub(_ => Ok("{}"));

        using var res = await new GithubProxy(stub).SendAsync("https://api.github.com/repos/a/b/releases/latest");

        res.Should().NotBeNull();
        var seen = stub.Seen.Should().ContainSingle().Subject;
        seen.UserAgent.Should().Be(AppVersion.UserAgent);
        seen.Accept.Should().Be("application/vnd.github+json");
    }

    // ── 4. GithubMirrors.Sanitize ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Public_https_mirrors_are_kept_and_given_a_trailing_slash() =>
        GithubMirrors.Sanitize(["https://ghproxy.net/", "https://lua.tools/api/gh", "  https://my-proxy.example.com/  "])
            .Should().Equal("https://ghproxy.net/", "https://lua.tools/api/gh/", "https://my-proxy.example.com/");

    [Theory]
    [InlineData("http://ghproxy.net/")]
    [InlineData("https://127.0.0.1/")]
    [InlineData("https://192.168.1.10/")]
    [InlineData("https://[::1]/")]
    [InlineData("https://localhost/")]
    [InlineData("https://app.localhost/")]
    [InlineData("https://proxy.local/")]
    [InlineData("https://intranet/")]
    [InlineData("not a url")]
    [InlineData("  ")]
    public void A_mirror_that_is_not_a_public_https_name_is_dropped(string mirror) =>
        GithubMirrors.Sanitize([mirror]).Should().BeEmpty();

    // ── 6. Hubcap: same staged-name rule as every other download, no partial file left behind ───────

    [Theory]
    [InlineData("..", "fallback.zip")]
    [InlineData("CON", "fallback.zip")]
    [InlineData("NUL.zip", "fallback.zip")]
    [InlineData("", "fallback.zip")]
    [InlineData(@"a/b\c.zip", "a_b_c.zip")]
    [InlineData("\"quoted.zip\"", "quoted.zip")]
    [InlineData("ok.zip", "ok.zip")]
    public void A_server_supplied_name_cannot_name_anything_but_a_file_in_the_staging_folder(string server, string expected) =>
        LuaToolsApiClient.StagedFileName(server, "fallback.zip").Should().Be(expected);

    private sealed class FailingStream : Stream
    {
        private int _reads;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            _reads++ == 0 ? new ValueTask<int>(16) : throw new IOException("connection reset");
    }

    private static string Downloads(string name) => Path.Combine(Path.GetTempPath(), "LuaToolsGui", "downloads", name);

    [Fact]
    public async Task A_hubcap_transfer_that_fails_midway_leaves_no_partial_file()
    {
        string name = $"hubcap-partial-{Guid.NewGuid():N}.zip";
        using var res = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FailingStream()) };
        res.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = name };

        var act = () => HubcapService.SaveResponseAsync(res, "fallback.zip", null, CancellationToken.None);

        await act.Should().ThrowAsync<IOException>();
        File.Exists(Downloads(name)).Should().BeFalse("the caller never receives the path, so nothing else would delete it");
    }

    [Fact]
    public async Task A_hubcap_download_with_a_reserved_server_name_is_staged_under_the_fallback()
    {
        string fallback = $"hubcap-fallback-{Guid.NewGuid():N}.zip";
        using var res = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
        res.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = "CON" };

        var file = await HubcapService.SaveResponseAsync(res, fallback, null, CancellationToken.None);
        try
        {
            file.FileName.Should().Be(fallback);
            Path.GetDirectoryName(file.FilePath).Should().Be(Path.GetDirectoryName(Downloads("x")));
        }
        finally { file.Discard(); }
    }

    // ── 8. /add-source logging ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Request_text_in_a_log_line_cannot_forge_a_new_line_and_is_truncated()
    {
        HttpServerService.ForLog("a\r\n[2026-01-01] FAKE ENTRY").Should().Be(@"a\r\n[2026-01-01] FAKE ENTRY");
        HttpServerService.ForLog(new string('x', 5000)).Should().HaveLength(2001).And.EndWith("…");
        HttpServerService.ForLog(null).Should().BeEmpty();
        HttpServerService.ForLog("short").Should().Be("short");
    }

    // ── 9. Paths on screen and in the startup dialog ────────────────────────────────────────────────

    [Fact]
    public void The_user_profile_folder_is_masked_in_text_meant_for_the_screen()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        string shown = LogSanitizer.SanitizePath($@"Access to the path '{profile}\AppData\Roaming\x.dll' is denied.");

        shown.Should().NotContain(profile).And.Contain(@"%USERPROFILE%\AppData\Roaming\x.dll");
    }

    [Fact]
    public void Every_toast_masks_the_profile_folder_in_one_place()
    {
        // ToastService needs a live WPF presenter, so the behaviour above is tested on its helper and the
        // wiring is pinned here: both entry points sanitize the message before it is shown.
        string src = RepoFile("src", "LuaToolsGui", "Services", "ToastService.cs");

        System.Text.RegularExpressions.Regex.Matches(src, @"message = LogSanitizer\.SanitizePath\(message\)")
            .Count.Should().Be(2, "Show and ShowAction");
    }

    // ── 10. installdir from appmanifest_<appid>.acf ─────────────────────────────────────────────────

    private SteamLibraryService Library(out string steamRoot)
    {
        steamRoot = Path.Combine(_root, "steam");
        Directory.CreateDirectory(Path.Combine(steamRoot, "steamapps", "common"));
        var settings = new SettingsService(Path.Combine(_root, "settings")) { SteamPathOverride = steamRoot };
        return new SteamLibraryService(new SteamService(settings));
    }

    private static void Manifest(string steamRoot, long appId, string vdfInstallDir) =>
        File.WriteAllText(Path.Combine(steamRoot, "steamapps", $"appmanifest_{appId}.acf"),
            $"\"AppState\"\n{{\n\t\"appid\"\t\t\"{appId}\"\n\t\"installdir\"\t\t\"{vdfInstallDir}\"\n}}");

    [Fact]
    public void A_normal_installdir_is_accepted_including_a_nested_one()
    {
        var library = Library(out string steam);
        Directory.CreateDirectory(Path.Combine(steam, "steamapps", "common", "Game"));
        Directory.CreateDirectory(Path.Combine(steam, "steamapps", "common", "Pub", "Game"));
        Manifest(steam, 10, "Game");
        Manifest(steam, 11, @"Pub\\Game");

        library.GetInstallDir(10).Should().Be(Path.Combine(steam, "steamapps", "common", "Game"));
        library.GetInstallDir(11).Should().Be(Path.Combine(steam, "steamapps", "common", "Pub", "Game"));
    }

    [Theory]
    [InlineData(@"..\\..\\Outside")]            // steam\Outside, which exists
    [InlineData(@"..")]                          // steamapps itself
    [InlineData(@".")]                           // common itself
    [InlineData(@"..\\common2")]                 // sibling that shares the "common" prefix
    [InlineData(@"\\Windows")]
    public void An_installdir_that_leaves_steamapps_common_is_not_a_game_folder(string vdfInstallDir)
    {
        var library = Library(out string steam);
        Directory.CreateDirectory(Path.Combine(steam, "Outside"));
        Directory.CreateDirectory(Path.Combine(steam, "steamapps", "common2"));
        Manifest(steam, 20, vdfInstallDir);

        library.GetInstallDir(20).Should().BeNull();
    }

    [Fact]
    public void An_absolute_installdir_is_not_a_game_folder()
    {
        var library = Library(out string steam);
        string elsewhere = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        Manifest(steam, 30, elsewhere.Replace(@"\", @"\\")); // VDF escapes backslashes

        library.GetInstallDir(30).Should().BeNull();
    }

    // ── 5. Steamless: the archive is screened with the same call EnsureToolAsync makes ──────────────

    private string ZipOf(params (string Name, byte[] Data)[] entries)
    {
        string path = Path.Combine(_root, $"{Guid.NewGuid():N}.zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            foreach (var (name, data) in entries)
            {
                using var s = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
                s.Write(data);
            }
        return path;
    }

    private static bool Blocked(string zip) =>
        FixAnalyzer.AnalyzeArchive(zip, Path.Combine(Path.GetTempPath(), "steamless-tool"), luaProfile: LuaScreeningProfile.ApplicationCode).Blocked;

    private static byte[] Pe(int length) { var b = new byte[length]; new Random(7).NextBytes(b); b[0] = (byte)'M'; b[1] = (byte)'Z'; return b; }

    [Fact]
    public void A_steamless_shaped_archive_with_executables_and_plugin_dlls_is_not_blocked() =>
        Blocked(ZipOf(
            ("Steamless.CLI.exe", Pe(40_000)), ("Steamless.API.dll", Pe(30_000)), ("Steamless.exe", Pe(300_000)),
            (@"Plugins/Steamless.Unpacker.Variant31.x64.dll", Pe(50_000)), ("Plugins/Steamless.Unpacker.Variant30.x64.dll", Pe(50_000))))
            .Should().BeFalse("the real release is ~600 KB of exe/dll; executables are a warning here, not a block");

    [Fact]
    public void An_entry_that_escapes_the_tool_folder_is_blocked() =>
        Blocked(ZipOf((@"..\..\Startup\evil.exe", Pe(1000)))).Should().BeTrue();

    [Fact]
    public void A_decompression_bomb_is_blocked() =>
        Blocked(ZipOf(("payload.bin", new byte[60 * 1024 * 1024]))).Should().BeTrue();

    [Fact]
    public void An_absurd_entry_count_is_blocked()
    {
        var entries = Enumerable.Range(0, 20_001).Select(i => ($"f{i}.txt", Array.Empty<byte>())).ToArray();

        Blocked(ZipOf(entries)).Should().BeTrue();
    }

    [Fact]
    public void Steamless_screens_the_archive_before_it_extracts_anything_and_logs_why_it_refused()
    {
        string src = RepoFile("src", "LuaToolsGui", "Services", "SteamlessService.cs");

        src.IndexOf("FixAnalyzer.AnalyzeArchive(zipPath, ToolDir", StringComparison.Ordinal)
            .Should().BePositive().And.BeLessThan(src.IndexOf("ExtractToDirectory(ToolDir", StringComparison.Ordinal));
        src.Should().Contain("Steamless: archive refused before extraction").And.Contain("Steamless: could not install the tool");
    }

    // ── 2. The DoH handler no longer keeps cookies by default ───────────────────────────────────────

    [Fact]
    public void The_default_http_handler_does_not_keep_cookies_and_still_follows_redirects()
    {
        var parameters = typeof(DnsFallbackHttpHandler).GetMethod("CreateDefault")!.GetParameters();

        parameters.Single(p => p.Name == "useCookies").DefaultValue.Should().Be(false);
        parameters.Single(p => p.Name == "allowAutoRedirect").DefaultValue.Should().Be(true);
    }

    // ── 1. Non-2xx responses are disposed ───────────────────────────────────────────────────────────

    private sealed class TrackedContent : HttpContent
    {
        public bool Disposed { get; private set; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;
        protected override bool TryComputeLength(out long length) { length = 0; return true; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    [Fact]
    public async Task A_failed_signed_download_disposes_its_response()
    {
        var tracked = new TrackedContent();
        var api = new LuaToolsApiClient(null!, null!, null!, null!, null!);
        typeof(LuaToolsApiClient).GetField("FixDownloadHttp", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!
            .SetValue(api, new HttpClient(new Stub(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = tracked })));

        var act = () => api.DownloadFromUrlAsync("https://files.example.com/fix.zip", "fix.zip", null, CancellationToken.None);

        await act.Should().ThrowAsync<ApiException>();
        tracked.Disposed.Should().BeTrue("an undisposed non-2xx response holds its connection until the finalizer runs");
    }

    // ── 11. Relaunch: the exe path reaches cmd as a variable VALUE, not as command text ─────────────

    [Fact]
    public void The_relaunch_command_does_not_contain_the_exe_path()
    {
        var psi = App.RelaunchStartInfo(@"C:\Program Files\100%tools\LuaTools.exe");

        psi.Arguments.Should().NotContain("100%tools").And.Contain($"%{App.RelaunchExeVariable}%");
        psi.Environment[App.RelaunchExeVariable].Should().Be(@"C:\Program Files\100%tools\LuaTools.exe");
        psi.UseShellExecute.Should().BeFalse();
    }

    [Theory]
    [InlineData(@"C:\100%PATH%tools\LuaTools.exe")]
    [InlineData(@"C:\a&b\LuaTools.exe")]
    [InlineData(@"C:\a^b(1)\LuaTools.exe")]
    [InlineData(@"C:\Program Files\LuaTools\LuaTools.exe")]
    public void A_path_with_percent_or_cmd_metacharacters_survives_the_variable_indirection(string exe)
    {
        // Same expansion the relaunch command performs, minus the launch: echo what cmd would hand to "start".
        var psi = new ProcessStartInfo("cmd.exe", $"/c echo \"%{App.RelaunchExeVariable}%\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        psi.Environment[App.RelaunchExeVariable] = exe;

        using var proc = Process.Start(psi)!;
        string output = proc.StandardOutput.ReadToEnd().Trim();
        proc.WaitForExit();

        output.Should().Be($"\"{exe}\"");
    }
}
